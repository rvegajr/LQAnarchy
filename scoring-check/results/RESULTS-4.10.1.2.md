# ScoringCheck results

Every number below was computed by the LQX assemblies listed here, loaded unmodified.

| Assembly | Version | PE link time (UTC) | SHA-256 |
|---|---|---|---|
| Console.exe | 4.10.1.2 | 2019-04-11 15:41:46 | `bb3b6d65c116a7b41012f9e7a078336172ba46ef4b56cc5a383362d1f0281999` |
| Versent.LQX.Core.dll | 4.10.1.2 | 2019-04-11 15:41:16 | `2bc170b3cd3772f7ad1027e8726837340830aebaa35bda6a885f8fa631d5443d` |

Runtime: .NET Framework 4.0.30319.42000, 32-bit process, Microsoft Windows NT 6.2.9200.0.
Run at 2026-10-16 03:15 UTC.

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

## 5. Shoulder tags during the game

Pack 1 tags pack 2 six times on the front and four times on the shoulder. Live replies carry shoulder tags in the phaser column, as real packs do.

| | Phaser column | Shoulder column | Pack 1 tag points | Pack 2 tag points |
|---|---|---|---|---|
| Live poll (Msg40 parser) | 4 | 0 | 100 | -42 |
| Final upload (Msg53 parser) | 0 | 4 | 100 | -42 |

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

## 8. A full 8v8 game (red 1-8 vs green 9-16), seed 2026

Model (see THE-FAMOUS-LQ-DOWNLOAD.md for the sources): 15-minute Team game; each player fires 200-900 shots with 3-13 % accuracy; each tag picks a random opponent and a sensor in the site's measured shares (rear 20.1, front 35.5, phaser 31.8, shoulder 12.7 %). Live loop as GameEngine runs it: leaderboard broadcast when 30 s have passed (4.55 s on air), otherwise poll the next pack (55.6 % of polls answered; 1.22 s per good poll, 2.71 s per failed one). Download: 75.8 % of requests answered first time; 2.80 s per good request, 3.14 s per failed one.

Live phase: 415 polls (236 answered), 28 broadcasts. Download: 23 requests, 86 s.

| Pack | Team | Shots | Tags made | Tags taken | Accuracy % | Gun at the whistle (score, rank) | Score card (score, rank) | Rank change |
|---|---|---|---|---|---|---|---|---|
| 2 | red | 843 | 109 | 43 | 12.9 | 1014, 1 | 1016, 1 | - |
| 4 | red | 668 | 77 | 31 | 11.5 | 747, 2 | 759, 2 | - |
| 10 | green | 565 | 62 | 52 | 11.0 | 515, 3 | 529, 3 | - |
| 12 | green | 477 | 53 | 43 | 11.1 | 475, 4 | 467, 4 | - |
| 5 | red | 639 | 42 | 26 | 6.6 | 427, 5 | 387, 5 | - |
| 7 | red | 486 | 39 | 39 | 8.0 | 313, 8 | 323, 6 | up 2 |
| 6 | red | 836 | 41 | 34 | 4.9 | 353, 6 | 322, 7 | down 1 |
| 13 | green | 381 | 35 | 34 | 9.2 | 316, 8 | 314, 8 | - |
| 15 | green | 308 | 32 | 43 | 10.4 | 207, 10 | 242, 9 | up 1 |
| 3 | red | 842 | 32 | 32 | 3.8 | 296, 9 | 231, 10 | down 1 |
| 11 | green | 208 | 25 | 52 | 12.0 | 124, 12 | 149, 11 | up 1 |
| 1 | red | 312 | 18 | 30 | 5.8 | 108, 13 | 128, 12 | up 1 |
| 14 | green | 773 | 24 | 39 | 3.1 | 125, 11 | 120, 13 | down 2 |
| 8 | red | 287 | 17 | 31 | 5.9 | 75, 15 | 113, 14 | up 1 |
| 9 | green | 557 | 23 | 52 | 4.1 | 88, 14 | 91, 15 | down 1 |
| 16 | green | 299 | 12 | 60 | 4.0 | -76, 16 | 20, 16 | - |

