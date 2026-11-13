# C# test shards in CI

The C# suite in `apps/backend.tests` takes about 30 minutes as one `dotnet test`
process. Most of that time is spent starting a PostgreSQL container for each
test and running `SentryGlobalHubCollection`, which has parallel execution
turned off. CI runs the suite as several matrix jobs instead. Each job is its own
process, so each job runs part of the Sentry collection.

## Jobs

| Job | Runs |
|---|---|
| `Checks` | Source policy, lint, browser regressions, Python suites, the web component and marketing builds, the backend build and the F# suite. Uploads the marketing artifact. |
| `Backend integration tests (N/M)` | One shard of the C# suite. |
| `Validate` | Waits for `Checks` and every shard. Fails unless all of them succeeded. |
| `Build and scan backend image` | Builds and scans the image. Does not wait for validation. |

The ruleset requires `Validate` and `Build and scan backend image`, so the
shards do not need their own required contexts. `Release production` still
needs both required jobs.

## How a shard picks its tests

`scripts/ci/backend-test-shard.py <index> <count>` gets `strategy.job-index` and
`strategy.job-total` from the workflow:

1. Lists the built suite with `dotnet test --list-tests`.
2. Groups the tests by class and puts each class, largest first, in the shard
   with the fewest tests so far. Every shard computes the same split from the
   same list, so each class runs in exactly one shard.
3. Runs its classes with a `FullyQualifiedName~backend.tests.<Class>.` filter
   and writes a TRX file.
4. Fails if a test failed, errored or timed out, if no test passed, or if
   the TRX classes differ from the classes the shard was given.

To change the number of shards, edit the `shard` list in the matrix. Nothing
else stores the count. `tests/ci/test_backend_test_shard.py` covers the
split and every failure check against a fake `dotnet`.

Run one shard locally after a Release build:

```sh
dotnet build apps/backend.tests/backend.tests.csproj --configuration Release
python scripts/ci/backend-test-shard.py 0 4
```
