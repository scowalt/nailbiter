# Isolated browser fixture (#638)

This is **deterministic setup smoke**, not TesterArmy/AI navigation or pilot evaluation.
It renders the real backend's Home, NBA listing and game routes against disposable PostgreSQL.
No production startup, schema, seeding endpoint or acceptance journey is changed.
See [maintainer setup/commands](../../docs/operations/testerarmy-pilot-local.md),
[test/oracle notes](../testerarmy/README.md) and
[reviewed evaluation](../../docs/operations/testerarmy-pilot-evaluation.md).

## Requirements / smoke

From the repository root: Docker with working Testcontainers/Ryuk, .NET 10.0.401
(`mise exec dotnet-legacy@10.0.401 -- dotnet`), Bun, and Portless with **private Tailscale**.

```sh
bun install --frozen-lockfile
bun tests/browser-fixture/setup-smoke.js
bun tests/browser-fixture/setup-failure-smoke.js
```

The existing pinned Playwright installation needs its Chromium browser installed.
The browser uses the reported **private Tailscale URL** with normal certificate
validation. On this host Chromium rejected the local Portless CA even though
curl/OS trust worked; no `ignoreHTTPSErrors` or manual exposure fallback is used.
The smoke automatically builds the adapter, starts it through
`portless run --name nba-browser-fixture --tailscale`, verifies both URLs against
`portless list` and `/health`, and stops its owned resources in `finally`.
No hard-coded backend port, public exposure, manual Tailscale configuration,
shared-proxy stop, `--force` or Docker-wide cleanup is used.

If Portless cannot start its proxy without a TTY, use its documented proxy startup
in an authorized terminal; do not substitute a tunnel or guessed port. Missing
private hosting or failing Docker/Ryuk stops setup. Neither is a passing smoke.

## Fixture lifecycle

```js
import { startFixture } from "./tests/browser-fixture/lifecycle.js";
const fixture = await startFixture();
try {
  // Use fixture.manifest.privateUrl as the browser base URL (browser-trusted TLS).
  // Keep fixture.manifest.intended/distraction as the expected identities.
} finally {
  await fixture.stop();
}
```

`startFixture()` resolves to `{ manifest, manifestPath, stop }` only after seed,
Kestrel startup and verified local/private hosting. `stop()` is asynchronous and
idempotent; always await it, including after browser/assertion failures. Startup
failure disposes the host/container and removes its owned route/temp manifest.
A failure to stop is reported, not hidden by broad cleanup. This process-local
lifecycle is not a persistent service: do not use SIGKILL or abandon its owner.

Manifest version 1:

- `localUrl`, `privateUrl`: actual Portless-provided, verified HTTPS URLs.
- `homePath`: `/`; `listingPath`: `/nba`.
- `listingDateEastern`: current Eastern calendar date at startup (`yyyy-MM-dd`).
- `intended`, `distraction`: `id`, `detailPath`, `awayTeam`, `awayAbbreviation`,
  `awayScore`, `homeTeam`, `homeAbbreviation`, `homeScore`, `status`.
- `databaseContainerId`: owned disposable container identity for cleanup evidence;
  **no connection string or database password** is published.

Stable fixture identities (synthetic NBA completed games):

| Game | Away | Away score | Home | Home score | Status |
| --- | --- | --- | --- | --- | --- |
| `pilot-nba-completed` | Pilot Harbor Otters (PHO) | 107 | Pilot Summit Owls (PSO) | 113 | final |
| `pilot-nba-distraction` | Pilot Canyon Foxes (PCF) | 98 | Pilot Meadow Cranes (PMC) | 101 | final |

Both are seeded at noon on the current Eastern date, with the current NBA season
ending year (October–December belong to the next year). Noon avoids DST-boundary
ambiguity. They appear on the unmodified default `/nba` route, not an invented
listing or redirected page. Restart the short-lived fixture if a run crosses
Eastern midnight; the manifest records its listing date. No injected fake clock
or production clock refactor is used. Current NBA cards display abbreviations;
locate them by exact visible PHO/PSO or PCF/PMC text descendants, independently of
href. The detail scoreboard shows the full team names. The navbar's accessible
NBA link name includes its logo alt text as well as its text label.

