# NHL power-ranking source assessment

## Scope

On 2026-11-03 the owner first selected [IcyData's Reddit rankings visualization](https://icydata.hockey/visualizations#viz=reddit_rankings&version=1), then directed use of [Power Rankings Guru's NHL power rankings](https://powerrankingsguru.com/nhl/power-rankings). The latest source choice is Power Rankings Guru, subject to its applicable access/reuse terms and a verified feed contract. This concerns team strength for **Quality**, not playoff qualification probability, Stakes or game-winner odds. Source choice does not authorize an access-challenge bypass or fabricated input. See the [glossary](../GLOSSARY.md) and [hype evidence contract](pro-hype-evidence.md).

A local Power Rankings Guru integration now implements the limited single-page collection and Quality fallback described in the [collector contract](nhl-guru-rankings.md). No production operation has been performed. MoneyPuck's admission state, shared daily budget and refusal backoff are unchanged.

## Earlier IcyData observations

- A single ordinary retrieval of the supplied first-party URL on 2026-11-03 returned a managed Cloudflare challenge, not ranking content. Requests to that provider stopped; no challenge-solving, proxy, browser-identity workaround or guessed backend endpoint was attempted. This observation does **not** establish that all clients or the production backend are denied.
- Consequently, this investigation did not verify current ranking values, current-season coverage, the number of teams, publication timestamps, methodology, attribution requirements or a machine-readable schema from that page. The fragment name alone does not establish any of those facts.
- Documentation discovery did not identify a verifiable first-party API specification or published reuse terms. That is an unresolved evidence gap, not proof that no API or permission exists. Search summaries and unrelated third-party APIs are not substitutes for publisher documentation.
- Follow-up documentation discovery, including a GitHub repository-name search, did not establish an official IcyData feed or source repository. An attempted first-party community methodology reference on Reddit also returned HTTP 403; requests there stopped without changing fetch routing, using mirrors or enabling remote-reader fallbacks. No ranking values or licensing conclusions are inferred from search summaries.
- No provider response, captured ranking or access-challenge payload is included in this repository or its fixtures.

## Power Rankings Guru observations

- An ordinary retrieval of the [requested page](https://powerrankingsguru.com/nhl/power-rankings) on 2026-11-03 returned readable server-rendered HTML. It identifies “2026-27 NHL Power Rankings,” says “All 32 teams ranked,” includes a 32-item structured ranking list, and displays “Updated Nov 3, 2026, 3:31 AM ET.” Its description advertises daily updates. This establishes a visible season/publication contract, not independent verification of every team, recurring backend access or sustained coverage.
- The first-party [Terms of Use](https://powerrankingsguru.com/terms), effective October 20, 2026, section 6 permits personal, non-commercial use and says, without written permission, not to “scrape, crawl, or use automated tools to bulk-download content”; it separately discusses bulk republication, commercial redistribution and competing products. Short attributed quotations are separately allowed. The owner explicitly clarified that the intended use is one page, not bulk downloading, list republication or a competing rankings product, and directed proceeding on that interpretation. This records operator authorization for the limited implementation, not express publisher permission or a settled legal interpretation.
- Section 7 subjects the site's own data endpoints to the same terms and rate limits. No endpoint probing, account creation, permission outreach or collection workaround was attempted; the implementation fetches only the requested page.
- Before this change, `PowerRankingsGuruService.fs` used the older NHL `/nhl/team-power-rankings.php` route, the scheduled Guru refresh omitted NHL, and the NHL Quality selector accepted MoneyPuck only. The local integration replaces the NHL entrypoint with the single-page collector and adds a same-source fallback behind MoneyPuck. Other sports' Guru paths and qualification sources are unchanged.
- No provider HTML, observed ranking values or artwork were added to public fixtures. No production operation or MoneyPuck admission change was made.

## Contract and remaining live verification

1. The owner's authorization is narrowly implemented as one page per admitted daily attempt, with attribution and no ranking-list republication. No paid trial, new account, permission outreach or alternate access path is assumed. Applicable publisher terms remain relevant; this is not a blanket data licence.
2. The parser verifies the explicit season heading, original Eastern publication clock and full 32-team canonical set. Nailbiter identifies 2026–27 as season **2027**. Missing/duplicate/unknown teams, inconsistent ranks/seasons and ambiguous dates fail closed.
3. The page advertises daily updates. The collector uses a persisted 24-hour admission interval; current Guru-based Quality requires both publication and collection age strictly under 48 hours. Fetching unchanged content does not renew its publication age.
4. Offline collector/scorer/lifecycle/page tests verify the implemented contract using independently generated synthetic HTML, controlled HTTP/clocks and disposable PostgreSQL. They do not prove recurring production access or sustained coverage. That verification remains part of a separately authorized reviewed rollout, without forced refreshes or denial bypasses.

## Earlier IcyData integration proposal (not implemented)

- Store IcyData ranking provenance independently of MoneyPuck, with verified season and original publication/retrieval clocks. Do not relabel captured observations or overwrite MoneyPuck rows.
- Prefer an eligible complete MoneyPuck ranking pair, then an independently eligible complete IcyData pair. Never mix one team's rank from each provider or average sources to manufacture Quality.
- Keep the existing rank-to-strength and harmonic Quality formula. Treat any community-consensus methodology honestly; it is not a playoff probability or a confirmed qualification outcome.
- Preserve current-versus-retained hype semantics, supporting-source invalidation and the exclusive pregame cutoff. An unavailable ranking source cannot create a retrospective snapshot.
- Add deterministic synthetic parser, season/team/publication, malformed-batch, priority, provenance, persistence and page regressions. A source schema must be established before inventing fixtures for it.

This earlier IcyData proposal remains unimplemented and is not the current source choice. No further requests to its challenged visualization were made.

## Disposition

**Power Rankings Guru selected; limited integration implemented locally under the owner's clarified direction.** The earlier characterization of written permission as a settled prerequisite was overstated and is superseded by that clarification. The implementation remains subject to review and normal release controls, not manual activation or production data repair. IcyData's separate access/schema gaps remain unresolved. No sustained-coverage or deployment claim follows from the source inspection or synthetic tests.
