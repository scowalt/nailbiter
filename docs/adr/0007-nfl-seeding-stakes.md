# Score NFL playoff position, not only qualification

On 2026-11-09 the owner reported that BUF @ MIN scored hype 40. Both teams were near-locked playoff teams, so the qualification swing was small and Stakes was 4, even though the game decided division titles and the No. 1 seed. Seeding had been removed from regular-season Stakes on 2026-11-05 because no integrated feed published seeding consequences. PlayoffStatus "What If" pages, already the NFL conditional source, publish Seed 1–7 probabilities next to No Playoffs for the win, current and loss scenarios, but the collector read only No Playoffs.

The owner approved scoring the NFL **playoff position swing** from those cells:

```text
position value V = P(playoffs) + .5 × P(seed ≤ 4) + .5 × P(seed 1)
team Stakes = min(100, 80 × (V | win − V | loss))
```

Seeds 5–7 weigh 1, seeds 2–4 (division titles) weigh 1.5 and the No. 1 seed weighs 2. The qualification term keeps its existing interval math. Each seeding term counts only its proven gain, `max(0, P(win).lower − P(loss).upper)`, so threshold cells such as `<1%` lower the score and never make it unavailable. Missing, unparseable or inconsistent seed cells drop only the seeding term: the forecast stays valid and Stakes is qualification-only. A clinched team keeps its seeding term when it has a valid forecast; eliminated teams contribute zero.

This is NFL-only: NBA, NHL and MLB have no published seed scenarios and stay qualification-only. NFL regular-season snapshots move to `FormulaVersion = 6`, so earlier NFL scores, including version 5 tuples scored with [quality-scaled Closeness](0006-quality-scaled-pro-closeness.md) but no seeding term, are not reused. NBA, NHL and MLB stay on version 5. Forecasts stored without seed cells keep their JSON shape and fingerprints.
