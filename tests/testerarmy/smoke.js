import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { randomUUID } from "node:crypto";
import { readFile } from "node:fs/promises";
import { homedir } from "node:os";
import { join } from "node:path";
import { startFixture } from "../browser-fixture/lifecycle.js";
import { verifyStoppedFixture } from "../browser-fixture/stopped-verification.js";

if ("E2E_OAUTH_CREDENTIALS" in process.env)
  throw new Error("BLOCKED: unset E2E_OAUTH_CREDENTIALS");
const transport = process.argv.includes("--transport");
const fixture = await startFixture();
const output = `.e2e/pilot/smoke-${randomUUID()}`;
try {
  const env = Object.fromEntries(
    ["PATH", "HOME", "USER", "TMPDIR"]
      .filter((key) => process.env[key])
      .map((key) => [key, process.env[key]]),
  );
  Object.assign(env, {
    E2E_TELEMETRY_DISABLED: "1",
    XDG_CONFIG_HOME: join(homedir(), ".config", "nailbiter-testerarmy-pilot"),
    NAILBITER_PILOT_MANIFEST: fixture.manifestPath,
  });
  const config = transport
    ? "e2e.pilot-transport.config.ts"
    : "e2e.pilot-smoke.config.ts";
  const child = spawn(
    "node_modules/.bin/e2e",
    [
      "run",
      "--config",
      config,
      "--output",
      output,
      "--retries",
      "0",
      "--workers",
      "1",
      "--no-cache",
    ],
    { env, stdio: ["ignore", "pipe", "pipe"] },
  );
  let calls = 0;
  let stdout = "";
  child.stdout.on("data", (chunk) => {
    stdout += chunk;
    process.stdout.write(chunk);
  });
  child.stderr.on("data", (chunk) => process.stderr.write(chunk));
  const code = await new Promise((resolve, reject) => {
    child.on("error", reject);
    child.on("close", resolve);
  });
  assert.equal(code, transport ? 3 : 1);
  if (transport) {
    calls = (stdout.match(/^SYNTHETIC_TRANSPORT_ATTEMPT\r?$/gm) ?? []).length;
    assert.equal(
      calls,
      2,
      "each SDK path must send exactly once; no transport retries",
    );
  }
  const report = JSON.parse(await readFile(`${output}/report.json`, "utf8"));
  assert.equal(report.schemaVersion, "report-1");
  const negativeMatchers = {
    "wrong-link": "expect.toHaveAttribute",
    "duplicate-card": "expect.toHaveCount",
    "wrong-game": "expect.toHaveURL",
    "same-teams-wrong-identity": "expect.toHaveURL",
    "listing-not-details": "expect.toHaveURL",
    "url-query": "expect.toHaveURL",
    "reversed-scores": "expect.toHaveText",
    "reversed-teams": "expect.toHaveText",
    "wrong-away-team": "expect.toHaveText",
    "wrong-home-team": "expect.toHaveText",
    "hidden-away-team": "expect.toBeVisible",
    "hidden-home-team": "expect.toBeVisible",
    "wrong-status": "expect.toHaveText",
    "hidden-status": "expect.toBeVisible",
  };
  assert.equal(report.run.results.length, transport ? 2 : 16);
  for (const result of report.run.results) {
    assert.equal(result.attempts.length, 1);
    const attempt = result.attempts[0];
    assert.equal(attempt.cleanup, "complete");
    assert.equal(attempt.index, 0);
    if (transport) {
      assert.equal(result.status, "failed");
      assert.equal(attempt.error.code, "MODEL_PROVIDER_FAILED");
      continue;
    }
    const scenario = result.titlePath[0].split(": ")[1];
    assert.ok(
      scenario === "valid" ||
        scenario === "restored" ||
        scenario in negativeMatchers,
    );
    assert.equal(
      result.status,
      scenario in negativeMatchers ? "failed" : "passed",
    );
    assert.ok(
      attempt.steps.every((step) => step.kind !== "agent"),
      "no AI navigation in oracle smoke",
    );
    if (scenario in negativeMatchers) {
      assert.equal(attempt.error.code, "ASSERTION_FAILED");
      const failure = attempt.steps.find((step) => step.status === "failed");
      assert.equal(failure.api, negativeMatchers[scenario]);
      assert.equal(failure.source.file, "tests/testerarmy/oracle.ts");
      if (scenario === "wrong-link") {
        assert.equal(
          attempt.steps.filter((step) => step.api === "locator.click").length,
          1,
          "hard link failure prevents opening details",
        );
      }
    }
  }
  console.log(
    `Deterministic-only smoke report: ${output}/report.json (gitignored; NOT AI reliability)`,
  );
} finally {
  await fixture.stop();
  await verifyStoppedFixture(fixture);
  console.log(
    "PASS: owned PostgreSQL, route and manifest removed; shared proxy untouched",
  );
}
