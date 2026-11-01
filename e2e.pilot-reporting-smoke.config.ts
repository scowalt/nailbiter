import type { E2EConfig } from "e2e";
import { desktop } from "./tests/testerarmy/desktop.js";

export default {
  ...desktop,
  tests: "tests/testerarmy/reporting-smoke.e2e.ts",
  assertionTimeout: 1_000,
} satisfies E2EConfig;
