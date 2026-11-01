# MLB highlights deterministic acceptance matrix

This maps the entire #647 Testing Decisions matrix to the integrated public seams.
It is a coverage audit, not the parent's final two-axis standards/spec review or
production rollout approval. Commands/results and historical failures are recorded
in the external ticket handoff, not rewritten as successes here.

## Seams and good-test boundary

Backend tests use the real `Program`/DI, EF/Npgsql and disposable PostgreSQL, fake
supported upstream HTTP and controlled `TimeProvider`. Tests invoke collection,
application/lifecycle/operator operations and observe inspection, command or actual
game-detail HTTP output. SQL/schema assertions belong to migration/persistence
acceptance; a private matcher is not acceptance proof. Renderer tests cover concise
DOM rules. The browser supplement loads real completed MLB routes and static assets,
never component-only injected markup. No public testing/mutation route was added.

The existing AppFactory, MLB evidence, NFL page, renderer, live-sync and isolated
browser-fixture conventions remain the prior art. Red-before-green evidence for the
pool regression and real-route fixture slice is external. Additional already-safe
cross-feature/browser cases are regression controls, not invented red failures.

## Parent matrix audit

| Parent Testing Decisions | Observable acceptance evidence |
| --- | --- |
| Completion matrix | `HighlightsPersistenceTests.Stored_final_never_substitutes_for_actual_completion` exercises real collection with post/completed=false, postponed/suspended/canceled, scheduled/live, missing flags/competition and contradictions; `Other_sports_ingestion_remains_unchanged_without_MLB_completion_evidence`; application `Explicit_MLB_noncompletion_overrides_stored_and_ESPN_final_for_detail_eligibility` persists explicit MLB rejection through remove/resume/restart. Real HTTP `Detail_HTTP_excludes_ineligible_games_and_game_lists_never_gain_highlights` and browser hidden matrix include absent evidence and NBA. |
| Positive discovery journey | Application `Completed_authoritative_contest_selects_once_and_detail_reads_never_contact_upstream`; lifecycle `Shared_poll_selects_once_at_fifteen_minutes_and_persists_cadence_across_restart`; added persistence `Real_collection_discovery_failure_recovery_and_restart_reach_persisted_detail_without_render_HTTP` chains actual ESPN collection → failed discovery → unaffected collection/score → recovered shared discovery → HTTP after restart. Browser asserts persisted chart/no-chart selections, not manually inserted highlights rows. |
| Identity/content rejection | Application `Unsafe_identity_or_content_never_selects`, `Additional_conflicting_or_stale_evidence_stays_unselected`, `Official_date_not_utc_or_upload_day_drives_matching_and_explicit_gamepk_can_distinguish_doubleheader`, `Narrow_iso_title_is_supported_but_ambiguous_description_never_qualifies`, `Multiple_event_ids_cannot_claim_the_same_authoritative_contest`, `Refreshed_official_date_cannot_make_an_old_selection_for_another_date_playable`; adapter pinned-channel/MFK/playability tests. Includes wrong teams/date/previous season, delayed uploads, UTC/date boundary, doubleheader/makeup/resumption, generic Game 2, explicit conflicting IDs/numbers, missing authoritative fields, nearest-start rejection, unsuitable content. |
| Time/restart | Persistence `Completion_and_original_deadline_survive_refresh_contradiction_and_host_restart` and ordered observations; application late corrections/restart, original-window recovery; lifecycle shared cadence, daily validation, real hosted controlled timer/shutdown, interrupted validation/restart and exact 30-day expiry. No real multi-day sleeps. |
| Catch-up bounds | Application `Launch_catchup_has_a_separate_fixed_boundary_and_never_initializes_legacy_retry_window`; lifecycle launch snapshot/resumption, just-inside/outside/future/non-MLB/live, cancellation-before-first-upload, pagination cap and strict remaining page budget. Only existing fixed cohort; already-published initial recap after expired ordinary deadline; no import, renewed window or repeat launch. Final-review regressions `Launch_cohort_cannot_replace_a_daily_invalidated_automatic_decision_at_original_deadline`, `Launch_cohort_preserves_expired_decisions_after_provider_payload_deletion` and `Launch_cohort_can_replace_invalid_automatic_choice_inside_original_window` drive the actual lifecycle and detail HTTP, including original-deadline/manual/suppression guards after retention deletion. |
| Ownership/operator | Application `Operator_dry_runs_late_attach_remove_resume_and_restart_preserve_decisions_and_deadline`, `Attach_wont_overwrite_any_existing_decision_replace_is_explicit_even_after_payload_deletion`, `Manual_game_choice_cannot_bypass_safety`, `Manual_game_choice_resolves_ambiguous_contest_without_relaxing_provider_or_content`, `Operator_cli_is_nonpublic_dry_run_by_default_and_requires_explicit_apply`, `One_manual_candidate_cannot_remain_chosen_for_two_distinct_doubleheader_contests`; lifecycle manual/quota preservation. |
| Concurrent/repeated operations | Application concurrent discovery, in-flight automatic vs operator/manual/suppression, stale revalidation vs manual correction; persistence atomic predicates/xmin; lifecycle Busy/cancellation recovery and Pacific rollover vs concurrent operator. Assert resulting ownership/selection/deadline, not locking implementation order. |
| Availability/regions | Adapter removed/private/non-embeddable/unprocessed/globally blocked/unapproved/MFK true/unknown matrix, partial regions and full ISO-country blocking (not merely preferred locales); application definitive invalidity/manual preservation/original-window recovery; lifecycle daily validation/transient preservation. Browser region disclosure, native local iframe error, unchanged inspected choice and real reload fallback. No claim that cross-origin player errors can be introspected or that fallback bypasses restrictions. |
| Failures/config/quota | Adapter missing key/disabled/invalid config, malformed/oversized/gapped/repeated pagination, timeout/stream/cancellation, bounded 200-ID batching, lower allocation/reset/Retry-After/quota; lifecycle durable bounds/backoff/credential rejection, cancellation reservations, daily retry spacing, large shared batches and operator no-double-charge; application stale/conflicting detail and interrupted authoritative response. Final-review `Independent_operator_hosts_cannot_overrun_daily_ceiling_across_Pacific_midnight` recreates the five-new-day-request/three-unit overrun using two real process-equivalent hosts/clients and also checks the 1000 ceiling. `Quota_commit_rollover_cannot_dispatch_a_channel_after_independent_host_spends_current_day_budget` advances time during real EF/PostgreSQL commit completion, before the first channel dispatch; an independent host spends the current-day ceiling (including 1000 actual fake-HTTP requests), and the stale authorization sends nothing. Capacity, continuously-changing-day backoff and commit-time cancellation/restart controls use the same external transaction boundary. Interrupted operators retain original/new-day charges through restart without blocking future days; rollover requests coexist with worker reservations and cannot refund a different day. Configuration-policy agreement is checked at application/operator/worker/adapter/detail boundaries; operator/worker rejection transitions persist through transient failures/restarts and clear only on success. Optional storage failure preserves scoreboard HTTP. Added chained journey proves ordinary collection succeeds during discovery outage. |
| Provider data freshness | Lifecycle disabled retention, metadata-only legacy deletion and exact 30-day prolonged-outage boundary cover ID/title/channel/evidence/MFK/regions/playlist/cursor removal without losing ownership/suppression/deadlines/cohort. Detail reads never offer stale payload before deletion. |
| Privacy/player contract | `tests/browser-fixture/highlights-acceptance.js` records all requests and intercepts every third party; no YouTube/Google/thumbnail before explicit interaction, no iframe/preconnect. Named native button with consent description and actual Enter/pointer activation yields nocookie `autoplay=0&controls=1`, title, native permissions/fullscreen and strict-origin referrer; minimum 200×200 measured at 320/390px. Attribution/terms/privacy/regions precede load. No-JS actual direct-link navigation is locally intercepted. Fake credential absent from HTML/responses/local and loaded assets/browser URLs/manifest/full host logs including shutdown. Adapter tests also capture sanitized logger failures. |
| Presentation/compatibility | Renderer selected/escaped/safety/status/order tests; actual `Detail_HTTP*`; browser asserts scoreboard scores, chart data, correct sibling placement with/without chart, exact pending/unavailable contexts, no empty player, all hidden states, no list highlights and unchanged NBA details. Existing `tests/live-sync/browser-regression.js` preserves active synchronization, chart identity/lines/bounds, races, listing discovery and localized times. |
| Terminal pages/reload | Actual browser pending final page stays open while owned local IPC runs real lifecycle discovery with fake upstreams and controlled backend time. Advancing 20 browser minutes proves no EventSource, snapshot, highlights poll or request/reopened sync; inspected persisted selection becomes visible only after `page.reload()`, not DOM injection. Rendering does not increment upstream calls. Existing live-sync initial-final and final-transition regressions remain independent controls. |
| Schema validation | `HighlightsPersistenceTests.Forward_migration_preserves_legacy_games_and_reapplies_without_resetting_durable_state` removes feature tables/columns from representative existing final/live/scheduled MLB and final NFL data, executes actual **030 + 031 SQL**, checks legacy data/unknown eligibility, single-selection/singleton/quota constraints, preserved operator/launch state after reapplication and no fresh legacy window. `HighlightsLifecycleTests.Additive_sql_preserves_cohort_progress_and_quota_on_reapplication` executes actual 031, including backfilled provider-rejection decision, preserved cohort/quota/reservation and SQL bounds. This is not EnsureCreated-only migration proof. |
| Host safety/delivery | Added `IsolatedPostgreSqlTests.Real_host_initializes_second_database_after_owned_OS_port_reuse_with_normal_pooling` reuses one OS-selected owned port across two sequential actual hosts/containers; unique per-container DB/password fixes Npgsql alias without disabling pooling/Ryuk/rootless allocation or retrying/skipping cleanup. Browser verifies both reported Portless/private TLS URLs and finally removes only owned route/container/manifest/Unix IPC. NBA setup and post-start failure smoke remain compatible. No production key/DB/launch/migration/deployment, further agent or paid evaluation. Parent owns draft integration PR and final review/fixes. |

