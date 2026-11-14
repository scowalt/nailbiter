# Hype slate tests

`apps/backend.tests/HypeSlateTests.cs` contains slates of upcoming games that the owner has reviewed. Each slate records how the home page should present that day's games. A formula change that breaks a reviewed judgment fails the build. The test does not pin exact scores.

Each slate is a list of ranked groups. Each game in a group has a reviewed home-page section: `HypeHigh` (**Best games**), `HypeMedium` (**Good games**) or `HypeLow` (**More games**). The tests assert two things:

- Each game's hype falls in its reviewed section, as `FormatHelpers.getHypeLevel` decides it for the home page.
- Each game scores higher than every game in a later group. The test does not compare games in the same group.

Pro games are scored through `ProHypePolicy.Calculate` with the `ProHypeTests` fixtures. College games are scored through `CollegeFootballPolicy`.

## Add a reviewed slate

1. Record the displayed hype score for each game, and record the owner's judgment of the order and sections.
2. Construct inputs that reproduce each displayed score under the current formula to within about one point. Use rounded or inferred values for ranks, playoff probabilities, FPI and lines. Do not copy provider responses. [Fixture provenance](../../tests/fixtures/README.md) explains this rule.
3. Name each game by its shape, such as "NHL November, #2 against #7, both safely in", not by its teams.
4. Put games that the owner ranked equally in one group. Order the groups from the highest-ranked to the lowest.
5. Add the slate to `Reviewed`. If the test fails under the current formula, the review requires a formula change. Record that change in an ADR, as [ADR 0010](../adr/0010-stakes-led-nhl-nba-hype.md) does.

```sh
scripts/test-backend csharp --filter FullyQualifiedName~HypeSlate
```
