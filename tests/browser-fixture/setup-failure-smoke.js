import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtemp, mkdir, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { setTimeout as delay } from "node:timers/promises";

const root = fileURLToPath(new URL("../../", import.meta.url));
const env = Object.fromEntries(["PATH", "HOME", "USER", "TMPDIR"]
	.filter((key) => process.env[key]).map((key) => [key, process.env[key]]));
const dotnet = ["exec", "dotnet-legacy@10.0.401", "--", "dotnet"];
const build = spawnSync("mise", [...dotnet, "build", "apps/backend.browser-fixture/backend.browser-fixture.csproj", "--nologo", "-v:q"], {
	cwd: root, env, encoding: "utf8", timeout: 120_000,
});
assert.equal(build.status, 0, build.stdout + build.stderr);
const directory = await mkdtemp(join(tmpdir(), "nailbiter-browser-failure-"));
try {
	const destination = join(directory, "manifest.json");
	await mkdir(destination);
	const run = spawnSync("portless", ["run", "--name", "nba-browser-fixture-failure", "--tailscale", "mise", ...dotnet,
		"apps/backend.browser-fixture/bin/Debug/net10.0/backend.browser-fixture.dll", destination], {
		cwd: root, env, encoding: "utf8", timeout: 90_000,
	});
	const output = run.stdout + run.stderr;
	assert.equal(run.status, 1, output);
	assert.match(output, /Fixture setup failed: IOException/);
	assert.ok(!output.includes("Browser fixture ready"));
	const ids = [...output.matchAll(/Docker container ([a-f0-9]+) created/g)].map((match) => match[1]);
	assert.equal(ids.length, 2, "Failure must occur after owned Ryuk and PostgreSQL actually start");
	for (const id of ids) {
		const deadline = Date.now() + 20_000;
		let inspected;
		do {
			inspected = spawnSync("docker", ["inspect", id], { env, encoding: "utf8" });
			if (inspected.status !== 0) break;
			await delay(200);
		} while (Date.now() < deadline);
		assert.notEqual(inspected.status, 0, `Owned container ${id} remained after setup failure`);
		assert.match(inspected.stderr, /No such (object|container)/i);
	}
	const routes = spawnSync("portless", ["list"], { env, encoding: "utf8" });
	assert.equal(routes.status, 0);
	const localUrl = output.match(/https:\/\/[^\s]+nba-browser-fixture-failure\.localhost/)?.[0];
	assert.ok(localUrl, "Portless must have provided the failed setup's owned route");
	assert.ok(!routes.stdout.includes(localUrl));
	console.log("PASS: post-start setup failure exits cleanly and disposes only owned host/database/reaper");
} finally {
	await rm(directory, { recursive: true, force: true });
}