## Test locations

- [Persistence, collection and migration acceptance](../../apps/backend.tests/HighlightsPersistenceTests.cs)
- [Application/identity/operator/detail HTTP acceptance](../../apps/backend.tests/HighlightsApplicationTests.cs)
- [Supported YouTube adapter and credential hygiene](../../apps/backend.tests/YouTubeHighlightsTests.cs)
- [Lifecycle/cohort/quota/freshness acceptance](../../apps/backend.tests/HighlightsLifecycleTests.cs)
- [Deterministic real-host pool regression](../../apps/backend.tests/IsolatedPostgreSqlTests.cs)
- [Concise renderer rules](../../apps/backend.fsharp.tests/HighlightsRendererTests.fs)
- [Actual-route browser supplement](../../tests/browser-fixture/highlights-acceptance.js)
- [Existing live-sync browser regression](../../tests/live-sync/browser-regression.js)

## Focused migration/pool checks

Use the pinned environment in [local operational instructions](../operations/mlb-highlights-local.md):

```sh
dotnet test apps/backend.tests --filter FullyQualifiedName~IsolatedPostgreSqlTests
dotnet test apps/backend.tests --filter 'FullyQualifiedName~Forward_migration|FullyQualifiedName~Additive_sql_preserves'
```

Full suites/builds, browser acceptance and setup/failure smoke must still run. An
isolated recheck does not erase a prior failed full suite. The deterministic pool
red reproduced the earlier EOF cause; post-fix full-suite success is new evidence,
not a retrospective success claim for #650/#651/#652 failures.

## Bounded conclusions

Synthetic metadata establishes fail-closed behavior and application/browser
contracts, not universal publisher coverage, production access/quota, legal
certification or real playback. Unsupported/ambiguous provider evidence must remain
unselected. Real YouTube/Google is intercepted and never accessed by this supplement.
Private iframe error content is simulated locally; a DOM `error` event tests the
component's local fallback handler without mutating server selection. No browser
failure is authoritative provider deletion evidence. Final two-axis review and any
fixes belong to the parent integration stage.
