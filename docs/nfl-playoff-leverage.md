# Proposed NFL playoff leverage from PlayoffStatus

The owner selected **NFL-only** PlayoffStatus win/loss-conditioned playoff forecasts in the continuing implementation proposal. This is separate from Kalshi's unconditional playoff outlook and verified final outcomes. Migration of this draft does not merge, deploy, activate or grant a publisher reuse license. NHL PlayoffStatus remains rejected; MoneyPuck controls and the shared daily budget are unchanged. See the [scoped source decision](playoff-leverage-source-research.md).

## Scoring and presentation

For a verified upcoming regular-season NFL game:

```text
qualification contribution = 80 × (P(playoffs | win) − P(playoffs | loss))
team Stakes = qualification contribution
game Stakes = max(home Stakes, away Stakes)
hype = .45 × Stakes + .35 × Quality + .20 × Closeness
```

Do not multiply game-specific leverage by season urgency again. Verified season progress remains required. On 2026-11-05 the owner removed seeding consequences from regular-season Stakes across NFL, NBA, NHL and MLB: there is no seeding bonus, ceiling or required certificate. Conditional probability intervals remain intervals; a numeric result is permitted only when the combined qualification bounds agree at full precision. Unavailable inputs are never neutral defaults or reweighted components.

A conditional forecast can supply NFL qualification Stakes without a current Kalshi estimate; an unconditional Kalshi estimate cannot fill a missing conditional forecast. Trusted eliminated and qualified outcomes both contribute zero qualification Stakes without unrelated forecasts or seeding evidence. Model endpoints never become confirmed outcomes. NFL preseason/postseason bands, Quality, Closeness and existing weights remain unchanged.

Complement the single **No Playoffs** column rather than adding rounded seed columns. Threshold percentages remain intervals: `<1%` is not zero and `>99%` is not one. Reject reversed numeric scenarios; bound overlapping intervals without inventing a midpoint.

Detail pages show away/home **Playoff chances by result**, **If win / If loss**, and explicit model/source credit. The Kalshi Playoff outlook stays separate. Valid individual scenarios/components remain visible when the total is unavailable; missing scenarios receive a team-specific explanation. Validation reasons, clocks and coverage IDs remain server-side.

## Low-interest playoff display

The owner selected the compact `<1%` label for low-interest NFL regular-season qualification markets on 2026-11-05. It is a display convention, not an exact probability or a claim of elimination. No range or longer visible qualifier is added; hover/accessibility descriptions explain the convention.

Apply it only when the latest season/team/contract-verified Kalshi observation is strictly under one hour old, has an explicitly empty zero YES bid and a populated YES ask above one cent and at most two cents, and supplies no accepted numerical probability. Missing, stale, malformed, inactive, ambiguous or broader-priced markets remain unavailable. Valid quoted probabilities and confirmed finals retain priority. The collector preserves the rejected quote envelope in the existing evidence column during its ordinary refresh; no extra request or migration is needed.

The presentation override stays separate from `PlayoffProbability`, qualification selection and final-outcome state. It never supplies a scoring representative, changes conditional NFL Stakes or repairs missing conditional forecasts. Other sports and source/admission controls are unchanged.

## Implemented source contract — not live conformance proof

The collector supports ordinary HTTPS pages on `www.playoffstatus.com/nfl/`:

- `nflschedule.html`: explicit season heading/week, 18 ordered weekly tables, 272 distinct contests and 32 recognized teams with 17 contests each. ET date/time columns and unambiguous source season are required. January/February use the following calendar year; ambiguous/nonexistent DST starts fail closed. Future TBD times are not invented for the target.
- Participating `TEAMwhatif.html` pages: exact team title (SF accepts only its `San Francisco 49ers What If?` or `San Francisco Forty-Niners What If?` identity), matching displayed publication/week token, correct Next Game opponent, one each of Win Next Game / Current Standings / Lose Next Game, correct W/L/T transitions and usable No Playoffs values. Other scenario tables cannot fill missing next-game rows.
- The target must be both teams' next unplayed contest with exact roles/week. Exact kickoff agreement remains the default. On 2026-11-05 the owner approved a corroborated same-game exception for precise starts at most 30 minutes apart: the source must identify one contest on the same Eastern calendar date, and both ESPN team schedules must independently contain the actual canonical event ID, season, regular-season phase, explicit week, home/away roles, `timeValid=true`, identical canonical kickoff and unstarted zero-score status. The target must also be each team's earliest uncompleted ESPN event, not only the source's next contest. Missing copies/IDs, unknown times, different dates, later games and larger shifts remain unavailable; there is no SF-specific override or rounding tolerance beyond the exact 30-minute bound.
- All 32 existing ESPN season schedules corroborate every matchup's roles/week, both ESPN event copies and completed results in both directions. Completed-game kickoff times and scores must agree exactly. If a week is absent, the calendar date must corroborate it. An unrelated unplayed game's changed kickoff does not invalidate the whole batch: an affected target still requires either exact kickoff agreement or the approved corroborated same-game proof above. Neither provider's time is rewritten. ESPN remains authoritative for the exclusive scheduled/confirmed-start cutoff; an erroneous earlier source time does not move that cutoff forward. This never relabels a forecast or invents a kickoff. Missing identities, contradictory ESPN copies or newer completed evidence still invalidate coverage. The timezone-free visible label is only a consistency token, never an invented UTC timestamp.

