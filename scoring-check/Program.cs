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

        // A 0x40 live reply as real packs send it: STX "99" '@', then status, name(10), 50 x [rear, front, phaser+shoulder]
        // (each 0x80 + count), 50 bytes that are always 0x80, stun byte. The site's radio logs settled this layout:
        // column 3 carries phaser and shoulder tags together (10,971 entries compared with the same packs' final uploads).
        public static byte[] Live(int pack, params Taken[] taken)
        {
            var f = new byte[4 + 212];
            Head(f, 0x40); f[4] = (byte)'0'; Put(f, 5, Name(pack));
            for (int i = 0; i < 50; i++) { f[15 + i * 3] = 0x80; f[16 + i * 3] = 0x80; f[17 + i * 3] = 0x80; f[165 + i] = 0x80; }
            foreach (var t in taken)
            {
                f[15 + t.From * 3] = (byte)(0x80 + t.Rear); f[16 + t.From * 3] = (byte)(0x80 + t.Front);
                f[17 + t.From * 3] = (byte)(0x80 + t.Phaser + t.Shoulder);
            }
            f[215] = 0x00;                                                        // StunInfo: always 0x00 in the site's logs
            return Close(f);
        }

        // A 0x53 final upload: STX "99" 'S', then status, colour, shots(4), lives(3), pack(2), filler, name(10),
        // 50 x [rear, front, phaser, shoulder] (each 0x80 + count).
        public static byte[] Final(int pack, TeamColors colour, int shots, int lives, params Taken[] taken)
        {
            var f = new byte[4 + 222];
            Head(f, 0x53); f[4] = (byte)'0'; f[5] = (byte)colour;
            Put(f, 6, shots.ToString("0000")); Put(f, 10, lives.ToString("000")); Put(f, 13, pack.ToString("00")); f[15] = (byte)'0';
            Put(f, 16, Name(pack));
            for (int i = 0; i < 200; i++) f[26 + i] = 0x80;
            foreach (var t in taken)
            {
                f[26 + t.From * 4] = (byte)(0x80 + t.Rear); f[27 + t.From * 4] = (byte)(0x80 + t.Front);
                f[28 + t.From * 4] = (byte)(0x80 + t.Phaser); f[29 + t.From * 4] = (byte)(0x80 + t.Shoulder);
            }
            return Close(f);
        }

        // Append EOT, the 4-character checksum and ETX, as LqxMessage does: sum of STX..EOT, as 4 hex digits, each digit
        // written as nibble + 0x30 (so A-F become ':' to '?').
        static byte[] Close(byte[] body)
        {
            var f = new byte[body.Length + 6];
            Array.Copy(body, f, body.Length);
            f[body.Length] = 0x04;
            int sum = 0; for (int i = 0; i <= body.Length; i++) sum += f[i];
            for (int k = 0; k < 4; k++) f[body.Length + 1 + k] = (byte)(0x30 + ((sum >> (12 - 4 * k)) & 0xF));
            f[f.Length - 1] = 0x03;
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

        public PackUploadData FinalUpload(int pack, int shots, params Taken[] taken) => FinalUpload(Frames.Final(pack, _teams[pack], shots, taken.Sum(t => t.Front + t.Rear + t.Phaser + t.Shoulder), taken));

        public PackUploadData FinalUpload(byte[] frame)
        {
            var data = new Msg53_PackUploadData(frame).PackUploadData;
            int pack = (int)data.PayloadPackId;
            Engine.ProcessScoreReply((DeviceIds)pack, Name(pack), null, _teams[pack], data);
            return data;
        }

        public void EndOfDownload() => Engine.CalculateScore(true);       // GameEngine, line 934

        public KeyValuePair<int, int> Gun(int pack) => Engine.GetScoreandRank((DeviceIds)pack);   // (rank, score)
        public PackScore Card(int pack) => Engine.PackScores.FirstOrDefault(p => (int)p.DeviceId == pack);   // null: LQX never heard from it
        public int ScoreOf(int pack) { var c = Card(pack); return c == null ? 0 : c.Score; }
    }

    // A simulated 8v8 Team game, run through LQX's ScoreEngine the way GameEngine runs it.
    sealed class SimPlayer
    {
        public int Pack, Shots, TagsMade, TagsTaken, GunScore, GunRank, CardScore, CardRank;
        public double Accuracy;
        public TeamColors Team;
        public string TeamName => Team == TeamColors._30_RED ? "red" : "green";
        public byte[] FinalFrame;
    }

    sealed class SimResult
    {
        public List<SimPlayer> Players;
        public int Polls, GoodPolls, Broadcasts, Requests, StopRebroadcasts, LiveRed, LiveGreen, CardRed, CardGreen;
        public double CollectSeconds;
        public List<double> Arrivals = new List<double>();                 // seconds after time-up when each final score arrived
        public List<string> Timeline = new List<string>(), Download = new List<string>(), Trajectory = new List<string>();
    }

    sealed class Sim
    {
        const double GameSeconds = 900;
        static readonly double[] SensorShare = { 0.201, 0.355, 0.318, 0.127 };     // rear, front, phaser, shoulder (site's final uploads)
        readonly Random _r;
        readonly List<SimPlayer> _players = new List<SimPlayer>();
        readonly List<double[]> _tags = new List<double[]>();                        // time, from, to, sensor

        public Sim(Random r)
        {
            _r = r;
            for (int pack = 1; pack <= 16; pack++)
            {
                var p = new SimPlayer { Pack = pack, Team = pack <= 8 ? TeamColors._30_RED : TeamColors._31_GREEN, Shots = _r.Next(200, 901), Accuracy = 0.03 + _r.NextDouble() * 0.10 };
                p.TagsMade = (int)Math.Round(p.Shots * p.Accuracy);
                _players.Add(p);
            }
            foreach (var p in _players)
                for (int i = 0; i < p.TagsMade; i++)
                {
                    var foes = _players.Where(q => q.Team != p.Team).ToList();
                    var to = foes[_r.Next(foes.Count)];
                    double u = _r.NextDouble(), acc = 0; int sensor = 3;
                    for (int k = 0; k < 4; k++) { acc += SensorShare[k]; if (u < acc) { sensor = k; break; } }
                    _tags.Add(new double[] { _r.NextDouble() * GameSeconds, p.Pack, to.Pack, sensor });
                    to.TagsTaken++;
                }
        }

        static string Clock(double t) => ((int)t / 60) + ":" + ((int)t % 60).ToString("00");

        Taken[] TakenBy(int pack, double until)
        {
            var t = new Dictionary<int, Taken>();
            foreach (var e in _tags.Where(e => (int)e[2] == pack && e[0] < until))
            {
                int from = (int)e[1];
                if (!t.ContainsKey(from)) t[from] = new Taken(from);
                switch ((int)e[3]) { case 0: t[from].Rear++; break; case 1: t[from].Front++; break; case 2: t[from].Phaser++; break; default: t[from].Shoulder++; break; }
            }
            return t.Values.ToArray();
        }

        // What the final scores would be if every pack's hits were known at time t (shots pro rata), before any boost.
        Dictionary<int, PackScore> TrueAt(double t)
        {
            var g = new Game(GameType.Team, _players.ToDictionary(p => p.Pack, p => p.Team));
            foreach (var p in _players)
            {
                var taken = TakenBy(p.Pack, t);
                g.FinalUpload(Frames.Final(p.Pack, p.Team, (int)Math.Round(p.Shots * t / GameSeconds), taken.Sum(x => x.Front + x.Rear + x.Phaser + x.Shoulder), taken));
            }
            return _players.ToDictionary(p => p.Pack, p => g.Card(p.Pack));
        }

        public SimResult Play(int[] failedPacks, bool keepFrames)
        {
            var g = new Game(GameType.Team, _players.ToDictionary(p => p.Pack, p => p.Team));
            var res = new SimResult { Players = _players.Select(p => new SimPlayer { Pack = p.Pack, Team = p.Team, Shots = p.Shots, TagsMade = p.TagsMade, TagsTaken = p.TagsTaken, Accuracy = p.Accuracy }).ToList() };
            var shown = res.Players.ToDictionary(p => p.Pack, p => new KeyValuePair<int, int>(0, 0));
            var poll = new Random(_r.Next());                                          // radio luck, separate from the game itself
            double t = 0, lastBroadcast = 0; int next = 0;
            while (t < GameSeconds)
            {
                if (t > lastBroadcast + 30)
                {
                    var bc = g.Engine.GetRealTimeScoreBroadcastData();
                    foreach (var p in res.Players) shown[p.Pack] = new KeyValuePair<int, int>(bc.Rank[p.Pack - 1], bc.Score[p.Pack - 1]);
                    if (keepFrames)
                    {
                        var truth = TrueAt(t);
                        int gunLead = res.Players.OrderByDescending(p => bc.Score[p.Pack - 1]).ThenBy(p => p.Pack).First().Pack;
                        int realLead = truth.OrderByDescending(kv => kv.Value.Score).ThenBy(kv => kv.Key).First().Key;
                        Func<TeamColors, int> mon = team => res.Players.Where(p => p.Team == team).Sum(p => g.ScoreOf(p.Pack));
                        Func<TeamColors, int> real = team => res.Players.Where(p => p.Team == team).Sum(p => truth[p.Pack].Score);
                        res.Timeline.Add(Clock(t) + " | " + gunLead + " (" + bc.Score[gunLead - 1] + ") | " + realLead + " (" + truth[realLead].Score + ") | " +
                                         mon(TeamColors._30_RED) + " vs " + mon(TeamColors._31_GREEN) + " | " + real(TeamColors._30_RED) + " vs " + real(TeamColors._31_GREEN));
                        foreach (var p in res.Players)
                            res.Trajectory.Add(((int)t) + "," + p.Pack + "," + p.TeamName + "," + bc.Score[p.Pack - 1] + "," + bc.Rank[p.Pack - 1] + "," + truth[p.Pack].Score + "," + truth[p.Pack].Rank);
                    }
                    res.Broadcasts++; lastBroadcast = t; t += 4.55; continue;
                }
                int pack = res.Players[next % 16].Pack; next++;
                res.Polls++;
                var before = g.Gun(pack);                                                  // GameEngine reads rank and score first...
                if (poll.NextDouble() < 0.556)
                {
                    shown[pack] = before;                                                   // ...the pack shows them...
                    g.LivePoll(pack, TakenBy(pack, t));                                     // ...and its reply is scored
                    res.GoodPolls++; t += 1.22;
                }
                else t += 2.71;
            }
            foreach (var p in res.Players) { p.GunRank = shown[p.Pack].Key; p.GunScore = shown[p.Pack].Value; }
            res.LiveRed = res.Players.Where(p => p.Team == TeamColors._30_RED).Sum(p => g.ScoreOf(p.Pack));
            res.LiveGreen = res.Players.Where(p => p.Team == TeamColors._31_GREEN).Sum(p => g.ScoreOf(p.Pack));

            // Download: rotate through the packs until each has given its final score; failed packs never do.
            var pending = res.Players.Where(p => !failedPacks.Contains(p.Pack)).Select(p => p.Pack).ToList();
            var fails = res.Players.ToDictionary(p => p.Pack, p => 0);
            int maxFails = 0;
            double c = 6.2;                                                                 // STOP x3 (HSDU and RS-485), then 2 s
            while (pending.Count > 0)
            {
                foreach (var pack in pending.ToList())
                {
                    if (fails[pack] > maxFails)                                             // CollectFinalScoreFromPacks: a new worst
                    {                                                                       // failure count re-broadcasts STOP
                        maxFails = fails[pack]; c += 4.2; res.StopRebroadcasts++;
                        if (keepFrames) res.Download.Add(Clock(c - 4.2) + " | STOP x3 again (pack " + pack + " has now failed " + fails[pack] + " times) | ");
                    }
                    res.Requests++;
                    if (poll.NextDouble() < 0.758)
                    {
                        var me = res.Players.First(p => p.Pack == pack);
                        var taken = TakenBy(pack, GameSeconds);
                        var frame = Frames.Final(pack, me.Team, me.Shots, Math.Min(999, me.TagsTaken), taken);
                        if (keepFrames) me.FinalFrame = frame;
                        g.FinalUpload(frame);
                        pending.Remove(pack); c += 2.80; res.Arrivals.Add(c);
                        if (keepFrames) res.Download.Add(Clock(c - 2.80) + " | 0x54 to pack " + pack + " | answered, score in (" + (16 - failedPacks.Length - pending.Count) + " of " + (16 - failedPacks.Length) + ")");
                    }
                    else
                    {
                        c += 3.14; fails[pack]++;
                        if (keepFrames) res.Download.Add(Clock(c - 3.14) + " | 0x54 to pack " + pack + " | no answer in 3 s (try " + fails[pack] + ")");
                    }
                }
            }
            res.CollectSeconds = c;
            g.EndOfDownload();
            foreach (var p in res.Players) { var card = g.Card(p.Pack); p.CardScore = card == null ? 0 : card.Score; p.CardRank = card == null ? 0 : card.Rank; }
            res.CardRed = res.Players.Where(p => p.Team == TeamColors._30_RED).Sum(p => p.CardScore);
            res.CardGreen = res.Players.Where(p => p.Team == TeamColors._31_GREEN).Sum(p => p.CardScore);
            return res;
        }
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
            string dir = args.Length > 0 ? Path.GetDirectoryName(Path.GetFullPath(args[0])) : ".";
            EightVersusEight(dir);
            ManyGames();
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

        // 5. Shoulder tags: live replies carry them inside the phaser column; the final upload separates them.
        static void Shoulders()
        {
            W(); W("## 5. Shoulder tags during the game");
            W(); W("Pack 1 tags pack 2 six times on the front and four times on the shoulder. Live replies carry shoulder tags in the phaser column, as real packs do.");
            var g = new Game(GameType.Solo, 1, 2);
            g.LivePoll(1);
            var live = g.LivePoll(2, new Taken(1, front: 6, shoulder: 4));
            int liveAttack = g.Card(1).Score - g.Card(1).HitRatioPoints, liveDefence = g.Card(2).Score - g.Card(2).HitRatioPoints;
            g.FinalUpload(1, 0);
            var fin = g.FinalUpload(2, 0, new Taken(1, front: 6, shoulder: 4));
            W(); W("| | Phaser column | Shoulder column | Pack 1 tag points | Pack 2 tag points |");
            W("|---|---|---|---|---|");
            W("| Live poll (Msg40 parser) | " + live.HitDataHhu[1] + " | " + live.HitDataShoulder[1] + " | " + liveAttack + " | " + liveDefence + " |");
            W("| Final upload (Msg53 parser) | " + fin.HitDataHhu[1] + " | " + fin.HitDataShoulder[1] + " | " + (g.Card(1).Score - g.Card(1).HitRatioPoints) + " | " + g.Card(2).Score + " |");
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

        // 8. One full 8v8 Team game, simulated with the site's measured numbers, then downloaded.
        static void EightVersusEight(string dir)
        {
            var sim = new Sim(new Random(2026));
            var r = sim.Play(failedPacks: new int[0], keepFrames: true);
            W(); W("## 8. A full 8v8 game (red 1-8 vs green 9-16), seed 2026");
            W(); W("Model (see THE-FAMOUS-LQ-DOWNLOAD.md for the sources): 15-minute Team game; each player fires 200-900 shots with 3-13 % accuracy; " +
                   "each tag picks a random opponent and a sensor in the site's measured shares (rear 20.1, front 35.5, phaser 31.8, shoulder 12.7 %). " +
                   "Live loop as GameEngine runs it: leaderboard broadcast when 30 s have passed (4.55 s on air), otherwise poll the next pack " +
                   "(55.6 % of polls answered; 1.22 s per good poll, 2.71 s per failed one). Download: 75.8 % of requests answered first time; 2.80 s per good request, 3.14 s per failed one.");
            W(); W("Live phase: " + r.Polls + " polls (" + r.GoodPolls + " answered), " + r.Broadcasts + " broadcasts. Download: " + r.Requests + " requests, " + r.CollectSeconds.ToString("0") + " s.");
            W(); W("| Pack | Team | Shots | Tags made | Tags taken | Accuracy % | Gun at the whistle (score, rank) | Score card (score, rank) | Rank change |");
            W("|---|---|---|---|---|---|---|---|---|");
            foreach (var p in r.Players.OrderBy(p => p.CardRank).ThenBy(p => p.Pack))
                W("| " + p.Pack + " | " + p.TeamName + " | " + p.Shots + " | " + p.TagsMade + " | " + p.TagsTaken + " | " + (100.0 * p.TagsMade / p.Shots).ToString("0.0") +
                  " | " + p.GunScore + ", " + p.GunRank + " | " + p.CardScore + ", " + p.CardRank + " | " + Move(p.GunRank - p.CardRank) + " |");
            W(); W("| Team | Score monitor at the whistle | Score card (sum of player scores) |");
            W("|---|---|---|");
            W("| Red | " + r.LiveRed + " | " + r.CardRed + " |");
            W("| Green | " + r.LiveGreen + " | " + r.CardGreen + " |");

            W(); W("### Across the game: what the guns showed vs the real standings");
            W(); W("At each leaderboard broadcast. \"Real\" = what the score card would say if every pack's hits were known at that moment (shots pro rata, before the end-of-game boost). " +
                   "The score monitor shows LQX's own running totals, built only from the polls that got through.");
            W(); W("| Time | Gun leader (pack, score shown) | Real leader | Score monitor red vs green | Real red vs green |");
            W("|---|---|---|---|---|");
            foreach (var row in r.Timeline) W("| " + row + " |");
            W("| Score card | " + r.Players.First(p => p.CardRank == 1).Pack + " (" + r.Players.First(p => p.CardRank == 1).CardScore + ") | | | " + r.CardRed + " vs " + r.CardGreen + " |");
            W(); W("### The download, request by request");
            W(); W("Time after the game ends. " + r.Requests + " requests, " + r.StopRebroadcasts + " STOP re-broadcasts, last score in at " + (r.CollectSeconds).ToString("0") + " s.");
            W(); W("| Time | Sent | Result |"); W("|---|---|---|");
            foreach (var row in r.Download) W("| " + row + " |");
            File.WriteAllText(Path.Combine(dir, "8v8-timeline.csv"), "# seconds,pack,team,gun_score,gun_rank,real_score,real_rank\r\n" + string.Join("\r\n", r.Trajectory) + "\r\n", new UTF8Encoding(false));

            var top = r.Players.Where(p => p.CardRank <= 8).OrderByDescending(p => p.CardScore - p.GunScore).First();   // most to gain from the download
            var failOwn = new Sim(new Random(2026)).Play(failedPacks: new[] { top.Pack }, keepFrames: false);
            var two = r.Players.Where(p => p.Team == TeamColors._31_GREEN).OrderByDescending(p => p.TagsTaken).Take(2).Select(p => p.Pack).ToArray();
            var failTwo = new Sim(new Random(2026)).Play(failedPacks: two, keepFrames: false);
            W(); W("### The same game when downloads fail");
            W(); W("| Case | Pack " + top.Pack + " (score, rank) | Top 3 on the card | Red | Green |");
            W("|---|---|---|---|---|");
            foreach (var row in new[] { Tuple.Create("All downloads arrive", r), Tuple.Create("Pack " + top.Pack + "'s own download fails", failOwn),
                                        Tuple.Create("Packs " + two[0] + " and " + two[1] + " (most-tagged greens) have no final score", failTwo) })
            {
                var t = row.Item2.Players.First(p => p.Pack == top.Pack);
                W("| " + row.Item1 + " | " + t.CardScore + ", " + t.CardRank + " | " + string.Join(", ", row.Item2.Players.OrderBy(p => p.CardRank).ThenBy(p => p.Pack).Take(3).Select(p => p.Pack + " (" + p.CardScore + ")")) +
                  " | " + row.Item2.CardRed + " | " + row.Item2.CardGreen + " |");
            }

            // Frames for anyone who wants to check another implementation against LQX: every final upload of the baseline game.
            var sb = new StringBuilder();
            sb.AppendLine("# 8v8 game, seed 2026: every final upload as the PC receives it (0x53, STX through ETX, as hex) and LQX's score card");
            sb.AppendLine("# pack,team,card_score,card_rank,frame_hex");
            foreach (var p in r.Players.OrderBy(p => p.Pack))
                sb.AppendLine(p.Pack + "," + p.TeamName + "," + p.CardScore + "," + p.CardRank + "," + BitConverter.ToString(p.FinalFrame).Replace("-", ""));
            File.WriteAllText(Path.Combine(dir, "8v8-final-uploads.csv"), sb.ToString(), new UTF8Encoding(false));
        }

        // 9. A thousand such games: how often does the download change the result?
        static void ManyGames()
        {
            const int games = 1000;
            var rnd = new Random(4800);
            int topDiffers = 0, teamFlips = 0, gamesWithFailure = 0, failureChangesTop = 0, failureFlipsTeam = 0, players = 0, moved3 = 0;
            double sumMove = 0;
            var collect = new List<double>(); var lastGap = new List<double>(); var stops = new List<int>();
            for (int i = 0; i < games; i++)
            {
                int seed = rnd.Next();
                var clean = new Sim(new Random(seed)).Play(new int[0], false);
                var gunLeader = clean.Players.OrderByDescending(p => p.GunScore).ThenBy(p => p.Pack).First();   // highest score showing on any gun
                if (gunLeader.CardRank != 1) topDiffers++;
                if (Math.Sign(clean.LiveRed - clean.LiveGreen) != Math.Sign(clean.CardRed - clean.CardGreen)) teamFlips++;
                collect.Add(clean.CollectSeconds); stops.Add(clean.StopRebroadcasts);
                var arr = clean.Arrivals.OrderBy(x => x).ToList(); lastGap.Add(arr[arr.Count - 1] - arr[arr.Count - 4]);
                foreach (var p in clean.Players) { players++; int d = Math.Abs(p.GunRank - p.CardRank); sumMove += d; if (d >= 3) moved3++; }
                // The site's database: 1.84 % of pack-games ended with no final score.
                var fr = new Random(seed ^ 0x5A5A5A);
                var failed = Enumerable.Range(1, 16).Where(p => fr.NextDouble() < 0.0184).ToArray();
                if (failed.Length == 0) continue;
                gamesWithFailure++;
                var hit = new Sim(new Random(seed)).Play(failed, false);
                int cleanTop = clean.Players.First(p => p.CardRank == 1).Pack, hitTop = hit.Players.First(p => p.CardRank == 1).Pack;
                if (cleanTop != hitTop) failureChangesTop++;
                if (Math.Sign(clean.CardRed - clean.CardGreen) != Math.Sign(hit.CardRed - hit.CardGreen)) failureFlipsTeam++;
            }
            W(); W("## 9. A thousand 8v8 games");
            W(); W("Same model as section 8, 1,000 games (seeds from Random(4800)). Failures: each pack independently has no final score with probability 1.84 %, the site's rate.");
            W(); W("| Measure | Result |"); W("|---|---|");
            W("| Gun's #1 at the whistle is not #1 on the card | " + Pct(topDiffers, games) + " of games |");
            W("| Winning team on the score monitor at the whistle loses on the card | " + Pct(teamFlips, games) + " of games |");
            W("| Average rank change from gun to card | " + (sumMove / players).ToString("0.0") + " places |");
            W("| Players who move 3 or more places at the download | " + Pct(moved3, players) + " |");
            W("| Games where at least one pack has no final score | " + Pct(gamesWithFailure, games) + " |");
            W("| ... of those, the failure changes who is #1 | " + Pct(failureChangesTop, gamesWithFailure) + " |");
            W("| ... of those, the failure flips the winning team | " + Pct(failureFlipsTeam, gamesWithFailure) + " |");
            W(); W("How long the download takes (16 packs, every pack answers eventually):");
            W(); W("| | Median | 90th percentile | Longest |"); W("|---|---|---|---|");
            W("| Game end to last score | " + Q(collect, 0.5) + " s | " + Q(collect, 0.9) + " s | " + collect.Max().ToString("0") + " s |");
            W("| Waiting for the last 3 packs | " + Q(lastGap, 0.5) + " s | " + Q(lastGap, 0.9) + " s | " + lastGap.Max().ToString("0") + " s |");
            W("| STOP re-broadcasts | " + Q(stops.Select(x => (double)x).ToList(), 0.5) + " | " + Q(stops.Select(x => (double)x).ToList(), 0.9) + " | " + stops.Max() + " |");
        }

        static string Q(List<double> v, double q) { var o = v.OrderBy(x => x).ToList(); return o[(int)Math.Min(o.Count - 1, Math.Floor(q * o.Count))].ToString("0"); }
        static string Pct(int n, int of) => of == 0 ? "-" : (100.0 * n / of).ToString("0.0") + " % (" + n + " of " + of + ")";
        static string Move(int d) => d > 0 ? "up " + d : d < 0 ? "down " + (-d) : "-";

        static string Sha256(string path) { using (var s = SHA256.Create()) return BitConverter.ToString(s.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant(); }

        static string LinkTime(string path)
        {
            var b = File.ReadAllBytes(path); int pe = BitConverter.ToInt32(b, 0x3c);
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(BitConverter.ToUInt32(b, pe + 8)).ToString("yyyy-MM-dd HH:mm:ss");
        }
    }
}
