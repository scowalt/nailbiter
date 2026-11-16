namespace Backend.Rendering

open Falco.Markup
open backend.Services

module MlbScoreboardRenderer =

    let private cls = Layout.cls

    let private ariaHidden = Attr.create "aria-hidden" "true"

    [<RequireQualifiedAccess>]
    type Situation =
        | Facts of MlbActiveContext
        | RichContextUnavailable
        | LastKnown
        | RecordedScore
        | ScoreboardUnavailable
        | NoSituation

    [<RequireQualifiedAccess>]
    type Clock =
        | Generic
        | Blank
        | Live of string
        | Status of string

    [<RequireQualifiedAccess>]
    type Probability =
        | Current of homeProbability: float
        | Unavailable
        | Hidden

    let situation (gameInProgress: bool) (view: MlbScoreboardView) =
        match view.Phase with
        | MlbScoreboardPhase.Live ->
            match view.Active with
            | Some active when active.Count.IsSome || active.Bases.IsSome || active.Matchup.IsSome -> Situation.Facts active
            | _ -> Situation.RichContextUnavailable
        | MlbScoreboardPhase.Stale -> Situation.LastKnown
        | MlbScoreboardPhase.Recorded -> Situation.RecordedScore
        | MlbScoreboardPhase.Unavailable when gameInProgress -> Situation.ScoreboardUnavailable
        | MlbScoreboardPhase.Unavailable
        | MlbScoreboardPhase.Pregame
        | MlbScoreboardPhase.Delayed
        | MlbScoreboardPhase.Suspended
        | MlbScoreboardPhase.Postponed
        | MlbScoreboardPhase.Canceled
        | MlbScoreboardPhase.Final -> Situation.NoSituation

    let ordinal (n: int) =
        let suffix =
            match n % 100, n % 10 with
            | (11 | 12 | 13), _ -> "th"
            | _, 1 -> "st"
            | _, 2 -> "nd"
            | _, 3 -> "rd"
            | _ -> "th"
        $"{n}{suffix}"

    let clock (gameScheduled: bool) (view: MlbScoreboardView) =
        let inning = view.Board |> Option.bind (fun board -> board.Inning)
        let interrupted label =
            match inning with
            | Some i -> Clock.Status $"{label} · {ordinal i} inning"
            | None -> Clock.Status label
        match view.Phase with
        | MlbScoreboardPhase.Unavailable when gameScheduled -> Clock.Blank
        | MlbScoreboardPhase.Unavailable -> Clock.Generic
        | MlbScoreboardPhase.Pregame -> Clock.Blank
        | MlbScoreboardPhase.Live ->
            match inning, view.Active |> Option.bind (fun active -> active.Half) with
            | Some i, Some MlbHalf.Top -> Clock.Live $"Top {ordinal i}"
            | Some i, Some MlbHalf.Bottom -> Clock.Live $"Bottom {ordinal i}"
            | Some i, None -> Clock.Live $"{ordinal i} inning"
            | None, _ -> Clock.Live "In progress"
        | MlbScoreboardPhase.Stale
        | MlbScoreboardPhase.Recorded ->
            match inning with
            | Some i -> Clock.Status $"{ordinal i} inning"
            | None -> Clock.Status "In progress"
        | MlbScoreboardPhase.Delayed -> interrupted "Delayed"
        | MlbScoreboardPhase.Suspended -> interrupted "Suspended"
        | MlbScoreboardPhase.Postponed -> Clock.Status "Postponed"
        | MlbScoreboardPhase.Canceled -> Clock.Status "Canceled"
        | MlbScoreboardPhase.Final ->
            match view.Board with
            | Some board when board.Innings > 9 -> Clock.Status $"Final · {board.Innings} innings"
            | _ -> Clock.Status "Final"

    let probability (gameFinal: bool) (gameInProgress: bool) (view: MlbScoreboardView) (homeProbability: float option) =
        let final = gameFinal || view.Phase = MlbScoreboardPhase.Final
        match homeProbability with
        | _ when final -> Probability.Hidden
        | Some p -> Probability.Current p
        | None when gameInProgress -> Probability.Unavailable
        | None -> Probability.Hidden

    let renderClock (clock: Clock) (generic: XmlNode list) =
        Elem.div [ Attr.id "detail-clock" ] [
            match clock with
            | Clock.Generic -> yield! generic
            | Clock.Blank -> ()
            | Clock.Live text -> Elem.div [ cls "detail-clock-text" ] [ Text.raw text ]
            | Clock.Status text -> Elem.div [ cls "mlb-clock-status" ] [ Text.raw text ]
        ]

    let private quantity (n: int) (unit: string) = if n = 1 then $"{n} {unit}" else $"{n} {unit}s"

    let private fact (term: string) (description: XmlNode list) =
        Elem.div [ cls "situation-fact" ] [
            Elem.dt [] [ Text.raw term ]
            Elem.dd [] description
        ]

    let private basesInWords (occupied: string list) =
        match occupied with
        | [] -> "Bases empty"
        | [ only ] -> $"Runner on {only}"
        | [ lead; trail ] -> $"Runners on {lead} and {trail}"
        | _ -> "Bases loaded"

    let private basesFact (bases: MlbBases) =
        let positions = [ "1st", "base-first", bases.First; "2nd", "base-second", bases.Second; "3rd", "base-third", bases.Third ]
        let occupied =
            positions |> List.choose (fun (label, _, state) ->
                match state with
                | MlbBase.Occupied runner -> Some(label, runner)
                | MlbBase.Empty -> None)
        fact "Bases" [
            Elem.span [ cls "bases-diamond"; ariaHidden ] [
                for _, position, state in positions do
                    match state with
                    | MlbBase.Occupied _ -> Elem.span [ cls $"base {position} is-occupied" ] []
                    | MlbBase.Empty -> Elem.span [ cls $"base {position}" ] []
            ]
            Elem.span [ cls "bases-text" ] [ Text.raw (basesInWords (occupied |> List.map fst)) ]
            for label, runner in occupied do
                match runner with
                | Some name -> Elem.span [ cls "bases-runner" ] [ Text.enc $"{label}: {name}" ]
                | None -> ()
        ]

    let private facts (active: MlbActiveContext) = [
        Elem.h3 [ cls "visually-hidden" ] [ Text.raw "Situation" ]
        Elem.dl [ cls "situation-facts" ] [
            match active.Count with
            | Some count ->
                let spoken = String.concat ", " [ quantity count.Balls "ball"; quantity count.Strikes "strike" ]
                fact "Count" [
                    Elem.span [ ariaHidden ] [ Text.raw $"{count.Balls}-{count.Strikes}" ]
                    Elem.span [ cls "visually-hidden" ] [ Text.raw spoken ]
                ]
                fact "Outs" [ Text.raw $"{count.Outs}" ]
            | None -> ()
            match active.Bases with
            | Some bases -> basesFact bases
            | None -> ()
            match active.Matchup |> Option.bind (fun matchup -> matchup.Batter) with
            | Some batter -> fact "Batting" [ Text.enc batter ]
            | None -> ()
            match active.Matchup |> Option.bind (fun matchup -> matchup.Pitcher) with
            | Some pitcher -> fact "Pitching" [ Text.enc pitcher ]
            | None -> ()
        ]
    ]

    let renderSituation (situation: Situation) =
        let note (text: string) = [ Elem.p [ cls "situation-note" ] [ Text.raw text ] ]
        Elem.div [ Attr.id "mlb-situation"; cls "mlb-situation" ] (
            match situation with
            | Situation.Facts active -> facts active
            | Situation.RichContextUnavailable -> note "Count, runners and matchup unavailable"
            | Situation.LastKnown -> note "Last known score · live situation unavailable"
            | Situation.RecordedScore -> note "Recorded score · no live situation"
            | Situation.ScoreboardUnavailable -> note "Live scoreboard unavailable"
            | Situation.NoSituation -> [])

    let private spokenCell (className: string) (visible: string option) (spoken: string) =
        Elem.td [ cls className ] [
            match visible with
            | Some mark -> Elem.span [ ariaHidden ] [ Text.raw mark ]
            | None -> ()
            Elem.span [ cls "visually-hidden" ] [ Text.raw spoken ]
        ]

    let private missingCell = spokenCell "line-score-missing" (Some "–") "Not recorded"

    let private numberCell (n: int) = Elem.td [] [ Text.raw $"{n}" ]

    let private inningCell (cell: MlbLineCell option) =
        match cell with
        | Some(MlbLineCell.Runs n) -> numberCell n
        | Some MlbLineCell.Missing -> missingCell
        | Some MlbLineCell.NotPlayed -> spokenCell "line-score-not-played" (Some "x") "Not played"
        | None -> spokenCell "line-score-pending" None "Not yet played"

    let private countCell (count: int option) =
        match count with
        | Some n -> numberCell n
        | None -> missingCell

    let private teamRow (innings: int) (abbreviation: string, name: string) (score: MlbTeamScore) =
        Elem.tr [] [
            Elem.th [ Attr.create "scope" "row" ] [ Elem.abbr [ Attr.title name ] [ Text.enc abbreviation ] ]
            for inning in 0 .. innings - 1 do
                inningCell (List.tryItem inning score.Line)
            numberCell score.Runs
            countCell score.Hits
            countCell score.Errors
        ]

    let renderLineScore (away: string * string) (home: string * string) (board: MlbScoreboardBoard option) =
        match board with
        | Some board when board.Innings > 0 ->
            let column (label: string) = Elem.th [ Attr.create "scope" "col" ] [ Text.raw label ]
            let total (title: string) (label: string) =
                Elem.th [ Attr.create "scope" "col"; cls "line-score-total" ] [ Elem.abbr [ Attr.title title ] [ Text.raw label ] ]
            [
                Elem.section [ Attr.id "mlb-line-score"; cls "panel"; Attr.create "aria-labelledby" "mlb-line-score-heading" ] [
                    Elem.h2 [ Attr.id "mlb-line-score-heading"; cls "panel-title" ] [ Text.raw "Line Score" ]
                    Elem.div [
                        cls "line-score-scroll"
                        Attr.create "role" "region"
                        Attr.create "aria-labelledby" "mlb-line-score-heading"
                        Attr.create "tabindex" "0"
                    ] [
                        Elem.table [ cls "line-score" ] [
                            Elem.caption [ cls "visually-hidden" ] [ Text.raw "Runs by inning, then total runs, hits and errors" ]
                            Elem.thead [] [
                                Elem.tr [] [
                                    column "Team"
                                    for inning in 1 .. board.Innings do
                                        column $"{inning}"
                                    total "Runs" "R"
                                    total "Hits" "H"
                                    total "Errors" "E"
                                ]
                            ]
                            Elem.tbody [] [
                                teamRow board.Innings away board.Away
                                teamRow board.Innings home board.Home
                            ]
                        ]
                    ]
                ]
            ]
        | _ -> []
