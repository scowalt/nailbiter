import { strict as assert } from "node:assert";
import { readFile } from "node:fs/promises";
import { chromium } from "playwright";

const components = await readFile(
  "apps/backend/wwwroot/js/nailbiter-components.js",
  "utf8",
);
const liveSync = await readFile("apps/backend/wwwroot/js/live-sync.js", "utf8");
const points = (value) =>
  JSON.stringify([
    { label: "start", homeWinProbability: 0.5 },
    { label: "now", homeWinProbability: value },
  ]);
const shell = (content, terminal = false) =>
  `<!doctype html><html><head><title>fixture</title><style>nb-win-probability-chart{display:block;height:256px}.hidden{display:none}</style></head><body><div id="live-reconnect-warning" class="hidden"></div><main id="main-content"><section data-live-terminal="${terminal}">${content}</section></main></body></html>`;
const gamePoints = (value, outcomes = 2) =>
  JSON.parse(points(value)).map((point) => ({
    ...point,
    ...(outcomes >= 2 && {
      awayWinProbability:
        1 - point.homeWinProbability - (outcomes === 3 ? 0.1 : 0),
    }),
    ...(outcomes === 3 && { drawWinProbability: 0.1 }),
  }));
const game = (value, final = false, history = gamePoints(value)) =>
  `<div id="score">${final ? "FINAL 9-7" : "LIVE 7-7"}</div><nb-win-probability-chart game-id="g1" data-live-final="${final}" data='${JSON.stringify(history)}'></nb-win-probability-chart>`;
const race = (final = false) =>
  `<div id="status">${final ? "Final" : "Lap 5"}</div><nb-win-probability-chart race-id="r1" sport="f1" data-live-final="${final}"></nb-win-probability-chart>`;
const spark = (id, final) =>
  `<nb-sparkline game-id="${id}" data-live-final="${final}"></nb-sparkline>`;

