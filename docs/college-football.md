# College football

College support is implemented in the ASP.NET Core / F# / Falco / Datastar application. It is not a GraphQL or SolidJS integration. The public contract is described here and in the [pregame evidence decision](adr/0001-college-pregame-kalshi-evidence.md); historical requirements records remain private archival evidence.

## Data boundary

- `/cfb` groups games by season and ESPN week, with Previous Week / Next Week links like NFL. Deep links use `/cfb?week=3&season=2026&type=2`; every game in the selected group appears, across all its dates. The default selects a live or nearest upcoming game's week, rather than jumping to the last game in the lookahead. Games also appear on the homepage and `/game/{id}`, with the normal SSE and chart components. No CFP bracket or seeds are rendered.
- Week numbers come from ESPN scoreboard objects or scalar summary headers, never an NFL date formula. ESPN's first regular-season group is labeled `Week 1 (includes Week 0)` because it combines the early games with the opening weekend; an explicit provider week zero is also a valid filter. The provider groups bowls and CFP games together, so postseason is labeled `Postseason (Bowls & CFP)`, not regular-season Week 1. January games keep the prior season's start year. Previous/next links stay within the selected season and season type, as on NFL.
- Existing current-season games without week metadata, including completed games outside the rolling collection window, are refreshed by event ID on collection. This fills the existing week columns without a schema migration, a new history import, or guessed date-based assignments.
- Both an ESPN season-specific FBS identity and a Kalshi game listing are required. ESPN team IDs, not a hard-coded abbreviation roster or today's scoreboard group membership, establish FBS eligibility. FBS–FCS is included; FCS-only is not.
- `CollegeEspnClient` validates the requested season and FBS group on the fully paginated FPI roster. An FPI value need not be present to establish membership. FPI probability columns are **never** imported as CFP probabilities.
- ESPN scoreboards use `groups=80&limit=100`. The paginated core event index supplies missing event IDs, resolved through summary headers. Discovery is cached for 15 minutes; active scores are not. ESPN's explicit empty-date index (`count=0`, `pageIndex=0`, `pageCount=0`, `items=[]`) is valid on the first requested page; it must not abort collection of other dates. Missing/inconsistent metadata and partial nonempty indexes still fail validation. Known absent nonfinal events are queried by ID. Absence never means cancellation.
- `CollegeKalshiClient` matches whole event/outcome identities using college-specific aliases and exact team names. It accepts long codes, either neutral-site ordering, and finalized listings. Ambiguous matches are rejected. The matched home/away tickers and ESPN team IDs are persisted; a changed kickoff date does not rematch the market.
- A listing is not a price. Ordinary live probabilities require active, unexpired, two-sided orderbooks with positive depth and a spread at most 20 percentage points. A **near-certain outcome bound** is the explicit exception: a fresh, active pair with a winner bid ≥99¢ and opposing ask ≤1¢, positive depth on those supporting sides, uncrossed quotes, and at least one terminal/empty opposing side displays `>99%` / `<1%`. Confirmed complementary YES/NO resolutions also supply these bounds, but only binary $1 contracts with explicit full/zero settlement values; voids, fair-price settlements, and contradictory results do not qualify. ESPN still owns game status and scores. Stale last trades and arbitrary illiquid books never supply bounds. A failed quote refresh clears the current probability display without discarding prior chart samples or blocking score/clock updates. Quotes expire after two minutes without successful collection.
- Bounds use internal representatives `0.995` / `0.005` for chart geometry and excitement calculations, not exact observed probabilities. Their distinct `Source` is persisted, and bounds expire from current displays after two minutes across sports without removing historical chart points. SSR chart data and public odds APIs expose `isProbabilityBound`; chart tooltips show bounds and their evidence source, never `99.5%`. The same fallback policy applies to NFL, NBA, NHL, MLB, and World Cup advancement. World Cup regulation requires all three home/draw/away contracts: every opposing outcome must support the extreme winner, or all three resolutions must agree on one winner. For three-way chart/scoring geometry only, each losing outcome receives half of the `0.005` residual; both still display `<1%`. Race-winner markets and playoff qualification policies are unchanged.

## Card presentation

