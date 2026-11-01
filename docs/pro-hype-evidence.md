# Pregame hype evidence

Hype describes pregame watch-worthiness through independent Stakes, Quality and Closeness components. An unavailable component remains unavailable; no missing input is silently replaced with a neutral score. The shared scorer in `apps/backend.shared/Scoring/ProHypePolicy.cs` and the real importer/page tests are the executable contract.

## Eligibility and source clocks

Require verified sport/season/team/start identity, eligible unstarted game state, consistent competition context and fresh evidence. MLB alone permits a scheduled scoreless first inning. A successful invalid observation revokes eligibility; transport failure preserves previous values and their original clocks. Fetching old publication content does not renew its publication age.

## Last-known-good display

For NFL/NBA/NHL/MLB, `IProHypeLifecycle` separates strict current calculation from a complete saved preview. Permitted age, transport and completed-result coverage gaps neither expire an applicable whole snapshot nor renew its original observations. New complete valid evidence replaces it; changed identity/schedule/phase/status, rejected supporting evidence or revoked source selection prevents reuse. Pregame stops at scheduled kickoff or confirmed play, including canonical `in_progress`, whichever comes first. Without a valid saved snapshot, existing unavailable/partial-component behavior remains unchanged.

Home, league and detail consumers resolve the same whole result and authoritative game identity. Current ordinary odds and team information remain independent. Required derived-score credit follows the evidence that actually produced the saved estimate, not a newer selected source. No stale badge, timestamp, hidden diagnostic payload or new public endpoint is introduced. See the [canonical specification](https://github.com/scowalt/nailbiter/issues/17) and [offline verification](testing/pro-hype-continuity.md).

## Durable lifecycle

The proposed additive `035_ProHypeLifecycle.sql` installs snapshots and evidence-writer journal/fence triggers after the existing series migration 034. It does not populate scores, rewrite input rows or backfill history. Isolated fixtures replay both migrations; `EnsureCreated` alone does not install the journal. Production application remains separately gated.

Registered background capture runs at startup and every 30 seconds against already committed evidence in the existing exclusive seven-day horizon. It makes no provider requests, owns no collection budget and does not change source flags or denial handling. Current numeric calculations still apply all original eligibility rules. Complete snapshots preserve their original component tuple, source provenance and evaluation time across restarts and concurrent work.

## Components

- **Quality:** current-season complete same-source ranks, converted to relative strengths and combined harmonically. NHL uses MoneyPuck rolling ranks, not an unapproved substitute or its forecast values. Rank comes from row order, with explicit publication-season/freshness policy.
- **Closeness:** `100 * (1 - abs(2 * homeProbability - 1))` for an eligible complementary whole pair. Preserve game-winner source preference, executable quote/depth validation and fixed expiry; do not mix qualification probabilities into matchup closeness.
- **Stakes:** phase-specific competition consequences. Regular-season qualification urgency uses verified completed/scheduled counts and explicit playoff probabilities. Unknown seeding may be bounded only when verified terms dominate its entire range; otherwise keep it unknown. Postseason rounds, series progression and play-in variants use verified context, not generic playoff flags or name guesses.

For the separate NFL regular-season proposal, qualification Stakes uses the verified next-game win/loss playoff swing from [conditional leverage](nfl-playoff-leverage.md), scaled by 80 without multiplying game-specific leverage by urgency again. Existing seeding consequences and exact dominance bounds remain; Kalshi's unconditional outlook and trusted final facts stay separate. No other league, phase, weight or source activation is changed by that proposal.

For other regular-season qualification, the implemented urgency is `(exp(3 * (completed + 1) / scheduled) - 1) / (exp(3) - 1)`. The nonfinal qualification term is `80 * urgency * (1 - abs(2 * probability - 1))`. The final/known seeding branches and dominance bounds remain separately explicit in the shared policy. A confirmed qualification outcome is not a market price and does not establish settled seeding/home advantage.

Composite weights differ by sport and remain in the shared policy. This publication does not change weights/calibration or claim predictive accuracy.

## Synthetic coverage

Generated schedule/quote/round inputs exercise full season lengths, contradiction rejection, source freshness, first-inning eligibility, series outcomes, alias isolation, exact unknown-seeding bounds and actual importer-to-page propagation. Independent short synthetic policy cases remain separate. Files are constructed, not recordings of the game IDs used as compatibility identifiers. See [fixture provenance](../tests/fixtures/README.md). NFL conditional-leverage coverage uses an independently constructed 272-contest round robin and hand-authored HTML contract fixtures, not captured pages or a schedule transcribed from them; a separate rational oracle checks the exact synthetic leverage contributions.

Public fixture replacements preserve implementation controls, not the original empirical claims. Historical captures and old evidence documents stay private and are not build/test requirements. New source collection or production data repair needs separate approval.
