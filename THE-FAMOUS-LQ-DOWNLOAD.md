# The famous LQ download

A wire-level deep dive into the minute or more between the end-of-game buzzer and your score card. It covers what
crosses the radio, why it takes so long, why the last few scores straggle in, and why the scoreboard reshuffles when
the cards print.

Every claim here comes from one of four places, and says which:
- **Code:** LQX 4.10.1.2's compiled code (`Console.exe`, `Versent.LQX.Core.dll`, built 2019-04-11; the 2017 build
  4.9.265.1 is identical in everything below). Excerpts are ILSpy 11.1 output from the vendor binaries; names like
  `num2` are the decompiler's.
- **Logs:** a site's game-PC radio logs from its last week of games (10-15 March 2020: 61 games, 44,423 transmitted
  frames, 933 final uploads).
- **Database:** that site's LQX database (8,117 games, 2018-2020).
- **Simulation:** 8v8 games run through LQX's own scoring engine with the measured timings
  ([`scoring-check/`](scoring-check/)).

Hashes and build details are in the [README](README.md). Anything that is an inference is marked **(inference)**.

**Contents**

1. [The 30-second version](#1-the-30-second-version)
2. [The cast: PC, HSDU, packs](#2-the-cast-pc-hsdu-packs)
3. [Anatomy of a frame](#3-anatomy-of-a-frame)
4. [On the air](#4-on-the-air)
5. [The buzzer: how a game ends](#5-the-buzzer-how-a-game-ends)
6. [The collection loop](#6-the-collection-loop)
7. [Why it takes so long](#7-why-it-takes-so-long)
8. [The upload, byte by byte](#8-the-upload-byte-by-byte)
9. [From uploads to the score card](#9-from-uploads-to-the-score-card)
10. [Score changes across a game: the gun vs the truth](#10-score-changes-across-a-game-the-gun-vs-the-truth)
11. [The reshuffle](#11-the-reshuffle)
12. [When a download fails](#12-when-a-download-fails)
13. [Oddities](#13-oddities)
14. [How the download could be faster](#14-how-the-download-could-be-faster)
15. [Reproduce everything](#15-reproduce-everything)

---

## 1. The 30-second version

- When time is up, the PC tells every pack to stop. It then asks the packs **one at a time** for their final score.
  Each pack needs about **2.6 seconds** to answer. LQX waits **3.0 seconds** before giving up on it, a margin of
  0.3 seconds, so about **1 request in 4 fails**.
- A pack that misses its turn waits for **a whole lap** of the other packs before it is asked again. Each time a
  failure count sets a new record, the PC also re-sends STOP to every pack, which costs about 4 seconds. That's why the
  first dozen scores arrive quickly and **the last few straggle in**.
- An 8v8 download takes **about 75 seconds** in a typical game (simulated median), and 90 seconds or more in one game
  in ten. The real site averaged **57-62 seconds** with around 15 players, and the operator sometimes ended the wait by
  hand.
- During the game your gun shows a rank and score that the PC calculates and radios back. The download replaces two
  approximations with the real thing:
  - **Accuracy:** the gun assumes everyone fired **333 shots**; the card uses your real count.
  - **Late tags:** the gun only has tags as of each pack's last poll; the card has everything.

  So the order changes. In 1,000 simulated 8v8 games, the gun's #1 lost on the card in **9.4 %** of games, and the
  team leading on the arena screen lost on the card in **4.5 %**.
- If a pack's download never arrives, LQX scores that player from mid-game data: 333 shots, and none of their late
  tags taken. In the site's database **1.84 %** of all pack-games carry that fingerprint.

## 2. The cast: PC, HSDU, packs

```
 LQX PC ──RS-232, 4800 8N1──► HSDU ~~~~ one radio channel, half duplex ~~~~► packs 01-40
         RTS = transmit                 (everyone hears everything)          (data link board + antenna)
         DTR = which HSDU (airlock/arena, at two-HSDU sites)
```

- **The PC** (address `99`) runs everything: polling, scoring and the score card. Packs never talk unless asked.
- **The HSDU** ("high-speed data unit", Threshold Electronics model LQX 302) is a **transparent radio modem**. Raise
  RTS and it transmits whatever bytes arrive on the serial line; whatever it hears comes back on the serial line.
  There's no addressing, framing or retrying inside it. An opened unit shows a Philips **TDA7021T** FM receiver chip,
  which only works below 110 MHz, so this is a low-VHF radio. The exact frequency hasn't been measured.
- **Packs** answer only frames addressed to their own number (01-40), or broadcasts (`00`). Their radio is the "data
  link" board in the rear pod, with a wire antenna over the shoulder.
- **One channel:** a pack stuck transmitting jams everyone (the 2010 support binder says so). So LQX polls strictly
  one pack at a time. That single fact explains most of this document.

At sites with two HSDUs, **downloads go through the airlock HSDU and STOP goes out through the arena HSDU**
(`SerialPortManager.DownloadScoreFromPack` calls `UseAirlockHsdu()`, and `StopGame` calls `UseArenaHsdu()`). The site
these logs come from had one HSDU, so no switching happened there.

## 3. Anatomy of a frame

Every message, in both directions, has the same shape (`LqxMessage.ToMessageString`):

| Offset | Bytes | Content |
|---|---|---|
| 0 | 1 | STX `0x02` |
| 1 | 2 | address as two ASCII digits: `01`-`40` a pack, `00` everyone, `99` the PC |
| 3 | 1 | opcode, a printable character (`T` = 0x54 upload request, `S` = 0x53 upload, `4` = 0x34 stop...) |
| 4 | n | payload, fixed length per opcode |
| 4+n | 1 | EOT `0x04` |
| 5+n | 4 | checksum |
| 9+n | 1 | ETX `0x03` |

```csharp
public string ToMessageString()
{
    string text = "";
    string text2 = ToMessageStringComponent();
    text += "\u0002";
    ...
    text = text3 + nPackId.ToString("d").PadLeft(2, '0');
    text += (char)_nOpcode;
    text += text2;
    text += "\u0004";
    text += CalcChecksum(text);
    return text + "\u0003";
}
```

**The checksum** adds every byte from STX through EOT, prints the sum as four hex digits, and shifts any `A`-`F` down
by 7. That turns them into `:;<=>?`, so every checksum byte is `0x30` + one nibble (`LqxMessage.CalcChecksum`):

```csharp
int num = 0;
for (int i = 0; i < strMessageWithoutChecksum.Length; i++)
{
    byte b = (byte)strMessageWithoutChecksum[i];
    num += b;
}
string text = $"{num:X4}";
...
num2 = text[0];
if (num2 > 57)        // above '9'
{
    num2 -= 7;        // 'A' (65) becomes ':' (58)
}
```

**Worked example:** the upload request to pack 01. That's address `01`, opcode `T` (0x54), no payload:

```
bytes   02  30  31  54  04            STX '0' '1' 'T' EOT
sum     0x02+0x30+0x31+0x54+0x04 = 0xBB  ->  "00BB"  ->  '0' '0' ';' ';'   (B = 11 -> 0x30+11 = 0x3B ';')
frame   02 30 31 54 04 30 30 3B 3B 03
```

The same in Python, from [`tools/lqdownload.py`](tools/lqdownload.py):

```python
def checksum(body: bytes) -> bytes:
    total = sum(body)
    return bytes(0x30 + ((total >> shift) & 0xF) for shift in (12, 8, 4, 0))

def build(header: int, opcode: int, payload: bytes = b"") -> bytes:
    body = bytes([STX]) + b"%02d" % header + bytes([opcode]) + payload + bytes([EOT])
    return body + checksum(body) + bytes([ETX])

>>> build(1, 0x54).hex(" ")
'02 30 31 54 04 30 30 3b 3b 03'
>>> build(99, 0x3D).hex(" ")          # a pack's ACK; every one of the 1,038 in the logs is exactly this
'02 39 39 3d 04 30 30 3b 35 03'
```

**No escaping is needed.** No payload byte is ever `0x02`, `0x03` or `0x04`: numbers are ASCII digits, names are text,
and counters are sent as `0x80 + n`. The logs confirm it: no payload in 44,423 frames contains them.

## 4. On the air

The PC never sends a bare frame. `HSDUSerialController` wraps it in a radio warm-up and keys the transmitter around it:

```csharp
private void SendWithWaitControl(string output)
{
    string text = AddHsduPreAmble(output);
    Transmit = true;                                                   // RTS up: key the HSDU
    Thread.Sleep(80);
    base.Send(text);
    Thread.Sleep(Convert.ToInt32((float)text.Length * (MsPerByte - 1f)));   // +1 ms per byte
    Transmit = false;                                                  // RTS down
}

private string AddHsduPreAmble(string output)
{
    ... "".PadRight(_preambleChars, 'ª'), '\0', '\0', '\u001b', output, ' ', ' ', '\0' ...
}
```

The wrapper and what each part is for:

| Part | Purpose |
|---|---|
| `0xAA` × N | `10101010`, a square wave the receiver can lock onto **(inference)**. N = preamble ms × 4800 / 10000: the site's 20 ms setting gives **9 bytes** |
| `00 00` | a gap |
| `1B` | ESC, the marker just before STX |
| the frame | as in section 3 |
| `20 20 00` | padding, so the carrier outlasts the ETX |

So the 10-byte upload request becomes 25 bytes on the wire:

```
AA AA AA AA AA AA AA AA AA 00 00 1B 02 30 31 54 04 30 30 3B 3B 03 20 20 00
```

**Timing.** At 4800 baud with 8N1, each byte is 10 bits, or **2.083 ms**. The logs show a send takes
80 ms + about 3.08 ms per wire byte, so the serial write blocks for the airtime and RTS drops *after* the last byte.

| Message | Frame | On the wire | Airtime | Send, including key-up |
|---|---|---|---|---|
| 0x54 upload request | 10 | 25 | 52 ms | ≈ 160 ms |
| 0x34 stop (broadcast) | 10 | 25 | 52 ms | ≈ 160 ms, sent 3 times with 500 ms pauses |
| 0x53 final upload (from a pack) | 232 | ≈ 265 | ≈ 0.55 s | |

**The PC hears itself.** The HSDU passes its own transmission back: 89.6 % of the PC's request frames came back as
byte-identical echoes 56-295 ms later. LQX ignores them because they carry the wrong opcode.

**Packs use the same wrapper.** Here is a real reply captured on 2026-10-15 from one of the owner's packs: pack 01
answering a find, read through the HSDU. There's line noise, a run of `AA`, then `00 1B` and the frame. This pack sent
its ACK twice back to back:

```
5B  80 29 A5 A9  AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA
00  00 1B 02 39 39 3D 04 30 30 3B 35 03  1B 02 39 39 3D 04 30 30 3B 35 03
```

The `AA` run arrives intact through the HSDU's receiver, which is why the HSDU looks like a simple pass-through radio.

## 5. The buzzer: how a game ends

In the `Running` state, LQX checks the clock on every pass (`GameEngine`):

```csharp
if (DateTime.Now >= _gamePlayViewModel.ScheduledEndTime || _gameEngineSignals.IsStopGameSignaled())
{
    _scoreMonitorProxy.StopGame();
    _serialPortManager.StopGame();            // STOP x3 on the HSDU, then x3 on the arena RS-485 bus
    DeviceManager.StopAllPacks();             // every in-game pack is now "Stopped"
    _gamePlayViewModel.StopGame();
    await Task.Delay(2000, cancellationToken);
    SetGameEngineState(GameEngineStates.CollectingFinalScores, _gamePlayViewModel);
    break;
}
```

`StopGame` is a broadcast. A broadcast is never acknowledged, so LQX sends it three times, half a second apart
(`SerialPortManager.PackBroadCast`):

```csharp
for (int i = 0; i < 3; i++)
{
    _packCommsSerialController.Send(message);
    Thread.Sleep(500);
}
```

The STOP frame is `02 30 30 34 04 30 30 39 3A 03`. In the logs, the first upload request goes out **6.2 seconds** after
time-up (6.09-6.30 s): three STOPs on the HSDU, three on the arena bus, then the 2-second wait. Six seconds of the
download are gone before any pack is asked for anything.

## 6. The collection loop

Each pass of the game engine asks **one** pack. The loop walks a rotation of packs, skips the ones already
`Finished`, and asks the first one still `Stopped`:

```csharp
case GameEngineStates.CollectingFinalScores:
{
    bool flag = true;
    int num = 0;
    while (FinalScorePacks.MoveNext())
    {
        DeviceViewModel current2 = FinalScorePacks.Current;
        if (current2 == null || num++ > 50)
        {
            break;
        }
        if (current2.DeviceState == DeviceStates.Stopped)
        {
            flag = CollectFinalScoreFromPacks(current2);   // always returns false
            break;
        }
    }
    bool flag2 = CollectFinalScoreFromHeadquarters(InGameHeadquarters);
    if ((flag & flag2) || _gameEngineSignals.IsStopGameSignaled())
    {
        _scoreEngine.CalculateScore(finalScore: true);
        _scoreRepository.Save(_scoreEngine.PackScores, _gamePlayViewModel.GamePlayId);
        ...
        SetGameEngineState(GameEngineStates.PrintingReports, _gamePlayViewModel);
    }
    break;
}
```

`flag` only stays `true` when a pass finds **no** pack still `Stopped`. So collection ends in one of two ways:
- every pack is `Finished`, either with a score or after 25 failures;
- or the operator presses Stop.

One request, `SerialPortManager.DownloadScoreFromPack`:

```csharp
public PackUploadData DownloadScoreFromPack(DeviceIds deviceId)
{
    UseAirlockHsdu();
    ...
    Msg54_UploadRequest msg54_UploadRequest = new Msg54_UploadRequest(deviceId)
    {
        ResponseTimeoutMs = DatabaseHelper.VariableFetchInt("download timeout", 3000)
    };
    _packCommsSerialController.ClearReceiveBuffer();
    _packCommsSerialController.Send(msg54_UploadRequest.ToMessageString().AddHsduPreamble());
    return (!(WaitForMessage(_packCommsSerialController, Opcodes._53_UPLOAD, DeviceIds._99_PC, msg54_UploadRequest.ResponseTimeoutMs)
              is Msg53_PackUploadData msg53_PackUploadData)) ? null : msg53_PackUploadData.PackUploadData;
}
```

Clear the receive buffer, send `0x54`, then wait up to `download timeout` (3,000 ms; the site never changed it) for
any `0x53`. A late answer is thrown away by the next `ClearReceiveBuffer`. What happens next
(`GameEngine.CollectFinalScoreFromPacks`):

```csharp
if (deviceViewModel.Failures > _lastDeviceFailureCount)       // a new record number of failures:
{
    _lastDeviceFailureCount = deviceViewModel.Failures;
    _serialPortManager.StopGame();                            // re-send STOP x3 + x3 (about 4 s)
}
PackUploadData packUploadData = _serialPortManager.DownloadScoreFromPack(deviceViewModel.DeviceId);
...
if (packUploadData == null || packUploadData.PayloadPackId != deviceViewModel.DeviceId)
{
    DeviceManager.Fail(deviceViewModel);
    if (deviceViewModel.Failures < 25)
    {
        ReportGameEngineActivityProgress(MessageSeverity.Warning, $"Final Score not received from {...} after {...} Trials.");
        return false;
    }
    ReportGameEngineActivityProgress(MessageSeverity.Warning, $"Couldn't get final score from pack {...}.");
    DeviceManager.Finish(deviceViewModel);                    // give up: keep its mid-game data
    return false;
}
...
DeviceManager.Finish(deviceViewModel);
_scoreEngine.ProcessScoreReply(deviceViewModel.DeviceId, ..., packUploadData);
```

Three details matter:
1. **An answer only counts if the pack ID inside the payload matches** the pack that was asked. An answer from the
   wrong pack counts as a failure.
2. **A failed pack goes to the back of the line.** Its retry comes only after every other pending pack has had a turn.
3. **The STOP re-send** fires whenever any pack's failure count beats the previous worst. It's meant to stop packs that
   missed the first STOP **(inference)**, but it costs about 4 seconds of airtime each time, during which nobody is
   downloading.

## 7. Why it takes so long

### The budget for one pack

| Step | Time | Source |
|---|---|---|
| PC keys up and sends `0x54` (25 bytes) | ≈ 0.16 s | Code + logs |
| Pack builds its upload on the first request | ≈ 1.9 s | Logs: total minus airtime **(inference)** |
| Pack sends `0x53` (≈ 265 bytes on air) | ≈ 0.55 s | Arithmetic |
| **First answer arrives (logs: 755 first requests)** | **min 1.56 s, median 2.64 s, max 2.68 s** | Logs |
| LQX's patience | **3.00 s** | Code (`download timeout`) |
| Next request after a success | 2.80 s after the last | Logs |
| Next request after a failure | 3.14 s after the last | Logs |

Most first answers land in a narrow window, 749 of 755 between 2.50 and 2.68 s. LQX gives up at 3.00 s. **The margin
is about 0.3 seconds.** A pack that is a little slow, a little far away or a little garbled misses.

**Repeat requests are faster.** A pack asked a second time answers in **1.55-1.70 s** (178 repeats, none slow). The
pack seems to build its upload once and keep it **(inference)**. LQX doesn't take advantage of that: the retry waits
for a whole lap.

### What the logs say about the real thing

- Requests answered in time: **75.8 %** of attempts. Packs that answered on their first request: **78.6 %**.
- Attempts needed, per pack per game: 1 try 753, 2 tries 138, 3 tries 27, 4 tries 9, 5 tries 1. The worst was 12 tries.
- STOP re-sends during collection: 117 extra STOP triples across 61 games, about 2 per game.
- Collection time per game: median **62 s**, range 0-126 s. In the database (6,729 Solo games), the average from end
  of game to scores collected was **57 s**, and the longest 285 s.
- The 25-failure give-up **never fired** that week. When a pack never answered (30 of 958 pack-games), the operator
  ended the collection.

### Best case, and a real one

Best case for 16 packs: 6.2 s of STOPs + 16 × 2.80 s = **51 seconds**, if every pack answers first time. That never
happens: with 75.8 % success, 16 packs need about 21 requests on average. Here is a simulated 8v8 download (section 8
of [the results](scoring-check/results/RESULTS-4.10.1.2.md)), request by request:

| Time | Sent | Result |
|---|---|---|
| 0:06 | 0x54 to pack 1 | no answer in 3 s (try 1) |
| 0:09 | 0x54 to pack 2 | answered (1 of 16) |
| 0:12 | 0x54 to pack 3 | answered (2 of 16) |
| 0:14 | 0x54 to pack 4 | no answer in 3 s (try 1) |
| 0:18-0:37 | packs 5-12 | answered (3-10 of 16) |
| 0:40 | 0x54 to pack 13 | no answer in 3 s (try 1) |
| 0:43 | 0x54 to pack 14 | answered (11 of 16) |
| 0:46 | 0x54 to pack 15 | no answer in 3 s (try 1) |
| 0:49 | 0x54 to pack 16 | answered (12 of 16) |
| 0:52 | **STOP x3 again** (pack 1 has now failed once) | |
| 0:56 | 0x54 to pack 1 | answered (13 of 16) |
| 0:59 | 0x54 to pack 4 | no answer (try 2) |
| 1:02 | 0x54 to pack 13 | no answer (try 2) |
| 1:05 | 0x54 to pack 15 | answered (14 of 16) |
| 1:08 | **STOP x3 again** (pack 4: 2 failures) | |
| 1:12 | 0x54 to pack 4 | answered (15 of 16) |
| 1:15 | 0x54 to pack 13 | no answer (try 3) |
| 1:18 | **STOP x3 again** (pack 13: 3 failures) | |
| 1:22 | 0x54 to pack 13 | answered (16 of 16), **86 s after the buzzer** |

The first 12 scores took 49 seconds. **The last 4 took 37.** Once only the stragglers remain, each lap is a 3-second
timeout, often plus a 4-second STOP, for one pack at a time. That's the wait for the last scores.

Over 1,000 simulated 8v8 games:

| | Median | 1 game in 10 | Worst |
|---|---|---|---|
| Buzzer to last score | 75 s | 91 s or more | 120 s |
| Time spent on the last 3 packs | 16 s | 27 s or more | 52 s |
| STOP re-sends | 2 | 3 | 7 |

The simulation is a little slower than the site's 62-second median. It always has 16 packs (the site averaged about
15 players per game), treats every request as an independent 75.8 % chance, and never lets the operator cut the wait
short.

## 8. The upload, byte by byte

The `0x53` payload is 222 bytes, so the frame is 232 (`Msg53_PackUploadData`):

| Payload offset | Bytes | Field | Notes |
|---|---|---|---|
| 0 | 1 | status | always `'0'` in the logs |
| 1 | 1 | colour | `0x30` red, `0x31` green, `0x32` mixed (solo), `0x33` orange |
| 2 | 4 | shots fired | ASCII digits. Logs: 0-2173, median 483 |
| 6 | 3 | lives used (tags taken) | ASCII digits. Logs: 0-219, median 37 |
| 9 | 2 | pack ID | **must match the pack that was asked** |
| 11 | 1 | filler | `'0'` |
| 12 | 10 | player name | `Player: 07` in unnamed games |
| 22 | 200 | 50 attackers × [rear, front, phaser, shoulder] | each byte `0x80 + count`; entry *i* = tags received from device *i* |

```csharp
public Msg53_PackUploadData(byte[] payload)
    : base(Opcodes._53_UPLOAD, payload)
{
    int index1 = 4;                                   // the byte array starts at STX, so PD starts at 4
    this.PackUploadData = new PackUploadData();
    this.PackUploadData.Status = (int) payload[index1];
    ...
    this.PackUploadData.ShotsUsed = StringHelpers.ByteArray2Int(payload, offset1, 4);
    ...
    this.PackUploadData.PayloadPackId = (DeviceIds) StringHelpers.ByteArray2Int(payload, offset3, 2);
    ...
    for (int index4 = 0; index4 < 50; ++index4)
    {
        this.PackUploadData.HitDataPackRear[index4]  = (int) sbyte.MaxValue & (int) payload[num + index4 * 4];
        this.PackUploadData.HitDataPackFront[index4] = (int) sbyte.MaxValue & (int) payload[num + index4 * 4 + 1];
        this.PackUploadData.HitDataHhu[index4]       = (int) sbyte.MaxValue & (int) payload[num + index4 * 4 + 2];
        this.PackUploadData.HitDataShoulder[index4]  = (int) sbyte.MaxValue & (int) payload[num + index4 * 4 + 3];
    }
}
```

**A real one.** Pack 01's final upload after a 3-minute test game on 2026-10-15 (the owner's pack and LQ Radio; 2
shots, no hits):

```
02 39 39 53                      STX "99" 'S'           from a pack, to the PC, opcode 0x53
30                               status '0'
32                               colour 0x32 = mixed (solo)
30 30 30 32                      shots "0002"
30 30 30                         lives "000"
30 31                            pack "01"              matches the pack that was asked
30                               filler
50 6C 61 79 65 72 3A 20 30 31    "Player: 01"
80 80 80 80 ... (200 bytes)      50 attackers x 4 sensors, all 0x80 + 0
04 36 3A 33 38 03                EOT, checksum "6:38" (byte sum 0x6A38), ETX
```

**A busier one.** Pack 11's upload from the simulated 8v8 game, in the exact on-air format, decoded by the Python tool:

```
$ python3 tools/lqdownload.py --decode 02393953303130323038303532313130506C61...
address 99  opcode 0x53 'S'  payload 222 bytes  checksum ok
pack 11  Player: 11  colour 0x31  shots 208  lives 52
  tagged by 01: front 2
  tagged by 02: rear 1  front 6  phaser 5  shoulder 1
  tagged by 03: front 1  phaser 1
  tagged by 04: rear 2  front 5  phaser 6  shoulder 1
  ...
```

Things the format implies:
- **Each count is 7 bits.** The most one pack can record from one attacker on one sensor is **127**.
- **A pack records who tagged it, never whom it tagged.** Your tags exist only in other players' uploads. That's why
  only the PC can score anyone (section 9).
- **The pack ID check matters in practice.** Five uploads in the logs came back with pack ID `00`, shots `0000` and no
  hits, from packs that seem to have lost their game data **(inference: a reboot)**. LQX rejects them as a mismatch.

## 9. From uploads to the score card

Each accepted upload **replaces** that pack's stored hits; nothing is added (`PackScore.UpdateHits`):

```csharp
for (int i = 0; i < 50; i++)
{
    _hitYouScore[i].Gun      = (byte)(packUploadData.HitDataHhu[i] & 0x7F);
    _hitYouScore[i].Rear     = (byte)(packUploadData.HitDataPackRear[i] & 0x7F);
    _hitYouScore[i].Front    = (byte)(packUploadData.HitDataPackFront[i] & 0x7F);
    _hitYouScore[i].Shoulder = (byte)(packUploadData.HitDataShoulder[i] & 0x7F);
    ...
}
LivesUsed = packUploadData.LivesUsed;
ShotsUsed = packUploadData.ShotsUsed;
```

Then `ProcessScoreReply` re-scores **everyone** (`CalculateScore(false)`). When collection ends, LQX runs
`CalculateScore(true)`, which adds the end-of-game boost, then ranks the players. The core of the scoring:

```csharp
foreach (PackScore item2 in list)                       // every OTHER pack LQX has heard from
{
    ...
    Hits hits = item2.HitBy(thisPackId);                // my tags, from THEIR upload
    num += hits.Front + hits.Rear + hits.Gun + hits.Shoulder;
    int num5 = hits.Front * YhFrontPoints + hits.Rear * YhRearPoints + hits.Gun * YhLaserPoints + hits.Shoulder * YhShoulderPoints;
    Hits hits2 = item.HitBy(deviceId);                  // their tags on me, from MY upload
    int defensePoints = hits2.Front * HyFrontPoints + hits2.Rear * HyRearPoints + hits2.Gun * HyLaserPoints + hits2.Shoulder * HyShoulderPoints;
    if (_gameType == GameType.AdvancedTeam && team2 == team) { hits.AttackPoints = -num5; hits2.DefensePoints = 0; }
    ...
}
double num9 = ((item.ShotsUsed <= 0) ? 0.0 : ((double)num * 100.0 / (double)item.ShotsUsed));     // accuracy %
int num10 = (int)(num9 * (double)_bizGamePointsProfile.HitPerMissBonusPointsPercent);              // x 10
if (num10 > _bizGamePointsProfile.HitPerMissMaxBonusPoints) { num10 = _bizGamePointsProfile.HitPerMissMaxBonusPoints; }   // cap 100
...
item.Score = num2 - num3 + num8 + num10 - num11;        // attack - defence + bases + accuracy - shot penalty
```

With the site's points profile (database row 1, the only one any game used):

| | Rear | Front | Phaser | Shoulder |
|---|---|---|---|---|
| You tag someone there | +10 | +10 | +10 | +10 |
| You are tagged there | −4 | −5 | −3 | −3 |

Accuracy: 10 points per percentage point, capped at 100. Shot penalty: never (threshold 9999). Boost: on.

The same in Python, a port checked against LQX on every case in this document:

```python
for me, mine in packs.items():
    tags = attack = defence = 0
    for other, theirs in packs.items():
        if other == me:
            continue
        made = theirs["taken"][me]                     # my tags live in THEIR pack
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
    score = attack - defence + bonus - penalty
```

**Worked example.** Pack 11 in the 8v8 game (`score()` on the CSV prints each part):

| Part | Working | Points |
|---|---|---|
| Attack | 25 tags found in the 8 red packs' uploads × 10 | **+250** |
| Defence | its own upload: 5 rear × 4 + 20 front × 5 + 20 phaser × 3 + 7 shoulder × 3 | **−201** |
| Accuracy | 25 × 100 / 208 = 12.0 % → × 10 = 120 → capped | **+100** |
| Boost | its score is already above 100 | 0 |
| **Score card** | | **149** |

**The end-of-game boost** (`BoostScores(20, 100)`, final only): if the lowest score is under 20, every score below 100
is stretched linearly so the lowest becomes exactly 20 (in practice 19, after float truncation) and 100 stays 100.
Scores of 100 or more never move.

```csharp
int num1 = this._packScores.Min(score => score.Value.Score);
if (num1 >= minBoostedScore) return;
double num2 = Convert.ToDouble(maxBoostedScore - minBoostedScore) / Convert.ToDouble(maxBoostedScore - num1);
double num3 = -num2 * (double) num1 + (double) minBoostedScore;
... if (score < maxBoostedScore) score = (int)(score * num2 + num3);
```

**Ranks** are dense: equal scores share a rank and the next score takes the next number (1, 1, 2).

**Team totals** are plain sums of the players' final scores, boost included. Both the score monitor
(`ScoreMonitorProxy.SendToScoreMonitor`: group by colour, `Sum(score => score.Score)`) and the score-card query
(`select @redScore = sum(score) ... team_id = 48`) do this. So the boost lifts a team's total by lifting its weakest
players.

## 10. Score changes across a game: the gun vs the truth

### How the gun gets its number

During the game the PC alternates between two jobs (`GameEngine`, state `Running`):

```csharp
if (DateTime.Now > _lastScoreBroadcast.AddSeconds(30.0))
{
    _serialPortManager.RealtimeScoreBroadcast(_scoreEngine.GetRealTimeScoreBroadcastData());   // everyone's rank + score
    _lastScoreBroadcast = DateTime.Now;
    break;
}
...
if (LiveScorePacks.MoveNext())
{
    ... CollectLiveScore(current);                     // poll the next pack in the rotation
}
```

```csharp
private void CollectLiveScore(DeviceViewModel deviceViewModel)
{
    KeyValuePair<int, int> scoreandRank = _scoreEngine.GetScoreandRank(deviceViewModel.DeviceId);   // read FIRST
    PackUploadData packUploadData = _serialPortManager.RealtimeScoreToPack(deviceViewModel.DeviceId, scoreandRank.Key, scoreandRank.Value);
    ...
    packUploadData.ShotsUsed = 333;
    _scoreEngine.ProcessScoreReply(..., packUploadData);
}
```

The live request (`0x3F`) carries that pack's rank and score as `dd±dddd`, for example `03+0156`. The pack shows them
on the HHU and replies with a `0x40`: its running tag table, in three columns (rear, front, phaser+shoulder) and with
**no shot count**. Hence the 333. In the logs the PC polled about every 1.5 s, reached each pack about every 34 s, and
broadcast the full leaderboard about every 35 s. Only **55.6 %** of live polls got a clean answer: 20.7 % came back
garbled and 23.6 % got nothing.

### Five reasons the gun's number isn't the truth

1. **It's old.** The rank and score are read before that pack's new reply is processed. Then they wait until that
   pack's next turn or the next broadcast, up to about 35 seconds.
2. **It's partial.** Your tags only count once *your victims'* packs have reported them.
3. **It assumes 333 shots.** The live accuracy bonus is `int(tags × 1000 / 333)`, about **3 points per tag**, capped
   at 100 once you have 34 tags. The card uses your real accuracy × 10.
4. **Nothing counts until both packs have been heard from.** `CalculateScore` only loops over packs it has a record
   for. A tag between two packs counts only after **both** have answered at least once. At the start of the simulated
   game below, every gun shows 0 at 0:31, because the rotation had only reached the red packs.
5. **No boost.** The boost is applied only at the end.

Shoulder tags are *not* a difference with the site's profile. The live reply folds them into the phaser column (the
logs show this from 10,971 entries compared against final uploads), and phaser and shoulder are worth the same: +10
to the shooter, −3 to the target.

### One 8v8 game, broadcast by broadcast

At every leaderboard broadcast, this table compares the gun leader with the real leader. The real leader is who the
score card would put first if every hit were known at that moment (shots pro rata, before the boost). It also compares
LQX's running team totals, as shown on the arena score monitor, with the real totals. From
[section 8 of the results](scoring-check/results/RESULTS-4.10.1.2.md); every number comes from LQX's `ScoreEngine`.

| Time | Gun leader (pack, score shown) | Real leader | Score monitor, red vs green | Real, red vs green |
|---|---|---|---|---|
| 0:31 | 1 (0) | 12 (150) | 0 vs 0 | 573 vs 661 |
| 1:01 | 12 (62) | 12 (167) | 88 vs 187 | 597 vs 835 |
| 1:32 | 7 (83) | 4 (180) | 264 vs 200 | 934 vs 767 |
| 2:33 | 2 (161) | 4 (232) | 545 vs 296 | 1073 vs 909 |
| 4:08 | 2 (167) | 4 (258) | 703 vs 593 | **1120 vs 1130** |
| 4:39 | 2 (232) | 10 (277) | 937 vs 633 | 1207 vs 1148 |
| 5:10 | 10 (243) | 2 (339) | 913 vs 760 | 1334 vs 1139 |
| 7:45 | 2 (497) | 2 (527) | 1623 vs 1011 | 1767 vs 1334 |
| 10:19 | 2 (700) | 2 (745) | 2215 vs 1302 | 2458 vs 1450 |
| 12:57 | 2 (904) | 2 (910) | **3029** vs 1515 | **2924** vs 1687 |
| 14:28 | 2 (1014) | 2 (976) | 3333 vs 1677 | 3194 vs 1782 |
| **Score card** | **2 (1016)** | | | **3279 vs 1932** |

Some moments to notice:
- **4:08:** green is really ahead (1130 to 1120), but the screen shows red up by 110.
- **5:10:** pack 10 leads on the guns while pack 2 really leads by nearly 100 points.
- **12:57:** the screen overstates red by more than 100. Between the last broadcast (14:28) and the card, green gains
  another 255 points from late tags, real accuracy and the boost.

Two players from that game:

| Time | Pack 6 gun | Pack 6 real | Pack 11 gun | Pack 11 real |
|---|---|---|---|---|
| 2:03 | 14 | 69 | 29 | 142 |
| 5:10 | 65 | 80 | 106 | 170 |
| 8:16 | 147 | 127 | 143 | 196 |
| 11:22 | 234 | 231 | 137 | 172 |
| 14:28 | 353 | 301 | 124 | 144 |
| **Card** | **322** | | **149** | |

- **Pack 6** fires 836 shots at 4.9 % accuracy. Its gun gives it the full 100 accuracy points (41 tags × 3 ≈ 123,
  capped). The card gives it 49. It drops from 6th on the gun to 7th on the card.
- **Pack 11** fires 208 shots at 12 %. It's the opposite: the gun gives it about 75, the card the full 100. It climbs
  from 12th to 11th.

The per-player time series for all 16 packs is in
[`8v8-timeline.csv`](scoring-check/results/8v8-timeline.csv) (`seconds,pack,team,gun_score,gun_rank,real_score,real_rank`).

## 11. The reshuffle

Gun at the whistle vs score card, same game:

| Pack | Team | Shots | Tags made | Tags taken | Accuracy | Gun (score, rank) | Card (score, rank) | Change |
|---|---|---|---|---|---|---|---|---|
| 2 | red | 843 | 109 | 43 | 12.9 % | 1014, 1 | 1016, 1 | - |
| 4 | red | 668 | 77 | 31 | 11.5 % | 747, 2 | 759, 2 | - |
| 10 | green | 565 | 62 | 52 | 11.0 % | 515, 3 | 529, 3 | - |
| 12 | green | 477 | 53 | 43 | 11.1 % | 475, 4 | 467, 4 | - |
| 5 | red | 639 | 42 | 26 | 6.6 % | 427, 5 | 387, 5 | - |
| 7 | red | 486 | 39 | 39 | 8.0 % | 313, 8 | 323, 6 | **up 2** |
| 6 | red | 836 | 41 | 34 | 4.9 % | 353, 6 | 322, 7 | down 1 |
| 13 | green | 381 | 35 | 34 | 9.2 % | 316, 8 | 314, 8 | - |
| 15 | green | 308 | 32 | 43 | 10.4 % | 207, 10 | 242, 9 | up 1 |
| 3 | red | 842 | 32 | 32 | 3.8 % | 296, 9 | 231, 10 | down 1 |
| 11 | green | 208 | 25 | 52 | 12.0 % | 124, 12 | 149, 11 | up 1 |
| 1 | red | 312 | 18 | 30 | 5.8 % | 108, 13 | 128, 12 | up 1 |
| 14 | green | 773 | 24 | 39 | 3.1 % | 125, 11 | 120, 13 | down 2 |
| 8 | red | 287 | 17 | 31 | 5.9 % | 75, 15 | 113, 14 | up 1 |
| 9 | green | 557 | 23 | 52 | 4.1 % | 88, 14 | 91, 15 | down 1 |
| 16 | green | 299 | 12 | 60 | 4.0 % | −76, 16 | 20, 16 | - (boosted from −80) |

Who wins and loses at the download is predictable from two numbers, tags made (T) and shots (S):

| Gun bonus | Card bonus | Who gains |
|---|---|---|
| `min(100, int(T × 1000 / 333))` ≈ 3 per tag | `min(100, int(T × 1000 / S))` = accuracy % × 10 | accurate players with fewer than 34 tags gain up to 100. Sprayers with 34+ tags and under 10 % accuracy lose up to 100 |

Plus whatever tags landed after each pack's last poll, and the boost at the bottom of the table.

Over **1,000 simulated 8v8 games** ([section 9 of the results](scoring-check/results/RESULTS-4.10.1.2.md)):

| Measure | Result |
|---|---|
| The gun's #1 at the whistle is not #1 on the card | **9.4 %** of games |
| The team leading on the score monitor at the whistle loses on the card | **4.5 %** of games |
| Average rank change from gun to card | 0.6 places |
| Players who move 3 or more places at the download | 2.4 % |

**The model**, and where each number comes from:

| Parameter | Value | Source |
|---|---|---|
| Game | 15-minute Team game, red 1-8 vs green 9-16 | the site's standard game length |
| Shots per player | 200-900 | Logs: median 483 |
| Accuracy | 3-13 % | Database: hits per shot ≈ 7.5 % |
| Which sensor a tag hits | rear 20.1, front 35.5, phaser 31.8, shoulder 12.7 % | Logs: 933 final uploads |
| Live polls | 55.6 % answered; 1.22 s per good poll, 2.71 s per failed one | Logs |
| Broadcast | when 30 s have passed; 4.55 s on air | Code, logs (copies 1,517 ms apart) |
| Downloads | 75.8 % answered; 2.80 s per success, 3.14 s per failure; STOP re-send 4.2 s | Logs, code |
| Missing final scores | 1.84 % per pack | Database (section 12) |

## 12. When a download fails

**Two ways it happens:**
1. **LQX gives up after 25 failures** (`CollectFinalScoreFromPacks`). Each failure costs a 3 s timeout, and record
   failure counts add STOP re-sends, so 25 failures take a couple of minutes.
2. **The operator ends the collection** (Stop → `IsStopGameSignaled()`). In the logs, this is how every missing score
   happened; the 25-failure limit never fired.

**Either way, the player is scored from their last successful live poll:**
- `ShotsUsed = 333`, so the accuracy bonus is about 3 per tag instead of their real accuracy;
- `LivesUsed = 0`;
- only the tags they had taken as of that poll. Everyone who tagged them after that loses those points.

A player LQX never heard from at all doesn't appear on the card. Neither do the tags anyone made on them, nor the tags
they made: `CalculateScore` only loops over packs it has a record for.

**The fingerprint in the database.** No real pack reports exactly 333 shots very often, but a failed download leaves
exactly 333:

| `shots_used` | Rows |
|---|---|
| 330-332, 334-336 | about 140-180 each |
| **333** | **2,342** (2,196 of them with `lives_used = 0`) |

That's **1.84 %** of all pack-games. By pack, the worst was pack 29 at 5.3 % (it was later disabled), then pack 3
(2.8 %) and pack 18 (2.6 %). The best packs sat around 1.1-1.3 %. A weak radio in a pack makes it both a worse
live-poll partner and a likelier failed download. Players who swore certain packs were cursed had a point.

**What it does to a game.** From the results (sections 4 and 8):

| Case | Effect |
|---|---|
| Your own download fails, few tags but accurate (pack 1 in section 4: 12 tags, 50 shots) | 220 and 1st becomes **156 and 2nd** |
| Your target's download fails before your late tags reach it | the other shooter falls from 192 to 120 |
| In the 8v8 game, pack 10's own download fails | it *gains* 8 (its late tags taken are lost) and pack 2 loses 10 |
| Over 1,000 8v8 games at 1.84 % per pack | 25.5 % of games have at least one failure; it changes the #1 in 2.0 % of those and the winning team in 0.4 % |

So a failed download rarely decides a whole game. When it hits you, though, it can cost a placing outright.

## 13. Oddities

- **Ghost attacker 34.** The site had no pack 34, yet 6 uploads and 21 live replies record tags "from 34"
  **(inference: IR codes misread by the target's sensor)**. The scorer only scores packs in the game, so they vanish.
- **Fewer hits than before.** `ScoreEngine.ValidateMessage` logs "Pack {0} reports fewer hits than before" and accepts
  the upload anyway (it always returns true). Uploads replace, they never add.
- **Score limits on the gun.** The broadcast clamps rank to 0-40 and score to ±9999; ranking casts scores to `short`.
- **No range checks on outgoing numbers.** A 4-digit field fed 10000 would grow a digit and shift the whole frame.
  Nothing in normal play gets near that.
- **Checksum ceiling.** The checksum is only correct while the byte sum stays at or below 0xFFFF. The largest real
  frames stay under about 59,200.
- **Echoes everywhere.** The PC hears its own transmissions and discards them by opcode. That's why
  `ClearReceiveBuffer()` runs before every request.

## 14. How the download could be faster

None of these are in LQX. They're ideas for a replacement controller such as LQ Radio, with rough effects estimated
from the numbers above:

1. **Retry the same pack straight away.** It has already built its upload and answers repeats in about 1.6 s. A
   failure would then cost ~3 + 1.6 s instead of a lap plus a STOP. With about 5 failures per 16-pack game, that's
   roughly 20-30 seconds saved.
2. **Wait 3.5-4 s on the first request.** Typical answers land at 2.64 s against a 3.00 s cut-off; some misses are
   probably packs that answered just too late **(inference: the logs can't show answers that arrived after LQX moved
   on)**.
3. **Don't re-broadcast STOP for every new failure record.** Re-send it only if a pack is still playing.
4. **Show the download live.** Put each score on the screen as it arrives, and say plainly which pack is being waited
   for.

The live scoring can't be fully fixed from the PC: the live reply has no shot count, so true accuracy on the gun needs
pack firmware that reports it. A controller could at least leave the accuracy bonus off the gun, or label it, so the
end-of-game swing isn't a surprise.

## 15. Reproduce everything

```
# Python: frames, checksums, parsers and the scoring port, checked against LQX's own output
python3 tools/lqdownload.py --self-test
python3 tools/lqdownload.py --decode <hex frame>

# C#: LQX's own ScoreEngine (needs Console.exe, Versent.LQX.Core.dll, log4net.dll from an LQX install)
dotnet build scoring-check -c Release          # any OS
scoring-check\bin\Release\net48\ScoringCheck.exe results.md     # Windows: LQX's files are 32-bit .NET Framework
```

The self-test checks:
- the ACK, reset and upload-request frames byte for byte;
- the real upload from 2026-10-15;
- every scoring case in the README;
- the whole 8v8 game: it parses all 16 uploads in
  [`8v8-final-uploads.csv`](scoring-check/results/8v8-final-uploads.csv) (on-air format, checksums included),
  re-scores them in Python, and matches LQX's score card for all 16 players.

Results for both LQX builds are in [`scoring-check/results/`](scoring-check/results/), and they're identical.
