# Listing performance check

The listing check requests the home and league listing pages, checks status and
page content, and reports time to first byte (TTFB) and response completion
against an explicit budget. It has two modes:

- **Fixture mode** runs the real app in process against a disposable
  TimescaleDB with a synthetic, production-shaped workload. It also checks the
  database work each page does. Its query-work rules are deterministic and are
  the hard gate. Both budget options are still required. When only the
  query-work rules matter, set the budgets high, as the automated tests do.
- **Explicit-URL mode** sends a small, capped number of GET requests to an app
  that is already running. It has no database code and no provider code.

## Run it

Fixture mode needs Docker and the `timescale/timescaledb` image pinned in
`docker-compose.yml`:

```sh
dotnet run --project apps/backend.tests -c Release -- fixture \
  --samples 20 --warmup 3 --ttfb-budget-ms 1500 --completion-budget-ms 2500 \
  --report listing-fixture.json
```

Explicit-URL mode:

```sh
dotnet run --project apps/listing-check -c Release -- url \
  --base-url http://127.0.0.1:5093 --path / --path '/nhl?date=2026-10-13' \
  --samples 3 --warmup 1 --ttfb-budget-ms 1500 --completion-budget-ms 2500 \
  --report listing-url.json
```

The normal `backend.tests` suite runs both modes against loopback servers:
`ListingFixtureCheckTests` runs fixture mode and then URL mode against the
fixture app, and `ListingCheckTests` drives URL mode against an in-process stub
that produces each failure class.

## Options

| Option | Modes | Rule |
|---|---|---|
| `--ttfb-budget-ms` | both | Required. 1 to 60000, and not more than the completion budget. |
| `--completion-budget-ms` | both | Required. 1 to 60000. |
| `--request-timeout-ms` | both | Per-request deadline. Default 10000. At least the completion budget, at most 60000. |
| `--samples` | both | Measured rounds. URL mode 1 to 5 (default 3). Fixture mode 1 to 100 (default 10). |
| `--warmup` | both | Unmeasured rounds before the measured ones. URL mode 0 to 2 (default 1). Fixture mode 0 to 20 (default 2). |
| `--report` | both | Path for the JSON report. Without it, only the text summary is printed. |
| `--base-url` | URL | Required. An `http` or `https` origin with no credentials, path, query or fragment. |
| `--path` | URL | Up to 6 distinct listing paths: `/`, `/nba`, `/nfl`, `/nhl`, `/mlb` or `/cfb`, each with an optional `?date=YYYY-MM-DD`. Default `/`. |
| `--allow-remote-host` | URL | Required for any host that is not loopback. Without it, the run fails as `invalid-config` and sends nothing. |

A round requests every path once, in order, one request at a time. URL mode
therefore sends at most `(2 + 5) × 6 = 42` requests. The client sends GET only,
does not follow redirects, sends no cookies and has no option for headers or
credentials. Fixture mode owns its paths, so `--path`, `--base-url` and
`--allow-remote-host` are invalid there.

## Failure classes

The outcome is the most severe failure class present. The exit code identifies
it.

| Exit | Outcome | Meaning |
|---|---|---|
| 0 | `pass` | Every request succeeded, every content rule held, and every page's median TTFB and completion are within budget. |
| 1 | — | Unexpected error in the checker. A stack trace is printed to stderr. |
| 2 | `invalid-config` | The options broke a rule above. No request is sent, no container is started and no report is written. |
| 3 | `request` | Transport error, non-2xx status, an unfollowed redirect, or a content type other than `text/html`. |
| 4 | `deadline` | The response did not complete within `--request-timeout-ms`. |
| 5 | `content` | A 2xx HTML page failed a content rule. |
| 6 | `query-work` | Fixture mode only. A page broke a deterministic database-work rule. |
| 7 | `budget` | All requests succeeded, but a page's measured median TTFB or completion exceeded its budget. |
| 130 | — | Interrupted by Ctrl-C (SIGINT) or SIGTERM before the report was written. One `cancelled:` line is printed to stderr and no report is written. Fixture mode still removes its container. |