| Team | Score monitor at the whistle | Score card (sum of player scores) |
|---|---|---|
| Red | 3357 | 3279 |
| Green | 1820 | 1932 |

### Across the game: what the guns showed vs the real standings

At each leaderboard broadcast. "Real" = what the score card would say if every pack's hits were known at that moment (shots pro rata, before the end-of-game boost). The score monitor shows LQX's own running totals, built only from the polls that got through.

| Time | Gun leader (pack, score shown) | Real leader | Score monitor red vs green | Real red vs green |
|---|---|---|---|---|
| 0:31 | 1 (0) | 12 (150) | 0 vs 0 | 573 vs 661 |
| 1:01 | 12 (62) | 12 (167) | 88 vs 187 | 597 vs 835 |
| 1:32 | 7 (83) | 4 (180) | 264 vs 200 | 934 vs 767 |
| 2:03 | 7 (96) | 2 (200) | 290 vs 192 | 1035 vs 762 |
| 2:33 | 2 (161) | 4 (232) | 545 vs 296 | 1073 vs 909 |
| 3:05 | 2 (154) | 4 (232) | 635 vs 491 | 1075 vs 995 |
| 3:37 | 2 (167) | 4 (248) | 667 vs 589 | 1116 vs 1068 |
| 4:08 | 2 (167) | 4 (258) | 703 vs 593 | 1120 vs 1130 |
| 4:39 | 2 (232) | 10 (277) | 937 vs 633 | 1207 vs 1148 |
| 5:10 | 10 (243) | 2 (339) | 913 vs 760 | 1334 vs 1139 |
| 5:40 | 2 (329) | 2 (356) | 1120 vs 866 | 1412 vs 1174 |
| 6:10 | 2 (342) | 2 (393) | 1148 vs 930 | 1419 vs 1228 |
| 6:41 | 2 (368) | 2 (455) | 1249 vs 913 | 1527 vs 1250 |
| 7:14 | 2 (401) | 2 (476) | 1360 vs 938 | 1583 vs 1288 |
| 7:45 | 2 (497) | 2 (527) | 1623 vs 1011 | 1767 vs 1334 |
| 8:16 | 2 (547) | 2 (567) | 1837 vs 1037 | 1930 vs 1349 |
| 8:48 | 2 (557) | 2 (624) | 1905 vs 1084 | 2110 vs 1348 |
| 9:18 | 2 (634) | 2 (654) | 2114 vs 1167 | 2212 vs 1353 |
| 9:48 | 2 (680) | 2 (698) | 2191 vs 1290 | 2293 vs 1441 |
| 10:19 | 2 (700) | 2 (745) | 2215 vs 1302 | 2458 vs 1450 |
| 10:51 | 2 (722) | 2 (749) | 2357 vs 1450 | 2493 vs 1519 |
| 11:22 | 2 (762) | 2 (809) | 2606 vs 1475 | 2676 vs 1537 |
| 11:53 | 2 (784) | 2 (839) | 2648 vs 1565 | 2719 vs 1604 |
| 12:25 | 2 (794) | 2 (890) | 2671 vs 1612 | 2827 vs 1633 |
| 12:57 | 2 (904) | 2 (910) | 3029 vs 1515 | 2924 vs 1687 |
| 13:27 | 2 (924) | 2 (932) | 3112 vs 1592 | 3064 vs 1712 |
| 13:58 | 2 (954) | 2 (952) | 3226 vs 1549 | 3066 vs 1763 |
| 14:28 | 2 (1014) | 2 (976) | 3333 vs 1677 | 3194 vs 1782 |
| Score card | 2 (1016) | | | 3279 vs 1932 |

### The download, request by request

Time after the game ends. 23 requests, 3 STOP re-broadcasts, last score in at 86 s.

