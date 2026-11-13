# Documentation index

Each document below is the authoritative source for its topic. [AGENTS.md](../AGENTS.md) and [README.md](../README.md) point here instead of repeating these procedures. Read [GLOSSARY.md](../GLOSSARY.md) for domain vocabulary and [adr/](adr/) for architectural decisions.

## Agent workflow and conventions

- [agents/domain.md](agents/domain.md): use the glossary and flag ADR conflicts.
- [agents/issue-tracker.md](agents/issue-tracker.md): GitHub issue commands and wayfinding.
- [agents/triage-labels.md](agents/triage-labels.md): triage role labels.
- [development-setup.md](development-setup.md): `scripts/setup.sh`, its readiness checks for a new checkout or worktree, and the BB provisioning hook.
- [styling.md](styling.md): plain layered CSS conventions.

## Delivery and operations

- [deployment.md](deployment.md): hosting, secrets, CI validation, the release contract and production boundaries.
- [overload-safeguards.md](overload-safeguards.md): the swap reserve and PostgreSQL statement timeout on the shared VPS.
- [operations/database-backups.md](operations/database-backups.md): backup and restore verification.
- [operations/mlb-highlights-local.md](operations/mlb-highlights-local.md): MLB highlights local acceptance and operational boundary.
- [operations/testerarmy-pilot-local.md](operations/testerarmy-pilot-local.md) and [operations/testerarmy-pilot-evaluation.md](operations/testerarmy-pilot-evaluation.md): the optional TesterArmy pilot.

## Scoring and provider policy

- [pro-hype-evidence.md](pro-hype-evidence.md): pregame hype components and their evidence.
- [live-odds.md](live-odds.md) and [sportsbook-sources.md](sportsbook-sources.md): game-winner probabilities and provider boundaries.
- [playoff-odds.md](playoff-odds.md) and [nfl-playoff-odds.md](nfl-playoff-odds.md): playoff qualification probabilities.
- [nfl-playoff-leverage.md](nfl-playoff-leverage.md) and [playoff-leverage-source-research.md](playoff-leverage-source-research.md): conditional playoff leverage.
- [nba-seeding-evidence.md](nba-seeding-evidence.md) and [nhl-seeding-evidence.md](nhl-seeding-evidence.md): seeding evidence.
- [nhl-playoff-odds-sources.md](nhl-playoff-odds-sources.md), [nhl-moneypuck-qualification.md](nhl-moneypuck-qualification.md) and [nhl-moneypuck-qualification-source-notes.md](nhl-moneypuck-qualification-source-notes.md): NHL qualification sources and the MoneyPuck runbook.
- [nhl-guru-rankings.md](nhl-guru-rankings.md) and [nhl-power-ranking-source-research.md](nhl-power-ranking-source-research.md): NHL quality from power rankings.
- [college-football.md](college-football.md): college football support.
- [mlb-highlights-application.md](mlb-highlights-application.md) and [mlb-highlights-lifecycle.md](mlb-highlights-lifecycle.md): MLB highlights.

## Testing

- [testing/shared-postgresql-fixture.md](testing/shared-postgresql-fixture.md): the shared PostgreSQL server for hosted game tests.
- [testing/ci-backend-shards.md](testing/ci-backend-shards.md): how CI splits the C# suite across parallel jobs.
- [testing/ryuk-host-port-stress.md](testing/ryuk-host-port-stress.md): the Testcontainers Ryuk host-port probe.
- [testing/pro-hype-continuity.md](testing/pro-hype-continuity.md), [testing/suspense-weighted-excitement.md](testing/suspense-weighted-excitement.md) and [testing/mlb-highlights-acceptance.md](testing/mlb-highlights-acceptance.md): feature verification matrices.
- [../tests/fixtures/README.md](../tests/fixtures/README.md): synthetic fixture provenance.

## Historical records

These documents record past states. They are not current instructions.

- [woodpecker.md](woodpecker.md): the retired Woodpecker controller. [deployment.md](deployment.md) records its retirement.
- [1password-cloudflare-dns.md](1password-cloudflare-dns.md): an older origin DNS configuration.
- [operations/2026-09-22-odds-recovery.md](operations/2026-09-22-odds-recovery.md): a completed odds-history recovery.
