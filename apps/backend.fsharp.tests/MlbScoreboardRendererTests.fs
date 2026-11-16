module MlbScoreboardRendererTests

open System
open System.Collections.Generic
open AngleSharp.Dom
open AngleSharp.Html.Parser
open Falco.Markup
open Xunit
open Backend.Rendering
open backend.GraphQL.Types
open backend.Models
open backend.Services

let private now = DateTime(2026, 7, 4, 23, 30, 0, DateTimeKind.Utc)

let private game status =
    Game(Id = "mlb-synthetic-1", Sport = "mlb", Status = status, ScheduledStart = now.AddHours(-1.0),
         AwayTeam = "Chicago Cubs", AwayTeamAbbreviation = "CHC", HomeTeam = "Cincinnati Reds", HomeTeamAbbreviation = "CIN",
         AwayScore = Nullable 1, HomeScore = Nullable 2)

let private snapshot (probability: float) (minutesAgo: float) =
    OddsSnapshot(GameId = "mlb-synthetic-1", HomeWinProbability = probability, AwayWinProbability = 1.0 - probability,
                 Timestamp = now.AddMinutes(-minutesAgo))

let private runs (innings: int list) = innings |> List.map MlbLineCell.Runs

let private team total (line: MlbLineCell list) : MlbTeamScore =
    { Runs = total; Hits = Some 7; Errors = Some 1; Line = line }

let private board innings (away: MlbTeamScore) (home: MlbTeamScore) : MlbScoreboardBoard =
    { Away = away; Home = home; Innings = innings; Detail = "Top 5th"; Inning = Some innings; ObservedAt = now.AddSeconds(-20.0) }

let private fiveInnings =
    board 5 (team 4 (runs [ 0; 2; 0; 1; 1 ])) (team 3 (runs [ 1; 0; 2; 0 ]))

let private emptyContext : MlbActiveContext = { Half = None; Count = None; Bases = None; Matchup = None }

let private view phase (scoreboard: MlbScoreboardBoard option) (active: MlbActiveContext option) : MlbScoreboardView =
    { Phase = phase; Board = scoreboard; Active = active; Completed = false }

let private live active = view MlbScoreboardPhase.Live (Some fiveInnings) (Some active)

let private parse (html: string) = HtmlParser().ParseDocument html

let private html nodes = nodes |> List.map XmlNodeRenderer.renderNode |> String.concat ""

let private pageWith (g: Game) (snapshots: OddsSnapshot list) (highlights: HighlightsDetailState) (scoreboard: MlbScoreboardView) =
    GameDetailRenderer.RenderMlbWithHighlightsAtTime now g (List<OddsSnapshot>(snapshots)) None (HypeComponents()) (Nullable()) None None highlights scoreboard
    |> html |> parse

let private page (g: Game) scoreboard = pageWith g [] (HighlightsDetailState(HighlightsDetailStatus.Hidden)) scoreboard

let private livePage scoreboard = page (game "in_progress") scoreboard

let private single (doc: IDocument) (selector: string) =
    let element = doc.QuerySelector(selector)
    Assert.True(not (isNull element), $"Expected {selector}")
    element

let private text (doc: IDocument) (selector: string) = (single doc selector).TextContent.Trim()

let private rowCells (doc: IDocument) (row: int) =
    let rows = doc.QuerySelectorAll("#mlb-line-score tbody tr")
    Assert.True(rows.Length > row, $"Expected line score row {row + 1}")
    rows.[row].QuerySelectorAll("td") |> Seq.toList

