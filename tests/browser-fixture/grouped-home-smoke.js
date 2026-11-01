import assert from "node:assert/strict";
import { access, stat } from "node:fs/promises";
import { chromium } from "playwright";
import { startFixture } from "./lifecycle.js";
import { verifyStoppedFixture } from "./stopped-verification.js";

await assert.rejects(startFixture({ highlights: true, groupedHome: true }), /Choose one opt-in/);
const fixture = await startFixture({ groupedHome: true });
let browser;
try {
  assert.ok(fixture.manifest.groupedHome, "opt-in grouped-home manifest is required");
  assert.equal((await stat(fixture.manifest.groupedHome.controlPath)).mode & 0o777, 0o600);
  browser = await chromium.launch({ headless: true });
  const page = await browser.newPage();
  const origin = new URL(fixture.manifest.privateUrl).origin;
  await page.route("**/*", async (route) => {
    if (new URL(route.request().url()).origin === origin) await route.continue();
    else await route.abort();
  });
  await page.goto(fixture.manifest.privateUrl);
  const ids = await page.locator("#main-content a[href^='/game/']").evaluateAll((links) => links.map((link) => link.getAttribute("href").slice(6)));
  assert.deepEqual([...ids].sort(), [
    "group-best-100", "group-best-70", "group-best-nba", "group-final", "group-good-40", "group-good-6999",
    "group-live-old", "group-live-today", "group-low-3999", "group-low-zero", "group-null-early",
    "group-null-late", "group-null-tie-a", "group-null-nhl", "group-postponed", "group-suspended",
  ].sort());
  assert.equal(new Set(ids).size, ids.length);
  const inspected = await fixture.control("inspect");
  for (const [id, expected] of Object.entries({
    "group-low-zero": 0, "group-low-3999": 39.99, "group-good-40": 40,
    "group-good-6999": 69.99, "group-best-70": 70, "group-best-100": 100,
  })) {
    const game = inspected.games.find((game) => game.id === id);
    assert.ok(Math.abs(game.hype - expected) < 1e-9, `${id}: real service ${game.hype} != ${expected}`);
    assert.equal(game.complete, true);
  }
  assert.equal(inspected.games.find((game) => game.id === "group-null-early").hype, null);
  console.log("PASS: opt-in real page membership and real-service raw boundary evidence");
  await fixture.control("hype group-good-40 70");
  await page.waitForFunction(() => document.querySelector("#excitement-group-good-40")?.textContent.includes("70"));
  assert.equal((await fixture.control("inspect")).games.find((game) => game.id === "group-good-40").hype, 70);
  console.log("PASS: controlled evidence change reaches the open real page via normal snapshots");
  await fixture.control("start group-good-40 2026-10-13T15:15:00Z");
  await page.waitForFunction(() => document.querySelector("a[href='/game/group-good-40'] time")?.getAttribute("datetime")?.startsWith("2026-10-13T15:15"));
  await fixture.control("status group-good-40 in_progress");
  await page.waitForFunction(() => document.querySelector("#status-group-good-40")?.textContent.includes("Live"));
  await fixture.control("status group-good-40 final");
  await page.waitForFunction(() => document.querySelector("#status-group-good-40")?.textContent.includes("Final"));
  await fixture.control("add group-added 40 2026-10-13T16:15:00Z");
  await page.waitForFunction(() => !!document.querySelector("a[href='/game/group-added']"));
  await fixture.control("remove group-added");
  await page.waitForFunction(() => !document.querySelector("a[href='/game/group-added']"));
  console.log("PASS: start/status/add/remove controls converge on the open real page");
  const credit = page.locator("#qualification-credit-group-null-nhl");
  assert.equal(await credit.getByRole("link", { name: "MoneyPuck", exact: true }).getAttribute("href"), "https://moneypuck.com/predictions.htm");
  assert.ok((await credit.innerText()).includes("NSH"));
  assert.ok(!(await credit.innerText()).includes("OTT"));
  assert.equal(await credit.locator("xpath=ancestor::a").count(), 0);
  const partial = (await fixture.control("inspect")).games.find((game) => game.id === "group-null-nhl");
  assert.equal(partial.hype, null);
  assert.equal(partial.qualityAvailable, true);
  assert.equal(partial.closenessAvailable, true);
  console.log("PASS: selected-source sibling credit and partial evidence survive unavailable total hype");
  const scenarios = {
    "no-best": ["group-good-40", "group-good-6999", "group-live-old", "group-live-today", "group-final", "group-postponed", "group-suspended", "group-low-3999", "group-low-zero", "group-null-early", "group-null-late", "group-null-nhl", "group-null-tie-a"],
    "no-good": ["group-best-100", "group-best-70", "group-best-nba", "group-live-old", "group-live-today", "group-final", "group-postponed", "group-suspended", "group-low-3999", "group-low-zero", "group-null-early", "group-null-late", "group-null-nhl", "group-null-tie-a"],
    "no-more": ["group-best-100", "group-best-70", "group-best-nba", "group-good-40", "group-good-6999", "group-live-old", "group-live-today", "group-final", "group-postponed", "group-suspended"],
    "all-unavailable": ["group-best-100", "group-best-70", "group-best-nba", "group-good-40", "group-good-6999", "group-low-3999", "group-low-zero", "group-null-early", "group-null-late", "group-null-nhl", "group-null-tie-a"],
    "single": ["group-best-70"], "live-only": ["group-live-old", "group-live-today"],
    "other-only": ["group-final", "group-postponed", "group-suspended"], "empty": [],
  };
  for (const [scenario, expected] of Object.entries(scenarios)) {
    await fixture.control(`scenario ${scenario}`);
    await page.waitForFunction((expected) => {
      const ids = [...document.querySelectorAll("#main-content a[href^='/game/']")].map((link) => link.getAttribute("href").slice(6)).sort();
      return JSON.stringify(ids) === JSON.stringify([...expected].sort());
    }, expected);
    if (scenario === "all-unavailable") {
      const data = await fixture.control("inspect");
      assert.ok(data.games.filter((game) => game.status === "scheduled" && !game.id.startsWith("group-hidden-")).every((game) => game.hype === null));
    }
  }
  assert.ok((await page.locator("#main-content").innerText()).includes("No games scheduled for today."));
  await fixture.control("scenario mixed");
  await page.waitForFunction(() => !!document.querySelector("a[href='/game/group-best-100']"));
  console.log("PASS: empty-band/all-unavailable/single/live/other/empty presets via real snapshots");
  const beforeFailure = await page.locator("#main-content a[href^='/game/']").evaluateAll((links) => links.map((link) => link.getAttribute("href")));
  const beforeScore = await page.locator("#excitement-group-low-zero").innerText();
  await fixture.control("fail-refresh");
  const failedSnapshot = await page.request.get(fixture.manifest.privateUrl, { headers: { "X-Nailbiter-Live-Snapshot": "1" } });
  assert.equal(failedSnapshot.status(), 503);
  await fixture.control("hype group-low-zero 70");
  await page.waitForFunction(() => !document.querySelector("#live-reconnect-warning")?.classList.contains("hidden"));
  assert.deepEqual(await page.locator("#main-content a[href^='/game/']").evaluateAll((links) => links.map((link) => link.getAttribute("href"))), beforeFailure);
  assert.equal(await page.locator("#excitement-group-low-zero").innerText(), beforeScore);
  assert.ok((await fixture.control("inspect")).rejectedSnapshots > 0);
  const ordinary = await page.request.get(fixture.manifest.privateUrl);
  assert.equal(ordinary.status(), 200, "only live snapshot requests fail");
  await fixture.control("recover");
  await page.waitForFunction(() => document.querySelector("#excitement-group-low-zero")?.textContent.includes("70"));
  assert.equal((await page.request.get(fixture.manifest.privateUrl, { headers: { "X-Nailbiter-Live-Snapshot": "1" } })).status(), 200);
  console.log("PASS: server-controlled refresh failure retains last page; recovery applies latest evidence");
  await fixture.control("hype group-low-zero null");
  await page.waitForFunction(() => !document.querySelector("#excitement-group-low-zero")?.textContent.includes("70"));
  assert.equal((await fixture.control("inspect")).games.find((game) => game.id === "group-low-zero").hype, null);
  await fixture.control("hype group-low-zero 70");
  await page.waitForFunction(() => document.querySelector("#excitement-group-low-zero")?.textContent.includes("70"));
  console.log("PASS: evidence availability changes to/from unavailable on the open page");
  await fixture.control("invalid-evidence group-low-zero");
  await page.waitForFunction(() => !document.querySelector("#excitement-group-low-zero")?.textContent.includes("70"));
  const invalid = (await fixture.control("inspect")).games.find((game) => game.id === "group-low-zero");
  assert.equal(invalid.hype, null);
  assert.equal(invalid.qualityAvailable, false);
  assert.equal(invalid.closenessAvailable, true);
  await fixture.control("hype group-low-zero 0");
  await page.waitForFunction(() => document.querySelector("#excitement-group-low-zero")?.textContent.trim().endsWith("0"));
  for (const id of ["group-live-today", "group-live-old", "group-final"]) {
    assert.match(await page.locator(`#excitement-${id}`).innerText(), /Excitement\s*\d+/);
  }
  await assert.rejects(fixture.control("unknown"), /ArgumentException/);
  assert.equal((await page.request.get(`${fixture.manifest.privateUrl}/health`)).status(), 200);
  console.log("PASS: invalid input fails closed, real excitement evidence renders, invalid controls leave server healthy");
  await page.waitForFunction(() => document.querySelector("#live-reconnect-warning")?.classList.contains("hidden"), null, { timeout: 5_000 })
    .catch(() => assert.fail("Successful recovered snapshot must clear the production reconnect warning"));
  console.log("PASS: successful live snapshot clears the reconnect warning");
} finally {
  try {
    await browser?.close();
  } finally {
    await fixture.stop();
    await verifyStoppedFixture(fixture);
    if (fixture.manifest.groupedHome) {
      await assert.rejects(access(fixture.manifest.groupedHome.controlPath), { code: "ENOENT" });
    }
    console.log("PASS: owned grouped-home resources removed; shared proxy untouched");
  }
}