If the interrupt arrives after the report was written, the run keeps the
report's exit code; if cleanup then fails, it says so on stderr.

SIGTERM is always graceful, however many copies arrive. `dotnet run` forwards
SIGTERM to the app, so one SIGTERM to its process group (how service managers
and CI stop a job) reaches the app twice. Use SIGKILL to stop a run that will
not finish cleanup.

Under `dotnet run`, its own exit code does not show how the app ended. For
one graceful SIGTERM it can exit 0, 130 or 143, depending on when its own copy
of the signal arrives. In SDK 10.0.401, `dotnet` handles SIGTERM with
`Environment.Exit(0)`, and its process reaper replaces that 0 with the app's
exit code only while it still waits for the app. A copy handled after the app
has exited gives 0, and one that arrives after `dotnet` has removed its SIGTERM
handler gives 143, the default action. The single
`cancelled:` line shows the app stopped gracefully. Running the built app
directly (`dotnet apps/listing-check/bin/Release/net10.0/listing-check.dll`)
reports the app's own 130.

A second Ctrl-C (SIGINT) ends the process at once without waiting for cleanup.
A terminal sends each Ctrl-C to the foreground process group once and
`dotnet run` does not forward SIGINT, so the app sees one SIGINT per press.
The Testcontainers reaper (Ryuk) then removes the fixture container, usually
within about 15 seconds.

Content rules in both modes: the body starts with `<!DOCTYPE html>`, contains
`</html>` (so a cut-off body fails), and carries the page marker (`home-schedule page` for
home, `league-title` for a league) plus either a listing (`home-section`,
`league-games`) or the empty state (`empty-state-text`). A valid empty listing
passes.

Fixture mode adds rules from the workload manifest: the page links exactly the
expected games, each live or final game shows its expected excitement score,
and each scheduled game shows its latest pregame probability and "Hype
unavailable".

## Fixture workload and controls

`ListingWorkload` builds the same data on every run:

- The clock is fixed at `2026-10-13T23:30:00Z` (19:30 in New York) through
  `HostedGameFixture`'s `FakeTimeProvider`. League paths pass `?date=` because
  the league route reads the system clock for its default date.
- 28 earlier days each hold 24 final games across `nhl`, `nba`, `mlb` and `nfl`
  with 180 one-minute snapshots. These unrelated histories fill 28 daily
  chunks.
- Today has 17 listed games: `nhl` and `nba` each with 2 final, 2 live and 3
  scheduled games, and `mlb` with one of each. Each has 36 pregame snapshots
  two hours apart, so its history crosses several chunks. Final and live games
  add one snapshot per played minute. Each scheduled game's newest pregame
  snapshot has a distinct probability, so a stale latest-odds row shows a wrong
  percentage.
- Snapshots are loaded with binary `COPY`. Chunks older than seven days are
  then compressed and the database is analyzed. The run has 29 chunks, 21 of
  them compressed, and about 122,600 snapshots.

Schema: `SharedPostgreSql` builds the EF Core template, then
`ListingWorkload.PrepareTemplateAsync` turns `odds_snapshots` into a hypertable
the way `apps/backend/Migrations/001_InitialSchema.sql` does: one-day chunks,
the `(id, timestamp)` primary key, `idx_odds_game_time`, and compression
segmented by `game_id`. The automatic compression policy is replaced by the
explicit compression step.

Controls:

