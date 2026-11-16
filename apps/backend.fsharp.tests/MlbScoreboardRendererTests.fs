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
    let signature (element: IElement) =
        let id = if element.Id = "" then "" else "#" + element.Id
        let classes = element.ClassList |> Seq.map (fun c -> "." + c) |> String.concat ""
        element.LocalName + id + classes
    let children (selector: string) =
        (single doc selector).Children |> Seq.filter (fun e -> e.LocalName <> "img") |> Seq.map signature |> Seq.toList
    Assert.Equal<string list>(
        [ "div.detail-topbar"; "div#detail-scoreboard.panel"; "div.panel"; "section#game-highlights.panel" ],
        children ".page-narrow")
    Assert.Equal<string list>([ "div.scoreboard"; "div#detail-probbar" ], children "#detail-scoreboard")
    Assert.Equal<string list>([ "div.scoreboard-team"; "div.scoreboard-center"; "div.scoreboard-team" ], children ".scoreboard")
    Assert.Equal<string list>([ "span.scoreboard-abbr"; "span.meta-text"; "span#detail-away-score.detail-score" ], children ".scoreboard-team")
    Assert.Equal<string list>([ "div#detail-clock" ], children ".scoreboard-center")
    Assert.Equal<string list>([ "span#detail-status.detail-status-badge.status-live.animate-live-pulse" ], children ".detail-league")

let private occupied name = MlbBase.Occupied name

let private bases first second third : MlbBases = { First = first; Second = second; Third = third }

let private facts (doc: IDocument) =
    doc.QuerySelectorAll("#mlb-situation .situation-fact")
    |> Seq.map (fun fact -> fact.QuerySelector("dt").TextContent, fact.QuerySelector("dd"))
    |> Seq.toList

let private fact (doc: IDocument) (term: string) =
    match facts doc |> List.tryFind (fun (dt, _) -> dt = term) with
    | Some(_, dd) -> dd
    | None -> failwith $"Expected a {term} fact"

let private terms (doc: IDocument) = facts doc |> List.map fst

[<Theory>]
[<InlineData(2, 1, 1, "2-1", "2 balls, 1 strike", "1")>]
[<InlineData(1, 2, 2, "1-2", "1 ball, 2 strikes", "2")>]
[<InlineData(0, 0, 0, "0-0", "0 balls, 0 strikes", "0")>]
[<InlineData(3, 2, 2, "3-2", "3 balls, 2 strikes", "2")>]
let ``count shows balls strikes and outs with a spoken equivalent`` balls strikes outs visible spoken outsText =
    let doc = livePage (live { emptyContext with Count = Some { Balls = balls; Strikes = strikes; Outs = outs } })
    Assert.Equal("Situation", text doc "#mlb-situation > h3.visually-hidden")
    let count = fact doc "Count"
    Assert.Equal(visible, count.QuerySelector("[aria-hidden='true']").TextContent)
    Assert.Equal(spoken, count.QuerySelector(".visually-hidden").TextContent)
    Assert.Equal(outsText, (fact doc "Outs").TextContent)
    Assert.Equal<string list>([ "Count"; "Outs" ], terms doc)

[<Fact>]
let ``bases empty is stated and the diamond is decorative`` () =
    let doc = livePage (live { emptyContext with Bases = Some(bases MlbBase.Empty MlbBase.Empty MlbBase.Empty) })
    let dd = fact doc "Bases"
    let diamond = dd.QuerySelector(".bases-diamond")
    Assert.Equal("true", diamond.GetAttribute("aria-hidden"))
    Assert.Equal(3, diamond.QuerySelectorAll(".base").Length)
    Assert.Equal(0, diamond.QuerySelectorAll(".is-occupied").Length)
    Assert.Equal("Bases empty", dd.TextContent.Trim())

let baseCases : obj[] list =
    [ [| bases (occupied None) MlbBase.Empty MlbBase.Empty; "Runner on 1st"; [ "base-first" ] |]
      [| bases MlbBase.Empty (occupied None) MlbBase.Empty; "Runner on 2nd"; [ "base-second" ] |]
      [| bases (occupied None) MlbBase.Empty (occupied None); "Runners on 1st and 3rd"; [ "base-first"; "base-third" ] |]
      [| bases MlbBase.Empty (occupied None) (occupied None); "Runners on 2nd and 3rd"; [ "base-second"; "base-third" ] |]
      [| bases (occupied None) (occupied None) (occupied None); "Bases loaded"; [ "base-first"; "base-second"; "base-third" ] |] ]

