# Proposed NFL playoff leverage from PlayoffStatus

The owner selected **NFL-only** PlayoffStatus win/loss-conditioned playoff forecasts in the continuing implementation proposal. This is separate from Kalshi's unconditional playoff outlook and verified final outcomes. Migration of this draft does not merge, deploy, activate or grant a publisher reuse license. NHL PlayoffStatus remains rejected; MoneyPuck controls and the shared daily budget are unchanged. See the [scoped source decision](playoff-leverage-source-research.md).

## Scoring and presentation

For a verified upcoming regular-season NFL game:

```text
position value V = P(playoffs) + .5 × P(seed ≤ 4) + .5 × P(seed 1)
team Stakes = min(100, 80 × (V | win − V | loss))
game Stakes = max(home Stakes, away Stakes)
hype = .45 × Stakes + .35 × Quality + .20 × Closeness × Quality / 100
```

Seeds 5–7 weigh 1, the division-title seeds 2–4 weigh 1.5 and the No. 1 seed weighs 2 ([ADR 0007](adr/0007-nfl-seeding-stakes.md)). The qualification term keeps its interval bounds. Each seeding term counts only its proven gain, `max(0, P(win).lower − P(loss).upper)`, and is added to both bounds; `P(seed ≤ 4)` sums the seed 1–4 intervals. Threshold seed cells therefore only lower the score. Seed cells that are missing, unparseable or disagree with **No Playoffs** by more than the eight cells' rounding are dropped: the forecast stays valid and the explanation says Stakes is qualification-only.

Do not multiply game-specific leverage by season urgency. NBA/NHL/MLB use an [estimated playoff swing](adr/0004-estimated-playoff-swing-stakes.md) in the same `80 × swing` model; the NFL never falls back to that estimate. Verified season progress remains required. On 2026-11-05 the owner removed seeding consequences from regular-season Stakes across NFL, NBA, NHL and MLB; on 2026-11-09 the published NFL seed scenarios above restored them for the NFL only. NBA, NHL and MLB still have no seeding term, and no league requires a seeding certificate. Conditional probability intervals remain intervals; a numeric result is permitted only when the combined qualification bounds agree at full precision. Unavailable inputs are never neutral defaults or reweighted components.

A conditional forecast can supply NFL qualification Stakes without a current Kalshi estimate; an unconditional Kalshi estimate cannot fill a missing conditional forecast. Trusted eliminated outcomes contribute zero. A trusted qualified outcome contributes only the seeding term of a valid conditional forecast with seed cells, and zero without one. Model endpoints never become confirmed outcomes. NFL preseason/postseason bands, Quality, Closeness and existing weights remain unchanged; the composite's quality-scaled Closeness comes from [ADR 0006](adr/0006-quality-scaled-pro-closeness.md).

Complement the **No Playoffs** column; seed cells add division title and No. 1 seed scenarios. Threshold percentages remain intervals: `<1%` is not zero and `>99%` is not one. Reject reversed numeric scenarios; bound overlapping intervals without inventing a midpoint.

Detail pages show away/home **Playoff chances by result**, **If win / If loss**, **Division title** and **No. 1 seed** win/loss rows when seed cells are usable, and explicit model/source credit. The Kalshi Playoff outlook stays separate. Valid individual scenarios/components remain visible when the total is unavailable; missing scenarios receive a team-specific explanation. Validation reasons, clocks and coverage IDs remain server-side.

## Quiet playoff markets

NFL qualification markets with no buyers and an ask of at most five cents follow the shared empty-bid rule in [playoff-odds.md](playoff-odds.md): they show the ask as an upper bound such as `<4%` and score half the ask. This replaced the 2026-11-05 display-only `<1%` convention for one- to two-cent asks on 2026-11-08. Qualification odds do not change conditional NFL Stakes, which come from the leverage forecast.

## Implemented source contract — not live conformance proof

The collector supports ordinary HTTPS pages on `www.playoffstatus.com/nfl/`:

