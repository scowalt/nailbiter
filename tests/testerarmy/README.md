# TesterArmy local navigation pilot

One desktop Home → NBA → intended synthetic completed-game acceptance test against
the real backend and disposable PostgreSQL. See:

- [Maintainer setup, commands, compatibility and cleanup](../../docs/operations/testerarmy-pilot-local.md)
- [Reviewed evaluation and bounded recommendation](../../docs/operations/testerarmy-pilot-evaluation.md)
- [Fixture lifecycle/isolation](../browser-fixture/README.md)
- [Approved spec](../../docs/plans/2026-10-05-001-feat-testerarmy-e2e-pilot-spec.md)

Provider/model selection is exclusively `e2e.pilot.config.ts`:
`chatgpt(NAILBITER_PILOT_MODEL)`, with an explicit coordinator-verified account ID,
no default, generic provider layer or paid API fallback. Dedicated login and all
subscription preflight/cohort/regression calls belong to the approved coordinator,
not implementing agents. Config/discovery/type/transport/oracle/reporting checks
are non-model evidence and do not establish AI navigation reliability.

Authored pilot source, fixture scripts, configuration and dependency-patch additions
follow the [comment-free source policy](../../.comment-policy/README.md). Keep
implementation rationale in Markdown and behavior in names, types and tests;
do not add prose comments or weaken the policy with exclusions.

## Quick non-model verification

From the repository root:

```sh
bun install --frozen-lockfile
bun run typecheck:pilot
bun run pilot:evaluate --plan
bun run test:pilot:config
bun run test:pilot:oracle
bun run test:pilot:transport
bun run test:pilot:reporting
```

Scripts allowlist child environments, opt out of framework telemetry on every
CLI call, reject `E2E_OAUTH_CREDENTIALS` even empty, and stop their owned fixture in
`finally`. Config smoke uses/removes empty owned external metadata placeholders,
not real credentials; discovery via CLI list is not auth verification. Transport
smoke uses a synthetic failing SDK model without network access, not an acceptance
fallback. Native oracle smoke intentionally has two valid/restored passes and 14
expected assertion failures; drivers return zero only after validating verdicts,
matcher provenance, fresh state and owned cleanup. The native CLI exits 1 for the
intentional oracle failures and 3 for synthetic provider failures; the drivers
assert those codes rather than ignoring failures. Reporting smoke validates the
real-browser mutation/report contract and separately synthetic accounting/cache
contracts, not measured live regression or replay.

## Exact independent oracle

The unchanged `acceptance.e2e.ts` has two stable AI navigation goals. Each attempt
starts with `app.open()` at Home in a fresh native context at 1440×900. App/project,
test path/title, ordinary parameters and goals stay stable across cache modes.

After the first act, the oracle verifies exact NBA URL, uniquely identifies one
visible PHO/PSO 107/113 card by content **independently of href**, and hard-asserts
`/game/pilot-nba-completed` before the second act. Details must have that exact URL,
Pilot Harbor Otters away score 107, Pilot Summit Owls home score 113 in their
respective visible scoreboard columns, and exact visible `Final`. Completed means
play ended, not a championship or market settlement. Hidden/page-wide text,
reversed scores, distraction URL and another game between the same teams cannot
pass. Model opinion is never the oracle.

The negative scenario mutates only that visible card's href in the real DOM. An
owned MutationObserver maintains it across live-sync replacements, then disconnects
and restores originals in teardown. No backend response fabrication or production
hook. The unchanged AI acceptance test must fail `expect.toHaveAttribute` at the
oracle, with exact expected/observed paths and no second act. Restoration uses a
fresh new context with mutation disabled. An infrastructure/model error is **not**
wrong-link proof. The deterministic smoke shares oracle helpers, but is not a
substitute for this live unchanged-test experiment.

## Retry and evidence boundaries

Never automatically rerun a failed measured **test attempt** so a later pass erases
it. Runner/test retries are zero. Frozen Bun install applies
`patches/e2e@0.17.0.patch`, changing shared `TRANSPORT_RETRIES` and its declaration
from 5 to 0 for both `generateText` judgments and `ToolLoopAgent` navigation.
Synthetic failure-path checks observed one physical invocation per path after
patching (six before), followed by `MODEL_PROVIDER_FAILED`; no provider contacted.

Normal within-attempt behavior remains allowed, **not** “all recovery disabled”:

- Proactive OAuth refresh and once-after-401 refresh/inference resend.
- Forced-tool-choice fallback, context-overflow recovery, text-only continuation,
  invalid-tool repair/action-error turns and structured-output repair within budget.
- Stall failure diagnostic can still say “sending it again”; SDK transport retries
  are patched to zero.

`pilot:evaluate --cohorts` schedules exactly ten cache-off attempts, then a separate
empty-cache read-write recording and nine read-only warm attempts (`strict:false`).
Every ordinary failure is retained; terminal configuration/model/auth/quota,
fixture/hosting/report/cleanup failures stop and list unperformed slots. Separate
`--regression` uses cache off. There is no resume/last-failed/rerun path.

Use the installed public CLI and supported reporter, native `report-1` and physical
`trace-1`; no private programmatic runner import. UUID output/cache roots preserve
previous invocations. Actual per-step cache mode/reason/replayed/total actions are
not inferred from cache being enabled. Passing fallback is navigation success but
not model-free replay. That claim needs **both** navigation steps `self-finalized`
and **zero observed logical model calls**. Physical HTTP counts are unavailable;
logical SDK turns exclude transport details/OAuth resends. Provider-provenance
input/output tokens are actual; adapter upper bounds remain separately labeled;
missing tokens are `UNAVAILABLE`, never invented zero. No dollar estimate.

The orchestrator saves each scheduled start before invoking the CLI, then retains
native statuses even if sidecar evidence is missing. Reporter construction has no
worker-side effects; its public completion hook writes the invocation's sidecar.
`evidence.js` sums nonduplicated agent-step metrics, not run totals or `model.calls`
a second time. Corrupt cache entries retain their bytes' hashes for fingerprint
comparison but cannot count as verified recordings. Raw provider/auth error prose
stays in ignored reports, not the reviewable summary.

Raw reports, traces/screenshots, caches and UUID evidence stay in explicitly ignored
`/.e2e/pilot/` because pinned output must be inside the config project root.
Credentials always remain external. Whitelist summaries still require manual review
before publishing sanitized results. No CI/deployment, production access or
replacement of existing tests is part of this pilot.

API references: [config](https://e2e.tester.army/docs/reference/config),
[subscriptions](https://e2e.tester.army/docs/subscriptions),
[expect](https://e2e.tester.army/docs/reference/expect),
[cache](https://e2e.tester.army/docs/cache),
[reporters](https://e2e.tester.army/docs/reference/reporters). Compatibility must be
checked against the installed pinned release, not assumed from current main.