[<Theory>]
[<MemberData(nameof baseCases)>]
let ``occupied bases are filled and named in words`` (state: MlbBases) (words: string) (filled: string list) =
    let doc = livePage (live { emptyContext with Bases = Some state })
    let dd = fact doc "Bases"
    let occupiedBases =
        dd.QuerySelectorAll(".bases-diamond .base.is-occupied")
        |> Seq.map (fun b -> b.ClassList |> Seq.find (fun c -> c.StartsWith "base-"))
        |> Seq.toList
    Assert.Equal<string list>(filled, occupiedBases)
    Assert.Equal(words, dd.TextContent.Trim())

[<Fact>]
let ``runner names appear only for occupied bases whose runner is known`` () =
    let state = bases (occupied(Some "Synthetic Runner A")) MlbBase.Empty (occupied None)
    let doc = livePage (live { emptyContext with Bases = Some state })
    let dd = fact doc "Bases"
    Assert.Contains("Runners on 1st and 3rd", dd.TextContent)
    Assert.Contains("1st: Synthetic Runner A", dd.TextContent)
    Assert.DoesNotContain("3rd:", dd.TextContent)
    Assert.DoesNotContain("2nd:", dd.TextContent)

[<Fact>]
let ``batter and pitcher each render only when known`` () =
    let batterOnly = livePage (live { emptyContext with Matchup = Some { Batter = Some "Synthetic Batter"; Pitcher = None } })
    Assert.Equal<string list>([ "Batting" ], terms batterOnly)
    Assert.Equal("Synthetic Batter", (fact batterOnly "Batting").TextContent)
    let pitcherOnly = livePage (live { emptyContext with Matchup = Some { Batter = None; Pitcher = Some "Synthetic Pitcher" } })
    Assert.Equal<string list>([ "Pitching" ], terms pitcherOnly)
    Assert.Equal("Synthetic Pitcher", (fact pitcherOnly "Pitching").TextContent)

[<Fact>]
let ``unknown facts leave no placeholder count diamond or half`` () =
    let doc = livePage (live { emptyContext with Matchup = Some { Batter = Some "Synthetic Batter"; Pitcher = Some "Synthetic Pitcher" } })
    Assert.Equal<string list>([ "Batting"; "Pitching" ], terms doc)
    let situation = text doc "#mlb-situation"
    for absent in [ "0-0"; "Top"; "Bottom"; "Bases"; "Count"; "Outs" ] do
        Assert.DoesNotContain(absent, situation)
    Assert.Null(doc.QuerySelector("#mlb-situation .bases-diamond"))

[<Fact>]
let ``live without count runners or matchup says the rich context is unavailable`` () =
    let doc = livePage (live { emptyContext with Half = Some MlbHalf.Bottom })
    Assert.Equal("Count, runners and matchup unavailable", text doc "#mlb-situation")
    Assert.Null(doc.QuerySelector("#mlb-situation dl"))
    Assert.Equal("Bottom 5th", text doc "#detail-clock")

[<Theory>]
[<InlineData("top", "Top 5th")>]
[<InlineData("bottom", "Bottom 5th")>]
[<InlineData("", "5th inning")>]
let ``live clock shows the half only when it is known`` half expected =
    let known = match half with "top" -> Some MlbHalf.Top | "bottom" -> Some MlbHalf.Bottom | _ -> None
    let doc = livePage (live { emptyContext with Half = known; Count = Some { Balls = 0; Strikes = 0; Outs = 0 } })
    Assert.Equal(expected, text doc "#detail-clock")
    Assert.DoesNotContain("Top", text doc "#mlb-situation")
    Assert.DoesNotContain("Bottom", text doc "#mlb-situation")

[<Fact>]
let ``live clock without an inning says in progress`` () =
    let doc = livePage (view MlbScoreboardPhase.Live (Some { fiveInnings with Inning = None }) (Some { emptyContext with Half = Some MlbHalf.Top }))
    Assert.Equal("In progress", text doc "#detail-clock")

[<Theory>]
[<InlineData(1, "1st")>]
[<InlineData(2, "2nd")>]
[<InlineData(3, "3rd")>]
[<InlineData(4, "4th")>]
[<InlineData(11, "11th")>]
[<InlineData(12, "12th")>]
[<InlineData(13, "13th")>]
[<InlineData(21, "21st")>]
[<InlineData(22, "22nd")>]
[<InlineData(23, "23rd")>]
let ``innings use English ordinals`` inning ordinal =
    let doc = livePage (view MlbScoreboardPhase.Live (Some { fiveInnings with Inning = Some inning }) (Some emptyContext))
    Assert.Equal($"{ordinal} inning", text doc "#detail-clock")

