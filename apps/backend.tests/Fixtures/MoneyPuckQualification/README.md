# Synthetic model-qualification input

Every file in `Synthetic/` is independently constructed by `scripts/test-fixtures/generate.py`. No values, marker or HTML are presented as source observations. The invented NSH/OTT probabilities are 0.2518/0.6804; all remaining model values are 0.5. The exact game-ID/season join deliberately uses 2099-prefixed IDs with an explicit 2026–27 season, so tests cannot accidentally treat a prefix as proof.

The constructed Kalshi counterpart has an unusably wide NSH quote and an eligible OTT pair. The actual model/market refresh and page harnesses test precedence, attribution, evidence expiry and isolation without real provider HTTP. Exact endpoint handlers reject unknown requests. Persistence, daily shared admission, restart/denial/race/high-water semantics and atomic invalidation remain covered.

These inputs do not establish a real page/schema shape, exact provider-to-NHL linkage, sustained access or predictive accuracy. Source collection/selection remain default off under the [activation policy](../../../../docs/nhl-moneypuck-qualification.md). See [central provenance and regeneration](../../../../tests/fixtures/README.md).