AP Top 25 positions appear as superscripts next to team abbreviations, with AP-specific accessible labels and tooltips. Cards use compact `CFP <1%`-style qualification labels. FPI values belong in the detail comparison, not repeated team-context lines on cards. Regular-season captions are omitted; CFP, conference championship, bowl, and historical-import notices remain.

Missing live-probability warnings appear only during games in progress, including SSE updates. College card score updates target individual score spans, preserving AP ranks, qualification labels, and each page's layout. College league cards stack their matchup and scheduling sections at small widths.

## Strength, polls, qualification, and hype

FPI is numerical predictive strength. AP badges come only from poll ID 1 with matching season metadata; ESPN `curatedRank` can refer to another poll and is not used. The roster/poll refresh runs every six hours. Source observations older than eight days are not copied into current games. Game-level observations retain the evidence shown during collection; imported finals never receive today's FPI or AP positions.

CFP qualification uses season-verified Kalshi markets with `KXNCAAFPLAYOFF-{start year}` and matching `2026-27`-style subtitle. Its canonical team set comes from the verified season roster. Qualification aliases include `MIZ → MIZZ`, `SC → SCAR`, `NCSU → NCST`, and `M-OH → MOH`, in addition to the other college mappings. Northwestern's qualification market uses `NW`, mapped to the verified roster's `NU` independently of game-winner ticker matching. An unmapped qualification market blocks no-market assumptions for the entire roster; known aliases must be resolved without relaxing that completeness check.

For current-season FBS teams confirmed to have no qualification market, use **`<1%`**, per the user's 2026-09-17 policy change. The assumption is separately sourced as `cfp_no_market`, never `kalshi`, and uses an internal representative value of `0.005` for scoring rather than displaying a falsely precise 0.5%. Following the subsequent card-cleanup request, percentages have no explanatory suffix; the detail source reads `Nailbiter estimate` rather than `Kalshi`. This explicit assumption can satisfy the qualification input to pregame hype.

Absence must be established by a verified season event, a verified FBS roster, complete live/archive discovery, and recognized team identities. Unknown market codes, failed/incomplete requests, FCS opponents, and past-season refreshes do not create assumptions. A listed but unusable quote stays unavailable. Verified final qualification outcomes take precedence; a newly listed market replaces an assumption, even if its quote is unusable. Both market estimates and no-market assumptions expire after an hour without fresh evidence. No national-championship or ESPN qualification probabilities are substituted. No schema migration or historical import is needed; the next qualification refresh applies this policy.

College hype has three required components:

- **Stakes:** explicit CFP elimination round = 100; explicit conference championship = 80; verified ordinary bowl = 0 CFP qualification boost. Regular-season stakes require both qualification probabilities and increase with season progress. Championship-winning probabilities are not inputs.
- **Quality:** harmonic mean of two FPI strengths, normalized with `1 / (1 + exp(-FPI / 15))`. This smooth scale does not clamp legitimate college ratings at the NFL +/-20 bounds. Missing FCS FPI leaves quality unavailable.
- **Closeness:** prefer usable ESPN pregame moneylines, at most 24 hours old. If the current ESPN response has no usable pair, use a valid Kalshi two-sided game-winner probability or the existing depth-backed near-certain outcome bound. Both Kalshi fallbacks expire after **two minutes** without successful collection; failures never refresh the observation timestamp. Source season, kickoff and home/away identities must match the game. Resolved/fair-price settlements and arbitrary empty or illiquid books do not supply pregame closeness.

The user approved this college-only pregame exception on **2026-10-10** after investigating Stanford–Notre Dame's `OFF` moneylines. Bounds retain their distinct source and `>99%` / `<1%` labels; internal `0.995` / `0.005` representatives produce 1/100 closeness, not an exact observed probability. Kalshi evidence stores **null moneylines**, not manufactured American prices, and is never appended to live chart/excitement samples. The detail page omits nonexistent prices, explains bound-derived closeness, and hides expired probabilities while preserving other available components. An old ticker from a different game date is conservatively rejected for pregame use, rather than relabeled for the new kickoff without verified contract applicability. Existing live ticker preservation is unchanged.

The composite is 45% stakes, 35% quality, 20% closeness, only when all three are known. No neutral defaults or partial-score reweighting are used. Known contextual stakes do not require unrelated CFP probability coverage. Each available individual component remains visible.

