# License scope and third-party material

[MIT](LICENSE) covers Nailbiter's original code, original documentation and independently authored synthetic test fixtures. Third-party components retain their own terms. MIT does not grant rights in third-party artwork, provider content, trademarks or endorsement. The Nailbiter name and Swing identity are not an invitation to imply an official or endorsed service; the code-license selection does not separately license the artwork.

## Fixtures and compatibility references

All maintained provider-format fixtures are independently constructed by `scripts/test-fixtures/generate.py`. They are test controls, not saved responses or claims about actual games, prices, provider availability or current schemas. Protocol field names, team codes, ticker syntax and the fixed contract-validation language in the parser/tests are compatibility references, not a license to provider feeds or contract content. URLs in mock handlers identify routes; tests do not retrieve those URLs. See [synthetic fixture provenance](tests/fixtures/README.md).

Live access/use remains subject to applicable provider terms and operator permissions. Installing this software does not grant a data license or authorize bypassing access controls. Existing provider-specific activation/rate/attribution restrictions remain in force.

## League and template artwork

| Paths | Ownership/provenance | Treatment |
|---|---|---|
| `apps/backend/wwwroot/logos/{nba,nfl,nhl,mlb}.png` | Respective leagues; introduced locally when replacing ESPN CDN references | Retained to identify the referenced leagues, not as Nailbiter's brand. Third-party ownership; excluded from MIT. |
| `apps/backend/wwwroot/logos/{f1,indycar}.png` | Referenced series/organizations; exact download provenance and express license not established in source records | Same referential-use treatment; no claim of a blanket redistribution license or endorsement. |
| `apps/backend/wwwroot/logos/nascar.png` | Generic flag artwork; exact upstream artwork provenance unresolved | Excluded from MIT pending provenance clarification; filename is not proof of NASCAR ownership. |
| `apps/backend/wwwroot/logos/{cfb,world-cup}.svg` | Geometric sport icons introduced in application source; not the official competition emblems | Preserve source provenance; do not claim official affiliation. |
| `apps/www/public/vite.svg` | Matches the [Vite v6.3.5 template](https://github.com/vitejs/vite/blob/v6.3.5/packages/create-vite/template-vanilla/public/vite.svg) in complete XML structure/attributes, differing only in formatting | Upstream MIT notice retained at `LICENSES/assets/vite/LICENSE-v6.3.5.txt`; not original Nailbiter artwork or a trademark/endorsement grant. Unused by the current marketing page. |
| `assets/brand/nailbiter.svg` and generated icon copies | Nailbiter Swing identity | Original-code MIT scope does not independently grant artwork/branding rights. |

Team logos are normally loaded externally from ESPN's CDN. External loading is not a license grant, and the maintained offline tests do not need that CDN. The owner retains league marks on a referential-use/fair-use rationale. Trademark nominative use and copyright fair use are context-specific; this notice documents scope and does not certify every downstream use. [Copyright Office guidance](https://www.copyright.gov/fair-use/) and [F1's stated guidelines](https://www.formula1.com/en/information/guidelines.4EOKE9RRqevL4niTK9kWyt) describe distinct considerations.

## Software notices

- `patches/e2e@0.17.0.patch` modifies the Apache-2.0 TesterArmy package by changing its transport-retry default from five to zero. Preserve the upstream Apache license and TesterArmy NOTICE in `LICENSES/`. `@e2e-dev/web` retains its own Apache-2.0 terms. The local pilot is optional, not a production or CI dependency.
- The web-component bundle contains Chart.js and its transitive dependencies. Their upstream notices apply to the generated bundle even when minification removes banners. Preserve the supplied notice files when redistributing builds.
- NuGet dependencies retain their declared licenses and notices; see the local dependency inventory where available. Restoring or importing a library does not relicense it under the root MIT license.
- Python analysis uses [Psycopg (LGPLv3)](https://github.com/psycopg/psycopg/blob/master/LICENSE.txt), among other separately licensed dependencies. Recheck the resolved distribution and any bundled binary-library obligations before redistribution.
- The database container is a separately obtained [TimescaleDB distribution](https://github.com/timescale/timescaledb/blob/main/LICENSE), including components under the Timescale License. Nailbiter's license is not a license to that entire runtime image.
- Datastar is fetched by the browser from the pinned distribution referenced in `Rendering/Layout.fs`; its own upstream license applies. The application does not claim ownership of that distribution.

Review resolved dependencies and notices on updates. A metadata inventory is evidence of declarations, not a complete legal compatibility opinion. Preserve required license/attribution notices using the comment-policy notice exception if an upstream notice must remain in source.
