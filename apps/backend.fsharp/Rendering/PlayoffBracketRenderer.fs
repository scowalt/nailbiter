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
            if isWinner then "flex items-center gap-1.5 py-1 px-1.5 rounded font-semibold"
            elif isEliminated then "flex items-center gap-1.5 py-1 px-1.5 rounded opacity-40"
            else "flex items-center gap-1.5 py-1 px-1.5 rounded"
        Elem.div [ cls rowClass ] [
            Elem.span [ cls "text-xs text-text-muted w-4 flex-shrink-0 tabular-nums" ] [
                Text.raw (if seed > 0 then $"{seed}" else "")
            ]
            match logoUrl with
            | Some url ->
                Elem.img [
                    Attr.src url
                    Attr.alt team
                    cls "w-5 h-5 flex-shrink-0"
                    Attr.create "loading" "lazy"
                ]
            | None ->
                Elem.div [ cls "w-5 h-5 flex-shrink-0 rounded-full bg-bg-card-hover" ] []
            Elem.span [
                cls "text-xs font-medium truncate flex-1"
                Attr.style $"color: {teamColor}"
            ] [
                Text.enc team
            ]
            Elem.span [ cls "text-sm font-semibold ml-auto tabular-nums text-text-primary flex-shrink-0" ] [
                Text.raw $"{wins}"
            ]
        ]

    let private seriesBox (sport: Nullable<Sport>) (series: PlayoffSeriesData) =
        let higherIsWinner = series.Winner = Some series.HigherSeedTeam
        let lowerIsWinner = series.Winner = Some series.LowerSeedTeam
        let higherEliminated = series.Completed && not higherIsWinner
        let lowerEliminated = series.Completed && not lowerIsWinner

        Elem.div [ cls "bg-bg-card border border-border rounded-lg p-1.5 mb-2 min-w-[130px]" ] [
            teamRow sport series.HigherSeedTeam series.HigherSeedNumber series.HigherSeedWins higherIsWinner higherEliminated
            Elem.div [ cls "border-t border-border/50 mx-1" ] []
            teamRow sport series.LowerSeedTeam series.LowerSeedNumber series.LowerSeedWins lowerIsWinner lowerEliminated
        ]

    let private tbdSeriesBox () =
        Elem.div [ cls "bg-bg-card border border-border border-dashed rounded-lg p-1.5 mb-2 min-w-[130px]" ] [
            Elem.div [ cls "flex items-center gap-1.5 py-1 px-1.5" ] [
                Elem.div [ cls "w-5 h-5 flex-shrink-0 rounded-full bg-bg-card-hover" ] []
                Elem.span [ cls "text-xs text-text-muted/50" ] [ Text.raw "TBD" ]
            ]
            Elem.div [ cls "border-t border-border/50 mx-1" ] []
            Elem.div [ cls "flex items-center gap-1.5 py-1 px-1.5" ] [
                Elem.div [ cls "w-5 h-5 flex-shrink-0 rounded-full bg-bg-card-hover" ] []
                Elem.span [ cls "text-xs text-text-muted/50" ] [ Text.raw "TBD" ]
            ]
        ]

    let private roundColumn (label: string) (sport: Nullable<Sport>) (series: PlayoffSeriesData list) (expectedCount: int) =
        let seriesCount = series.Length
        let placeholderCount = max 0 (expectedCount - seriesCount)
        Elem.div [ cls "flex flex-col justify-around flex-1 min-w-[140px]" ] [
            Elem.p [ cls "text-xs text-text-muted text-center mb-2 font-medium whitespace-nowrap" ] [
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
        Elem.div [ cls "flex flex-col justify-center flex-1 min-w-[140px]" ] [
            Elem.p [ cls "text-xs text-text-muted text-center mb-2 font-medium" ] [
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
            Elem.div [ cls "bg-bg-card border border-border rounded-xl p-6 mb-6" ] [
                Elem.h2 [ cls "text-lg font-semibold mb-4 text-text-primary" ] [
                    Text.raw $"{bracket.SeasonYear} Playoff Bracket"
                ]
                Elem.div [ cls "overflow-x-auto" ] [
                    Elem.div [ cls "flex gap-2 min-w-[700px]" ] [
                        roundColumn r1Label sport westR1 4
                        roundColumn r2Label sport westR2 2
                        roundColumn r3Label sport westR3 1

                        finalsColumn sport finals

                        roundColumn r3Label sport eastR3 1
                        roundColumn r2Label sport eastR2 2
                        roundColumn r1Label sport eastR1 4
                    ]
                ]
                Elem.div [ cls "flex justify-between mt-3 px-1" ] [
                    Elem.span [ cls "text-xs text-text-muted/60 font-medium" ] [ Text.raw "Western Conference →" ]
                    Elem.span [ cls "text-xs text-text-muted/60 font-medium" ] [ Text.raw "← Eastern Conference" ]
                ]
            ]
        ]
