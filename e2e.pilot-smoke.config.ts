import type { E2EConfig } from "e2e";
import { desktop } from "./tests/testerarmy/desktop.js";

export default {
  ...desktop,
  assertionTimeout: 1_000,
  tests: "tests/testerarmy/oracle-smoke.e2e.ts",
} satisfies E2EConfig;