[<Fact>]
let ``MLB detail reads strip then situation then probability then chart then line score then highlights`` () =
    let doc =
        pageWith (game "in_progress") [ snapshot 0.4 2.0; snapshot 0.45 0.5 ] (HighlightsDetailState(HighlightsDetailStatus.Pending))
            (live { emptyContext with Half = Some MlbHalf.Top })
    let order =
        doc.QuerySelectorAll("#detail-scoreboard > .scoreboard, #mlb-situation, #detail-probbar, nb-win-probability-chart, #mlb-line-score, #game-highlights")
        |> Seq.map (fun e -> if e.Id <> "" then "#" + e.Id elif e.LocalName = "div" then "div." + e.ClassName else e.LocalName)
        |> Seq.toList
    Assert.Equal<string list>(
        [ "div.scoreboard"; "#mlb-situation"; "#detail-probbar"; "nb-win-probability-chart"; "#mlb-line-score"; "#game-highlights" ],
        order)
    let heading = (single doc "#detail-scoreboard").FirstElementChild
    Assert.Equal("H2", heading.TagName)
    Assert.Equal("visually-hidden", heading.ClassName)
    Assert.Equal("Score", heading.TextContent)
    Assert.Equal("Line Score", text doc "#mlb-line-score > h2#mlb-line-score-heading")

[<Fact>]
let ``team columns name away and home and board runs override stored scores`` () =
    let doc = livePage (live emptyContext)
    let sides = doc.QuerySelectorAll("#detail-scoreboard .scoreboard-team .scoreboard-side") |> Seq.map (fun e -> e.TextContent) |> Seq.toList
    Assert.Equal<string list>([ "Away"; "Home" ], sides)
    Assert.Equal("4", text doc "#detail-away-score")
    Assert.Equal("3", text doc "#detail-home-score")

[<Fact>]
let ``supplied zero runs stay zero on the strip and in the line score`` () =
    let scoreless = board 2 (team 0 (runs [ 0; 0 ])) (team 0 (runs [ 0; 0 ]))
    let doc = livePage (view MlbScoreboardPhase.Live (Some scoreless) (Some emptyContext))
    Assert.Equal("0", text doc "#detail-away-score")
    Assert.Equal("0", text doc "#detail-home-score")
    Assert.Equal<string list>([ "0"; "0"; "0"; "7"; "1" ], rowCells doc 0 |> List.map (fun c -> c.TextContent))

[<Fact>]
let ``board absent keeps the stored game scores`` () =
    let doc = livePage (view MlbScoreboardPhase.Unavailable None None)
    Assert.Equal("1", text doc "#detail-away-score")
    Assert.Equal("2", text doc "#detail-home-score")
    Assert.Null(doc.QuerySelector("#mlb-line-score"))
    Assert.NotNull(doc.QuerySelector("#mlb-situation"))

[<Fact>]
let ``missing not played and pending innings are distinct cells with hidden text`` () =
    let away = team 4 [ MlbLineCell.Runs 1; MlbLineCell.Missing; MlbLineCell.Runs 3 ]
    let home = team 2 [ MlbLineCell.Runs 2; MlbLineCell.NotPlayed ]
    let doc = livePage (view MlbScoreboardPhase.Live (Some(board 3 away home)) (Some emptyContext))
    let awayCells = rowCells doc 0
    let missing = awayCells.[1]
    Assert.Equal("line-score-missing", missing.ClassName)
    Assert.Equal("–", missing.QuerySelector("[aria-hidden='true']").TextContent)
    Assert.Equal("Not recorded", missing.QuerySelector(".visually-hidden").TextContent)
    let homeCells = rowCells doc 1
    let notPlayed = homeCells.[1]
    Assert.Equal("line-score-not-played", notPlayed.ClassName)
    Assert.Equal("x", notPlayed.QuerySelector("[aria-hidden='true']").TextContent)
    Assert.Equal("Not played", notPlayed.QuerySelector(".visually-hidden").TextContent)
    let pending = homeCells.[2]
    Assert.Equal("line-score-pending", pending.ClassName)
    Assert.Null(pending.QuerySelector("[aria-hidden='true']"))
    Assert.Equal("Not yet played", pending.QuerySelector(".visually-hidden").TextContent)
    Assert.Equal<string list>([ "1"; "3"; "4" ], [ awayCells.[0]; awayCells.[2]; awayCells.[3] ] |> List.map (fun c -> c.TextContent))

