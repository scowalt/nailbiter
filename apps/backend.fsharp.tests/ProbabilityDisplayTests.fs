module ProbabilityDisplayTests

open System
open System.Collections.Generic
open System.Text.Json
open AngleSharp.Html.Parser
open Xunit
open Falco.Markup
open Backend.Rendering
open backend.GraphQL.Types
open backend.Models
open backend.Services

let private game () =
    Game(Id = "cubs-reds", Sport = "mlb", HomeTeam = "Cincinnati Reds", AwayTeam = "Chicago Cubs",
         HomeTeamAbbreviation = "CIN", AwayTeamAbbreviation = "CHC", Status = "in_progress",
         ScheduledStart = DateTime.UtcNow, HomeScore = Nullable 2, AwayScore = Nullable 1)

let private snapshots (g: Game) =
    ResizeArray [
        OddsSnapshot(GameId = g.Id, HomeWinProbability = 0.5, Timestamp = DateTime.UtcNow.AddSeconds(-30.0))
        OddsSnapshot(GameId = g.Id, HomeWinProbability = 0.53, Timestamp = DateTime.UtcNow)
    ]

let private document nodes =
    nodes |> List.map XmlNodeRenderer.renderNode |> String.concat "" |> HtmlParser().ParseDocument

let private detail g =
    GameDetailRenderer.Render g (snapshots g) None (HypeComponents()) (Nullable()) None None |> document

[<Fact>]
let ``detail and listing components expose terminal state`` () =
    let g = game ()
    let liveDetail = detail g
    Assert.Equal("false", liveDetail.QuerySelector("[data-live-terminal]").GetAttribute("data-live-terminal"))
    Assert.Equal("false", liveDetail.QuerySelector("nb-win-probability-chart").GetAttribute("data-live-final"))

    g.Status <- "final"
    let finalDetail = detail g
    Assert.Equal("true", finalDetail.QuerySelector("[data-live-terminal]").GetAttribute("data-live-terminal"))
    Assert.Equal("true", finalDetail.QuerySelector("nb-win-probability-chart").GetAttribute("data-live-final"))

    let games = ResizeArray [g]
    let odds = dict [g.Id, (snapshots g).[1]]
    let scores = dict []
    let ranks = dict []
    let home = HomePageRenderer.Render games odds scores scores ranks |> document
    let sport = SportExtensions.FromDbValue(g.Sport).Value
    let league = LeaguePageRenderer.Render sport (LeagueConfig.getLeagueConfig sport) games odds scores scores None ranks None None None |> document
    Assert.Equal("true", home.QuerySelector("nb-sparkline").GetAttribute("data-live-final"))
    Assert.Equal("true", league.QuerySelector("nb-sparkline").GetAttribute("data-live-final"))

[<Theory>]
[<InlineData("in_progress")>]
[<InlineData("final")>]
let ``detail chart embeds both stored team probability histories`` status =
    let g = game ()
    g.Status <- status
    let history = snapshots g
    history[0].AwayWinProbability <- 0.5
    history[1].AwayWinProbability <- 0.47
    let doc = GameDetailRenderer.Render g history None (HypeComponents()) (Nullable()) None None |> document
    use data = JsonDocument.Parse(doc.QuerySelector("nb-win-probability-chart").GetAttribute("data"))
    let points = data.RootElement.EnumerateArray() |> Seq.toArray
    Assert.Equal(2, points.Length)
    Assert.Equal(0.5, points[0].GetProperty("homeWinProbability").GetDouble())
    Assert.Equal(0.5, points[0].GetProperty("awayWinProbability").GetDouble())
    Assert.Equal(0.53, points[1].GetProperty("homeWinProbability").GetDouble())
    Assert.Equal(0.47, points[1].GetProperty("awayWinProbability").GetDouble())

