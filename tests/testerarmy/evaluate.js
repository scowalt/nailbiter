import assert from "node:assert/strict";
import { spawn, spawnSync } from "node:child_process";
import { randomUUID } from "node:crypto";
import { mkdir, readFile, rename, writeFile } from "node:fs/promises";
import { homedir } from "node:os";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { startFixture } from "../browser-fixture/lifecycle.js";
import { verifyStoppedFixture } from "../browser-fixture/stopped-verification.js";
import { cohortFindings, inspectCache, summarizeAttempt } from "./evidence.js";

assert.equal(
  resolve(process.cwd()),
  resolve(fileURLToPath(new URL("../../", import.meta.url))),
  "run from this script's own checkout root; never select another checkout's config/output",
);
const mode = process.argv[2];
assert.ok(
  process.argv.length === 3 &&
    ["--smoke", "--preflight", "--cohorts", "--regression", "--plan"].includes(
      mode,
    ),
  "use exactly --smoke, --preflight, --cohorts, --regression or --plan",
);
const smoke = mode === "--smoke";
const plan =
  mode === "--cohorts" || mode === "--plan"
    ? [
        ...Array.from({ length: 10 }, (_, index) => ({
          cohort: "uncached",
          ordinal: index + 1,
          cacheMode: "off",
          scenario: "normal",
        })),
        {
          cohort: "recording",
          ordinal: 1,
          cacheMode: "read-write",
          scenario: "normal",
        },
        ...Array.from({ length: 9 }, (_, index) => ({
          cohort: "warm",
          ordinal: index + 1,
          cacheMode: "read-only",
          scenario: "normal",
        })),
      ]
    : mode === "--preflight"
      ? [
          {
            cohort: "unmeasured-preflight",
            ordinal: 1,
            cacheMode: "off",
            scenario: "normal",
          },
        ]
      : [
          ...(smoke
            ? [
                {
                  cohort: "smoke-baseline",
                  ordinal: 1,
                  cacheMode: "off",
                  scenario: "normal",
                },
              ]
            : []),
          {
            cohort: smoke ? "smoke-negative" : "regression-negative",
            ordinal: 1,
            cacheMode: "off",
            scenario: "wrong-link",
          },
          {
            cohort: smoke ? "smoke-restored" : "regression-restored",
            ordinal: 1,
            cacheMode: "off",
            scenario: "normal",
          },
        ];
if (mode === "--plan") {
  console.log(JSON.stringify(plan, null, 2));
  process.exit(0);
}
const output = `.e2e/pilot/evaluation-${randomUUID()}`;
await mkdir(output, { recursive: true, mode: 0o700 });
assert.equal(
  spawnSync("git", ["check-ignore", "-q", `${output}/report.json`]).status,
  0,
);
const summaryPath = `${output}/summary.json`;
const summary = {
  version: 1,
  kind: smoke ? "deterministic-smoke-not-ai-reliability" : mode.slice(2),
  state: "incomplete",
  plan,
  attempts: [],
  cleanup: "pending",
  recordingEvidence: "unavailable",
  regressionProof: "unavailable",
  limitations: [
    "Fresh native contexts; test retries 0; patched SDK transport retries 0.",
    "Normal within-attempt agent recovery and OAuth refresh remain allowed.",
    "Logical model counts are not physical HTTP counts; no subscription dollar estimates.",
    "Whitelist summary is reviewable, not automatically safe to publish. Raw artifacts remain ignored.",
  ],
};
let saving = Promise.resolve();
const save = () => {
  const bytes = `${JSON.stringify(summary, null, 2)}\n`;
  saving = saving.then(async () => {
    await writeFile(`${summaryPath}.partial`, bytes, { mode: 0o600 });
    await rename(`${summaryPath}.partial`, summaryPath);
  });
  return saving;
};
await save();
console.log(`Pilot evidence: ${summaryPath}`);
let fixture,
  activeChild,
  interrupted = false;
