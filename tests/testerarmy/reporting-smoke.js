import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { mkdtemp, readFile, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { cohortFindings, inspectCache, summarizeAttempt } from "./evidence.js";
import { homeToNba, openIntended } from "./oracle.ts";

let path = process.argv[2];
if (!path) {
  const child = spawnSync("bun", ["tests/testerarmy/evaluate.js", "--smoke"], {
    encoding: "utf8",
    timeout: 240_000,
  });
  process.stdout.write(child.stdout);
  process.stderr.write(child.stderr);
  assert.equal(child.status, 0);
  path = child.stdout.match(/^Pilot evidence: (.+)$/m)?.[1];
}
assert.ok(path, "CLI publishes the retained report location");
const summary = JSON.parse(await readFile(path, "utf8"));
assert.equal(summary.kind, "deterministic-smoke-not-ai-reliability");
assert.equal(summary.attempts.length, 3);
assert.deepEqual(
  summary.attempts.map((attempt) => attempt.status),
  ["passed", "failed", "passed"],
);
const negative = summary.attempts[1];
assert.equal(negative.wrongLinkRejected, true);
assert.equal(negative.deterministicFailure.api, "expect.toHaveAttribute");
assert.equal(
  negative.deterministicFailure.source.file,
  "tests/testerarmy/oracle.ts",
);
assert.equal(
  negative.deterministicFailure.expected,
  "/game/pilot-nba-completed",
);
assert.equal(
  negative.deterministicFailure.observed,
  "/game/pilot-nba-distraction",
);
assert.equal(negative.fixtureEvidence.mutation.restored, true);
assert.equal(summary.cleanup, "complete");
const dry = spawnSync("bun", ["tests/testerarmy/evaluate.js", "--plan"], {
  encoding: "utf8",
});
assert.equal(dry.status, 0, dry.stderr);
const plan = JSON.parse(dry.stdout);
assert.equal(plan.length, 20);
assert.equal(
  plan.filter((item) => item.cohort === "uncached" && item.cacheMode === "off")
    .length,
  10,
);
assert.equal(
  plan.filter(
    (item) => item.cohort === "recording" && item.cacheMode === "read-write",
  ).length,
  1,
);
assert.equal(
  plan.filter(
    (item) => item.cohort === "warm" && item.cacheMode === "read-only",
  ).length,
  9,
);
assert.ok(plan.every((item) => item.scenario === "normal"));
const cacheDir = await mkdtemp(".e2e/pilot/cache-contract-");
const provenance = {
  testId:
    "tests/testerarmy/acceptance.e2e.ts::Home%20%E2%86%92%20NBA%20%E2%86%92%20intended%20completed%20game",
  targetId: "desktop",
};
const digest = (text) =>
  createHash("sha256")
    .update(text.replace(/\r\n?/g, "\n").normalize("NFC").trim())
    .digest("hex");
const trace = (goal, startPath, endPath) => ({
  schemaVersion: "trace-1",
  createdAt: new Date().toISOString(),
  payload: {
    recordedFor: { ...provenance, instructionDigest: digest(goal) },
    executor: { name: "synthetic-reporting-fixture" },
    startPath,
    endPath,
    actions: [
      {
        name: "tap",
        summary: "synthetic reporting ONLY",
        target: { role: "link", name: "synthetic" },
      },
    ],
    summary: "synthetic, not a live recording",
  },
});
await writeFile(
  `${cacheDir}/${"a".repeat(64)}.json`,
  JSON.stringify(trace(homeToNba, "/", "/nba")),
);
const partial = await inspectCache(cacheDir, provenance.testId);
assert.equal(
  partial.bothNavigationTracesPresent,
  false,
  "a passing recording with one trace is not both recordings",
);
await writeFile(
  `${cacheDir}/${"b".repeat(64)}.json`,
  JSON.stringify(trace(openIntended, "/nba", "/game/pilot-nba-completed")),
);
const full = await inspectCache(cacheDir, provenance.testId);
assert.equal(full.bothNavigationTracesPresent, true);
assert.equal(full.entries.length, 2);
assert.ok(
  full.entries.every(
    (entry) => entry.sha256.length === 64 && entry.actions[0] === "tap",
  ),
);
await writeFile(`${cacheDir}/${"b".repeat(64)}.json`, "corrupt");
const corrupt = await inspectCache(cacheDir, provenance.testId);
assert.equal(corrupt.bothNavigationTracesPresent, false);
assert.notEqual(
  corrupt.fingerprint,
  full.fingerprint,
  "warm writes/corruption cannot be hidden",
);
const valid = summary.attempts[0];
const native = JSON.parse(await readFile(valid.reportPath, "utf8"));
const sidecar = JSON.parse(
  await readFile(
    valid.reportPath.replace("report.json", "fixture-evidence.json"),
    "utf8",
  ),
);
const synthetic = structuredClone(native);
synthetic.run.results[0].file = "tests/testerarmy/acceptance.e2e.ts";
synthetic.run.results[0].attempts[0].steps.push(
  ...[1, 2].map((id) => ({
    id: `synthetic-agent-${id}`,
    api: "agent.act",
    kind: "agent",
    status: "passed",
    durationMs: 1,
    metrics: { modelCalls: 0 },
    cache: { mode: "self-finalized", replayedActions: 1, totalActions: 1 },
  })),
);
let evidence = summarizeAttempt(
  synthetic,
  { ...valid, smoke: false, cacheMode: "off" },
  sidecar,
);
assert.equal(
  evidence.modelFreeReplay,
  false,
  "cache-off passes cannot be replay evidence",
);
evidence = summarizeAttempt(
  synthetic,
  { ...valid, smoke: false, cacheMode: "read-only" },
  sidecar,
);
assert.equal(
  evidence.modelFreeReplay,
  true,
  "both native cache modes plus explicit zero logical calls required",
);
assert.equal(evidence.actualTokenUsage, "unavailable");
const abortedReplay = structuredClone(synthetic);
abortedReplay.run.exitCode = 3;
abortedReplay.run.errors = [
  {
    code: "MODEL_PROVIDER_FAILED",
    category: "infrastructure",
    message: "synthetic report blocker",
  },
];
assert.equal(
  summarizeAttempt(
    abortedReplay,
    { ...valid, smoke: false, cacheMode: "read-only", exitCode: 3 },
    sidecar,
  ).modelFreeReplay,
  false,
  "terminal run errors cannot establish model-free replay",
);
const agent = synthetic.run.results[0].attempts[0].steps.at(-1);
delete agent.metrics;
evidence = summarizeAttempt(
  synthetic,
  { ...valid, smoke: false, cacheMode: "read-only" },
  sidecar,
);
assert.equal(evidence.logicalModelCalls, "unavailable");
assert.equal(evidence.modelFreeReplay, false);
agent.metrics = { modelCalls: 2 };
agent.cache = {
  mode: "agent-concluded",
  reason: "end-mismatch",
  replayedActions: 1,
  totalActions: 1,
};
agent.model = {
  tokenAccounting: "adapter-upper-bound",
  inputTokens: 999,
  outputTokens: 42,
};
evidence = summarizeAttempt(
  synthetic,
  { ...valid, smoke: false, cacheMode: "read-only" },
  sidecar,
);
assert.equal(
  evidence.status,
  "passed",
  "live fallback still counts as navigation success",
);
assert.equal(evidence.modelFreeReplay, false);
assert.equal(
  evidence.actualTokenUsage,
  "unavailable",
  "upper bounds never become actual usage",
);
assert.deepEqual(evidence.agentSteps.at(-1).adapterUpperBoundTokens, {
  inputTokens: 999,
  outputTokens: 42,
});
assert.equal(evidence.agentSteps.at(-1).cache.reason, "end-mismatch");
assert.equal(evidence.agentSteps.at(-1).promptCacheReadTokens, "unavailable");
agent.cache = {
  mode: "missed",
  reason: "no-entry",
  replayedActions: 0,
  totalActions: 0,
};
evidence = summarizeAttempt(
  synthetic,
  { ...valid, smoke: false, cacheMode: "read-only" },
  sidecar,
);
assert.equal(evidence.agentSteps.at(-1).cache.reason, "no-entry");
const firstAgent = synthetic.run.results[0].attempts[0].steps.at(-2);
firstAgent.metrics = { modelCalls: 1 };
firstAgent.model = {
  tokenAccounting: "provider",
  calls: 1,
  inputTokens: 30,
  outputTokens: 4,
};
agent.model = {
  tokenAccounting: "provider",
  calls: 2,
  inputTokens: 20,
  outputTokens: 7,
};
evidence = summarizeAttempt(
  synthetic,
  { ...valid, smoke: false, cacheMode: "read-only" },
  sidecar,
);
assert.equal(
  evidence.logicalModelCalls,
  3,
  "step counts not counted twice via model.calls",
);
assert.deepEqual(evidence.actualTokenUsage, {
  inputTokens: 50,
  outputTokens: 11,
});
const infrastructure = JSON.parse(await readFile(negative.reportPath, "utf8"));
infrastructure.run.exitCode = 3;
infrastructure.run.results[0].attempts[0].error = {
  code: "MODEL_PROVIDER_FAILED",
  category: "infrastructure",
  message: "synthetic quota fixture",
  phase: "body",
};
const negativeSidecar = JSON.parse(
  await readFile(
    negative.reportPath.replace("report.json", "fixture-evidence.json"),
    "utf8",
  ),
);
evidence = summarizeAttempt(
  infrastructure,
  { ...negative, exitCode: 3 },
  negativeSidecar,
);
assert.equal(
  evidence.wrongLinkRejected,
  false,
  "model/infra failure with href artifacts is NOT regression proof",
);
assert.equal(evidence.blocker, true);
assert.ok(
  !JSON.stringify(evidence).includes("synthetic quota fixture"),
  "provider prose excluded from reviewed summary",
);
const retry = structuredClone(native);
retry.run.results[0].attempts.push(
  structuredClone(retry.run.results[0].attempts[0]),
);
assert.throws(
  () => summarizeAttempt(retry, valid, sidecar),
  /no failed-test reruns/,
);
assert.throws(
  () =>
    summarizeAttempt(
      native,
      { ...valid, startedMs: Date.now() + 60_000 },
      sidecar,
    ),
  /no stale report/,
);
assert.throws(
  () => summarizeAttempt(native, valid, { ...sidecar, runId: "old" }),
  /sidecar belongs/,
);
const incomplete = cohortFindings(plan, [
  { cohort: "uncached", status: "passed" },
  { cohort: "uncached", status: "failed" },
]);
assert.equal(incomplete.uncached.planned, 10);
assert.equal(incomplete.uncached.reported, 2);
assert.equal(incomplete.uncached.passed, 1);
assert.equal(incomplete.uncached.failed, 1);
assert.equal(incomplete.uncached.complete, false);
assert.equal(incomplete.warm.planned, 9);
assert.equal(incomplete.warm.modelFreeReplays, 0);
assert.equal(incomplete.recording.complete, false);
const blocked = spawnSync(
  "bun",
  ["tests/testerarmy/evaluate.js", "--cohorts"],
  {
    encoding: "utf8",
    env: Object.fromEntries(
      ["PATH", "HOME", "USER", "TMPDIR"]
        .filter((key) => process.env[key])
        .map((key) => [key, process.env[key]]),
    ),
  },
);
assert.equal(blocked.status, 1);
const blockedPath = blocked.stdout.match(/^Pilot evidence: (.+)$/m)?.[1];
assert.ok(blockedPath);
const incompleteReport = JSON.parse(await readFile(blockedPath, "utf8"));
assert.equal(incompleteReport.state, "incomplete");
assert.equal(incompleteReport.attempts.length, 0);
assert.equal(incompleteReport.unperformed.length, 20);
assert.equal(incompleteReport.navigationFindings.uncached.passed, 0);
assert.equal(incompleteReport.recordingEvidence, "unavailable");
const wrongCheckout = spawnSync(
  "bun",
  [resolve("tests/testerarmy/evaluate.js"), "--plan"],
  { cwd: "/tmp/nailbiter-pilot-636", encoding: "utf8" },
);
assert.equal(
  wrongCheckout.status,
  1,
  "must never select another checkout's config/output",
);
assert.match(wrongCheckout.stderr, /own checkout root/);
console.log(`Preserved missing-model contract blocker: ${blockedPath}`);
console.log(
  "PASS: real browser, exact plan and synthetic cache/usage/failure/incomplete contracts (not measured AI reliability)",
);
