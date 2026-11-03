# Opt-in grouped-home fixture

Maintains the [canonical grouped-home contract](https://github.com/scowalt/nailbiter/issues/10). The approved design is carried forward without prototype captures or private issue dependencies.

```sh
bun install --frozen-lockfile
bun tests/browser-fixture/grouped-home-smoke.js
bun tests/browser-fixture/grouped-home-live.js
bun tests/browser-fixture/grouped-home-acceptance.js
```

Uses the real application entry point, renderer, static assets, PostgreSQL and
`HypeScoreService`. No production control route, injected HTML, replacement scoring
service or production formula change. Workers/monitoring are disabled and backend
provider HTTP is denied. No AI/paid journey or live provider request is needed.
See [hosting/setup prerequisites](README.md).

```js
const fixture = await startFixture({ groupedHome: true });
try {
  // Both HTTPS endpoints have already passed portless list + /health checks.
  await page.goto(fixture.manifest.privateUrl);
  await fixture.control("hype group-good-40 70");
  // Wait for the real page-sync snapshot; never replace page DOM yourself.
} finally {
  await fixture.stop(); // owned route, database, manifest directory and IPC only
}
```

`highlights` and `groupedHome` are mutually exclusive. The default NBA fixture and
opt-in MLB highlights fixture are unchanged. Grouped home **replaces** default NBA
seeding, not a supplement. The optional `manifest.groupedHome` contains `now` and
`controlPath`; `listingDateEastern` is `2026-10-13`. Default `intended`/`distraction`
metadata is retained for manifest compatibility but those games are not seeded
in this scenario. Backend time is fixed at **2026-10-13 14:00 UTC (10:00 Eastern)**;
the browser's clock/time zone is not replaced.

## Deterministic mixed schedule

Starts below are hours relative to that controlled UTC time. Insertion order is
deliberately shuffled, including the equal-start low/unavailable pair. Card detail
links remain `/game/<id>`.

| Identity | League / status | Start | Real raw hype |
| --- | --- | --- | --- |
| `group-live-old` | NBA / in_progress | −24 | unavailable |
| `group-live-today` | NBA / in_progress | −1 | unavailable |
| `group-best-nba` | NBA / scheduled, postseason round 1 | +4 | 90.333333333… |
| `group-best-100` | CFB / scheduled, CFP | +5 | 100 |
| `group-best-70` | CFB / scheduled, CFP | +8 | 70 |
| `group-good-6999` | CFB / scheduled, CFP | +6 | 69.99 |
| `group-good-40` | CFB / scheduled, non-CFP bowl | +7 | 40 |
| `group-null-early` | CFB / scheduled | +1 | unavailable |
| `group-low-3999` | CFB / scheduled, non-CFP bowl | +1.5 | 39.99 |
| `group-low-zero` | CFB / scheduled, non-CFP bowl | +2 | 0 |
| `group-null-tie-a` | CFB / scheduled | +2 | unavailable |
| `group-null-nhl` | NHL / scheduled | +2.5 | unavailable |
| `group-null-late` | CFB / scheduled | +3 | unavailable |
| `group-suspended` | MLB / suspended | −3 | unavailable |
| `group-final` | NBA / final | −2 | unavailable |
| `group-postponed` | MLB / postponed | +9 | unavailable |

Hidden controls: `group-hidden-preseason` (NBA, phase 1),
`group-hidden-canceled` (NBA), and `group-hidden-world-cup` (`world_cup`). All have
start +1. Today's live NBA game is returned by both home queries; the previous
Eastern day's live NBA game is outside today's query. Both must appear once.
Live/final NBA games have controlled probability histories for real excitement,
sparklines and scorelines (home 5, away 2). Scheduled games store zero scorelines.

NHL uses independently constructed Ottawa (OTT) 57% Kalshi and Nashville (NSH) 25.18% selected MoneyPuck
model qualification evidence and MoneyPuck #1/#2 ranking evidence. Missing
competition context prevents total hype while Quality and Closeness remain usable.
The NBA postseason matchup also has persisted BOS #1 / NYK #2 seed standings.
NSH-only attribution is a sibling link to `https://moneypuck.com/predictions.htm`,
not nested in the card. The simulated linkage ID is the independent `2099020002`; no historical marker, probability or game response is transcribed. This is synthetic persisted evidence, **not** a provider
import/collection or authorization test; no collection budget is changed.

### Boundary evidence, not returned-score stubs

Only the six CFB boundaries use intentionally extreme synthetic FPI values. Finite
+11000 saturates the existing logistic quality at 100; −11000 paired with zero
produces exactly zero quality. These are test inputs, not plausible team ratings.
Pro rank-midpoint quality cannot reach 0/100, so pro fixtures are not used to fake
those endpoints. CFP stakes are 100 and non-CFP bowl stakes are 0, per the existing
college policy. Existing `Kalshi` game-winner probabilities are respectively
1, .87525, .875, .81275, .8125, .5 for 0, 39.99, 40, 69.99, 70, 100. For example,
non-CFP bowl quality 100 and closeness 25 yield `0 + 35 + 5 = 40`; CFP quality 50
and closeness 37.5 yield `45 + 17.5 + 7.5 = 70`. Production computes the result.
The smoke checks literal independent expected raw scores, not fixture labels.

Unavailable CFB removes the odds observation while retaining FPI/AP rankings,
logos, colors and competition context. `invalid-evidence` stores nonfinite FPI;
the real service rejects it as unavailable quality/total, retaining closeness.
A nonfinite *returned* hype value and an absent dictionary entry cannot be
produced through this service contract; renderer-only coverage is separate.

## Unix controls

Only the local owned Unix socket (mode 0600) receives these newline-terminated
commands. `fixture.control(string)` returns parsed JSON or rejects with a sanitized
exception type. Commands persist disposable evidence and are serialized; existing
page-sync/SSE paths continue unchanged. Mutations do not publish artificial HTML
or introduce polling/transport intervals.

| Command | Effect |
| --- | --- |
| `inspect` | Current games' identities, status/start, real nullable hype and complete/quality/closeness availability; controlled time and failure counters |
| `hype <cfb-id> <score-or-null>` | Change competition/FPI/game-winner evidence; supported scores: 0, 39.99, 40, 69.99, 70, 100, null |
| `invalid-evidence <cfb-id>` | Replace home FPI with NaN; `hype` restores valid inputs |
| `start <id> <ISO-offset-timestamp>` | Change scheduled start and matching odds/competition applicable-start evidence |
| `status <id> <status>` | scheduled, in_progress, final, postponed, suspended, canceled; deterministic score/progress and actual-start state |
| `add <new-group-id> <score-or-null> <ISO-offset-timestamp>` | Add a new CFB matchup using the supported boundary evidence |
| `remove <id>` | Remove game and cascade its disposable observations |
| `scenario <name>` | Atomically reseed the original baseline, then choose a preset below |
| `fail-refresh` | Return HTTP 503 **only** for requests carrying the real live-snapshot header; ordinary navigation/health/SSE keep working |
| `recover` | Restore successful snapshots; browser must catch up itself |
| `scores <id> <home-or-null> <away-or-null>` | Persist nullable/nonzero score evidence without publishing; normal snapshot remains authoritative |
| `publish <id>` | Publish persisted game scalars and real excitement via production `IGameUpdateService` / `ISseBroadcaster`; no artificial HTML |

Presets: `mixed`, `no-best`, `no-good`, `no-more`, `all-unavailable`, `single`
(only `group-best-70`), `live-only`, `other-only`, `empty`. Empty-band presets retain
all unrelated baseline games, including hidden rows. `all-unavailable` retains
scheduled games and removes game-winner odds. Presets reset previous mutations,
not the failure gate; use `recover` explicitly after a failure experiment.

## Smoke and live acceptance scope

The smoke observes real home card identities/links, exact membership, raw service
boundaries, selected credit association, live evidence/start/status/add/remove
updates, availability, presets, refresh failure/retention/recovery, valid excitement
and rejected commands. It explicitly verifies the IPC path disappears, alongside
existing independent owned PostgreSQL/route/manifest cleanup, even on failure.
Both hosting URLs are printed from Portless; no persistent server remains.

The original successful-snapshot reconnect-warning red remains asserted in the
smoke; production recovery clears the warning rather than changing failure simulation.

`grouped-home-live.js` asserts real section/card order through raw hype bands,
availability/start/status/add/remove changes, stable ties and reload, whole-section
creation/removal, failed snapshot retention/recovery, card/credit keyboard focus,
visible anchor, localized times, sparkline/chart state and selected-source linkage.
While server-controlled snapshot failures hold the last successful page, actual
per-game publications must preserve scheduled score omission, ranks/AP/context,
Hype and source credit. A DOM mutation guard catches temporary regressions; positive
live score changes prove the real production client actually applies the fragments.
The same browser exercises actual NBA listing and game-detail fragments, exactly
one active subscription after repeated snapshots, and final-detail cleanup.
Changed snapshots preserve the visible game's viewport offset while reading well
inside a multi-viewport More games section, including earlier insertion/reordering.
With snapshots held failed, scheduled NBA publications preserve postseason seeds
and Hype without transient loss; nullable league scalar targets receive positive
and cleared scores. A scheduled-to-live NBA publication updates Excitement, and
ranked-live NHL publications retain rankings and the selected MoneyPuck credit.
League presentation and its existing score display remain ungrouped and unchanged.

This acceptance allows only the exact version-pinned production Datastar CDN
script off-origin; all other browser off-origin requests are denied. CDN access is
a prerequisite, not a replacement client or mocked transport. It uses unmodified
production CSS/web components, real backend routes and normal snapshot intervals.
`grouped-home-acceptance.js` covers the remaining presentation requirements without repeating the large live suite:

| Acceptance | Observable oracle |
| --- | --- |
| Initial HTML and mixed live DOM | Literal section names, identities, order, detail hrefs, exact-once membership |
| Raw 0 / 39.99 / 40 / 69.99 / 70 / 100 / unavailable | Rounded visible numbers, unavailable dash/accessibility text, raw-band semantic colors, no pregame scores |
| Context and visibility | Hidden preseason/canceled/World Cup absent; previous-day live retained; exact Eastern-day scheduled boundaries; NBA seeds, CFB AP/context and NHL rank/qualification evidence |
| Partial and selected-source evidence | Detail Quality 97 / Closeness 100 with unavailable Stakes/Hype; NSH-only sibling MoneyPuck credit |
| Fixed page policy | Obsolete variant/sort/direction/date parameters, reload and returning home preserve the default; no saved preference or rejected chrome |
| Page states | Each empty band, first available pregame heading, all-unavailable, singleton, live-only, other-only and empty message |
| Keyboard/live accessibility | Entire literal card/credit tab order; usable headings/regions; card focus follows a band move and credit focus follows its reordered game |
| Responsive | 1440×1000, 814×882, 390×844, 320×844 and 720×500 effective 200% reflow; no overflow, full-width headings, row-major DOM/visual order, ≥12px metadata and actual text fitting cards |
| Singleton | Desktop card fills its section, is ≤180px high, teams read horizontally in DOM order |

Optional `GROUPED_HOME_EVIDENCE_DIR` must resolve outside the repository. It saves
synthetic PNGs and a geometry/error JSON report; leave unset for ordinary test runs.
Capture one batched device set and at most one confirming set. No DOM replacement,
score-service stub, helper tests or CSS utility-string assertions are used.

The existing live suite remains the oracle for full transitions, fragments,
failure/recovery, visible anchors, component state, single subscriptions and
actual listing/detail transport. Run it alongside acceptance, not as replaced or
additional distinct backend test counts. Both suites share only test-side schedule
observation and the exact empty-stack native Chromium cross-document rejection classification
isolated during the original implementation, log its occurrence, and fail all other page script errors.
Scenario expectations and independent raw-score oracles remain in each suite.

These are headless Chromium viewport/keyboard checks, not physical-device or
screen-reader certification. Effective zoom uses the halved desktop CSS viewport,
not a native browser zoom control. Team-logo markup/alt/source preservation is
covered, but remote team-logo requests are deliberately blocked; screenshots show
that limitation. No provider collection, paid AI journey or production data is
used. Existing default and highlights fixture behavior remains unchanged.
