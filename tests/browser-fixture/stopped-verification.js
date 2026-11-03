import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { access } from "node:fs/promises";

export async function verifyStoppedFixture(fixture) {
	const database = spawnSync("docker", ["inspect", fixture.manifest.databaseContainerId], { encoding: "utf8" });
	assert.notEqual(database.status, 0);
	assert.match(database.stderr, /No such (object|container)/i);
	const routes = spawnSync("portless", ["list"], { encoding: "utf8" });
	assert.equal(routes.status, 0);
	assert.ok(!routes.stdout.includes(fixture.manifest.localUrl));
	await assert.rejects(access(fixture.manifestPath), { code: "ENOENT" });
	const supplement = fixture.manifest.groupedHome ?? fixture.manifest.series ?? fixture.manifest.highlights;
	if (supplement) await assert.rejects(access(supplement.controlPath), { code: "ENOENT" });
	return {
		databaseContainerId: fixture.manifest.databaseContainerId,
		databaseAbsent: true,
		routeAbsent: true,
		manifestRemoved: true,
		...(supplement && { ownedControlRemoved: true }),
		sharedProxyStopped: false,
	};
}
