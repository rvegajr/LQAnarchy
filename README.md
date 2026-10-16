# LQ Anarchy: how Laser Quest's LQX software scores a game

Why the rank on the back of your phaser comes in batches, and why the final download can reshuffle the scoreboard.
Checked against LQX's own compiled code, not guessed.

## For players: the short version

- **Your gun doesn't work out your score. The PC does.** A pack only records who tagged *it*. Your tags live in other
  players' packs, so only the PC, which hears from every pack, can add up your score.
- **It comes in batches.** During a game the PC asks one pack at a time for its hits and sends that pack its current
  rank and score. Every 30 seconds or so it also broadcasts the whole leaderboard to every gun at once.
- **The gun and the score card use different maths for accuracy.** During the game the PC doesn't know how many shots
  you've fired, so it assumes **333 shots for everyone**. The score card uses your real shot count. Accurate players
  climb at the final download; players who spray drop.
- **Accuracy is worth up to 100 points**, as much as 10 tags. Each percentage point of accuracy is worth 10 points,
  so **10 % accuracy already earns the full 100**.
- **A missed radio poll costs nothing.** Each reply carries the pack's running totals, so the next one catches up.
- **A failed final download does cost.** If the PC can't download a pack after 25 tries, it scores that player from
  the last mid-game poll: 333 shots (most of the accuracy bonus gone), and no tags since that poll. That also costs
  everyone who tagged that player late. Sometimes another player's pack really is playing against you.
- **Shoulder tags only arrive with the final download**, so they can move the scoreboard at the end too.

## The code checked

The numbers on this page come from running Laser Quest's PC software, **LQX by Versent Corporation**, unmodified.

| File | Version | Compiled (PE link time, UTC) | SHA-256 |
|---|---|---|---|
| `Console.exe` | 4.10.1.2 | 2019-04-11 15:41:46 | `bb3b6d65c116a7b41012f9e7a078336172ba46ef4b56cc5a383362d1f0281999` |
| `Versent.LQX.Core.dll` | 4.10.1.2 | 2019-04-11 15:41:16 | `2bc170b3cd3772f7ad1027e8726837340830aebaa35bda6a885f8fa631d5443d` |
| `Console.exe` | 4.9.265.1 | 2017-09-22 21:20:24 | `4314e5af128f1dc64002977f7fc8be55a8b6c9365688ce551b9255a3182d9fe7` |
| `Versent.LQX.Core.dll` | 4.9.265.1 | 2017-09-22 21:20:15 | `84c70d31a2051c8eb0c035fc45337243160c4c358c662385712e82d91587be1e` |

- The 4.10.1.2 files come from the LQX 4.10.1.2 installer (`Setup.msi`, 2019-04-11) found on a site's game PC. The
  4.9.265.1 files come from that site's older installer.
- The scoring code is the same in both builds. ILSpy 11.1 decompiles `ScoreEngine` and `GameEngine` to identical C#,
  and `PackScore` and the two radio-reply parsers to identical IL.
- Code excerpts below are ILSpy 11.1 output from the 4.10.1.2 files. Names like `num2` are the decompiler's; the
  vendor's own variable names aren't in the binaries.
- Validated 2026-10-16 on Windows 11 Pro 23H2, .NET Framework 4.8 (CLR 4.0.30319.42000), 32-bit process. Full output:
  [`scoring-check/results/RESULTS-4.10.1.2.md`](scoring-check/results/RESULTS-4.10.1.2.md) and
  [`RESULTS-4.9.265.1.md`](scoring-check/results/RESULTS-4.9.265.1.md). The two are identical apart from the header.

## 1. Where the batches come from

LQX's game engine runs this every pass while a game is running (`LqxConsole.GamePlay.GameEngine`, state `Running`):

```csharp
case GameEngineStates.Running:
    if (DateTime.Now > _lastScoreBroadcast.AddSeconds(30.0))
    {
        _serialPortManager.RealtimeScoreBroadcast(_scoreEngine.GetRealTimeScoreBroadcastData());
        _lastScoreBroadcast = DateTime.Now;
        break;
    }
    ...
    if (LiveScorePacks.MoveNext())
    {
        DeviceViewModel current = LiveScorePacks.Current;
        if (current != null)
        {
            CollectLiveScore(current);
        }
    }
```

So each pass either broadcasts the leaderboard (if 30 s have passed) or polls the next pack in the rotation. Polling a
pack sends it its current rank and score, which is what its gun shows, and takes back its hits:

```csharp
private void CollectLiveScore(DeviceViewModel deviceViewModel)
{
    KeyValuePair<int, int> scoreandRank = _scoreEngine.GetScoreandRank(deviceViewModel.DeviceId);
    PackUploadData packUploadData = _serialPortManager.RealtimeScoreToPack(deviceViewModel.DeviceId, scoreandRank.Key, scoreandRank.Value);
    ...
    packUploadData.ShotsUsed = 333;
    _scoreEngine.ProcessScoreReply(deviceViewModel.DeviceId, playerAssociation.UniquePlayerName, playerAssociation.Player.FppCode, playerAssociation.Player.Team, packUploadData);
```

