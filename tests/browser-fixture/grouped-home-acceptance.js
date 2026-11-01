import assert from "node:assert/strict";
import { access, mkdir, writeFile } from "node:fs/promises";
import { resolve, sep } from "node:path";
import { chromium } from "playwright";
import { installScheduleObservation, isNativeNavigationError } from "./grouped-home-observations.js";
import { startFixture } from "./lifecycle.js";
import { verifyStoppedFixture } from "./stopped-verification.js";

const evidence = process.env.GROUPED_HOME_EVIDENCE_DIR && resolve(process.env.GROUPED_HOME_EVIDENCE_DIR);
assert.ok(!evidence || (evidence !== resolve(".") && !evidence.startsWith(`${resolve(".")}${sep}`)), "synthetic visual evidence must stay outside the repository");
if (evidence) await mkdir(evidence, { recursive: true });
const mixed = [
  { heading: "Live now", ids: ["group-live-old", "group-live-today"] },
  { heading: "Best games", ids: ["group-best-nba", "group-best-100", "group-best-70"] },
  { heading: "Good games", ids: ["group-good-6999", "group-good-40"] },
  { heading: "More games", ids: ["group-null-early", "group-low-3999", "group-low-zero", "group-null-tie-a", "group-null-nhl", "group-null-late"] },
  { heading: "Other games", ids: ["group-suspended", "group-final", "group-postponed"] },
];
const unavailable = [{ heading: "More games", ids: ["group-null-early", "group-low-3999", "group-low-zero", "group-null-tie-a", "group-null-nhl", "group-null-late", "group-best-nba", "group-best-100", "group-good-6999", "group-good-40", "group-best-70"] }];
const fixture = await startFixture({ groupedHome: true });
let browser;
const report = [];
try {
  browser = await chromium.launch({ headless: true });
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 }, timezoneId: "America/Los_Angeles" });
  const errors = [];
  page.on("pageerror", (error) => errors.push({ name: error.name, message: error.message, stack: error.stack }));
  const origin = new URL(fixture.manifest.privateUrl).origin;
  const datastarUrl = "https://cdn.jsdelivr.net/gh/starfederation/datastar@v1.0.4/bundles/datastar.js";
  await page.route("**/*", async (route) => {
    const url = route.request().url();
    if (new URL(url).origin === origin || url === datastarUrl) await route.continue();
    else await route.abort();
  });
  await page.addInitScript(installScheduleObservation);
  await page.addInitScript(() => {
    window.snapshotCount = 0;
    window.fragmentCount = 0;
    document.addEventListener("datastar-fetch", (event) => {
      if (event.detail.type === "datastar-patch-elements") window.fragmentCount++;
    });
    document.addEventListener("DOMContentLoaded", () => {
      new MutationObserver(() => window.snapshotCount++).observe(document.querySelector("#main-content"), { childList: true });
    });
  });
  const schedule = () => page.evaluate(() => window.readGroupedSchedule());
  const expectSchedule = async (expected) => {
    await page.waitForFunction((expected) => JSON.stringify(window.readGroupedSchedule()) === JSON.stringify(expected), expected);
    assert.deepEqual(await schedule(), expected);
    const ids = expected.flatMap((section) => section.ids);
    assert.deepEqual(await page.locator("#main-content a[href^='/game/']").evaluateAll((links) => links.map((link) => link.getAttribute("href"))), ids.map((id) => `/game/${id}`));
    assert.equal(new Set(ids).size, ids.length);
    assert.equal(await page.getByRole("heading", { level: 1, name: "Today's Games", exact: true }).count(), 1);
    assert.equal(await page.locator("#main-content h1, #main-content h2, #main-content h3, #main-content h4").count(), 1 + expected.length);
    for (const section of expected) assert.equal(await page.getByRole("region", { name: section.heading, exact: true }).count(), 1);
    assert.equal(await page.locator("#main-content button, #main-content select, #main-content input, #main-content [role='tab'], #main-content details").count(), 0);
    assert.equal(await page.locator("#main-content h1").evaluate((heading) => heading.parentElement.children.length), 2, "no subtitle, counts, prototype controls or debug footer compete with the schedule");
    assert.doesNotMatch(await page.locator("#main-content").innerText(), /Start time|Ascending|Descending|\b\d+ games\b|70\s*[–-]\s*100|40\s*[–-]\s*69|prototype|snapshot|inspector|debug|variant|watch-worthiness|hype estimates/i);
  };
  const snapshots = async () => {
    const before = await page.evaluate(() => window.snapshotCount);
    await page.waitForFunction((target) => window.snapshotCount >= target, before + 2);
  };
  const response = await page.goto(origin);
  assert.equal(response.status(), 200);
  const initial = await response.text();
  assert.deepEqual(await page.evaluate((html) => window.readGroupedSchedule(new DOMParser().parseFromString(html, "text/html")), initial), mixed, "initial production HTML has the same authoritative literal schedule as the live DOM");
  await expectSchedule(mixed);
  for (const [id, text] of [
    ["group-best-nba", "Hype 90"], ["group-best-100", "Hype 100"], ["group-best-70", "Hype 70"],
    ["group-good-6999", "Hype 70"], ["group-good-40", "Hype 40"], ["group-low-3999", "Hype 40"], ["group-low-zero", "Hype 0"],
    ["group-null-early", "Hype unavailable —"], ["group-null-tie-a", "Hype unavailable —"], ["group-null-nhl", "Hype unavailable —"], ["group-null-late", "Hype unavailable —"],
  ]) {
    assert.equal((await page.locator(`#excitement-${id}`).innerText()).replace(/\s+/g, " ").trim(), text);
    for (const side of ["home", "away"]) assert.equal(await page.locator(`#${side}-score-${id}`).innerText(), "", `${id} has no unstarted scoreline`);
  }
  const metricColor = (id) => page.locator(`#excitement-${id} > span:last-child`).evaluate((el) => getComputedStyle(el).color);
  assert.equal(await metricColor("group-good-6999"), await metricColor("group-good-40"));
  assert.notEqual(await metricColor("group-good-6999"), await metricColor("group-best-70"), "rounded 70 retains the raw Good band interpretation");
  assert.equal(await metricColor("group-low-3999"), await metricColor("group-low-zero"));
  assert.notEqual(await metricColor("group-low-3999"), await metricColor("group-good-40"), "rounded 40 retains the raw More band interpretation");
  for (const id of ["group-live-old", "group-live-today", "group-final"]) {
    assert.match(await page.locator(`#excitement-${id}`).innerText(), /Excitement\s+\d+/);
    assert.equal(await page.locator(`#home-score-${id}`).innerText(), "5");
    assert.equal(await page.locator(`#away-score-${id}`).innerText(), "2");
  }
  for (const [id, status] of [["group-suspended", "Suspended"], ["group-final", "Final"], ["group-postponed", "Postponed"]]) {
    assert.equal(await page.locator(`#status-${id}`).innerText(), status);
    assert.doesNotMatch(await page.locator(`a[href='/game/${id}']`).innerText(), /Hype/);
  }
  assert.equal(new Date(await page.locator("a[href='/game/group-live-old'] time").getAttribute("datetime")).toISOString(), "2026-10-12T14:00:00.000Z");
  assert.equal(await page.locator("a[href='/game/group-live-today'] time").innerText(), "Oct 13, 6:00 AM");
  for (const id of ["group-hidden-preseason", "group-hidden-canceled", "group-hidden-world-cup"]) assert.equal(await page.locator(`a[href='/game/${id}']`).count(), 0);
  const college = page.locator("a[href='/game/group-best-70']");
  assert.match(await college.innerText(), /TEX2/);
  assert.match(await college.innerText(), /ALA1/);
  assert.match(await college.innerText(), /CFP elimination game/);
  assert.deepEqual(await college.locator("sup").evaluateAll((els) => els.map((el) => el.getAttribute("aria-label"))), ["AP rank 2", "AP rank 1"]);
  assert.equal(await college.locator("img[alt='TEX']").count(), 1);
  assert.equal(await college.locator("img[alt='TEX']").getAttribute("src"), "https://a.espncdn.com/i/teamlogos/ncaa/500/251.png");
  assert.equal(await college.locator("img[alt='ALA']").count(), 1);
  assert.equal(await college.locator("img[alt='ALA']").getAttribute("src"), "https://a.espncdn.com/i/teamlogos/ncaa/500/333.png");
  assert.match(await page.locator("#scores-group-best-nba").innerText(), /NYK[\s\S]*BOS/);
  assert.match(await page.locator("#scores-group-best-nba").innerText(), /NYK\s+#2 seed\s+BOS\s+#1 seed/);
  const nhl = page.locator("a[href='/game/group-null-nhl']");
  const credit = page.locator("#qualification-credit-group-null-nhl a");
  assert.match(await nhl.innerText(), /NSH[\s\S]*2nd in NHL \| 25 % playoff odds \(model estimate\)[\s\S]*OTT[\s\S]*1st in NHL \| 57 % playoff odds/);
  assert.equal(await page.locator("#qualification-credit-group-null-nhl").innerText(), "NSH playoff probability: MoneyPuck model estimate.");
  assert.equal(await credit.getAttribute("href"), "https://moneypuck.com/predictions.htm");
  assert.equal(await credit.locator("xpath=ancestor::a").count(), 0);
  assert.equal(await credit.evaluate((el) => el.parentElement.parentElement.querySelector("a[href^='/game/']")?.getAttribute("href")), "/game/group-null-nhl");
  assert.match(await page.locator("#excitement-group-null-nhl").ariaSnapshot(), /Hype unavailable/);
  assert.doesNotMatch(await page.locator("#excitement-group-null-nhl").ariaSnapshot(), /—/);
  const keyboardOrder = ["group-live-old", "group-live-today", "group-best-nba", "group-best-100", "group-best-70", "group-good-6999", "group-good-40", "group-null-early", "group-low-3999", "group-low-zero", "group-null-tie-a", "group-null-nhl", "credit", "group-null-late", "group-suspended", "group-final", "group-postponed"];
  await page.locator("a[href='/game/group-live-old']").focus();
  for (const [index, id] of keyboardOrder.entries()) {
    assert.equal(await page.evaluate(() => document.activeElement.getAttribute("href")), id === "credit" ? "https://moneypuck.com/predictions.htm" : `/game/${id}`, "keyboard reading order follows the literal visible schedule including its separate credit");
    if (index === 6) await snapshots();
    if (index < keyboardOrder.length - 1) await page.keyboard.press("Tab");
  }
  console.log("PASS: initial HTML/live DOM literal bands, raw-rounding display/color, zero/dash, exact-once hidden/date/live membership, scores, cross-league evidence and selected sibling credit");
  await nhl.focus();
  await page.keyboard.press("Enter");
  await page.waitForURL(`${origin}/game/group-null-nhl`);
  assert.match(await page.locator("#detail-hype").innerText(), /Stakes\s+Unavailable[\s\S]*Quality\s+97[\s\S]*Closeness\s+100/);
  assert.match(await page.locator("#detail-hype-status").innerText(), /Hype unavailable/);
  await page.goto(origin);
  await expectSchedule(mixed);
  for (const query of ["?variant=a&sort=hype&direction=asc", "?variant=c&sort=start&order=desc&view=ranked", "?prototype=b&hypeSort=ascending&date=2026-10-12"]) {
    await page.goto(`${origin}/${query}`);
    await expectSchedule(mixed);
    await snapshots();
    await page.reload();
    await expectSchedule(mixed);
  }
  assert.deepEqual(await page.evaluate(() => Object.keys(localStorage)), [], "fixed home requires no persisted sort preference");
  await page.goto(origin);
  await expectSchedule(mixed);
  const moving = page.locator("a[href='/game/group-good-40']");
  await moving.focus();
  await fixture.control("hype group-good-40 70");
  await page.waitForFunction(() => document.querySelector("#home-section-best a[href='/game/group-good-40']"));
  assert.equal(await moving.evaluate((el) => document.activeElement === el), true, "keyboard focus follows the same game across a band move");
  await fixture.control("hype group-good-40 null");
  await page.waitForFunction(() => document.querySelector("#home-section-more a[href='/game/group-good-40']"));
  assert.equal(await moving.evaluate((el) => document.activeElement === el), true);
  await credit.focus();
  await fixture.control("start group-null-nhl 2026-10-13T14:15:00Z");
  await page.waitForFunction(() => document.querySelector("#home-section-more a[href^='/game/']")?.getAttribute("href") === "/game/group-null-nhl");
  assert.equal(await credit.evaluate((el) => document.activeElement === el), true, "reordered source focus remains scoped to its own game-specific credit");
  console.log("PASS: fixed policy on obsolete URL params, successful snapshots, reload and returning home; card/credit focus follows live membership/order changes");
  await fixture.control("scenario mixed");
  await fixture.control("start group-good-40 2026-10-13T03:59:00Z");
  await expectSchedule([mixed[0], mixed[1], { heading: "Good games", ids: ["group-good-6999"] }, mixed[3], mixed[4]]);
  await fixture.control("start group-good-40 2026-10-14T04:00:00Z");
  await snapshots();
  await expectSchedule([mixed[0], mixed[1], { heading: "Good games", ids: ["group-good-6999"] }, mixed[3], mixed[4]]);
  await fixture.control("start group-good-40 2026-10-13T04:00:00Z");
  await expectSchedule([mixed[0], mixed[1], { heading: "Good games", ids: ["group-good-40", "group-good-6999"] }, mixed[3], mixed[4]]);
  console.log("PASS: exact Eastern-day boundaries exclude scheduled games outside today while previous-day live membership remains once");
  const states = [
    ["no-best", [mixed[0], mixed[2], mixed[3], mixed[4]], "Good games"],
    ["no-good", [mixed[0], mixed[1], mixed[3], mixed[4]], "Best games"],
    ["no-more", [mixed[0], mixed[1], mixed[2], mixed[4]], "Best games"],
    ["all-unavailable", unavailable, "More games"],
    ["single", [{ heading: "Best games", ids: ["group-best-70"] }], "Best games"],
    ["live-only", [mixed[0]], null],
    ["other-only", [mixed[4]], null],
    ["empty", [], null],
  ];
  for (const [name, expected, firstPregame] of states) {
    await fixture.control(`scenario ${name}`);
    await expectSchedule(expected);
    assert.equal((await schedule()).find((section) => ["Best games", "Good games", "More games"].includes(section.heading))?.heading ?? null, firstPregame);
    if (name === "all-unavailable") {
      for (const id of unavailable[0].ids) assert.match(await page.locator(`#excitement-${id}`).innerText(), /Hype\s+unavailable\s+—/);
    }
    if (name === "empty") assert.equal((await page.locator("#main-content").innerText()).trim(), "Today's Games\n\nNo games scheduled for today.");
    await snapshots();
    await expectSchedule(expected);
  }
  await fixture.control("scenario mixed");
  await fixture.control("invalid-evidence group-low-zero");
  await page.waitForFunction(() => document.querySelector("#excitement-group-low-zero")?.textContent.includes("unavailable"));
  await expectSchedule(mixed);
  assert.match(await page.locator("#excitement-group-low-zero").innerText(), /Hype\s+unavailable\s+—/);
  assert.doesNotMatch(await page.locator("a[href='/game/group-low-zero']").innerText(), /NaN|Infinity/);
  console.log("PASS: each empty band/first pregame, all-unavailable, singleton, live-only, other-only, empty and rejected nonfinite evidence through production pages");
  await fixture.control("scenario mixed");
  await expectSchedule(mixed);
  for (const device of [
    { name: "desktop", width: 1440, height: 1000, columns: 3 },
    { name: "tablet", width: 814, height: 882, columns: 2 },
    { name: "mobile", width: 390, height: 844, columns: 1 },
    { name: "narrow", width: 320, height: 844, columns: 1 },
    { name: "effective-zoom-200", width: 720, height: 500, columns: 1 },
  ]) {
    await page.setViewportSize({ width: device.width, height: device.height });
    await snapshots();
    await expectSchedule(mixed);
    const geometry = await page.locator("#main-content").evaluate((main) => {
      const box = (el) => { const r = el.getBoundingClientRect(); return { x: r.x, y: r.y + scrollY, width: r.width, height: r.height }; };
      return {
        viewport: innerWidth, scrollWidth: document.documentElement.scrollWidth,
        sections: [...main.querySelectorAll("section")].map((section) => ({
          heading: section.querySelector("h2").textContent, headingBox: box(section.querySelector("h2")),
          cards: [...section.querySelectorAll("a[href^='/game/']")].map((card) => ({
            id: card.getAttribute("href").slice(6), box: box(card),
            metadata: [...card.querySelectorAll("time, [id^='scores-'] span, .home-card-league")].filter((el) => el.textContent.trim()).map((el) => {
              const range = document.createRange();
              range.selectNodeContents(el);
              return { text: el.textContent, font: Number.parseFloat(getComputedStyle(el).fontSize), box: box(el), textBoxes: [...range.getClientRects()].map((r) => ({ x: r.x, width: r.width })) };
            }),
          })),
        })),
      };
    });
    assert.ok(geometry.scrollWidth <= device.width, `${device.name}: page has no horizontal overflow`);
    for (const [index, section] of geometry.sections.entries()) {
      assert.deepEqual(section.cards.map((card) => card.id), mixed[index].ids);
      assert.ok(section.headingBox.width >= section.cards[0].box.width);
      assert.ok(section.headingBox.y + section.headingBox.height <= section.cards[0].box.y);
      for (const [cardIndex, card] of section.cards.entries()) {
        assert.ok(card.box.x >= 0 && card.box.x + card.box.width <= device.width + 1);
        for (const metadata of card.metadata) {
          assert.ok(metadata.font >= 12, `${device.name} ${card.id}: readable metadata font`);
          assert.ok(metadata.box.x >= card.box.x && metadata.box.x + metadata.box.width <= card.box.x + card.box.width + 1, `${device.name} ${card.id}: metadata fits its card`);
          for (const textBox of metadata.textBoxes) assert.ok(textBox.x >= card.box.x && textBox.x + textBox.width <= card.box.x + card.box.width + 1, `${device.name} ${card.id}: actual text does not spill outside card`);
        }
        const previous = section.cards[cardIndex - 1];
        if (previous) assert.ok(card.box.y > previous.box.y + 1 || (Math.abs(card.box.y - previous.box.y) < 1 && card.box.x > previous.box.x), `${device.name}: visual row-major order matches literal DOM schedule`);
      }
      if (section.cards.length > 1) assert.equal(Math.abs(section.cards[1].box.y - section.cards[0].box.y) < 1, device.columns > 1, `${device.name}: responsive column density`);
    }
    await nhl.focus();
    await snapshots();
    await page.keyboard.press("Tab");
    assert.equal(await credit.evaluate((el) => document.activeElement === el), true, `${device.name}: keyboard card then separate source credit with snapshots active`);
    assert.ok(await credit.evaluate((el) => Number.parseFloat(getComputedStyle(el).outlineWidth) >= 2), `${device.name}: visible keyboard focus indicator`);
    report.push({ device, geometry });
    if (evidence) {
      await page.evaluate(() => scrollTo(0, 0));
      await page.screenshot({ path: `${evidence}/${device.name}.png`, fullPage: true });
      if (device.name === "mobile") await page.getByRole("region", { name: "More games", exact: true }).screenshot({ path: `${evidence}/mobile-more.png` });
    }
  }
  await page.setViewportSize({ width: 1440, height: 1000 });
  await fixture.control("scenario single");
  await expectSchedule([{ heading: "Best games", ids: ["group-best-70"] }]);
  const singleton = await page.locator("a[href='/game/group-best-70']").evaluate((card) => {
    const r = card.getBoundingClientRect();
    const section = card.closest("section").getBoundingClientRect();
    const teams = [...card.querySelectorAll("[id^='scores-'] > div")].map((el) => { const r = el.getBoundingClientRect(); return { x: r.x, y: r.y }; });
    return { width: r.width, height: r.height, sectionWidth: section.width, teams };
  });
  assert.ok(Math.abs(singleton.width - singleton.sectionWidth) < 1, "desktop singleton fills the entire section width");
  assert.ok(singleton.height <= 180, "desktop singleton remains a compact matchup rather than a stretched vertical grid card");
  assert.ok(Math.abs(singleton.teams[0].y - singleton.teams[1].y) < 1 && singleton.teams[0].x < singleton.teams[1].x, "desktop singleton teams read horizontally in DOM order");
  report.push({ singleton });
  if (evidence) await page.screenshot({ path: `${evidence}/desktop-single.png`, fullPage: true });
  const before = await page.evaluate(() => window.fragmentCount);
  await fixture.control("status group-best-70 in_progress");
  await expectSchedule([{ heading: "Live now", ids: ["group-best-70"] }]);
  await fixture.control("publish group-best-70");
  await page.waitForFunction((count) => window.fragmentCount > count, before, { timeout: 5_000 });
  const nativeNavigationErrors = errors.filter(isNativeNavigationError);
  assert.deepEqual(errors.filter((error) => !nativeNavigationErrors.includes(error)), [], "production home/partial detail assets run without browser script errors");
  if (nativeNavigationErrors.length) console.log("NOTE: exact native Chromium cross-document transition rejection previously isolated without Datastar/snapshots/publications");
  report.push({ nativeNavigationErrors: nativeNavigationErrors.length, scriptErrors: errors.length - nativeNavigationErrors.length });
  console.log("PASS: batched desktop/tablet/mobile/narrow/effective-200%-zoom layout, headings/visual order/metadata/keyboard; compact full-width singleton; actual SSE client active");
  if (evidence) await writeFile(`${evidence}/browser-report.json`, JSON.stringify(report, null, 2));
} finally {
  try { await browser?.close(); }
  finally {
    await fixture.stop();
    await verifyStoppedFixture(fixture);
    await assert.rejects(access(fixture.manifest.groupedHome.controlPath), { code: "ENOENT" });
    console.log("PASS: owned acceptance fixture cleaned up; shared proxy untouched");
  }
}
