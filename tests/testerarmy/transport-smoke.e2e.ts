import { test } from "@e2e-dev/web";

test("transport act stops on quota", async ({ app, agent }) => {
  await app.open();
  await agent.act("Navigate to NBA.");
});
test("transport judgment stops on quota", async ({ app, agent }) => {
  await app.open();
  await agent.assert("The homepage is visible.");
});
