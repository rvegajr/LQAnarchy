#!/usr/bin/env python3
"""lqdownload: Laser Quest LQX radio frames and scoring, in plain Python.

A small, readable reference for "the download": how LQX asks a pack for its final score, what comes back over the
radio, and how LQX turns every pack's upload into the score card. Python 3.8+, standard library only.

    python3 lqdownload.py --self-test              # checks everything below against LQX's own results
    python3 lqdownload.py --decode 0239395330...   # decode any LQX frame given as hex

The scoring functions are a line-by-line port of LQX 4.10.1.2's ScoreEngine (CalculateScore, BoostScores,
OrderScores). The self-test compares them with numbers printed by LQX's own compiled code (scoring-check/results).
"""
import csv
import os
import sys

STX, EOT, ETX = 0x02, 0x04, 0x03
OP_ACK, OP_LIVE_REQUEST, OP_LIVE, OP_UPLOAD, OP_UPLOAD_REQUEST, OP_RESET, OP_STOP = 0x3D, 0x3F, 0x40, 0x53, 0x54, 0x5A, 0x34
PC = 99
SENSORS = ("rear", "front", "phaser", "shoulder")


# ---------------------------------------------------------------------------------------------------------- frames

def checksum(body: bytes) -> bytes:
    """LqxMessage checksum: sum every byte from STX through EOT, write it as 4 hex digits, then shift each digit so
    '0'..'9' stay '0'..'9' and 'A'..'F' become ':'..'?' (nibble + 0x30)."""
    total = sum(body)
    if total > 0xFFFF:
        raise ValueError("sum %d does not fit 4 digits; LQX would send a broken checksum" % total)
    return bytes(0x30 + ((total >> shift) & 0xF) for shift in (12, 8, 4, 0))


def build(header: int, opcode: int, payload: bytes = b"") -> bytes:
    """One LQX frame: STX, 2-digit address, opcode, payload, EOT, checksum(4), ETX."""
    body = bytes([STX]) + b"%02d" % header + bytes([opcode]) + payload + bytes([EOT])
    return body + checksum(body) + bytes([ETX])


def hsdu_wire(frame: bytes, preamble_bytes: int = 9) -> bytes:
    """What actually goes down the serial line to the HSDU: a run of 0xAA (radio warm-up; 9 bytes = the site's 20 ms
    preamble), two zero bytes, ESC, the frame, then two spaces and a zero so the carrier outlasts the ETX."""
    return b"\xAA" * preamble_bytes + b"\x00\x00\x1B" + frame + b"\x20\x20\x00"


def airtime_ms(n_bytes: int, baud: int = 4800) -> float:
    """8N1 = 10 bits per byte."""
    return n_bytes * 10 * 1000 / baud


def parse(frame: bytes):
    """Split a frame and verify its checksum. Returns (address, opcode, payload, checksum_ok)."""
    if len(frame) < 10 or frame[0] != STX or frame[-1] != ETX or frame[-6] != EOT:
        raise ValueError("not an LQX frame")
    body = frame[:-5]
    return int(frame[1:3]), frame[3], frame[4:-6], checksum(body) == frame[-5:-1]


def parse_upload(payload: bytes) -> dict:
    """0x53 final upload (222 bytes): status, colour, shots(4 digits), lives(3), pack(2), filler, name(10), then
    50 attackers x [rear, front, phaser, shoulder], each byte 0x80 + count (so 0-127)."""
    if len(payload) != 222:
        raise ValueError("0x53 payload is 222 bytes, got %d" % len(payload))
    hits = [[payload[22 + i * 4 + k] & 0x7F for k in range(4)] for i in range(50)]
    return {"status": chr(payload[0]), "colour": payload[1], "shots": int(payload[2:6]), "lives": int(payload[6:9]),
            "pack": int(payload[9:11]), "name": payload[12:22].decode("latin-1"), "taken": hits}


def parse_live(payload: bytes) -> dict:
    """0x40 live reply (212 bytes): status, name(10), 50 x [rear, front, phaser+shoulder], 50 bytes always 0x80,
    stun byte. LQX reads column 3 as 'HHU' and leaves shoulder at 0, so shoulder tags score as phaser tags."""
    if len(payload) != 212:
        raise ValueError("0x40 payload is 212 bytes, got %d" % len(payload))
    hits = [[payload[11 + i * 3] & 0x7F, payload[12 + i * 3] & 0x7F, payload[13 + i * 3] & 0x7F, 0] for i in range(50)]
    return {"status": chr(payload[0]), "name": payload[1:11].decode("latin-1"), "taken": hits, "stun": payload[211]}


