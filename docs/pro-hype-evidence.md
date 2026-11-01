# Pregame hype evidence

Hype describes pregame watch-worthiness through independent Stakes, Quality and Closeness components. An unavailable component remains unavailable; no missing input is silently replaced with a neutral score. The shared scorer in `apps/backend.shared/Scoring/ProHypePolicy.cs` and the real importer/page tests are the executable contract.

## Eligibility and source clocks

Require verified sport/season/team/start identity, eligible unstarted game state, consistent competition context and fresh evidence. MLB alone permits a scheduled scoreless first inning. A successful invalid observation revokes eligibility; transport failure preserves previous values and their original clocks. Fetching old publication content does not renew its publication age.

## Components

- **Quality:** current-season complete same-source ranks, converted to relative strengths and combined harmonically. NHL uses MoneyPuck rolling ranks, not an unapproved substitute or its forecast values. Rank comes from row order, with explicit publication-season/freshness policy.
- **Closeness:** `100 * (1 - abs(2 * homeProbability - 1))` for an eligible complementary whole pair. Preserve game-winner source preference, executable quote/depth validation and fixed expiry; do not mix qualification probabilities into matchup closeness.
- **Stakes:** phase-specific competition consequences. Regular-season qualification urgency uses verified completed/scheduled counts and explicit playoff probabilities. Unknown seeding may be bounded only when verified terms dominate its entire range; otherwise keep it unknown. Postseason rounds, series progression and play-in variants use verified context, not generic playoff flags or name guesses.

For regular-season qualification, the implemented urgency is `(exp(3 * (completed + 1) / scheduled) - 1) / (exp(3) - 1)`. The nonfinal qualification term is `80 * urgency * (1 - abs(2 * probability - 1))`. The final/known seeding branches and dominance bounds remain separately explicit in the shared policy. A confirmed qualification outcome is not a market price and does not establish settled seeding/home advantage.

Composite weights differ by sport and remain in the shared policy. This publication does not change weights/calibration or claim predictive accuracy.

## Synthetic coverage

Generated schedule/quote/round inputs exercise full season lengths, contradiction rejection, source freshness, first-inning eligibility, series outcomes, alias isolation, exact unknown-seeding bounds and actual importer-to-page propagation. Independent short synthetic policy cases remain separate. Files are constructed, not recordings of the game IDs used as compatibility identifiers. See [fixture provenance](../tests/fixtures/README.md).

Public fixture replacements preserve implementation controls, not the original empirical claims. Historical captures and old evidence documents stay private and are not build/test requirements. New source collection or production data repair needs separate approval.
