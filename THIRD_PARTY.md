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
| `apps/backend/wwwroot/logos/networks/*.svg` | Respective broadcasters and streaming services. Each file is a copy, with editor comments removed, of a Wikimedia Commons file tagged public domain (below the threshold of originality) and trademarked; sources are listed under [Network logos](#network-logos) | Shown next to a game's start time to identify where it airs, not as Nailbiter's brand or a claim of endorsement. Trademark rights remain with their owners; excluded from MIT. |
| `assets/brand/nailbiter.svg` and generated icon copies | Nailbiter Swing identity | Original-code MIT scope does not independently grant artwork/branding rights. |

Team logos are normally loaded externally from ESPN's CDN. External loading is not a license grant, and the maintained offline tests do not need that CDN. The owner retains league marks on a referential-use/fair-use rationale. Trademark nominative use and copyright fair use are context-specific; this notice documents scope and does not certify every downstream use. [Copyright Office guidance](https://www.copyright.gov/fair-use/) and [F1's stated guidelines](https://www.formula1.com/en/information/guidelines.4EOKE9RRqevL4niTK9kWyt) describe distinct considerations.

### Network logos

Networks without an entry here render as text. Add a network only from a file whose source page states a public-domain or free license, and record the source page here.

- `abc.svg` from [ABC-2021-LOGO.svg](https://commons.wikimedia.org/wiki/File:ABC-2021-LOGO.svg)
- `acc-network.svg` from [ACC Network logo 1c black.svg](https://commons.wikimedia.org/wiki/File:ACC_Network_logo_1c_black.svg)
- `apple-tv.svg` from [Apple TV logo.svg](https://commons.wikimedia.org/wiki/File:Apple_TV_logo.svg)
- `btn.svg` from [BTN 2024 (wordmark).svg](https://commons.wikimedia.org/wiki/File:BTN_2024_(wordmark).svg)
- `cbs.svg` from [CBS logo (2020).svg](https://commons.wikimedia.org/wiki/File:CBS_logo_(2020).svg)
- `espn.svg` from [ESPN wordmark.svg](https://commons.wikimedia.org/wiki/File:ESPN_wordmark.svg)
- `espn-plus.svg` from [ESPN Plus.svg](https://commons.wikimedia.org/wiki/File:ESPN_Plus.svg)
- `espn2.svg` from [ESPN2 logo.svg](https://commons.wikimedia.org/wiki/File:ESPN2_logo.svg)
- `fox.svg` from [FOX wordmark-black.svg](https://commons.wikimedia.org/wiki/File:FOX_wordmark-black.svg)
- `fs1.svg` from [2015 Fox Sports 1 logo.svg](https://commons.wikimedia.org/wiki/File:2015_Fox_Sports_1_logo.svg)
- `fs2.svg` from [FS2 logo 2015.svg](https://commons.wikimedia.org/wiki/File:FS2_logo_2015.svg)
- `hbo-max.svg` from [HBO Max May 2025 (Horizontal).svg](https://commons.wikimedia.org/wiki/File:HBO_Max_May_2025_(Horizontal).svg)
- `nbc.svg` from [NBC logo 2022.svg](https://commons.wikimedia.org/wiki/File:NBC_logo_2022.svg)
- `netflix.svg` from [Netflix 2015 logo.svg](https://commons.wikimedia.org/wiki/File:Netflix_2015_logo.svg)
- `paramount-plus.svg` from [Paramount+ logo.svg](https://commons.wikimedia.org/wiki/File:Paramount%2B_logo.svg)
- `peacock.svg` from [NBCUniversal Peacock Logo (2026).svg](https://commons.wikimedia.org/wiki/File:NBCUniversal_Peacock_Logo_(2026).svg)
- `prime-video.svg` from [Prime Video logo (2024).svg](https://commons.wikimedia.org/wiki/File:Prime_Video_logo_(2024).svg)
- `tnt.svg` from [TNT Logo 2016.svg](https://commons.wikimedia.org/wiki/File:TNT_Logo_2016.svg)
- `trutv.svg` from [TruTV 2014.svg](https://commons.wikimedia.org/wiki/File:TruTV_2014.svg)
- `usa-network.svg` from [USA Network logo (2016).svg](https://commons.wikimedia.org/wiki/File:USA_Network_logo_(2016).svg)

## Software notices

- `patches/e2e@0.17.0.patch` modifies the Apache-2.0 TesterArmy package by changing its transport-retry default from five to zero. Preserve the upstream Apache license and TesterArmy NOTICE in `LICENSES/`. `@e2e-dev/web` retains its own Apache-2.0 terms. The local pilot is optional, not a production or CI dependency.
- The web-component bundle contains Chart.js and its transitive dependencies. Their upstream notices apply to the generated bundle even when minification removes banners. Preserve the supplied notice files when redistributing builds.
- NuGet dependencies retain their declared licenses and notices; see the local dependency inventory where available. Restoring or importing a library does not relicense it under the root MIT license.
- Python analysis uses [Psycopg (LGPLv3)](https://github.com/psycopg/psycopg/blob/master/LICENSE.txt), among other separately licensed dependencies. Recheck the resolved distribution and any bundled binary-library obligations before redistribution.
- The database container is a separately obtained [TimescaleDB distribution](https://github.com/timescale/timescaledb/blob/main/LICENSE), including components under the Timescale License. Nailbiter's license is not a license to that entire runtime image.
- Datastar is fetched by the browser from the pinned distribution referenced in `Rendering/Layout.fs`; its own upstream license applies. The application does not claim ownership of that distribution.

Review resolved dependencies and notices on updates. A metadata inventory is evidence of declarations, not a complete legal compatibility opinion. Preserve required license/attribution notices using the comment-policy notice exception if an upstream notice must remain in source.
