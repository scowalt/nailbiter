import { APICallError } from "ai";
import type { E2EConfig } from "e2e";
import { desktop } from "./tests/testerarmy/desktop.js";

const syntheticModel = {
  specificationVersion: "v4",
  provider: "synthetic-failure-probe",
  modelId: "no-network",
  supportedUrls: {},
  async doGenerate() {
    console.log("SYNTHETIC_TRANSPORT_ATTEMPT");
    throw new APICallError({
      message: "synthetic quota rejection",
      url: "https://invalid.invalid/not-called",
      requestBodyValues: undefined,
      statusCode: 429,
      isRetryable: true,
      responseHeaders: { "retry-after-ms": "1" },
    });
  },
};

export default {
  ...desktop,
  tests: "tests/testerarmy/transport-smoke.e2e.ts",
  agents: { default: { model: syntheticModel } },
} satisfies E2EConfig;
