# NFL playoff odds

NFL playoff qualification probabilities come from Kalshi alone. NBA, NHL, and MLB now use the same source and selection policy.
See [Playoff qualification probabilities](playoff-odds.md) for season matching, price rules, refreshes, final outcomes, and tests.
Power rankings and championship probabilities remain separate inputs.

The continuing [NFL conditional-leverage proposal](nfl-playoff-leverage.md) uses separately verified PlayoffStatus win/loss scenarios for regular-season qualification Stakes, not for Kalshi's unconditional Playoff outlook or trusted final outcomes. This unmerged source proposal does not deploy or activate collection, change other leagues or authorize NHL PlayoffStatus. Missing conditional evidence cannot be replaced with an unconditional estimate.

## NFL season verification

The 2026 NFL season uses Kalshi event `KXNFLPLAYOFF-27`. Its subtitle must identify `2026-27` or `2026-2027`.
Game pages and hype scores use the game's `SeasonYear`. Refreshes use the prior season in January and February, then the upcoming season from March.

Migration `025_TeamRatingSourceSeason.sql` adds nullable `source_season` metadata.
It leaves existing records unverified because their fetch date cannot prove their source season. The migration is safe to run twice.
NFL readers ignore unverified PowerRankingsGuru rows, including rows previously mislabeled as the current season.

A successful PowerRankingsGuru refresh now requires the source page heading to match the expected season in **all four pro leagues** before it sets `source_season`. ESPN FPI/BPI imports independently verify the response's requested season and named rank column. Fetching an old-season page does not relabel it.

Pro-league rank readers reject observations at 48 hours, future timestamps, invalid ranks and unverified league-season formats. Hype quality selects a complete same-source pair, preferring ESPN FPI/BPI over PowerRankingsGuru, and does not normalize numerical ratings. Successful unusable observations clear eligibility; request failures preserve the old timestamp. See the [pro hype runbook](pro-hype-evidence.md), including seeding/schedule coverage gaps and migration 028. Kalshi qualification source and final-outcome rules remain unchanged.
