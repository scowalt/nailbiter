# Estimate playoff swing for NBA, NHL and MLB regular-season Stakes

On 2026-11-05 the owner observed that long-season leagues' regular-season Stakes looked inflated relative to the NFL, and approved putting NBA, NHL and MLB on the NFL's model: team Stakes is `80 × playoff swing` and game Stakes is the larger team's value. The NFL measured a published win/loss conditional forecast, while the other leagues used `80 × urgency × (1 − |2p − 1|)`, whose urgency depended only on the fraction of the season played. One game moves playoff chances much less in an 82- or 162-game season than in a 17-game one, so from mid-season on the old formula scored long-season games about two to three times higher than their actual swing in a synthetic simulation.

No approved free source publishes next-game conditional forecasts for those leagues: NHL PlayoffStatus is rejected (ADR 0002), MoneyPuck conditional rows are unvalidated and NBA/MLB sources are unresearched. The owner therefore chose an **estimated playoff swing** computed from existing evidence — the selected playoff probability `p` and verified games remaining `R` — rather than waiting for a new source:

```text
estimated swing = φ(Φ⁻¹(p)) / sqrt(0.55² × (R − 1) + 0.7²)
```

The constants come from the synthetic calibration in `scripts/stakes-swing/calibrate.py`, not observed provider or production data. This deliberately departs from the earlier research finding that unconditional probability cannot identify a conditional forecast: the estimate is an in-house Stakes heuristic, as the urgency formula it replaces was, and must never be displayed or stored as P(playoffs | win/loss) evidence. NFL keeps the published conditional forecast and never falls back to the estimate. Trusted finals, missing-evidence handling, verified progress, composite weights and the postseason/play-in formulas are unchanged. Regular-season snapshots move to `FormulaVersion = 3`, so version 2 scores are not reused.
