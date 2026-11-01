namespace Backend.Rendering

open System
open System.Collections.Generic
open Falco.Markup
open backend.GraphQL.Types
open backend.Models
open backend.Services

module internal QualificationCredit =
    let selectedTeams (game: Game) (ranks: IDictionary<string, TeamRankInfo>) =
        if game.IsHistoricalImport || game.SeasonType = Nullable 3 then []
        else
            [ game.AwayTeamAbbreviation; game.HomeTeamAbbreviation ]
            |> List.filter (fun team ->
                match ranks.TryGetValue($"{game.Sport}:{PlayoffRatingPolicy.gameSeason game}:{game.SeasonType.GetValueOrDefault()}:{team}") with
                | true, rank -> rank.PlayoffOdds.IsSome && rank.PlayoffSource = PlayoffRatingPolicy.moneyPuckQualificationSource
                                && rank.PlayoffEvidenceKind = backend.Scoring.ProQualificationEvidenceKind.MoneyPuckModel
                | _ -> false)

    let render id (teams: string list) derivedHype =
        let plural = teams.Length > 1
        Elem.p [ Layout.cls "text-xs text-text-muted mt-2 px-4"; Attr.id id ] [
            Text.enc (String.concat " / " teams + (if plural then " playoff probabilities: " else " playoff probability: "))
            Elem.a [ Attr.href "https://moneypuck.com/predictions.htm"; Layout.cls "underline hover:text-text-primary" ] [ Text.raw "MoneyPuck" ]
            Text.raw (if plural then " model estimates." else " model estimate.")
            if derivedHype then Text.raw (if plural then " Hype uses these estimates." else " Hype uses this estimate.")
        ]

