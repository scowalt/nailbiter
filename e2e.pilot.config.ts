import { lstatSync } from "node:fs";
import { homedir } from "node:os";
import { join, relative, resolve } from "node:path";
import type { E2EConfig } from "e2e";
import { chatgpt } from "e2e/oauth/chatgpt";
import { desktop } from "./tests/testerarmy/desktop.js";

const modelId = process.env.NAILBITER_PILOT_MODEL;
if (!modelId?.trim() || modelId !== modelId.trim()) {
  throw new Error(
    "BLOCKED: set NAILBITER_PILOT_MODEL to an explicit account-listed model ID verified by the coordinator",
  );
}
const authRoot = join(homedir(), ".config", "nailbiter-testerarmy-pilot");
if (process.env.XDG_CONFIG_HOME !== authRoot)
  throw new Error(`BLOCKED: XDG_CONFIG_HOME must be ${authRoot}`);
if (
  Object.keys(process.env).some((key) =>
    /(?:API_KEY|ACCESS_TOKEN|OIDC_TOKEN)$/.test(key),
  )
) {
  throw new Error(
    "BLOCKED: isolate paid API keys/tokens; use an allowlisted runner environment",
  );
}
try {
  const auth = lstatSync(`${authRoot}/e2e/oauth.json`);
  if (!auth.isFile() || (auth.mode & 0o077) !== 0)
    throw new Error("unsafe auth file");
} catch {
  throw new Error(
    "BLOCKED: dedicated TesterArmy subscription authentication missing/unsafe; coordinator must complete approved authorization",
  );
}

const cacheMode = process.env.NAILBITER_PILOT_CACHE_MODE ?? "off";
if (
  cacheMode !== "off" &&
  cacheMode !== "read-write" &&
  cacheMode !== "read-only"
) {
  throw new Error(
    "BLOCKED: NAILBITER_PILOT_CACHE_MODE must be off, read-write or read-only",
  );
}

const cacheDir = process.env.NAILBITER_PILOT_CACHE_DIR ?? ".e2e/pilot/cache";
const cacheRelative = relative(resolve(".e2e/pilot"), resolve(cacheDir));
if (
  !cacheRelative ||
  cacheRelative.startsWith("..") ||
  !cacheRelative.endsWith("cache")
)
  throw new Error(
    "BLOCKED: cache must be a child cache directory under ignored .e2e/pilot",
  );

export default {
  ...desktop,
  tests: "tests/testerarmy/acceptance.e2e.ts",
  cache: { mode: cacheMode, dir: cacheDir, strict: false },
  agents: {
    default: { model: chatgpt(modelId), maxSteps: 12, maxModelCalls: 12 },
  },
} satisfies E2EConfig;
