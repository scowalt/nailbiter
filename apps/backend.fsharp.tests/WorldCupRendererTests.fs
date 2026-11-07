module WorldCupRendererTests

open System
open Xunit
open Falco.Markup
open Backend.Rendering
open backend.GraphQL.Types
open backend.Models
open backend.Services

let private game =
    Game(
        Id = "wc-1",
        Sport = SportExtensions.ToDbValue(Sport.WORLD_CUP),
        HomeTeam = "United States",
        AwayTeam = "Australia",
        HomeTeamAbbreviation = "USA",
        AwayTeamAbbreviation = "AUS",
        ScheduledStart = DateTime(2026, 6, 19, 19, 0, 0, DateTimeKind.Utc),
        Status = "in_progress",
        HomeScore = Nullable<int>(1),
        AwayScore = Nullable<int>(0)
    )

let private completeCard =
    { Game = game
      HomeProbability = Nullable<float>(0.45)
      DrawProbability = Nullable<float>(0.25)
      AwayProbability = Nullable<float>(0.30)
      HomeMoneyline = Nullable<int>(120)
      DrawMoneyline = Nullable<int>(260)
      AwayMoneyline = Nullable<int>(220)
      NailbiterScore = Nullable<float>(82.5)
      GameClock = "20:00"
      Period = Nullable<int>(1)
      Source = "DraftKings"
      OddsTimestamp = Nullable<DateTime>(DateTime(2026, 6, 19, 19, 20, 0, DateTimeKind.Utc)) }

let private render node = XmlNodeRenderer.renderNode node
let private renderNodes nodes = nodes |> List.map XmlNodeRenderer.renderNode |> String.concat ""

[<Fact>]
let ``RenderCardLink points World Cup cards to match page with 3-way probabilities`` () =
    let html = WorldCupRenderer.RenderCardLink completeCard |> render

    Assert.Contains("href=\"/world-cup/match/wc-1\"", html)
    Assert.Contains("🇺🇸", html)
    Assert.Contains("🇦🇺", html)
    Assert.Contains("🇺🇸 USA 45%", html)
    Assert.Contains("Draw 25%", html)
    Assert.Contains("🇦🇺 AUS 30%", html)
    Assert.Contains("Nailbiter", html)
    Assert.Contains("H1 20:00", html)

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``World Cup cards and SSE never display bound representatives as exact probabilities`` overtime =
    let g = Game(Id = "bound", Sport = "world_cup", HomeTeam = "USA", AwayTeam = "AUS",
                 HomeTeamAbbreviation = "USA", AwayTeamAbbreviation = "AUS", Status = "in_progress", IsOvertime = overtime)
    let card = { completeCard with Game = g; HomeProbability = Nullable 0.995;
                                  DrawProbability = (if overtime then Nullable() else Nullable 0.0025);
                                  AwayProbability = Nullable (if overtime then 0.005 else 0.0025);
                                  Source = GameWinnerProbabilityPolicy.OneSidedSource }
    let html = WorldCupRenderer.RenderCardStatic card |> render
    Assert.Contains("USA &gt;99%", html)
    Assert.Contains("AUS &lt;1%", html)
    Assert.Contains("greater than 99 percent", html)
    if not overtime then Assert.Contains("Draw &lt;1%", html)
    let update = GameUpdate(GameId = g.Id, Sport = Sport.WORLD_CUP, HomeTeam = "USA", AwayTeam = "AUS",
                            HomeTeamAbbreviation = "USA", AwayTeamAbbreviation = "AUS", Status = GameStatus.IN_PROGRESS,
                            IsOvertime = overtime, HomeWinProbability = card.HomeProbability,
                            DrawWinProbability = card.DrawProbability, AwayWinProbability = card.AwayProbability,
                            OddsSource = card.Source)
    let fragment = WorldCupRenderer.RenderFragments update
    Assert.Contains("USA &gt;99%", fragment)
    Assert.Contains("AUS &lt;1%", fragment)