| Source of noise | Control |
|---|---|
| Clock | `FakeTimeProvider`; nothing advances it. |
| App background workers | `AppFactory` removes every hosted service from the F# services assembly plus the college football and highlights workers, and turns off scraping, pro-hype capture, Sentry and OpenTelemetry. |
| TimescaleDB jobs | The server runs with `timescaledb.max_background_workers=0` and telemetry off. The report reads both settings back from the server. |
| Provider requests | Every `IHttpClientFactory` client gets a handler that rejects the request. The report counts attempts, and any attempt is a `query-work` failure. |
| Container | Named `nailbiter-listing-check-<guid>` and disposed in a `finally` block on success and failure. If the process is killed, the Testcontainers reaper (Ryuk) removes it. |

## Query-work rules

After the timed rounds, fixture mode requests each path once more with an EF
Core command interceptor recording SQL and parameters:

- Exactly one latest-odds read (an `odds_snapshots` query without
  `is_historical_import`).
- `EXPLAIN` of that read names each relation once. A second scan of the odds
  history, through a self-join, subquery or repeated union, names the same
  chunks twice.
- That plan also names the chunk that holds the newest snapshot of each
  listed game. A plan with no relation names, for example a read moved into a
  function that `EXPLAIN` cannot see into, fails this rule instead of passing.
- Exactly one excitement history read when the page has live or final games,
  and none otherwise. Its game ids are exactly the page's live and final games:
  no scheduled game, and none missing.
- No provider request was attempted.

The latest-odds read looks up each listed game's newest snapshot time in the
`newest_odds` table, which a trigger from `042_NewestOdds.sql` keeps current on
every insert, update and delete of `odds_snapshots`. It then reads
`odds_snapshots` at those times only, so TimescaleDB plans just the chunks that
hold the listed games' newest snapshots, however long the history is.
`LatestOddsHistoryTests` checks that the planned chunks are exactly those chunks
for 7-day and 56-day histories. The report shows the counts (`relationScans`,
`chunkScans`) so a change in that number is visible in review. The excitement
history read still scans every chunk.

The trigger locks the game's `newest_odds` row on every insert, update and
delete, and holds that lock until the transaction commits. Two consequences
follow:

- Concurrent changes to one game are safe. A delete or move locks the row
  before it compares the deleted time with the pointer, so it waits for the
  other transaction and then reads its committed rows.
  `Concurrent_odds_changes_to_one_game_keep_its_newest_odds` pins four such
  interleavings, and it fails if the trigger drops `FOR UPDATE`.
- Writers that run at the same time must insert or delete odds for their games
  in one consistent order, for example ordered by game id. Two transactions
  that change games A and B in opposite orders now deadlock, and PostgreSQL
  aborts one of them.

The app's own writers meet the rule. The live odds loop scrapes one sport and
date at a time, and the pregame loop that runs beside it writes
`pregame_odds`, not snapshots. The college collector and
`--import-college-history` share one advisory lock and write only college
games, which no other writer changes.

The NFL backfill (`BACKFILL_MODE=true`) keeps this rule between games. It
still has one lock-order conflict with the live loop, so run the backfill
while the live loop does not write the same games. The backfill runs as a
separate process, and nothing stops it from running while the deployed
backend's live loop writes.

The backfill opens a new `DbContext` for each game and saves that game's rows
in one transaction. EF Core writes the game row before that game's pregame
odds and Kalshi snapshots, so the foreign key holds. Each backfill transaction
locks the `newest_odds` row of one game at most, so it cannot lock two games
in the opposite order to another writer. A failed save rolls back that game's
rows alone. Its context is then discarded, so the next game's save carries
only that game's rows. `Backfill_writes_each_game_in_its_own_transaction` and
`Backfill_failed_game_save_leaks_no_rows_into_the_next_game` check both
against TimescaleDB, in skip mode and in update mode.

