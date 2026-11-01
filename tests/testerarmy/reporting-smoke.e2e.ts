import { expect } from "e2e";
import { test } from "./fixture.js";
import { assertDetails, assertListing } from "./oracle.js";

test("deterministic reporting journey", async ({ app, browser, screen }) => {
  await app.open();
  await expect(browser).toHaveURL("/");
  await screen
    .getByRole("navigation")
    .getByRole("link", { name: /NBA/ })
    .click();
  const card = await assertListing(browser, screen);
  await card.click();
  await assertDetails(browser);
});