let noteCases : obj[] list =
    [ [| MlbScoreboardPhase.Stale; "Last known score · live situation unavailable"; "5th inning" |]
      [| MlbScoreboardPhase.Recorded; "Recorded score · no live situation"; "5th inning" |] ]

[<Theory>]
[<MemberData(nameof noteCases)>]
let ``stale and recorded boards explain the missing situation and never show a half`` (phase: MlbScoreboardPhase) (note: string) (clock: string) =
    let doc = livePage (view phase (Some fiveInnings) None)
    Assert.Equal(note, text doc "#mlb-situation")
    Assert.Equal(clock, text doc "#detail-clock")
    Assert.Equal("4", text doc "#detail-away-score")

[<Fact>]
let ``unavailable scoreboard during a game says so and keeps the generic clock`` () =
    let quote = snapshot 0.55 0.5
    quote.Period <- Nullable 6
    let doc = pageWith (game "in_progress") [ quote ] (HighlightsDetailState(HighlightsDetailStatus.Hidden)) (view MlbScoreboardPhase.Unavailable None None)
    Assert.Equal("Live scoreboard unavailable", text doc "#mlb-situation")
    Assert.Equal("Inning 6", text doc "#detail-clock")
    let withoutOdds = livePage (view MlbScoreboardPhase.Unavailable None None)
    Assert.Equal("@", text withoutOdds "#detail-clock")

[<Theory>]
[<InlineData("final")>]
[<InlineData("scheduled")>]
let ``unavailable scoreboard outside a game leaves the situation empty`` status =
    let doc = page (game status) (view MlbScoreboardPhase.Unavailable None None)
    Assert.Equal("", text doc "#mlb-situation")

[<Fact>]
let ``scheduled pages keep the matchup center and an empty clock to patch`` () =
    for phase in [ MlbScoreboardPhase.Pregame; MlbScoreboardPhase.Unavailable ] do
        let doc = page (game "scheduled") (view phase None None)
        Assert.Equal("VS", text doc ".scoreboard-center .scoreboard-vs")
        Assert.Equal("", text doc ".scoreboard-center #detail-clock")
        Assert.Equal("", text doc "#mlb-situation")

let interruptionCases : obj[] list =
    [ [| MlbScoreboardPhase.Delayed; Some 5; "Delayed · 5th inning" |]
      [| MlbScoreboardPhase.Delayed; None; "Delayed" |]
      [| MlbScoreboardPhase.Suspended; Some 7; "Suspended · 7th inning" |]
      [| MlbScoreboardPhase.Suspended; None; "Suspended" |]
      [| MlbScoreboardPhase.Postponed; Some 3; "Postponed" |]
      [| MlbScoreboardPhase.Canceled; None; "Canceled" |]
      [| MlbScoreboardPhase.Final; Some 9; "Final" |] ]

[<Theory>]
[<MemberData(nameof interruptionCases)>]
let ``interrupted and ended games label the clock and show no situation`` (phase: MlbScoreboardPhase) (inning: int option) (label: string) =
    let doc = livePage (view phase (Some { board 9 (team 4 (runs [ 0; 2; 0; 1; 1; 0; 0; 0; 0 ])) (team 3 (runs [ 1; 0; 2; 0; 0; 0; 0; 0 ])) with Inning = inning }) None)
    Assert.Equal(label, text doc "#detail-clock")
    Assert.Equal("", text doc "#mlb-situation")
    Assert.Empty(facts doc)

[<Fact>]
let ``a current quote renders the probability bar from a single snapshot without a chart`` () =
    let doc = pageWith (game "in_progress") [ snapshot 0.6 0.5 ] (HighlightsDetailState(HighlightsDetailStatus.Hidden)) (live emptyContext)
    let bar = single doc "#detail-probbar .detail-probability"
    Assert.Contains("CIN 60%", bar.TextContent)
    Assert.Contains("CHC 40%", bar.TextContent)
    Assert.Null(doc.QuerySelector("nb-win-probability-chart"))

