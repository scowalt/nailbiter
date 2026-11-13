# Nailbiter

Nailbiter helps viewers choose sports events to watch, using pregame hype and live excitement scores. The live service is at [nailbiter.app](https://nailbiter.app).

This is the canonical public repository for development, issues/PRs, CI and production delivery on GitHub Actions. The original repository remains a private historical archive, not an active development or deployment dependency. See [deployment and operational boundaries](docs/deployment.md). Agents start at [AGENTS.md](AGENTS.md); [docs/README.md](docs/README.md) indexes the other documents.

## Components

- `apps/backend`: .NET 10 C# host, HTTP routes, Datastar/SSE streams and static assets, including the plain layered CSS described in [Styling](docs/styling.md).
- `apps/backend.fsharp`: F# game-data services and Falco.Markup rendering; `apps/backend.shared`: shared EF Core models, scoring and policies.
- `apps/backend.fsharp.tests` and `apps/backend.tests`: F# unit tests and PostgreSQL integration tests; `apps/backend.browser-fixture`: isolated host for [browser fixture](tests/browser-fixture/README.md) checks.
- `apps/web-components`: independently built browser components, including Chart.js.
- `apps/www`: independently built Astro marketing site, deployed to Cloudflare Workers.
- `apps/analysis`: offline Python analysis code.
- `alloy`: Grafana Alloy telemetry configuration that `docker-compose.yml` mounts; `opentofu`: infrastructure configuration, governed by [its README](opentofu/README.md).
- `scripts/ci` and `.github/workflows`: validation, artifact publication and single-controller release contracts.

Runtime versions live in `.mise.toml`; the .NET SDK pin must agree with `apps/backend/global.json`. Install the pinned tools with Mise. Keep locked Bun installs; no production credentials are needed to compile or run the offline tests.

## Build and test independently

```sh
bun install --frozen-lockfile --ignore-scripts
bun run lint
python scripts/test-fixtures/generate.py --check

bun install --cwd apps/web-components --frozen-lockfile --ignore-scripts
bun run --cwd apps/web-components build --outDir ../../.ci/web-components

bun install --cwd apps/www --frozen-lockfile --ignore-scripts
bun run --cwd apps/www build

dotnet restore apps/backend/backend.csproj
dotnet build apps/backend/backend.csproj --configuration Release
dotnet test apps/backend.fsharp.tests/backend.fsharp.tests.fsproj --configuration Release
dotnet test apps/backend.tests/backend.tests.csproj --configuration Release
```

Backend integration tests use disposable PostgreSQL Testcontainers, not the live database. Docker must be available. Recovery tests use isolated TimescaleDB containers with no host-published ports. All provider fixtures are synthetic and all provider HTTP is replaced by local handlers in the tested paths; never populate fixtures from production or provider endpoints. [Fixture provenance and coverage](tests/fixtures/README.md).

Browser regression tests use Playwright request interception rather than an application server:

```sh
bunx playwright install chromium
bun run test:live-sync
bun run test:icons
```

The optional [TesterArmy pilot](docs/operations/testerarmy-pilot-local.md) remains separate from normal CI and requires its own explicit provider/budget authorization. It is not necessary to build, test or deploy the application.

## Development

The marketing site runs independently from the backend. On a shared headless development machine, start browser-accessible development servers through Portless with private Tailscale sharing:

```sh
cd apps/www
portless run --tailscale astro dev
```

For backend development, set `ConnectionStrings__DefaultConnection` to your isolated development database. Do not start the production Compose stack against existing production-named volumes just to run tests. Explicitly disable collection and telemetry (the preserved application defaults are not an offline-development profile). From the repository root:

```sh
: "${ConnectionStrings__DefaultConnection:?Set an isolated development database first}"
DOTNET_ENVIRONMENT=Development ASPNETCORE_ENVIRONMENT=Development \
EnableScraper=false Sentry__Dsn= OpenTelemetry__Enabled=false \
Highlights__Worker__Enabled=false Highlights__YouTube__Enabled=false \
portless run --tailscale sh -c 'dotnet run --project apps/backend --no-launch-profile -- --urls "http://0.0.0.0:$PORT"'
```

Use `portless list` to discover/verify URLs; do not force or replace another project's route. Development configuration must not inject production telemetry or data credentials. [Domain vocabulary](GLOSSARY.md) and [agent/development rules](AGENTS.md) describe contribution conventions.

## Licensing and data

Original code, original documentation and newly constructed test fixtures are [MIT licensed](LICENSE). Dependencies, league artwork and provider material are separate: read [third-party scope/notices](THIRD_PARTY.md). The fixture generator does not read captured responses. Reference protocols, fixture IDs and team names do not assert historical observations or grant data-access rights.

League logos are retained for referential identification, not endorsement or ownership claims. Existing provider-use conditions and default-off activation gates remain intact. Publishing this code does not enable new collection or authorize bypassing denied access.

## Delivery boundary

GitHub Actions validates PRs on hosted runners without production credentials and is the production release controller. [The deployment runbook](docs/deployment.md) is authoritative for release requirements, approval and rollback boundaries.

No production, DNS, database, backup, provider activation or migration action should be inferred from a source/documentation edit.
