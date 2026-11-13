# Worktree setup and preflight

`scripts/setup.sh` makes a new checkout or worktree ready to build, test and commit. It checks the toolchain first and stops before installing anything if a tool is wrong. Run it again at any time. A second run changes nothing.

```sh
scripts/setup.sh
```

It exits 0 with `READY` when the worktree is usable, and 1 with `NOT READY` when something needs a fix. Every problem line has a `fix:` line under it.

## What it checks and prepares

1. It loads the tools pinned in `.mise.toml` through `mise env`, so its own commands use the pinned versions whatever your shell's `PATH` says.
2. It checks that each pinned tool reports its pinned version. A pin such as `"1.4"` accepts any `1.4.x`. It skips comments and warns about pin forms it cannot read, such as inline tables. For .NET it checks that `.mise.toml` and `apps/backend/global.json` pin the same SDK. It then runs `dotnet --version` in `apps/backend`, where `global.json` applies. The reported SDK must be the pinned one, or a later feature band of the same major and minor version, as `rollForward: latestFeature` allows.
3. It warns when the same command in your own shell resolves to a different version. On this project's shared host, `~/.bun/bin/bun` comes before mise's shims, so a bare `bun` is not the pinned version. Run such commands as `mise exec -- bun ...`, or activate mise in your shell profile so its tools come first.
4. It runs `bun install --frozen-lockfile --ignore-scripts` for the root, `apps/web-components` and `apps/www`.
5. It installs the Lefthook pre-commit hook (gitleaks and the comment policy). Worktrees share one hooks directory, so when a pre-commit hook that mentions Lefthook is already there, it leaves that hook alone. Lefthook renames any other existing pre-commit hook to `pre-commit.old`.
6. It restores the backend, `backend.fsharp.tests` and `backend.tests` projects.
7. It launches Playwright's Chromium once. If the launch fails, the result is a warning, because only the browser regressions need it.

Each install or restore step stops after 150 seconds, the browser check after 60, and each version check after 20. A step that ignores the stop signal is killed 10 seconds later. BB's hook also stops the whole run after 14 minutes, inside BB's 15-minute limit, and keeps the worktree. With empty bun and NuGet caches the whole run took 16.6 seconds on the shared host and filled about 1.5 GB of cache. A repeated run took about 6 seconds.

## What it never does

It never runs `mise install`, `mise use`, `mise trust` or `git config --global`. It never downloads a toolchain, switches a branch, changes another worktree, writes refs, pushes or opens a pull request. When a pinned tool or the .NET SDK is missing, the fix line says `mise install`. Run it from the repository root. It installs the versions in `.mise.toml`, including the .NET SDK, without changing any global default.

`.mise.toml` contains only `[tools]`, so mise resolves it in a new worktree path without `mise trust`. This was measured with mise 2026.7.13. If the file ever needs trust, the script reports mise's error and says to review the file and run `mise trust`.

## BB worktrees

BB runs the tracked `.bb-env-setup.sh` in each new BB-managed worktree. That hook runs `scripts/setup.sh` and always exits 0. BB deletes a new worktree when its hook exits non-zero, and every problem the script reports can be fixed inside the worktree, so a failing preflight never costs the thread its environment. BB shows the hook's output in the provisioning transcript. An agent in a new worktree runs `scripts/setup.sh` itself to see the verdict. With warm caches a new worktree took 8.9 seconds, and a repeated run 6.7 seconds.

The repository has no `.worktreeinclude`. Builds and tests need no untracked local configuration.

## Example

The first run in a new clone on the shared host:

```text
nailbiter setup: /tmp/nb-fresh-RwqU/wt
ok    bun 1.4.2
warn  'bun' in your own shell is /home/scowalt/.bun/bin/bun, not bun 1.4.2
      fix: run it as 'mise exec -- bun ...', or activate mise in your shell profile so its tools come first
ok    .NET SDK 10.0.401 (apps/backend/global.json)
ok    gitleaks 8.30.1
ok    python 3.14.8
ok    uv 0.11.32
ok    bun dependencies (.)
ok    bun dependencies (apps/web-components)
ok    bun dependencies (apps/www)
ok    git pre-commit hook installed (lefthook)
ok    dotnet restore (backend, backend.fsharp.tests, backend.tests)
ok    Playwright Chromium
READY (1 warning(s))
```

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
- a failed install, a missing browser, and the hook keeping a worktree that is not ready

The fake-toolchain cases use shims in a temporary directory and touch no installed tool. CI runs the file with the other `tests/ci` contracts. To run it locally:

```sh
mise exec -- python -m unittest tests/ci/test_setup.py -v
```
