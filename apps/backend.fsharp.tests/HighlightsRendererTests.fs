module HighlightsRendererTests

open System
open System.Collections.Generic
open AngleSharp.Html.Parser
open Falco.Markup
open Xunit
open backend.Models
open backend.Services
open Backend.Rendering

let private game () =
    Game(Id = "highlights-fixture", Sport = "mlb", Status = "final", HomeTeam = "Atlanta Braves",
         AwayTeam = "Los Angeles Dodgers", HomeTeamAbbreviation = "ATL", AwayTeamAbbreviation = "LAD")

let private render (g: Game) snapshots state =
    GameDetailRenderer.RenderWithHighlightsAtTime DateTime.UtcNow g snapshots None (HypeComponents()) (Nullable()) None None state
    |> List.map XmlNodeRenderer.renderNode |> String.concat "" |> HtmlParser().ParseDocument

[<Fact>]
let ``Selected highlights offer escaped text consent and a no JavaScript fallback without remote media`` () =
    let title = "Dodgers & Braves <script>alert('title')</script>"
    use doc = render (game ()) (List<OddsSnapshot>()) (HighlightsDetailState(HighlightsDetailStatus.Playable, "EQMJSt1j-uA", title))
    let section = doc.QuerySelector("#game-highlights")
    Assert.NotNull(section)
    Assert.Equal("Game Highlights", section.QuerySelector("h2").TextContent)
    Assert.Equal(title, section.QuerySelector("#highlights-title").TextContent)
    Assert.Null(section.QuerySelector("script"))
    Assert.Contains("MLB", section.TextContent)
    Assert.Contains("YouTube", section.TextContent)
    let button = section.QuerySelector("button")
    Assert.Equal("Load highlights from YouTube", button.TextContent)
    Assert.Equal("button", button.GetAttribute("type"))
    Assert.Contains("contacts YouTube", section.QuerySelector("#highlights-consent").TextContent)
    Assert.Equal("https://www.youtube.com/t/terms", section.QuerySelector("#highlights-terms").GetAttribute("href"))
    Assert.Equal("https://policies.google.com/privacy", section.QuerySelector("#highlights-privacy").GetAttribute("href"))
    Assert.Equal("Watch on YouTube", section.QuerySelector("#highlights-watch").TextContent)
    Assert.Equal("https://www.youtube.com/watch?v=EQMJSt1j-uA", section.QuerySelector("#highlights-watch").GetAttribute("href"))
    Assert.Null(section.QuerySelector("iframe, img, link, [src]"))
    Assert.Equal("detail-scoreboard", section.PreviousElementSibling.Id)
    Assert.Equal("true", doc.QuerySelector("[data-live-terminal]").GetAttribute("data-live-terminal"))

[<Theory>]
[<InlineData(HighlightsDetailStatus.Pending, "Highlights not available yet")>]
[<InlineData(HighlightsDetailStatus.Unavailable, "Highlights unavailable")>]
let ``Availability states use exact copy without an empty player`` status copy =
    use doc = render (game ()) (List<OddsSnapshot>()) (HighlightsDetailState(status))
    let section = doc.QuerySelector("#game-highlights")
    Assert.NotNull(section)
    Assert.Equal(copy, section.QuerySelector("#highlights-status").TextContent)
    Assert.Null(section.QuerySelector("nb-game-highlights, button, iframe, a"))

[<Theory>]
[<InlineData("bad\" onclick=\"alert(1)")>]
[<InlineData("1234567890/")>]
[<InlineData("1234567890é")>]
[<InlineData("tooShort")>]
[<InlineData("")>]
[<InlineData(null)>]
let ``Malformed selected video identifiers fail closed rather than entering DOM or URLs`` videoId =
    use doc = render (game ()) (List<OddsSnapshot>()) (HighlightsDetailState(HighlightsDetailStatus.Playable, videoId, "A recap"))
    Assert.Equal("Highlights unavailable", doc.QuerySelector("#highlights-status").TextContent)
    Assert.Null(doc.QuerySelector("nb-game-highlights, #highlights-watch, iframe"))

[<Fact>]
let ``Hidden state preserves the existing page without highlights`` () =
    use doc = render (game ()) (List<OddsSnapshot>()) (HighlightsDetailState(HighlightsDetailStatus.Hidden))
    Assert.Null(doc.QuerySelector("#game-highlights"))

[<Fact>]
let ``Selected highlights follow the chart and disclose regional limits without promising a bypass`` () =
    let snapshots = List<OddsSnapshot>()
    snapshots.Add(OddsSnapshot(GameId = "highlights-fixture", Timestamp = DateTime.UtcNow.AddMinutes(-1.), HomeWinProbability = 0.4, AwayWinProbability = 0.6))
    snapshots.Add(OddsSnapshot(GameId = "highlights-fixture", Timestamp = DateTime.UtcNow, HomeWinProbability = 0.5, AwayWinProbability = 0.5))
    use doc = render (game ()) snapshots (HighlightsDetailState(HighlightsDetailStatus.Playable, "EQMJSt1j-uA", "A recap", [| "US"; "CA" |], [| "DE" |]))
    let section = doc.QuerySelector("#game-highlights")
    Assert.NotNull(section.PreviousElementSibling.QuerySelector("nb-win-probability-chart"))
    let disclosure = section.QuerySelector("#highlights-regions").TextContent
    Assert.Contains("US, CA", disclosure)
    Assert.Contains("DE", disclosure)
    Assert.Contains("does not bypass", section.TextContent)
