namespace Backend.Rendering

open System
open Falco.Markup
open backend.GraphQL.Types
open backend.Services

module PlayoffBracketRenderer =

    let private cls = Layout.cls

    let private sportFromString (sportStr: string) =
        SportExtensions.FromDbValue(sportStr)

    let private teamRow (sport: Nullable<Sport>) (team: string) (seed: int) (wins: int) (isWinner: bool) (isEliminated: bool) =
        let logoUrl = if sport.HasValue then Logos.getTeamLogoUrl sport.Value team else None
        let teamColor = if sport.HasValue then TeamColors.getTeamColorOrDefault sport.Value team "#6b7280" else "#6b7280"
        let rowClass =
            if isWinner then "bracket-team is-winner"
            elif isEliminated then "bracket-team is-eliminated"
            else "bracket-team"
        Elem.div [ cls rowClass ] [
            Elem.span [ cls "bracket-seed" ] [
                Text.raw (if seed > 0 then $"{seed}" else "")
            ]
            match logoUrl with
            | Some url ->
                Elem.img [
                    Attr.src url
                    Attr.alt team
                    cls "bracket-logo"
                    Attr.create "loading" "lazy"
                ]
            | None ->
                Elem.div [ cls "bracket-logo-placeholder" ] []
            Elem.span [
                cls "bracket-team-name"
                Attr.style $"color: {teamColor}"
            ] [
                Text.enc team
            ]
            Elem.span [ cls "bracket-wins" ] [
                Text.raw $"{wins}"
            ]
        ]

    let private seriesBox (sport: Nullable<Sport>) (series: PlayoffSeriesData) =
        let higherIsWinner = series.Winner = Some series.HigherSeedTeam
        let lowerIsWinner = series.Winner = Some series.LowerSeedTeam
        let higherEliminated = series.Completed && not higherIsWinner
        let lowerEliminated = series.Completed && not lowerIsWinner

        Elem.div [ cls "bracket-series" ] [
            teamRow sport series.HigherSeedTeam series.HigherSeedNumber series.HigherSeedWins higherIsWinner higherEliminated
            Elem.div [ cls "bracket-divider" ] []
            teamRow sport series.LowerSeedTeam series.LowerSeedNumber series.LowerSeedWins lowerIsWinner lowerEliminated
        ]

    let private tbdSeriesBox () =
        Elem.div [ cls "bracket-series-tbd" ] [
            Elem.div [ cls "bracket-tbd-row" ] [
                Elem.div [ cls "bracket-logo-placeholder" ] []
                Elem.span [ cls "bracket-tbd" ] [ Text.raw "TBD" ]
            ]
            Elem.div [ cls "bracket-divider" ] []
            Elem.div [ cls "bracket-tbd-row" ] [
                Elem.div [ cls "bracket-logo-placeholder" ] []
                Elem.span [ cls "bracket-tbd" ] [ Text.raw "TBD" ]
            ]
        ]

    let private roundColumn (label: string) (sport: Nullable<Sport>) (series: PlayoffSeriesData list) (expectedCount: int) =
        let seriesCount = series.Length
        let placeholderCount = max 0 (expectedCount - seriesCount)
        Elem.div [ cls "bracket-round" ] [
            Elem.p [ cls "bracket-round-label" ] [
                Text.raw label
            ]
            Elem.div [] [
                for s in series do
                    seriesBox sport s
                for _ in 1..placeholderCount do
                    tbdSeriesBox ()
            ]
        ]

    let private finalsColumn (sport: Nullable<Sport>) (finalSeries: PlayoffSeriesData list) =
        Elem.div [ cls "bracket-finals" ] [
            Elem.p [ cls "bracket-finals-label" ] [
                Text.raw "Finals"
            ]
            Elem.div [] [
                match finalSeries with
                | [] -> tbdSeriesBox ()
                | series ->
                    for s in series do
                        seriesBox sport s
            ]
        ]

    let render (bracket: PlayoffBracketData) =
        if bracket.Rounds.IsEmpty then []
        else

        let sport = sportFromString bracket.Sport

        let roundByNumber n =
            bracket.Rounds |> List.tryFind (fun r -> r.Number = n)

        let filterConference (conf: string) (series: PlayoffSeriesData list) =
            series |> List.filter (fun s ->
                s.Conference.ToUpperInvariant().Contains(conf.ToUpperInvariant()))

        let r1 = roundByNumber 1
        let r2 = roundByNumber 2
        let r3 = roundByNumber 3
        let r4 = roundByNumber 4

        let westR1 = r1 |> Option.map (fun r -> filterConference "Western" r.Series) |> Option.defaultValue []
        let eastR1 = r1 |> Option.map (fun r -> filterConference "Eastern" r.Series) |> Option.defaultValue []
        let westR2 = r2 |> Option.map (fun r -> filterConference "Western" r.Series) |> Option.defaultValue []
        let eastR2 = r2 |> Option.map (fun r -> filterConference "Eastern" r.Series) |> Option.defaultValue []
        let westR3 = r3 |> Option.map (fun r -> filterConference "Western" r.Series) |> Option.defaultValue []
        let eastR3 = r3 |> Option.map (fun r -> filterConference "Eastern" r.Series) |> Option.defaultValue []
        let finals = r4 |> Option.map (fun r -> r.Series) |> Option.defaultValue []

        let r1Label = r1 |> Option.map (fun r -> r.Label) |> Option.defaultValue "First Round"
        let r2Label = r2 |> Option.map (fun r -> r.Label) |> Option.defaultValue "Conference Semifinals"
        let r3Label = r3 |> Option.map (fun r -> r.Label) |> Option.defaultValue "Conference Finals"

        [
            Elem.div [ cls "panel" ] [
                Elem.h2 [ cls "bracket-title" ] [
                    Text.raw $"{bracket.SeasonYear} Playoff Bracket"
                ]
                Elem.div [ cls "bracket-scroll" ] [
                    Elem.div [ cls "bracket-rounds" ] [
                        roundColumn r1Label sport westR1 4
                        roundColumn r2Label sport westR2 2
                        roundColumn r3Label sport westR3 1

                        finalsColumn sport finals

                        roundColumn r3Label sport eastR3 1
                        roundColumn r2Label sport eastR2 2
                        roundColumn r1Label sport eastR1 4
                    ]
                ]
                Elem.div [ cls "bracket-legend" ] [
                    Elem.span [ cls "bracket-legend-label" ] [ Text.raw "Western Conference →" ]
                    Elem.span [ cls "bracket-legend-label" ] [ Text.raw "← Eastern Conference" ]
                ]
            ]
        ]
