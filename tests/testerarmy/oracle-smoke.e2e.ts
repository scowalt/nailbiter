import { test } from "@e2e-dev/web";
import { expect } from "e2e";
import { assertDetails, assertListing } from "./oracle.js";

const cases = [
  "valid",
  "wrong-link",
  "duplicate-card",
  "wrong-game",
  "same-teams-wrong-identity",
  "listing-not-details",
  "url-query",
  "reversed-scores",
  "reversed-teams",
  "wrong-away-team",
  "wrong-home-team",
  "hidden-away-team",
  "hidden-home-team",
  "wrong-status",
  "hidden-status",
  "restored",
] as const;

for (const scenario of cases) {
  test(`deterministic oracle: ${scenario}`, async ({
    app,
    browser,
    screen,
  }) => {
    await expect(await browser.cookies()).toEqual([]);
    await app.open();
    await expect(browser).toHaveURL("/");
    if (!app.baseUrl) throw new Error("setup: fixture base URL required");
    await browser.setCookies([
      { name: "pilot-smoke", value: scenario, url: app.baseUrl },
    ]);
    await screen
      .getByRole("navigation")
      .getByRole("link", { name: /NBA/ })
      .click();
    await expect(browser).toHaveURL("/nba");
    if (scenario === "wrong-link" || scenario === "duplicate-card") {
      await browser.evaluate((kind) => {
        const cards = () =>
          [
            ...document.querySelectorAll("a:not([data-oracle-duplicate])"),
          ].filter((a) =>
            ["PHO", "PSO", "107", "113"].every((text) =>
              [...a.querySelectorAll("span")].some(
                (s) => s.innerText.trim() === text,
              ),
            ),
          );
        if (cards().length !== 1)
          throw new Error("setup: intended card not unique");
        const apply = () => {
          const card = cards()[0];
          if (!card) return;
          if (
            kind === "wrong-link" &&
            card.getAttribute("href") !== "/game/pilot-nba-distraction"
          ) {
            card.setAttribute("href", "/game/pilot-nba-distraction");
          }
          if (
            kind === "duplicate-card" &&
            !document.querySelector("[data-oracle-duplicate]")
          ) {
            const copy = card.cloneNode(true) as HTMLElement;
            copy.setAttribute("data-oracle-duplicate", "true");
            card.after(copy);
          }
        };
        new MutationObserver(apply).observe(document.body, {
          subtree: true,
          childList: true,
          attributes: true,
          attributeFilter: ["href"],
        });
        apply();
        return null;
      }, scenario);
    }
    const card = await assertListing(browser, screen);
    if (scenario !== "listing-not-details") await card.click();
    if (scenario === "wrong-game")
      await browser.goto("/game/pilot-nba-distraction");
    if (scenario === "same-teams-wrong-identity" || scenario === "url-query") {
      await browser.evaluate((kind) => {
        history.replaceState(
          null,
          "",
          kind === "url-query" ? "?other=1" : "/game/another-same-team-game",
        );
        return null;
      }, scenario);
    }
    if (
      [
        "reversed-scores",
        "reversed-teams",
        "wrong-away-team",
        "wrong-home-team",
        "hidden-away-team",
        "hidden-home-team",
        "wrong-status",
        "hidden-status",
      ].includes(scenario)
    ) {
      await browser.evaluate((kind) => {
        const away = document.querySelector<HTMLElement>(
          "#detail-scoreboard > div:first-child > div:nth-child(1) > span:nth-of-type(2)",
        )!;
        const home = document.querySelector<HTMLElement>(
          "#detail-scoreboard > div:first-child > div:nth-child(3) > span:nth-of-type(2)",
        )!;
        const status = document.querySelector<HTMLElement>("#detail-status")!;
        if (kind === "reversed-scores") {
          document.querySelector("#detail-away-score")!.textContent = "113";
          document.querySelector("#detail-home-score")!.textContent = "107";
        }
        if (kind === "reversed-teams")
          [away.textContent, home.textContent] = [
            home.textContent,
            away.textContent,
          ];
        if (kind === "wrong-away-team") away.textContent = "Wrong away team";
        if (kind === "wrong-home-team") home.textContent = "Wrong home team";
        if (kind === "hidden-away-team") away.style.display = "none";
        if (kind === "hidden-home-team") home.style.display = "none";
        if (kind === "wrong-status") status.textContent = "Live";
        if (kind === "hidden-status") status.style.display = "none";
        return null;
      }, scenario);
    }
    await assertDetails(browser);
  });
}
