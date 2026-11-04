# Pro-league hype continuity verification

The [continuity specification](https://github.com/scowalt/nailbiter/issues/17) distinguishes strict current calculation from display of a complete, still-applicable last-known-good estimate. This draft introduces durable capture/resolution; it does not authorize a production migration, backfill, provider activation or release.

## Contract and test boundary

`IProHypeLifecycle` is the shared production boundary for registered background capture and page resolution. Tests use the actual application registration, isolated PostgreSQL, injected clocks and intercepted/denied HTTP. They seed source/game inputs, not the final snapshot when claiming automatic capture. Recreating the application against the same owned database verifies durability.

Apply additive `035_ProHypeLifecycle.sql` after `EnsureCreated` in isolated fixtures and before capture. The EF schema alone does not install the writer-journal/fence triggers. The existing series migration remains `034_PlayoffSeriesObservation.sql`; tests replay both without resetting durable evidence. Do not run these statements against production as part of testing or migration of this PR.

Coverage includes:

- Complete capture before a viewer, an unrelated completed-result gap, age/transport gaps, restart/first HTTP, complete changing recovery and hard invalidation.
- Whole unrounded total/Stakes/Quality/Closeness consistency, current/raw unavailability and required source credit independent of current team information and ordinary game odds.
- Participant/identity/season/phase/schedule/source correction, rejection, revocation, cancellation and concurrent publication; a revoked context cannot resurrect after restart or a later unrelated update.
- Exclusive scheduled/confirmed-start cutoff, seven-day capture horizon and canonical `in_progress` status, including a feed reverting to scheduled with fresh evidence.
- Current MoneyPuck qualification/rank expiry remains strict. Successful validated age-only observations cannot become new eligible evidence; same-publication corrections and explicit rejection still revoke saved support.
- HTTP, existing SSE/page-sync and reload parity across home/league/detail, no viewer-facing retention metadata, preserved ordinary score/odds/excitement and out-of-scope sports.
- Numeric zero versus unavailable, corrupted persistence, additive migration replay and ownership-scoped cleanup. A complete zero total from valid rank inputs is not manufactured merely to satisfy an impossible fixture.

## Independently synthetic inputs

The public generator supplies MoneyPuck fixtures; new test-local schedules, ranks, clocks and game data are authored scenarios. Historical captured values and incident payloads are not inputs. Compatibility IDs in existing helpers do not turn constructed rows into a replay of a real event.

For the generated NSH/OTT probabilities **0.2518 / 0.6804**, ranks 2/1, zero completed games out of 84 and an even game-winner pair, an independent worked oracle gives:

- Midpoint strengths: `100 × 63/64` and `100 × 61/64`; harmonic Quality `96.84979838709677`.
- Urgency: `(exp(3/84) - 1) / (exp(3) - 1)`.
- Stakes: the maximum of each team's `80 × urgency × (1 - abs(2p - 1))` and `40 × urgency`, giving `0.09741877836726988`.
- NHL weighted total: `0.35 × Stakes + 0.35 × Quality + 0.30 × 100 = 63.93152600791242`.

Literal assertions use that independently calculated result, not a value obtained by calling the production scorer. Mutated malformed/missing/nonfinite/corrected cases target these generated values, so the negative paths cannot silently become no-ops against changed fixtures. Tests prove the implemented lifecycle, not historical incident reconstruction, current provider conformance or sustained coverage.

## Constructive NHL seeding regression

The [NHL seeding witness](../nhl-seeding-evidence.md) extends proof coverage, not scoring policy. Generated 32-team/84-game contest controls exercise the real schedule parser/certifier; independent complete-result assignments establish a strict first/second exchange missed by the original bounds. Registered refresh → PostgreSQL → raw scorer → automatic capture → home/league/detail tests verify a literal unrounded total **54.05426437142408**, original clocks/credit across age and restart, cutoff and preserved MoneyPuck admission. A depth-rejected qualification market still prevents a score despite verified seeding. No provider request or captured observation is required, and actual live coverage is not inferred from the synthetic regression.

## NBA seeding with unpublished games

The [NBA bounded proof](../nba-seeding-evidence.md) keeps 80 known contests per team separate from the 82-game denominator. Independent assignments verify all nine missing-slot win-count combinations without adding matchups. Registered BPI/qualification/competition refresh → PostgreSQL → raw scorer → automatic capture → home/league/detail tests assert the independent unrounded total **55.89795865211886**, original BPI source/tuple/clock across age and restart, exclusive cutoff and unchanged NHL provider admission. Cup/ambiguous targets and unusable qualification remain unavailable. Unsafe-source, official Eastern Cup-date, noncounting Cup-stage and all-cases-not-some controls remain separate from progress verification. Passing tests do not establish live-provider conformance or guarantee a future real game's recovery.

## Offline commands

From the repository root with the pinned Mise toolchain and a local test Docker engine:

```sh
python scripts/test-fixtures/generate.py --check
mise exec -- dotnet test apps/backend.tests/backend.tests.csproj -c Release --filter 'FullyQualifiedName~ProHype|FullyQualifiedName~MoneyPuck|FullyQualifiedName~NflPlayoffPageTests|FullyQualifiedName~PlayoffSeries'
mise exec -- dotnet test apps/backend.tests/backend.tests.csproj -c Release
mise exec -- dotnet test apps/backend.fsharp.tests/backend.fsharp.tests.fsproj -c Release
bun install --frozen-lockfile --ignore-scripts
bun run test:live-sync
```

The primary lifecycle/HTTP suites are in-process and require no development listener. Any separate browser-fixture acceptance must use the existing Portless/private-Tailscale lifecycle and clean up only owned resources. No production credentials, paid model or live provider request is part of ordinary validation.

## Fresh migration evidence

The canonical-status regression first failed: a canonical `in_progress` observation reverted to scheduled could produce a new pregame total after fresh evidence. Treating that status as confirmed play made the same regression pass and preserves non-resurrection across application recreation. This is a narrow correction of the accepted cutoff contract, not a scoring-policy change.

On this adapted tree, the focused regression, **479 affected backend cases**, the **full 1,032 backend cases** and **812 F# cases** passed locally, zero skipped. Exact-head Actions outcomes belong to the draft PR's verification record; historical private-branch test counts and review claims are not relabeled as new validation.

Integrating the subsequently merged NFL leverage policy required separate conditional supporting-evidence references, not reinstating the old NFL bubble formula or treating its unconditional outlook as a dependency. The initial combined suites exposed missing conditional fixture inputs and failure to revoke a successfully rejected forecast. Dedicated lifecycle regressions cover absent/withdrawn outlook, conditional expiry with original tuple/scenario/clock retention, and rejected scenarios restored before evaluation or application restart. Synthetic continuity fixtures use a 0.01 conditional swing (Stakes 0.8) rather than the superseded NFL heuristic; literal weighted totals are independently calculated. Non-NFL qualification and legacy-role tests remain separate. The integrated local tree passed 1,068 backend tests, 812 F# tests, Release build and live-sync, with zero skipped cases. This records offline integration evidence, not publisher conformance or deployment.
