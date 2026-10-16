// ScoringCheck: runs Laser Quest LQX's own compiled scoring code (Console.exe 4.10.1.2, LqxConsole.GamePlay.ScoreEngine)
// on made-up games and prints what the guns would show during the game and what the score cards would show after the
// final download. Nothing here re-implements LQX's maths: every number below comes out of the vendor's ScoreEngine.
//
// The steps copy what LQX's GameEngine does (Console/GamePlay/GameEngine.cs in the decompiled source):
//   live poll    : radio reply 0x40 -> Msg40_RealTimeScoreResponse -> ShotsUsed = 333 (line 978) -> ProcessScoreReply
//   final upload : radio reply 0x53 -> Msg53_PackUploadData -> ProcessScoreReply
//   end          : CalculateScore(true) (line 934), which applies the end-of-game boost
//   gun display  : GetScoreandRank(pack) (line 966) and GetRealTimeScoreBroadcastData() (line 887)
//
// Usage (Windows, .NET Framework 4.8): ScoringCheck.exe [output.md]
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Core.Infrastructure;
using Core.Infrastructure.Comm;
using Core.Persistence;
using LqxConsole.GamePlay;

namespace ScoringCheck
{
    // The source site's only points profile ("Default Points Profile", game_points_profile row 1 in its database).
    sealed class SiteDefaultProfile : IPointsProfile
    {
        public string Description => "Default Points Profile";
        public int YhFrontPoints => 10;
        public int YhRearPoints => 10;
        public int YhLaserPoints => 10;
        public int YhShoulderPoints => 10;
        public int YhScanner => 0;
        public int HyFrontPoints => 5;
        public int HyRearPoints => 4;
        public int HyLaserPoints => 3;
        public int HyShoulderPoints => 3;
        public int MarshallPenalty => 0;          // "marshall laser enabled" = 0 at the site
        public int ShotPenaltiesThreshold => 9999;
        public int ShotPenaltyPoints => 0;
        public int HitPerMissBonusPointsPercent => 10;
        public int HitPerMissMaxBonusPoints => 100;
        public bool EnableBoostedScore => true;
    }

    // Tags a pack has taken from one attacker, by sensor.
    sealed class Taken
    {
        public int From, Front, Rear, Phaser, Shoulder;
        public Taken(int from, int front = 0, int rear = 0, int phaser = 0, int shoulder = 0) { From = from; Front = front; Rear = rear; Phaser = phaser; Shoulder = shoulder; }
    }

    static class Frames
    {
        static string Name(int pack) => "Player: " + pack.ToString("00");     // LQX's default name for unnamed games

        // A 0x40 live reply as the PC receives it: STX "99" '@', then status, name(10), 50 x [rear, front, phaser]
        // (each 0x80 + count), 50 more bytes, stun byte. Shoulder counts go in the 50 extra bytes; see the README.
        public static byte[] Live(int pack, params Taken[] taken)
        {
            var f = new byte[4 + 1 + 10 + 150 + 50 + 1];
            Head(f, 0x40); f[4] = (byte)'0'; Put(f, 5, Name(pack));
            for (int i = 0; i < 50; i++) { f[15 + i * 3] = 0x80; f[16 + i * 3] = 0x80; f[17 + i * 3] = 0x80; f[165 + i] = 0x80; }
            foreach (var t in taken)
            {
                f[15 + t.From * 3] = (byte)(0x80 + t.Rear); f[16 + t.From * 3] = (byte)(0x80 + t.Front); f[17 + t.From * 3] = (byte)(0x80 + t.Phaser);
                f[165 + t.From] = (byte)(0x80 + t.Shoulder);
            }
            f[215] = (byte)'0';
            return f;
        }