## Isolation / implementation

`apps/backend.browser-fixture` is a test-only executable using the existing
`backend.tests/AppFactory` techniques: `WebApplicationFactory<Program>`, worker
removal, replacement EF options, `EnsureCreated`, additive migration replay and Testcontainers PostgreSQL
(`postgres:16-alpine`). It never accepts a connection string. It requires a local
Unix Docker endpoint and sets **process-local**
`TestcontainersSettings.DockerSocketOverride` to that resolved endpoint's socket
path. This avoids the rootless provider's rootful Ryuk-mount default on this host;
Ryuk remains enabled. No Docker daemon or global configuration is changed. After `EnsureCreated` and before seeding/host startup, the integrated fixture applies both `034_PlayoffSeriesObservation.sql` and `035_ProHypeLifecycle.sql`, installing the real writer journal/fence triggers for scheduled hype reads. Lifecycle 035 is the renamed undeployed script; the incoming series 034 remains unchanged. This uses only the fixture's newly created disposable database and does not invoke a production migration runner.

PostgreSQL publishes 5432 on a host port that Docker allocates from
61000-65535. Docker's allocator never gives two containers the same port, and
the host OS never hands out ports above its ephemeral range (Linux default
32768-60999) to other processes. Rootless dockerd's port allocator cannot see
host-side listeners, and RootlessKit does not retry when the host port is
taken, so a Docker-allocated port inside the ephemeral range can collide with
any process that got its listening port from the OS. Selecting the port on the
host and handing it to Docker does not help either. The selection is released
before Docker binds it, so a parallel test or another container can take it in
between (issue #83). `IsolatedPostgreSql` in `backend.tests` uses the same range.

Ryuk gets its host port through the public `ResourceReaperPublicHostPort`
callback: although obsolete, the
[pinned 4.15.0 constructor](https://github.com/testcontainers/testcontainers-dotnet/blob/4.15.0/src/Testcontainers/Containers/ResourceReaper.cs)
still invokes it in `WithPortBinding`; actual container bindings were runtime-verified.
The callback returns a single port, so Ryuk cannot use a Docker-allocated range.
`DisposableHostPorts.SelectFree` probes the same 61000-65535 range instead. It
starts at a random port and returns the first one it can bind with a dual-stack
socket. The port is released before Docker binds it, but Linux with its default
ephemeral range never assigns ports in that range, so only Docker allocating it
to another container in the gap, or a process that binds it by number, can take
it (issue #102). The random start makes it unlikely that concurrent test
processes choose the same port.
`DisposableHostPorts.cs` lives in `backend.tests` and this fixture compiles it
through a linked `Compile` item, so both use one selection and one range.
[The Ryuk stress probe](../../docs/testing/ryuk-host-port-stress.md) measures
the race. Only this obsolete assignment is warning-suppressed; its pinned-version
rationale stays here in Markdown, not source comments. Revalidate this behavior
on any Testcontainers upgrade.

This is fixture-only allocation, not a generic port framework or a startup
retry. A setup failure is reported and never silently rerun. Portless's
actual backend `PORT` and exposure remain completely unchanged. No common/fixed
host port, daemon/socket/proxy change, Ryuk disablement, HTTP exposure fallback,
or other-service termination is used. Passing setup smoke does not erase earlier
setup/debug failures or establish AI navigation/acceptance success.

[.NET 10 factory source](https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Mvc/Mvc.Testing/src/WebApplicationFactory.cs)
confirms `UseKestrel` is supported and must be called before factory initialization.
This adapter uses its Kestrel-options overload to listen on **loopback at the
Portless-assigned `PORT`**, preserving the production entry point. The fixture
builds/seeds before `host.Start()` and serves actual backend static assets.

The launcher passes an allowlisted environment. The adapter independently clears
inherited configuration **before invoking backend Program**, creates an empty
temporary content root, and sets disabled monitoring/scraper configuration and
the freshly created disposable connection. Late factory configuration alone is
not sufficient for Program's early configuration/monitoring reads. Production
workers are also removed before startup and backend HttpClient requests are denied
(including accidental calls to manual scrape/refresh routes). Backend Sentry and
OpenTelemetry stay off. This module makes no OAuth/model calls and has no
TesterArmy dependencies. The next ticket separately owns runner telemetry/auth.

Temporary manifests live outside the checkout and are removed on stop. Credentials
always stay outside the repository. The pinned TesterArmy runner requires output
under its project root, so its raw logs/artifacts/recordings/cache are allowed only
under explicitly ignored `/.e2e/pilot/`, never committed. Manually review whitelist
summaries before publishing sanitized evidence.

## Opt-in MLB highlights supplement (#653)

`bun tests/browser-fixture/highlights-acceptance.js` starts the same fixture with
`startFixture({ highlights: true })`. Default NBA setup remains unchanged. This is
deterministic Playwright acceptance, not an AI navigation/paid evaluation.

The opt-in fixture adds synthetic MLB games with chart/no-chart playable selections,
an initially pending selection, closed/suppressed/known-unusable contexts, and
scheduled/live/canceled/postponed/suspended/post-false/missing-evidence exclusions.
It replaces time and only the two highlights named HTTP clients with controlled
fake upstreams; all other backend HTTP remains denied. Persisted choices come from
real lifecycle/application operations, not injected page markup or seeded highlights
rows. The existing NBA data, real routes and static assets remain intact.

The optional `manifest.highlights` adds completed/hidden game IDs and a local
`controlPath`. `fixture.control(command)` connects to a Unix socket inside the owned
external temporary directory (socket mode 0600). It is not an HTTP route or a
production service. Fixed test commands inspect state, temporarily toggle gates/key
or invoke discovery while a final page is open. `discover` changes fake uploads,
advances controlled backend time 15 minutes and invokes the actual lifecycle.
Browser assertions require genuine reload to reveal that persisted choice.

All third-party browser traffic is intercepted, including nocookie iframe and direct
no-JS fallback navigation. No real provider playback is claimed. The runner scans
HTML/assets/browser URLs/manifest and complete captured host logs, including shutdown,
for its fake sentinel. Optional `HIGHLIGHTS_ARTIFACT_DIR=/absolute/external/path` saves
only a synthetic pre-load section screenshot and whitelist report outside the repo.
Owned Unix IPC is removed alongside the manifest/container/route in finally and
verified independently; normal private TLS and both endpoint checks remain required.

See [feature operations](../../docs/operations/mlb-highlights-local.md) and the
[parent acceptance matrix](../../docs/testing/mlb-highlights-acceptance.md).

## Opt-in grouped-home scenario

Implements the [canonical grouped-home specification](https://github.com/scowalt/nailbiter/issues/10).

Use `startFixture({ groupedHome: true })`,
`bun tests/browser-fixture/grouped-home-smoke.js`,
`bun tests/browser-fixture/grouped-home-live.js` and
`bun tests/browser-fixture/grouped-home-acceptance.js`. See the
[grouped-home data matrix, Unix controls and acceptance coverage](grouped-home.md).
Default NBA and MLB highlights scenarios remain unchanged.
## Opt-in playoff series footer supplement (#687)

```sh
SERIES_ARTIFACT_DIR=/absolute/external/artifact-directory \
  bun tests/browser-fixture/series-acceptance.js
```

This uses `startFixture({ series: true })` and the same real Program, routes,
static assets, disposable PostgreSQL, owned Unix IPC and private Portless lifecycle.
It does not enable highlights or alter the default NBA fixture. Supplements are
mutually exclusive. No production route, synthetic control, provider activation,
series patch contract or alternate layout is added.

The fixture runs ordinary ESPN collection and persistence against the captured
`mlb-pregame-401907973.json` board and schedules, then renders the real home page.
The original scheduled MLB card retains the captured known Hype 89 source path.
Clearly synthetic variants add a live MLB leader, an opening 0–0 MLB standing,
a completed NBA series, a long NHL round with Hype unavailable, and a regular-season
baseline. Existing ranks, seeds and live odds are test inputs, not injected markup,
mocked rendering helpers or directly seeded series snapshots. A controlled backend
clock uses 2026-10-01; these are deterministic layout scenarios, not actual schedules.
The completed variant is NBA so it cannot falsely invalidate the MLB captured Hype
context's complete-result coverage. All unconfigured backend HTTP is denied.

The optional `manifest.series` contains `cards`, `live` and an owned `controlPath`.
`inspect` returns only upstream request counts and usable fixture identities.
`publish` invokes the existing actual `IGameUpdateService.PublishUpdateAsync` with
changed live scores, probability, clock and Excitement. It does not inject browser
HTML or modify SSE, GraphQL, GameUpdate or series contracts. The browser must process
the production Datastar event with its production client; a version mismatch fails
acceptance rather than being worked around with a fixture client or fake patch.

The first real publish exposed an inherited compatibility defect: the beta.11
client consumed `datastar-merge-fragments`, while the existing .NET server emitted
`datastar-patch-elements`. The authorized prerequisite correction pins the official
stable `v1.0.4/bundles/datastar.js` and replaces the three existing home/league/World
Cup `data-on-load` initializers with stable `data-init`. `@get` is unchanged; the
mobile menu is native onclick, and detail pages retain native page-sync plus
the existing nonfinal game stream. Home/league/World Cup subscriptions use cleanup
cancellation. No SSE event, route or payload contract changes. See the official
[installation guide](https://data-star.dev/guide/getting_started),
[attributes reference](https://data-star.dev/reference/attributes#data-init) and
[SSE reference](https://data-star.dev/reference/sse_events#datastar-patch-elements).
The runner verifies actual publishes on home and the MLB league, and confirms the
live detail's page-sync request path; run `bun run test:live-sync` for its separate
snapshot/component/terminal lifecycle regression coverage.

The script captures one 1440/390/320px desktop/phone batch to the required external
artifact directory and writes a whitelist `report.json`, including cleanup evidence
on failure. It checks no-JS initial HTML, Hype/Excitement, seeds and game-score
preservation for live/final games and scheduled score omission, text bounds, no
horizontal overflow, sparkline placement, keyboard Tab/Enter whole-card navigation
and exactly one unchanged standing after a real score/probability/Excitement patch.
Series context is preserved inside the approved grouped-home layout, with one
status-specific metric target per card. Screenshots are visual evidence, not an
automated similarity oracle. Run at most one batched fix and confirmation round,
not an open-ended screenshot loop.

Independent `/sse/page-sync` whole-page polling is held for initial layout and
patch-only checks to isolate the preapproved Datastar fragment seam. Home/league
patches now retain team/seed rows and use stable scalar score targets; series
context remains outside those targets. A separate real snapshot-enabled context
asserts three successful grouped-page refreshes retain scheduled/live/final series
context without new provider calls. This is not reconnect/failure coverage; use
the grouped-home live suite for that matrix.

Only the existing pinned Datastar module and ESPN team-logo static CDN assets are
replayed from their real bytes fetched with curl and normal certificate validation;
there is no TLS bypass, client replacement, provider API call or synthetic JS patch.
All application assets still come from real production paths. Browser traffic to
other external origins is rejected. This is not live provider accuracy, deployment,
real playback or model evaluation evidence. `finally` closes every browser context,
stops only the owned fixture and independently verifies container, route, manifest
and Unix control removal; the shared proxy and other projects remain running.

## Setup failure / cleanup checks

`verifyStoppedFixture()` independently checks the owned PostgreSQL container,
server route and manifest without stopping resources itself. The normal smoke
confirms that verification rejects a running fixture, then succeeds after stop
and a second idempotent stop. The failure smoke exercises a real post-start filesystem
publication error (the manifest destination is an existing directory), then checks
exit code 1, no ready publication, removal of its own database and Ryuk, and removal
of its own route. It inspects only the captured owned container identities; it never
runs a Docker prune or shared-proxy stop. Ryuk can take a few seconds to self-remove
after disconnect. The CLI catches setup errors after disposal and reports only the
exception type, avoiding a core dump or credential-bearing exception details.

These setup checks require no model/auth and do not substitute for #639's separate
AI journey, #640's cohorts/regression experiment or #641's evaluation.
