module OddsConverterTests

open Xunit
open backend.Services

[<Fact>]
let ``MoneylineToImpliedProbability - favorite (negative odds)`` () =
    let result = OddsConverter.MoneylineToImpliedProbability(-340)
    Assert.InRange(result, 0.7727, 0.7728)

[<Fact>]
let ``MoneylineToImpliedProbability - underdog (positive odds)`` () =
    let result = OddsConverter.MoneylineToImpliedProbability(270)
    Assert.InRange(result, 0.2702, 0.2704)

[<Fact>]
let ``MoneylineToImpliedProbability - even odds`` () =
    let result = OddsConverter.MoneylineToImpliedProbability(100)
    Assert.Equal(0.5, result)

[<Fact>]
let ``MoneylineToImpliedProbability - even odds negative`` () =
    let result = OddsConverter.MoneylineToImpliedProbability(-100)
    Assert.Equal(0.5, result)

[<Fact>]
let ``MoneylineToImpliedProbability - extreme favorite`` () =
    let result = OddsConverter.MoneylineToImpliedProbability(-10000)
    Assert.InRange(result, 0.9900, 0.9902)

[<Fact>]
let ``MoneylineToImpliedProbability - extreme underdog`` () =
    let result = OddsConverter.MoneylineToImpliedProbability(10000)
    Assert.InRange(result, 0.0098, 0.0100)

[<Fact>]
let ``ToNormalizedProbabilities - removes vig and sums to 1`` () =
    let struct (homeProb, awayProb) = OddsConverter.ToNormalizedProbabilities(-340, 270)
    Assert.InRange(homeProb + awayProb, 0.9999, 1.0001)
    Assert.True(homeProb > awayProb)

[<Fact>]
let ``ToNormalizedProbabilities - even matchup`` () =
    let struct (homeProb, awayProb) = OddsConverter.ToNormalizedProbabilities(-110, -110)
    Assert.InRange(homeProb, 0.4999, 0.5001)
    Assert.InRange(awayProb, 0.4999, 0.5001)

[<Fact>]
let ``ToNormalizedThreeWayProbabilities - removes vig and sums to 1`` () =
    let struct (homeProb, drawProb, awayProb) = OddsConverter.ToNormalizedThreeWayProbabilities(-130, 260, 320)
    Assert.InRange(homeProb + drawProb + awayProb, 0.9999, 1.0001)
    Assert.True(homeProb > drawProb)
    Assert.True(drawProb > awayProb)

[<Fact>]
let ``ToNormalizedThreeWayProbabilities - balanced market`` () =
    let struct (homeProb, drawProb, awayProb) = OddsConverter.ToNormalizedThreeWayProbabilities(200, 200, 200)
    Assert.InRange(homeProb, 0.3332, 0.3334)
    Assert.InRange(drawProb, 0.3332, 0.3334)
    Assert.InRange(awayProb, 0.3332, 0.3334)

[<Fact>]
let ``ParseMoneyline - negative odds`` () =
    Assert.Equal(-340, OddsConverter.ParseMoneyline("-340"))

[<Fact>]
let ``ParseMoneyline - positive odds with plus sign`` () =
    Assert.Equal(270, OddsConverter.ParseMoneyline("+270"))

[<Fact>]
let ``ParseMoneyline - positive odds without plus sign`` () =
    Assert.Equal(270, OddsConverter.ParseMoneyline("270"))

[<Fact>]
let ``ParseMoneyline - EVEN returns 100`` () =
    Assert.Equal(100, OddsConverter.ParseMoneyline("EVEN"))
    Assert.Equal(100, OddsConverter.ParseMoneyline("even"))

[<Fact>]
let ``ParseMoneyline - empty or whitespace returns 0`` () =
    Assert.Equal(0, OddsConverter.ParseMoneyline(""))
    Assert.Equal(0, OddsConverter.ParseMoneyline("  "))
    Assert.Equal(0, OddsConverter.ParseMoneyline(null))
