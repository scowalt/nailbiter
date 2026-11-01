import { spawn, spawnSync } from "node:child_process";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { createConnection } from "node:net";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { setTimeout as delay } from "node:timers/promises";

const root = fileURLToPath(new URL("../../", import.meta.url));
const project = "apps/backend.browser-fixture/backend.browser-fixture.csproj";
const dll = "apps/backend.browser-fixture/bin/Debug/net10.0/backend.browser-fixture.dll";
const dotnet = ["exec", "asdf:mise-plugins/mise-dotnet-core@10.0.401", "--", "dotnet"];

function cleanEnvironment() {
	return Object.fromEntries(
		["PATH", "HOME", "USER", "TMPDIR"].filter((key) => process.env[key]).map((key) => [key, process.env[key]]),
	);
}

export async function startFixture({ handleSignals = true, highlights = false, groupedHome = false, series = false } = {}) {
	if ([highlights, groupedHome, series].filter(Boolean).length > 1) throw new Error("Choose one opt-in fixture scenario.");
	const env = cleanEnvironment();
	const build = spawnSync("mise", [...dotnet, "build", project, "--nologo", "-v:q"], {
		cwd: root, env, encoding: "utf8", timeout: 120_000,
	});
	if (build.error || build.status !== 0) throw new Error(`Fixture build failed: ${build.error ?? build.stdout + build.stderr}`);
	const directory = await mkdtemp(join(tmpdir(), "nailbiter-browser-lifecycle-"));
	const manifestPath = join(directory, "manifest.json");
	const child = spawn("portless", ["run", "--name", series ? "series-browser-fixture" : "nba-browser-fixture", "--tailscale", "mise", ...dotnet, dll, manifestPath, ...(highlights ? ["--highlights"] : groupedHome ? ["--grouped-home"] : series ? ["--series"] : [])], {
		cwd: root, env, stdio: ["ignore", "pipe", "pipe"],
	});
	let output = "";
	const capture = (chunk) => { output = highlights || series ? output + chunk : (output + chunk).slice(-16_000); };
	child.stdout.on("data", capture);
	child.stderr.on("data", capture);
	let spawnError;
	child.on("error", (error) => { spawnError = error; });
	let exited = false;
	const exit = new Promise((resolve) => {
		child.once("close", (code, signal) => { exited = true; resolve({ code, signal }); });
	});
	let stopped;
	function stop() {
		stopped ??= (async () => {
			if (!exited) child.kill("SIGTERM");
			const result = await Promise.race([exit, delay(30_000, null, { ref: false })]);
			if (!result) throw new Error(`Owned fixture did not stop; inspect PID ${child.pid}. No shared cleanup attempted.`);
			process.removeListener("SIGINT", onInterrupt);
			process.removeListener("SIGTERM", onTerminate);
			await rm(directory, { recursive: true, force: true });
		})();
		return stopped;
	}
	const onInterrupt = () => { stop().then(() => process.exit(130)).catch(console.error); };
	const onTerminate = () => { stop().then(() => process.exit(143)).catch(console.error); };
	if (handleSignals) {
		process.once("SIGINT", onInterrupt);
		process.once("SIGTERM", onTerminate);
	}
	try {
		const deadline = Date.now() + 90_000;
		let manifest;
		while (!manifest) {
			if (spawnError || exited) throw new Error(`Fixture setup failed: ${spawnError ?? output}`);
			if (Date.now() >= deadline) throw new Error(`Fixture setup timed out: ${output}`);
			try { manifest = JSON.parse(await readFile(manifestPath, "utf8")); }
			catch (error) { if (error.code !== "ENOENT") throw error; }
			if (!manifest) await delay(100);
		}
		const listing = spawnSync("portless", ["list"], { cwd: root, env, encoding: "utf8", timeout: 10_000 });
		if (listing.status !== 0 || !listing.stdout.includes(manifest.localUrl) || !listing.stdout.includes(manifest.privateUrl))
			throw new Error(`Portless did not report both fixture URLs: ${listing.stdout}${listing.stderr}`);
		for (const url of [manifest.localUrl, manifest.privateUrl]) {
			const check = spawnSync("curl", ["--fail", "--silent", "--show-error", "--max-time", "10", `${url}/health`], {
				cwd: root, env, encoding: "utf8", timeout: 15_000,
			});
			if (check.status !== 0 || JSON.parse(check.stdout).status !== "healthy")
				throw new Error(`Required hosting unavailable at ${url}: ${check.stderr}`);
		}
		console.log(listing.stdout.trim());
		async function control(command) {
			const scenario = manifest.groupedHome ?? manifest.series ?? manifest.highlights;
			if (!scenario) throw new Error("Opt-in fixture controls are not enabled.");
			return new Promise((resolve, reject) => {
				const socket = createConnection(scenario.controlPath);
				let response = "";
				socket.setTimeout(30_000, () => socket.destroy(new Error("Owned fixture IPC timed out.")));
				socket.on("error", reject);
				socket.on("connect", () => socket.write(`${command}\n`));
				socket.on("data", (chunk) => {
					response += chunk;
					if (!response.includes("\n")) return;
					socket.end();
					try {
						const result = JSON.parse(response);
						if (result.error) reject(new Error(`Fixture operation failed: ${result.error}`));
						else resolve(result);
					} catch (error) { reject(error); }
				});
			});
		}
		return { manifest, manifestPath, stop, control, logs: () => output };
	} catch (error) {
		await stop();
		throw error;
	}
}
