import { expect } from "e2e";
import { test } from "./fixture.js";
import {
  assertDetails,
  assertListing,
  homeToNba,
  openIntended,
} from "./oracle.js";

test("Home → NBA → intended completed game", async ({
  app,
  agent,
  browser,
  screen,
}) => {
  await app.open();
  await expect(browser).toHaveURL("/");
  await agent.act(homeToNba);
  await assertListing(browser, screen);
  await agent.act(openIntended);
  await assertDetails(browser);
});