[<Fact>]
let ``RenderCardStatic shows no-odds state when draw probability is missing`` () =
    let card =
        { completeCard with
            HomeProbability = Nullable<float>()
            DrawProbability = Nullable<float>()
            AwayProbability = Nullable<float>()
            NailbiterScore = Nullable<float>() }

    let html = WorldCupRenderer.RenderCardStatic card |> render

    Assert.Contains("3-way odds unavailable", html)
    Assert.DoesNotContain("Nailbiter", html)

[<Fact>]
let ``RenderFragments emits draw-aware World Cup SSE fragments`` () =
    let update =
        GameUpdate(
            GameId = "wc-1",
            Sport = Sport.WORLD_CUP,
            HomeTeam = "United States",
            AwayTeam = "Australia",
            HomeTeamAbbreviation = "USA",
            AwayTeamAbbreviation = "AUS",
            HomeScore = Nullable<int>(1),
            AwayScore = Nullable<int>(0),
            Status = GameStatus.IN_PROGRESS,
            HomeWinProbability = Nullable<float>(0.50),
            DrawWinProbability = Nullable<float>(0.30),
            AwayWinProbability = Nullable<float>(0.20),
            ExcitementScore = Nullable<float>(),
            GameClock = "20:00",
            Period = Nullable<int>(1),
            UpdatedAt = DateTime(2026, 6, 19, 19, 20, 0, DateTimeKind.Utc)
        )

    let html = WorldCupRenderer.RenderFragments update

    Assert.Contains("id=\"probbar-wc-1\"", html)
    Assert.Contains("id=\"market-label-wc-1\"", html)
    Assert.Contains("Home / Draw / Away", html)
    Assert.Contains("🇺🇸 USA 50%", html)
    Assert.Contains("Draw 30%", html)
    Assert.Contains("🇦🇺 AUS 20%", html)
    Assert.Contains("H1 20:00", html)

[<Fact>]
let ``RenderMatchPage shows match detail layout with draw-aware odds`` () =
    let snapshots =
        System.Collections.Generic.List<OddsSnapshot>([
            OddsSnapshot(
                GameId = "wc-1",
                Timestamp = DateTime(2026, 6, 19, 19, 10, 0, DateTimeKind.Utc),
                HomeWinProbability = 0.45,
                DrawWinProbability = Nullable<float>(0.25),
                AwayWinProbability = 0.30,
                HomeScore = Nullable<int>(1),
                AwayScore = Nullable<int>(0),
                GameClock = "10:00",
                Period = Nullable<int>(1)
            )
            OddsSnapshot(
                GameId = "wc-1",
                Timestamp = DateTime(2026, 6, 19, 19, 20, 0, DateTimeKind.Utc),
                HomeWinProbability = 0.50,
                DrawWinProbability = Nullable<float>(0.30),
                AwayWinProbability = 0.20,
                HomeScore = Nullable<int>(1),
                AwayScore = Nullable<int>(0),
                GameClock = "20:00",
                Period = Nullable<int>(1)
            )
        ])

    let html = WorldCupRenderer.RenderMatchPage completeCard snapshots |> renderNodes

    Assert.Contains("href=\"/world-cup\"", html)
    Assert.Contains("data-live-terminal=\"false\"", html)
    Assert.DoesNotContain("data-on-load", html)
    Assert.Contains("AUS at USA", html)
    Assert.Contains("id=\"match-scores-wc-1\"", html)
    Assert.Contains("id=\"match-probbar-wc-1\"", html)
    Assert.Contains("🇺🇸 USA 45%", html)
    Assert.Contains("Draw 25%", html)
    Assert.Contains("🇦🇺 AUS 30%", html)
    Assert.Contains("+260", html)
    Assert.Contains("DraftKings", html)
    Assert.Contains("Probability History", html)
    Assert.Contains("nb-win-probability-chart", html)
    Assert.Contains("drawWinProbability", html)