def upload_payload(pack: int, colour: int, shots: int, lives: int, name: str, taken: dict) -> bytes:
    """Build a 0x53 payload; taken = {attacker: (rear, front, phaser, shoulder)}."""
    p = bytearray(b"0" + bytes([colour]) + b"%04d%03d%02d0" % (shots, lives, pack) + name.encode("latin-1").ljust(10)[:10])
    cells = bytearray([0x80] * 200)
    for attacker, counts in taken.items():
        for k, n in enumerate(counts):
            cells[attacker * 4 + k] = 0x80 + n
    return bytes(p + cells)


# --------------------------------------------------------------------------------------------------------- scoring

SITE_PROFILE = {  # game_points_profile row 1, "Default Points Profile"
    "yh": (10, 10, 10, 10),          # you hit them: rear, front, phaser, shoulder
    "hy": (4, 5, 3, 3),              # they hit you: rear, front, phaser, shoulder
    "bonus_x": 10, "bonus_max": 100,  # HitPerMissBonusPointsPercent (used as a multiplier), HitPerMissMaxBonusPoints
    "penalty_threshold": 9999, "penalty_points": 0, "boost": True, "marshal": 0,
}


def score(packs: dict, game_type: str = "Solo", final: bool = False, profile: dict = SITE_PROFILE) -> dict:
    """Port of ScoreEngine.CalculateScore. packs = {pack: {"team": colour, "shots": n, "taken": 50 x [r, f, p, s]}}.
    Only packs present here are scored or count as attackers, exactly as in LQX. Returns {pack: {...}}."""
    out = {}
    for me, mine in packs.items():
        tags = attack = defence = 0
        for other, theirs in packs.items():
            if other == me:
                continue
            made = theirs["taken"][me]                                   # my tags live in THEIR pack
            took = mine["taken"][other]
            tags += sum(made)
            a = sum(n * w for n, w in zip(made, profile["yh"]))
            d = sum(n * w for n, w in zip(took, profile["hy"]))
            if game_type == "AdvancedTeam" and theirs["team"] == mine["team"]:
                a, d = -a, 0
            attack += a
            defence += d
        ratio = 0.0 if mine["shots"] <= 0 else tags * 100.0 / mine["shots"]
        bonus = min(int(ratio * profile["bonus_x"]), profile["bonus_max"])
        penalty = max(0, mine["shots"] - profile["penalty_threshold"]) * profile["penalty_points"]
        out[me] = {"tags": tags, "attack": attack, "defence": defence, "accuracy": int(ratio), "bonus": bonus,
                   "penalty": penalty, "boost": 0, "score": attack - defence + bonus - penalty}
    if profile["boost"] and final:
        _boost(out, 20, 100)
    _rank(out)
    return out


def _boost(out, lo, hi):
    """BoostScores(20, 100): if the lowest score is under 20, stretch every score below 100 so the lowest lands on 20."""
    low = min(r["score"] for r in out.values())
    if low >= lo:
        return
    k = float(hi - lo) / float(hi - low)
    c = -k * low + lo
    for r in out.values():
        if r["score"] < hi:
            new = int(float(r["score"]) * k + c)
            r["boost"], r["score"] = new - r["score"], new


def _rank(out):
    """OrderScores: highest first; equal scores share a rank, the next score takes the next number (1, 1, 2)."""
    rank, last = 0, None
    for pack in sorted(out, key=lambda p: -out[p]["score"]):         # stable, like LINQ OrderByDescending
        if out[pack]["score"] != last:
            rank += 1
        last = out[pack]["score"]
        out[pack]["rank"] = rank


def table(taken: dict) -> list:
    """{attacker: (rear, front, phaser, shoulder)} -> 50 x 4 list."""
    t = [[0, 0, 0, 0] for _ in range(50)]
    for a, counts in taken.items():
        t[a] = list(counts)
    return t


# ------------------------------------------------------------------------------------------------------- self-test