Classification is intentionally conservative: explicit CFP round notes take precedence over bowl branding. Ordinary bowls require postseason season metadata, ESPN bowl competition type 42, and a bowl annotation outside the six CFP-rotation bowl names. A generic postseason flag, bowl name, or unannotated CFP-rotation bowl remains **unknown**, not maximum stakes. ESPN sometimes supplies only generic type `STD`; those games retain unavailable stakes until adequate classification evidence is supplied. Conference competition flags alone are not evidence of a championship. Raw classification notes are retained for audit.

## Initial historical import

**No deployment or production import was performed as part of implementation.** Applying the migration or running against production requires separate authorization.

1. Apply `apps/backend/Migrations/027_CollegeFootball.sql` and `032_CollegePregameProbabilities.sql` before starting the updated collector. The latter makes pregame moneylines nullable while preserving existing prices; both migrations are repeatable. Normal repository database migration procedures still apply. Do not roll back to a binary that requires non-null moneylines while Kalshi probability-only rows remain; review/remove those rows first under the normal database change workflow.
2. Point `ConnectionStrings__DefaultConnection` at an explicitly chosen **isolated local database** for initial validation. Never copy production secrets into commands, logs, or fixtures.
3. Preview without starting an HTTP server or background workers:

   ```bash
   dotnet run --project apps/backend -- --import-college-history
   ```

4. Only after reviewing the target and window, commit the same import:

   ```bash
   dotnet run --project apps/backend -- --import-college-history --apply
   ```

There is no arbitrary season/date/interval option and no public import endpoint. The first successful college collection/import persists one `college_imports` checkpoint for the database: first import time and the preceding 14 days, clamped to ESPN's regular-season calendar start. A preseason import has an empty retrospective window. Retries always use the persisted window, even after the calendar advances. A dry run does not persist a checkpoint or any data; its proposed window will therefore move until the first actual write.

The importer accepts only completed, eligible, listed games in that window. A PostgreSQL transaction advisory lock serializes college imports/collection; game IDs and historical observation timestamps make retries idempotent. An additional partial unique index protects imported observation identity. It never appends candles to an already live-collected game.

Historical lines come exclusively from ESPN's explicitly labeled `close` fields, including available closing spreads and totals. `FetchedAt` is the retrieval time, **not** a fabricated quote time. History comes from one-minute Kalshi trade closes in both contracts. The chart includes only complete minutes inside ESPN's observed first/last play wallclock boundaries, positive trade volume, and nonterminal prices. Without those play bounds or usable candles, the chart remains absent. No pregame/postgame or settlement moves are added.

The adapter consults `/historical/cutoff` for candle routing and retries the other endpoint on a 404. Recently settled markets remain on standard endpoints. Archived listing pagination uses the supported series filter (not an ignored date filter), with a 50-page safety cap; event matching and storage still enforce the fixed import window. Failure to complete required discovery aborts the transaction rather than claiming completeness.

Imported games have `IsHistoricalImport`; candle samples have `IsHistorical` and an explicit source. SSR, API, and excitement queries preserve that distinction. Historical charts do not produce comparable hype or excitement scores. Current ratings and final CFP outcomes are not back-projected as pregame evidence.

## Verification

`CollegeFootballTests.cs` exercises PostgreSQL persistence, provider-shaped HTTP fixtures, collection, the dry-run/import path, migration repeatability, SSR pages, SSE fragments, and score exclusion. `CollegePolicyTests.cs` covers alias/name matching, long/neutral-site codes, quote quality, classification and overtime. Existing F# and backend tests provide other-sport regression coverage.

```bash
dotnet build apps/backend/backend.csproj --configuration Release
dotnet test apps/backend.fsharp.tests/backend.fsharp.tests.fsproj --configuration Release
dotnet test apps/backend.tests/backend.tests.csproj --configuration Release
```

On a rootless Docker machine, Testcontainers' cleanup container must mount the same socket as the daemon. Set `DOCKER_HOST` and `TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE` to that machine's actual rootless socket for the test process; do not disable cleanup or change the shared daemon. Tests use in-process ASP.NET TestServer, not a browser-accessible development server.
