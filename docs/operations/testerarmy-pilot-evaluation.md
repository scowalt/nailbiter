# TesterArmy local pilot evaluation

**Observed 2026-10-05:** 10/10 uncached navigation passes; separate recording and
9/9 warm navigation passes; all nine warm attempts met model-free replay criteria.
The unchanged AI acceptance test rejected the wrong visible link at the exact
href assertion, then passed in a fresh restored context. No measured slots were
unperformed, no terminal blockers, and owned cleanup completed for both groups.

[Reproduction and compatibility](testerarmy-pilot-local.md) ·
[Acceptance oracle/retry contract](../../tests/testerarmy/README.md) ·
[Public test/oracle contract](../../tests/testerarmy/README.md). Historical pilot specification/evaluation evidence does not authorize a new paid run.

## Scope, provenance and accounting

Coordinator executed the installed public CLI on source revision
`951e89d18422ce2d4ba3c419ab159096ef30152a`, with explicit dedicated ChatGPT
subscription model `gpt-6.1-sol` selected only in config. One desktop Chromium
Home → NBA → intended synthetic completed-game journey spans the real backend
and disposable PostgreSQL. No production data, paid fallback, CI rollout or
comparison benchmark.

Coordinator manually reviewed all 22 native-derived rows, assertions/failures,
cache and fixture evidence before delivering a whitelist projection. Documentation
review rechecked every row's status/duration/calls/tokens/cache/error/fixture fields
and assertion verdicts; only the following sanitized tables/metadata are published.
Retained ignored invocation roots (not committed artifacts):

- Cohorts: `.e2e/pilot/evaluation-c1fecde3-188f-427c-8e70-ec9cab8c01fa/`.
- Separate regression: `.e2e/pilot/evaluation-39d0990f-edd4-4b27-b722-9590847e756f/`.

Each attempt used a fresh native context at Home, one invocation/index 0, no
automatic failed-attempt rerun. Fresh-cookie evidence was true for all 22;
normal/restored mutation evidence was null. Passing journeys had all 20
deterministic assertions pass: independent visible card identity/href, exact game
URL, full team names, away 107/home 113 on their displayed sides and visible `Final`.
No normal attempt failed; native secondary/run errors were empty throughout.

Runner/test and patched SDK transport retries are zero. Normal within-attempt
recovery/OAuth refresh remains allowed. **Logical calls are SDK turns, not physical
inference HTTP counts; physical counts are UNAVAILABLE.** Actual tokens below
have per-step `provider` provenance (`chatgpt.responses`, `gpt-6.1-sol`), not adapter
upper bounds. No upper-bound estimate is included as actual usage. Warm provider/
token records are absent: usage is **UNAVAILABLE**, not zero despite zero logical
calls. No subscription dollar estimate.

## Measured normal cohorts: every attempt

Exactly ten cache-off attempts, then a separate physically empty read-write
recording, then nine read-only warm attempts; `strict:false`, stable identity/goals.
Durations are native attempt milliseconds, not fixture startup/total wall time.

Actual cache evidence keys (both navigation acts, not merely configured modes):

- **O**: configured `off`; both native steps have cache off. Reasons/action counts
  not applicable; no model-free replay claim.
- **R**: configured `read-write`; both steps `missed`, reason `no-entry`, native
  replayed/total counters **0/0 each**. These miss counters do not describe the
  subsequently finalized physical recordings.
- **S**: configured `read-only`; both steps `self-finalized`, **1/1 action each**,
  no reason reported. Zero logical calls plus these actual cache outcomes establish
  model-free replay; original recording fingerprint unchanged for every warm row.

All rows below: PASS, no failure, 20/20 assertions; no row omitted or rerun.

| Attempt | Status | Duration ms | Actual cache | Logical calls | Actual input | Actual output |
| --- | --- | --- | --- | --- | --- | --- |
| Uncached 1 | PASS | 14930 | O | 4 | 19152 | 169 |
| Uncached 2 | PASS | 12934 | O | 4 | 19144 | 165 |
| Uncached 3 | PASS | 15356 | O | 4 | 19146 | 166 |
| Uncached 4 | PASS | 16973 | O | 4 | 19156 | 179 |
| Uncached 5 | PASS | 13177 | O | 4 | 19162 | 185 |
| Uncached 6 | PASS | 14155 | O | 4 | 19150 | 168 |
| Uncached 7 | PASS | 14241 | O | 4 | 19150 | 169 |
| Uncached 8 | PASS | 19511 | O | 4 | 19148 | 168 |
| Uncached 9 | PASS | 15653 | O | 4 | 19154 | 174 |
| Uncached 10 | PASS | 15901 | O | 4 | 19150 | 169 |
| Recording 1 | PASS | 19329 | R | 4 | 19188 | 166 |
| Warm 1 | PASS | 1577 | S | 0 | UNAVAILABLE | UNAVAILABLE |
| Warm 2 | PASS | 1344 | S | 0 | UNAVAILABLE | UNAVAILABLE |
| Warm 3 | PASS | 1435 | S | 0 | UNAVAILABLE | UNAVAILABLE |
| Warm 4 | PASS | 1400 | S | 0 | UNAVAILABLE | UNAVAILABLE |
| Warm 5 | PASS | 1433 | S | 0 | UNAVAILABLE | UNAVAILABLE |
| Warm 6 | PASS | 1400 | S | 0 | UNAVAILABLE | UNAVAILABLE |
| Warm 7 | PASS | 1426 | S | 0 | UNAVAILABLE | UNAVAILABLE |
| Warm 8 | PASS | 1471 | S | 0 | UNAVAILABLE | UNAVAILABLE |
| Warm 9 | PASS | 1526 | S | 0 | UNAVAILABLE | UNAVAILABLE |