[<Fact>]
let ``unknown hits and errors render as not recorded rather than zero`` () =
    let away = { team 1 (runs [ 1 ]) with Hits = None; Errors = None }
    let doc = livePage (view MlbScoreboardPhase.Live (Some(board 1 away (team 0 (runs [ 0 ])))) (Some emptyContext))
    let cells = rowCells doc 0
    Assert.Equal("1", cells.[1].TextContent)
    for cell in [ cells.[2]; cells.[3] ] do
        Assert.Equal("line-score-missing", cell.ClassName)
        Assert.Equal("Not recorded", cell.QuerySelector(".visually-hidden").TextContent)

[<Fact>]
let ``extra innings get one column per inning and a final innings count`` () =
    let away = team 5 (runs [ 0; 0; 1; 0; 0; 2; 0; 0; 1; 0; 0; 1 ])
    let home = team 4 (runs [ 1; 0; 0; 0; 2; 0; 0; 1; 0; 0; 0; 0 ])
    let doc = page (game "final") (view MlbScoreboardPhase.Final (Some(board 12 away home)) None)
    let headers = (single doc "#mlb-line-score thead").QuerySelectorAll("th") |> Seq.map (fun e -> e.TextContent) |> Seq.toList
    Assert.Equal<string list>("Team" :: ([ 1..12 ] |> List.map string) @ [ "R"; "H"; "E" ], headers)
    Assert.Equal(15, (rowCells doc 0).Length)
    Assert.Equal(15, (rowCells doc 1).Length)
    Assert.Equal("Final · 12 innings", text doc "#detail-clock")

[<Fact>]
let ``line score is a captioned table with column and row headers`` () =
    let doc = livePage (live emptyContext)
    let section = single doc "section#mlb-line-score.panel"
    Assert.Equal("mlb-line-score-heading", section.GetAttribute("aria-labelledby"))
    let scroll = section.QuerySelector(".line-score-scroll")
    Assert.Equal("region", scroll.GetAttribute("role"))
    Assert.Equal("0", scroll.GetAttribute("tabindex"))
    Assert.Equal("mlb-line-score-heading", scroll.GetAttribute("aria-labelledby"))
    let table = scroll.QuerySelector("table.line-score")
    Assert.Equal("Runs by inning, then total runs, hits and errors", table.QuerySelector("caption.visually-hidden").TextContent)
    let columns = table.QuerySelectorAll("thead th")
    Assert.All(columns, fun th -> Assert.Equal("col", th.GetAttribute("scope")))
    let totals = table.QuerySelectorAll("thead th.line-score-total abbr") |> Seq.map (fun a -> a.GetAttribute("title"), a.TextContent) |> Seq.toList
    Assert.Equal<(string * string) list>([ "Runs", "R"; "Hits", "H"; "Errors", "E" ], totals)
    let rows = table.QuerySelectorAll("tbody th") |> Seq.toList
    Assert.All(rows, fun th -> Assert.Equal("row", th.GetAttribute("scope")))
    Assert.Equal<(string * string) list>(
        [ "Chicago Cubs", "CHC"; "Cincinnati Reds", "CIN" ],
        rows |> List.map (fun th -> let abbr = th.QuerySelector("abbr") in abbr.GetAttribute("title"), abbr.TextContent))

[<Fact>]
let ``board without innings shows no line score`` () =
    let doc = page (game "scheduled") (view MlbScoreboardPhase.Pregame (Some(board 0 (team 0 []) (team 0 []))) None)
    Assert.Null(doc.QuerySelector("#mlb-line-score"))

[<Theory>]
[<InlineData("nba")>]
[<InlineData("mlb")>]
let ``detail pages without a scoreboard view carry no MLB scoreboard markup`` sport =
    let g = game "in_progress"
    g.Sport <- sport
    let doc =
        GameDetailRenderer.RenderWithHighlightsAtTime now g (List<OddsSnapshot>([ snapshot 0.4 2.0; snapshot 0.45 0.5 ])) None (HypeComponents()) (Nullable()) None None
            (HighlightsDetailState(HighlightsDetailStatus.Pending))
        |> html |> parse
    Assert.NotNull(doc.QuerySelector("#detail-scoreboard"))
    for selector in [ "#mlb-situation"; "#mlb-line-score"; ".scoreboard-side" ] do
        Assert.Null(doc.QuerySelector(selector))
