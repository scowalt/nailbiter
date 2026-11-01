import type { Browser } from "@e2e-dev/web";
import { expect, type Screen } from "e2e";

export const intendedPath = "/game/pilot-nba-completed";
export const homeToNba =
  "Navigate from the homepage to the NBA league using the visible navigation.";
export const openIntended =
  "Open the completed NBA game showing PHO (Pilot Harbor Otters) 107 away and PSO (Pilot Summit Owls) 113 home. Use its visible game card.";

export function intendedCard(screen: Screen) {
  return screen
    .getByRole("link", { visible: true })
    .filter({ has: screen.getByText("PHO", { visible: true }) })
    .filter({ has: screen.getByText("PSO", { visible: true }) })
    .filter({ has: screen.getByText("107", { visible: true }) })
    .filter({ has: screen.getByText("113", { visible: true }) });
}

export async function assertListing(browser: Browser, screen: Screen) {
  await expect(browser).toHaveURL("/nba");
  const card = intendedCard(screen);
  await expect(card).toHaveCount(1);
  await expect(card).toBeVisible();
  await expect(card).toHaveAttribute("href", intendedPath);
  return card;
}

export async function assertDetails(browser: Browser) {
  await expect(browser).toHaveURL(intendedPath);
  const scoreboard = browser.locator("#detail-scoreboard");
  await expect(scoreboard).toHaveCount(1);
  await expect(scoreboard).toBeVisible();
  const away = browser.locator(
    "#detail-scoreboard > div:first-child > div:nth-child(1)",
  );
  const home = browser.locator(
    "#detail-scoreboard > div:first-child > div:nth-child(3)",
  );
  const awayName = browser.locator(
    "#detail-scoreboard > div:first-child > div:nth-child(1) > span:nth-of-type(2)",
  );
  const homeName = browser.locator(
    "#detail-scoreboard > div:first-child > div:nth-child(3) > span:nth-of-type(2)",
  );
  await expect(away).toBeVisible();
  await expect(home).toBeVisible();
  await expect(awayName).toBeVisible();
  await expect(awayName).toHaveText("Pilot Harbor Otters");
  await expect(homeName).toBeVisible();
  await expect(homeName).toHaveText("Pilot Summit Owls");
  const awayScore = browser.locator(
    "#detail-scoreboard > div:first-child > div:nth-child(1) > #detail-away-score",
  );
  const homeScore = browser.locator(
    "#detail-scoreboard > div:first-child > div:nth-child(3) > #detail-home-score",
  );
  await expect(awayScore).toBeVisible();
  await expect(awayScore).toHaveText("107");
  await expect(homeScore).toBeVisible();
  await expect(homeScore).toHaveText("113");
  const status = browser.locator("main #detail-status");
  await expect(status).toBeVisible();
  await expect(status).toHaveText("Final");
}
