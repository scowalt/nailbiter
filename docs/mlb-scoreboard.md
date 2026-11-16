# MLB scoreboard

An MLB game keeps one scoreboard observation: the latest accepted report of its score, status detail, line score and at-bat situation. Pages read a resolved view of that observation. Nothing in production writes observations yet. Ingestion (#14), rendering (#15) and live updates (#16) build on this contract.

The domain code is in `apps/backend.fsharp/Services/MlbScoreboard.fs`. Read the glossary entries for scoreboard observation, active context, source revision high-water mark and recorded observation first.

## Storage

Migration `045_MlbScoreboard.sql` adds two nullable columns to `games`. The migration is additive and safe to run again.

| Column | Meaning |
| --- | --- |
| `mlb_scoreboard_json` | The last accepted `MlbScoreboardReport`, in camelCase JSON |
| `scoreboard_source_revision_at` | The source revision high-water mark |

An accepted observation also sets `home_score`, `away_score`, `status_detail`, `period` (the inning) and `scoreboard_updated_at` (the observation time). It never changes `status`, odds, `market_observed_at`, completion evidence or highlights columns. Scoreboard freshness and odds freshness are independent: a new odds snapshot does not make a scoreboard live again, and a scoreboard does not renew odds.

## Writing an observation

Call `MlbScoreboardStore.AcceptAsync(db, timeProvider, report, cancellationToken)`. It returns `Ok frame` or `Error rejection`. A rejection writes nothing.

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
| `observedAt` is later than the stored observation's | `RegressingObservation` |
| The provider is the same as the stored observation's | `ProviderChanged` |
| `sourceRevisionAt`, when present, is not older than the high-water mark | `RegressingRevision` |

A report with the same revision as the high-water mark and a newer `observedAt` is accepted. A report without a revision keeps the stored high-water mark, so a later report with an older revision is still rejected. A stored report that no longer validates, for example after a team id correction, does not take part in these comparisons.

## Reading a game

Call `GameDataService.GetMlbScoreboardAsync(gameId, cancellationToken)`. It returns `None` for a game that is not a visible MLB game. Otherwise it returns an `MlbScoreboardView`:

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

Only the `Live` phase has an active context. Interrupted and ended games keep their scores, line score and detail.

The line score has as many innings as the longer line, with no nine-inning limit. When a final game's home line is one inning shorter than the away line and the home team leads, the board adds a not-played cell for the bottom half that was not batted. An in-progress game with a short home line does not get this cell.

## Tests

`apps/backend.tests/MlbScoreboardTests.cs` writes and reads through the real host and PostgreSQL. Its reports come from the synthetic fixtures in `apps/backend.tests/Fixtures/MlbScoreboard`. `MigrationReplayTests` checks that migration 045 replays and that its columns round-trip through the `Game` model.