module HomePageRenderer =

    let private cls = Layout.cls

    let private seriesSummary (game: Game) (series: PlayoffSeriesSnapshot) =
        let standing =
            if series.HomeWins = series.AwayWins then $"Series tied {series.HomeWins}–{series.AwayWins}"
            else
                let leader, wins, losses =
                    if series.HomeWins > series.AwayWins then game.HomeTeamAbbreviation, series.HomeWins, series.AwayWins
                    else game.AwayTeamAbbreviation, series.AwayWins, series.HomeWins
                let verb = if series.SeriesCompleted then "wins series" else "leads"
                $"{leader} {verb} {wins}–{losses}"
        let context =
            [ if not (String.IsNullOrWhiteSpace series.RoundLabel) then Text.enc series.RoundLabel
              if series.CurrentGameNumber.HasValue then
                  Elem.span [ cls "whitespace-nowrap" ] [ Text.enc $"Game {series.CurrentGameNumber.Value}" ]
              Elem.span [ cls "whitespace-nowrap" ] [ Text.enc $"Best of {series.BestOf}" ] ]
            |> List.mapi (fun index part -> if index = 0 then [ part ] else [ Text.enc " · "; part ])
            |> List.concat
        Elem.div [ cls "min-w-0"; Attr.id $"series-{game.Id}" ] [
            Elem.div [ cls "text-sm font-medium text-text-primary" ] [ Text.enc standing ]
            Elem.div [ cls "text-xs text-text-secondary/70 mt-0.5" ] context
        ]

    let private gameCard (game: Game) (odds: IDictionary<string, OddsSnapshot>) (scores: IDictionary<string, Nullable<float>>) (hypeScores: IDictionary<string, Nullable<float>>) (teamRanks: IDictionary<string, TeamRankInfo>) =
        let sport = SportExtensions.FromDbValue(game.Sport)
        let status = GameStatusExtensions.FromDbValue(game.Status)
        let excitementScore =
            match scores.TryGetValue(game.Id) with
            | true, s -> s
            | _ -> Nullable<float>()
        let hypeScore =
            match hypeScores.TryGetValue(game.Id) with
            | true, h -> h
            | _ -> Nullable<float>()
        let latestOdds =
            match odds.TryGetValue(game.Id) with
            | true, o -> Some o
            | _ -> None

        let statusLabel = if status.HasValue then FormatHelpers.getStatusLabel status.Value else ""
        let statusClass = if status.HasValue then FormatHelpers.getStatusClass status.Value else ""
        let isLive = game.Status = "in_progress"
        let series = PlayoffSeriesObservation.TryReadFor(game) |> Option.ofObj

        let colors = TeamColors.forGame game

        let homeLogoUrl = if game.Sport = "cfb" then CollegeFootballPolicy.Logo(game.HomeTeamId) |> Option.ofObj elif sport.HasValue then Logos.getTeamLogoUrl sport.Value game.HomeTeamAbbreviation else None
        let awayLogoUrl = if game.Sport = "cfb" then CollegeFootballPolicy.Logo(game.AwayTeamId) |> Option.ofObj elif sport.HasValue then Logos.getTeamLogoUrl sport.Value game.AwayTeamAbbreviation else None

        let homeWinProb = latestOdds |> Option.map (fun o -> o.HomeWinProbability) |> Option.defaultValue 0.5

        let liveClass = if isLive then " border-status-live/30" else ""

        let card = Elem.a [ Attr.href $"/game/{game.Id}"; cls $"block bg-bg-card border border-border rounded-lg p-4 hover:border-border-hover transition-colors group{liveClass}" ] [
            Elem.div [ cls "flex items-center justify-between mb-3" ] [
                Elem.div [ cls "flex items-center gap-2" ] [
                    match sport with
                    | s when s.HasValue ->
                        Elem.img [ Attr.src (Logos.getLeagueLogoUrl s.Value); Attr.alt (s.Value.ToString()); cls "w-4 h-4" ]
                    | _ -> ()
                    Elem.span [ cls $"text-xs font-medium px-1.5 py-0.5 rounded {statusClass}"; Attr.id $"status-{game.Id}" ] [
                        Text.raw statusLabel
                    ]
                ]
                if game.Status = "scheduled" || game.Status = "final" then
                    Elem.span [ cls "text-sm text-text-muted" ] [
                        FormatHelpers.scheduledTimeNode game.ScheduledStart
                    ]
            ]
            Elem.div [ cls "space-y-2"; Attr.id $"scores-{game.Id}" ] [
                Elem.div [ cls "flex items-center justify-between" ] [
                    Elem.div [ cls "flex items-center gap-2" ] [
                        match awayLogoUrl with
                        | Some url -> Elem.img [ Attr.src url; Attr.alt game.AwayTeamAbbreviation; cls "w-6 h-6 rounded-full ring-1 ring-white/20" ]
                        | None -> ()
                        Elem.div [ cls "flex flex-col" ] [
                            Elem.span [ cls "font-medium text-text-primary" ] [
                                Text.enc game.AwayTeamAbbreviation
                                if game.Sport = "cfb" then yield! FormatHelpers.apRank game.AwayApRank
                            ]
                            match teamRanks.TryGetValue($"{game.Sport}:{PlayoffRatingPolicy.gameSeason game}:{game.SeasonType.GetValueOrDefault()}:{game.AwayTeamAbbreviation}") with
                            | true, rank ->
                                match rank.DisplayRank with
                                | Some text -> Elem.span [ cls "text-xs text-text-muted/70" ] [ Text.enc text ]
                                | None -> ()
                            | _ -> ()
                        ]
                    ]
                    if game.Sport = "cfb" || game.AwayScore.HasValue then
                        Elem.span [ cls "text-lg font-semibold tabular-nums text-text-primary"; Attr.id $"away-score-{game.Id}" ] [
                            if game.AwayScore.HasValue then Text.raw $"{game.AwayScore.Value}"
                        ]
                ]
                Elem.div [ cls "flex items-center justify-between" ] [
                    Elem.div [ cls "flex items-center gap-2" ] [
                        match homeLogoUrl with
                        | Some url -> Elem.img [ Attr.src url; Attr.alt game.HomeTeamAbbreviation; cls "w-6 h-6 rounded-full ring-1 ring-white/20" ]
                        | None -> ()
                        Elem.div [ cls "flex flex-col" ] [
                            Elem.span [ cls "font-medium text-text-primary" ] [
                                Text.enc game.HomeTeamAbbreviation
                                if game.Sport = "cfb" then yield! FormatHelpers.apRank game.HomeApRank
                            ]
                            match teamRanks.TryGetValue($"{game.Sport}:{PlayoffRatingPolicy.gameSeason game}:{game.SeasonType.GetValueOrDefault()}:{game.HomeTeamAbbreviation}") with
                            | true, rank ->
                                match rank.DisplayRank with
                                | Some text -> Elem.span [ cls "text-xs text-text-muted/70" ] [ Text.enc text ]
                                | None -> ()
                            | _ -> ()
                        ]
                    ]
                    if game.Sport = "cfb" || game.HomeScore.HasValue then
                        Elem.span [ cls "text-lg font-semibold tabular-nums text-text-primary"; Attr.id $"home-score-{game.Id}" ] [
                            if game.HomeScore.HasValue then Text.raw $"{game.HomeScore.Value}"
                        ]
                ]
            ]
            yield! FormatHelpers.collegeContext game
            Elem.div [ Attr.id $"probbar-{game.Id}" ] [
                if game.Sport = "cfb" && latestOdds.IsNone && isLive then
                    Elem.span [ cls "text-xs text-text-muted" ] [ Text.raw "Live probability unavailable" ]
                if latestOdds.IsSome && game.Status <> "final" then
                    Elem.div [ cls "mt-3" ] [
                        ProbabilityBar.render true game.HomeTeamAbbreviation game.AwayTeamAbbreviation homeWinProb (1.0 - homeWinProb) colors
                    ]
            ]
            Elem.div [ Attr.id $"clock-{game.Id}" ] [
                if game.Sport = "cfb" && isLive then
                    Elem.div [ cls "mt-2 text-sm text-status-live font-medium text-center" ] [
                        Text.enc (FormatHelpers.formatGameClock game.Period game.GameClock game.Sport)
                    ]
                elif isLive && latestOdds.IsSome then
                    let odds = latestOdds.Value
                    let clockText = FormatHelpers.formatGameClock odds.Period odds.GameClock game.Sport
                    if clockText <> "" then
                        Elem.div [ cls "mt-2 text-sm text-status-live font-medium text-center" ] [
                            Text.raw clockText
                        ]
            ]
            if game.Status = "scheduled" && series.IsSome then
                Elem.div [ cls "flex justify-between items-end gap-3 pt-2 border-t border-border" ] [
                    seriesSummary game series.Value
                    Elem.div [ cls "shrink-0 text-right"; Attr.id $"excitement-{game.Id}" ] [
                        if hypeScore.HasValue then
                            let level = FormatHelpers.getHypeLevel hypeScore
                            let levelClass = FormatHelpers.hypeLevelClass level
                            Elem.span [ cls "block text-xs text-text-muted mb-0.5" ] [ Text.raw "Hype" ]
                            Elem.span [ cls $"text-xl font-semibold tabular-nums {levelClass}" ] [ Text.raw $"{Math.Round(hypeScore.Value)}" ]
                        else
                            Elem.span [ cls "text-xs text-text-muted" ] [ Text.raw "Hype unavailable" ]
                    ]
                ]
            if series.IsNone && (game.Sport = "cfb" || backend.Scoring.ProHypePolicy.Supports(game.Sport)) && game.Status = "scheduled" && not hypeScore.HasValue then
                Elem.div [ cls "text-xs text-text-muted"; Attr.id $"excitement-{game.Id}" ] [ Text.raw "Hype unavailable" ]
            if series.IsNone && game.Status = "scheduled" && hypeScore.HasValue then
                Elem.div [ cls "pt-2 border-t border-border text-right" ] [
                    Elem.div [ Attr.id $"excitement-{game.Id}" ] [
                        let level = FormatHelpers.getHypeLevel hypeScore
                        let levelClass = FormatHelpers.hypeLevelClass level
                        Elem.div [ cls "text-right" ] [
                            Elem.span [ cls "block text-xs text-text-muted mb-0.5" ] [ Text.raw "Hype" ]
                            Elem.span [ cls $"text-xl font-semibold tabular-nums {levelClass}" ] [
                                Text.raw $"{Math.Round(hypeScore.Value)}"
                            ]
                        ]
                    ]
                ]
            if isLive || game.Status = "final" then
                Elem.div [ cls ("flex justify-between items-end pt-2 border-t border-border" + (if series.IsSome then " gap-3" else "")) ] [
                    Elem.div [ cls (if series.IsSome then "min-w-0 flex-1" else "mt-2") ] [
                        if series.IsSome then seriesSummary game series.Value
                        let sparkline =
                            Elem.create "nb-sparkline" [
                                Attr.create "game-id" game.Id
                                Attr.create "data-live-final" (if game.Status = "final" then "true" else "false")
                                Attr.create "color" colors.Home
                                cls "block h-8"
                            ] []
                        if series.IsSome then Elem.div [ cls "mt-2" ] [ sparkline ]
                        else sparkline
                    ]
                    Elem.div [ yield Attr.id $"excitement-{game.Id}"
                               if series.IsSome then yield cls "shrink-0 text-right" ] [
                        if game.Sport = "cfb" && not excitementScore.HasValue then
                            Elem.span [ cls "text-xs text-text-muted" ] [ Text.raw "Excitement unavailable" ]
                        if excitementScore.HasValue then
                            let level = FormatHelpers.getExcitementLevel excitementScore
                            let levelClass = FormatHelpers.excitementLevelClass level
                            Elem.div [ cls "text-right" ] [
                                Elem.span [ cls "block text-xs text-text-muted mb-0.5" ] [ Text.raw "Excitement" ]
                                Elem.span [ cls $"text-xl font-semibold tabular-nums {levelClass}" ] [
                                    Text.raw $"{Math.Round(excitementScore.Value)}"
                                ]
                            ]
                    ]
                ]
        ]
        let creditedTeams = QualificationCredit.selectedTeams game teamRanks
        if creditedTeams.IsEmpty then card
        else Elem.div [] [ card; QualificationCredit.render $"qualification-credit-{game.Id}" creditedTeams (game.Status = "scheduled" && game.SeasonType = Nullable 2 && hypeScore.HasValue) ]

    let Render (games: Collections.Generic.List<Game>) (odds: IDictionary<string, OddsSnapshot>) (scores: IDictionary<string, Nullable<float>>) (hypeScores: IDictionary<string, Nullable<float>>) (teamRanks: IDictionary<string, TeamRankInfo>) =
        [
            Elem.div [ cls "max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8" ] [
                Elem.div [ cls "mb-8" ] [
                    Elem.h1 [ cls "text-2xl font-semibold mb-1" ] [ Text.raw "Today's Games" ]
                    Elem.p [ cls "text-text-muted text-sm" ] [ Text.raw "Live scores, win probabilities, and excitement scores" ]
                ]
                if games.Count = 0 then
                    Elem.div [ cls "bg-bg-card border border-border rounded-lg p-12 text-center" ] [
                        Elem.p [ cls "text-text-muted" ] [ Text.raw "No games scheduled for today." ]
                    ]
                else
                    Elem.section [] [
                        Elem.div [ cls "grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4"; Attr.create "data-init" "@get('/sse/live')" ] [
                            for game in games do
                                gameCard game odds scores hypeScores teamRanks
                        ]
                    ]
            ]
        ]
