# Weight NHL and NBA hype toward Stakes

On 2026-11-14 the owner asked for a review of that day's home page. All eight upcoming games scored between 41 and 51, so all of them were in **Good games**. Minnesota–Boston, a November NHL game between two teams with 95% and 72% playoff odds, scored 51. That was the highest score of the day. Texas–LSU was a ranked college game in which both teams were still in the CFP race, and it scored 49. The other two NHL games scored 47 and 44, and the two NBA games scored 42 and 41.

Early in an 82- or 84-game season, the [estimated playoff swing](0004-estimated-playoff-swing-stakes.md) keeps every game's Stakes below about 7. The NHL composite gave Quality the same weight as Stakes (0.425). Two top-10 teams with an even line therefore scored about 50 from Quality and [quality-scaled Closeness](0006-quality-scaled-pro-closeness.md), with almost nothing at stake. The NBA had the same structure, with weights of 0.30, 0.40 and 0.30.

The owner told the agent to change the formula until it matched the review. The review made three judgments:

- Every college game with CFP consequences on the slate should rank above every early-season pro game.
- The pro games should mostly fall to **More games**. A game between two top-seven teams can stay in **Good games**.
- No game on this slate belongs in **Best games**.

The weights for these two leagues are now:

| League | Stakes | Quality | Closeness |
| --- | --- | --- | --- |
| NHL | 0.55 (was 0.425) | 0.30 (was 0.425) | 0.15 |
| NBA | 0.45 (was 0.30) | 0.25 (was 0.40) | 0.30 |

Quality, Closeness, Stakes, the NFL, MLB and college football are unchanged.

## Effect on the slate

The inputs are constructed to reproduce each displayed total under the old weights to within one point. They are not provider observations. The FPI values, ranks and lines are inferred, and the playoff probabilities are rounded.

| Game | Displayed | Old weights | New weights | Section |
| --- | --- | --- | --- | --- |
| Texas–LSU (CFB) | 49 | 48.9 | 48.9 | Good |
| Wake Forest–SMU (CFB) | 45 | 45.1 | 45.1 | Good |
| Tennessee–Texas A&M (CFB) | 44 | 44.0 | 44.0 | Good |
| Minnesota–Boston (NHL) | 51 | 50.9 | 40.7 | Good |
| Colorado–Montreal (NHL) | 47 | 47.2 | 37.9 | More |
| New Jersey–Washington (NHL) | 44 | 43.9 | 35.2 | More |
| Dallas–Cleveland (NBA) | 42 | 42.1 | 34.1 | More |
| Detroit–Miami (NBA) | 41 | 41.1 | 32.8 | More |

The weight change does not lower games that already have high Stakes. The approved calibrations in `ProHypeTests` change as follows:

| Calibration | Old | New |
| --- | --- | --- |
| NHL opener, #1 v #2 | 58 | 47 |
| NHL opener, #14 v #15 | 34 | 28 |
| NHL finale, #16 v #17, both at 50% playoff odds | 47 | 47 |
| Stanley Cup Final elimination game, #1 v #2 | 98 | 99 |

With the New Orleans–Clippers inputs from ADR 0006, the NBA score falls from 17 to 14.

## Alternatives rejected

Squaring each team's rank strength fixed the slate, but it also lowered games between mid-table teams that have high Stakes. The #16 v #17 finale fell from 47 to 33, and that is the kind of game that decides a playoff place. Making college Stakes rise linearly through the season, instead of by the current exponential curve, lifted the three college games by 2–4 points. That change was not needed once the pro scores fell, so college football is unchanged.

## Regression slate and formula version

`apps/backend.tests/HypeSlateTests.cs` keeps this slate as a reviewed case. Each game must land in its reviewed home-page section, and each game must outrank every game in a later ranked group. With the old weights, Minnesota–Boston outranks Texas–LSU and Colorado–Montreal stays in **Good games**, so the old formula fails the test. [Hype slate tests](../testing/hype-slates.md) describes how to add another reviewed slate.

NBA, NHL and MLB regular-season snapshots move to `FormulaVersion = 6`, so version 5 scores are not reused. These three leagues share one version, so MLB snapshots are also discarded, although the MLB formula is unchanged. The NFL stays on version 6. Postseason and preseason snapshots have no formula version, so a saved snapshot from those phases keeps its earlier total until new evidence replaces it.
