# NHL Quality from Power Rankings Guru

The owner selected [this single NHL rankings page](https://powerrankingsguru.com/nhl/power-rankings) and explicitly directed its limited use after reviewing the bulk-download wording in the publisher's [terms](https://powerrankingsguru.com/terms). This records the operator's direction, not a publisher licence or a definitive interpretation of those terms. No permission outreach, account, paid feed or access-control bypass is introduced. See the [source assessment](nhl-power-ranking-source-research.md) and [third-party scope](../THIRD_PARTY.md).

## Collection

`NhlPowerRankingsGuruService.RefreshAsync` retrieves only that page, with an honest Nailbiter user agent, a 15-second timeout, no automatic retries and no redirect following. It does not fetch logos, scripts, team pages or undocumented endpoints. An access challenge or unsuccessful HTTP response stops collection.

The existing ratings worker and manual refresh entrypoint delegate NHL rankings to this module. A PostgreSQL compare-and-update admits at most one attempt per 24 hours across callers, application restarts and failures. A later valid `Retry-After` extends admission. The independent `nhl_guru_refresh_state` ledger is created by additive migration `037_NhlGuruRankings.sql`; replay does not reset it. Apply that migration before running the collector. It never reads or changes MoneyPuck's admission/refusal ledger.

## Validation and storage

Require exactly one server-rendered rankings table within the page sheet, an explicit `YYYY-YY NHL Power Rankings` heading with consecutive years, and one `Updated ... ET` publication label. Convert the Eastern publication time to UTC, rejecting ambiguous/nonexistent local times and future observations. Nailbiter identifies 2026–27 as season **2027**; only existing supported NHL seasons qualify.

Accept only the complete canonical set of 32 team-page identities with explicit consecutive ranks 1–32. Missing, duplicate, unknown, fractional or out-of-order ranks/teams and ambiguous table/header changes reject the whole batch. Read ranks, not Elo magnitudes, trend, points projections, playoff chances or title probabilities.

Store the complete batch atomically in `team_ratings` under `powerrankingsguru_nhl`, independently of older `powerrankingsguru` rows and MoneyPuck. Preserve the source season, original publication time and collection observation time. No forecast probabilities are imported. Invalid successful observations clear this source's rank eligibility; transport/access failures preserve previous data and its original clocks. A publication regression is rejected rather than rolling back the accepted batch.

## Quality and retained hype

Prefer an eligible complete MoneyPuck pair, then a complete Power Rankings Guru pair from one coherent publication/observation batch. Never mix providers or batches. Both publication and fetch age must be strictly under 48 hours for current Guru-based Quality; collecting an unchanged old page does not renew publication age. MoneyPuck's existing seven-day publication policy and independent budget are unchanged.

Use the existing rank-to-strength conversion, harmonic Quality and sport weights. Qualification evidence, Stakes, game-winner probabilities and Closeness are independent and unchanged. A recovered ranking pair does not guarantee a complete hype score.

A previously captured whole tuple can survive age/transport gaps without new raw eligibility. Withdrawal, rejected supporting evidence or a corrected supporting rank under the same publication revokes that saved estimate durably, including when corrected/restored before the next read. Unrelated providers do not revoke it. Original source credit is retained with the tuple, even if current inputs prefer MoneyPuck. Home/league/detail credit links to Power Rankings Guru when its ranks actually support the displayed Quality/hype; no ranking-list page or new data endpoint is introduced. Existing exclusive start cutoffs and hidden-diagnostic restrictions remain.

## Offline verification and limits

`NhlGuruRankingsTests` exercises the registered refresh, real PostgreSQL, raw scorer, database-only automatic capture and home/league/detail. Cases cover full canonical coverage, source/season/publication rejection, Eastern DST, same-source priority, concurrency/restart/daily failure admission, longer `Retry-After`, migration replay, forecast isolation, strict expiry, supporting-source withdrawal/correction, retained attribution and start cutoff. MoneyPuck state is seeded as blocked to prove it is neither consulted nor altered by Guru collection.

The HTML fixture is generated independently by `scripts/test-fixtures/generate.py`, not captured or transcribed ranking values. Synthetic postseason scenarios make lifecycle/page assertions independent of qualification feeds; they are not claims about actual NHL games or calendar timing. Offline success does not prove production access, sustained coverage or deployment. No new live-provider request or production operation is a test prerequisite.