| Group | Planned / reported / passed | Failed / unperformed | Logical calls | Provider actual input / output | Native duration min / median / max ms |
| --- | --- | --- | --- | --- | --- |
| Uncached | 10 / 10 / 10 | 0 / 0 | 40 | 191512 / 1712 | 12934 / 15143 / 19511 |
| Recording | 1 / 1 / 1 | 0 / 0 | 4 | 19188 / 166 | 19329 / 19329 / 19329 |
| Warm | 9 / 9 / 9 | 0 / 0 | 0 | UNAVAILABLE / UNAVAILABLE | 1344 / 1433 / 1577 |

The eleven model-backed normal attempts total 210700 actual input / 1878 output
provider tokens, and all twenty total 44 logical calls. That token subtotal does
**not** impute missing warm usage or establish an all-twenty actual token total.
Observed native cache outcomes: recording two `missed/no-entry` steps; warm
18 `self-finalized` steps, 18/18 actions replayed, zero warm misses/hand-offs and
nine model-free attempts. Successful live fallback was permitted but not observed.

Recording began physically empty and finalized **two actual native `trace-1`
receipts**, one `tap` each. Inspection matched native test ID, desktop target,
stable goal digests, `/` → `/nba` and `/nba` → `/game/pilot-nba-completed`, with
neither trace truncated. Both-goals presence was verified, not inferred from PASS.
Recording fingerprint, unchanged in all nine read-only attempts:
`156ade54210dc0188b992f2c1c9e3866e4d49b16207aae9c9621fc92200aa1dd`.

## Separate unchanged-test wrong-link regression

Same canonical AI body/goals/oracle, cache **off**, not part of the twenty normal
slots. No fallback to a special failing test or direct deterministic navigation.

| Attempt | Native status | Duration ms | Actual cache | Logical calls | Actual input / output | Assertions |
| --- | --- | --- | --- | --- | --- | --- |
| Wrong link | FAILED (expected, exit 1) | 12343 | Off, first act only | 2 | 9277 / 69 | 4 pass, exact href failure |
| Restored, new context | PASS | 14073 | Off, both acts | 4 | 19150 / 170 | 20/20 pass |

The negative was `ASSERTION_FAILED`, `expect.toHaveAttribute` at
`tests/testerarmy/oracle.ts:26:22`. The locator independently found exactly **one
visible** PHO/PSO 107/113 card. Expected href `/game/pilot-nba-completed`; observed
`/game/pilot-nba-distraction`. First AI goal reached NBA; the hard assertion stopped
before the second act or opening the scoreboard. This is exact link rejection,
not a model/infrastructure failure or a model workaround.

Real live-sync DOM updates caused the owned mutation to apply ten times; all ten
original hrefs were the intended path, and the observed wrong href was one distinct
path. Teardown disconnected the observer, restored hrefs and reported no mutation
error. The next invocation used a **new fresh context**, mutation null, and passed.
Both `wrongLinkRejected` and `restoredInNewContext` evidence are true. No regression
slots unperformed; no secondary/run errors or remaining mutation.

## Unmeasured preflight and development evidence

These findings are **not** any of the twenty measured normal attempts:

- Earlier uncached live preflight on merged #639 passed in 17.280 s: four logical
  calls, provider-reported 19122 input / 167 output tokens. Approved dedicated login
  and account listing established `gpt-6.1-sol` availability; preflight established
  this journey's functionality then, not future quota or other model/page support.
- Subsequent cache-debug setup failed **before TesterArmy/model execution**:
  rootless PostgreSQL host port 38886 collided with an existing BB listener. No
  recording/warm attempt was produced. Three #640 helper setup collisions and a
  prior #639 collision were also retained, not erased by later success.
- Scoped #638 host-OS ephemeral allocation now applies only to owned disposable
  PostgreSQL/Ryuk, not Portless backend ports. Source and actual runtime bindings
  were verified. Releasing the reservation before Docker binds still permits a
  handoff race; failures remain visible with no automatic setup retry.