- `nflschedule.html`: explicit season heading/week, 18 ordered weekly tables, 272 distinct contests and 32 recognized teams with 17 contests each. ET date/time columns and unambiguous source season are required. January/February use the following calendar year; ambiguous/nonexistent DST starts fail closed. Future TBD times are not invented for the target.
- Participating `TEAMwhatif.html` pages: exact team title (SF accepts only its `San Francisco 49ers What If?` or `San Francisco Forty-Niners What If?` identity), matching displayed publication/week token, correct Next Game opponent, one each of Win Next Game / Current Standings / Lose Next Game, correct W/L/T transitions and usable No Playoffs values. Seed 1–7 cells are optional and use the same percent/threshold format. Other scenario tables cannot fill missing next-game rows.
- The target must be both teams' next unplayed contest with exact roles/week. Exact kickoff agreement remains the default. On 2026-11-05 the owner approved a corroborated same-game exception for precise starts at most 30 minutes apart: the source must identify one contest on the same Eastern calendar date, and both ESPN team schedules must independently contain the actual canonical event ID, season, regular-season phase, explicit week, home/away roles, `timeValid=true`, identical canonical kickoff and unstarted zero-score status. The target must also be each team's earliest uncompleted ESPN event, not only the source's next contest. Missing copies/IDs, unknown times, different dates, later games and larger shifts remain unavailable; there is no SF-specific override or rounding tolerance beyond the exact 30-minute bound.
- All 32 existing ESPN season schedules corroborate every matchup's roles/week, both ESPN event copies and completed results in both directions. Completed-game kickoff times and scores must agree exactly. If a week is absent, the calendar date must corroborate it. An unrelated unplayed game's changed kickoff does not invalidate the whole batch: an affected target still requires either exact kickoff agreement or the approved corroborated same-game proof above. Neither provider's time is rewritten. ESPN remains authoritative for the exclusive scheduled/confirmed-start cutoff; an erroneous earlier source time does not move that cutoff forward. This never relabels a forecast or invents a kickoff. Missing identities, contradictory ESPN copies or newer completed evidence still invalidate coverage. The timezone-free visible label is only a consistency token, never an invented UTC timestamp.

## Clocks and collection

Observation age must be strictly under one hour. Schedule and team publications require actual UTC HTTP `Last-Modified`, strictly under seven days old and not future. HTTP `Date` is required, nonfuture and under one hour old. Team publication must be within ten minutes of the schedule publication, with the same visible generation/week token. Every score read rechecks current completed-result coverage.

The seven-day ceiling is a bounded publication policy, not an hourly update or freshness guarantee. Re-fetching unchanged content cannot renew its publication clock or fabricate result coverage.

The proposal joins the existing scraper-gated startup/15-minute pro competition refresh and seven-day upcoming horizon. Shared per-batch requests cap unique schedule/team URLs at 33. Page reads and scorers do not request providers; the legacy NHL/manual ratings path is not reused. Requests use an honest Nailbiter identity, 15-second deadline and 1 MiB body ceiling, with no automatic retries/redirects. A 401/403/429 stops the remaining batch. No denial bypass, proxy, private endpoint, paid service or outreach is authorized.

Transport/server failures retain prior evidence without renewing clocks. So does a source schedule that still lists, unscored at the same kickoff, a game ESPN already reports final: PlayoffStatus publishes results hours after ESPN, and treating that delay as a rejection revoked every later game's saved hype on each game day. The retained evidence no longer covers the new result, so current scoring stays strict and only the saved snapshot carries through as a completed-result coverage gap. A different score or kickoff is still a contradiction. Other successfully fetched unusable observations replace affected evidence with unavailable state. Cancellation cannot commit a partial competition refresh. These are implemented guards, not proof of sustained access or source rights.

## Storage, rollout and offline verification

`NflPlayoffLeverage` is additive evidence inside existing `games.pro_evidence_json`; no SQL migration or backfill is introduced. Exact-match version 1 records retain their existing serialized shape and evidence fingerprints. Corroborated version 2 records additionally retain the actual source kickoff, canonical ESPN kickoff, week and both target/next-game IDs in `CorroboratedKickoff`. Every selection revalidates that proof; the proof is bound to each conditional evidence revision and remains server-side through persistence, capture and retention. Age gaps may retain a complete supported tuple without renewing its clocks, but rejected/overwritten corroboration still revokes it across restart. Optional `Seeds` intervals are omitted when absent, so records without them keep their serialized shape and fingerprints; present seeds are part of each conditional evidence revision. NFL regular-season snapshots are `FormulaVersion = 5`. Legacy JSON without the field cannot supply the proposed new NFL qualification term until a legitimate ordinary refresh. A separately authorized future rollout must reconcile this availability consequence and source-use/operational evidence; this draft does not perform it.

The [synthetic fixture contract](../apps/backend.tests/Fixtures/NflPlayoffLeverage/README.md) uses four independently generated files rather than captured HTML or transcribed schedules. Tests exercise exact identities, result corroboration, malformed/duplicate/threshold cases, publication/observation expiry, request sharing, denial/cancellation, trusted outcomes, seed-cell scoring and fallback, ignored seeding metadata and all three actual page routes through isolated PostgreSQL and intercepted HTTP.

```sh
python scripts/test-fixtures/generate.py --check
python -m unittest discover -s tests/fixtures -v
mise exec -- dotnet test apps/backend.tests/backend.tests.csproj -c Release --filter FullyQualifiedName~NflPlayoffLeverage
mise exec -- dotnet test apps/backend.tests/backend.tests.csproj -c Release
mise exec -- dotnet test apps/backend.fsharp.tests/backend.fsharp.tests.fsproj -c Release
```

`NFL_LEVERAGE_ARTIFACT_DIR`, if used, must point outside the checkout for the controlled rendered page. It is not a running app or live data. Any temporary browser preview must use Portless/private Tailscale and owned cleanup. Passing tests does not prove current publisher fields, independent real schedule agreement, source accuracy, recurring access, public reuse rights or production delivery.
