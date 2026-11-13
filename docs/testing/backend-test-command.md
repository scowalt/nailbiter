# Backend test command

`scripts/test-backend` runs the backend test suites. It builds the selected suites and the projects they share in one `dotnet build`, then runs `dotnet test --no-build` for each suite. Use it instead of bare `dotnet test`.

```sh
scripts/test-backend
scripts/test-backend fsharp
scripts/test-backend csharp --filter FullyQualifiedName~NflPlayoffLeverage
scripts/test-backend --configuration Debug
```

Run `scripts/setup.sh` once in a new worktree first. The command takes the pinned .NET SDK from `mise env`, so it does not need `mise exec`. It passes the rest of your environment to `dotnet` unchanged, so variables such as `TESTCONTAINERS_RYUK_DISABLED` and `DOCKER_HOST` still apply.

## Selection

- The optional first argument selects the suites: `all` (the default), `fsharp` for `apps/backend.fsharp.tests`, or `csharp` for `apps/backend.tests`. The F# suite runs first because it takes seconds, and the C# suite takes about 26 minutes.
- `--filter` is a `dotnet test --filter` expression. The command passes it to every selected suite.
- `--configuration` is `Release` (the default, as CI uses) or `Debug`. The command prints the configuration that it uses.

Each selected suite must run at least one test. A filter that matches tests in only one suite therefore fails with `all`. Name that suite instead.

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | Every selected suite ran tests, and all of them passed. |
| 1 | A test failed, or a test run stopped without results. |
| 2 | The selection is invalid: an unknown suite or option, or a filter that the test adapter cannot parse. |
| 3 | Restore or build failed. No test ran. |
| 4 | A selected suite ran no test. |
| 5 | The toolchain is not available: mise is missing, `mise env` failed, or the pinned `dotnet` is not installed. |

When suites end differently, the command returns the first code that applies in the order 1, 2, 4.

`dotnet test` with xunit.runner.visualstudio 4.0.1 exits 0 when a filter matches no test. It also exits 0 for a filter that it cannot parse, and prints only an `Incorrect format for TestCaseFilter` message. The command reads each suite's TRX results file and its output, so neither case passes.

## Output and logs

For each failed test, the command prints the test name, its assertion message, and the first 8 lines of its stack trace. A short summary line for each suite follows. You do not need a second run to see why a test failed.

Each run keeps its logs in `obj/test-backend/runs/<time>-<pid>/`: `build.log`, one `<suite>.log` and one `<suite>.trx` for each suite. Git ignores `obj/`. The command keeps the 10 newest runs and deletes older ones. The build log uses normal verbosity, so you can see which projects compiled. The command never writes a binary log and never uses diagnostic verbosity, because both record environment variables. Read a log before you paste it anywhere.

## One worktree, one run at a time

The command holds an exclusive lock on `obj/test-backend/lock` through the build and all test runs. A second invocation in the same worktree prints `waiting for <the other run> to finish in this worktree` and starts when the first finishes. Its build is then incremental. Other worktrees have their own `obj/` and do not wait.

The lock covers the test phase too, because a test run loads the assemblies that a build would overwrite. Separate output directories for each invocation would remove the wait, but each invocation would then restore and compile every project again. The lock keeps one set of outputs. A long C# run makes a second invocation in the same worktree wait. Use another worktree to run in parallel.

If you stop the command with Ctrl+C or `SIGTERM`, it stops its build or test processes before it releases the lock. A `SIGKILL` cannot be caught, so after one the build or test can continue without the lock.

## Why the build is a solution

The command writes a solution file with the selected test projects into the run directory and builds it with `-p:ShouldUnsetParentConfigurationAndPlatform=false`. Without that property, a solution build compiles referenced projects that the solution does not list, such as `backend.shared` and `backend.fsharp`, in `Debug` while the test projects use `Release`. One build of both test projects compiles each shared project once. Two `dotnet test` commands run at the same time each compile the shared projects, into the same files.

## Tests

`tests/ci/test_backend_test_command.py` runs the command against fake `dotnet` and `mise` executables. It needs no .NET SDK. CI runs it with the other `tests/ci` contracts. To run it locally:

```sh
mise exec -- python -m unittest tests/ci/test_backend_test_command.py -v
```

CI still runs `dotnet test` directly. A later change can make CI use this command, so that CI also rejects a suite that runs no test.
