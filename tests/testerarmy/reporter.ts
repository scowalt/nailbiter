import { writeFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import type { Reporter } from "e2e";

export function fixtureReporter(): Reporter {
  let output = "";
  return {
    name: "pilot-fixture-evidence",
    onEvent(event) {
      if (event.type === "output" && event.stream === "stdout")
        output += event.text;
    },
    async onRunFinished(run) {
      if (!run.reportPath) return;
      const records = output
        .split(/\r?\n/)
        .filter((line) => line.startsWith("PILOT_FIXTURE_EVIDENCE="))
        .map((line) =>
          JSON.parse(line.slice("PILOT_FIXTURE_EVIDENCE=".length)),
        );
      await writeFile(
        join(dirname(run.reportPath), "fixture-evidence.json"),
        `${JSON.stringify({ runId: run.report.run.id, records })}\n`,
        { mode: 0o600 },
      );
    },
  };
}