| Time | Sent | Result |
|---|---|---|
| 0:06 | 0x54 to pack 1 | no answer in 3 s (try 1) |
| 0:09 | 0x54 to pack 2 | answered, score in (1 of 16) |
| 0:12 | 0x54 to pack 3 | answered, score in (2 of 16) |
| 0:14 | 0x54 to pack 4 | no answer in 3 s (try 1) |
| 0:18 | 0x54 to pack 5 | answered, score in (3 of 16) |
| 0:20 | 0x54 to pack 6 | answered, score in (4 of 16) |
| 0:23 | 0x54 to pack 7 | answered, score in (5 of 16) |
| 0:26 | 0x54 to pack 8 | answered, score in (6 of 16) |
| 0:29 | 0x54 to pack 9 | answered, score in (7 of 16) |
| 0:32 | 0x54 to pack 10 | answered, score in (8 of 16) |
| 0:34 | 0x54 to pack 11 | answered, score in (9 of 16) |
| 0:37 | 0x54 to pack 12 | answered, score in (10 of 16) |
| 0:40 | 0x54 to pack 13 | no answer in 3 s (try 1) |
| 0:43 | 0x54 to pack 14 | answered, score in (11 of 16) |
| 0:46 | 0x54 to pack 15 | no answer in 3 s (try 1) |
| 0:49 | 0x54 to pack 16 | answered, score in (12 of 16) |
| 0:52 | STOP x3 again (pack 1 has now failed 1 times) |  |
| 0:56 | 0x54 to pack 1 | answered, score in (13 of 16) |
| 0:59 | 0x54 to pack 4 | no answer in 3 s (try 2) |
| 1:02 | 0x54 to pack 13 | no answer in 3 s (try 2) |
| 1:05 | 0x54 to pack 15 | answered, score in (14 of 16) |
| 1:08 | STOP x3 again (pack 4 has now failed 2 times) |  |
| 1:12 | 0x54 to pack 4 | answered, score in (15 of 16) |
| 1:15 | 0x54 to pack 13 | no answer in 3 s (try 3) |
| 1:18 | STOP x3 again (pack 13 has now failed 3 times) |  |
| 1:22 | 0x54 to pack 13 | answered, score in (16 of 16) |

### The same game when downloads fail

| Case | Pack 10 (score, rank) | Top 3 on the card | Red | Green |
|---|---|---|---|---|
| All downloads arrive | 529, 3 | 2 (1016), 4 (759), 10 (529) | 3279 | 1932 |
| Pack 10's own download fails | 537, 3 | 2 (1006), 4 (759), 10 (537) | 3256 | 1940 |
| Packs 16 and 9 (most-tagged greens) have no final score | 529, 3 | 2 (1006), 4 (759), 10 (529) | 3230 | 1955 |

## 9. A thousand 8v8 games

Same model as section 8, 1,000 games (seeds from Random(4800)). Failures: each pack independently has no final score with probability 1.84 %, the site's rate.

| Measure | Result |
|---|---|
| Gun's #1 at the whistle is not #1 on the card | 9.4 % (94 of 1000) of games |
| Winning team on the score monitor at the whistle loses on the card | 4.5 % (45 of 1000) of games |
| Average rank change from gun to card | 0.6 places |
| Players who move 3 or more places at the download | 2.4 % (384 of 16000) |
| Games where at least one pack has no final score | 25.5 % (255 of 1000) |
| ... of those, the failure changes who is #1 | 2.0 % (5 of 255) |
| ... of those, the failure flips the winning team | 0.4 % (1 of 255) |

How long the download takes (16 packs, every pack answers eventually):

| | Median | 90th percentile | Longest |
|---|---|---|---|
| Game end to last score | 75 s | 91 s | 120 s |
| Waiting for the last 3 packs | 16 s | 27 s | 52 s |
| STOP re-broadcasts | 2 | 3 | 7 |