The conflict that remains involves one game and the fence from migration
`035_ProHypeLifecycle.sql`. Every write to `games` or `pregame_odds` locks row
1 of `pro_hype_revision`. In update mode (`SkipExisting=false`), when the
backfill leaves a game's row unchanged, it inserts that game's snapshots
before its pregame odds. So it locks the game's `newest_odds` row before the
fence. A live save that updates a game row locks the fence first and then the
`newest_odds` rows of its snapshots. If both write the same game at the same
time, PostgreSQL aborts one transaction with `40P01`. The backfill reports the
failed game at Error and rolls back its rows, and a rerun repairs it.
[#178](https://github.com/scowalt/nailbiter/issues/178) tracks the fix.

Any other parallel or multi-game writer, including a manual script such as
`scripts/backfills/recover_empty_odds.py`, must also write its games in one
consistent order.

## Report

Text output is a table with one row per path: measured and warmup counts,
median and maximum TTFB and completion, budget state and failure count. The
failures and an `outcome` line follow, with the run time and the load average
before and after.

The JSON report (schema `nailbiter.listing-check/1`) has these fields:

| Field | Content |
|---|---|
| `schema`, `mode`, `outcome`, `exitCode` | Report version, `url` or `fixture`, result. |
| `startedAtUtc`, `durationMs` | When the run started and how long it took. |
| `target` | The origin (`scheme://host:port`) in URL mode, or `in-process fixture (loopback)`. |
| `config` | Samples, warmup, budgets, timeout and paths. |
| `host` | 1-minute load average before and after, and the processor count. |
| `pages` | Per path: measured count, `ttfb` and `completion` min, median and max in ms, and `withinBudget`. Only measured, successful samples count. |
| `samples` | Every request: path, `warmup` or `measured`, round, status, TTFB, completion, bytes, failure class and detail. |
| `failures` | Class, path and message for each failure. |
| `fixture` | `null` in URL mode. Otherwise `workload` (image, TimescaleDB version, telemetry level and background workers as read from the server, chunk counts, games by status, snapshot counts, clock), `controls`, `startup` phase times in ms, and `queryWork` per path. |

The report never contains request or response headers, cookies, credentials or
page text. Content failures name the broken rule and at most an element id.
Fixture data is synthetic, so a fixture report can be kept as an artifact. A
URL-mode report holds only the origin, paths, status codes, sizes and timings.

## Timing limits on a shared host

The development host is shared, and its load average ranges from about 10 to
120. TTFB and completion include scheduler delay, so a number from a loaded
host is an upper bound on the app's work, not a measurement of it.

- Compare timings only between runs on the same host, close together in time,
  with similar load averages. Use `scripts/listing-test-timing/measure.py` for
  an interleaved before-and-after comparison.
- Use medians, not maximums. Report the load average with every number.
- Keep budgets in automated tests generous. The query-work rules, not timing,
  decide whether a change adds database work.
- Fixture timings use a small synthetic database on a local container. They do
  not predict production latency.

## Use after a deployment (#8)

Issue #8 runs URL mode against the deployed live app after a release. This
repository does not run that check yet, and nothing here has requested a
production host. The intended call, after #8's own readiness wait, uses the
live app origin from `docs/deployment.md`; #8 confirms which origin serves the
listing pages:

```sh
dotnet run --project apps/listing-check -c Release -- url \
  --base-url https://live.nailbiter.app --allow-remote-host \
  --path / --path /nhl --samples 3 --warmup 1 \
  --ttfb-budget-ms <budget> --completion-budget-ms <budget> \
  --report listing-postdeploy.json
```

- Readiness and revision checks belong to #8. The checker does not poll
  `/health` and does not know which revision is deployed. Wrap the call so the
  report is stored with the deployed revision and image digest.
- Map exit codes to #8's failures: 3 and 4 are request failures, 5 is wrong
  content, and 7 is an exceeded budget. Readiness expiry and revision mismatch
  come from #8's wrapper.
- Without `?date=`, league pages show the server's current day, which may
  legitimately be empty. The content rules accept an empty listing.
- The report has no secrets, so the whole file can be kept as the release
  record.
- `--allow-remote-host` is required for a production host. Do not add it in
  tests or local runs.