const killOwned = (signal) => {
  if (activeChild?.pid) {
    try {
      process.kill(-activeChild.pid, signal);
    } catch (error) {
      if (error.code !== "ESRCH") throw error;
    }
  }
};
const onSignal = () => {
  interrupted = true;
  summary.blocker = "interrupted";
  void save();
  killOwned("SIGINT");
};
process.on("SIGINT", onSignal);
process.on("SIGTERM", onSignal);
try {
  if ("E2E_OAUTH_CREDENTIALS" in process.env) {
    summary.blocker =
      "configuration: unset forbidden E2E_OAUTH_CREDENTIALS override";
    throw new Error(summary.blocker);
  }
  if (!smoke && !process.env.NAILBITER_PILOT_MODEL?.trim()) {
    summary.blocker =
      "configuration: explicit coordinator-selected model missing";
    throw new Error(summary.blocker);
  }
  fixture = await startFixture({ handleSignals: false });
  summary.endpoints = {
    local: fixture.manifest.localUrl,
    private: fixture.manifest.privateUrl,
  };
  const env = Object.fromEntries(
    ["PATH", "HOME", "USER", "TMPDIR"]
      .filter((key) => process.env[key])
      .map((key) => [key, process.env[key]]),
  );
  Object.assign(env, {
    E2E_TELEMETRY_DISABLED: "1",
    XDG_CONFIG_HOME: join(homedir(), ".config", "nailbiter-testerarmy-pilot"),
    NAILBITER_PILOT_MANIFEST: fixture.manifestPath,
    ...(smoke
      ? {}
      : { NAILBITER_PILOT_MODEL: process.env.NAILBITER_PILOT_MODEL }),
  });
  const cacheDir = resolve(output, "cache");
  await mkdir(cacheDir, { mode: 0o700 });
  const emptyCache = await inspectCache(cacheDir);
  assert.equal(emptyCache.entries.length, 0);
  let recordingFingerprint;
  for (const item of plan) {
    if (item.cohort === "recording")
      assert.equal(
        (await inspectCache(cacheDir)).entries.length,
        0,
        "recording must start empty after uncached group",
      );
    if (interrupted) throw new Error("interrupted");
    assert.equal(
      new Intl.DateTimeFormat("en-CA", {
        timeZone: "America/New_York",
        year: "numeric",
        month: "2-digit",
        day: "2-digit",
      }).format(new Date()),
      fixture.manifest.listingDateEastern,
      "fixture crossed Eastern midnight",
    );
    const health = await fetch(`${fixture.manifest.privateUrl}/health`, {
      signal: AbortSignal.timeout(10_000),
    });
    assert.ok(
      health.ok && (await health.json()).status === "healthy",
      "fixture health blocker",
    );
    const label = `${item.cohort}-${item.ordinal}`;
    const attemptOutput = join(output, label);
    const invocation = {
      ...item,
      smoke,
      startedMs: Date.now(),
      reportPath: `${attemptOutput}/report.json`,
    };
    const retained = {
      ...invocation,
      status: "report-unavailable",
      reportContract: "incomplete",
      logicalModelCalls: "unavailable",
      actualTokenUsage: "unavailable",
    };
    summary.attempts.push(retained);
    await save();
    const args = [
      "run",
      "--config",
      smoke ? "e2e.pilot-reporting-smoke.config.ts" : "e2e.pilot.config.ts",
      "--output",
      attemptOutput,
      "--retries",
      "0",
      "--workers",
      "1",
      "--reporter",
      "json",
      ...(item.cacheMode === "off" ? ["--no-cache"] : []),
    ];
    const child = spawn("node_modules/.bin/e2e", args, {
      env: {
        ...env,
        NAILBITER_PILOT_CACHE_MODE: item.cacheMode,
        NAILBITER_PILOT_CACHE_DIR: cacheDir,
        NAILBITER_PILOT_SCENARIO: item.scenario,
      },
      detached: true,
      stdio: ["ignore", "pipe", "pipe"],
    });
    activeChild = child;
    let stdout = "",
      stderr = "",
      timedOut = false;
    child.stdout.on("data", (data) => {
      stdout += data;
    });
    child.stderr.on("data", (data) => {
      stderr += data;
    });
    const timeout = setTimeout(() => {
      timedOut = true;
      killOwned("SIGINT");
    }, 360_000);
    const forceTimeout = setTimeout(() => {
      timedOut = true;
      killOwned("SIGKILL");
    }, 375_000);
    let exitCode;
    try {
      exitCode = await new Promise((accept, reject) => {
        child.on("error", reject);
        child.on("close", accept);
      });
    } finally {
      clearTimeout(timeout);
      clearTimeout(forceTimeout);
      activeChild = undefined;
      await writeFile(`${output}/${label}.stdout.log`, stdout, { mode: 0o600 });
      await writeFile(`${output}/${label}.stderr.log`, stderr, { mode: 0o600 });
    }
    retained.exitCode = exitCode;
    await save();
    const report = JSON.parse(await readFile(invocation.reportPath, "utf8"));
    retained.nativeResults =
      report.run?.results?.map((result) => ({
        status: result.status,
        attempts: result.attempts.map((attempt) => ({
          status: attempt.status,
          index: attempt.index,
          durationMs: attempt.durationMs,
          errorCode: attempt.error?.code ?? null,
        })),
      })) ?? "unavailable";
    await save();
    const fixtureReport = JSON.parse(
      await readFile(`${attemptOutput}/fixture-evidence.json`, "utf8"),
    );
    Object.assign(
      retained,
      summarizeAttempt(report, { ...invocation, exitCode }, fixtureReport),
      { reportContract: "complete" },
    );
    assert.ok(
      !summary.attempts
        .slice(0, -1)
        .some(
          (attempt) =>
            attempt.runId === retained.runId ||
            attempt.attemptId === retained.attemptId,
        ),
      "fresh run/attempt IDs required",
    );
    if (item.cohort === "recording" || item.cohort === "warm") {
      const cache = await inspectCache(cacheDir, retained.testId);
      await writeFile(
        `${output}/${label}.cache-inventory.json`,
        `${JSON.stringify(cache, null, 2)}\n`,
        { mode: 0o600 },
      );
      retained.cacheInventory = `${output}/${label}.cache-inventory.json`;
      if (item.cohort === "recording") {
        summary.recordingEvidence = {
          emptyBefore: emptyCache.entries.length === 0,
          ...cache,
        };
        recordingFingerprint = cache.fingerprint;
      } else {
        retained.recordingUnchanged =
          cache.fingerprint === recordingFingerprint;
        assert.equal(
          retained.recordingUnchanged,
          true,
          "read-only warm attempt altered recording; stop and preserve evidence",
        );
      }
    }
    await save();
    if (interrupted || timedOut || retained.blocker)
      throw new Error(
        "terminal infrastructure/model/auth/fixture blocker; inspect retained native report",
      );
    if (
      item.scenario === "wrong-link" &&
      !retained.fixtureEvidence.mutation?.restored
    )
      throw new Error("negative fixture cleanup not verified");
  }
  const negative = summary.attempts.find(
    (attempt) => attempt.scenario === "wrong-link",
  );
  if (negative)
    summary.regressionProof = {
      wrongLinkRejected: negative.wrongLinkRejected,
      restoredInNewContext:
        summary.attempts.at(-1).status === "passed" &&
        summary.attempts.at(-1).fixtureEvidence.mutation === null,
    };
  summary.state = "complete";
  const expectedFailure = (attempt) =>
    attempt.scenario === "wrong-link" && attempt.wrongLinkRejected;
  if (
    summary.attempts.some(
      (attempt) => attempt.status !== "passed" && !expectedFailure(attempt),
    ) ||
    (negative && !negative.wrongLinkRejected)
  )
    process.exitCode = 1;
} catch (error) {
  await writeFile(`${output}/blocker.log`, `${error.stack ?? error}\n`, {
    mode: 0o600,
  });
  summary.blocker ??=
    "setup-or-report-contract-or-terminal-attempt-blocker (see ignored blocker.log/native report)";
  process.exitCode = 1;
} finally {
  killOwned("SIGTERM");
  if (fixture) {
    try {
      await fixture.stop();
      const cleanupEvidence = await verifyStoppedFixture(fixture);
      summary.cleanup = "complete";
      summary.cleanupEvidence = cleanupEvidence;
    } catch (error) {
      await writeFile(
        `${output}/cleanup-blocker.log`,
        `${error.stack ?? error}\n`,
        { mode: 0o600 },
      );
      summary.cleanup = "failed";
      summary.blocker = "owned cleanup failed; see ignored cleanup-blocker.log";
      summary.state = "incomplete";
      process.exitCode = 1;
    }
  } else
    summary.cleanup =
      "not-started-or-startup-unwound; no resource manifest for independent verification";
  if (interrupted) {
    summary.state = "incomplete";
    process.exitCode = 1;
  }
  summary.unperformed = plan.slice(summary.attempts.length);
  summary.navigationFindings = cohortFindings(plan, summary.attempts);
  await save();
  process.removeListener("SIGINT", onSignal);
  process.removeListener("SIGTERM", onSignal);
}
