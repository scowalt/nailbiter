# Suspense-weighted excitement verification

The production policy in `apps/backend.fsharp/Services/ExcitementScore.fs` remains unchanged. It filters invalid probabilities, uses a 0.03 meaningful-reversal threshold, integrates suspense across meaningful movement, multiplies accumulated credit by 20 and caps the score at 100. CFB/NFL/NBA/NHL/MLB use this policy; motorsport/World Cup retain the existing raw-movement behavior.

The public acceptance inputs are independently constructed staircase histories, not historical calibration data. The original empirical observations/assertions remain private archival evidence; the public suite does not depend on them or silently relabel them.

| Scenario | Construction | Analytic score |
|---|---|---:|
| `990000001` | 300 samples; alternating 0.5/0.75 every 40 samples; final ten at 0.995 | 20.83333 |
| `990000002` | 320 samples; alternating 0.2/0.8 every 35 samples | 56.16 |

`tests/fixtures/test_generation.py` independently integrates these step endpoints with rational arithmetic; it does not call the production scorer. Literal .NET expectations verify prefix scores, full-history scoring, field fidelity, latest-100 API behavior, bound expiry and exclusion flags through the actual in-process HTTP app and isolated PostgreSQL.

Short hand-authored policy cases independently cover jitter, threshold boundaries, unfinished tails, initial excursions, invalid/unavailable versus flat-zero history, team symmetry, interpolation/duplicates, saturation, monotonic prefixes, ordering and historical/recovered exclusions. Preserve those cases when updating generated fixtures.

Regenerate and check with `scripts/test-fixtures/generate.py`; see [central provenance](../../tests/fixtures/README.md). Synthetic success is implementation regression evidence, not current empirical calibration, predictive accuracy, live provider conformance or deployment acceptance.
