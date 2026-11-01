import assert from "node:assert/strict";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { isAbsolute, relative, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright";
import { startFixture } from "./lifecycle.js";
import { verifyStoppedFixture } from "./stopped-verification.js";

const sentinel = "fixture-sentinel-no-live-key-653";
const artifacts = process.env.HIGHLIGHTS_ARTIFACT_DIR;
if (artifacts) {
  const path = relative(
    fileURLToPath(new URL("../../", import.meta.url)),
    artifacts,
  );
  if (!isAbsolute(artifacts) || (path !== ".." && !path.startsWith(`..${sep}`)))
    throw new Error(
      "Optional browser artifacts must use an absolute path outside the checkout.",
    );
}
const fixture = await startFixture({ highlights: true });
let browser;
const report = {
  claims: [],
  endpoints: {
    local: fixture.manifest.localUrl,
    private: fixture.manifest.privateUrl,
  },
};
const scans = [];
const sensitive = [];
const contexts = [];
const forbiddenHost = (host) =>
  /(^|\.)(youtube(?:-nocookie)?\.com|youtu\.be|ytimg\.com|google\.com|googleapis\.com|googlevideo\.com|gstatic\.com|ggpht\.com)$/.test(
    host,
  );
const origin = new URL(fixture.manifest.privateUrl).origin;

async function context(options = {}) {
  const ctx = await browser.newContext(options);
  const requests = [];
  contexts.push(requests);
  ctx.on("request", (request) => {
    const url = request.url();
    if (url.includes(sentinel)) sensitive.push("browser URL");
    requests.push({
      url,
      method: request.method(),
      type: request.resourceType(),
      referrer: request.headers().referer ?? null,
      snapshot: Boolean(request.headers()["x-nailbiter-live-snapshot"]),
    });
  });
  ctx.on("response", (response) => {
    if (new URL(response.url()).origin !== origin) return;
    if (!/text|javascript|json/.test(response.headers()["content-type"] ?? ""))
      return;
    if (response.request().resourceType() === "eventsource") return;
    scans.push(
      response.text().then((body) => {
        if (body.includes(sentinel)) sensitive.push("HTML/bundle/response");
      }),
    );
  });
  await ctx.route("**/*", async (route) => {
    const url = new URL(route.request().url());
    if (url.origin === origin) {
      if (url.pathname.startsWith("/sse/")) await route.abort();
      else await route.continue();
    } else if (
      url.hostname === "www.youtube-nocookie.com" &&
      url.pathname.startsWith("/embed/")
    ) {
      await route.fulfill({
        contentType: "text/html",
        body: "<!doctype html><title>Local simulated embed failure</title><p>Fixture only: playback unavailable</p>",
      });
    } else if (
      url.hostname === "www.youtube.com" &&
      url.pathname === "/watch"
    ) {
      await route.fulfill({
        contentType: "text/html",
        body: "<!doctype html><title>Intercepted direct fallback</title><p>No provider connection</p>",
      });
    } else await route.abort();
  });
  if (options.javaScriptEnabled !== false)
    await ctx.addInitScript(() => {
      window.__eventSources = [];
      const NativeEventSource = window.EventSource;
      window.EventSource = class extends NativeEventSource {
        constructor(...args) {
          super(...args);
          window.__eventSources.push(args[0]);
        }
      };
    });
  return { ctx, requests, page: await ctx.newPage() };
}

async function visit(page, id) {
  const response = await page.goto(`${origin}/game/${id}`, {
    waitUntil: "networkidle",
  });
  assert.equal(response.status(), 200, id);
  assert.ok(!(await page.content()).includes(sentinel));
}

function noProvider(requests) {
  assert.deepEqual(
    requests.filter((r) => forbiddenHost(new URL(r.url).hostname)),
    [],
  );
}

function noSync(requests) {
  assert.deepEqual(
    requests.filter(
      (r) =>
        r.type === "eventsource" ||
        r.snapshot ||
        /\/sse\/|highlights/.test(new URL(r.url).pathname),
    ),
    [],
  );
}

async function unavailable(page, id, copy) {
  await visit(page, id);
  assert.equal(await page.locator("#highlights-status").textContent(), copy);
  assert.equal(
    await page
      .locator(
        "#game-highlights iframe, #game-highlights button, #highlights-watch",
      )
      .count(),
    0,
  );
}

try {
  browser = await chromium.launch({ headless: true });
  const before = await fixture.control("inspect");
  const pending = await context();
  await visit(pending.page, "mlb-pending");
  assert.equal(
    await pending.page.locator("#highlights-status").textContent(),
    "Highlights not available yet",
  );
  assert.equal(
    await pending.page.locator("[data-live-terminal=true]").count(),
    1,
  );
  await pending.page.clock.install();

  const selected = await context({ viewport: { width: 390, height: 844 } });
  await visit(selected.page, "mlb-chart");
  await selected.page.waitForFunction(
    () => document.querySelector("nb-win-probability-chart")?.chart,
  );
  const score = await selected.page.locator("#detail-scoreboard").textContent();
  assert.match(score, /Atlanta Braves/);
  assert.match(score, /Los Angeles Dodgers/);
  assert.equal(
    await selected.page.locator("#detail-home-score").textContent(),
    "5",
  );
  assert.equal(
    await selected.page.locator("#detail-away-score").textContent(),
    "2",
  );
  assert.equal(
    await selected.page
      .locator("#game-highlights")
      .evaluate((el) =>
        Boolean(
          el.previousElementSibling.querySelector("nb-win-probability-chart"),
        ),
      ),
    true,
  );
  assert.deepEqual(
    await selected.page
      .locator("nb-win-probability-chart")
      .evaluate((el) => el.chart.data.datasets.map((d) => d.data)),
    [
      [40, 60, 100],
      [60, 40, 0],
    ],
  );
  assert.match(
    await selected.page.locator("#game-highlights").textContent(),
    /Published by MLB · Video served by YouTube/,
  );
  assert.match(
    await selected.page.locator("#highlights-consent").textContent(),
    /contacts YouTube.*may collect data/,
  );
  assert.equal(
    await selected.page.locator("#highlights-terms").getAttribute("href"),
    "https://www.youtube.com/t/terms",
  );
  assert.equal(
    await selected.page.locator("#highlights-privacy").getAttribute("href"),
    "https://policies.google.com/privacy",
  );
  assert.match(
    await selected.page.locator("#highlights-regions").textContent(),
    /restricted.*CA/,
  );
  assert.match(
    await selected.page.locator("#highlights-fallback").textContent(),
    /does not bypass restrictions/,
  );
  assert.equal(
    await selected.page
      .locator("iframe, #game-highlights img, link[rel=preconnect]")
      .count(),
    0,
  );
  noProvider(selected.requests);
  assert.equal(
    (await fixture.control("inspect")).upstreamCalls,
    before.upstreamCalls,
  );
  if (artifacts) {
    await mkdir(artifacts, { recursive: true });
    await selected.page
      .locator("#game-highlights")
      .screenshot({ path: `${artifacts}/pre-load-synthetic.png` });
  }
  report.claims.push(
    "Persisted real-route selection, chart/score preserved, correct chart placement, pre-load disclosures and zero provider/thumbnail traffic",
  );

  const load = selected.page.getByRole("button", {
    name: "Load highlights from YouTube",
    exact: true,
  });
  assert.equal(
    await load.getAttribute("aria-describedby"),
    "highlights-consent",
  );
  await load.focus();
  assert.equal(
    await load.evaluate((el) => document.activeElement === el),
    true,
  );
  await selected.page.keyboard.press("Enter");
  const player = selected.page.locator("#highlights-player");
  await player.waitFor();
  assert.equal(
    await player.getAttribute("src"),
    "https://www.youtube-nocookie.com/embed/fixture0001?autoplay=0&controls=1",
  );
  assert.equal(
    await player.getAttribute("title"),
    "Game highlights: Dodgers vs. Braves Highlights (10/1/26) | MLB Highlights",
  );
  assert.equal(
    await player.getAttribute("referrerpolicy"),
    "strict-origin-when-cross-origin",
  );
  assert.equal(
    await player.getAttribute("allow"),
    "encrypted-media; picture-in-picture; fullscreen",
  );
  assert.notEqual(await player.getAttribute("allowfullscreen"), null);
  for (const width of [320, 390]) {
    await selected.page.setViewportSize({ width, height: 844 });
    const box = await player.boundingBox();
    assert.ok(box.width >= 200 && box.height >= 200, JSON.stringify(box));
    assert.ok(box.width <= width);
  }
  assert.ok(
    selected.requests.some((r) =>
      r.url.includes("youtube-nocookie.com/embed/fixture0001"),
    ),
  );
  assert.equal(
    selected.requests.find((r) =>
      r.url.includes("youtube-nocookie.com/embed/fixture0001"),
    ).referrer,
    `${origin}/`,
  );
  noSync(selected.requests);
  assert.equal(
    await selected.page.evaluate(() => window.__eventSources.length),
    0,
  );
  await player.evaluate((el) => el.dispatchEvent(new Event("error")));
  assert.equal(await selected.page.locator("#highlights-player").count(), 0);
  assert.match(
    await selected.page
      .locator("#highlights-player-error[role=status]")
      .textContent(),
    /Watch on YouTube/,
  );
  assert.equal(
    await selected.page.locator("#highlights-watch").getAttribute("href"),
    "https://www.youtube.com/watch?v=fixture0001",
  );
  assert.deepEqual((await fixture.control("inspect")).states, before.states);
  await selected.page.reload({ waitUntil: "networkidle" });
  assert.equal(
    await selected.page
      .locator("nb-game-highlights")
      .getAttribute("data-video-id"),
    "fixture0001",
  );
  assert.equal(
    await selected.page.locator("#highlights-player-error").count(),
    0,
  );
  assert.equal(
    await selected.page.locator("#detail-scoreboard").textContent(),
    score,
  );
  report.claims.push(
    "Keyboard load: nocookie/autoplay=0/native controls/title/referrer/permissions, >=200x200 at 320/390px; simulated local iframe error retains fallback and global selection",
  );

  const noChart = await context({ viewport: { width: 320, height: 700 } });
  await visit(noChart.page, "mlb-no-chart");
  assert.equal(
    await noChart.page.locator("nb-win-probability-chart").count(),
    0,
  );
  assert.equal(
    await noChart.page
      .locator("#game-highlights")
      .evaluate((el) => el.previousElementSibling.id),
    "detail-scoreboard",
  );
  noProvider(noChart.requests);
  await noChart.page
    .getByRole("button", { name: "Load highlights from YouTube", exact: true })
    .click();
  assert.equal(
    await noChart.page.locator("#highlights-player").getAttribute("src"),
    "https://www.youtube-nocookie.com/embed/fixture0002?autoplay=0&controls=1",
  );

  const noJs = await context({ javaScriptEnabled: false });
  await visit(noJs.page, "mlb-no-chart");
  assert.equal(await noJs.page.locator("iframe").count(), 0);
  assert.match(
    await noJs.page.locator("noscript").textContent(),
    /You can still Watch on YouTube/,
  );
  noProvider(noJs.requests);
  await noJs.page
    .getByRole("link", { name: "Watch on YouTube", exact: true })
    .click();
  assert.equal(noJs.page.url(), "https://www.youtube.com/watch?v=fixture0002");
  assert.equal(await noJs.page.title(), "Intercepted direct fallback");
  report.claims.push(
    "No-chart scoreboard placement and pointer load; actual no-JS direct fallback navigation intercepted locally",
  );

  const states = await context();
  for (const id of ["mlb-closed", "mlb-suppressed", "mlb-unusable"])
    await unavailable(states.page, id, "Highlights unavailable");
  for (const command of ["worker-off", "adapter-off", "missing-key"]) {
    await fixture.control(command);
    await unavailable(states.page, "mlb-pending", "Highlights unavailable");
    await fixture.control("enabled");
  }
  for (const id of fixture.manifest.highlights.hidden) {
    await visit(states.page, id);
    assert.equal(await states.page.locator("#game-highlights").count(), 0, id);
  }
  await visit(states.page, fixture.manifest.intended.id);
  assert.equal(await states.page.locator("#game-highlights").count(), 0);
  assert.match(
    await states.page.locator("#detail-scoreboard").textContent(),
    /Pilot Harbor Otters.*107.*Pilot Summit Owls.*113/,
  );
  for (const path of [
    "/mlb?date=2026-10-01",
    `/nba?date=${fixture.manifest.listingDateEastern}`,
    "/",
  ]) {
    assert.equal(
      (
        await states.page.goto(`${origin}${path}`, { waitUntil: "networkidle" })
      ).status(),
      200,
    );
    assert.equal(
      await states.page.locator("#game-highlights, nb-game-highlights").count(),
      0,
    );
  }
  noProvider(states.requests);
  report.claims.push(
    "Exact active/closed/suppressed/unusable/worker-disabled/adapter-disabled/missing-key copy; hidden scheduled/live/canceled/postponed/suspended/post-false/missing evidence/NBA; unchanged lists",
  );

  const beforeDiscoveryRequests = pending.requests.length;
  const discovery = await fixture.control("discover");
  assert.equal(
    discovery.states.find((s) => s.id === "mlb-pending").videoId,
    "fixture0003",
  );
  await pending.page.clock.fastForward("00:20:00");
  assert.equal(
    await pending.page.locator("#highlights-status").textContent(),
    "Highlights not available yet",
  );
  assert.equal(await pending.page.locator("#highlights-title").count(), 0);
  assert.equal(
    await pending.page.evaluate(() => window.__eventSources.length),
    0,
  );
  assert.equal(pending.requests.length, beforeDiscoveryRequests);
  noSync(pending.requests);
  noProvider(pending.requests);
  assert.equal(
    (await fixture.control("inspect")).upstreamCalls,
    discovery.upstreamCalls,
  );
  await pending.page.reload({ waitUntil: "networkidle" });
  assert.equal(
    await pending.page
      .locator("nb-game-highlights")
      .getAttribute("data-video-id"),
    "fixture0003",
  );
  assert.equal(await pending.page.locator("#highlights-status").count(), 0);
  assert.equal(
    await pending.page.evaluate(() => window.__eventSources.length),
    0,
  );
  noSync(pending.requests);
  assert.equal(
    (await fixture.control("inspect")).upstreamCalls,
    discovery.upstreamCalls,
  );
  report.claims.push(
    "Actual lifecycle selection persisted while page open; 20 browser minutes produce no SSE/snapshot/highlights polling or reopened sync; only genuine reload shows discovery; render makes no upstream calls",
  );

  await Promise.all(scans);
  for (const path of [
    "apps/backend/wwwroot/js/nailbiter-components.js",
    "apps/backend/wwwroot/js/live-sync.js",
    "apps/backend/wwwroot/css/global.css",
  ])
    assert.ok(!(await readFile(path, "utf8")).includes(sentinel));
  assert.ok(!JSON.stringify(fixture.manifest).includes(sentinel));
  assert.ok(!fixture.logs().includes(sentinel));
  assert.deepEqual(sensitive, []);
  report.claims.push(
    "Fake sentinel absent from HTML, loaded/local bundles, browser URLs, manifest and captured host/Portless logs; no live provider, playback or paid evaluation claim",
  );
  report.requestCounts = contexts.map((requests) => ({
    total: requests.length,
    interceptedProvider: requests.filter((r) =>
      forbiddenHost(new URL(r.url).hostname),
    ).length,
  }));
  console.log("PASS real-backend MLB browser acceptance", report);
} finally {
  await browser?.close();
  await fixture.stop();
  assert.ok(
    !fixture.logs().includes(sentinel),
    "Full host/Portless log including shutdown must not contain the fake credential.",
  );
  report.cleanup = await verifyStoppedFixture(fixture);
  console.log(report.cleanup);
  if (artifacts) {
    await mkdir(artifacts, { recursive: true });
    await writeFile(
      `${artifacts}/report.json`,
      `${JSON.stringify(report, null, 2)}\n`,
    );
  }
}