[<Fact>]
let ``no quote during a game says live probability is unavailable instead of an even bar`` () =
    for phase in [ MlbScoreboardPhase.Live; MlbScoreboardPhase.Stale; MlbScoreboardPhase.Unavailable ] do
        let scoreboard = if phase = MlbScoreboardPhase.Unavailable then None else Some fiveInnings
        let doc = livePage (view phase scoreboard (if phase = MlbScoreboardPhase.Live then Some emptyContext else None))
        Assert.Equal("Live probability unavailable", text doc "#detail-probbar")
        Assert.Null(doc.QuerySelector("#detail-probbar .probability-track"))
        Assert.DoesNotContain("50%", doc.Body.TextContent)

[<Fact>]
let ``final games show no current quote even with a fresh snapshot`` () =
    let doc =
        pageWith (game "final") [ snapshot 0.98 0.2 ] (HighlightsDetailState(HighlightsDetailStatus.Hidden))
            (view MlbScoreboardPhase.Final (Some fiveInnings) None)
    Assert.Equal("", text doc "#detail-probbar")
    let finalBoard =
        pageWith (game "in_progress") [ snapshot 0.98 0.2 ] (HighlightsDetailState(HighlightsDetailStatus.Hidden))
            (view MlbScoreboardPhase.Final (Some fiveInnings) None)
    Assert.Equal("", text finalBoard "#detail-probbar")

let private update status (homeWinProbability: float option) =
    GameUpdate(GameId = "mlb-synthetic-1", Sport = Sport.MLB, Status = status,
               HomeTeam = "Cincinnati Reds", AwayTeam = "Chicago Cubs", HomeTeamAbbreviation = "CIN", AwayTeamAbbreviation = "CHC",
               HomeScore = Nullable 2, AwayScore = Nullable 1,
               HomeWinProbability = Option.toNullable homeWinProbability,
               AwayWinProbability = Option.toNullable (homeWinProbability |> Option.map (fun p -> 1.0 - p)))

let private fragments status probability scoreboard =
    SseFragmentRenderer.renderMlbDetailFragments (update status probability) scoreboard |> parse

[<Fact>]
let ``live fragments without a quote say probability is unavailable and never show an even bar`` () =
    let doc = fragments GameStatus.IN_PROGRESS None (live emptyContext)
    Assert.Equal("Live probability unavailable", text doc "#detail-probbar")
    Assert.Null(doc.QuerySelector("#detail-probbar .probability-track"))
    Assert.DoesNotContain("50%", doc.Body.TextContent)

[<Fact>]
let ``fragments render a current quote and drop it once final`` () =
    let current = fragments GameStatus.IN_PROGRESS (Some 0.6) (live emptyContext)
    Assert.Contains("CIN 60%", text current "#detail-probbar .detail-probability")
    let ended = fragments GameStatus.FINAL (Some 0.98) (view MlbScoreboardPhase.Final (Some fiveInnings) None)
    Assert.Equal("", text ended "#detail-probbar")
    let finalBoard = fragments GameStatus.IN_PROGRESS (Some 0.98) (view MlbScoreboardPhase.Final (Some fiveInnings) None)
    Assert.Equal("", text finalBoard "#detail-probbar")

[<Fact>]
let ``fragments take scores from the board and fall back to the update`` () =
    let fromBoard = fragments GameStatus.IN_PROGRESS None (live emptyContext)
    Assert.Equal("4", text fromBoard "#detail-away-score")
    Assert.Equal("3", text fromBoard "#detail-home-score")
    let fromUpdate = fragments GameStatus.IN_PROGRESS None (view MlbScoreboardPhase.Unavailable None None)
    Assert.Equal("1", text fromUpdate "#detail-away-score")
    Assert.Equal("2", text fromUpdate "#detail-home-score")
    Assert.Null(fromUpdate.QuerySelector("#mlb-line-score"))
    Assert.Equal("Live scoreboard unavailable", text fromUpdate "#mlb-situation")
    Assert.Equal("@", text fromUpdate "#detail-clock")

let private richContext : MlbActiveContext =
    { Half = Some MlbHalf.Top
      Count = Some { Balls = 2; Strikes = 1; Outs = 1 }
      Bases = Some(bases (occupied(Some "Synthetic Runner A")) MlbBase.Empty (occupied None))
      Matchup = Some { Batter = Some "Synthetic Batter"; Pitcher = Some "Synthetic Pitcher" } }