        // A 0x53 final upload: STX "99" 'S', then status, colour, shots(4), lives(3), pack(2), filler, name(10),
        // 50 x [rear, front, phaser, shoulder] (each 0x80 + count).
        public static byte[] Final(int pack, TeamColors colour, int shots, int lives, params Taken[] taken)
        {
            var f = new byte[4 + 22 + 200];
            Head(f, 0x53); f[4] = (byte)'0'; f[5] = (byte)colour;
            Put(f, 6, shots.ToString("0000")); Put(f, 10, lives.ToString("000")); Put(f, 13, pack.ToString("00")); f[15] = (byte)'0';
            Put(f, 16, Name(pack));
            for (int i = 0; i < 200; i++) f[26 + i] = 0x80;
            foreach (var t in taken)
            {
                f[26 + t.From * 4] = (byte)(0x80 + t.Rear); f[27 + t.From * 4] = (byte)(0x80 + t.Front);
                f[28 + t.From * 4] = (byte)(0x80 + t.Phaser); f[29 + t.From * 4] = (byte)(0x80 + t.Shoulder);
            }
            return f;
        }

        static void Head(byte[] f, byte op) { f[0] = 0x02; f[1] = (byte)'9'; f[2] = (byte)'9'; f[3] = op; }
        static void Put(byte[] f, int at, string s) { var b = Encoding.GetEncoding("ISO-8859-1").GetBytes(s); Array.Copy(b, 0, f, at, b.Length); }
    }

    // One game, driven the way GameEngine drives ScoreEngine.
    sealed class Game
    {
        public readonly ScoreEngine Engine;
        readonly Dictionary<int, TeamColors> _teams = new Dictionary<int, TeamColors>();

        public Game(GameType type, params int[] packs) : this(type, packs.ToDictionary(p => p, p => TeamColors._32_MIXED)) { }
        public Game(GameType type, Dictionary<int, TeamColors> teams) { Engine = new ScoreEngine(type, new SiteDefaultProfile()); _teams = teams; }

        static string Name(int pack) => "Player: " + pack.ToString("00");

        public PackUploadData LivePoll(int pack, params Taken[] taken)
        {
            var data = new Msg40_RealTimeScoreResponse(Frames.Live(pack, taken)).PackUploadData;
            data.ShotsUsed = 333;                                            // GameEngine.CollectLiveScore, line 978
            Engine.ProcessScoreReply((DeviceIds)pack, Name(pack), null, _teams[pack], data);
            return data;
        }

        public PackUploadData FinalUpload(int pack, int shots, params Taken[] taken)
        {
            var data = new Msg53_PackUploadData(Frames.Final(pack, _teams[pack], shots, 0, taken)).PackUploadData;
            Engine.ProcessScoreReply((DeviceIds)pack, Name(pack), null, _teams[pack], data);
            return data;
        }

        public void EndOfDownload() => Engine.CalculateScore(true);       // GameEngine, line 934

        public KeyValuePair<int, int> Gun(int pack) => Engine.GetScoreandRank((DeviceIds)pack);   // (rank, score)
        public PackScore Card(int pack) => Engine.PackScores.First(p => (int)p.DeviceId == pack);
    }

    static class Program
    {
        static readonly StringBuilder Out = new StringBuilder();
        static void W(string s = "") { Out.AppendLine(s); Console.WriteLine(s); }

        static int Main(string[] args)
        {
            Header();
            Accuracy();
            LiveVersusFinal();
            MissedPoll();
            FailedDownload();
            Shoulders();
            Ranks();
            AdvancedTeam();
            if (args.Length > 0) File.WriteAllText(args[0], Out.ToString(), new UTF8Encoding(false));
            return 0;
        }

