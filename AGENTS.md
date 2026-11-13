# Nailbiter development rules

The canonical public development, issue/PR, CI and CD repository is `scowalt/nailbiter`. GitHub Actions owns production delivery; the original repository is a private historical archive. Historical repositories are not active development, test or release dependencies.

## Source and review

- Every change reaches `main` through a reviewed pull request. Push a branch and create a PR only when the current task authorizes GitHub writes. Merge a PR only when the task explicitly authorizes that merge; authorization to create a PR is not authorization to merge it. Never push to `main` directly, and never bypass required checks, branch protection or failures.
- Preserve application behavior, data, backups, provider flags and deployment identity unless the task explicitly approves those changes.
- Source, tests, scripts and configuration are comment-free; put prose in Markdown. Follow `.comment-policy/README.md`.
- Backend styles are plain layered CSS with semantic class names and no build step. Follow `docs/styling.md`; do not add utility classes, `!important` or a styling dependency.
- Keep web components, marketing and backend independently buildable. Runtime pins are in `.mise.toml` and `apps/backend/global.json`.
- Preserve upstream license/NOTICE requirements. Original code and new synthetic fixtures are MIT; third-party artwork/provider material are not relicensed. See `THIRD_PARTY.md` before changing bundled material.

## Code map

The live application is one .NET 10 host: C# in `apps/backend`, F# in `apps/backend.fsharp`, and shared EF Core models, scoring and policies in `apps/backend.shared`. It renders HTML on the server with Falco.Markup and pushes live updates as Datastar fragments over SSE. Vanilla web components in `apps/web-components` draw the charts, highlights and race leaderboard. The marketing site in `apps/www` is a separate Astro build that runs as a Cloudflare Worker. Production runs the backend image with Docker Compose and Caddy on a VPS, and Doppler holds its secrets; `docs/deployment.md` owns those details. `README.md` lists every component and owns the build, test and development commands.

- If you change a page or its HTTP handler, start at `apps/backend/Routes/PageRoutes.cs`. `HandleHomePage` serves `/` and `HandleLeaguePage` serves `/nba`, `/nfl`, `/nhl`, `/mlb` and `/cfb`. Motorsport pages are in `MotorsportRoutes.cs`, SSE streams in `SseRoutes.cs`, and service wiring in `apps/backend/Program.cs`.
- If you change how games are read or collected, start at `apps/backend.fsharp/Services/GameDataService.fs`. Provider collectors and background services are beside it; college football, highlights and live-update services are in `apps/backend/Services`. The F# compile order is explicit in `apps/backend.fsharp/backend.fsharp.fsproj`.
- If you change rendering, start at `apps/backend.fsharp/Rendering/`: `HomePageRenderer.fs`, `LeaguePageRenderer.fs`, `SseFragmentRenderer.fs` and `Layout.fs`.
- If you change behavior, add or update tests. F# unit tests are in `apps/backend.fsharp.tests`. C# tests are in `apps/backend.tests`, where the integration tests run the real host against disposable PostgreSQL; `GroupedHomePageTests.cs`, `LeagueDatePageTests.cs` and `ListingPerformanceTests.cs` cover the listing routes. Browser regressions are in `tests/live-sync`.

## Context pointers

- In a new checkout or worktree, run `scripts/setup.sh` before building and fix every `fail` line it prints. `docs/development-setup.md` explains its checks, warnings and the BB provisioning hook.
- Before domain/policy work, read `GLOSSARY.md` and the relevant `docs/adr/` decisions, and follow `docs/agents/domain.md`.
- Before issue operations, read `docs/agents/issue-tracker.md`; before triage, read `docs/agents/triage-labels.md`.
- Before provider or odds changes, read `docs/live-odds.md` and `docs/sportsbook-sources.md`. `docs/README.md` indexes the other policy, runbook and test documents.
- Before fixture changes, read `tests/fixtures/README.md`. Maintain deterministic synthetic cases and regenerate with `scripts/test-fixtures/generate.py`; no provider/production captures or relabeled observations.
- Before database timeout, query cancellation or memory changes, read `docs/overload-safeguards.md`.
- Before Actions, package publishing, production, hosting, secrets or infrastructure work, read `docs/deployment.md`. Source workflow code is not permission to arm or run CD. Full migration means one public development home and one Actions release controller, never parallel private/public publishers.
- Before database backup or restore work, read `docs/operations/database-backups.md`; verify the target and exact extension version before restoring.
- Before the optional TesterArmy pilot, read `docs/operations/testerarmy-pilot-local.md`. Running a paid/live provider experiment requires separate explicit approval; it is not CI.

## Validation and safety

Run the relevant offline test suites and report actual results, failures and coverage limits. Synthetic fixtures verify implemented contracts, not live provider conformance. Preserve default-off source/feature activation gates and MoneyPuck's shared daily limit. Stop on an access denial; no bypasses, provider probing or production-data repair without a separately authorized scope.

For PR CI status, waiting and explicitly authorized merges, use `scripts/pr/pr-ci.py` as `docs/pr-workflow.md` describes.

On shared headless machines, start browser-accessible development servers with Portless/private Tailscale as the Development section of `README.md` shows. Let Portless allocate ports, verify reported endpoints with `portless list`, and remove temporary test servers. Preserve other projects' routes/proxy and keep tunnels private. In-process HTTP test servers and intercepted Playwright fixtures are preferred for offline regressions.
