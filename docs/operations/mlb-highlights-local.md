# MLB highlights: local acceptance and operational boundary

This is a disabled-by-default, best-effort completed-MLB detail-page feature. This document is not launch permission. **Production key creation/use, actual project allocation, production migrations, deployment, and the one-time launch cohort each require separate authorization.** No production configuration or provider access was exercised by local acceptance.

## Configuration

Both explicit gates default off in `apps/backend/appsettings.json`:

| Server setting | Default | Meaning |
| --- | --- | --- |
| `Highlights:YouTube:Enabled` | `false` | Supported API adapter and operator validation gate |
| `Highlights:Worker:Enabled` | `false` | Automatic discovery/revalidation gate |
| `Highlights:YouTube:ApiKey` | absent | Server-only key; never source, browser configuration, command argument, URL or logs |
| `Highlights:YouTube:DailyUnitLimit` | `1000` | Ceiling 0–1000 non-search units/day; reduce to the actual authorized allocation |
| `Highlights:YouTube:MaxUploadPages` | `2` | 1–4 pages/operation, at most 200 videos |
| `Highlights:Worker:MaxGamesPerCycle` | `100` | 1–100 games/batch |
| `Highlights:Worker:MaxCatchupPages` | `16` | 1–16 pages/fixed cohort batch, at least the adapter page bound |
| `Highlights:Worker:AuthorizeLaunchCatchup` | `false` | Separate one-time initialization authorization, not a restart backfill |

Environment equivalents use double underscores, e.g. `Highlights__Worker__Enabled`.
Do not enable these against a real provider as a local smoke. The isolated fixture
supplies its own non-live sentinel and denies unexpected upstream HTTP; it never
accepts a database connection string or production key from the launcher.

Both gates, valid credentials and positive bounded budget are required for automatic
work. Missing/disabled/invalid configuration does not promise active searching.
Disabling discovery does not immediately remove fresh valid choices. Local retention
still runs while discovery is disabled. The worker is independent of scraper
activation; do not rely on `EnableScraper=false` to disable it.

## Production secret forwarding

The normal deployment runs Docker Compose through Doppler (`nailbiter` / `prd`).
Compose forwards these settings only to the backend container:

| Doppler variable | Backend environment | Unset default |
| --- | --- | --- |
| `HIGHLIGHTS_YOUTUBE_ENABLED` | `Highlights__YouTube__Enabled` | `false` |
| `HIGHLIGHTS_YOUTUBE_API_KEY` | `Highlights__YouTube__ApiKey` | empty |
| `HIGHLIGHTS_YOUTUBE_DAILY_UNIT_LIMIT` | `Highlights__YouTube__DailyUnitLimit` | `1000` |
| `HIGHLIGHTS_YOUTUBE_MAX_UPLOAD_PAGES` | `Highlights__YouTube__MaxUploadPages` | `2` |
| `HIGHLIGHTS_WORKER_ENABLED` | `Highlights__Worker__Enabled` | `false` |
| `HIGHLIGHTS_WORKER_MAX_GAMES_PER_CYCLE` | `Highlights__Worker__MaxGamesPerCycle` | `100` |
| `HIGHLIGHTS_WORKER_MAX_CATCHUP_PAGES` | `Highlights__Worker__MaxCatchupPages` | `16` |
| `HIGHLIGHTS_WORKER_AUTHORIZE_LAUNCH_CATCHUP` | `Highlights__Worker__AuthorizeLaunchCatchup` | `false` |

For an authorized rollout, enable YouTube Data API v3 in the chosen Google Cloud
project and create a dedicated API key restricted to that API and the VPS's
verified outbound IP address. A Cloudflare-proxied public hostname's IP is not
necessarily the VPS egress address. Public metadata needs an API key, not OAuth.
Confirm the project's available allocation before setting the daily ceiling to at
most 1000, or a lower allocation. Keep launch catch-up false unless separately
authorized.

Collect/store the key through a secure form or the approved secret manager, never
chat, source, command arguments, browser configuration or logs. Do not print a
production `docker compose config`, container environment or Doppler secret dump:
rendered configuration includes the key. The offline Compose tests use synthetic
values and do not start containers or contact YouTube.

