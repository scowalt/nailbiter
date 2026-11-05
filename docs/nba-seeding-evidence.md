# Verified NBA seeding with unpublished matchups

Since the owner's 2026-11-05 decision, regular-season Stakes uses qualification only; seeding certificates are retained compatibility metadata and do not change scores or gate availability. Progress remains independently required: a fresh, season-labelled record can verify completed progress from an 80/81-event schedule without pretending that missing games have been published. The denominator stays 82. The proof below can establish positioning metadata without naming, scheduling or storing unpublished games.

## Supported rules and source facts

The new proof is limited to NBA season 2027, regular season. The NBA's [2026–27 schedule announcement](https://www.nba.com/news/2026-27-nba-regular-season-schedule) specifies 80 initially assigned games of 82, with two additional games determined by NBA Cup Group Play. The [2026 Cup rules](https://www.nba.com/news/nba-cup-101) make Group Play and the quarterfinals/semifinals count toward regular-season standings, but not the championship. Cup qualification, matchup allocation and seeding use Cup results/scoring, the previous season's record and possibly a draw—not current ordinary regular-season results.

Require all 30 distinct team schedules, each with an 82-game denominator and 80–82 actual events. Every known contest must have exactly two participant-owned copies agreeing on identity, home/away roles, date, result and Cup classification. Recompute completed wins/counts/IDs; preserve overtime wins as one win, reject ties and non-basketball final classifications. Every unfinished known contest must be future, explicitly scheduled, period zero and scoreless. Missing teams, rejected progress, unmatched copies and live/postponed/suspended/ambiguous games cannot be excused by unknown slots.

The target must have a standard `STD` classification and an explicit empty notes array in the scoreboard and both team schedules, not merely missing Cup metadata. Also exclude the official 2026 Eastern-date Cup windows: Fridays October 30–November 27, November 24/25, and conservatively all December 4–11 knockout/final dates. Unrecognized Cup stages and the noncounting championship cannot reduce the unknown regular-season capacity. Unsupported cases receive no certificate from this new path; existing full-schedule proofs and other sports are unchanged.

## A proof for every missing-game outcome

For team `i`, let `d[i] = 82 - known events`, between zero and two. Do not supply missing opponents, dates, scores, game IDs or probabilities. Their eventual contribution to any team's regular-season wins lies between zero and `d[i]`.

Choose **one fixed baseline** assigning legal hypothetical winners to all known unfinished games except the target, including the same fixed Cup results. The sufficient search tries home/cyclic outsider results and each allocation of the other known target head-to-head games. Target teams initially win their other known games.

For **every** combination of target unknown-slot wins `x = 0..d[home]` and `y = 0..d[away]`:

1. Add `x/y` to that same baseline. There are at most nine cases, including combinations that may not arise under the eventual Cup fixtures.
2. Balance the target totals using distinct, source-explicit ordinary non-head-to-head games starting January 1, 2027 UTC or later: change hypothetical wins into losses to outsiders. Never alter an observed result or a Cup result.
3. Require the target totals before the target to be exactly equal, and every outsider's known completed-season total **plus all of its unknown slots** to be strictly smaller.

Changing the target winner then exchanges strict first/second positioning or home-court advantage, with both teams qualifying, for any actual unpublished fixtures/results. No standings tiebreaker is assumed. Cup results are fixed before examining the unknown-slot cases, and only later ordinary games are adjusted, so these assignments cannot alter the Cup fixtures they depend on. Permitting a different Cup baseline for each unknown case would not be the same proof.

Every case must succeed for the same baseline; a witness for only one plausible completion is insufficient. If a required case lacks legal balancing games or a strict margin, keep both seeding consequences unknown. The bounded search is sufficient, not exhaustive.

## Scoring, persistence and verification

The proof only supplies seeding-consequence metadata, which the qualification-only formula ignores. The estimated playoff swing, weights, exact-unrounded Stakes/composite requirements, qualification selection/liquidity, ranking and game-winner sources, source clocks, collection budgets and source activation are unchanged. Rejected qualification still prevents hype. No score is derived from assumed unknown-game prices or a rounded-away interval.

Complete tuples still pass through normal automatic capture, authoritative identity/provenance, durable invalidation, retention and exclusive scheduled/confirmed-start cutoff. No manual snapshots, database repair, provider refresh or migration is needed.

The generator builds 1,200 fictional known contests, 80 per team, in `apps/backend.tests/Fixtures/ProHype/nba-seeding-contests.csv`. No missing matches are added to that baseline. Independently constructed test completions exercise all nine target capacity combinations; maintained controls cover actual additional published games, early records, unsafe observations, Cup classification/dates, insufficient balancing, overtime/tie handling and inability to qualify. These are synthetic graphs/calendars, not real schedule captures or live-provider conformance evidence.

Registered BPI/qualification/competition collection through intercepted HTTP, PostgreSQL, the real scorer, automatic capture and home/league/detail verifies the supported path. The current qualification-only page oracle uses 7/8 completed games, qualification probability `0.9`, ranks 4/9 and home game-winner probability `3/5`: Stakes `2.955106581196874` (74 games remaining), Quality `11395/144`, Closeness `80`, total `56.53930975213684`. Retention preserves the new-formula tuple/source/clock across aging and restart; rejected qualification remains unavailable. A verified regular-season Cup Group Play target can now score without a seeding certificate; the Cup exclusions above limit proof metadata, not the qualification contribution. Live recovery requires a separately approved rollout and ordinary collection, not a forced fetch or a retrospective score.