        static void Header()
        {
            var console = typeof(ScoreEngine).Assembly; var core = typeof(PackUploadData).Assembly;
            W("# ScoringCheck results");
            W();
            W("Every number below was computed by the LQX assemblies listed here, loaded unmodified.");
            W();
            W("| Assembly | Version | PE link time (UTC) | SHA-256 |");
            W("|---|---|---|---|");
            foreach (var a in new[] { console, core })
                W("| " + Path.GetFileName(a.Location) + " | " + a.GetName().Version + " | " + LinkTime(a.Location) + " | `" + Sha256(a.Location) + "` |");
            W();
            W("Runtime: .NET Framework " + Environment.Version + ", " + (Environment.Is64BitProcess ? "64" : "32") + "-bit process, " + Environment.OSVersion + ".");
            W("Run at " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm") + " UTC.");
        }

        // 1. The accuracy bonus is (accuracy %) x 10, capped at 100.
        static void Accuracy()
        {
            W(); W("## 1. Accuracy bonus = accuracy % x 10, capped at 100");
            W(); W("Pack 1 tags pack 2 ten times on the front. Only pack 1's shot count changes.");
            W(); W("| Shots fired | Accuracy % (HitRatioPercent) | Bonus (HitRatioPoints) | Score |");
            W("|---|---|---|---|");
            foreach (int shots in new[] { 400, 200, 100, 50, 20 })
            {
                var g = new Game(GameType.Solo, 1, 2);
                g.FinalUpload(1, shots);
                g.FinalUpload(2, 0, new Taken(1, front: 10));
                var c = g.Card(1);
                W("| " + shots + " | " + c.HitRatioPercent + " | " + c.HitRatioPoints + " | " + c.Score + " |");
            }
        }

        // 2. The gun scores accuracy as if everyone fired 333 shots; the card uses real shots, so ranks can swap.
        static void LiveVersusFinal()
        {
            W(); W("## 2. On the gun vs on the score card (live polls use 333 shots)");
            W(); W("Pack 1 makes 12 tags with 50 shots. Pack 2 makes 16 tags with 500 shots. Pack 3 is the target (40 shots, no tags).");
            var g = new Game(GameType.Solo, 1, 2, 3);
            g.LivePoll(1); g.LivePoll(2); g.LivePoll(3, new Taken(1, front: 12), new Taken(2, front: 16));
            var live = new[] { 1, 2, 3 }.ToDictionary(p => p, p => new { gun = g.Gun(p), bonus = g.Card(p).HitRatioPoints });
            var bc = g.Engine.GetRealTimeScoreBroadcastData();
            g.FinalUpload(1, 50); g.FinalUpload(2, 500); g.FinalUpload(3, 40, new Taken(1, front: 12), new Taken(2, front: 16));
            g.EndOfDownload();
            W(); W("| Pack | Tags | Shots | Gun: bonus | Gun: score | Gun: rank | Broadcast (rank, score) | Card: bonus | Card: boost | Card: score | Card: rank |");
            W("|---|---|---|---|---|---|---|---|---|---|---|");
            int[] tags = { 0, 12, 16, 0 }, shots = { 0, 50, 500, 40 };
            foreach (int p in new[] { 1, 2, 3 })
            {
                var c = g.Card(p);
                W("| " + p + " | " + tags[p] + " | " + shots[p] + " | " + live[p].bonus + " | " + live[p].gun.Value + " | " + live[p].gun.Key + " | " + bc.Rank[p - 1] + ", " + bc.Score[p - 1] +
                  " | " + c.HitRatioPoints + " | " + c.BoostPoints + " | " + c.Score + " | " + c.Rank + " |");
            }
        }

        // 3. A missed poll loses nothing: each reply carries the pack's running totals and replaces the old ones.
        static void MissedPoll()
        {
            W(); W("## 3. A missed radio poll loses nothing");
            W(); W("Pack 1 tags pack 2. Pack 2's first poll gets through (5 tags so far), its second is lost, then its final upload says 12.");
            var g = new Game(GameType.Solo, 1, 2);
            g.LivePoll(1); g.LivePoll(2, new Taken(1, front: 5));
            int afterFirst = g.Card(1).LivesTaken;
            g.FinalUpload(1, 0); g.FinalUpload(2, 0, new Taken(1, front: 12));
            W(); W("| Step | Pack 1's tags counted (LivesTaken) |");
            W("|---|---|");
            W("| After the first poll | " + afterFirst + " |");
            W("| Second poll lost | " + afterFirst + " (nothing sent) |");
            W("| After the final upload | " + g.Card(1).LivesTaken + " (replaced, not added: 5 + 12 would be 17) |");
        }

        // 4. A failed final download leaves that pack scored from its last live poll.
        static void FailedDownload()
        {
            W(); W("## 4. When a final download fails");
            W(); W("Same game as section 2. LQX gives up on a pack after 25 tries and keeps its last live data (GameEngine lines 915-933).");
            W(); W("| Case | Pack 1 score, rank | Pack 2 score, rank | Pack 3 score, rank |");
            W("|---|---|---|---|");
            foreach (var failed in new[] { 0, 1, 3 })
            {
                var g = new Game(GameType.Solo, 1, 2, 3);
                g.LivePoll(1); g.LivePoll(2);
                g.LivePoll(3, new Taken(1, front: 8), new Taken(2, front: 10));        // pack 3's last good poll, before the last tags
                if (failed != 1) g.FinalUpload(1, 50);
                g.FinalUpload(2, 500);
                if (failed != 3) g.FinalUpload(3, 40, new Taken(1, front: 12), new Taken(2, front: 16));
                g.EndOfDownload();
                string label = failed == 0 ? "All downloads arrive" : failed == 1 ? "Pack 1's own download fails" : "Pack 3's (the target's) download fails";
                W("| " + label + " | " + string.Join(" | ", new[] { 1, 2, 3 }.Select(p => g.Card(p).Score + ", " + g.Card(p).Rank)) + " |");
            }
        }

        // 5. Shoulder tags: the live parser reads three sensor columns, the final parser four.
        static void Shoulders()
        {
            W(); W("## 5. Shoulder tags only count from the final upload");
            W(); W("Pack 1 tags pack 2 six times on the front and four times on the shoulder.");
            var g = new Game(GameType.Solo, 1, 2);
            g.LivePoll(1);
            var live = g.LivePoll(2, new Taken(1, front: 6, shoulder: 4));
            int liveScore = g.Card(1).Score - g.Card(1).HitRatioPoints;
            g.FinalUpload(1, 0);
            var fin = g.FinalUpload(2, 0, new Taken(1, front: 6, shoulder: 4));
            W(); W("| | Shoulder tags parsed | Pack 1's tag points |");
            W("|---|---|---|");
            W("| Live poll (Msg40 parser) | " + live.HitDataShoulder[1] + " | " + liveScore + " |");
            W("| Final upload (Msg53 parser) | " + fin.HitDataShoulder[1] + " | " + (g.Card(1).Score - g.Card(1).HitRatioPoints) + " |");
        }

        // 6. Ranks are dense: ties share a rank and the next score takes the next number.
        static void Ranks()
        {
            W(); W("## 6. Ties share a rank (1, 1, 2)");
            var g = new Game(GameType.Solo, 1, 2, 3, 4);
            g.FinalUpload(1, 0); g.FinalUpload(2, 0); g.FinalUpload(3, 0);
            g.FinalUpload(4, 0, new Taken(1, front: 10), new Taken(2, front: 10), new Taken(3, front: 9));
            W(); W("| Pack | Score | Rank |"); W("|---|---|---|");
            foreach (int p in new[] { 1, 2, 3, 4 }) W("| " + p + " | " + g.Card(p).Score + " | " + g.Card(p).Rank + " |");
        }

        // 7. Advanced Team: tagging a teammate costs the shooter and not the teammate.
        static void AdvancedTeam()
        {
            W(); W("## 7. Team vs Advanced Team: tagging your own team");
            W(); W("Packs 1 and 2 are red, pack 3 green. Pack 1 tags pack 2 (teammate) 5 times and pack 3 5 times, all on the front.");
            W(); W("| Game type | Pack 1 | Pack 2 | Pack 3 |"); W("|---|---|---|---|");
            foreach (var type in new[] { GameType.Team, GameType.AdvancedTeam })
            {
                var g = new Game(type, new Dictionary<int, TeamColors> { { 1, TeamColors._30_RED }, { 2, TeamColors._30_RED }, { 3, TeamColors._31_GREEN } });
                g.FinalUpload(1, 0); g.FinalUpload(2, 0, new Taken(1, front: 5)); g.FinalUpload(3, 0, new Taken(1, front: 5));
                W("| " + type + " | " + g.Card(1).Score + " | " + g.Card(2).Score + " | " + g.Card(3).Score + " |");
            }
        }

        static string Sha256(string path) { using (var s = SHA256.Create()) return BitConverter.ToString(s.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant(); }

        static string LinkTime(string path)
        {
            var b = File.ReadAllBytes(path); int pe = BitConverter.ToInt32(b, 0x3c);
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(BitConverter.ToUInt32(b, pe + 8)).ToString("yyyy-MM-dd HH:mm:ss");
        }
    }
}
