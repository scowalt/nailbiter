# Independently synthetic NFL conditional forecasts

All files here are authored by `scripts/test-fixtures/generate.py`, without downloading, reading, reducing or transcribing provider responses. They are original MIT test inputs, not PlayoffStatus/ESPN content or records of real games. The historical captured HTML and transcribed schedule remain private, inert evidence and are not a build/test dependency.

## Construction

- Generate a 32-team circle-method round robin, choose 17 distinct rounds and place DET/GB in synthetic week 7. Each team has 17 contests, each directed matchup is unique, and there are 272 contests. An eighteenth table is intentionally empty; this is a structural contract fixture, not a plausible reconstruction of an actual NFL calendar or bye policy.
- Dates start at 13:00 Eastern on 2026-09-06 and advance by week; source HTML explicitly uses ET, later synthetic weeks exercise TBD handling, and the control CSV uses independently chosen exact instants. Scores in the first six rounds are deterministic integer formulas. No actual league results or schedule rows are copied.
- The target is DET at home to GB, week 7, at `2026-10-18T17:00:00Z`; both have six constructed completed games. Team W/L/T and win/loss next-game row transitions are derived from those invented results.
- Page structure is minimal hand-authored compatibility markup for the implemented parser. `Synthetic publication batch A`, test HTTP dates and publication clocks are explicit controls, not observed publisher metadata.
- No-Playoffs inputs are GB 18/32/43 and DET 26/36/46 for win/current/loss. Thus win/loss playoff probabilities are GB 0.82/0.57 and DET 0.74/0.54, giving independent qualification contributions **20 / 16**. These values were chosen for regression coverage, not fitted to historical observations.

The main [fixture manifest](../../../../tests/fixtures/manifest.json) records exact hashes. `tests/fixtures/test_generation.py` verifies deterministic inventory, 272 unique matchups, 32×17 participation, target identity and independent rational leverage/weighted-score arithmetic. The .NET suite checks the reviewed fixture bytes and exercises parser/scorer/page behavior, corruption and missing-data variants. Regeneration must preserve the established negative paths, not silently make string mutations ineffective.

In the controlled page case, separate invented Kalshi outlook is 0.62 for both teams, rank inputs are 1/2, and game-winner odds are 60/40. Exact Stakes is 20 and weighted hype rounds to 59; neither is a production score claim. Source reuse permission, current provider schema, recurring access, independent live ESPN agreement and sustained coverage are **not established by synthetic acceptance**. No test contacts a provider.
