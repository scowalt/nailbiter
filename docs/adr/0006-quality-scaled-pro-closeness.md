# Scale pro-league Closeness by Quality

On 2026-11-08 the owner said New Orleans at the LA Clippers had "too much hype" because "these teams suck". The game scored Hype 39, the highest of that night's live games. Kalshi priced it 47.5/52.5, so Closeness was 95, and the NBA's 0.30 Closeness weight added about 28.5 points. Quality was about 23 (both teams ranked near 23rd of 30), and Stakes was about 4. Early in an 82-game season the [estimated playoff swing](0004-estimated-playoff-swing-stakes.md) keeps every game's Stakes under 7, so an even line between two weak teams outscored lopsided games between stronger ones. College football fixed the same problem on 2026-11-06 after USF–ECU by scaling Closeness by CFP relevance; pro leagues had no such scaling.

The owner chose to scale Closeness by Quality for NFL, NBA, NHL and MLB:

```text
hype = s × Stakes + q × Quality + c × Closeness × Quality / 100
```

Sport weights `(s, q, c)` are unchanged. An even game between two top teams keeps almost all of its Closeness credit; an even game between two bottom teams keeps little. With the scaling, the New Orleans–Clippers inputs score about 17 instead of 39. The owner preferred Quality over a playoff-relevance multiplier because November playoff probabilities leave most NBA teams between 20% and 60%, which would preserve most of the Closeness credit for weak teams.

The Closeness component stays unscaled. Game-winner sources, expiry and the detail page's Closeness bar are unchanged; the detail page adds a line stating the share of Closeness that counts. The scaling applies in every phase. Regular-season snapshots in every sport move to `FormulaVersion = 5`, so version 3 and the NHL's version 4 are not reused. Postseason and preseason snapshots have no formula version, so a saved snapshot from those phases keeps its earlier total until new evidence replaces it.