def self_test():
    here = os.path.dirname(os.path.abspath(__file__))
    ok = True

    def check(name, got, want):
        nonlocal ok
        good = got == want
        ok &= good
        print(("PASS " if good else "FAIL ") + name + ("" if good else "   got %r, want %r" % (got, want)))

    # Frames LQX really sent or received (site radio logs, and LQ Radio's own capture on 2026-10-15).
    check("ACK from a pack", build(PC, OP_ACK).hex(" "), "02 39 39 3d 04 30 30 3b 35 03")
    check("reset pack 07", build(7, OP_RESET).hex(" "), "02 30 37 5a 04 30 30 3c 37 03")
    check("upload request to pack 01", build(1, OP_UPLOAD_REQUEST), b"\x0201T\x0400;;\x03")
    check("upload request on the wire (bytes)", len(hsdu_wire(build(1, OP_UPLOAD_REQUEST))), 25)

    # Pack 1's real final upload, 2026-10-15 (3-minute test game: 2 shots, no hits). Rebuilt from the decoded capture;
    # LQ Radio verified the checksum on receipt, so this frame is byte-identical to what came over the air.
    real = build(PC, OP_UPLOAD, b"020002000010" + b"Player: 01" + b"\x80" * 200)
    addr, op, pd, good = parse(real)
    up = parse_upload(pd)
    check("real upload: length", len(real), 232)
    check("real upload: decode", (addr, chr(op), good, up["shots"], up["lives"], up["pack"], up["name"], up["colour"]),
          (99, "S", True, 2, 0, 1, "Player: 01", 0x32))

    # Scoring port against LQX's own ScoreEngine (scoring-check/results/RESULTS-4.10.1.2.md).
    for shots, want in ((400, (2, 25, 125)), (200, (5, 50, 150)), (100, (10, 100, 200)), (50, (20, 100, 200))):
        r = score({1: {"team": 0x32, "shots": shots, "taken": table({})},
                   2: {"team": 0x32, "shots": 0, "taken": table({1: (0, 10, 0, 0)})}})
        check("accuracy, %d shots" % shots, (r[1]["accuracy"], r[1]["bonus"], r[1]["score"]), want)

    live = {1: {"team": 0x32, "shots": 333, "taken": table({})}, 2: {"team": 0x32, "shots": 333, "taken": table({})},
            3: {"team": 0x32, "shots": 333, "taken": table({1: (0, 12, 0, 0), 2: (0, 16, 0, 0)})}}
    r = score(live)
    check("gun (333 shots): scores and ranks", [(r[p]["score"], r[p]["rank"]) for p in (1, 2, 3)], [(156, 2), (208, 1), (-140, 3)])
    for p, s in ((1, 50), (2, 500), (3, 40)):
        live[p]["shots"] = s
    r = score(live, final=True)
    check("card (real shots, boost): scores and ranks", [(r[p]["score"], r[p]["rank"], r[p]["boost"]) for p in (1, 2, 3)],
          [(220, 1, 0), (192, 2, 0), (19, 3, 159)])

    teams = lambda gt: score({1: {"team": 0x30, "shots": 0, "taken": table({})},
                              2: {"team": 0x30, "shots": 0, "taken": table({1: (0, 5, 0, 0)})},
                              3: {"team": 0x31, "shots": 0, "taken": table({1: (0, 5, 0, 0)})}}, gt)
    check("Team vs AdvancedTeam", [[teams(gt)[p]["score"] for p in (1, 2, 3)] for gt in ("Team", "AdvancedTeam")], [[100, -25, -25], [0, 0, -25]])

    # A whole 8v8 game: parse every real-format 0x53 frame LQX scored, re-score in Python, compare with LQX's card.
    path = os.path.join(here, "..", "scoring-check", "results", "8v8-final-uploads.csv")
    if os.path.exists(path):
        rows = [r for r in csv.reader(l for l in open(path) if not l.startswith("#"))]
        packs, want = {}, {}
        for pack, team, card, rank, hexframe in rows:
            addr, op, pd, good = parse(bytes.fromhex(hexframe))
            u = parse_upload(pd)
            ok &= good and u["pack"] == int(pack)
            packs[u["pack"]] = {"team": u["colour"], "shots": u["shots"], "taken": u["taken"]}
            want[int(pack)] = (int(card), int(rank))
        r = score(packs, "Team", final=True)
        check("8v8 game: all 16 checksums and pack IDs", all(parse(bytes.fromhex(x[4]))[3] for x in rows), True)
        check("8v8 game: all 16 score-card lines match LQX", {p: (r[p]["score"], r[p]["rank"]) for p in r}, want)
    print("ALL PASS" if ok else "SOME CHECKS FAILED")
    return 0 if ok else 1


def decode(hexframe: str):
    addr, op, pd, good = parse(bytes.fromhex(hexframe))
    print("address %02d  opcode 0x%02X '%s'  payload %d bytes  checksum %s" % (addr, op, chr(op), len(pd), "ok" if good else "BAD"))
    if op == OP_UPLOAD:
        u = parse_upload(pd)
        print("pack %(pack)02d  %(name)s  colour 0x%(colour)02X  shots %(shots)d  lives %(lives)d" % u)
        for a, row in enumerate(u["taken"]):
            if any(row):
                print("  tagged by %02d: " % a + "  ".join("%s %d" % (s, n) for s, n in zip(SENSORS, row) if n))
    elif op == OP_LIVE:
        u = parse_live(pd)
        print("%(name)s (live)" % u)
        for a, row in enumerate(u["taken"]):
            if any(row):
                print("  tagged by %02d: rear %d  front %d  phaser+shoulder %d" % (a, row[0], row[1], row[2]))


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--self-test":
        sys.exit(self_test())
    if len(sys.argv) > 2 and sys.argv[1] == "--decode":
        decode(sys.argv[2])
        sys.exit(0)
    print(__doc__)