[<Theory>]
[<InlineData("Kalshi (near-certain orderbook bound)")>]
[<InlineData("Kalshi (resolved game-winner bound)")>]
let ``bounds render consistently and chart retains their provenance`` source =
    let g = game ()
    let history = snapshots g
    history[1].HomeWinProbability <- 0.995
    history[1].AwayWinProbability <- 0.005
    history[1].Source <- source
    let doc = GameDetailRenderer.Render g history None (HypeComponents()) (Nullable()) None None |> document
    Assert.Contains("CIN >99%", doc.Body.TextContent)
    Assert.Contains("CHC <1%", doc.Body.TextContent)
    use data = JsonDocument.Parse(doc.QuerySelector("nb-win-probability-chart").GetAttribute("data"))
    Assert.True(data.RootElement[1].GetProperty("isProbabilityBound").GetBoolean())
    Assert.Equal(source, data.RootElement[1].GetProperty("source").GetString())
    Assert.False(data.RootElement[0].GetProperty("isProbabilityBound").GetBoolean())
    let odds = dict [g.Id, history[1]]
    let home = HomePageRenderer.Render (ResizeArray [g]) odds (dict []) (dict []) (dict []) |> document
    Assert.Contains("CIN >99%", home.Body.TextContent)
    Assert.Contains("CHC <1%", home.Body.TextContent)

[<Fact>]
let ``expired bound disappears from current bar but remains identified in chart history`` () =
    let g = game ()
    let history = snapshots g
    let now = DateTime(2026, 10, 3, 18, 0, 0, DateTimeKind.Utc)
    history[1].Timestamp <- now.AddMinutes(-2.0)
    history[1].HomeWinProbability <- 0.995
    history[1].AwayWinProbability <- 0.005
    history[1].Source <- GameWinnerProbabilityPolicy.OneSidedSource
    let doc = GameDetailRenderer.RenderAtTime now g history None (HypeComponents()) (Nullable()) None None |> document
    Assert.Null(doc.QuerySelector(".probability-track"))
    use data = JsonDocument.Parse(doc.QuerySelector("nb-win-probability-chart").GetAttribute("data"))
    Assert.True(data.RootElement[1].GetProperty("isProbabilityBound").GetBoolean())

[<Fact>]
let ``Cubs at Reds uses blue for Chicago rather than two red segments`` () =
    let doc = detail (game ())
    let chart = doc.QuerySelector("nb-win-probability-chart")
    let away = chart.GetAttribute("away-color").TrimStart('#')
    let red = Convert.ToInt32(away.Substring(0, 2), 16)
    let blue = Convert.ToInt32(away.Substring(4, 2), 16)
    Assert.True(blue > red, $"Expected recognizable Cubs blue, got #{away}")

[<Theory>]
[<InlineData("in_progress", true)>]
[<InlineData("final", false)>]
[<InlineData("canceled", false)>]
let ``college football clocks appear only while live in initial pages and SSE`` status showClock =
    let g = game ()
    g.Sport <- "cfb"
    g.Status <- status
    g.Period <- Nullable 4
    g.GameClock <- "0:00"
    let games = ResizeArray [g]
    let odds = dict []
    let scores = dict []
    let ranks = dict []
    let u = GameUpdate(GameId = g.Id, Sport = Sport.CFB, HomeTeam = g.HomeTeam, AwayTeam = g.AwayTeam,
                       Status = GameStatusExtensions.FromDbValue(status).Value,
                       Period = Nullable 4, GameClock = "0:00",
                       HomeTeamAbbreviation = g.HomeTeamAbbreviation, AwayTeamAbbreviation = g.AwayTeamAbbreviation)
    let home = HomePageRenderer.Render games odds scores scores ranks |> document
    let league = LeaguePageRenderer.Render Sport.CFB (LeagueConfig.getLeagueConfig Sport.CFB) games odds scores scores None ranks None None None |> document
    let initialDetail = detail g
    let cardPatch = HtmlParser().ParseDocument(SseFragmentRenderer.renderGameCardFragments u)
    let detailPatch = HtmlParser().ParseDocument(SseFragmentRenderer.renderDetailFragments u)
    for doc, selector in [home, $"#clock-{g.Id}"; league, $"#clock-{g.Id}";
                          initialDetail, "#detail-clock"; cardPatch, $"#clock-{g.Id}";
                          detailPatch, "#detail-clock"] do
        let clock = doc.QuerySelector(selector)
        Assert.NotNull(clock)
        Assert.Equal(showClock, clock.TextContent.Contains("Q4 0:00"))
        if not showClock then Assert.Null(clock.QuerySelector(".text-status-live"))

