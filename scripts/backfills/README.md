# Guarded empty-history recovery

`recover_empty_odds.py` is an **offline SQL generator**, not a scraper or database
client. Use it only after a human approves an explicit batch of entirely empty
MLB/NFL histories. It is not a season importer or a partial-gap repair tool.

## Evidence and preview

1. Under separately approved read-only operational scope, audit identities and production coverage. Keep a private reviewed manifest listing each
   game, home/away market tickers, scheduled start, observed play bounds and expected
   point count. Never match doubleheaders by nearest start or mix event outcomes.
2. Only under separately authorized provider access, retain responses in a private evidence directory, using these filenames:
   - `<id>-espn.json`: ESPN site summary, including header and plays/drives.
   - `<id>-home-market.json`, `<id>-away-market.json`: the **inner `market` object**
     from Kalshi `/trade-api/v2/markets/<ticker>`.
   - `<id>-home-candles.json`, `<id>-away-candles.json`: full Kalshi responses from
     `/trade-api/v2/series/<series>/markets/<ticker>/candlesticks`, queried with
     `start_ts`, `end_ts` and `period_interval=1`. Include the response's `ticker`.
   Keep source URLs and retrieval times alongside the evidence. Archived markets
   need their documented historical route and applicable access permission. No new capture is authorized by this runbook.
3. Generate a new preview directory (existing output directories are refused):

   ```sh
   python3 scripts/backfills/recover_empty_odds.py \
     --manifest /path/to/reviewed-private-manifest.json \
     --evidence /path/to/evidence --output /path/to/new-preview
   ```

The generator verifies sport/date/teams, exact event identity, ESPN final status,
play bounds, market settlement/winner and a close within 30 minutes of the last
play. Schedule exceptions require a reviewed `selection_note`. An explicit NFL
`end_play_id` plus `play_window_note` can identify the actual final whistle when
administrative records have corrupt timestamps; it cannot identify an arbitrary
midgame play. These exceptions are audited facts, not automatic heuristics.

Only unique, aligned one-minute intervals entirely inside play are eligible.
Both outcomes need positive trade volume and finite, nonterminal closing prices.
Probabilities are normalized from those two simultaneous closes. Invalid, absent,
duplicate, pregame and postgame candles are omitted, never interpolated. A changed
point count aborts preparation rather than silently changing the approved batch.

Outputs include `apply.sql`, `rollback.sql`, the manifest, and a receipt containing
SHA-256 hashes of every evidence file and both SQL files. SQL defaults to
**ROLLBACK**; nothing is executed by the generator.

## Database safety and application

Run tests before writing, using an isolated database without host port exposure:

```sh
python3 -m unittest discover -s scripts/backfills -p 'test_*.py' -v
RUN_RECOVERY_DB_TESTS=1 python3 -m unittest discover -s scripts/backfills -p 'test_*.py' -v
```

The second command owns and removes its own TimescaleDB container. Tests exercise
compressed chunks, dry runs, commits, existing-data/duplicate refusal, changed game
identity, unchanged unrelated live data and guarded undo. CI runs both kinds.

Before an approved production write:

- Save the target games and all existing target snapshots using a read-only query.
  All target histories must still be empty. Save control-row checksums as well.
- Rehearse the **entire generated batch** in an isolated database, including undo.
  A SQL `ROLLBACK` does not restore sequence counters; this is not a read-only probe.
- Generate a separate output directory with `--commit`. Review its manifest, point
  count, evidence hashes and SQL digest against the tested preview.
- Use the already-authenticated database connection, with `psql -X -v ON_ERROR_STOP=1`,
  to execute `apply.sql` once. No connection string or credential is embedded.

The transaction rechecks every game identity/status, refuses any existing snapshot
for a target game, rejects reused batch IDs, and checks counts and play bounds.
It locks only the target game rows. The existing `odds_snapshots.game_id` foreign
key makes concurrent inserts acquire conflicting key-share locks; preserve that
foreign key. Lock/statement timeouts fail closed rather than blocking live writes
indefinitely. All rows are tagged with a unique batch `source` and
`is_historical=true`. Scores, clocks, periods and unrelated odds remain null;
no game record, existing snapshot or live excitement score is rewritten.

Verify the stored rows against the generated plan, unchanged target game metadata,
control-row checksums, public APIs and full chart data. The API returns at most 100
points; the detail page includes the complete history. If execution disconnects,
inspect the batch before retrying. A repeat import intentionally refuses to run.

## Undo

Keep the evidence, receipts, pre-write backup, post-write row export and generated
`rollback.sql` in durable operator storage. Undo requires its own approval: it
removes only this batch and checks that every imported value is unchanged first.
The rollback file uses COMMIT only when generated with `--commit`; otherwise it too
is a dry run. Code rollback alone does **not** remove historical rows.

Do not use the legacy NFL season importer for this operation: this tool resolves
explicit event identities (including MLB doubleheaders) and enforces chart-only
provenance independently of live collection.

The September 22 batch is already applied; see
[`docs/operations/2026-09-22-odds-recovery.md`](../../docs/operations/2026-09-22-odds-recovery.md).
