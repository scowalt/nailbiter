# Shared PostgreSQL server for hosted game tests

`HostedGameFixture` starts a real app host against PostgreSQL. By default each
instance starts its own container, which takes about 2.5 seconds on an idle
host and is most of a short test's run time. `SharedPostgreSql` lets a test
class or collection start one container and give each case its own database
instead. `ListingPerformanceTests` and `SharedPostgreSqlTests` use it. Other
suites still use one container per fixture.

## Lifecycle

| Step | Owner | What happens |
|---|---|---|
| Class or collection starts | `SharedPostgreSql.InitializeAsync` | Starts one container through `IsolatedPostgreSql`, so it keeps the Ryuk reaper and the disposable host-port range. Creates `nailbiter_template` and runs `EnsureCreatedAsync` on it once. |
| Case starts | `HostedGameFixture.InitializeAsync` | Runs `CREATE DATABASE nailbiter_case_<guid> TEMPLATE nailbiter_template`, then starts its own `WebApplicationFactory` with its own `FakeTimeProvider` and service registrations. `EnsureCreatedAsync` finds the tables and does nothing. |
| Case ends | `HostedGameFixture.DisposeAsync` | Disposes the client and app host, clears the case database's connection pool, and runs `DROP DATABASE ... WITH (FORCE)` on that database only. |
| Class or collection ends | `SharedPostgreSql.DisposeAsync` | Disposes the container. If the test process dies first, Ryuk removes the container. |

Nothing outside these steps is touched. The fixture does not change Docker
settings, prune images or volumes, or stop containers it did not start.

## Isolation rules

- Each case gets a fresh database cloned from the template. Seed data, row IDs
  and sequences never carry over, so cases can reuse game IDs and run in any
  order.
- Each case gets its own app host. The fake clock, `configureServices`
  registrations, interceptors and in-memory caches belong to that case only.
  Do not put an app host, a `FakeTimeProvider` or an `HttpClient` on the shared
  fixture.
- Cases that run at the same time are supported. Several `HostedGameFixture`
  instances can start against one `SharedPostgreSql` concurrently.
- The template holds only the schema from `EnsureCreatedAsync`. Seed data in
  each case, never in the template, or later cases inherit it.

`SharedPostgreSqlTests` checks these rules. Two cases seed the same game ID
with different values, four concurrent cases do the same while one advances its
clock, and a disposed case's database must be gone while another case still
works. If every case shares one database, all four tests fail.

## Adopt it in a test class

1. Add `IClassFixture<SharedPostgreSql>` to the test class. To share one
   container across several classes, use an `ICollectionFixture` instead.
2. Take `SharedPostgreSql server` in the constructor and pass it to the
   per-case fixture: `new HostedGameFixture(now, configureServices, server)`.
3. Keep creating the `HostedGameFixture` per case and calling its
   `InitializeAsync` and `DisposeAsync` from the test class's `IAsyncLifetime`.

A suite that needs a schema other than `EnsureCreatedAsync` output, such as
SQL migrations or TimescaleDB hypertables, needs its own template step.
`SharedPostgreSql` builds only the EF Core model.

## Measure a change

`scripts/listing-test-timing/measure.py` builds two git refs from `git archive`
exports, runs one unrecorded warm-up per side, then runs the same test
selection before, after, before, after. It records the 1-minute load average
around each run, the `dotnet test --no-build` wall time, pass and fail counts,
and phase totals from `HostedGameFixture`.

```sh
python3 scripts/listing-test-timing/measure.py --before <base-ref> --after <ref> --runs 5
```

Both refs must contain `FixtureTimings`, or the phase columns stay at zero. The
default selection is `ListingPerformanceTests`; pass `--filter` to change it.
The script deletes its exported trees and keeps the logs and `runs.tsv` in
`--out` or a new temporary directory. It exits non-zero when any run fails or
skips a test.

The phases are summed across cases and include each case's share of the shared
server:

| Phase | Measures |
|---|---|
| `container` | Container start. Per case without a shared server, once per class with one. |
| `database` | `CREATE DATABASE ... TEMPLATE` per case. |
| `host` | `WebApplicationFactory` build and `CreateClient`. |
| `schema` | `EnsureCreatedAsync` per case, plus the template build on the shared server. |
| `seed` | Time inside `HostedGameFixture.SeedAsync`. |
| `body` | Time from the end of `InitializeAsync` to the start of `DisposeAsync`, minus `seed`. |
| `dispose` | Host disposal and database or container removal. |

Phases overlap when cases run in parallel, so the sum can exceed the wall
time. The shared host's load moves run times a lot. Compare sides only within
one interleaved session, and report the load average with each number.