- Post-fix live cache-format debugging passed: recording 18.877 s/four logical
  calls; warm 1.401 s/zero logical calls. Inspection with the actual native report
  test ID confirmed both navigation `trace-1` entries (one `tap` each) and stable
  fingerprint. An earlier diagnostic omitted test ID and was not valid provenance
  evidence; it does **not** establish a cache failure. This one-record/one-warm
  check is separate from, not substituted for, the measured 1+9 above.

## Existing regressions and documentation checks

**Current executable source `951e89d`:** coordinator completed the following
validation and provided logs, independently read for this documentation update.
The documentation-only commits do not change executable behavior. These checks
are separate from measured subscription navigation, not extra AI cohort attempts.

| Current check | Observed result | External evidence log |
| --- | --- | --- |
| Backend `dotnet test` | 308/308, 0 failed/skipped; 3 m 2 s | `final-backend-tests.log` |
| F# `dotnet test` | 613/613, 0 failed/skipped; 12 s | `final-fsharp-tests.log` |
| Existing live-sync browser/client matrix | PASS | `final-live-sync.log` |
| Pilot config guards/list discovery | PASS; six guard rejections, three cache-mode discoveries | `final-config.log` |
| Pilot synthetic SDK transport paths | PASS driver; two expected native `MODEL_PROVIDER_FAILED` failures, one invocation per path | `final-transport.log` |
| Pilot deterministic oracle | PASS driver; valid/restored pass, 14 expected native assertion failures; owned cleanup verified | `final-oracle.log` |
| Pilot plan/typecheck/reporting smoke | PASS; exact plan and three fresh deterministic baseline/negative/restored contexts | `integration-640-reporting.log` (reporting/plan contracts); coordinator typecheck handoff |

Backend tests used only process-local `DOCKER_HOST` and
`TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE`, derived from the selected local Unix
Docker context; Ryuk stayed enabled. No CI/daemon edits. Config/transport checks
also compiled and started the actual backend fixture, with owned cleanup.
Synthetic SDK failures have no network inference, credentials or measured model
usage; deterministic reporting is not the actual AI regression proof above.
No broad frontend/widget build result is claimed.

**Historical baseline `90fd5a7`, retained separately:** backend build passed with
existing F# unnecessary-upcast warnings, F# 613/613 and live-sync passed. Initial
backend suite had **213 pass / 95 infrastructure failures** from rootless Ryuk
socket discovery; after both process-local endpoint/socket overrides, 308/308
passed. `baseline-backend-tests.log`, `baseline-backend-configured.log`,
`baseline-fsharp.log` and `baseline-browser.log` remain in the external handoff
directory. Neither current success nor the earlier configuration repair erases
those failures; they are not measured AI cohort results.

Documentation worktree passed frozen Bun install, pilot typecheck, config
list-only discovery (including fixture build/start/stop), exact20 `--plan`, existing
live-sync browser regression, local Markdown links and shell-example syntax.
Config smoke used owned empty external metadata, no real auth contents or
subscription calls. Full current backend/F# results above were coordinator-run,
not falsely attributed to the documentation agent.

## Actual hosting, review and cleanup

Both measured groups verified these **actual Portless-reported** endpoints against
listing and local/private HTTPS `/health`, with normal TLS (now stopped):

- Local: <https://636-testerarmy-integration.nba-browser-fixture.localhost>
- Private/browser: <https://devinabox.hair-salmon.ts.net:8451>

Portless allocated backend ports 4608 then 4969; these observations are **not**
manual port configuration for future runs. Chromium used trusted private HTTPS,
not the local CA it rejected or a TLS bypass. No public tunnel/manual Tailscale
Serve, guessed/common port, shared-route replacement or shared-proxy stop.
Both groups independently verified owned DB absence, route absence and temporary
manifest removal; final listing had no active routes. Other services untouched.

Public reporter/native `report-1` and physical `trace-1` evidence only, no private
runner API. Native raw reports, stdout/stderr, screenshots and cache recordings
remain ignored in their UUID roots; credentials remain external. Coordinator also
reported a read-only marker scan of 146 ignored artifact text files with no matching
JWT/Bearer/token/API-key-like markers; values were never printed and the credential
file was never read. That bounded scan is **not** general proof of redaction or a
substitute for independent manual whitelist review. No raw JSON/cache dump or
credential contents committed.

## Bounded recommendation

The clean local target was observed for **this** pinned desktop journey/account:
10/10 uncached, 1+9 cached navigation, nine model-free warm replays, exact wrong-link
failure and fresh restoration. Proceed only to a separately scoped local follow-up
if desired, preserving deterministic independent oracles and uncached vs replay
accounting. Do not expand automatically to CI, production, live data, more pages or
other models. This single-day sample does not establish long-term reliability,
future quota, dollar savings or superiority over another tool. The earlier setup
failures and remaining host-port handoff race still constrain reproducibility.
