# Playoff qualification probabilities

**Current implementation:** [ADR 0003](adr/0003-nhl-moneypuck-qualification-exception.md) narrowly permits the user-approved MoneyPuck NHL model-derived qualification fallback. Application collection/selection controls remain **default off**, with an empty explicit season allowlist; only season **2027** is supported. Following the owner's 2026-11-03 “use forecast ON” instruction, production Compose defaults refresh and selection on for season 2027, with independent operator stops. This enables consideration of eligible stored evidence, not new provider requests or a denial reset. Live-source readiness and usable coverage are not established by the flag; missing, invalid or expired forecasts remain unavailable. See the [MoneyPuck qualification runbook](nhl-moneypuck-qualification.md) and [bounded primary-source notes](nhl-moneypuck-qualification-source-notes.md). NHL PlayoffStatus remains rejected and disabled.

Kalshi supplies market-derived NFL, NBA, NHL, MLB, and college football playoff qualification probabilities. **On 2026-10-10 the user required market-only, free-only NHL qualification evidence and rejected all model-derived qualification forecasts.** No additional gambling-market fallback is integrated; the narrow October 11 MoneyPuck exception is not approval of another source. Current-season FBS teams confirmed to have no CFP market use a separately sourced below-1% assumption, described below.
Power rankings, championship probabilities, and game-winner odds remain separate inputs with their existing sources. The separately selected [NFL conditional-leverage proposal](nfl-playoff-leverage.md) affects regular-season NFL Stakes only; it is not an unconditional qualification source, NHL approval or a deployment/activation claim.

## NHL PlayoffStatus model fallback — rejected; disabled legacy implementation

**The user's 2026-10-10 market-only decision supersedes the previous pending-approval model policy. Do not enable this fallback, even if reuse rights or publication clocks are resolved.** The following describes retained software, not an authorized source-selection policy. See the [decision](adr/0002-nhl-market-only-qualification.md) and the [current source policy](sportsbook-sources.md).

`PlayoffRatingsOptions` defaults to `NhlModelMode=off`, `NhlModelRefreshEnabled=false`, and an empty `ApprovedSeasons` list. Both selection and HTTP refresh require `NhlModelMode=fallback` for the NHL season; setting refresh true while mode is off is still a kill switch. This software path is tested with synthetic offline fixtures only. Those fixtures do not prove PlayoffStatus reuse rights, attribution compatibility, publication-clock semantics, model accuracy, or live coverage.

The retained model implementation's selection precedence is shown below for maintenance only. Default NHL qualification uses Kalshi alone. The following legacy precedence is historical maintenance documentation, **not** the new MoneyPuck path; this PlayoffStatus branch must not be enabled:

1. verified Kalshi final qualification outcome;
2. eligible Kalshi estimate, strictly younger than one hour;
3. eligible PlayoffStatus model estimate, strictly younger than 36 hours from the source publication timestamp;
4. unavailable.

The fallback stores explicit model evidence (`model_projection`) with provider, validation policy, season/team verification, source publication time, fetch time, state and rejection reason. It is not a Kalshi quote, has no market/event IDs, no bid/ask/depth fields, and can never be final. Legacy PlayoffStatus rows without this model evidence remain ineligible until a validated refresh replaces them.

The former model proposal's clock contract was intentionally narrow: every fetched document must carry the same offset-aware configured publication timestamp (`nailbiter-approved-source-published-at`) before runtime accepts the batch. Timezone-free strings, missing page metadata, conflicting page clocks, future publication times and older out-of-order publications are rejected. The real PlayoffStatus page clock and recurring reuse/attribution terms remain unresolved, so live collection and deployment stay blocked.