After secure credential/quota setup, set both enabled flags true, deploy through
the normal reviewed workflow and verify the lifecycle's safe reason/count logs
and public health. The lifecycle verifies the official channel and accounts all
attempted API units; do not use an unaccounted standalone provider probe. Success
with zero selections can be a legitimate conservative coverage result. To stop
automatic discovery, set `HIGHLIGHTS_WORKER_ENABLED=false` and redeploy; local
retention and independent operator decisions remain intact.

## Conservative identity and playability

Only MLB's immutable main-channel identity `UCoLrcjPV5PbUrUyXq5mjc_A` is approved
and verified through the supported API. Shared channel uploads plus batched video
validation are used, not searches per game/visit, scraping or paid discovery.

The ESPN event/competition must explicitly be actually final. Stored `final` alone
is insufficient; post/completed=false, contradictory postponed/suspended/canceled
evidence, missing evidence, or persisted MLB noncompletion fails closed. Both
canonical team identities, calendar-year season and exact scheduled start must agree
with exactly one authoritative MLB schedule contest. The MLB `officialDate`, not
upload time, UTC day or visitor timezone, is the matching date. No nearest-time guess.

The first release supports a deliberately narrow 2–30 minute single-game recap title:
`AwayNickname vs. HomeNickname [Game ]Highlights (M/D/YY|M/D/YYYY|YYYY-MM-DD)`,
optionally followed by ` | MLB Highlights`. Conflicting metadata and isolated plays,
commentary, multi-game roundups or full replays are excluded, even from MLB.

Automatic special-contest matching needs a unique explicit matching `gamePk` in
supported video metadata. Generic Game 2, display names and upload proximity cannot
resolve doubleheaders, same-day repeats, makeup/resumption context or ambiguous
identity. Official videos may not supply this metadata; those games remain
unselected/manual-only. Schedule corrections can reduce coverage. Do not broaden
publishers, formats, historical bounds or identity rules to meet a coverage target.

Removed/private/non-embeddable/globally blocked videos are not offered as playable.
Partial country restrictions remain eligible with disclosure; the direct fallback
does not bypass them. Made for Kids **true or unknown is excluded** in this first
release because no compliant differentiated child-directed embedding path was
implemented. This is an operational exclusion, not a region/source-invalid claim
or legal-compliance certification.

## Non-public operator workflow

In a separately authorized, server-configured environment, from the backend content
root (so the normal application configuration is loaded):

```sh
cd apps/backend

dotnet run -- --highlights inspect GAME_ID
dotnet run -- --highlights attach GAME_ID VIDEO_ID
dotnet run -- --highlights attach GAME_ID VIDEO_ID --apply
dotnet run -- --highlights replace GAME_ID VIDEO_ID
dotnet run -- --highlights replace GAME_ID VIDEO_ID --apply
dotnet run -- --highlights remove GAME_ID
dotnet run -- --highlights remove GAME_ID --apply
dotnet run -- --highlights resume GAME_ID
dotnet run -- --highlights resume GAME_ID --apply
```

These examples are syntax, not permission to contact production. Supply secrets by
the existing authorized server-side mechanism, never by copying them into commands.
Use disabled monitoring for disposable local command-entry checks. The command
exits before starting HTTP hosting. There is no highlights admin page or correction
route. Exit 0 is accepted; exit 2 is rejection/invalid usage. Inspect is read-only;
changes are dry runs unless `--apply` is explicit. Check accepted/applied/reason and
inspection output before applying. Dry runs preserve game decisions but video
validation consumes and durably accounts API quota.

Attach requires no current decision; replace is explicit for an existing decision,
including a manual choice with deleted provider payload. Manual identification may
resolve special-contest ambiguity but still cannot bypass actual completion,
publisher, teams/date/season, single-game content or playability checks. The same
video cannot identify two contests. Late manual attachment is permitted.

Manual ownership survives discovery/restarts and definitive invalidity leaves it
chosen but unavailable. Remove clears the choice/payload and independently persists
suppression. Attach while suppressed does not resume automation. Resume only clears
suppression; it does not reset the original deadline. Provider-data expiry may leave
manual ownership without a video ID; restore fresh metadata by deliberate replace,
not by silently reenabling automatic selection.

## Cadence, cohort, quota, freshness