[<Fact>]
let ``RenderMatchFragments emits detail-specific World Cup SSE fragments`` () =
    let update =
        GameUpdate(
            GameId = "wc-1",
            Sport = Sport.WORLD_CUP,
            HomeTeam = "United States",
            AwayTeam = "Australia",
            HomeTeamAbbreviation = "USA",
            AwayTeamAbbreviation = "AUS",
            HomeScore = Nullable<int>(2),
            AwayScore = Nullable<int>(1),
            Status = GameStatus.IN_PROGRESS,
            HomeWinProbability = Nullable<float>(0.55),
            DrawWinProbability = Nullable<float>(0.25),
            AwayWinProbability = Nullable<float>(0.20),
            ExcitementScore = Nullable<float>(),
            GameClock = "72:00",
            Period = Nullable<int>(2),
            UpdatedAt = DateTime(2026, 6, 19, 20, 20, 0, DateTimeKind.Utc)
        )

    let html = WorldCupRenderer.RenderMatchFragments update

    Assert.Contains("id=\"match-scores-wc-1\"", html)
    Assert.Contains("id=\"match-probbar-wc-1\"", html)
    Assert.Contains("id=\"match-market-label-wc-1\"", html)
    Assert.Contains("Home / Draw / Away", html)
    Assert.Contains("id=\"match-clock-wc-1\"", html)
    Assert.Contains("🇺🇸 USA 55%", html)
    Assert.Contains("Draw 25%", html)
    Assert.Contains("🇦🇺 AUS 20%", html)
    Assert.Contains("H2 72:00", html)

[<Fact>]
let ``RenderMatchFragments keeps probability panel informative when match becomes final`` () =
    let update =
        GameUpdate(
            GameId = "wc-1",
            Sport = Sport.WORLD_CUP,
            HomeTeam = "United States",
            AwayTeam = "Australia",
            HomeTeamAbbreviation = "USA",
            AwayTeamAbbreviation = "AUS",
            HomeScore = Nullable<int>(2),
            AwayScore = Nullable<int>(1),
            Status = GameStatus.FINAL,
            HomeWinProbability = Nullable<float>(0.55),
            DrawWinProbability = Nullable<float>(0.25),
            AwayWinProbability = Nullable<float>(0.20),
            ExcitementScore = Nullable<float>(),
            UpdatedAt = DateTime(2026, 6, 19, 21, 0, 0, DateTimeKind.Utc)
        )

    let html = WorldCupRenderer.RenderMatchFragments update

    Assert.Contains("id=\"match-probbar-wc-1\"", html)
    Assert.Contains("Final matches no longer show live probability.", html)

[<Fact>]
let ``RenderCardStatic maps FIFA-specific abbreviations to country flags`` () =
    let fifaGame =
        Game(
            Id = "wc-2",
            Sport = SportExtensions.ToDbValue(Sport.WORLD_CUP),
            HomeTeam = "Germany",
            AwayTeam = "Switzerland",
            HomeTeamAbbreviation = "GER",
            AwayTeamAbbreviation = "SUI",
            ScheduledStart = DateTime(2026, 6, 20, 19, 0, 0, DateTimeKind.Utc),
            Status = "scheduled"
        )

    let html = WorldCupRenderer.RenderCardStatic { completeCard with Game = fifaGame } |> render

    Assert.Contains("🇨🇭", html)
    Assert.Contains("🇩🇪", html)
    Assert.Contains("🇩🇪 GER 45%", html)
    Assert.Contains("🇨🇭 SUI 30%", html)

