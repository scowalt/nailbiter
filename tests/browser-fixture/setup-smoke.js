import assert from "node:assert/strict";
import { chromium } from "playwright";
import { startFixture } from "./lifecycle.js";
import { verifyStoppedFixture } from "./stopped-verification.js";

const fixture = await startFixture();
let browser;
try {
	browser = await chromium.launch({ headless: true });
	const page = await browser.newPage();
	const origin = new URL(fixture.manifest.privateUrl).origin;
	await page.route("**/*", async (route) => {
		if (new URL(route.request().url()).origin === origin) await route.continue();
		else await route.abort();
	});
	await page.goto(fixture.manifest.privateUrl);
	await page.getByRole("navigation").first().getByRole("link", { name: /NBA/ }).click();
	assert.equal(new URL(page.url()).pathname, "/nba");
	const intended = page.getByRole("link")
		.filter({ has: page.getByText("PHO", { exact: true }) })
		.filter({ has: page.getByText("PSO", { exact: true }) });
	const distraction = page.getByRole("link")
		.filter({ has: page.getByText("PCF", { exact: true }) })
		.filter({ has: page.getByText("PMC", { exact: true }) });
	assert.equal(await intended.count(), 1);
	assert.equal(await distraction.count(), 1);
	assert.equal(await intended.getAttribute("href"), "/game/pilot-nba-completed");
	assert.equal(await distraction.getAttribute("href"), "/game/pilot-nba-distraction");
	assert.ok(await intended.isVisible());
	assert.ok(await distraction.isVisible());
	console.log("PASS: real browser Home → current Eastern NBA listing (setup only)");
	await intended.click();
	assert.equal(new URL(page.url()).pathname, "/game/pilot-nba-completed");
	const scoreboard = page.locator("#detail-scoreboard");
	assert.ok(await scoreboard.getByText("Pilot Harbor Otters", { exact: true }).isVisible());
	assert.ok(await scoreboard.getByText("Pilot Summit Owls", { exact: true }).isVisible());
	assert.equal(await scoreboard.locator("#detail-away-score").innerText(), "107");
	assert.equal(await scoreboard.locator("#detail-home-score").innerText(), "113");
	assert.equal(await page.locator("#detail-status").innerText(), "Final");
	assert.ok(await page.locator("#detail-status").isVisible());
	console.log("PASS: real completed-game details, exact teams/away-home scores/Final (setup only)");
	console.log(JSON.stringify(fixture.manifest, null, 2));
	await assert.rejects(verifyStoppedFixture(fixture), { code: "ERR_ASSERTION" });
	console.log("PASS: stopped verification rejects the still-running real fixture");
} finally {
	await browser?.close();
	await fixture.stop();
	await verifyStoppedFixture(fixture);
	await fixture.stop();
	await verifyStoppedFixture(fixture);
	console.log("PASS: owned server route, PostgreSQL and manifest removed; shared proxy untouched");
}
