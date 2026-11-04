namespace Backend.Rendering

open System
open System.Collections.Generic
open Falco.Markup
open backend.GraphQL.Types
open backend.Models
open backend.Services

module LeaguePageRenderer =

    let private cls = Layout.cls

    let private gameRow (game: Game) (sport: Sport) (odds: IDictionary<string, OddsSnapshot>) (scores: IDictionary<string, Nullable<float>>) (hypeScores: IDictionary<string, Nullable<float>>) (teamRanks: IDictionary<string, TeamRankInfo>) (hypeCredits: IDictionary<string, DerivedHypeCredit>) =
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

        let colors = TeamColors.forGame game

        let homeLogoUrl = if game.Sport = "cfb" then CollegeFootballPolicy.Logo(game.HomeTeamId) |> Option.ofObj else Logos.getTeamLogoUrl sport game.HomeTeamAbbreviation
        let awayLogoUrl = if game.Sport = "cfb" then CollegeFootballPolicy.Logo(game.AwayTeamId) |> Option.ofObj else Logos.getTeamLogoUrl sport game.AwayTeamAbbreviation

        let homeWinProb = latestOdds |> Option.map (fun o -> o.HomeWinProbability) |> Option.defaultValue 0.5
        let liveClass = if isLive then " border-status-live/30 bg-status-live/5" else ""

        let card = Elem.a [ Attr.href $"/game/{game.Id}"; cls $"block bg-bg-card border border-border rounded-lg p-4 hover:border-border-hover transition-colors group{liveClass}" ] [
            Elem.div [ cls (if game.Sport = "cfb" then "flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between" else "flex items-center justify-between") ] [
                Elem.div [ cls "flex items-center gap-3 min-w-0 flex-1"; Attr.id $"scores-{game.Id}" ] [
                    Elem.div [ cls "flex items-center gap-2 min-w-0" ] [
                        match awayLogoUrl with
                        | Some url -> Elem.img [ Attr.src url; Attr.alt game.AwayTeamAbbreviation; cls "w-6 h-6 flex-shrink-0 rounded-full ring-1 ring-white/20" ]
                        | None -> ()
                        Elem.div [ cls "flex flex-col min-w-0" ] [
                            Elem.span [ cls "font-medium text-text-primary truncate group-hover:text-accent transition-colors" ] [
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
                        Elem.span [ cls "text-lg font-semibold tabular-nums text-text-primary"; Attr.id $"away-score-{game.Id}" ] [
                            if game.AwayScore.HasValue then Text.raw $"{game.AwayScore.Value}"
                        ]
                    ]
                    Elem.span [ cls "text-text-muted text-xs mx-2" ] [ Text.raw "@" ]
                    Elem.div [ cls "flex items-center gap-2 min-w-0" ] [
                        match homeLogoUrl with
                        | Some url -> Elem.img [ Attr.src url; Attr.alt game.HomeTeamAbbreviation; cls "w-6 h-6 flex-shrink-0 rounded-full ring-1 ring-white/20" ]
                        | None -> ()
                        Elem.div [ cls "flex flex-col min-w-0" ] [
                            Elem.span [ cls "font-medium text-text-primary truncate group-hover:text-accent transition-colors" ] [
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
                        Elem.span [ cls "text-lg font-semibold tabular-nums text-text-primary"; Attr.id $"home-score-{game.Id}" ] [
                            if game.HomeScore.HasValue then Text.raw $"{game.HomeScore.Value}"
                        ]
                    ]
                ]
                Elem.div [ cls (if game.Sport = "cfb" then "flex items-center justify-between gap-3 sm:flex-shrink-0 sm:ml-4" else "flex items-center gap-3 flex-shrink-0 ml-4") ] [
                    Elem.span [ cls $"text-xs font-medium px-1.5 py-0.5 rounded {statusClass}"; Attr.id $"status-{game.Id}" ] [
                        Text.raw statusLabel
                    ]
                    Elem.div [ Attr.id $"excitement-{game.Id}" ] [
                        if (game.Sport = "cfb" || backend.Scoring.ProHypePolicy.Supports(game.Sport)) && game.Status = "scheduled" && not hypeScore.HasValue then
                            Elem.span [ cls "text-xs text-text-muted" ] [ Text.raw "Hype unavailable" ]
                        elif game.Sport = "cfb" && (isLive || game.Status = "final") && not excitementScore.HasValue then
                            Elem.span [ cls "text-xs text-text-muted" ] [ Text.raw "Excitement unavailable" ]
                        if excitementScore.HasValue && (isLive || game.Status = "final") then
                            let level = FormatHelpers.getExcitementLevel excitementScore
                            let levelClass = FormatHelpers.excitementLevelClass level
                            Elem.div [ cls "text-right" ] [
                                Elem.span [ cls "block text-xs text-text-muted mb-0.5" ] [ Text.raw "Excitement" ]
                                Elem.span [ cls $"text-xl font-semibold tabular-nums {levelClass}" ] [
                                    Text.raw $"{Math.Round(excitementScore.Value)}"
                                ]
                            ]
                        if hypeScore.HasValue && game.Status = "scheduled" then
                            let level = FormatHelpers.getHypeLevel hypeScore
                            let levelClass = FormatHelpers.hypeLevelClass level
                            Elem.div [ cls "text-right" ] [
                                Elem.span [ cls "block text-xs text-text-muted mb-0.5" ] [ Text.raw "Hype" ]
                                Elem.span [ cls $"text-xl font-semibold tabular-nums {levelClass}" ] [
                                    Text.raw $"{Math.Round(hypeScore.Value)}"
                                ]
                            ]
                    ]
                    if game.Status = "scheduled" || game.Status = "final" then
                        Elem.span [ cls "text-text-muted text-sm" ] [
                            FormatHelpers.scheduledTimeNode game.ScheduledStart
                        ]
                    Elem.span [ cls "text-text-muted group-hover:text-text-primary transition-colors" ] [
                        Text.raw "→"
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
            if isLive || game.Status = "final" then
                Elem.div [ cls "mt-2" ] [
                    Elem.create "nb-sparkline" [
                        Attr.create "game-id" game.Id
                        Attr.create "data-live-final" (if game.Status = "final" then "true" else "false")
                        Attr.create "color" colors.Home
                        cls "block h-8"
                    ] []
                ]
        ]
        let credits = QualificationCredit.renderForGame game teamRanks hypeCredits
        if credits.IsEmpty then card
        else Elem.div [] [ card; yield! credits ]

    let RenderWithHypeCredits (sport: Sport) (config: LeagueConfig.LeagueConfig) (games: Collections.Generic.List<Game>) (odds: IDictionary<string, OddsSnapshot>) (scores: IDictionary<string, Nullable<float>>) (hypeScores: IDictionary<string, Nullable<float>>) (weekInfo: CurrentWeekData option) (teamRanks: IDictionary<string, TeamRankInfo>) (bracket: PlayoffBracketData option) (prevWeekUrl: string option) (nextWeekUrl: string option) (hypeCredits: IDictionary<string, DerivedHypeCredit>) =
        let usesWeeks = sport = Sport.NFL || sport = Sport.CFB
        let navPrevLabel = if usesWeeks then "← Previous Week" else "← Previous Day"
        let navNextLabel = if usesWeeks then "Next Week →" else "Next Day →"
        [
            Elem.div [ cls "max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8" ] [
                Elem.div [ cls "mb-8" ] [
                    Elem.h1 [ cls "text-2xl font-semibold mb-1 flex items-center gap-2" ] [
                        Elem.span [] [ Text.raw config.Meta.Emoji ]
                        Elem.span [] [ Text.raw config.Name ]
                    ]
                    match weekInfo with
                    | Some wi ->
                        Elem.p [ cls "text-text-muted text-sm" ] [
                            Text.raw (if String.IsNullOrEmpty wi.WeekLabel then $"{wi.SeasonYear} Season" else $"{wi.SeasonYear} {wi.WeekLabel}")
                        ]
                    | None ->
                        Elem.p [ cls "text-text-muted text-sm" ] [ Text.raw "No current season data" ]
                    match prevWeekUrl, nextWeekUrl with
                    | None, None -> ()
                    | _ ->
                        Elem.div [ cls "flex items-center gap-4 mt-2" ] [
                            match prevWeekUrl with
                            | Some url ->
                                Elem.a [ Attr.href url; cls "text-sm text-text-muted hover:text-text-primary transition-colors" ] [
                                    Text.raw navPrevLabel
                                ]
                            | None -> ()
                            match nextWeekUrl with
                            | Some url ->
                                Elem.a [ Attr.href url; cls "text-sm text-text-muted hover:text-text-primary transition-colors ml-auto" ] [
                                    Text.raw navNextLabel
                                ]
                            | None -> ()
                        ]
                ]
                match bracket with
                | Some bracketData ->
                    Elem.div [ cls "overflow-x-auto" ] [
                        yield! PlayoffBracketRenderer.render bracketData
                    ]
                | None -> ()
                if games.Count = 0 then
                    Elem.div [ cls "bg-bg-card border border-border rounded-lg p-12 text-center" ] [
                        Elem.p [ cls "text-text-muted" ] [ Text.raw $"No {config.ShortName} games found." ]
                    ]
                else
                    let sportStr = sport.ToString().ToLowerInvariant()
                    Elem.div [ cls "space-y-2"; Attr.create "data-init" $"@get('/sse/live/{sportStr}', {{requestCancellation: 'cleanup'}})" ] [
                        for game in games do
                            gameRow game sport odds scores hypeScores teamRanks hypeCredits
                    ]
                yield! QualificationCredit.renderQualityForGames games hypeScores hypeCredits
            ]
        ]

    let Render sport config games odds scores hypeScores weekInfo teamRanks bracket prevWeekUrl nextWeekUrl =
        RenderWithHypeCredits sport config games odds scores hypeScores weekInfo teamRanks bracket prevWeekUrl nextWeekUrl (dict [])
