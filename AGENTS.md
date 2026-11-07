# Nailbiter development rules

The canonical public development, issue/PR, CI and CD repository is `scowalt/nailbiter`. GitHub Actions owns production delivery; the original repository is a private historical archive. Historical repositories are not active development, test or release dependencies.

## Source and review

- Make changes through reviewed PRs. Push/create PRs only when the current task authorizes GitHub writes. Preserve required checks and protection; never merge directly to main or bypass failures.
- Preserve application behavior, data, backups, provider flags and deployment identity unless the task explicitly approves those changes.
- Keep frontend, marketing and backend independently buildable. Runtime pins are in `.mise.toml` and `apps/backend/global.json`.
- Preserve upstream license/NOTICE requirements. Original code and new synthetic fixtures are MIT; third-party artwork/provider material are not relicensed. See `THIRD_PARTY.md` before changing bundled material.

## Context pointers

- Before domain/policy work, read `GLOSSARY.md` and the relevant `docs/adr/` decisions.
- Before issue operations, read `docs/agents/issue-tracker.md`; before triage, read `docs/agents/triage-labels.md`.
- Before fixture changes, read `tests/fixtures/README.md`. Maintain deterministic synthetic cases and regenerate with `scripts/test-fixtures/generate.py`; no provider/production captures or relabeled observations.
- Before Actions, package publishing, production or infrastructure work, read `docs/deployment.md`. Source workflow code is not permission to arm or run CD. Full migration means one public development home and one Actions release controller, never parallel private/public publishers.
- Before database backup or restore work, read `docs/operations/database-backups.md`; verify the target and exact extension version before restoring.
- Before the optional TesterArmy pilot, read `docs/operations/testerarmy-pilot-local.md`. Running a paid/live provider experiment requires separate explicit approval; it is not CI.

## Validation and safety

Run the relevant offline test suites and report actual results, failures and coverage limits. Synthetic fixtures verify implemented contracts, not live provider conformance. Preserve default-off source/feature activation gates and MoneyPuck's shared daily limit. Stop on an access denial; no bypasses, provider probing or production-data repair without a separately authorized scope.

For GitHub Actions status, obtain the exact PR head SHA and use `gh run list --commit`; the configured token cannot read Checks/statusCheckRollup. Actions-only evidence does not prove every protection requirement passed.

On shared headless machines, start browser-accessible development servers with Portless/private Tailscale, let it allocate ports, verify reported endpoints with `portless list`, and remove temporary test servers. Preserve other projects' routes/proxy and keep tunnels private. In-process HTTP test servers and intercepted Playwright fixtures are preferred for offline regressions.
