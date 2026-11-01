# MLB highlights application and corrections

The highlights application boundary is `backend.Services.Highlights.HighlightsApplicationService`. Rendering consumes `backend.Models.HighlightsDetailState` from `backend.shared`. `GetDetailAsync` and `InspectAsync` read persisted data only: no ESPN, MLB, or YouTube call is made to compose a detail page. Discovery and revalidation are separate, explicit background operations. A browser playback error is not an application mutation.

## Authoritative identity

The supported evidence bridge requests an ESPN MLB summary by the persisted ESPN event ID, then the MLB-operated Stats API schedule for a bounded UTC start-date ±1-day range. It requires:

- Exactly one ESPN competition with matching event/competition IDs, explicit final/post/completed status, and no contradictory status details. A stored final alone is insufficient.
- Canonical ESPN team IDs and abbreviations for both home and away, consistent with persisted team IDs when present. A fixed canonical ESPN-to-MLB ID mapping is used; display names are never identity evidence.
- An explicit ESPN season matching the persisted MLB season, and an exact event scheduled start matching persistence. The schedule bridge uses **exact start equality**, never nearest start, as additional consistency evidence alongside both canonical MLB team IDs and the season. Zero or multiple exact rows fail closed.
- MLB `gamePk`, `officialDate`, season, doubleheader flag, game number, and explicit final status. A previously assigned gamePk cannot silently change. PostgreSQL serializes assignment and enforces one ESPN event per MLB identity.

The persisted identity includes official date, calendar-year season, both MLB team IDs, exact start, doubleheader/game number, rescheduling/resumption context, same-day contest count, and observation time. A persisted explicit MLB noncompletion rejection remains independent of removal/resume and overrides an ESPN final in application inspection and detail eligibility. The MLB endpoint is a supported MLB-operated evidence source but has no formal schema guarantee claimed here. Missing/malformed/contradictory fields fail closed. Schedule corrections that break the exact bridge can reduce coverage; they never authorize proximity matching.

## Deliberately narrow content support

A playable approved-channel video must have a single-game title in this supported grammar:

```text
AwayNickname vs. HomeNickname [Game ]Highlights (M/D/YY)
AwayNickname vs. HomeNickname [Game ]Highlights (M/D/YYYY)
AwayNickname vs. HomeNickname [Game ]Highlights (YYYY-MM-DD)
```

The optional suffix is ` | MLB Highlights`. Nicknames are pinned canonical MLB identities, not arbitrary display-name comparisons. The title's official date and calendar-year season must match. Publication/upload time never establishes contest identity. Recaps must be 2–30 minutes long. Descriptions containing conflicting dates/years/teams, commentary-only, isolated/single-play, full-game/replay, multi-game/roundup/compilation signals are rejected. This is a conservative format whitelist, not a universal semantic classifier or a guarantee of coverage.

Ordinary automatic selection additionally requires a unique same-day, non-doubleheader, game-number-one contest with no makeup/resumption context. Special contests require a unique matching explicit `gamePk: NUMBER` or `gamePk=NUMBER` in supported video description metadata. Conflicting identifiers or game numbers fail closed. A generic Game 2 is never distinguishing evidence. No claim is made that official uploads routinely provide a gamePk; ambiguous games will normally be manual-only/unavailable. No upload-time inference, scraping, search per game, alternate publisher, or history expansion is implemented.

Manual choice may resolve a doubleheader/makeup contest lacking a gamePk, but still requires the same teams/date/season, narrow single-game content, actual completion, pinned MLB publisher, playable/embeddable availability, and non-child-directed provider evidence. It cannot attach a known-unplayable or wrong-publisher upload. A video already selected for another contest is rejected; an operator must deliberately remove the previous association before moving it. PostgreSQL advisory locks serialize this check across concurrent choices.

## Non-public operator command

Run in an appropriately configured authorized environment:

