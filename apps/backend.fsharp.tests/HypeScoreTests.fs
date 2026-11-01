module HypeScoreTests

open System
open Xunit
open backend.Services

[<Fact>]
let ``HypeComponents - default values are zero`` () =
    let components = HypeComponents()
    Assert.Equal(0.0, components.StakesScore)
    Assert.Equal(0.0, components.QualityScore)
    Assert.Equal(0.0, components.ClosenessScore)
    Assert.False(components.HomeTeamElo.HasValue)
    Assert.False(components.AwayTeamElo.HasValue)
    Assert.False(components.HomePlayoffOdds.HasValue)
    Assert.False(components.AwayPlayoffOdds.HasValue)

[<Fact>]
let ``HypeComponents - scores can be set and retrieved`` () =
    let components = HypeComponents()
    components.StakesScore <- 75.0
    components.QualityScore <- 80.0
    components.ClosenessScore <- 90.0
    Assert.Equal(75.0, components.StakesScore)
    Assert.Equal(80.0, components.QualityScore)
    Assert.Equal(90.0, components.ClosenessScore)

[<Fact>]
let ``HypeComponents - Elo values can be set as nullable`` () =
    let components = HypeComponents()
    components.HomeTeamElo <- Nullable<float>(1550.0)
    components.AwayTeamElo <- Nullable<float>(1480.0)
    Assert.True(components.HomeTeamElo.HasValue)
    Assert.Equal(1550.0, components.HomeTeamElo.Value)
    Assert.True(components.AwayTeamElo.HasValue)
    Assert.Equal(1480.0, components.AwayTeamElo.Value)

[<Fact>]
let ``HypeComponents - playoff odds can be set as nullable`` () =
    let components = HypeComponents()
    components.HomePlayoffOdds <- Nullable<float>(0.85)
    components.AwayPlayoffOdds <- Nullable<float>(0.42)
    Assert.True(components.HomePlayoffOdds.HasValue)
    Assert.Equal(0.85, components.HomePlayoffOdds.Value)
    Assert.True(components.AwayPlayoffOdds.HasValue)
    Assert.Equal(0.42, components.AwayPlayoffOdds.Value)

[<Fact>]
let ``HypeComponents - scores are clamped by convention to 0-100 range`` () =
    let components = HypeComponents()
    components.StakesScore <- 150.0
    components.QualityScore <- -10.0
    Assert.Equal(150.0, components.StakesScore)
    Assert.Equal(-10.0, components.QualityScore)

[<Fact>]
let ``CalculateThreeWayClosenessScore - balanced market is max score`` () =
    let score = HypeScoreService.CalculateThreeWayClosenessScore(1.0 / 3.0, 1.0 / 3.0, 1.0 / 3.0)
    Assert.InRange(score, 99.9, 100.0)

[<Fact>]
let ``CalculateThreeWayClosenessScore - dominant outcome lowers score`` () =
    let score = HypeScoreService.CalculateThreeWayClosenessScore(0.82, 0.10, 0.08)
    Assert.InRange(score, 26.9, 27.1)
