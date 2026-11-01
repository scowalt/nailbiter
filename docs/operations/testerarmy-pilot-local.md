# Local TesterArmy navigation pilot

Local-only reproduction for [#636](https://github.com/scowalt/nailbiter-app/issues/636).
Read the [public test/oracle contract](../../tests/testerarmy/README.md),
[reviewed evaluation](testerarmy-pilot-evaluation.md),
[oracle/retry notes](../../tests/testerarmy/README.md) and
[fixture lifecycle](../../tests/browser-fixture/README.md).
This is one desktop Home → NBA → synthetic completed-game journey through the
real backend and disposable PostgreSQL, not production access or a CI rollout.

## Requirements and compatibility

Run commands from this checkout's **repository root**. Do not use the README's
ordinary development database, production configuration, Doppler or deployment
commands for this pilot.

| Component | Pinned / exercised version |
| --- | --- |
| TesterArmy `e2e` | 0.17.0, with tracked Bun patch |
| Web engine `@e2e-dev/web` | 0.12.0 |
| AI SDK `ai` / `@ai-sdk/openai` | 7.0.127 / 4.0.83 |
| Playwright / Chromium installer | 1.63.0 (matches existing browser regressions) |
| TypeScript / Node types | 5.9.3 / 24.10.0 |
| Node / Bun | exercised 24.20.0 / 1.3.11 |
| .NET | 10.0.401 via `mise exec dotnet-legacy@10.0.401 -- dotnet` |
| Testcontainers | 4.15.0; PostgreSQL image `postgres:16-alpine` (tag, not digest pin) |
| Portless | exercised 0.15.4 with private Tailscale |
| Docker | working local Unix endpoint and Ryuk; exercised rootless Docker 29.8.x |

The published framework/web packages require Node ≥22.12.0. The installed CLI
runs under **Node**, even when Bun starts the script; Bun installs the exact
lockfile and patch. Current upstream main is not the pinned npm release.

```sh
bun install --frozen-lockfile
bun run typecheck:pilot
```

If Chromium/system dependencies are missing, use the installed pinned engine:

```sh
env -i PATH="$PATH" HOME="$HOME" USER="${USER:-}" TMPDIR="${TMPDIR:-/tmp}" \
  XDG_CONFIG_HOME="$HOME/.config/nailbiter-testerarmy-pilot" \
  E2E_TELEMETRY_DISABLED=1 node_modules/.bin/e2e-web install chromium --with-deps
```

`patches/e2e@0.17.0.patch` changes the shared SDK transport-retry constant and its
declaration from 5 to 0 for both navigation and judgment paths. Runner/test
retries are also zero. It does **not** disable normal within-attempt recovery or
OAuth refresh/resend; logical calls are not physical HTTP counts. After changing
any pin, revalidate published exports/config, `report-1`/`trace-1`, browser engine,
patch application, both synthetic transport failure paths and authorized live
preflight before measuring a new cohort. Revalidate Testcontainers' obsolete but
actually invoked Ryuk port callback against both source and runtime; host-port
reservation release still has a handoff race. No automatic setup retry is added.

## Dedicated login: approved maintainer/coordinator only

Implementing/documenting agents must **not** execute this section or the live
commands below. Obtain explicit human approval for a new dedicated TesterArmy
device authorization. Do not inspect/copy coding-agent credentials, reuse another
login, sign up for an account, or authorize a paid API fallback. Keep this root
outside every checkout; never print credential contents.

```sh
# Only after approval; umask applies to new auth files, not to other projects.
umask 077
mkdir -p "$HOME/.config/nailbiter-testerarmy-pilot"
chmod 700 "$HOME/.config/nailbiter-testerarmy-pilot"

# Empty environment isolates paid keys and excludes E2E_OAUTH_CREDENTIALS entirely.
env -i PATH="$PATH" HOME="$HOME" USER="${USER:-}" TMPDIR="${TMPDIR:-/tmp}" \
  XDG_CONFIG_HOME="$HOME/.config/nailbiter-testerarmy-pilot" \
  E2E_TELEMETRY_DISABLED=1 node_modules/.bin/e2e login openai --device

# After successful human approval; account listing is not an inference call.
env -i PATH="$PATH" HOME="$HOME" USER="${USER:-}" TMPDIR="${TMPDIR:-/tmp}" \
  XDG_CONFIG_HOME="$HOME/.config/nailbiter-testerarmy-pilot" \
  E2E_TELEMETRY_DISABLED=1 node_modules/.bin/e2e models openai
```

The dedicated regular file is
`$HOME/.config/nailbiter-testerarmy-pilot/e2e/oauth.json`, mode **0600**;
directory mode **0700**. Verify metadata only, never contents. Missing/unsafe
metadata, foreign XDG root, or `E2E_OAUTH_CREDENTIALS` present even empty stops
configuration. Telemetry opt-out must accompany **every** framework CLI invocation,
including login, models, browser install, discovery and runs.

The coordinator's approved account listing served `gpt-6.1-sol`; an uncached live
preflight verified this account/model on this journey (see evaluation). Neither
listing nor that single preflight guarantees future quota/model availability or
other tasks. Recheck your account and supply its verified ID explicitly through
`NAILBITER_PILOT_MODEL`. Only `e2e.pilot.config.ts` constructs `chatgpt(modelId)`;
there is no default model, alternate provider layer or paid fallback.

## Non-model checks and fixture ownership

These commands need no subscription inference or real credential contents:

```sh
bun run pilot:evaluate --plan       # exact 20 slots; no fixture/auth/model access
bun run test:pilot:config           # owned empty external metadata, CLI list only
bun tests/browser-fixture/setup-smoke.js
bun tests/browser-fixture/setup-failure-smoke.js
bun run test:pilot:oracle           # 2 pass + 14 expected assertion failures
bun run test:pilot:transport        # synthetic SDK failures, no network model
bun run test:pilot:reporting        # deterministic real browser + synthetic contracts
bun run test:live-sync              # existing mocked-page browser regression
```

Oracle/transport drivers return zero only when the intentionally failing native
tests have the expected verdicts; they are not failed measured navigation attempts.
Reporting smoke is **not** the live unchanged-test regression or measured cache
proof. Config smoke creates/removes its own empty external auth placeholders;
it never touches the coordinator's actual credentials.

The scripts start and stop their own test-only adapter. For a custom local caller:

```js
import { startFixture } from "./tests/browser-fixture/lifecycle.js";
const fixture = await startFixture(); // { manifest, manifestPath, stop }
try {
  // Actual local/private HTTPS URLs: fixture.manifest.localUrl/privateUrl.
  // Runner input: NAILBITER_PILOT_MANIFEST = fixture.manifestPath.
  // Browser uses privateUrl with normal TLS validation.
} finally {
  await fixture.stop(); // asynchronous, idempotent, owned resources only
}
```

Lifecycle runs `portless run --name nba-browser-fixture --tailscale ...`, lets
Portless allocate the backend port, then checks **both reported URLs** using
`portless list` and `/health`. Do not guess URLs or common ports. Local Chromium
CA trust failed on the evaluated host, so the browser uses trusted private HTTPS,
not TLS bypass. If Docker, Portless, Tailscale or normal TLS is unavailable, stop.
Never use a public tunnel, manual Tailscale Serve, `--force`, another app's route,
shared-proxy stop, Docker prune or unrelated container cleanup. The fixture uses
only synthetic data, disables ingestion/monitoring and does not accept production
connection strings. Eastern-midnight crossover stops a measured invocation;
a newly started fixture is a new evaluation, not a replacement of its evidence.

## Authorized live execution

Only the coordinator runs subscription evaluation. Each invocation creates a new
UUID artifact/cache root; **never** overwrite/resume an earlier invocation:

```sh
# Replace only with an ID verified for the approved dedicated account.
env -u E2E_OAUTH_CREDENTIALS NAILBITER_PILOT_MODEL=gpt-6.1-sol \
  bun run pilot:evaluate --preflight   # separate UNMEASURED attempt

env -u E2E_OAUTH_CREDENTIALS NAILBITER_PILOT_MODEL=gpt-6.1-sol \
  bun run pilot:evaluate --cohorts     # exactly 10 off, then 1 recording + 9 warm

env -u E2E_OAUTH_CREDENTIALS NAILBITER_PILOT_MODEL=gpt-6.1-sol \
  bun run pilot:evaluate --regression  # separate wrong-link then fresh restoration
```

The orchestrator allowlists OS variables for its child CLI, sets dedicated XDG
root and `E2E_TELEMETRY_DISABLED=1`, strips paid keys/provider overrides, supplies
its owned manifest and explicit cache/scenario inputs, and calls the installed
public CLI with `--retries 0 --workers 1`. No implicit preflight in `--cohorts`.
Every attempt opens Home in a fresh native context. Cache is off for the ten
uncached attempts; recording starts physically empty in `read-write`; nine warm
attempts share it `read-only`, `strict:false`, and verify its fingerprint unchanged.
Navigation fallback can pass, but live model use is not model-free replay.

An ordinary failed attempt remains in its scheduled slot; later scheduled slots
are independent, not reruns. Terminal model/auth/quota, hosting, fixture, report
contract or cleanup blockers stop and list unperformed slots. Preserve that
invocation before any separately approved investigation. `state:complete` means
execution/report completeness, not an all-passing pilot. Regression uses the same
acceptance body with a context-scoped wrong-href mutation and guaranteed teardown;
only the exact deterministic href failure proves detection. Restoration is a new
invocation/context with mutation disabled, or explicitly unperformed if blocked.

## Review and cleanup

The printed `.e2e/pilot/evaluation-<UUID>/summary.json` is a **reviewable whitelist,
not automatically publishable**. Manually review every field before deriving a
sanitized report: retain durations/status/failures, actual cache modes/reasons and
counts, provider-provenance tokens only, unavailable fields and unperformed slots.
A model-free claim requires both navigation cache steps `self-finalized` **and**
zero logical calls. Upper-bound estimates are separate, not actual tokens; missing
usage is `UNAVAILABLE`, not zero. Physical inference HTTP counts are unavailable.
Use public reporters and native `report-1`/`trace-1`, never a private runner API.

Pinned output must be under the config's project root, so raw artifacts and cache
are allowed **only in explicitly ignored** `/.e2e/pilot/`; credentials stay external.

```sh
git check-ignore -v .e2e/pilot/raw/report.json .e2e/pilot/cache/entry.json
git ls-files .e2e                    # must be empty
```

Finally cleanup stops only the owned host/DB/route and removes its temporary
manifest; it retains UUID evidence/cache roots for review and checks DB/route
absence. SIGINT/SIGTERM follows owned cleanup; do not SIGKILL the owner. No
persistent service is requested. Do not broadly remove artifacts or credentials;
after review, any manual deletion must identify only your owned invocation root.

Existing suite/build reproduction (separate from model evaluation):

```sh
mise exec dotnet-legacy@10.0.401 -- dotnet build apps/backend/backend.csproj
mise exec dotnet-legacy@10.0.401 -- dotnet test apps/backend.fsharp.tests/backend.fsharp.tests.fsproj
# Derive these process-local values from YOUR actual selected Unix Docker context.
endpoint=$(docker context inspect "$(docker context show)" --format '{{.Endpoints.docker.Host}}')
case "$endpoint" in unix://*) ;; *) echo 'STOP: local Unix Docker endpoint required'; exit 1;; esac
DOCKER_HOST="$endpoint" TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE="${endpoint#unix://}" \
  mise exec dotnet-legacy@10.0.401 -- dotnet test apps/backend.tests/backend.tests.csproj
(cd apps/web-components && bun install --frozen-lockfile && bun run build)
```

The backend test-suite socket overrides address this host's rootless Ryuk discovery
mismatch; leave the reaper enabled and global Docker settings unchanged. Results
and earlier setup failures belong in the evaluation, not in claims about CI,
production, long-term reliability, comparative superiority or subscription dollars.