**Polymarket remains rejected.** See the [canonical provider decision](sportsbook-sources.md#rejected-provider-polymarket). No configuration setting enables Polymarket, and this NHL model fallback does not revive Polymarket adapters, catalogs or settlement logic. Python remains intentionally Kalshi-only.

## Qualification scope

Qualification means a place in the actual playoff field. NBA play-in entry alone does not count.
An MLB Wild Card berth counts, even if the team loses the Wild Card Series.
For college football, qualification means entry into the CFP, not bowl eligibility or winning the national championship. See the [college runbook](college-football.md) for its stricter missing-data hype policy and eligibility boundary. College basketball, motorsport qualification, and World Cup advancement are not included.

## Seasons and market identity

Qualification displays use each game's `SeasonYear`, with the existing scheduled-date convention when absent. Pro-league hype is stricter: missing verified season identity makes affected components unavailable; calendar dates never establish its urgency or select another season's evidence.
The importer makes sure that the event, series, season subtitle, and team market identify the expected qualification contract.
A mismatched response cannot supply another season's probability.

| League | Stored season | Example event | Team count |
| --- | --- | --- | --- |
| NFL | Regular-season start year, 2026 | `KXNFLPLAYOFF-27` | 32 |
| NBA | Playoff year, 2027 | `KXNBAPLAYOFF-27` | 30 |
| NHL | Playoff year, 2027 | `KXNHLPLAYOFF-27` | 32 |
| MLB | Calendar year, 2026 | `KXMLBPLAYOFFS-26` | 30 |
| CFB | Season start year, 2026 | `KXNCAAFPLAYOFF-26` | Season-verified FBS roster; market coverage is partial |

Refreshes use the prior NFL season in January and February, then the upcoming season from March.
NBA and NHL refreshes include the ending and upcoming seasons from July through September. From October, they use the new season.
MLB refreshes use the calendar year and also request the upcoming season from November.
CFB January games retain the prior start-year season; refreshes use the upcoming season from February. College refresh requires a verified season roster.
An unavailable upcoming event cannot replace the ending season's data.

Team abbreviations differ between leagues and market families. The importer maps qualification tickers to ESPN team abbreviations.
Pro-league qualification aliases are independent of game-winner ticker mappings. College uses its college-specific aliases, including `MIZ → MIZZ`, `SC → SCAR`, `NCSU → NCST`, and `M-OH → MOH`. Unknown teams cannot overwrite another team's probability, and unrecognized college contracts prevent inferring market absence.

## Approved NHL sportsbook-price fallback contract — no provider integrated

The user approved keeping eligible Kalshi qualification evidence first, with a sportsbook-price fallback only for genuine gaps. Use the make and miss prices for the same team, season and bookmaker; convert each price to its implied probability, then remove the bookmaker margin proportionally with `p_make / (p_make + p_miss)`. This is arithmetic on gambling-market prices, not a sports forecasting model. Never combine opposing prices from different books or average the fallback with Kalshi.

A fallback source must be free-only and have appropriate existing recurring access/reuse permission. The user declined written permission requests; RotoWire is therefore excluded under its published prior-consent requirement. No fallback provider, freshness limit or implementation is approved yet. See the [current source boundaries](sportsbook-sources.md); historical source research remains private archival evidence. No sportsbook-price source is integrated. Default runtime selection remains Kalshi-only; the independently controlled MoneyPuck exception does not authorize a sportsbook provider.

## Price quality

A bid is a price offered by a buyer. An ask is a price requested by a seller.
With orders on both sides, the estimate equals the midpoint of the YES bid and ask in dollars.
For a synthetic example, a 60-cent bid and a 64-cent ask produce a probability of 62%.
Multiple teams qualify, so probabilities do not sum to 100% across teams.

An unsettled market normally must meet all these conditions:

- The market is active, without a result, and its closing time is in the future.
- Both prices exist and fall between zero and one dollar.
- Both sides have a positive order size.
- The bid does not exceed the ask.
- The spread does not exceed 25 percentage points (25 cents on a binary $1 contract).

On 2026-10-13, the user widened the shared qualification spread limit from 20 to 25 percentage points. Exactly 25 points is accepted; any larger spread remains `wide_spread`. A constructed 60-cent bid / 82-cent ask passes this spread check, yielding a 71% midpoint if the other eligibility checks pass. This applies to qualification across all supported leagues, not game-winner quotes. Previously rejected stored evidence needs the next successful normal Kalshi refresh; no migration or manual row repair is needed.

There is no minimum trading-volume threshold. A market can pass these rules despite little recent trading.

An extreme one-sided market can also supply an estimate, with the same identity, status, closing-time, and price checks.
A YES bid of at least 99 cents with positive order size permits an empty ask at one dollar.
A YES ask of at most one cent with positive order size permits an empty bid at zero dollars.
The empty side must explicitly report zero order size. Missing or malformed prices and sizes do not count as an empty side.
Ordinary one-sided quotes, markets with no orders on either side, and last trades remain unavailable. Missing markets also remain unavailable except for the explicit college no-market assumption below.

For these extreme quotes, the importer stores the midpoint between the populated quote and the zero-or-one endpoint for scoring.
A 99-cent bid becomes an internal estimate of 99.5%. A one-cent ask becomes an internal estimate of 0.5%.
The UI displays `>99%` and `<1%` instead of these internal estimates.
These estimates still expire after one hour. A price near zero or one does not prove elimination or qualification.

## College no-market assumption

For current-season FBS teams only, complete season-verified live/archive discovery may establish that a team has no CFP market. Such teams receive `<1%`, following the user's 2026-09-17 policy change and subsequent request for compact labels. The detail source identifies the value as a `Nailbiter estimate`; the percentage itself has no explanatory suffix. This is an assumption about an unlisted team, not a measured market probability or confirmed elimination.

These rows use source `cfp_no_market`, with an internal representative probability of `0.005` for scoring, and retain their evidence timestamp and verified season. They are not stored as Kalshi quotes. They expire after one hour just like market estimates, and cannot be created for past seasons, FCS teams, unknown identities, or failed/incomplete discovery. Listed markets with unusable quotes do not qualify for the assumption.

A new market supersedes the assumption whether or not its quote is usable. Verified final outcomes take precedence. Current snapshots never backfill what was supposedly known before a past game. No database migration is required.

## Refreshes and failures

The independent Kalshi playoff worker runs at startup and every five minutes. The daily power-ranking worker does not control these refreshes.
`POST /api/ratings/refresh` also refreshes the supported leagues and reports their combined market-quote/final-outcome count under `kalshiPlayoffs`. College no-market assumptions are counted separately in refresh logs.
Development environments need `EnableScraper=true` to run the worker.

The user-confirmed **at-most-once-per-day limit covers all MoneyPuck collection**. Rankings and opt-in qualification share one persisted cycle with at least 24 hours between admitted attempts, including failures/cancellation, across manual/background callers, simultaneous backend instances and restarts. This supersedes the six-hour synthesized #672 default. The dedicated MoneyPuck worker runs at startup and its next persisted boundary, not the general 06:00 UTC ranking schedule. `POST /api/ratings/refresh` invokes the owner once and reports `nhl_powerRankings` and `nhl_moneypuckQualification` separately. Application qualification controls remain default off with explicit supported-season 2027 allowlisting. Production Compose defaults qualification collection and selection on, with `MONEYPUCK_REFRESH_ENABLED=false` and `MONEYPUCK_SELECTION_ENABLED=false` as independent stops. They require reviewed rollout; strictly-under-36-hour publication expiry, the daily budget and persistent denial state are unchanged. Selection itself never requests provider data. Apply additive, repeatable `033_MoneyPuckDailyRefresh.sql` without resetting its operational state; unavailable admission storage fails closed. Page requests read stored evidence only. Kalshi refresh remains independent. The retained PlayoffStatus worker is unauthorized and must stay off; MoneyPuck enablement suppresses its legacy controls.

A failed event/model request keeps the previous snapshot without changing its fetch time. A snapshot is the latest stored state, not probability history.
Readers reject unsettled Kalshi estimates at an age of one hour and MoneyPuck qualification estimates at exactly 36 hours from publication (the disabled legacy PlayoffStatus path also has a 36-hour limit). They reject future timestamps, unverified seasons, and invalid probability values.

PostgreSQL scalar timestamps retain microseconds, while persisted JSON evidence retains .NET's 100-nanosecond ticks. Only JSON-versus-scalar ordering discards sub-microsecond ticks (never rounds): Kalshi observation/evidence versus scalar fetch, and NHL model JSON fetch versus scalar fetch. Values within the same microsecond bucket compare equal; the next microsecond remains future evidence. JSON evidence versus observation, resolution versus observation, model publication versus JSON fetch, and all timestamps versus current time retain strict full-precision ordering. Original evidence/publication timestamps still determine expiry, including rejection at exactly one hour or 36 hours. This reader-side rule recovers existing valid JSON without refresh, row repair, migration, or enabling the default-off model provider.

Readers choose the latest stored row per provider before assessing eligibility; an older usable same-provider row cannot replace a newer unavailable state. Eligible Kalshi, including verified finals, precedes the explicitly enabled NHL MoneyPuck fallback; trusted qualification-resolution blocks veto fallback. The college-only `cfp_no_market` source is unchanged. MoneyPuck evidence is separately versioned, cannot be final, and must prove complete 32-team validation, explicit season and exact NHL game-ID corroboration. See the [runbook](nhl-moneypuck-qualification.md#stored-evidence-and-selection).

A valid Kalshi response makes duplicate or unusable team markets unavailable immediately. For MoneyPuck, successfully retrieved invalid snapshots revoke the entire qualification batch atomically; transport failures and verified publication races retain all prior clocks. Unchanged publication cannot renew expiry. Denial in either MoneyPuck input stops requests and persistently blocks both inputs across restart; interrupted/unfinalized cycles also fail closed pending review. There is no reset endpoint and no restart-to-retry entitlement. Ranking evidence, source validation, formulas and publication clocks remain independent within the shared daily cycle. The following retained PlayoffStatus behavior is historical only: a valid complete PlayoffStatus observation can likewise make an individual model team unavailable with a stored rejection reason. PlayoffStatus completeness is checked before invalidation: a standings document needs the supported completion marker and syntactically complete table/document, plus the postseason page must provide full 32-team identity/schema. A recognized complete standings document that omits a team is a complete invalid observation for that team; a raw truncated standings response rejects the whole batch and retains prior rows without timestamp renewal. A present recognized team with malformed or contradictory probabilities is also a complete invalid observation for that team. Missing markets are unavailable unless the college-only absence policy applies.
For omitted markets, the importer requests the same event from Kalshi's historical endpoint to recover final outcomes.
It follows pagination with a three-page limit and rejects incomplete or mismatched archive responses.
If archive retrieval fails, a missing team's unsettled estimate becomes unavailable without receiving a new fetch timestamp.

## Final outcomes

A confirmed final YES result means that the team qualified. A confirmed final NO result means that the team did not qualify.
These outcomes appear as “Clinched playoffs” and “Missed playoffs” for the matching season and do not expire after one hour.
“Missed playoffs” means that a team cannot qualify, even if regular-season games remain. It does not mean elimination after qualifying for the playoffs.
Final outcomes do not require active quotes. Fractional payouts and other exceptional settlements do not establish qualification outcomes.

The importer keeps a known final outcome and its evidence timestamp when Kalshi omits the market.
Present market evidence takes precedence, including corrected outcomes or exceptional settlements that make qualification unavailable.
The detail view distinguishes final outcomes from estimates. It does not present them as probabilities known before a past game.
Automatic refreshes cover the season candidates above, not every historical season.

## Display and scoring

Homepage cards, league cards, scheduled-game comparisons, and hype scoring use the same qualification policy.
Missing probabilities appear as unavailable, never as zero or 50%, except for the separately sourced college no-market assumption.
Nonfinal estimates below 1% display as `<1%`, and those above 99% display as `>99%`, including quotes with orders on both sides.
Exactly 1% and 99% estimates retain their numeric labels. Only confirmed final outcomes display as “Clinched playoffs” or “Missed playoffs”.
Homepage and league cards show these labels without the “playoff odds” suffix.
Scheduled-game comparisons use one “Playoff outlook” row for an estimate, a confirmed outcome, or “Unavailable”.
The outlook row replaces the separate “Playoff Odds” and “Final outcome” rows. Fetch timestamps and raw calculation/source diagnostics stay server-side; there is no hidden client-side disclosure or diagnostic payload.
This formatting applies only to playoff qualification odds, not game-winner odds.
For selected MoneyPuck NHL qualification, probabilities say **model estimate** and a visible MoneyPuck predictions link credits only the selected team(s), including displayed derived hype. An unselected Ottawa model is not credited when Ottawa uses Kalshi. Home/league credits are siblings of game-navigation links, never nested anchors. Unavailable/unselected model evidence receives no credit. Other NHL attribution remains unchanged. Non-NHL detail views keep their existing concise attribution, including the college `Nailbiter estimate` when applicable. Evidence times remain on the server for freshness validation and diagnosis. When a required regular-season qualification estimate is missing, the main hype card names the affected team in a short explanation. This presentation does not loosen quote-quality checks: for example, a constructed 20¢ bid / 80¢ ask has a 60-point spread and remains rejected under the 25-point rule.
Fetch time does not measure the age of a trade or the amount of recent trading.

College hype requires all its components: the explicit no-market assumption is an accepted qualification input, but listed/unusable or otherwise unknown probabilities still leave regular-season stakes and total hype unavailable. College postseason classification is described in the [runbook](college-football.md).

For NHL, NBA, NFL and MLB, missing required qualification evidence makes regular-season stakes and total hype unavailable, not neutral 50. Valid quality and closeness remain visible. Closeness has no qualification-relevance multiplier or missing-probability substitute. Confirmed eliminated teams contribute zero; other teams require verified seeding consequences as well as the relevant qualification/progress evidence. A postseason flag alone does not establish round or elimination stakes.

See [Pro-league hype evidence](pro-hype-evidence.md) for the formulas, one-hour competition/game-odds expiry, 48-hour verified ranking pairs, schedule collection and seeding certificates, known coverage gaps, rendering behavior, and migration `028_ProHypeEvidence.sql`. These replace the former pro-league neutral-stakes and maximum-postseason policies without changing the qualification quote/final-outcome requirements above.

Pages read the stored snapshot when they load. This change does not add automatic probability updates to open pages.
PowerRankingsGuru and MoneyPuck still supply independent rating inputs. Championship probabilities remain independent from qualification. PlayoffStatus model qualification fields must not supply NHL display/scoring probabilities under the user's 2026-10-10 market-only decision; its championship fields are never treated as qualification.

## Analysis and historical evidence

The Python analysis package continues to select qualification probabilities from Kalshi alone; separately sourced college assumptions are not treated as market observations for historical analysis.
It matches sport, team, and season before it applies verification, expiry, and final-outcome rules.
Strength and championship inputs remain independent. Notebook consumers keep different seasons separate.

Current snapshots are not historical pregame evidence. A final result fetched after a game cannot be used as though it predicted that game.
The analysis tools treat missing evidence as unavailable. This migration does not backfill historical probabilities or retune the scoring algorithm.
See `apps/analysis/README.md` for analysis commands and time-selection behavior.

## Deployment and tests

Migration `029_PlayoffQuoteEvidence.sql` adds nullable `team_ratings.playoff_evidence_json` without rewriting rows or changing the existing provider snapshot uniqueness constraint. Apply it before deploying this foundation code. NHL Kalshi refreshes populate versioned contract, quote/rejection and correction metadata atomically with scalar values; other sports are unchanged. Present exceptional settlements or retracted finals retain a diagnostic resolution block until a verified binary correction. The shared NHL selection policy consumes trusted resolution blocks; a model forecast cannot bypass them. Rollback can leave the additive column in place; no data deletion is required.

Migration `026_PlayoffFinalOutcome.sql` adds `playoff_probability_is_final` with a false default.
Existing 0% and 100% rows remain estimates until Kalshi confirms an outcome. The migration is safe to run twice.
Apply migrations before restarting the backend, as the deployment workflow already does. No production rows need manual deletion.

The public tests use independently constructed synthetic provider-format inputs, fixed clocks, isolated SQLite databases and PostgreSQL test containers. `MoneyPuckQualificationPageRegressionTests` runs actual registered Kalshi/MoneyPuck refreshes and in-process home/NHL/detail HTTP against authored wide-quote/eligible-market/model scenarios. None is a provider response or historical reconstruction. See the [central fixture provenance](../tests/fixtures/README.md).
They cover team mappings, source isolation, season boundaries, quote rejection, archive recovery, expiry, final outcomes, legacy non-NHL PlayoffStatus marker parsing, NHL-only strict model scalar parsing, PlayoffStatus parsing/corroboration/publication-clock rejection, completeness-before-invalidation, cancellation and real database rollback without partial persistence, disabled/off-mode HTTP suppression, real background/manual shared single-flight cooldown, independent Kalshi refresh while the model gate is occupied, same-request rendered-page consistency across a refresh race, refresh-to-page-and-hype rendering, and historical/Python isolation.
The synthetic MLB scenario also covers opposing one-sided bounds through import, card and detail rendering, scoring, expiry and settlement.
No new migration is required for the extreme-quote treatment or NHL model evidence. The next successful refresh replaces previously unavailable estimates.
Run the backend suites from the repository root:

```bash
dotnet test apps/backend.fsharp.tests/backend.fsharp.tests.fsproj --configuration Release
dotnet test apps/backend.tests/backend.tests.csproj --configuration Release
```

## References

- [Kalshi public market data](https://docs.kalshi.com/getting_started/quick_start_market_data)
- [Kalshi market schema](https://docs.kalshi.com/api-reference/market/get-markets)
- [Kalshi historical data](https://docs.kalshi.com/getting_started/historical_data)
- [NBA play-in format](https://www.nba.com/news/nba-play-in-tournament)
- [MLB playoff format](https://www.mlb.com/news/mlb-playoff-format-faq)
- [NFL power-ranking season verification](nfl-playoff-odds.md)
