module GameDetailRendererTests

open System
open System.Collections.Generic
open AngleSharp.Html.Parser
open Falco.Markup
open Xunit
open backend.Models
open backend.Services
open Backend.Rendering

let private game phase =
    Game(Id = "401891781", Sport = "nhl", Status = "scheduled", SeasonYear = Nullable 2027,
         SeasonType = Nullable phase, ScheduledStart = DateTime(2026, 10, 1, 23, 0, 0, DateTimeKind.Utc),
         AwayTeam = "Buffalo Sabres", AwayTeamAbbreviation = "BUF", HomeTeam = "Columbus Blue Jackets", HomeTeamAbbreviation = "CBJ")

let private rank power odds =
    Some { PowerRank = Some power; PlayoffOdds = odds; PlayoffUpperBound = None; PlayoffFetchedAt = Some(DateTime(2026, 10, 1, 17, 34, 0, DateTimeKind.Utc))
           PlayoffIsFinal = false; PlayoffIsAssumed = false; PlayoffSource = "kalshi"; PlayoffEvidenceKind = backend.Scoring.ProQualificationEvidenceKind.Kalshi; PlayoffSeed = None; DisplayRank = None }

let private components () =
    HypeComponents(StakesAvailable = false, QualityAvailable = true, ClosenessAvailable = true,
                   QualityScore = 59., ClosenessScore = 99., AwayPlayoffOdds = Nullable 0.56,
                   StakesExplanation = "CBJ: Qualification probability unavailable, invalid, or expired; https://example.com/scoreboard?dates=20261001; observed 2026-10-01T17:34:15.2104460Z",
                   QualityExplanation = "moneypuck: #15 / #12; rolling ranking published 2026-09-27T16:49:00Z",
                   ClosenessExplanation = "espn, source season 2027, observed 2026-10-01T17:34:15.2104460Z")

let private renderHtml phase (hype: HypeComponents) =
    GameDetailRenderer.Render (game phase) (List<OddsSnapshot>()) None hype
        (if hype.IsComplete then Nullable 51. else Nullable()) (rank 15 (hype.HomePlayoffOdds |> Option.ofNullable)) (rank 12 (hype.AwayPlayoffOdds |> Option.ofNullable))
    |> List.map XmlNodeRenderer.renderNode |> String.concat ""

let private render phase hype = renderHtml phase hype |> HtmlParser().ParseDocument

let private assertNoDiagnostics (html: string) =
    for forbidden in [ "detail-data"; "Data sources &"; "example.com/scoreboard"; "source season"; "rolling ranking published"; "Qualification probability unavailable"; "17:34:15.2104460Z"; "2026-10-01 17:34 UTC"; "Last fetched" ] do
        Assert.DoesNotContain(forbidden, html)

[<Fact>]
let ``Missing Columbus odds get a concise explanation without diagnostics in HTML`` () =
    use doc = render 2 (components ())
    let status = doc.QuerySelector("#detail-hype-status")
    Assert.NotNull(status)
    Assert.Contains("Hype needs reliable playoff odds for Columbus Blue Jackets.", status.TextContent)
    Assert.DoesNotContain("https://", status.TextContent)
    Assert.DoesNotContain("2026-", status.TextContent)
    Assert.Null(doc.QuerySelector("details#detail-data"))
    assertNoDiagnostics (renderHtml 2 (components ()))
    Assert.DoesNotContain("Kalshi", doc.QuerySelector("#detail-comparison").TextContent)
    Assert.Contains("Quality59", doc.QuerySelector("#detail-hype").TextContent)
    Assert.Contains("Closeness99", doc.QuerySelector("#detail-hype").TextContent)
    let heading = doc.QuerySelector("#detail-comparison").TextContent
    Assert.Contains("Team comparison", heading)
    Assert.True(heading.IndexOf("BUF", StringComparison.Ordinal) < heading.IndexOf("CBJ", StringComparison.Ordinal))

[<Fact>]
let ``Both missing odds name both opponents in scoreboard order`` () =
    let hype = components ()
    hype.AwayPlayoffOdds <- Nullable()
    use doc = render 2 hype
    Assert.Contains("Buffalo Sabres and Columbus Blue Jackets", doc.QuerySelector("#detail-hype-status").TextContent)

[<Theory>]
[<InlineData(2)>]
[<InlineData(3)>]
let ``Missing competition evidence does not always blame qualification odds`` phase =
    let hype = components ()
    if phase = 2 then hype.HomePlayoffOdds <- Nullable 0.48
    use doc = render phase hype
    let status = doc.QuerySelector("#detail-hype-status").TextContent
    Assert.Contains("Stakes need verified competition and playoff context.", status)
    Assert.DoesNotContain("Hype needs reliable playoff odds", status)

[<Fact>]
let ``Other missing metrics receive their own short explanations`` () =
    let hype = components ()
    hype.StakesAvailable <- true
    hype.QualityAvailable <- false
    hype.ClosenessAvailable <- false
    use doc = render 2 hype
    let status = doc.QuerySelector("#detail-hype-status").TextContent
    Assert.Contains("Current team-strength rankings are unavailable.", status)
    Assert.Contains("Current game-winner odds are unavailable.", status)
    Assert.DoesNotContain("playoff odds", status)

[<Fact>]
let ``Complete hype has no missing data notice or diagnostic payload`` () =
    let hype = components ()
    hype.StakesAvailable <- true
    use doc = render 2 hype
    Assert.Null(doc.QuerySelector("#detail-hype-status"))
    Assert.DoesNotContain("Hype unavailable", doc.Body.TextContent)
    assertNoDiagnostics (renderHtml 2 hype)

[<Theory>]
[<InlineData(1, true)>]
[<InlineData(1, false)>]
[<InlineData(2, true)>]
[<InlineData(2, false)>]
[<InlineData(3, true)>]
[<InlineData(3, false)>]
[<InlineData(5, true)>]
[<InlineData(5, false)>]
let ``Diagnostic fields never enter the response even as escaped or hidden payloads`` phase complete =
    let hype = components ()
    hype.StakesAvailable <- complete
    hype.StakesExplanation <- "<script>PRIVATE_STAKES_DIAGNOSTIC</script>"
    hype.QualityExplanation <- "PRIVATE_QUALITY_DIAGNOSTIC https://provider.invalid/internal"
    hype.ClosenessExplanation <- "PRIVATE_CLOSENESS_DIAGNOSTIC 2026-10-01T17:34:15.2104460Z"
    let html = renderHtml phase hype
    for marker in [ "PRIVATE_STAKES_DIAGNOSTIC"; "PRIVATE_QUALITY_DIAGNOSTIC"; "PRIVATE_CLOSENESS_DIAGNOSTIC"; "provider.invalid" ] do
        Assert.DoesNotContain(marker, html)
    assertNoDiagnostics html
    Assert.Contains("PRIVATE_STAKES_DIAGNOSTIC", hype.StakesExplanation)

[<Fact>]
let ``Pro detail explains that hype counts closeness in proportion to quality`` () =
    use doc = render 2 (components ())
    Assert.Contains("Hype counts 59% of Closeness, matching the teams' Quality.", doc.QuerySelector("#detail-hype").TextContent)
    let hype = components ()
    hype.QualityAvailable <- false
    use missing = render 2 hype
    Assert.DoesNotContain("Hype counts", missing.QuerySelector("#detail-hype").TextContent)
