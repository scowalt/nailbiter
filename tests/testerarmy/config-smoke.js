import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { chmod, mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { startFixture } from "../browser-fixture/lifecycle.js";

if ("E2E_OAUTH_CREDENTIALS" in process.env)
  throw new Error("BLOCKED: unset E2E_OAUTH_CREDENTIALS");
const directory = await mkdtemp(join(tmpdir(), "nailbiter-pilot-config-"));
const home = join(directory, "home");
const authRoot = join(home, ".config", "nailbiter-testerarmy-pilot");
let fixture;
try {
  await mkdir(home, { mode: 0o700 });
  fixture = await startFixture();
  const env = Object.fromEntries(
    ["PATH", "USER", "TMPDIR"]
      .filter((key) => process.env[key])
      .map((key) => [key, process.env[key]]),
  );
  Object.assign(env, {
    HOME: home,
    E2E_TELEMETRY_DISABLED: "1",
    XDG_CONFIG_HOME: authRoot,
    NAILBITER_PILOT_MANIFEST: fixture.manifestPath,
  });
  const checks = [
    [{}, /explicit account-listed model ID/],
    [{ E2E_OAUTH_CREDENTIALS: "" }, /reject E2E_OAUTH_CREDENTIALS/],
    [
      {
        NAILBITER_PILOT_MODEL: "synthetic-not-an-account-model",
        XDG_CONFIG_HOME: join(directory, "not-approved-auth"),
      },
      /XDG_CONFIG_HOME must be/,
    ],
    [
      { NAILBITER_PILOT_MODEL: "synthetic-not-an-account-model" },
      /authentication missing\/unsafe/,
    ],
    [
      {
        NAILBITER_PILOT_MODEL: "synthetic-not-an-account-model",
        OPENAI_API_KEY: "synthetic-not-a-key",
      },
      /isolate paid API keys/,
    ],
  ];
  const list = (overrides) =>
    spawnSync(
      "node_modules/.bin/e2e",
      ["list", "--config", "e2e.pilot.config.ts", "--reporter", "json"],
      { env: { ...env, ...overrides }, encoding: "utf8", timeout: 30_000 },
    );
  for (const [overrides, expected] of checks) {
    const child = list(overrides);
    assert.equal(child.status, 2);
    assert.match(child.stdout + child.stderr, expected);
    console.log(`PASS: config stops clearly at ${expected.source}`);
  }
  const authDirectory = join(authRoot, "e2e");
  await mkdir(authDirectory, { recursive: true, mode: 0o700 });
  const placeholder = join(authDirectory, "oauth.json");
  await writeFile(placeholder, "", { mode: 0o600 });
  await chmod(placeholder, 0o644);
  const unsafe = list({
    NAILBITER_PILOT_MODEL: "synthetic-not-an-account-model",
  });
  assert.equal(unsafe.status, 2);
  assert.match(unsafe.stdout + unsafe.stderr, /authentication missing\/unsafe/);
  console.log("PASS: unsafe owned auth metadata rejected");
  await chmod(placeholder, 0o600);
  for (const mode of ["off", "read-write", "read-only"]) {
    const discovery = list({
      NAILBITER_PILOT_MODEL: "synthetic-not-an-account-model",
      NAILBITER_PILOT_CACHE_MODE: mode,
    });
    assert.equal(discovery.status, 0, discovery.stdout + discovery.stderr);
    const { pairs } = JSON.parse(discovery.stdout);
    assert.equal(pairs.length, 1);
    assert.equal(pairs[0].file, "tests/testerarmy/acceptance.e2e.ts");
    assert.equal(pairs[0].title, "Home → NBA → intended completed game");
    assert.equal(pairs[0].target, "desktop");
    assert.equal(pairs[0].disposition, "run");
    console.log(
      `PASS: canonical subscription-config discovery (${mode}); no model calls/auth contents`,
    );
  }
} finally {
  try {
    await fixture?.stop();
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
}
