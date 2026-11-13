# Worktree setup and preflight

`scripts/setup.sh` makes a new checkout or worktree ready to build, test and commit. It checks the toolchain first and stops before installing anything if a tool is wrong. Run it again at any time. A second run changes nothing.

```sh
scripts/setup.sh
```

It exits 0 with `READY` when the worktree is usable, and 1 with `NOT READY` when something needs a fix. Every problem line has a `fix:` line under it.

## What it checks and prepares

1. It loads the tools pinned in `.mise.toml` through `mise env`, so its own commands use the pinned versions whatever your shell's `PATH` says.
2. It checks that each pinned tool reports its pinned version. A pin such as `"1.4"` accepts any `1.4.x`. It skips comments and accepts CRLF line endings, extra spaces and single-quoted strings. A pin it cannot read, such as an inline table, an array, a dotted key like `bun.version`, a `[tools.name]` table or `"latest"`, is a failure, and so is a `[tools]` table with no pins, so the check never passes having checked nothing. A key with a backend prefix, such as `"aqua:gitleaks/gitleaks"`, does not name the command it installs, so setup warns that it cannot check it. For .NET it checks that `.mise.toml` and `apps/backend/global.json` pin the same SDK. It then runs `dotnet --version` in `apps/backend`, where `global.json` applies. The reported SDK must be the pinned one, or a later feature band of the same major and minor version, as `rollForward: latestFeature` allows.
3. It warns when the same command in your own shell resolves to a different version. In some agent shells on the shared host, `~/.bun/bin/bun` comes before mise's shims, so a bare `bun` there is not the pinned version. Run such commands as `mise exec -- bun ...`, or activate mise in your shell profile so its tools come first. BB's hook runs with mise's shims first, so it prints no such warning.
4. It runs `bun install --frozen-lockfile --ignore-scripts` for the root, `apps/web-components` and `apps/www`.
5. It installs the Lefthook pre-commit hook (gitleaks and the comment policy) only when there is no pre-commit hook at all. Worktrees share one hooks directory, and `core.hooksPath` can point it elsewhere. A Lefthook hook already there is left as it is. Lefthook would rename any other pre-commit hook to `pre-commit.old`, and that hook would then stop running, so setup never installs over one. It warns instead and says how to run this repository's checks.
6. It restores the backend, `backend.fsharp.tests` and `backend.tests` projects one at a time, so a failure names its project.
7. It launches Playwright's Chromium once. If the launch fails, the result is a warning, because only the browser regressions need it. If a bun install failed, it skips this check, because the missing packages are the cause.

Each install or restore step stops after 150 seconds, the browser check after 60, and each version check after 20. A step that ignores the stop signal is killed 10 seconds later. BB's hook also stops the whole run after 14 minutes, inside BB's 15-minute limit, and keeps the worktree. With empty bun and NuGet caches the whole run took 16.6 seconds on the shared host and filled about 1.5 GB of cache. A repeated run took about 6 seconds.

## What it never does

It never runs `mise install`, `mise use`, `mise trust` or `git config --global`. It never downloads a toolchain, switches a branch, changes another worktree, writes refs, pushes or opens a pull request, and never replaces or renames a git hook it did not install. It runs every command with `MISE_AUTO_INSTALL=0`. Otherwise a mise shim on `PATH` installs a missing pinned version the first time setup asks for its version. `MISE_EXEC_AUTO_INSTALL=0` alone does not stop a shim, and `mise env` never installs. When a pinned tool or the .NET SDK is missing, the fix line says `mise install`. Run it from the repository root. It installs the versions in `.mise.toml`, including the .NET SDK, without changing any global default.

`.mise.toml` contains only `[tools]`, so mise resolves it in a new worktree path without `mise trust`. This was measured with mise 2026.7.13. If the file ever needs trust, the script reports mise's error and says to review the file and run `mise trust`.

## BB worktrees

BB runs the tracked `.bb-env-setup.sh` in each new BB-managed worktree. That hook runs `scripts/setup.sh` in a session of its own and always exits 0. When setup finishes, reaches the 14-minute deadline, or BB cancels the hook with `TERM`, `INT` or `HUP`, the hook kills every process left in that session. That includes an install still running at the deadline and the MSBuild worker nodes that `dotnet restore` leaves behind, so nothing keeps working in the worktree after BB hands it to the agent or removes it. A `SIGKILL` to the hook cannot be caught, so after one setup keeps running until each step's own time limit. The hook reports the deadline only when its watchdog actually stopped setup.

The hook needs Linux: GNU `timeout`, util-linux `setsid`, and procps `pkill` and `pgrep`. Without `setsid`, `pkill` or `pgrep` it runs `scripts/setup.sh` under `timeout` with the same 14-minute deadline. That stops setup itself, but a step that is still running continues until its own time limit. Without `timeout`, setup reports `NOT READY` before doing any work. Either way the hook exits 0 and the worktree is kept. BB deletes a new worktree when its hook exits non-zero, and every problem the script reports can be fixed inside the worktree, so a failing preflight never costs the thread its environment. BB shows the hook's output in the provisioning transcript. An agent in a new worktree runs `scripts/setup.sh` itself to see the verdict. With warm caches and the daemon's `PATH`, a new worktree took 6.9 seconds, and a repeated run 6.0 seconds.

The repository has no `.worktreeinclude`. Builds and tests need no untracked local configuration.

## Example

The first run of BB's hook in a new worktree on the shared host, with the daemon's `PATH`:

```text
nailbiter setup: /tmp/nb-hook-8hy1/wt
ok    bun 1.4.2
ok    .NET SDK 10.0.401 (apps/backend/global.json)
ok    gitleaks 8.30.1
ok    python 3.14.8
ok    uv 0.11.32
ok    bun dependencies (.)
ok    bun dependencies (apps/web-components)
ok    bun dependencies (apps/www)
ok    git pre-commit hook (lefthook)
ok    dotnet restore (backend)
ok    dotnet restore (backend.fsharp.tests)
ok    dotnet restore (backend.tests)
ok    Playwright Chromium
READY (0 warning(s))
```

The hooks directory already held Lefthook's hook, so setup left it as it was. In a clone with no pre-commit hook, that line reads `ok    git pre-commit hook installed (lefthook)`.

## Tests

`tests/ci/test_setup.py` covers the following cases:

- a fresh clone and worktree set up through the BB hook command
- a repeated run that changes no file
- a standalone run
- a check that global defaults, branches and refs are unchanged
- a missing mise, a mise that cannot load the config, and mise warnings
- commented and prefix pins, and pin forms it cannot read
- a missing tool and a mismatched tool
- a missing, older and later-major .NET SDK, a later feature band, and pins that disagree
- CRLF line endings, a `[tools]` table with no pins, and keys with a backend prefix
- a failed install, a failed restore, a missing browser, and the hook keeping a worktree that is not ready
- an existing pre-commit hook that is not Lefthook's
- the hook stopping setup at its deadline and leaving no process behind, and not reporting a deadline that setup beat
- the hook cancelled with `TERM` to its process group, as BB cancels it, leaving no process behind
- a host without `setsid`, and a host without GNU `timeout`
- single-quoted pins, dotted keys and arrays
- a real mise shim, with `auto_install` on, in front of a pinned version that is not installed: setup must fail with `mise install` and make no network connection

The fake-toolchain cases use shims in a temporary directory and touch no installed tool. The real-mise case points mise's data, config, cache and state directories at a temporary directory, and sends any download to a local proxy that only counts connections. CI runs the file with the other `tests/ci` contracts. To run it locally:

```sh
mise exec -- python -m unittest tests/ci/test_setup.py -v
```