The `333` is a literal constant in the compiled code (IL of `GameEngine.CollectLiveScore`, both builds):

```
IL_00a2: ldc.i4 333
IL_00a7: stfld int32 [Versent.LQX.Core]Core.Infrastructure.Comm.PackUploadData::ShotsUsed
```

The rank and score sent to a pack are read *before* that pack's new hits are processed, so a gun is always showing
the result of the previous round of replies.

On a real site (the game PC's radio logs from one site's last week of games in March 2020) this worked out to one
poll every 1.46 s, each pack polled about every 34 s, and a leaderboard broadcast every 35.5 s.

## 2. The score formula

`LqxConsole.GamePlay.ScoreEngine.CalculateScore`, the core of it:

```csharp
foreach (PackScore item2 in list)            // every other pack
{
    ...
    Hits hits = item2.HitBy(thisPackId);     // tags I made, stored in THEIR pack
    num += hits.Front + hits.Rear + hits.Gun + hits.Shoulder;
    int num5 = hits.Front * YhFrontPoints + hits.Rear * YhRearPoints + hits.Gun * YhLaserPoints + hits.Shoulder * YhShoulderPoints;
    Hits hits2 = item.HitBy(deviceId);       // tags I took, stored in MY pack
    int defensePoints = hits2.Front * HyFrontPoints + hits2.Rear * HyRearPoints + hits2.Gun * HyLaserPoints + hits2.Shoulder * HyShoulderPoints;
    if (_gameType == GameType.AdvancedTeam && team2 == team)
    {
        hits.AttackPoints = -num5;           // tagging a teammate costs you...
        hits2.DefensePoints = 0;             // ...and costs them nothing
    }
    ...
}
...
double num9 = ((item.ShotsUsed <= 0) ? 0.0 : ((double)num * 100.0 / (double)item.ShotsUsed));
int num10 = (int)(num9 * (double)_bizGamePointsProfile.HitPerMissBonusPointsPercent);
if (num10 > _bizGamePointsProfile.HitPerMissMaxBonusPoints)
{
    num10 = _bizGamePointsProfile.HitPerMissMaxBonusPoints;
}
...
item.Score = num2 - num3 + num8 + num10 - num11;
```

Score = tag points made − tag points taken + base points + accuracy bonus − shot penalty. (`_bizGamePointsProfile.`
is trimmed from the first four lines here.)

The site's points profile (its database, `game_points_profile` row 1, the only profile any game used):

| | Front | Rear | Phaser | Shoulder |
|---|---|---|---|---|
| You tag someone | +10 | +10 | +10 | +10 |
| You get tagged | −5 | −4 | −3 | −3 |

Accuracy bonus setting 10, cap 100. Shot penalty threshold 9999 (never reached). End-of-game boost on. Base tags are
worth 50.

Despite its name, `HitPerMissBonusPointsPercent` (10) is used as a plain multiplier: accuracy 7.5 % × 10 = 75 points.

## 3. Ranks and the end-of-game boost

Ranks are dense: tied scores share a rank, and the next score takes the next number (1, 1, 2), from `OrderScores`:

```csharp
foreach (ScoreAndRank rank in _ranks)        // sorted by score, highest first
{
    int score = rank.Score;
    if (num2 != score)
    {
        num++;
    }
    num2 = score;
    rank.Rank = num;
```

The boost (`BoostScores(20, 100)`) runs only at the end (`CalculateScore(true)`). It applies when the lowest score is
under 20: it stretches every score below 100 so that the lowest becomes 20. It never touches scores of 100 or more, and
never shows on a gun during the game.

## 4. Why a missed poll doesn't matter, and a failed download does

Every reply replaces the pack's stored hits; nothing is added (`Core.Persistence.PackScore.UpdateHits`):

```csharp
for (int i = 0; i < 50; i++)
{
    _hitYouScore[i].Gun = (byte)(packUploadData.HitDataHhu[i] & 0x7F);
    _hitYouScore[i].Rear = (byte)(packUploadData.HitDataPackRear[i] & 0x7F);
    _hitYouScore[i].Front = (byte)(packUploadData.HitDataPackFront[i] & 0x7F);
    _hitYouScore[i].Shoulder = (byte)(packUploadData.HitDataShoulder[i] & 0x7F);
    ...
}
LivesUsed = packUploadData.LivesUsed;
ShotsUsed = packUploadData.ShotsUsed;
```

Packs report running totals, so the next good reply carries everything. But after the game, if a pack's final
download keeps failing, LQX gives up and keeps whatever it had from that pack's last live poll, including
`ShotsUsed = 333` (`GameEngine.CollectFinalScoreFromPacks`):