- Shared discovery is best effort every 15 minutes. The hosted operation wakes each
  minute, persists attempts before work and has no internal provider retry loop.
- New actual completion observations in normal collection create the original 72-hour
  window. Refresh/restart/failure/remove/resume never renew it. Legacy finals newly
  reobserved for catch-up do not acquire a new normal retry window.
- Authorized launch atomically persists one fixed preceding-seven-day set of already
  existing final MLB IDs (defensive cap 10,000). Actual completion is still required.
  No games are imported. Checkpoints/page budgets survive restart; later imports
  cannot join. Whole adapter operations round down to the remaining page budget.
- Due selections are validated daily in batches of at most 50 IDs, at most 200 per
  operation; interrupted attempts retain at least 15-minute retry spacing. First valid
  automatic choice stays chosen. Automatic recovery is only inside its original
  window; manual choices are never silently replaced.
- Worker/operator share persisted Pacific-day quota, reserve conservative attempted
  units before requests and refund only returned unused units on the original day.
  Crash/cancellation retains reservations; rollover accounts in-flight work safely.
  Honor lower allocations; quota exhaustion/backoff never extends a game window.
- Backoff starts at 15 minutes, grows to six hours, and honors longer provider
  Retry-After/quota-day limits. Transient failure is not deletion. Safe reasons/counts
  are observable through inspection and worker logs, not raw URLs/errors/credentials.
- Refresh or delete non-authorized provider payload within 30 calendar days. Local
  maintenance deletes overdue IDs/titles/channel/evidence/regions/Made-for-Kids and
  playlist/cursor payload even with missing credentials or prolonged outage. Ownership,
  suppression, deadlines and non-provider cohort progress survive. A stopped app must
  perform overdue deletion before resumed provider work; this is not a guarantee
  that an offline process runs maintenance.

See [application identity/operator details](../mlb-highlights-application.md) and
[lifecycle/accounting details](../mlb-highlights-lifecycle.md).

## Deterministic local acceptance

Prerequisites: the pinned .NET/Bun versions, working Docker/Testcontainers/Ryuk,
Portless/private Tailscale, normal private TLS and installed Playwright Chromium.
Missing infrastructure is a blocker, never a passing result.

```sh
export PATH=/home/scowalt/.local/share/mise/installs/dotnet-legacy/10.0.401:/home/scowalt/.local/share/mise/installs/bun/1.4.2/bin:$PATH
export DOTNET_ROOT=/home/scowalt/.local/share/mise/installs/dotnet-legacy/10.0.401
unset TESTCONTAINERS_RYUK_DISABLED
bun install --frozen-lockfile
(cd apps/web-components && bun install --frozen-lockfile && bun run build)
dotnet test apps/backend.tests
dotnet test apps/backend.fsharp.tests
dotnet build apps/backend -c Release
bun run lint
bun run test:live-sync
bun tests/browser-fixture/highlights-acceptance.js
bun tests/browser-fixture/setup-smoke.js
bun tests/browser-fixture/setup-failure-smoke.js
uv run --no-project --python 3.14 --with-requirements .comment-policy/requirements.txt python .comment-policy/check.py
```

The browser test extends the existing isolated fixture, using actual game routes,
persisted application/lifecycle selections and controlled time. Its owned local
Unix IPC invokes test-only operations while a final page stays open. It is not an
HTTP correction endpoint or a production registration. NBA mode remains the default.
All browser-accessible hosts run through Portless/private Tailscale; both reported
URLs are verified, and only owned routes/containers/manifests/IPC are cleaned in
`finally`. Never stop the shared proxy or use a public tunnel/TLS bypass.

All third-party browser requests are intercepted. Iframes/fallback navigation use
local synthetic responses, not real YouTube/Google, player APIs, playback, keys,
quota or paid evaluations. Optional `HIGHLIGHTS_ARTIFACT_DIR=/absolute/external/path`
saves a synthetic pre-load section screenshot and whitelist JSON report outside
the checkout; inspect before sharing. Acceptance establishes browser DOM/network
contracts and fallback behavior, **not real playback or universal coverage**.

See the [acceptance matrix](../testing/mlb-highlights-acceptance.md) for public-seam
coverage and actual migration checks. Prior failed fixture runs remain evidence;
clean post-fix runs do not retroactively make them pass.
