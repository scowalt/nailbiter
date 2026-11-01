import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { lstat, readdir, readFile } from "node:fs/promises";
import { homeToNba, openIntended } from "./oracle.ts";

const digest = (text) => createHash("sha256").update(text).digest("hex");
const goalDigest = (text) =>
  digest(text.replace(/\r\n?/gu, "\n").normalize("NFC").trim());

export async function inspectCache(directory, testId) {
  const entries = [];
  for (const file of (await readdir(directory)).sort()) {
    assert.match(
      file,
      /^[a-f0-9]{64}\.json$/,
      "unexpected file in owned cache",
    );
    const path = `${directory}/${file}`;
    assert.ok((await lstat(path)).isFile(), "no symlink cache entries");
    const bytes = await readFile(path);
    const entry = {
      file,
      sha256: digest(bytes),
      size: bytes.length,
      format: "unreadable",
      actions: [],
    };
    try {
      const raw = JSON.parse(bytes.toString("utf8"));
      const trace = raw.payload;
      if (
        raw.schemaVersion === "trace-1" &&
        typeof raw.createdAt === "string" &&
        Array.isArray(trace?.actions) &&
        trace.actions.length > 0 &&
        trace.actions.every((action) => typeof action.name === "string")
      ) {
        Object.assign(entry, {
          format: "trace-1",
          createdAt: raw.createdAt,
          testId: trace.recordedFor?.testId ?? "unavailable",
          targetId: trace.recordedFor?.targetId ?? "unavailable",
          instructionDigest:
            trace.recordedFor?.instructionDigest ?? "unavailable",
          startPath: trace.startPath ?? "unavailable",
          endPath: trace.endPath ?? "unavailable",
          truncated: trace.truncated ?? false,
          actions: trace.actions.map((action) => action.name),
        });
      }
    } catch {}
    entries.push(entry);
  }
  const goals = [
    [homeToNba, "/", "/nba"],
    [openIntended, "/nba", "/game/pilot-nba-completed"],
  ];
  const verifiedGoals = goals.map(([goal, start, end]) => ({
    instructionDigest: goalDigest(goal),
    startPath: start,
    endPath: end,
    files: entries
      .filter(
        (entry) =>
          entry.format === "trace-1" &&
          entry.testId === testId &&
          entry.targetId === "desktop" &&
          entry.instructionDigest === goalDigest(goal) &&
          entry.startPath === start &&
          entry.endPath === end &&
          !entry.truncated &&
          !entry.actions.includes("tool"),
      )
      .map((entry) => entry.file),
  }));
  return {
    entries,
    verifiedGoals,
    bothNavigationTracesPresent: verifiedGoals.every(
      (goal) => goal.files.length > 0,
    ),
    fingerprint: digest(
      JSON.stringify(entries.map(({ file, sha256 }) => ({ file, sha256 }))),
    ),
  };
}

export function cohortFindings(plan, attempts) {
  return Object.fromEntries(
    [...new Set(plan.map((item) => item.cohort))].map((cohort) => {
      const planned = plan.filter((item) => item.cohort === cohort).length;
      const group = attempts.filter((attempt) => attempt.cohort === cohort);
      const reported = group.filter((attempt) =>
        ["passed", "failed", "timed-out", "interrupted", "skipped"].includes(
          attempt.status,
        ),
      );
      const passed = reported.filter(
        (attempt) => attempt.status === "passed",
      ).length;
      const complete =
        reported.length === planned &&
        group.every(
          (attempt) =>
            !attempt.blocker && attempt.reportContract !== "incomplete",
        );
      return [
        cohort,
        {
          planned,
          started: group.length,
          reported: reported.length,
          passed,
          failed: reported.length - passed,
          complete,
          allPassed: complete && passed === planned,
          modelFreeReplays: reported.filter(
            (attempt) => attempt.modelFreeReplay === true,
          ).length,
        },
      ];
    }),
  );
}