let patchCases : obj[] list =
    let extra = board 12 (team 5 (runs [ 0; 0; 1; 0; 0; 2; 0; 0; 1; 0; 0; 1 ])) (team 4 (runs [ 1; 0; 0; 0; 2; 0; 0; 1; 0; 0; 0; 0 ]))
    [ [| "in_progress"; live richContext |]
      [| "in_progress"; live emptyContext |]
      [| "in_progress"; view MlbScoreboardPhase.Stale (Some fiveInnings) None |]
      [| "in_progress"; view MlbScoreboardPhase.Recorded (Some fiveInnings) None |]
      [| "in_progress"; view MlbScoreboardPhase.Delayed (Some fiveInnings) None |]
      [| "in_progress"; view MlbScoreboardPhase.Suspended (Some fiveInnings) None |]
      [| "in_progress"; view MlbScoreboardPhase.Unavailable None None |]
      [| "scheduled"; view MlbScoreboardPhase.Postponed (Some fiveInnings) None |]
      [| "canceled"; view MlbScoreboardPhase.Canceled None None |]
      [| "final"; view MlbScoreboardPhase.Final (Some extra) None |] ]

[<Theory>]
[<MemberData(nameof patchCases)>]
let ``a patch equals the page for the same scoreboard`` (status: string) (scoreboard: MlbScoreboardView) =
    let g = game status
    let pageDoc = page g scoreboard
    let patch = fragments (GameStatusExtensions.FromDbValue(status).Value) None scoreboard
    for selector in [ "#mlb-situation"; "#detail-clock"; "#detail-away-score"; "#detail-home-score" ] do
        Assert.Equal((single pageDoc selector).OuterHtml, (single patch selector).OuterHtml)
    match pageDoc.QuerySelector("#mlb-line-score") with
    | null -> Assert.Null(patch.QuerySelector("#mlb-line-score"))
    | section -> Assert.Equal(section.OuterHtml, (single patch "#mlb-line-score").OuterHtml)

let private badge (doc: IDocument) =
    let element = single doc "#detail-status"
    element.TextContent.Trim(), element.ClassList |> Seq.toList

let interruptedBadgeCases : obj[] list =
    [ [| "in_progress"; MlbScoreboardPhase.Delayed; "Delayed"; "status-scheduled" |]
      [| "in_progress"; MlbScoreboardPhase.Suspended; "Suspended"; "status-scheduled" |]
      [| "scheduled"; MlbScoreboardPhase.Postponed; "Postponed"; "status-scheduled" |]
      [| "canceled"; MlbScoreboardPhase.Canceled; "Canceled"; "status-final" |] ]