let private update (g: Game) =
    GameUpdate(GameId = g.Id, Sport = SportExtensions.FromDbValue(g.Sport).Value,
               HomeTeam = g.HomeTeam, AwayTeam = g.AwayTeam,
               HomeTeamAbbreviation = g.HomeTeamAbbreviation, AwayTeamAbbreviation = g.AwayTeamAbbreviation,
               HomeColor = g.HomeColor, AwayColor = g.AwayColor,
               Status = GameStatus.IN_PROGRESS, HomeWinProbability = Nullable 0.53)

[<Theory>]
[<InlineData("mlb", "CIN", "CHC", null, null)>]
[<InlineData("nba", "CHI", "HOU", null, null)>]
[<InlineData("cfb", "FRES", "RICE", "123456", "123456")>]
[<InlineData("cfb", "FRES", "RICE", "invalid", null)>]
[<InlineData("mlb", "UNKNOWN", "OTHER", null, null)>]
let ``cards detail history pregame and SSE agree on matchup colors`` sport home away homeColor awayColor =
    let g = game ()
    g.Sport <- sport
    g.HomeTeamAbbreviation <- home
    g.AwayTeamAbbreviation <- away
    g.HomeColor <- homeColor
    g.AwayColor <- awayColor
    let colors = TeamColors.forGame g
    Assert.Equal(colors, TeamColors.forUpdate (update g))
    Assert.NotEqual<string>(colors.Home, colors.Away)

    let odds = dict [g.Id, (snapshots g).[1]]
    let scores = dict []
    let ranks = dict []
    let games = ResizeArray [g]
    let sportEnum = SportExtensions.FromDbValue(sport).Value
    let initialDetail = detail g
    let homePage = HomePageRenderer.Render games odds scores scores ranks |> document
    let leaguePage = LeaguePageRenderer.Render sportEnum (LeagueConfig.getLeagueConfig sportEnum) games odds scores scores None ranks None None None |> document
    let cardPatch = HtmlParser().ParseDocument(SseFragmentRenderer.renderGameCardFragments (update g))
    let detailPatch = HtmlParser().ParseDocument(SseFragmentRenderer.renderDetailFragments (update g))
    g.SeasonYear <- Nullable(CollegeFootballPolicy.SeasonAt g.ScheduledStart)
    let pregame = PregameOdds(GameId = g.Id, HomeWinProbability = 0.53, AwayWinProbability = 0.47,
                             Source = "Kalshi", FetchedAt = DateTime.UtcNow, SourceSeason = g.SeasonYear,
                             ApplicableStart = Nullable g.ScheduledStart,
                             HomeTeamAbbreviation = home, AwayTeamAbbreviation = away)
    g.Status <- "scheduled"
    let scheduledDetail = GameDetailRenderer.Render g (ResizeArray()) (Some pregame) (HypeComponents()) (Nullable()) None None |> document

    for doc in [initialDetail; homePage; leaguePage; cardPatch; detailPatch; scheduledDetail] do
        let track = doc.QuerySelector(".probability-track")
        Assert.NotNull(track)
        Assert.Equal($"width: 47%%; background: {colors.Away}", track.QuerySelector(".probability-away").GetAttribute("style"))
        Assert.Equal($"width: 53%%; background: {colors.Home}", track.QuerySelector(".probability-home").GetAttribute("style"))
        Assert.Contains("left: 47%;", track.QuerySelector(".probability-divider").GetAttribute("style"))
        let group = track.ParentElement
        Assert.Contains($"{away} 47%%", group.TextContent)
        Assert.Contains($"{home} 53%%", group.TextContent)
        Assert.Contains("text-text-secondary", group.QuerySelector(".tabular-nums").ClassName)
        Assert.Empty(group.QuerySelectorAll("span[style]"))

    Assert.Equal(homePage.QuerySelector($"#probbar-{g.Id}").InnerHtml, cardPatch.QuerySelector($"#probbar-{g.Id}").InnerHtml)
    Assert.Equal(initialDetail.QuerySelector("#detail-probbar").InnerHtml, detailPatch.QuerySelector("#detail-probbar").InnerHtml)
    let chart = initialDetail.QuerySelector("nb-win-probability-chart")
    Assert.Equal(colors.Home, chart.GetAttribute("home-color"))
    Assert.Equal(colors.Away, chart.GetAttribute("away-color"))
    Assert.Equal(colors.Home, homePage.QuerySelector("nb-sparkline").GetAttribute("color"))
    Assert.Equal(colors.Home, leaguePage.QuerySelector("nb-sparkline").GetAttribute("color"))

