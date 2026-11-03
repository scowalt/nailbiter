import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdir, writeFile } from "node:fs/promises";
import { isAbsolute, relative, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright";
import { startFixture } from "./lifecycle.js";
import { verifyStoppedFixture } from "./stopped-verification.js";

const artifacts = process.env.SERIES_ARTIFACT_DIR;
if (!artifacts) throw new Error("Set SERIES_ARTIFACT_DIR to an absolute directory outside the checkout.");
const path = relative(fileURLToPath(new URL("../../", import.meta.url)), artifacts);
if (!isAbsolute(artifacts) || (path !== ".." && !path.startsWith(`..${sep}`)))
  throw new Error("Browser artifacts must remain outside the checkout.");
await mkdir(artifacts, { recursive: true });
const fixture = await startFixture({ series: true });
const origin = new URL(fixture.manifest.privateUrl).origin;
const report = { endpoints: { local: fixture.manifest.localUrl, private: fixture.manifest.privateUrl }, claims: [], viewports: [] };
let browser;
const staticAssets = new Map();
const resourceErrors = [];

async function staticAsset(route) {
  const url = route.request().url();
  if (!staticAssets.has(url)) {
    const result = spawnSync("curl", ["--fail", "--silent", "--show-error", "--max-time", "10", url], { timeout: 15_000 });
    if (result.status !== 0) {
      resourceErrors.push(new URL(url).pathname);
      await route.abort();
      return;
    }
    staticAssets.set(url, result.stdout);
  }
  await route.fulfill({ contentType: url.includes("datastar.js") ? "text/javascript" : "image/png", body: staticAssets.get(url) });
}

async function context(options = {}, patchOnly = false, snapshots = false) {
  const ctx = await browser.newContext(options);
  await ctx.route("**/*", async (route) => {
    const url = new URL(route.request().url());
    if (url.origin === origin) {
      if (url.pathname === "/sse/page-sync" && !snapshots) return;
      if (patchOnly && route.request().headers()["x-nailbiter-live-snapshot"]) await route.abort();
      else await route.continue();
    } else if (url.hostname === "cdn.jsdelivr.net" && url.pathname === "/gh/starfederation/datastar@v1.0.4/bundles/datastar.js") await staticAsset(route);
    else if (url.hostname === "a.espncdn.com" && url.pathname.startsWith("/i/teamlogos/")) await staticAsset(route);
    else await route.abort();
  });
  return { ctx, page: await ctx.newPage() };
}

async function home(page) {
  assert.equal((await page.goto(`${origin}/`, { waitUntil: "domcontentloaded" })).status(), 200);
  for (const id of fixture.manifest.series.cards) await page.locator(`a[href='/game/${id}']`).waitFor();
}

try {
  browser = await chromium.launch({ headless: true });
  const initial = await fixture.control("inspect");
  assert.equal(initial.usable.length, 5);
  const noJs = await context({ javaScriptEnabled: false, viewport: { width: 390, height: 844 } });
  await home(noJs.page);
  assert.equal(await noJs.page.locator("[id^='series-']").count(), 5);
  for (const [section, id] of [["best", "401907973"], ["more", "series-nhl-long"], ["live", "series-mlb-live"], ["other", "series-nba-final"]]) {
    assert.equal(await noJs.page.locator(`#home-section-${section} a[href='/game/${id}'] #series-${id}`).count(), 1, "verified series context stays attached to its real grouped game card");
  }
  for (const id of fixture.manifest.series.cards) {
    assert.equal(await noJs.page.locator(`a[href='/game/${id}']`).count(), 1);
    assert.equal(await noJs.page.locator(`#excitement-${id}`).count(), 1, "merged footer does not duplicate the authoritative status-specific metric target");
  }
  assert.equal(await noJs.page.getByRole("heading", { name: "Today's Games", exact: true }).count(), 1);
  assert.equal(await noJs.page.locator("main [role='tab'], main select, main input, main button").count(), 0);
  assert.equal(await noJs.page.locator("#series-401907973").textContent(), "Series tied 1–1NLWC · Game 3 · Best of 3");
  assert.equal(await noJs.page.locator("#excitement-401907973").textContent(), "Hype87");
  const scheduledCard = noJs.page.locator("a[href='/game/401907973']");
  assert.match(await scheduledCard.textContent(), /#2 seed/);
  assert.match(await scheduledCard.textContent(), /#5 seed/);
  assert.equal(await scheduledCard.locator("#home-score-401907973").textContent(), "");
  assert.equal(await scheduledCard.locator("#away-score-401907973").textContent(), "");
  assert.equal(await noJs.page.locator("#excitement-series-mlb-live").textContent(), "Excitement4");
  assert.equal(await noJs.page.locator("#excitement-series-nba-final").textContent(), "Excitement4");
  assert.equal(await noJs.page.locator("#excitement-series-nhl-long").textContent(), "Hype unavailable—");
  assert.equal(await noJs.page.locator("#series-series-nba-final").textContent(), "ATL wins series 4–1NBA Finals · Game 5 · Best of 7");
  assert.match(await noJs.page.locator("#series-series-mlb-opening").textContent(), /Series tied 0–0/);
  assert.equal(await noJs.page.locator("a[href='/game/series-mlb-baseline'] [id^='series-']").count(), 0);
  assert.equal(await noJs.page.locator("main select, main input, main button").count(), 0);
  assert.equal((await fixture.control("inspect")).upstreamCalls, initial.upstreamCalls);
  report.claims.push("Real collection persisted five standings; initial no-JS HTTP has wording, Hype89, unavailable Hype and unchanged non-series card; page reads make no upstream requests.");

  for (const viewport of [{ width: 1440, height: 1000 }, { width: 390, height: 844 }, { width: 320, height: 740 }]) {
    const { page, ctx } = await context({ viewport, timezoneId: "America/New_York" });
    await home(page);
    await page.waitForFunction(() => [...document.images].filter((image) => image.loading !== "lazy").every((image) => image.complete));
    assert.deepEqual(resourceErrors, [], "Required production static CDN assets must load with normal TLS validation.");
    await page.locator("#series-series-nhl-long").scrollIntoViewIfNeeded();
    const measurements = await page.evaluate(() => {
      const round = document.getElementById("series-series-nhl-long");
      const hype = document.getElementById("excitement-series-nhl-long");
      const text = round.children[1];
      const primary = round.children[0];
      const style = getComputedStyle(text);
      const rect = text.getBoundingClientRect();
      const canvas = document.createElement("canvas");
      const paint = canvas.getContext("2d");
      const background = getComputedStyle(round.closest("a")).backgroundColor;
      paint.fillStyle = background;
      paint.fillRect(0, 0, 1, 1);
      const bg = [...paint.getImageData(0, 0, 1, 1).data].slice(0, 3);
      paint.fillStyle = style.color;
      paint.fillRect(0, 0, 1, 1);
      const fg = [...paint.getImageData(0, 0, 1, 1).data].slice(0, 3);
      const luminance = (rgb) => rgb.map((value) => {
        const channel = value / 255;
        return channel <= .04045 ? channel / 12.92 : ((channel + .055) / 1.055) ** 2.4;
      }).reduce((total, value, i) => total + value * [.2126, .7152, .0722][i], 0);
      const contrast = (Math.max(luminance(fg), luminance(bg)) + .05) / (Math.min(luminance(fg), luminance(bg)) + .05);
      return {
        viewport: innerWidth, pageWidth: document.documentElement.scrollWidth,
        cardWidth: round.closest("a").getBoundingClientRect().width,
        summaryTop: round.getBoundingClientRect().top, metricBottom: hype.getBoundingClientRect().bottom,
        context: text.textContent, fontSize: style.fontSize, lineHeight: style.lineHeight,
        color: style.color, background, contextContrast: Math.round(contrast * 100) / 100,
        textHeight: rect.height, textFits: text.scrollWidth <= text.clientWidth,
        primarySize: getComputedStyle(primary).fontSize,
        sparklineBelow: document.querySelector("a[href='/game/series-mlb-live'] nb-sparkline").getBoundingClientRect().top >= document.getElementById("series-series-mlb-live").getBoundingClientRect().bottom,
      };
    });
    assert.ok(measurements.pageWidth <= viewport.width, JSON.stringify(measurements));
    assert.ok(measurements.textFits, JSON.stringify(measurements));
    assert.ok(measurements.summaryTop >= measurements.metricBottom, JSON.stringify(measurements));
    assert.ok(measurements.sparklineBelow);
    assert.ok(measurements.contextContrast >= 4.5, JSON.stringify(measurements));
    assert.equal(measurements.context, "Stanley Cup Final · Game 7 · Best of 7");
    await page.evaluate(() => scrollTo(0, 0));
    await page.screenshot({ path: `${artifacts}/footer-${viewport.width}.png`, fullPage: true });
    report.viewports.push(measurements);
    await ctx.close();
  }
  report.claims.push("One desktop/phone batch (1440/390/320): long NHL round and Hype unavailable fit; no horizontal overflow; live sparkline remains below summary. Independent whole-page polling is held to inspect stable initial server HTML.");

  const keyboard = noJs.page;
  await keyboard.evaluate(() => document.activeElement?.blur());
  let focused = false;
  for (let i = 0; i < 30; i++) {
    await keyboard.keyboard.press("Tab");
    focused = await keyboard.locator("a[href='/game/401907973']").evaluate((el) => document.activeElement === el);
    if (focused) break;
  }
  assert.ok(focused, "Whole scheduled card must be reachable by keyboard Tab.");
  await keyboard.keyboard.press("Enter");
  await keyboard.waitForURL(`${origin}/game/401907973`);
  report.claims.push("Keyboard Tab reaches the whole card and Enter navigates to the real detail route with JavaScript disabled.");

  const patch = await context({ viewport: { width: 1440, height: 1000 } }, true);
  const stream = patch.page.waitForRequest((request) => new URL(request.url()).pathname === "/sse/live");
  await home(patch.page);
  await stream;
  await patch.page.waitForTimeout(500);
  const id = fixture.manifest.series.live;
  const before = await patch.page.locator(`#series-${id}`).innerHTML();
  const beforeMetric = await patch.page.locator(`#excitement-${id}`).textContent();
  assert.equal(await patch.page.locator(`#home-score-${id}`).textContent(), "7");
  assert.equal(await patch.page.locator(`#away-score-${id}`).textContent(), "4");
  assert.match(await patch.page.locator(`#probbar-${id}`).textContent(), /55%/);
  await fixture.control("publish");
  try {
    await patch.page.waitForFunction((gameId) => document.getElementById(`excitement-${gameId}`)?.textContent === "Excitement83", id);
  } catch {
    throw new Error("Actual publish did not patch the browser. Check the production Datastar client/server event compatibility; no simulated patch or fixture client override is permitted.");
  }
  const scoreRows = patch.page.locator(`#scores-${id} > div`);
  assert.equal(await scoreRows.nth(1).locator(":scope > span").textContent(), "9");
  assert.equal(await scoreRows.nth(0).locator(":scope > span").textContent(), "6");
  assert.match(await patch.page.locator(`#probbar-${id}`).textContent(), /72%/);
  assert.match(await patch.page.locator(`#clock-${id}`).textContent(), /7/);
  assert.notEqual(beforeMetric, "Excitement83");
  assert.equal(await patch.page.locator(`#series-${id}`).count(), 1);
  assert.equal(await patch.page.locator(`#series-${id}`).innerHTML(), before);
  assert.equal(await patch.page.locator(`a[href='/game/${id}'] nb-sparkline`).count(), 1);
  report.patch = { homeScore: 9, awayScore: 6, homeProbability: "72%", excitement: 83, standingCopies: 1, unchangedStanding: true };
  const league = await context({}, true);
  const leagueStream = league.page.waitForRequest((request) => new URL(request.url()).pathname === "/sse/live/mlb");
  assert.equal((await league.page.goto(`${origin}/mlb?date=2026-10-01`, { waitUntil: "domcontentloaded" })).status(), 200);
  await leagueStream;
  await league.page.waitForTimeout(500);
  await fixture.control("publish");
  await league.page.waitForFunction((gameId) => document.getElementById(`excitement-${gameId}`)?.textContent?.includes("83"), id);
  assert.equal(await league.page.locator("[id^='series-']").count(), 0);
  const detail = await context({}, true);
  const detailSync = detail.page.waitForRequest((request) => new URL(request.url()).pathname === "/sse/page-sync");
  assert.equal((await detail.page.goto(`${origin}/game/${id}`, { waitUntil: "domcontentloaded" })).status(), 200);
  await detailSync;
  report.claims.push("Stable data-init/@get initializes both home and MLB league SSE; league receives actual publish without series addition. Live detail retains its existing native page-sync request path, covered separately by the live-sync regression suite.");
  const synced = await context({ viewport: { width: 390, height: 844 } }, false, true);
  await home(synced.page);
  await synced.page.evaluate(() => {
    window.snapshotCount = 0;
    new MutationObserver(() => window.snapshotCount++).observe(document.querySelector("#main-content"), { childList: true });
  });
  await synced.page.waitForFunction(() => window.snapshotCount >= 3);
  assert.equal(await synced.page.locator("[id^='series-']").count(), 5);
  assert.equal(await synced.page.locator("#home-section-best #series-401907973").textContent(), "Series tied 1–1NLWC · Game 3 · Best of 3");
  assert.equal(await synced.page.locator("#home-section-more #series-series-nhl-long").textContent(), "Series tied 3–3Stanley Cup Final · Game 7 · Best of 7");
  assert.equal(await synced.page.locator("#home-section-live #series-series-mlb-live").textContent(), "ATL leads 2–1NLDS · Game 4 · Best of 5");
  assert.equal(await synced.page.locator("#home-section-other #series-series-nba-final").textContent(), "ATL wins series 4–1NBA Finals · Game 5 · Best of 7");
  assert.equal(await synced.page.locator("#excitement-401907973").textContent(), "Hype87");
  assert.equal(await synced.page.locator("#home-score-401907973").textContent(), "");
  assert.equal((await fixture.control("inspect")).upstreamCalls, initial.upstreamCalls);
  report.claims.push("Three actual successful page-sync snapshots retain grouped scheduled/live/final series context, unique membership, synthetic Hype87 and pregame score omission without provider collection.");
  await synced.ctx.close();
  report.staticAssets = [...staticAssets.keys()].map((url) => new URL(url).pathname);
  report.claims.push("Actual IGameUpdateService.PublishUpdateAsync via owned Unix IPC produces real Datastar SSE patches: score/probability/clock/excitement change; standing remains unchanged exactly once. Patch-only context suppresses independent whole-page polling to isolate this preapproved seam; no HTML/helper injection.");
  console.log("PASS real-backend series browser acceptance", report);
} catch (error) {
  report.failure = error.message;
  throw error;
} finally {
  await browser?.close();
  await fixture.stop();
  report.cleanup = await verifyStoppedFixture(fixture);
  console.log(report.cleanup);
  await writeFile(`${artifacts}/report.json`, `${JSON.stringify(report, null, 2)}\n`);
}
