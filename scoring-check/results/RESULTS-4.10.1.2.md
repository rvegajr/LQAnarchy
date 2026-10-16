# ScoringCheck results

Every number below was computed by the LQX assemblies listed here, loaded unmodified.

| Assembly | Version | PE link time (UTC) | SHA-256 |
|---|---|---|---|
| Console.exe | 4.10.1.2 | 2019-04-11 15:41:46 | `bb3b6d65c116a7b41012f9e7a078336172ba46ef4b56cc5a383362d1f0281999` |
| Versent.LQX.Core.dll | 4.10.1.2 | 2019-04-11 15:41:16 | `2bc170b3cd3772f7ad1027e8726837340830aebaa35bda6a885f8fa631d5443d` |

Runtime: .NET Framework 4.0.30319.42000, 32-bit process, Microsoft Windows NT 6.2.9200.0.
Run at 2026-10-16 02:55 UTC.

## 1. Accuracy bonus = accuracy % x 10, capped at 100

Pack 1 tags pack 2 ten times on the front. Only pack 1's shot count changes.

| Shots fired | Accuracy % (HitRatioPercent) | Bonus (HitRatioPoints) | Score |
|---|---|---|---|
| 400 | 2 | 25 | 125 |
| 200 | 5 | 50 | 150 |
| 100 | 10 | 100 | 200 |
| 50 | 20 | 100 | 200 |
| 20 | 50 | 100 | 200 |

## 2. On the gun vs on the score card (live polls use 333 shots)

Pack 1 makes 12 tags with 50 shots. Pack 2 makes 16 tags with 500 shots. Pack 3 is the target (40 shots, no tags).

| Pack | Tags | Shots | Gun: bonus | Gun: score | Gun: rank | Broadcast (rank, score) | Card: bonus | Card: boost | Card: score | Card: rank |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 12 | 50 | 36 | 156 | 2 | 2, 156 | 100 | 0 | 220 | 1 |
| 2 | 16 | 500 | 48 | 208 | 1 | 1, 208 | 32 | 0 | 192 | 2 |
| 3 | 0 | 40 | 0 | -140 | 3 | 3, -140 | 0 | 159 | 19 | 3 |

## 3. A missed radio poll loses nothing

Pack 1 tags pack 2. Pack 2's first poll gets through (5 tags so far), its second is lost, then its final upload says 12.

| Step | Pack 1's tags counted (LivesTaken) |
|---|---|
| After the first poll | 5 |
| Second poll lost | 5 (nothing sent) |
| After the final upload | 12 (replaced, not added: 5 + 12 would be 17) |

## 4. When a final download fails

Same game as section 2. LQX gives up on a pack after 25 tries and keeps its last live data (GameEngine lines 915-933).

| Case | Pack 1 score, rank | Pack 2 score, rank | Pack 3 score, rank |
|---|---|---|---|
| All downloads arrive | 220, 1 | 192, 2 | 19, 3 |
| Pack 1's own download fails | 156, 2 | 192, 1 | 19, 3 |
| Pack 3's (the target's) download fails | 180, 1 | 120, 2 | 20, 3 |

## 5. Shoulder tags only count from the final upload

Pack 1 tags pack 2 six times on the front and four times on the shoulder.

| | Shoulder tags parsed | Pack 1's tag points |
|---|---|---|
| Live poll (Msg40 parser) | 0 | 60 |
| Final upload (Msg53 parser) | 4 | 100 |

## 6. Ties share a rank (1, 1, 2)

| Pack | Score | Rank |
|---|---|---|
| 1 | 100 | 1 |
| 2 | 100 | 1 |
| 3 | 90 | 2 |
| 4 | -145 | 3 |

## 7. Team vs Advanced Team: tagging your own team

Packs 1 and 2 are red, pack 3 green. Pack 1 tags pack 2 (teammate) 5 times and pack 3 5 times, all on the front.

| Game type | Pack 1 | Pack 2 | Pack 3 |
|---|---|---|---|
| Team | 100 | -25 | -25 |
| AdvancedTeam | 0 | 0 | -25 |
