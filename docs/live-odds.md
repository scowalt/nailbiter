# Live game-winner probabilities

The implementation keeps provider identity, schedule applicability, price quality and observation time separate. Kalshi is the current team-sport game-winner source, with existing ESPN compatibility/fallback behavior. A missing or unusable quote is unavailable, not neutral probability.

## Matching and schedule changes

Kalshi date/time tickers use US Eastern time. Resolve each team's sport-specific aliases; do not apply an MLB alias to NFL/NBA teams. Select one unambiguous event and both outcome contracts. Do not join by nearest start or pair markets from different events.

MLB may reconcile earlier or later same-day starts only through the implemented exact known rule formats and one authoritative linked milestone. Validate type/league/status, both opaque team identities, primary event linkage and exact new start. Both contracts must remain active binary game-winner contracts before the original 48-hour window ends. Reject duplicate/paginated/missing milestones, cross-date guesses, doubleheader ambiguity, unknown rule variants and exceptional settlement. These restrictions are compatibility logic, not proof that current provider feeds always match it.

## Probability meaning

Use current executable quotes, preserve original observation times and existing expiry limits, and keep whole-pair fallback/source isolation. A near-certain bound is not a precise probability or confirmed game completion. Settled qualification outcomes are distinct from game-winner prices. Historical/recovered points retain their flags and cannot supply live excitement.

## Offline evidence

`KalshiScheduleChangeTests.fs`, `KalshiPregameOddsTests.cs`, the importer/page regressions and policy tests use independently generated synthetic inputs. Date-only/time-specific rule cases, market-order reversal, invalid identities, deadlines, transport failure, withdrawal and page propagation remain covered. [Fixture provenance](../tests/fixtures/README.md) explains the loss of actual-feed corroboration and the independent construction process.

No live provider calls, source enablement or historical backfill is authorized by running these tests.
