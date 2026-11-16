# MLB scoreboard

An MLB game keeps one scoreboard observation: the latest accepted report of its score, status detail, line score and at-bat situation. Pages read a resolved view of that observation. ESPN ingestion is the only production writer; see [Ingestion from ESPN](#ingestion-from-espn). Rendering (#15) and live updates (#16) build on this contract.

The domain code is in `apps/backend.fsharp/Services/MlbScoreboard.fs`. Read the glossary entries for scoreboard observation, active context, source revision high-water mark and recorded observation first.

## Storage

Migration `045_MlbScoreboard.sql` adds two nullable columns to `games`. The migration is additive and safe to run again.

| Column | Meaning |
| --- | --- |
| `mlb_scoreboard_json` | The last accepted `MlbScoreboardReport`, in camelCase JSON |
| `scoreboard_source_revision_at` | The source revision high-water mark |

An accepted observation also sets `status`, `home_score`, `away_score`, `status_detail`, `period` (the inning) and `scoreboard_updated_at` (the observation time), in the same save. It never changes odds, `market_observed_at`, completion evidence or highlights columns. Scoreboard freshness and odds freshness are independent: a new odds snapshot does not make a scoreboard live again, and a scoreboard does not renew odds.

## Writing an observation

Inject the singleton `MlbScoreboardStore` and call `AcceptAsync(report, status, cancellationToken)`, where `status` is the `games.status` the report sets. It returns `Ok frame` or `Error rejection`. A rejection writes nothing, including the status.

The store creates its own service scope and `ExcitementDbContext` for each call, and it does not accept a caller's context. A caller's tracked `Game` therefore cannot stand in for the locked row, and the caller's pending changes are neither saved nor discarded by an accepted report.

The write runs in one transaction. On PostgreSQL it takes the pro-hype write fence, then the transaction advisory lock `(683, hashtext(game id))`, then the game row with `FOR UPDATE`. It saves once and commits only when the report is accepted. Concurrent writers for one game therefore compare against the same stored observation.

A report is a complete observation, not a patch. Fields it omits are absent in the new observation. For example, a report without bases clears the bases shown from the earlier report.

### Validation

The same validation runs when a report is accepted and each time a stored report is read.

| Rule | Rejection |
| --- | --- |
| The game's sport is `mlb` | `NotMlb` |
| The game exists and `gameId` equals its id | `WrongGame` |
| `competitionId` equals the game id, so one game of a doubleheader cannot overwrite the other | `WrongCompetition` |
| Both reported team ids and both stored team ids are present | `MissingTeamIdentity` |
| Each reported team id equals the stored id for that side | `WrongTeam side` |
| Each side has a line with runs | `MissingScore side` |
| `detail` is not blank | `MissingDetail` |
| `sourceRevisionAt` is not after `observedAt` | `RevisionAfterObservation` |
| Values are possible in baseball | `Impossible field` |
| On acceptance, `status` agrees with `state` (see [Status](#status)) | `Impossible "status"` |

The `Impossible` checks are as follows:

- `provider` is present.
- `provenance` is `live` or `recorded`.
- `state` is one of `scheduled`, `in_progress`, `delayed`, `suspended`, `postponed`, `canceled` or `final`.
- `observedAt` is present.
- The two team ids differ.
- Runs, hits, errors and each inning's runs are zero or more.
- A team's inning runs add up to no more than its runs.
- The home line has no more innings than the away line, and the away line has at most one more.
- The away line is no longer than the current inning.
- The inning is at least 1, and a half (`top` or `bottom`) requires an inning.
- Balls are 0 to 3, strikes 0 to 2 and outs 0 to 3.
- An empty base has no runner, and each reported base says whether it is occupied.

A zero is a value. A missing inning entry is shown as missing, not as zero. A missing inning number is not inning 0.

### Ordering

A valid report must also be in order with what is stored.

| Rule | Rejection |
| --- | --- |
| `observedAt` is not later than the server clock | `FutureObservation` |
| `observedAt` is later than `scoreboard_updated_at` | `RegressingObservation` |
| The provider is the same as the stored report's | `ProviderChanged` |
| `sourceRevisionAt`, when present, is not older than the high-water mark | `RegressingRevision` |

A report with the same revision as the high-water mark and a newer `observedAt` is accepted. A report without a revision keeps the stored high-water mark, so a later report with an older revision is still rejected. Both clocks are compared with columns, so they still apply when the stored report no longer validates, for example after a team id correction. Only the provider check reads the stored report, and it is skipped when that report does not validate.

## Ingestion from ESPN

The ESPN odds cycle for MLB (`EspnOddsService.ScrapeAndStoreOddsAsync "mlb"`) already fetches the general ESPN scoreboard for each date. It makes no other request for the scoreboard. `MlbEspnScoreboard.report` turns each ESPN event into an `MlbScoreboardReport`, and `MlbScoreboardIngestion.AcceptAsync` hands it to the store. Code is in `apps/backend.fsharp/Services/MlbEspnScoreboard.fs`.

- The report uses the event id as the game and competition id, and the competitor ids as team ids. An event id that appears twice on one board is skipped.
- `observedAt` is the cycle's clock time. `sourceRevisionAt` is the response `Date` header.
- `detail` is `status.type.detail`, then `status.detail`. The competition status is read first, then the event status.
- Hits, errors and inning runs that are present must be whole numbers, and inning entries must be in period order. Otherwise the whole event is rejected.
- The report never includes the count, bases or matchup. The general scoreboard does not supply them reliably, so ingestion does not guess the live situation.

For MLB, the cycle writes `status`, `home_score`, `away_score`, `period` and `status_detail` of an existing game only through the store. A game the cycle inserts gets its first status from the table below. The cycle still writes teams, schedule and odds as before. Its sweep still cancels a scheduled game that has left its date's board, which is how ESPN drops an unneeded postseason game. For MLB the sweep calls `MlbScoreboardStore.WithdrawAsync`, which takes the same locks as acceptance. In one save it sets `canceled` and clears the frame, score, inning and `status_detail`, so no frame contradicts the status. It sets `scoreboard_updated_at` to the cycle's time and raises the source revision to the board's `Date` header. A withdrawal is refused if the game is no longer scheduled, or if the board is older than the last accepted frame by either time. A board that returns the game later is accepted as usual. A board observed before the withdrawal cannot bring the old frame back. Withdrawal failures are logged with the same Error templates as acceptance failures. Odds snapshots made in the same cycle copy the accepted score and inning. Other sports are unchanged.

### Status

Ingestion maps ESPN's status to the coarse `games.status` and to the report's `state`, and the store writes both in one transaction. A status therefore never changes without its frame: a rejected or late frame leaves both as they were, so a page cannot show a live status over a final frame or the reverse.

| ESPN status | `games.status` | Report `state` |
| --- | --- | --- |
| `STATUS_SCHEDULED`, `pre` | `scheduled` | `scheduled` |
| `STATUS_IN_PROGRESS`, `in` | `in_progress` | `in_progress` |
| `STATUS_RAIN_DELAY` or `STATUS_DELAYED` | `in_progress`, or `scheduled` when ESPN's state is `pre` | `delayed` |
| `STATUS_SUSPENDED` | `in_progress`, or `scheduled` when ESPN's state is `pre` | `suspended` |
| `STATUS_POSTPONED` | `postponed` | `postponed` |
| `STATUS_CANCELED` | `canceled` | `canceled` |
| `STATUS_FINAL`, `post`, `completed: true`, no interruption text | `final` | `final` |

- A delayed or suspended game keeps `in_progress`, so it stays in the live lists. The report state and `status_detail` tell it apart from play.
- A postponed game is stored as `postponed`, not `final` or `canceled`. A postponement is not a result, and the makeup can come back as the same ESPN event. When ESPN later reports that event as scheduled or in progress, the accepted frame moves the game out of `postponed`. `postponed` is outside the GraphQL `GameStatus` enum, so the cycle publishes no live update for it. Pages label it Postponed and show no score for it.
- `final` needs affirmative evidence. The description, detail and short detail must not mention a postponement, suspension, cancellation, delay or abandonment. A final status with such text is rejected and keeps the game's existing status.
- The competition and event status must agree on name, state and `completed`, and each must have a `type`. Otherwise the report is rejected and the game keeps its existing status (`unknown` for a new game). One status level alone is enough.
- Any other ESPN status is rejected and keeps the existing status. A new game with one gets `scheduled` for `pre`, `in_progress` for `in` and `unknown` otherwise.

### Where acceptance runs

`AcceptAsync` opens its own connection and takes the pro-hype fence, the advisory lock and the row lock. A caller that holds any of these in an open transaction would wait on itself. The cycle therefore accepts reports after it has saved the games and the playoff series, outside every transaction it opens. It records completion evidence after acceptance, so evidence that confirms a completion sees the `final` status the same cycle accepted. Do not call `MlbScoreboardIngestion.AcceptAsync` or `MlbScoreboardStore.AcceptAsync` inside a transaction.

Each acceptance has a deadline of `MlbScoreboardIngestion.Deadline` (5 seconds), linked to the cycle's token. When the deadline passes, the game keeps its stored observation, the cycle logs an Error and goes on to the next game. Cancelling the cycle itself still cancels the cycle.

### Failures

- A failed or unreadable scoreboard response writes nothing, so every game keeps its last accepted score, line score, detail and observation time. The existing ESPN warnings report the failure.
- A rejected event, from `report` or from the store, keeps the stored observation. It is logged at Warning as `Rejected the ESPN MLB scoreboard frame for {GameId}: {Fault}`, once per game, frame content and fault. The record of warned frames for a game is dropped when the store accepts the game's final or canceled frame, so it holds only games that have not ended. A live game whose rejected frame changes every cycle logs one Warning per cycle. A Warning sends no Sentry event.
- An odds-only change does not touch the stored observation, so its observation time and phase do not change. An unchanged frame from a later cycle is accepted with the new observation time, because it is a new observation.

Sentry events per hour, inferred from the 30-second live cycle:

| Fault | Before | After |
| --- | --- | --- |
| Malformed or rejected provider frame | 0 | 0 |
| Missing (404) or malformed scoreboard body | 0 | 0 |
| Stored frame that no longer validates | 0 | 1 per game and observation per process |
| Acceptance deadline | 0 | 1 per game per cycle it stops, so at most 120 per stuck game per hour |

## Reading a game

Call `MlbScoreboardStore.ReadAsync(gameId, cancellationToken)`. It reads the game untracked in its own scope and returns `None` for a game that is not a visible MLB game. Otherwise it returns an `MlbScoreboardView`:

- `Phase`: one of the phases below.
- `Board`: the scores, hits, errors, line score, detail, inning and observation time, or `None` when the phase is `Unavailable`.
- `Active`: the active context, or `None`.
- `Completed`: the result of `MlbCompletionEvidence.IsActuallyCompleted`. A final scoreboard alone does not make a game completed.

| Phase | When |
| --- | --- |
| `Unavailable` | No stored report, unreadable JSON, or a stored report that fails validation |
| `Pregame` | State `scheduled` |
| `Recorded` | State `in_progress` and either `recorded` provenance or a historical import |
| `Live` | State `in_progress`, live provenance, and observation age from 0 up to but not including two minutes |
| `Stale` | State `in_progress` with any other age |
| `Delayed`, `Suspended`, `Postponed`, `Canceled`, `Final` | The matching state |

A stored report that cannot be parsed or no longer validates is our own fault, because every report was validated when it was accepted. The read still fails closed to `Unavailable`. It also logs at Error, with the template `MLB scoreboard stored frame for {GameId} observed at {ObservedAt} is unusable: {Fault}`. `Fault` is `Unreadable` or the validation rejection, such as `WrongTeam Home`. The store logs once per game and `scoreboard_updated_at` for the life of the process, so repeated page and SSE reads of one bad row send one event.

Only the `Live` phase has an active context. Interrupted and ended games keep their scores, line score and detail.

The line score has as many innings as the longer line, with no nine-inning limit. When a final game's home line is one inning shorter than the away line and the home team leads, the board adds a not-played cell for the bottom half that was not batted. An in-progress game with a short home line does not get this cell.

## Tests

`apps/backend.tests/MlbScoreboardTests.cs` writes and reads through the real host and PostgreSQL. Its reports come from the synthetic fixtures in `apps/backend.tests/Fixtures/MlbScoreboard`. `MlbScoreboardIngestionTests.cs` runs the ESPN cycle against synthetic HTTP and checks the API, pages and SSE. `MlbScoreboardSentryTests.cs` counts the events that reach the Sentry transport. `MlbPostponedVisibilityTests.cs` checks every route for a postponed game. `MigrationReplayTests` checks that migration 045 replays and that its columns round-trip through the `Game` model.
