import { readFileSync } from "node:fs";
import { web } from "@e2e-dev/web";
import type { E2EConfig } from "e2e";
import { fixtureReporter } from "./reporter.js";

const path = process.env.NAILBITER_PILOT_MANIFEST;
if (!path)
  throw new Error(
    "BLOCKED: NAILBITER_PILOT_MANIFEST from startFixture() is required",
  );
const manifest = JSON.parse(readFileSync(path, "utf8"));
if (
  manifest.version !== 1 ||
  manifest.intended.id !== "pilot-nba-completed" ||
  manifest.distraction.id !== "pilot-nba-distraction" ||
  !manifest.databaseContainerId ||
  !manifest.privateUrl.startsWith("https://")
) {
  throw new Error("BLOCKED: expected the isolated #638 fixture manifest");
}
if (process.env.E2E_TELEMETRY_DISABLED !== "1")
  throw new Error("BLOCKED: E2E_TELEMETRY_DISABLED=1 required");
if ("E2E_OAUTH_CREDENTIALS" in process.env)
  throw new Error("BLOCKED: reject E2E_OAUTH_CREDENTIALS (even empty)");

export const desktop = {
  projectId: "nailbiter-pilot-636",
  targets: [
    {
      name: "desktop",
      engine: web({
        browser: "chromium",
        viewport: { width: 1440, height: 900 },
      }),
      app: {
        url: manifest.privateUrl,
        identity: "nailbiter-pilot-636",
        environment: "test",
      },
    },
  ],
  retries: 0,
  workers: 1,
  timeout: 120_000,
  launchTimeout: 60_000,
  actionTimeout: 15_000,
  assertionTimeout: 5_000,
  cleanupTimeout: 30_000,
  trace: "on",
  video: "off",
  cache: "off",
  output: ".e2e/pilot/raw",
  reporters: ["list", fixtureReporter()],
} satisfies E2EConfig;
