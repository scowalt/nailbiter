# Estimated playoff swing calibration

Reproduces the `GameSpreadInWins = 0.55` and `CutLineSpreadInWins = 0.7` constants in `apps/backend.shared/Scoring/PlayoffSwingEstimate.cs` ([ADR 0004](../../docs/adr/0004-estimated-playoff-swing-stakes.md)).

```sh
uv run --no-project --with 'numpy>=2' python -I scripts/stakes-swing/calibrate.py
```

The script simulates synthetic NFL-, NBA-, NHL- and MLB-sized seasons. Each `LEAGUES` entry is teams, scheduled games, team-strength spread in logits and qualifiers per conference. At several points in each season it measures every bubble team's (3–97% playoff probability) true next-game swing, P(playoffs | win) − P(playoffs | loss), by Monte Carlo. It then grid-searches the constants of

```text
estimated swing = φ(Φ⁻¹(p)) / sqrt(game² × (remaining − 1) + cut_line²)
```

pooled across leagues, where `remaining` includes the next game. The model treats a team's final margin over the playoff cut line as normally distributed: one game moves that margin by one win, and the cut line and competitors keep some uncertainty even on the final day. `PlayoffSwingEstimate` computes Φ⁻¹ with Wichura's Algorithm AS 241 (PPND16).

Seasons use random pairings, logistic game odds, two conferences and top-k qualification. There are no ties, overtime points, divisions, wild cards, tiebreakers or head-to-head rival effects, so the constants are a calibrated heuristic, not provider-validated forecasts. Expected output:

```text
fit: GameSpreadInWins = 0.55, CutLineSpreadInWins = 0.7
nfl: n=308 true Stakes mean  17.3, estimated  16.3, r=0.98, mean absolute error 1.7
nba: n=170 true Stakes mean   6.9, estimated   7.4, r=0.96, mean absolute error 1.1
nhl: n=311 true Stakes mean   8.0, estimated   8.1, r=0.98, mean absolute error 0.9
mlb: n=263 true Stakes mean   5.4, estimated   5.7, r=0.93, mean absolute error 1.0
```