[<Fact>]
let ``RenderMatchFragments renders two-way advance probabilities without a draw`` () =
    let update =
        GameUpdate(
            GameId = "wc-1",
            Sport = Sport.WORLD_CUP,
            HomeTeam = "Norway",
            AwayTeam = "England",
            HomeTeamAbbreviation = "NOR",
            AwayTeamAbbreviation = "ENG",
            HomeScore = Nullable<int>(1),
            AwayScore = Nullable<int>(2),
            Status = GameStatus.IN_PROGRESS,
            IsOvertime = true,
            HomeWinProbability = Nullable<float>(0.075),
            DrawWinProbability = Nullable<float>(),
            AwayWinProbability = Nullable<float>(0.925),
            ExcitementScore = Nullable<float>(),
            GameClock = "111'",
            Period = Nullable<int>(4),
            UpdatedAt = DateTime(2026, 7, 11, 23, 36, 0, DateTimeKind.Utc)
        )

    let html = WorldCupRenderer.RenderMatchFragments update
    let cardHtml = WorldCupRenderer.RenderFragments update

    Assert.Contains("To advance", html)
    Assert.Contains("id=\"match-market-label-wc-1\"", html)
    Assert.Contains("id=\"market-label-wc-1\"", cardHtml)
    Assert.Contains("To advance", cardHtml)
    Assert.Contains("🇳🇴 NOR 8%", html)
    Assert.Contains("🏴", html)
    Assert.Contains("ENG 92%", html)
    Assert.DoesNotContain("Draw", html)
    for fragment in [html; cardHtml] do
        Assert.Contains("class=\"probability-divider\"", fragment)
        Assert.Contains("left: 7.5%;", fragment)
        Assert.Contains("wc-advance-labels", fragment)

[<Fact>]
let ``RenderMatchFragments hides stale regulation probabilities during overtime`` () =
    let update =
        GameUpdate(
            GameId = "wc-1",
            Sport = Sport.WORLD_CUP,
            HomeTeam = "Norway",
            AwayTeam = "England",
            HomeTeamAbbreviation = "NOR",
            AwayTeamAbbreviation = "ENG",
            HomeScore = Nullable<int>(1),
            AwayScore = Nullable<int>(1),
            Status = GameStatus.IN_PROGRESS,
            IsOvertime = true,
            HomeWinProbability = Nullable<float>(0.22),
            DrawWinProbability = Nullable<float>(0.25),
            AwayWinProbability = Nullable<float>(0.53),
            ExcitementScore = Nullable<float>(),
            GameClock = "91'",
            Period = Nullable<int>(3),
            UpdatedAt = DateTime(2026, 7, 11, 23, 8, 0, DateTimeKind.Utc)
        )

    let html = WorldCupRenderer.RenderMatchFragments update

    Assert.Contains("Advance odds unavailable", html)
    Assert.DoesNotContain("Draw", html)

[<Fact>]
let ``RenderMatchPage labels overtime probabilities as advance odds`` () =
    let overtimeGame =
        Game(
            Id = "wc-et",
            Sport = SportExtensions.ToDbValue(Sport.WORLD_CUP),
            HomeTeam = "Norway",
            AwayTeam = "England",
            HomeTeamAbbreviation = "NOR",
            AwayTeamAbbreviation = "ENG",
            ScheduledStart = DateTime(2026, 7, 11, 21, 0, 0, DateTimeKind.Utc),
            Status = "in_progress",
            IsOvertime = true,
            HomeScore = Nullable<int>(1),
            AwayScore = Nullable<int>(2)
        )

    let card =
        { completeCard with
            Game = overtimeGame
            HomeProbability = Nullable<float>(0.075)
            DrawProbability = Nullable<float>()
            AwayProbability = Nullable<float>(0.925)
            HomeMoneyline = Nullable<int>(1233)
            DrawMoneyline = Nullable<int>()
            AwayMoneyline = Nullable<int>(-1233)
            NailbiterScore = Nullable<float>()
            Source = "Kalshi (to advance)" }

    let html = WorldCupRenderer.RenderMatchPage card (System.Collections.Generic.List<OddsSnapshot>()) |> renderNodes

    Assert.Contains("To advance", html)
    Assert.Contains("Latest live advance odds", html)
    Assert.DoesNotContain("Draw", html)