[<Theory>]
[<MemberData(nameof interruptedBadgeCases)>]
let ``the page badge agrees with an interrupted scoreboard`` (status: string) (phase: MlbScoreboardPhase) (label: string) (badgeClass: string) =
    let label', classes = badge (page (game status) (view phase (Some fiveInnings) None))
    Assert.Equal(label, label')
    Assert.Contains(badgeClass, classes)
    Assert.Contains("detail-status-badge", classes)
    Assert.DoesNotContain("status-live", classes)

[<Fact>]
let ``a postponed scheduled game keeps the matchup center and says postponed`` () =
    let doc = page (game "scheduled") (view MlbScoreboardPhase.Postponed (Some fiveInnings) None)
    Assert.Equal("Postponed", fst (badge doc))
    Assert.Equal("VS", text doc ".scoreboard-center .scoreboard-vs")
    Assert.Equal("Postponed", text doc ".scoreboard-center #detail-clock")

[<Fact>]
let ``an in progress game without a scoreboard keeps the live badge`` () =
    let label, classes = badge (livePage (view MlbScoreboardPhase.Unavailable None None))
    Assert.Equal("Live", label)
    Assert.Contains("status-live", classes)

[<Fact>]
let ``a delayed patch says delayed rather than live`` () =
    let label, classes = badge (fragments GameStatus.IN_PROGRESS None (view MlbScoreboardPhase.Delayed (Some fiveInnings) None))
    Assert.Equal("Delayed", label)
    Assert.Contains("status-scheduled", classes)
    Assert.DoesNotContain("status-live", classes)

[<Fact>]
let ``a postponed patch says postponed rather than scheduled`` () =
    let label, classes = badge (fragments GameStatus.SCHEDULED None (view MlbScoreboardPhase.Postponed (Some fiveInnings) None))
    Assert.Equal("Postponed", label)
    Assert.Contains("status-scheduled", classes)

[<Theory>]
[<MemberData(nameof patchCases)>]
let ``a patch badge equals the page badge for the same scoreboard`` (status: string) (scoreboard: MlbScoreboardView) =
    let pageBadge = (single (page (game status) scoreboard) "#detail-status").OuterHtml
    let patchBadge = (single (fragments (GameStatusExtensions.FromDbValue(status).Value) None scoreboard) "#detail-status").OuterHtml
    Assert.Equal(pageBadge, patchBadge)

[<Fact>]
let ``uninterrupted scoreboards keep the stored status badge`` () =
    Assert.Equal("Live", fst (badge (livePage (live emptyContext))))
    Assert.Equal("Final", fst (badge (page (game "final") (view MlbScoreboardPhase.Final (Some fiveInnings) None))))
    Assert.Equal("Live", fst (badge (fragments GameStatus.IN_PROGRESS None (live emptyContext))))

let postponedViewCases : obj[] list =
    [ [| view MlbScoreboardPhase.Unavailable None None |]
      [| view MlbScoreboardPhase.Postponed (Some fiveInnings) None |] ]

[<Theory>]
[<MemberData(nameof postponedViewCases)>]
let ``a stored postponed game says postponed and shows no quote despite a fresh snapshot`` (scoreboard: MlbScoreboardView) =
    let doc = pageWith (game "postponed") [ snapshot 0.6 0.2 ] (HighlightsDetailState(HighlightsDetailStatus.Hidden)) scoreboard
    let label, classes = badge doc
    Assert.Equal("Postponed", label)
    Assert.Contains("status-scheduled", classes)
    Assert.Equal("", text doc "#detail-probbar")
    Assert.Equal("", text doc "#mlb-situation")

[<Fact>]
let ``postponed and canceled scoreboards show no quote despite fresh odds`` () =
    for status, phase in [ "canceled", MlbScoreboardPhase.Canceled; "in_progress", MlbScoreboardPhase.Canceled; "scheduled", MlbScoreboardPhase.Postponed ] do
        let doc = pageWith (game status) [ snapshot 0.6 0.2 ] (HighlightsDetailState(HighlightsDetailStatus.Hidden)) (view phase (Some fiveInnings) None)
        Assert.Equal("", text doc "#detail-probbar")
    for status, phase in [ GameStatus.CANCELED, MlbScoreboardPhase.Canceled; GameStatus.IN_PROGRESS, MlbScoreboardPhase.Canceled; GameStatus.SCHEDULED, MlbScoreboardPhase.Postponed ] do
        let patch = fragments status (Some 0.6) (view phase (Some fiveInnings) None)
        Assert.Equal("", text patch "#detail-probbar")

let private assertSettled (doc: IDocument) (label: string) =
    let clock = text doc "#detail-clock"
    Assert.Equal(label, clock)
    Assert.DoesNotContain("Top", clock)
    Assert.DoesNotContain("Bottom", clock)
    Assert.Null(doc.QuerySelector("#mlb-situation .situation-facts"))
    Assert.Equal("", text doc "#mlb-situation")
    Assert.Equal(label, fst (badge doc))
    Assert.Null(doc.QuerySelector("#mlb-line-score"))
    Assert.DoesNotContain("%", text doc "#detail-probbar")
    Assert.Equal("1", text doc "#detail-away-score")
    Assert.Equal("2", text doc "#detail-home-score")

[<Theory>]
[<InlineData("final", "Final")>]
[<InlineData("canceled", "Canceled")>]
[<InlineData("postponed", "Postponed")>]
let ``a settled stored status overrides an in progress frame on the page`` status label =
    let doc = pageWith (game status) [ snapshot 0.6 0.5 ] (HighlightsDetailState(HighlightsDetailStatus.Hidden)) (live richContext)
    assertSettled doc label

[<Theory>]
[<InlineData("final", "Final")>]
[<InlineData("canceled", "Canceled")>]
let ``a settled update status overrides an in progress frame in the patch`` status label =
    assertSettled (fragments (GameStatusExtensions.FromDbValue(status).Value) (Some 0.6) (live richContext)) label

[<Fact>]
let ``a final frame that agrees with the stored status keeps its extra innings`` () =
    let eleven = board 11 (team 4 (runs [ 0; 2; 0; 1; 1; 0; 0; 0; 0; 0; 0 ])) (team 3 (runs [ 1; 0; 2; 0; 0; 0; 0; 0; 0; 0; 0 ]))
    let scoreboard = view MlbScoreboardPhase.Final (Some eleven) None
    Assert.Equal("Final · 11 innings", text (page (game "final") scoreboard) "#detail-clock")
    Assert.Equal("Final · 11 innings", text (fragments GameStatus.FINAL None scoreboard) "#detail-clock")