const availableNumber = (value) => Number.isInteger(value) && value >= 0;
const sumOrUnavailable = (values) =>
  values.length && values.every(availableNumber)
    ? values.reduce((sum, value) => sum + value, 0)
    : "unavailable";
const hrefValue = (description) => {
  const match = description?.match(/^attribute "href" (".*")$/);
  return match ? JSON.parse(match[1]) : "unavailable";
};
function errorEvidence(error) {
  if (!error) return null;
  return {
    code: error.code,
    category: error.category,
    phase: error.phase ?? "unavailable",
    ...(error.code === "ASSERTION_FAILED"
      ? { message: error.message, details: error.details ?? "unavailable" }
      : {}),
  };
}

export function summarizeAttempt(report, invocation, fixtureReport) {
  assert.equal(report.schemaVersion, "report-1");
  assert.equal(report.run.runner.version, "0.17.0");
  assert.equal(report.run.results.length, 1, "exactly one canonical journey");
  assert.equal(report.run.exitCode, invocation.exitCode);
  assert.ok(
    Date.parse(report.run.startedAt) >= invocation.startedMs - 1_000,
    "no stale report",
  );
  assert.equal(report.run.serialGroups.length, 0);
  const result = report.run.results[0];
  assert.equal(result.selected, true);
  assert.equal(result.repeat, 0);
  assert.equal(result.attempts.length, 1, "no failed-test reruns");
  assert.equal(
    result.file,
    invocation.smoke
      ? "tests/testerarmy/reporting-smoke.e2e.ts"
      : "tests/testerarmy/acceptance.e2e.ts",
  );
  assert.equal(result.targetId, "desktop");
  assert.equal(result.agent, "default");
  const attempt = result.attempts[0];
  assert.equal(attempt.index, 0);
  assert.equal(
    fixtureReport.runId,
    report.run.id,
    "reporter sidecar belongs to this invocation",
  );
  assert.equal(
    fixtureReport.records.length,
    1,
    "fixture reporter must finish exactly once",
  );
  const fixtureEvidence = fixtureReport.records[0];
  assert.equal(fixtureEvidence.freshCookies, true);
  assert.equal(fixtureEvidence.scenario, invocation.scenario);
  const steps = attempt.steps;
  const agents = steps.filter((step) => step.kind === "agent");
  const logicalModelCalls = sumOrUnavailable(
    agents.map((step) => step.metrics?.modelCalls),
  );
  const actualInput = sumOrUnavailable(
    agents.map((step) =>
      step.model?.tokenAccounting === "provider"
        ? step.model.inputTokens
        : undefined,
    ),
  );
  const actualOutput = sumOrUnavailable(
    agents.map((step) =>
      step.model?.tokenAccounting === "provider"
        ? step.model.outputTokens
        : undefined,
    ),
  );
  const failed = steps.find(
    (step) => step.status === "failed" && step.kind === "assertion",
  );
  const deterministicFailure = failed
    ? {
        api: failed.api,
        source: failed.source ?? "unavailable",
        error: errorEvidence(failed.error),
        expected:
          failed.api === "expect.toHaveAttribute"
            ? hrefValue(failed.error?.details?.expected)
            : (failed.error?.details?.expected ?? "unavailable"),
        observed:
          failed.api === "expect.toHaveAttribute"
            ? hrefValue(failed.error?.details?.observed)
            : (failed.error?.details?.observed ?? "unavailable"),
      }
    : null;
  const errors = [
    attempt.error,
    ...attempt.secondaryErrors,
    ...report.run.errors,
  ].filter(Boolean);
  const blocker =
    ![0, 1].includes(report.run.exitCode) ||
    !["passed", "failed"].includes(report.run.status) ||
    attempt.cleanup !== "complete" ||
    attempt.status === "skipped" ||
    errors.some(
      (error) =>
        error.category !== "test" || (error.phase && error.phase !== "body"),
    );
  const mutation = fixtureEvidence.mutation;
  const wrongLinkRejected =
    invocation.scenario === "wrong-link" &&
    !blocker &&
    attempt.status === "failed" &&
    attempt.error?.code === "ASSERTION_FAILED" &&
    deterministicFailure?.api === "expect.toHaveAttribute" &&
    deterministicFailure.source.file === "tests/testerarmy/oracle.ts" &&
    deterministicFailure.expected === "/game/pilot-nba-completed" &&
    deterministicFailure.observed === "/game/pilot-nba-distraction" &&
    failed.error?.details?.matches === 1 &&
    attempt.error.message === failed.error.message &&
    mutation?.applied > 0 &&
    mutation.error === null &&
    mutation.restored === true &&
    mutation.disconnected === true &&
    mutation.original.length > 0 &&
    mutation.original.every((href) => href === "/game/pilot-nba-completed") &&
    mutation.observed.length === 1 &&
    mutation.observed[0] === "/game/pilot-nba-distraction" &&
    steps.filter((step) => step.api === "agent.act").length ===
      (invocation.smoke ? 0 : 1) &&
    steps.filter((step) => step.api === "locator.click").length ===
      (invocation.smoke ? 1 : 0);
  const navigation = agents.filter((step) => step.api === "agent.act");
  return {
    ...invocation,
    runId: report.run.id,
    testId: result.testId,
    attemptId: attempt.id,
    status: attempt.status,
    durationMs: attempt.durationMs,
    cleanup: attempt.cleanup,
    fixtureEvidence,
    blocker,
    wrongLinkRejected,
    deterministicFailure,
    logicalModelCalls,
    physicalInferenceRequests:
      "unavailable (logical SDK turns exclude OAuth refresh/resend and in-step recovery detail)",
    actualTokenUsage:
      actualInput === "unavailable" || actualOutput === "unavailable"
        ? "unavailable"
        : { inputTokens: actualInput, outputTokens: actualOutput },
    agentSteps: agents.map((step) => ({
      id: step.id,
      api: step.api,
      status: step.status,
      durationMs: step.durationMs,
      modelCalls: availableNumber(step.metrics?.modelCalls)
        ? step.metrics.modelCalls
        : "unavailable",
      cache:
        step.cache ?? (invocation.cacheMode === "off" ? "off" : "unavailable"),
      provider: step.model?.provider ?? "unavailable",
      model: step.model?.model ?? "unavailable",
      tokenAccounting: step.model?.tokenAccounting ?? "unavailable",
      actualTokens:
        step.model?.tokenAccounting === "provider" &&
        availableNumber(step.model.inputTokens) &&
        availableNumber(step.model.outputTokens)
          ? {
              inputTokens: step.model.inputTokens,
              outputTokens: step.model.outputTokens,
            }
          : "unavailable",
      adapterUpperBoundTokens:
        step.model?.tokenAccounting === "adapter-upper-bound"
          ? {
              inputTokens: step.model.inputTokens ?? "unavailable",
              outputTokens: step.model.outputTokens ?? "unavailable",
            }
          : "unavailable",
      promptCacheReadTokens: step.model?.cacheReadTokens ?? "unavailable",
      promptCacheWriteTokens: step.model?.cacheWriteTokens ?? "unavailable",
    })),
    modelFreeReplay:
      !blocker &&
      !invocation.smoke &&
      invocation.cacheMode === "read-only" &&
      attempt.status === "passed" &&
      logicalModelCalls === 0 &&
      navigation.length === 2 &&
      navigation.every((step) => step.cache?.mode === "self-finalized"),
    assertions: steps
      .filter((step) => step.kind === "assertion")
      .map((step) => ({
        api: step.api,
        source: step.source,
        status: step.status,
        error: errorEvidence(step.error),
      })),
    error: errorEvidence(attempt.error),
    secondaryErrors: attempt.secondaryErrors.map(errorEvidence),
    runErrors: report.run.errors.map(errorEvidence),
  };
}