[<Theory>]
[<InlineData(0.0, "100%", false)>]
[<InlineData(1.0, "0%", false)>]
[<InlineData(0.5, "50%", true)>]
[<InlineData(0.999, "0.1%", true)>]
[<InlineData(0.001, "99.9%", true)>]
let ``divider overlays exact split without reserving space or inventing a losing segment`` homeProbability awayWidth hasDivider =
    let colors = TeamColors.forGame (game ())
    let doc = [ProbabilityBar.render false "CIN" "CHC" homeProbability (1.0 - homeProbability) colors] |> document
    Assert.Contains($"width: {awayWidth};", doc.QuerySelector(".probability-away").GetAttribute("style"))
    let divider = doc.QuerySelector(".probability-divider")
    Assert.Equal(hasDivider, not (isNull divider))
    if hasDivider then
        Assert.Contains("absolute", divider.ClassName)
        Assert.Contains($"left: {awayWidth};", divider.GetAttribute("style"))
        Assert.Contains("translateX(-50%)", divider.GetAttribute("style"))

[<Fact>]
let ``probability dimensions use CSS decimal points in comma-decimal locales`` () =
    let previous = Globalization.CultureInfo.CurrentCulture
    try
        Globalization.CultureInfo.CurrentCulture <- Globalization.CultureInfo.GetCultureInfo("fr-FR")
        let doc = [ProbabilityBar.render true "CIN" "CHC" 0.537 0.463 (TeamColors.forGame (game ()))] |> document
        Assert.Contains("width: 46.3%;", doc.QuerySelector(".probability-away").GetAttribute("style"))
        Assert.Contains("left: 46.3%;", doc.QuerySelector(".probability-divider").GetAttribute("style"))
    finally
        Globalization.CultureInfo.CurrentCulture <- previous

[<Fact>]
let ``labels escape team names`` () =
    let doc = [ProbabilityBar.render true "<img src=x>" "<script>" 0.53 0.47 (TeamColors.forGame (game ()))] |> document
    Assert.Empty(doc.QuerySelectorAll("img, script"))
    Assert.Contains("<img src=x> 53%", doc.Body.TextContent)

[<Fact>]
let ``probability changes and final status do not change matchup colors`` () =
    let g = game ()
    let colors = TeamColors.forGame g
    let u = update g
    for probability in [0.01; 0.5; 0.99; 1.0] do
        u.HomeWinProbability <- Nullable probability
        Assert.Equal(colors, TeamColors.forUpdate u)
    g.Status <- "final"
    u.Status <- GameStatus.FINAL
    Assert.Equal(colors, TeamColors.forGame g)
    Assert.Equal(colors, TeamColors.forUpdate u)
    Assert.Null((detail g).QuerySelector(".probability-track"))
    let patch = HtmlParser().ParseDocument(SseFragmentRenderer.renderDetailFragments u)
    Assert.Null(patch.QuerySelector(".probability-track"))
