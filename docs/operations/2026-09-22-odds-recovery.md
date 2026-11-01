# Historical recovery boundary

A guarded odds-history recovery was applied in September 2026. Its original response captures, manifest, applied SQL/rollback receipt and production before/after evidence remain in private operator storage at `~/.local/state/nailbiter/backfills/2026-09-22-empty14/`. The public clean-source snapshot does not distribute that data or require it for development/CI.

Do not rerun, undo or reconstruct that production batch from examples. Existing-history guards intentionally reject duplicate recovery; code reversion is not data rollback. Production recovery/undo requires separate authorization and the exact reviewed evidence/rollback artifact. This note does not authorize opening private storage or live databases.

The reusable recovery tool and its independent synthetic tests remain public under `scripts/backfills/`. Use the synthetic tests and isolated TimescaleDB rehearsal contracts to develop the tool without old production data. See [the recovery tool runbook](../../scripts/backfills/README.md). Public development, tests and delivery must not depend on the private historical batch.