const waitUntil = async (predicate, message) => {
  for (let i = 0; i < 100; i++) {
    if (predicate()) return;
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
  throw new Error(`timed out: ${message}`);
};

async function fixture(browser, initial, snapshot, api, { timezoneId } = {}) {
  const page = await browser.newPage({ timezoneId });
  const counts = { snapshots: 0, odds: 0, race: 0, escaped: 0 };
  await page.route("**/*", async (route) => {
    const request = route.request();
    if (request.headers()["x-nailbiter-live-snapshot"]) {
      counts.snapshots++;
      await route.fulfill({
        contentType: "text/html",
        body: await snapshot(counts.snapshots),
      });
    } else if (request.url().includes("/api/games/")) {
      counts.odds++;
      await route.fulfill({
        contentType: "application/json",
        body: JSON.stringify({ oddsHistory: api("odds", counts.odds) }),
      });
    } else if (request.url().includes("/api/f1/race/")) {
      counts.race++;
      await route.fulfill({
        contentType: "application/json",
        body: JSON.stringify(api("race", counts.race)),
      });
    } else if (request.isNavigationRequest()) {
      await route.fulfill({ contentType: "text/html", body: "" });
    } else {
      counts.escaped++;
      await route.abort();
    }
  });
  await page.addInitScript(() => {
    window.__events = [];
    window.EventSource = class {
      constructor() {
        this.listeners = {};
        this.closed = false;
        window.__events.push(this);
      }
      addEventListener(name, fn) {
        if (!this.listeners[name]) this.listeners[name] = [];
        this.listeners[name].push(fn);
      }
      emit(name) {
        for (const fn of this.listeners[name] ?? []) fn();
      }
      close() {
        this.closed = true;
      }
    };
    window.IntersectionObserver = class {
      constructor(callback) {
        this.callback = callback;
      }
      observe() {
        queueMicrotask(() => this.callback([{ isIntersecting: true }]));
      }
      disconnect() {}
    };
  });
  await page.goto("http://fixture.test/");
  await page.setContent(initial);
  await page.addScriptTag({ content: components, type: "module" });
  await page.waitForFunction(() =>
    customElements.get("nb-win-probability-chart"),
  );
  await page.addScriptTag({ content: liveSync, type: "module" });
  return { page, counts };
}

const browser = await chromium.launch({ headless: true });
try {
  const active = await fixture(
    browser,
    shell(game(0.6)),
    async (n) => shell(game(0.6 + n / 100)),
    (_kind, n) => gamePoints(0.7 + n / 100),
  );
  await active.page.waitForFunction(
    () =>
      document.querySelector("nb-win-probability-chart")?.chart &&
      window.__events.length,
  );
  const before = await active.page.evaluate(() => {
    const el = document.querySelector("nb-win-probability-chart");
    window.__canvas = el.querySelector("canvas");
    window.__chart = el.chart;
    return { height: document.documentElement.scrollHeight };
  });
  for (let i = 1; i <= 3; i++) {
    await active.page.evaluate(() => window.__events[0].emit("sync"));
    await waitUntil(
      () => active.counts.snapshots === i,
      `active snapshot ${i}`,
    );
  }
  await active.page.waitForFunction(
    () =>
      document
        .querySelector("nb-win-probability-chart")
        .chart.data.datasets[0].data.at(-1) > 60,
  );
  const stable = await active.page.evaluate(() => {
    const el = document.querySelector("nb-win-probability-chart");
    return {
      canvases: el.querySelectorAll("canvas").length,
      height: document.documentElement.scrollHeight,
      sameCanvas: el.querySelector("canvas") === window.__canvas,
      sameChart: el.chart === window.__chart,
    };
  });
  assert.deepEqual(stable, {
    canvases: 1,
    height: before.height,
    sameCanvas: true,
    sameChart: true,
  });

  const embeddedCases = [];
  for (const outcomes of [1, 2, 3]) {
    const snapshot = (revision) => {
      const history = [
        { label: "pregame", homeWinProbability: 0.4 },
        ...gamePoints(0.6 + revision / 100, outcomes),
      ];
      return shell(
        `<div id="revision">${revision}</div>${game(0.6, revision === 3, history)}`,
        revision === 3,
      );
    };
    const embedded = await fixture(
      browser,
      snapshot(0),
      async (n) => snapshot(n),
      () => gamePoints(0.9),
    );
    await embedded.page.waitForFunction(
      () =>
        document.querySelector("nb-win-probability-chart")?.chart &&
        window.__events.length,
    );
    await embedded.page.evaluate(() => {
      const chart = document.querySelector("nb-win-probability-chart").chart;
      window.__lineCounts = [chart.data.datasets.length];
      chart.constructor.register({
        id: "line-count-regression",
        afterUpdate: (updated) =>
          window.__lineCounts.push(updated.data.datasets.length),
      });
    });
    for (let revision = 1; revision <= 3; revision++) {
      await embedded.page.evaluate(() => window.__events[0].emit("sync"));
      await embedded.page.waitForFunction(
        (n) => document.getElementById("revision").textContent === String(n),
        revision,
      );
    }
    await embedded.page.waitForFunction(() => window.__events[0].closed);
    const result = await embedded.page.evaluate(() => {
      const chart = document.querySelector("nb-win-probability-chart").chart;
      return {
        lineCounts: [...new Set(window.__lineCounts)],
        labels: chart.data.labels,
        home: chart.data.datasets[0].data,
      };
    });
    assert.deepEqual(result, {
      lineCounts: [outcomes],
      labels: ["pregame", "start", "now"],
      home: [40, 50, 63],
    });
    assert.equal(embedded.counts.odds, 0);
    embeddedCases.push({ outcomes, ...embedded.counts });
  }

  for (const source of [
    "Kalshi (near-certain orderbook bound)",
    "Kalshi (resolved game-winner bound)",
  ]) {
    for (const outcomes of [2, 3]) {
      const history = gamePoints(0.995, 2);
      history[1] = {
        ...history[1],
        homeWinProbability: 0.995,
        awayWinProbability: outcomes === 3 ? 0.0025 : 0.005,
        ...(outcomes === 3 && { drawWinProbability: 0.0025 }),
        source,
        isProbabilityBound: true,
      };
      const bounds = await fixture(
        browser,
        shell(game(0.995, true, history), true),
        async () => shell(game(0.995, true, history), true),
        () => history,
      );
      await bounds.page.waitForFunction(
        () => document.querySelector("nb-win-probability-chart")?.chart,
      );
      for (let refresh = 0; refresh < 2; refresh++) {
        if (refresh) {
          await bounds.page.evaluate(() =>
            document.querySelector("nb-win-probability-chart").refresh(),
          );
        }
        const labels = await bounds.page.evaluate(() => {
          const component = document.querySelector("nb-win-probability-chart");
          const chart = component.chart;
          const label = chart.options.plugins.tooltip.callbacks.label;
          return chart.data.datasets.map((dataset) =>
            label({ dataset, dataIndex: 1, parsed: { y: dataset.data[1] } }),
          );
        });
        assert.deepEqual(
          labels,
          outcomes === 2
            ? ["Home: >99%", "Away: <1%"]
            : ["Home: >99%", "Draw: <1%", "Away: <1%"],
        );
        assert.ok(labels.every((label) => !label.includes(source)));
      }
      await bounds.page.close();
    }
  }

  const initialFinal = await fixture(
    browser,
    shell(
      game(0.9, true) +
        '<time id="final-time" class="nb-localtime" datetime="2026-09-22T00:00:00.0000000Z">Sep 22, 12:00 AM</time>',
      true,
    ),
    async () => {
      throw new Error("unexpected snapshot");
    },
    () => [],
    { timezoneId: "America/Los_Angeles" },
  );
  await initialFinal.page.waitForFunction(
    () => document.querySelector("nb-win-probability-chart")?.chart,
  );
  assert.equal(
    await initialFinal.page.locator("#final-time").textContent(),
    "Sep 21, 5:00 PM",
  );
  assert.deepEqual(
    {
      events: await initialFinal.page.evaluate(() => window.__events.length),
      ...initialFinal.counts,
    },
    { events: 0, snapshots: 0, odds: 0, race: 0, escaped: 0 },
  );

  const activeRace = await fixture(
    browser,
    shell(race()),
    async () => shell(race()),
    (_kind, n) => JSON.parse(points(0.5 + n / 10)),
  );
  await waitUntil(() => activeRace.counts.race === 1, "initial race chart");
  await activeRace.page.waitForFunction(() => window.__events.length);
  for (let i = 1; i <= 2; i++) {
    await activeRace.page.evaluate(() => window.__events[0].emit("sync"));
    await waitUntil(
      () =>
        activeRace.counts.snapshots === i && activeRace.counts.race === i + 1,
      `race sync ${i}`,
    );
  }
  assert.equal(activeRace.counts.race, 3);

  let releaseFinal;
  const finalGate = new Promise((resolve) => {
    releaseFinal = resolve;
  });
  const transition = await fixture(
    browser,
    shell(game(0.6)),
    async () => {
      await finalGate;
      return shell(game(0.95, true), true);
    },
    () => [],
  );
  await transition.page.waitForFunction(() => window.__events.length);
  await transition.page.evaluate(() => window.__events[0].emit("sync"));
  await waitUntil(
    () => transition.counts.snapshots === 1,
    "in-flight final snapshot",
  );
  await transition.page.evaluate(() => window.__events[0].emit("sync"));
  releaseFinal();
  await transition.page.waitForFunction(
    () =>
      window.__events[0].closed &&
      document.querySelector("#score").textContent.includes("FINAL"),
  );
  await transition.page.evaluate(() => {
    window.__events[0].emit("sync");
    window.__events[0].emit("error");
  });
  assert.equal(transition.counts.snapshots, 1);
  assert.equal(
    await transition.page
      .locator("#live-reconnect-warning")
      .evaluate((el) => el.classList.contains("hidden")),
    true,
  );
  assert.equal(
    await transition.page.evaluate(() =>
      document
        .querySelector("nb-win-probability-chart")
        .chart.data.datasets[0].data.at(-1),
    ),
    95,
  );

  const finalRace = await fixture(
    browser,
    shell(race()),
    async () => shell(race(true), true),
    (_kind, n) => JSON.parse(points(0.5 + n / 10)),
  );
  await waitUntil(
    () => finalRace.counts.race === 1,
    "initial final-race fixture load",
  );
  await finalRace.page.waitForFunction(() => window.__events.length);
  await finalRace.page.evaluate(() => window.__events[0].emit("sync"));
  await finalRace.page.waitForFunction(() => window.__events[0].closed);
  assert.deepEqual(
    { snapshots: finalRace.counts.snapshots, race: finalRace.counts.race },
    { snapshots: 1, race: 2 },
  );

  const listing = await fixture(
    browser,
    shell(spark("done", true) + spark("live", false)),
    async (n) =>
      shell(
        spark("done", true) +
          spark("live", false) +
          (n > 1 ? spark("new", false) : ""),
      ),
    (_kind, n) => JSON.parse(points(0.4 + n / 100)),
  );
  await waitUntil(
    () => listing.counts.odds === 2,
    "initial listing sparklines",
  );
  await listing.page.waitForFunction(() => window.__events.length);
  for (let i = 1; i <= 2; i++) {
    await listing.page.evaluate(() => window.__events[0].emit("sync"));
    await waitUntil(
      () => listing.counts.snapshots === i && listing.counts.odds >= 2 + i,
      `listing sync ${i}`,
    );
  }
  await listing.page.waitForFunction(() =>
    document.querySelector('[game-id="new"]'),
  );
  assert.equal(listing.counts.odds, 5);
  assert.equal(
    await listing.page.evaluate(() => window.__events[0].closed),
    false,
  );

  const allFinalListing = await fixture(
    browser,
    shell(spark("done-1", true) + spark("done-2", true)),
    async () => shell(spark("done-1", true) + spark("done-2", true)),
    (_kind, n) => JSON.parse(points(0.8 + n / 100)),
  );
  await waitUntil(
    () => allFinalListing.counts.odds === 2,
    "initial all-final series",
  );
  await allFinalListing.page.waitForFunction(() => window.__events.length);
  await allFinalListing.page.evaluate(() => window.__events[0].emit("sync"));
  await waitUntil(
    () => allFinalListing.counts.snapshots === 1,
    "all-final discovery snapshot",
  );
  assert.equal(allFinalListing.counts.odds, 2);
  assert.equal(
    await allFinalListing.page.evaluate(() => window.__events[0].closed),
    false,
  );

  const timeCases = [
    {
      timezoneId: "America/Los_Angeles",
      initial: "Sep 21, 5:00 PM",
      changed: "Nov 1, 2:30 AM",
      inserted: "Mar 8, 3:30 AM",
    },
    {
      timezoneId: "Asia/Kolkata",
      initial: "Sep 22, 5:30 AM",
      changed: "Nov 1, 4:00 PM",
      inserted: "Mar 8, 4:00 PM",
    },
    {
      timezoneId: "UTC",
      initial: "Sep 22, 12:00 AM",
      changed: "Nov 1, 10:30 AM",
      inserted: "Mar 8, 10:30 AM",
    },
  ];
  const timeResults = [];
  for (const expected of timeCases) {
    const initialInstant = "2026-09-22T00:00:00.0000000Z";
    const changedInstant = "2026-11-01T10:30:00.0000000Z";
    const insertedInstant = "2026-03-08T10:30:00.0000000Z";
    const time = (id, instant, fallback = "server UTC fallback") =>
      `<time id="${id}" class="nb-localtime" datetime="${instant}">${fallback}</time>`;
    const times = await fixture(
      browser,
      shell(
        time("existing-time", initialInstant) +
          time("invalid-time", "not-a-date", "keep me") +
          '<time id="missing-time" class="nb-localtime">keep missing</time>',
      ),
      async () =>
        shell(
          time("existing-time", changedInstant) +
            time("new-time", insertedInstant) +
            time("invalid-time", "not-a-date", "keep me") +
            '<time id="missing-time" class="nb-localtime">keep missing</time>',
        ),
      () => [],
      { timezoneId: expected.timezoneId },
    );
    assert.equal(
      await times.page.locator("#existing-time").textContent(),
      expected.initial,
    );
    await times.page.waitForFunction(() => window.__events.length);
    for (let i = 1; i <= 3; i++) {
      await times.page.evaluate(() => window.__events[0].emit("sync"));
      await waitUntil(() => times.counts.snapshots === i, `time sync ${i}`);
      await times.page.waitForFunction(
        (value) => document.querySelector("#new-time")?.textContent === value,
        expected.inserted,
      );
    }
    assert.deepEqual(
      await times.page.evaluate(() => ({
        existing: document.querySelector("#existing-time").textContent,
        existingDatetime: document.querySelector("#existing-time").dateTime,
        inserted: document.querySelector("#new-time").textContent,
        insertedDatetime: document.querySelector("#new-time").dateTime,
        invalid: document.querySelector("#invalid-time").textContent,
        missing: document.querySelector("#missing-time").textContent,
      })),
      {
        existing: expected.changed,
        existingDatetime: changedInstant,
        inserted: expected.inserted,
        insertedDatetime: insertedInstant,
        invalid: "keep me",
        missing: "keep missing",
      },
    );
    timeResults.push({ timezoneId: expected.timezoneId, ...times.counts });
  }

  const cleanup = await active.page.evaluate(async () => {
    const element = document.querySelector("nb-win-probability-chart");
    const old = element.chart;
    element.remove();
    await new Promise(queueMicrotask);
    const destroyed = !element.chart && old.ctx === null;
    document.getElementById("main-content").append(element);
    await new Promise((resolve) => setTimeout(resolve, 0));
    return {
      destroyed,
      canvases: element.querySelectorAll("canvas").length,
      recreated: Boolean(element.chart),
    };
  });
  assert.deepEqual(cleanup, { destroyed: true, canvases: 1, recreated: true });

  for (const result of [
    active,
    initialFinal,
    activeRace,
    transition,
    finalRace,
    listing,
    allFinalListing,
  ])
    assert.equal(result.counts.escaped, 0);
  for (const result of [...timeResults, ...embeddedCases])
    assert.equal(result.escaped, 0);
  console.log("PASS live sync browser regression", {
    active: active.counts,
    embeddedCases,
    initialFinal: initialFinal.counts,
    activeRace: activeRace.counts,
    transition: transition.counts,
    finalRace: finalRace.counts,
    listing: listing.counts,
    allFinalListing: allFinalListing.counts,
    timeResults,
    stable,
    cleanup,
  });
} finally {
  await browser.close();
}