## Clocks and collection

Observation age must be strictly under one hour. Schedule and team publications require actual UTC HTTP `Last-Modified`, strictly under seven days old and not future. HTTP `Date` is required, nonfuture and under one hour old. Team publication must be within ten minutes of the schedule publication, with the same visible generation/week token. Every score read rechecks current completed-result coverage.

The seven-day ceiling is a bounded publication policy, not an hourly update or freshness guarantee. Re-fetching unchanged content cannot renew its publication clock or fabricate result coverage.

The proposal joins the existing scraper-gated startup/15-minute pro competition refresh and seven-day upcoming horizon. Shared per-batch requests cap unique schedule/team URLs at 33. Page reads and scorers do not request providers; the legacy NHL/manual ratings path is not reused. Requests use an honest Nailbiter identity, 15-second deadline and 1 MiB body ceiling, with no automatic retries/redirects. A 401/403/429 stops the remaining batch. No denial bypass, proxy, private endpoint, paid service or outreach is authorized.

Transport/server failures retain prior evidence without renewing clocks. Successfully fetched unusable observations replace affected evidence with unavailable state. Cancellation cannot commit a partial competition refresh. These are implemented guards, not proof of sustained access or source rights.

## Storage, rollout and offline verification

`NflPlayoffLeverage` is additive evidence inside existing `games.pro_evidence_json`; no SQL migration or backfill is introduced. Exact-match version 1 records retain their existing serialized shape and evidence fingerprints. Corroborated version 2 records additionally retain the actual source kickoff, canonical ESPN kickoff, week and both target/next-game IDs in `CorroboratedKickoff`. Every selection revalidates that proof; the proof is bound to each conditional evidence revision and remains server-side through persistence, capture and retention. Age gaps may retain a complete supported tuple without renewing its clocks, but rejected/overwritten corroboration still revokes it across restart. The regular-season score formula and its saved-tuple version are unchanged. Legacy JSON without the field cannot supply the proposed new NFL qualification term until a legitimate ordinary refresh. A separately authorized future rollout must reconcile this availability consequence and source-use/operational evidence; this draft does not perform it.

The [synthetic fixture contract](../apps/backend.tests/Fixtures/NflPlayoffLeverage/README.md) uses four independently generated files rather than captured HTML or transcribed schedules. Tests exercise exact identities, result corroboration, malformed/duplicate/threshold cases, publication/observation expiry, request sharing, denial/cancellation, trusted outcomes, ignored seeding metadata and all three actual page routes through isolated PostgreSQL and intercepted HTTP.

```sh
python scripts/test-fixtures/generate.py --check
python -m unittest discover -s tests/fixtures -v
mise exec -- dotnet test apps/backend.tests/backend.tests.csproj -c Release --filter FullyQualifiedName~NflPlayoffLeverage
mise exec -- dotnet test apps/backend.tests/backend.tests.csproj -c Release
mise exec -- dotnet test apps/backend.fsharp.tests/backend.fsharp.tests.fsproj -c Release
```

`NFL_LEVERAGE_ARTIFACT_DIR`, if used, must point outside the checkout for the controlled rendered page. It is not a running app or live data. Any temporary browser preview must use Portless/private Tailscale and owned cleanup. Passing tests does not prove current publisher fields, independent real schedule agreement, source accuracy, recurring access, public reuse rights or production delivery.
