import assert from "node:assert/strict";
import { access } from "node:fs/promises";
import { chromium } from "playwright";
import { installScheduleObservation, isNativeNavigationError } from "./grouped-home-observations.js";
import { startFixture } from "./lifecycle.js";
import { verifyStoppedFixture } from "./stopped-verification.js";

const fixture = await startFixture({ groupedHome: true });
let browser;
try {
  browser = await chromium.launch({ headless: true });
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 }, timezoneId: "America/Los_Angeles" });
  const pageErrors = [];
  page.on("pageerror", (error) => pageErrors.push({ name: error.name, message: error.message, stack: error.stack, url: page.url() }));

  const origin = new URL(fixture.manifest.privateUrl).origin;
  const datastarUrl = "https://cdn.jsdelivr.net/gh/starfederation/datastar@v1.0.4/bundles/datastar.js";
  await page.route("**/*", async (route) => {
    const url = route.request().url();
    if (new URL(url).origin === origin || url === datastarUrl) await route.continue();
    else await route.abort();
  });
  await page.addInitScript(installScheduleObservation);
  await page.addInitScript(() => {
    window.fragments = [];
    window.sseEvents = [];
    window.activeFetches = new Set();
    document.addEventListener("datastar-fetch", (event) => {
      const { type, el, argsRaw } = event.detail;
      window.sseEvents.push({ type, argsRaw });
      if (type === "started") window.activeFetches.add(el);
      if (type === "finished") window.activeFetches.delete(el);
      if (type === "datastar-patch-elements") window.fragments.push(argsRaw.elements);
    });
  });
  await page.goto(fixture.manifest.privateUrl);
  await page.evaluate(() => {
    window.snapshotCount = 0;
    new MutationObserver(() => window.snapshotCount++).observe(document.querySelector("#main-content"), { childList: true });
  });
  const snapshots = async (count = 3) => {
    const before = await page.evaluate(() => window.snapshotCount);
    await page.waitForFunction((target) => window.snapshotCount >= target, before + count);
  };
  const readSchedule = () => page.evaluate(() => window.readGroupedSchedule());
  const expected = [
    { heading: "Live now", ids: ["group-live-old", "group-live-today"] },
    { heading: "Best games", ids: ["group-best-nba", "group-best-100", "group-best-70"] },
    { heading: "Good games", ids: ["group-good-6999", "group-good-40"] },
    { heading: "More games", ids: ["group-null-early", "group-low-3999", "group-low-zero", "group-null-tie-a", "group-null-nhl", "group-null-late"] },
    { heading: "Other games", ids: ["group-suspended", "group-final", "group-postponed"] },
  ];
  const expectSchedule = async () => {
    await page.waitForFunction((expected) => JSON.stringify(window.readGroupedSchedule()) === JSON.stringify(expected), expected);
    const actual = await readSchedule();
    assert.deepEqual(actual, expected);
    const ids = actual.flatMap((section) => section.ids);
    assert.equal(new Set(ids).size, ids.length);
  };
  await expectSchedule();
  await fixture.control("hype group-good-40 70");
  expected[1].ids.splice(2, 0, "group-good-40");
  expected[2].ids.pop();
  await expectSchedule();
  await fixture.control("hype group-good-40 null");
  expected[1].ids.splice(2, 1);
  expected[3].ids.push("group-good-40");
  await expectSchedule();
  assert.match(await page.locator("#excitement-group-good-40").innerText(), /Hype\s+unavailable/);
  await fixture.control("hype group-good-40 40");
  expected[3].ids.pop();
  expected[2].ids.push("group-good-40");
  await expectSchedule();
  await fixture.control("start group-best-70 2026-10-13T14:30:00Z");
  expected[1].ids = ["group-best-70", "group-best-nba", "group-best-100"];
  await expectSchedule();
  assert.equal(await page.locator("a[href='/game/group-best-70'] time").innerText(), "Oct 13, 7:30 AM");
  await fixture.control("status group-best-70 in_progress");
  expected[1].ids.shift();
  expected[0].ids.push("group-best-70");
  await expectSchedule();
  assert.match(await page.locator("#excitement-group-best-70").innerText(), /Excitement unavailable/);
  assert.equal(await page.locator("#home-score-group-best-70").innerText(), "5");
  assert.equal(await page.locator("#away-score-group-best-70").innerText(), "2");
  await fixture.control("status group-best-70 final");
  expected[0].ids.pop();
  expected[4].ids.splice(2, 0, "group-best-70");
  await expectSchedule();
  assert.equal(await page.locator("#status-group-best-70").innerText(), "Final");
  assert.equal(await page.locator("#home-score-group-best-70").innerText(), "5");
  await fixture.control("add group-added 40 2026-10-13T16:15:00Z");
  expected[2].ids.unshift("group-added");
  await expectSchedule();
  await fixture.control("remove group-added");
  expected[2].ids.shift();
  await expectSchedule();
  await snapshots();
  await expectSchedule();
  await page.reload();
  await expectSchedule();
  await page.evaluate(() => {
    window.snapshotCount = 0;
    new MutationObserver(() => window.snapshotCount++).observe(document.querySelector("#main-content"), { childList: true });
  });
  console.log("PASS: raw hype/availability/start/status/membership changes converge without duplicates; unchanged ties and reload retain server order");
  await fixture.control("scenario mixed");
  expected[0].ids = ["group-live-old", "group-live-today"];
  expected[1].ids = ["group-best-nba", "group-best-100", "group-best-70"];
  expected[4].ids = ["group-suspended", "group-final", "group-postponed"];
  await expectSchedule();
  await page.waitForFunction(() => document.querySelector("nb-sparkline[game-id='group-live-today'] svg"));
  await page.evaluate(() => { window.savedSparkline = document.querySelector("nb-sparkline[game-id='group-live-today']"); });
  await snapshots();
  assert.equal(await page.evaluate(() => window.savedSparkline === document.querySelector("nb-sparkline[game-id='group-live-today']")), true);
  assert.equal(await page.locator("nb-sparkline[game-id='group-live-today'] svg").count(), 1);
  const card = page.locator("a[href='/game/group-null-nhl']");
  const credit = page.locator("#qualification-credit-group-null-nhl a");
  for (const link of [card, credit]) {
    await link.focus();
    await snapshots();
    assert.equal(await link.evaluate((el) => document.activeElement === el), true, "unchanged successful snapshots retain keyboard focus on the same usable game/credit link");
  }
  await card.focus();
  await snapshots();
  await page.keyboard.press("Tab");
  assert.equal(await credit.evaluate((el) => document.activeElement === el), true, "card and its sibling source credit remain consecutive usable keyboard links");
  await card.scrollIntoViewIfNeeded();
  const anchorTop = await card.evaluate((el) => el.getBoundingClientRect().top);
  await snapshots();
  assert.ok(Math.abs(await card.evaluate((el) => el.getBoundingClientRect().top) - anchorTop) < 1, "unchanged snapshots preserve the visible game anchor");
  console.log("PASS: card/credit keyboard access, visible anchor and sparkline instance survive repeated unchanged snapshots");
  await page.setViewportSize({ width: 390, height: 844 });
  const readingGames = ["group-reading-0", "group-reading-1", "group-reading-2", "group-reading-3", "group-reading-4", "group-reading-5"];
  for (const [index, id] of readingGames.entries()) await fixture.control(`add ${id} null 2026-10-13T16:${10 + index}:00Z`);
  expected[3].ids.splice(4, 0, ...readingGames);
  await expectSchedule();
  await card.evaluate((el) => {
    document.activeElement.blur();
    scrollBy(0, el.getBoundingClientRect().top - 200);
  });
  const readingTop = await card.evaluate((el) => el.getBoundingClientRect().top);
  const moreGeometry = await page.locator("#home-section-more").evaluate((el) => ({ top: el.getBoundingClientRect().top, height: el.getBoundingClientRect().height }));
  assert.ok(moreGeometry.top < -844 && moreGeometry.height > 844 * 2, "reader is well inside the real multi-viewport More games section");
  await fixture.control("add group-anchor-earlier null 2026-10-13T14:45:00Z");
  expected[3].ids.unshift("group-anchor-earlier");
  await expectSchedule();
  assert.ok(Math.abs(await card.evaluate((el) => el.getBoundingClientRect().top) - readingTop) < 1, "earlier insertion preserves the visible game's viewport offset after a successful snapshot");
  await fixture.control("start group-null-early 2026-10-13T16:45:00Z");
  expected[3].ids.splice(1, 1);
  expected[3].ids.splice(11, 0, "group-null-early");
  await expectSchedule();
  assert.ok(Math.abs(await card.evaluate((el) => el.getBoundingClientRect().top) - readingTop) < 1, "earlier game reordered after the visible game preserves the reading offset");
  await fixture.control("remove group-anchor-earlier");
  expected[3].ids.shift();
  await fixture.control("start group-null-early 2026-10-13T15:00:00Z");
  expected[3].ids.splice(10, 1);
  expected[3].ids.unshift("group-null-early");
  for (const id of readingGames) await fixture.control(`remove ${id}`);
  expected[3].ids.splice(4, 6);
  await expectSchedule();
  await page.setViewportSize({ width: 1440, height: 1000 });
  console.log("PASS: successful changed snapshots anchor the visible game inside More games rather than the enclosing section");
  await fixture.control("scores group-best-nba null null");
  await snapshots(2);
  for (const side of ["away", "home"]) {
    assert.equal(await page.locator(`#${side}-score-group-best-nba`).count(), 1, "nullable stored scores retain stable per-game scalar targets");
  }
  await fixture.control("fail-refresh");
  await page.waitForFunction(() => !document.querySelector("#live-reconnect-warning").classList.contains("hidden"));
  await page.waitForFunction(() => window.activeFetches.size === 1);
  await page.evaluate(() => {
    const guards = ["group-best-nba", "group-null-nhl", "group-best-70"].map((id) => ({
      id,
      teams: document.getElementById(`scores-${id}`).textContent,
      metric: document.getElementById(`excitement-${id}`).textContent,
    }));
    const credit = document.getElementById("qualification-credit-group-null-nhl").textContent;
    window.fragmentViolations = [];
    window.fragmentObserver = new MutationObserver(() => {
      for (const { id, teams, metric } of guards) {
        if (document.getElementById(`scores-${id}`)?.textContent !== teams || document.getElementById(`excitement-${id}`)?.textContent !== metric) window.fragmentViolations.push(id);
      }
      if (document.getElementById("qualification-credit-group-null-nhl")?.textContent !== credit) window.fragmentViolations.push("credit");
    });
    window.fragmentObserver.observe(document.querySelector("#main-content"), { subtree: true, childList: true, characterData: true, attributes: true });
  });
  for (const id of ["group-best-nba", "group-null-nhl", "group-best-70"]) {
    const scores = page.locator(`#scores-${id}`);
    const metric = page.locator(`#excitement-${id}`);
    const before = { teams: await scores.innerText(), metric: await metric.innerText(), probability: await page.locator(`#probbar-${id}`).innerText() };
    const received = await page.evaluate(() => window.fragments.length);
    await fixture.control(`scores ${id} 12 9`);
    await fixture.control(`publish ${id}`);
    await page.waitForFunction((count) => window.fragments.length > count, received, { timeout: 5_000 }).catch(async (error) => {
      console.error("SSE EVENTS", await page.evaluate(() => window.sseEvents));
      throw error;
    });
    await page.waitForTimeout(150);
    assert.equal(await page.evaluate(() => window.fragments.length), received + 1, "one publication reaches exactly one active subscription after repeated snapshots");
    const html = await page.evaluate(() => window.fragments.at(-1));
    assert.deepEqual(await page.evaluate(({ html, id }) => {
      const parsed = new DOMParser().parseFromString(html, "text/html");
      return ["away", "home"].map((side) => parsed.getElementById(`${side}-score-${id}`)?.textContent);
    }, { html, id }), ["", ""], "actual scheduled SSE payload suppresses nonzero stored scores");
    assert.doesNotMatch(html, new RegExp(`id="scores-${id}"`), "scalar publication cannot rebuild team evidence rows");
    assert.doesNotMatch(html, new RegExp(`id="excitement-${id}"`), "scheduled publication cannot overwrite authoritative Hype");
    assert.deepEqual({ teams: await scores.innerText(), metric: await metric.innerText(), probability: await page.locator(`#probbar-${id}`).innerText() }, before, `${id}: actual scheduled fragment preserves team ranks/context and hype without stored scorelines`);
    for (const side of ["away", "home"]) assert.equal(await page.locator(`#${side}-score-${id}`).innerText(), "");
  }
  await page.evaluate(() => window.fragmentObserver.disconnect());
  assert.deepEqual(await page.evaluate(() => window.fragmentViolations), [], "no actual fragment mutation temporarily erases hype/ranks/credit or introduces pregame scores");
  const liveReceived = await page.evaluate(() => window.fragments.length);
  await fixture.control("scores group-live-today 12 9");
  await fixture.control("publish group-live-today");
  await page.waitForFunction((count) => window.fragments.length > count, liveReceived);
  await page.waitForFunction(() => document.querySelector("#home-score-group-live-today")?.textContent === "12", null, { timeout: 5_000 });
  assert.equal(await page.locator("#away-score-group-live-today").innerText(), "9");
  assert.match(await page.locator("#excitement-group-live-today").innerText(), /Excitement\s+\d+/);
  await fixture.control("recover");
  await snapshots(1);
  console.log("PASS: actual Datastar fragment application preserves scheduled hype/scores/context; live score changes prove transport and single-subscription lifecycle");
  await page.waitForFunction(() => document.querySelector("#live-reconnect-warning").classList.contains("hidden"));
  const retained = await readSchedule();
  const teamContext = await page.locator("#scores-group-null-nhl").innerText();
  await fixture.control("fail-refresh");
  await page.waitForFunction(() => !document.querySelector("#live-reconnect-warning").classList.contains("hidden"));
  const rejectedBefore = (await fixture.control("inspect")).rejectedSnapshots;
  await fixture.control("remove group-good-40");
  await fixture.control("hype group-good-6999 70");
  await fixture.control("start group-null-nhl 2026-10-13T14:15:00Z");
  const rejectionDeadline = Date.now() + 10_000;
  while ((await fixture.control("inspect")).rejectedSnapshots < rejectedBefore + 3) {
    assert.ok(Date.now() < rejectionDeadline, "normal snapshots keep attempting refresh during failure");
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  assert.deepEqual(await readSchedule(), retained, "failed snapshots retain the last successful complete grouped schedule");
  assert.equal(await page.locator("#scores-group-null-nhl").innerText(), teamContext);
  await fixture.control("recover");
  expected[1].ids = ["group-best-nba", "group-best-100", "group-good-6999", "group-best-70"];
  expected.splice(2, 1);
  expected[2].ids = ["group-null-nhl", "group-null-early", "group-low-3999", "group-low-zero", "group-null-tie-a", "group-null-late"];
  await expectSchedule();
  await page.waitForFunction(() => document.querySelector("#live-reconnect-warning").classList.contains("hidden"));
  assert.equal(await page.locator("#scores-group-null-nhl").innerText(), teamContext);
  assert.match(await page.locator("#qualification-credit-group-null-nhl").innerText(), /NSH playoff probability:/);
  assert.doesNotMatch(await page.locator("#qualification-credit-group-null-nhl").innerText(), /OTT/);
  assert.equal(await credit.getAttribute("href"), "https://moneypuck.com/predictions.htm");
  assert.equal(await credit.locator("xpath=ancestor::a").count(), 0);
  assert.equal(await card.locator("time").innerText(), "Oct 13, 7:15 AM");
  await snapshots();
  await expectSchedule();
  console.log("PASS: failed snapshots retain membership/order/context; recovery removes empty headings, moves attributed context and clears warning");
  await fixture.control("scenario single");
  expected.splice(0, expected.length, { heading: "Best games", ids: ["group-best-70"] });
  await expectSchedule();
  assert.equal(await page.locator("#qualification-credit-group-null-nhl").count(), 0, "removed membership cannot leave orphan source credits");
  await fixture.control("status group-best-70 in_progress");
  expected[0].heading = "Live now";
  await expectSchedule();
  await fixture.control("status group-best-70 final");
  expected[0].heading = "Other games";
  await expectSchedule();
  await fixture.control("remove group-best-70");
  expected.length = 0;
  await expectSchedule();
  assert.match(await page.locator("#main-content").innerText(), /No games scheduled for today\./);
  console.log("PASS: last-game scheduled/live/final/removal creates and removes the entire authoritative section tree");
  await fixture.control("scenario mixed");
  await fixture.control("fail-refresh");
  await page.goto(`${origin}/nba?date=2026-10-13`);
  await page.waitForFunction(() => window.activeFetches.size === 1);
  assert.equal((await page.locator("#scores-group-best-nba").innerText()).replace(/\s+/g, " ").trim(), "NYK #2 seed 0 @ BOS #1 seed 0");
  assert.equal((await page.locator("#excitement-group-best-nba").innerText()).replace(/\s+/g, " ").trim(), "Hype 90");
  await page.evaluate(() => {
    window.leagueViolations = [];
    window.leagueObserver = new MutationObserver(() => {
      const teams = [...document.querySelectorAll("#scores-group-best-nba span:not([id])")].map((el) => el.textContent.trim());
      const metric = document.querySelector("#excitement-group-best-nba")?.textContent.replace(/\s+/g, " ").trim();
      if (JSON.stringify(teams) !== JSON.stringify(["NYK", "#2 seed", "@", "BOS", "#1 seed"]) || metric !== "Hype 90") window.leagueViolations.push({ teams, metric });
    });
    window.leagueObserver.observe(document.querySelector("#main-content"), { subtree: true, childList: true, characterData: true });
  });
  const scheduledListingReceived = await page.evaluate(() => window.fragments.length);
  await fixture.control("publish group-best-nba");
  await page.waitForFunction((count) => window.fragments.length > count, scheduledListingReceived);
  assert.equal((await page.locator("#scores-group-best-nba").innerText()).replace(/\s+/g, " ").trim(), "NYK #2 seed 0 @ BOS #1 seed 0", "actual scheduled pro league publication preserves postseason seed/team rows while snapshots fail");
  assert.equal((await page.locator("#excitement-group-best-nba").innerText()).replace(/\s+/g, " ").trim(), "Hype 90", "actual scheduled league publication preserves authoritative Hype");
  await page.evaluate(() => window.leagueObserver.disconnect());
  assert.deepEqual(await page.evaluate(() => window.leagueViolations), [], "actual scheduled league fragment never transiently removes seeds or Hype");
  await fixture.control("scores group-best-nba null null");
  await fixture.control("status group-live-today scheduled");
  await fixture.control("scores group-live-today null null");
  await page.goto(`${origin}/nba?date=2026-10-13`);
  await page.waitForFunction(() => window.activeFetches.size === 1);
  for (const id of ["group-best-nba", "group-live-today"]) {
    for (const side of ["home", "away"]) {
      assert.equal(await page.locator(`#${side}-score-${id}`).count(), 1, "nullable league scores retain a stable scalar target in the real initial page");
      assert.equal(await page.locator(`#${side}-score-${id}`).innerText(), "");
    }
  }
  const nullableListingReceived = await page.evaluate(() => window.fragments.length);
  await fixture.control("scores group-best-nba 13 10");
  await fixture.control("publish group-best-nba");
  await page.waitForFunction((count) => window.fragments.length > count && document.querySelector("#home-score-group-best-nba")?.textContent === "13", nullableListingReceived);
  assert.equal(await page.locator("#away-score-group-best-nba").innerText(), "10");
  assert.equal((await page.locator("#scores-group-best-nba").innerText()).replace(/\s+/g, " ").trim(), "NYK #2 seed 10 @ BOS #1 seed 13");
  assert.equal((await page.locator("#excitement-group-best-nba").innerText()).replace(/\s+/g, " ").trim(), "Hype 90");
  const clearedListingReceived = await page.evaluate(() => window.fragments.length);
  await fixture.control("scores group-best-nba null null");
  await fixture.control("publish group-best-nba");
  await page.waitForFunction((count) => window.fragments.length > count && document.querySelector("#home-score-group-best-nba")?.textContent === "", clearedListingReceived);
  assert.equal(await page.locator("#away-score-group-best-nba").innerText(), "");
  assert.equal((await page.locator("#excitement-group-live-today").innerText()).replace(/\s+/g, " ").trim(), "Hype unavailable");
  await fixture.control("status group-live-today in_progress");
  assert.match(await page.locator("#scores-group-live-today").innerText(), /NYK\s+Playoff odds unavailable[\s\S]*BOS\s+Playoff odds unavailable/);
  const listingReceived = await page.evaluate(() => window.fragments.length);
  await fixture.control("scores group-live-today 21 18");
  await fixture.control("publish group-live-today");
  await page.waitForFunction((count) => window.fragments.length > count && document.querySelector("#scores-group-live-today")?.textContent.includes("21") && document.querySelector("#scores-group-live-today")?.textContent.includes("18"), listingReceived);
  assert.equal(await page.locator("a[href='/game/group-live-today']").count(), 1);
  assert.match(await page.locator("#scores-group-live-today").innerText(), /NYK\s+Playoff odds unavailable[\s\S]*BOS\s+Playoff odds unavailable/, "actual live pro league score publication preserves qualification context");
  assert.match(await page.locator("#excitement-group-live-today").innerText(), /Excitement\s+\d+/);
  await fixture.control("status group-null-nhl in_progress");
  await page.goto(`${origin}/nhl?date=2026-10-13`);
  await page.waitForFunction(() => window.activeFetches.size === 1);
  assert.match(await page.locator("#scores-group-null-nhl").innerText(), /NSH\s+2nd in NHL[\s\S]*OTT\s+1st in NHL/);
  const rankedListingReceived = await page.evaluate(() => window.fragments.length);
  await fixture.control("scores group-null-nhl 21 18");
  await fixture.control("publish group-null-nhl");
  await page.waitForFunction((count) => window.fragments.length > count && document.querySelector("#home-score-group-null-nhl")?.textContent === "21", rankedListingReceived);
  assert.equal(await page.locator("#away-score-group-null-nhl").innerText(), "18");
  assert.match(await page.locator("#scores-group-null-nhl").innerText(), /NSH\s+2nd in NHL[\s\S]*OTT\s+1st in NHL/, "actual ranked-live league publication preserves rankings with snapshots held failed");
  assert.equal(await page.locator("#qualification-credit-group-null-nhl").innerText(), "NSH playoff probability: MoneyPuck model estimate.");
  assert.equal(await page.locator("#qualification-credit-group-null-nhl a").getAttribute("href"), "https://moneypuck.com/predictions.htm");
  console.log("PASS: actual scheduled and ranked-live league publications preserve seeds/rankings/credit/Hype; nullable scalar targets receive positive/cleared scores and live Excitement while snapshots fail");
  await page.goto(`${origin}/nba?date=2026-10-13`);
  await page.waitForFunction(() => window.activeFetches.size === 1);
  await page.locator("a[href='/game/group-live-today']").focus();
  await page.keyboard.press("Enter");
  await page.waitForURL(`${origin}/game/group-live-today`);
  await page.waitForFunction(() => window.activeFetches.size === 1, null, { timeout: 5_000 });
  await page.waitForFunction(() => document.querySelector("nb-win-probability-chart")?.chart);
  await page.evaluate(() => {
    window.savedChartElement = document.querySelector("nb-win-probability-chart");
    window.savedChart = window.savedChartElement.chart;
    window.savedCanvas = window.savedChartElement.querySelector("canvas");
  });
  const detailReceived = await page.evaluate(() => window.fragments.length);
  await fixture.control("scores group-live-today 22 19");
  await fixture.control("publish group-live-today");
  await page.waitForFunction((count) => window.fragments.length > count && document.querySelector("#detail-home-score")?.textContent === "22", detailReceived);
  assert.equal(await page.locator("#detail-away-score").innerText(), "19");
  assert.match(await page.locator("#detail-excitement").innerText(), /Excitement\s+\d+/);
  await fixture.control("status group-live-today final");
  await fixture.control("publish group-live-today");
  await page.waitForFunction(() => document.querySelector("#detail-status")?.textContent === "Final");
  await fixture.control("recover");
  await page.waitForFunction(() => document.querySelector("[data-live-terminal='true']") && window.activeFetches.size === 0);
  assert.equal(await page.evaluate(() => {
    const el = document.querySelector("nb-win-probability-chart");
    return el === window.savedChartElement && el.chart === window.savedChart && el.querySelector("canvas") === window.savedCanvas;
  }), true, "authoritative final snapshot preserves chart/canvas state and cleans up fragment subscription");
  const nativeNavigationErrors = pageErrors.filter(isNativeNavigationError);
  assert.deepEqual(pageErrors.filter((error) => !nativeNavigationErrors.includes(error)), [], "production assets run without browser script errors");
  if (nativeNavigationErrors.length) console.log("NOTE: native Chromium cross-document view-transition rejection (also reproduced without Datastar, snapshots or publications)");
  console.log("PASS: compatible production client applies real listing/detail fragments; keyboard Enter opens detail; final snapshot preserves chart and stops owned subscription");
} finally {
  try {
    await browser?.close();
  } finally {
    await fixture.stop();
    await verifyStoppedFixture(fixture);
    await assert.rejects(access(fixture.manifest.groupedHome.controlPath), { code: "ENOENT" });
    console.log("PASS: owned live fixture resources removed; shared proxy untouched");
  }
}