```csharp
if (packUploadData == null || packUploadData.PayloadPackId != deviceViewModel.DeviceId)
{
    DeviceManager.Fail(deviceViewModel);
    if (deviceViewModel.Failures < 25)
    {
        ReportGameEngineActivityProgress(MessageSeverity.Warning, $"Final Score not received from {(int)deviceViewModel.DeviceId} after {deviceViewModel.Failures} Trials.");
        return false;
    }
    ReportGameEngineActivityProgress(MessageSeverity.Warning, $"Couldn't get final score from pack {(int)deviceViewModel.DeviceId}.");
    DeviceManager.Finish(deviceViewModel);
    return false;
}
```

At that site, 1.8 % of final downloads failed across all packs. The worst pack failed 5.3 % of the time, so players'
suspicions about particular packs had something behind them.

## 5. Shoulder tags

The live-poll parser (`Msg40_RealTimeScoreResponse`) reads three sensor columns and never fills the shoulder column.
The final-upload parser (`Msg53_PackUploadData`) reads four:

```csharp
PackUploadData.HitDataShoulder = new int[50];
for (int i = 0; i < 50; i++)
{
    PackUploadData.HitDataPackRear[i] = payload[num + i * 3] & 0x7F;
    PackUploadData.HitDataPackFront[i] = payload[num + i * 3 + 1] & 0x7F;
    PackUploadData.HitDataHhu[i] = payload[num + i * 3 + 2] & 0x7F;
}
```

So shoulder tags only reach the score at the final download. One open question: the pack's firmware may fold
shoulder tags into the phaser column of its live reply. Nobody has captured a live reply with shoulder tags in it yet,
so this page only claims what the PC does with what it receives.

## 6. The checks, run through LQX's own code

[`scoring-check/`](scoring-check/) loads `Console.exe` and `Versent.LQX.Core.dll` and drives LQX's `ScoreEngine`
exactly as its `GameEngine` does:
- live poll: `Msg40` parser, then `ShotsUsed = 333`, then `ProcessScoreReply`;
- final upload: `Msg53` parser, then `ProcessScoreReply`;
- end: `CalculateScore(true)`.

Every score below was printed by LQX's code.

**Accuracy bonus.** Pack 1 makes 10 tags; only its shot count changes:

| Shots fired | Accuracy % | Bonus | Score |
|---|---|---|---|
| 400 | 2 (2.5) | 25 | 125 |
| 200 | 5 | 50 | 150 |
| 100 | 10 | 100 | 200 |
| 50 | 20 | 100 | 200 |

**Gun vs score card.** Pack 1 makes 12 tags with 50 shots. Pack 2 makes 16 tags with 500 shots. Pack 3 is their
target:

| Pack | On the gun (score, rank) | On the score card (score, rank) |
|---|---|---|
| 1 | 156, 2nd | **220, 1st** |
| 2 | **208, 1st** | 192, 2nd |
| 3 | −140, 3rd | 19, 3rd (boosted) |

**Failed downloads, same game:**

| Case | Pack 1 | Pack 2 | Pack 3 |
|---|---|---|---|
| All downloads arrive | 220, 1st | 192, 2nd | 19, 3rd |
| Pack 1's own download fails | 156, 2nd | **192, 1st** | 19, 3rd |
| Pack 3's (the target's) download fails, missing the last 4 + 6 tags | 180, 1st | 120, 2nd | 20, 3rd |

**Missed poll:** a pack reports 5 tags, its next poll is lost, and its final upload reports 12. The attacker is
credited with 12, not 5 and not 17.

**Shoulder tags:** 6 front and 4 shoulder tags. Live parser: 0 shoulder tags, 60 points. Final parser: 4 shoulder
tags, 100 points.

**Ties:** scores 100, 100, 90, −145 rank 1, 1, 2, 3.

**Team vs Advanced Team:** pack 1 (red) tags pack 2 (red, a teammate) and pack 3 (green) 5 times each:

| Game type | Pack 1 | Pack 2 | Pack 3 |
|---|---|---|---|
| Team | 100 | −25 | −25 |
| Advanced Team | 0 | 0 | −25 |

### Run it yourself

1. Copy `Console.exe`, `Versent.LQX.Core.dll` and `log4net.dll` from an LQX install (`C:\Program Files (x86)\LQX`)
   into `scoring-check/lqx/`, and compare their SHA-256 hashes with the table above.
2. Build with any .NET SDK 8 or later, on any OS: `dotnet build scoring-check -c Release`.
3. LQX's files are 32-bit Windows .NET Framework assemblies, so run the result on Windows:
   `scoring-check\bin\Release\net48\ScoringCheck.exe results.md`.

## Notes

- Not affiliated with Laser Quest or Versent Corporation. LQX is Versent's software. This repo contains no LQX
  files, only short excerpts of decompiled code quoted to document how scoring works.
- No player data is included. Site figures (poll timing, download failure rates) are aggregates from one site's logs
  and database.
- Corrections are welcome, especially from anyone who can capture a live reply containing shoulder tags.