```sh
dotnet run --project apps/backend -- --highlights inspect GAME_ID
dotnet run --project apps/backend -- --highlights attach GAME_ID VIDEO_ID
dotnet run --project apps/backend -- --highlights replace GAME_ID VIDEO_ID --apply
dotnet run --project apps/backend -- --highlights remove GAME_ID --apply
dotnet run --project apps/backend -- --highlights resume GAME_ID --apply
```

The command exits before starting the HTTP host. It is not a write route or an admin page. Commands are dry runs unless the final argument is `--apply`; JSON output reports accepted/applied, a safe reason, and persisted inspection. Exit code 2 means rejection/invalid usage. `attach` requires no existing selection decision; `replace` requires an existing decision, including manual ownership whose provider payload was already deleted. Inspect is always read-only. Dry runs leave game identity/completion/selection/ownership/suppression/deadline unchanged; validation still consumes API quota and therefore updates durable operational quota accounting.

Applied changes lock the PostgreSQL game row, insert selection state idempotently, and test current ownership/suppression while locked. Automatic work cannot overwrite manual ownership or suppression, even after the video ID/title are deleted. First valid automatic choice is retained. Revalidation checks the selected row's xmin/version after upstream work so an in-flight refresh cannot overwrite a later operator correction. Frozen selection identity is checked against current authoritative identity, so a changed official date cannot make an old wrong-date selection playable. A definitively invalid manual selection remains owned/chosen but unavailable; it is never automatically replaced.

Remove clears provider payload and the selection, then independently persists suppression. Resume only clears suppression; it never renews the original deadline. Manual attach while suppressed retains suppression. Late manual attach is permitted. All identity observations made by this application call completion recording with ordinary-window initialization disabled. Existing ingestion owns normal first-completion windows. A bounded catch-up operation requires the fixed persisted seven-day launch boundary and an unfinished catch-up checkpoint, and never initializes a legacy retry window or imports games. Catch-up may make an initial selection outside the ordinary window; it cannot replace any prior automatic decision after its original deadline, even when provider payload was deleted.

Operator validation reserves three units atomically against the persisted quota/backoff singleton before invoking the shared adapter. Returned unused units are refunded only while both the quota state and current Pacific day equal the original reservation day; cancellation/crash keeps the conservative reservation. Every request after rollover is atomically charged on its new day before sending HTTP, carrying outstanding worker units before allocation. The internal authorization result includes the authorized Pacific day. The adapter rechecks after asynchronous transaction completion/disposal; if stale, it authorizes the new day while keeping the previous charge. Three bounded, cancellable authorization attempts per request prevent time anomalies from causing unbounded work; denial or unstable/backward days cause no HTTP dispatch. Independent operator hosts therefore cannot exceed the shared daily ceiling. No additional operator reservation marker or schema is needed, so crashed commands retain conservative charges without permanently blocking later days. No credentials appear in command output. Worker scheduling, daily revalidation, catch-up checkpoints, durable polling/backoff, and refresh/deletion of provider payload within 30 calendar days belong to the worker lifecycle. Ownership and suppression are not provider payload and survive deletion. Expired provider payload is never presented as playable, even before scheduled deletion executes.

## Verification and limitations

`HighlightsApplicationTests` exercise real entry-point DI and EF/PostgreSQL with fake supported HTTP and controlled time, including identity/content matrices, late operator corrections, dry runs, restart, catch-up without renewed windows, concurrent operations, definitive versus transient validation, and persisted-only detail composition. PostgreSQL fixtures use the reusable rootless, Ryuk-enabled helper.

See the integrated [acceptance matrix](testing/mlb-highlights-acceptance.md) and [local operational instructions](operations/mlb-highlights-local.md) for complementary renderer/browser and worker evidence. This work neither accesses production/live YouTube nor authorizes configuration, migrations, catch-up, account changes, deployment, or rollout. Conservative unavailable results are intentional when supported metadata cannot safely identify a sporting contest.
