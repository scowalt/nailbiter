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
        let liveClass = if isLive then " is-live" else ""
        let displayOverride team =
            match teamRanks.TryGetValue($"{game.Sport}:{PlayoffRatingPolicy.gameSeason game}:{game.SeasonType.GetValueOrDefault()}:{team}") with
            | true, rank -> rank.PlayoffDisplayOverride.IsSome
            | _ -> false
        let responsiveRows = game.Sport = "cfb" || (game.Sport = "nfl" && (displayOverride game.HomeTeamAbbreviation || displayOverride game.AwayTeamAbbreviation))

        let card = Elem.a [ Attr.href $"/game/{game.Id}"; cls $"league-game{liveClass}" ] [
            Elem.div [ cls (if responsiveRows then "league-game-body-stacked" else "league-game-body") ] [
                Elem.div [ cls "league-matchup"; Attr.id $"scores-{game.Id}" ] [
                    Elem.div [ cls "league-team" ] [
                        match awayLogoUrl with
                        | Some url -> Elem.img [ Attr.src url; Attr.alt game.AwayTeamAbbreviation; cls "league-team-logo" ]
                        | None -> ()
                        Elem.div [ cls "league-team-label" ] [
                            Elem.span [ cls "league-team-name" ] [
                                Text.enc game.AwayTeamAbbreviation
                                if game.Sport = "cfb" then yield! FormatHelpers.apRank game.AwayApRank
                            ]
                            match teamRanks.TryGetValue($"{game.Sport}:{PlayoffRatingPolicy.gameSeason game}:{game.SeasonType.GetValueOrDefault()}:{game.AwayTeamAbbreviation}") with
                            | true, rank ->
                                match rank.DisplayRank with
                                | Some text -> Elem.span [ cls (if rank.PlayoffDisplayOverride.IsSome then "team-rank-override" else "team-rank"); yield! FormatHelpers.playoffDisplayConventionAttributes rank.PlayoffDisplayOverride.IsSome ] [ Text.enc text ]
                                | None -> ()
                            | _ -> ()
                        ]
                        Elem.span [ cls "team-score"; Attr.id $"away-score-{game.Id}" ] [
                            if game.AwayScore.HasValue then Text.raw $"{game.AwayScore.Value}"
                        ]
                    ]
                    Elem.span [ cls "league-at" ] [ Text.raw "@" ]
                    Elem.div [ cls "league-team" ] [
                        match homeLogoUrl with
                        | Some url -> Elem.img [ Attr.src url; Attr.alt game.HomeTeamAbbreviation; cls "league-team-logo" ]
                        | None -> ()
                        Elem.div [ cls "league-team-label" ] [
                            Elem.span [ cls "league-team-name" ] [
                                Text.enc game.HomeTeamAbbreviation
                                if game.Sport = "cfb" then yield! FormatHelpers.apRank game.HomeApRank
                            ]
                            match teamRanks.TryGetValue($"{game.Sport}:{PlayoffRatingPolicy.gameSeason game}:{game.SeasonType.GetValueOrDefault()}:{game.HomeTeamAbbreviation}") with
                            | true, rank ->
                                match rank.DisplayRank with
                                | Some text -> Elem.span [ cls (if rank.PlayoffDisplayOverride.IsSome then "team-rank-override" else "team-rank"); yield! FormatHelpers.playoffDisplayConventionAttributes rank.PlayoffDisplayOverride.IsSome ] [ Text.enc text ]
                                | None -> ()
                            | _ -> ()
                        ]
                        Elem.span [ cls "team-score"; Attr.id $"home-score-{game.Id}" ] [
                            if game.HomeScore.HasValue then Text.raw $"{game.HomeScore.Value}"
                        ]
                    ]
                ]
                Elem.div [ cls (if responsiveRows then "league-game-aside-stacked" else "league-game-aside") ] [
                    Elem.span [ cls $"status-badge {statusClass}"; Attr.id $"status-{game.Id}" ] [
                        Text.raw statusLabel
                    ]
                    Elem.div [ Attr.id $"excitement-{game.Id}" ] [
                        if (game.Sport = "cfb" || backend.Scoring.ProHypePolicy.Supports(game.Sport)) && game.Status = "scheduled" && not hypeScore.HasValue then
                            Elem.span [ cls "unavailable-note" ] [ Text.raw "Hype unavailable" ]
                        elif game.Sport = "cfb" && (isLive || game.Status = "final") && not excitementScore.HasValue then
                            Elem.span [ cls "unavailable-note" ] [ Text.raw "Excitement unavailable" ]
                        if excitementScore.HasValue && (isLive || game.Status = "final") then
                            let level = FormatHelpers.getExcitementLevel excitementScore
                            let levelClass = FormatHelpers.excitementLevelClass level
                            Elem.div [ cls "metric-block" ] [
                                Elem.span [ cls "metric-label" ] [ Text.raw "Excitement" ]
                                Elem.span [ cls $"metric-value {levelClass}" ] [
                                    Text.raw $"{Math.Round(excitementScore.Value)}"
                                ]
                            ]
                        if hypeScore.HasValue && game.Status = "scheduled" then
                            let level = FormatHelpers.getHypeLevel hypeScore
                            let levelClass = FormatHelpers.hypeLevelClass level
                            Elem.div [ cls "metric-block" ] [
                                Elem.span [ cls "metric-label" ] [ Text.raw "Hype" ]
                                Elem.span [ cls $"metric-value {levelClass}" ] [
                                    Text.raw $"{Math.Round(hypeScore.Value)}"
                                ]
                            ]
                    ]
                    if game.Status = "scheduled" || game.Status = "final" then
                        Elem.span [ cls "meta-text" ] [
                            FormatHelpers.scheduledTimeNode game.ScheduledStart
                            yield! FormatHelpers.broadcastNodes game
                        ]
                    elif isLive then
                        match FormatHelpers.broadcastNodes game with
                        | [] -> ()
                        | nodes -> Elem.span [ cls "meta-text" ] (List.tail nodes)
                    Elem.span [ cls "league-game-arrow" ] [
                        Text.raw "→"
                    ]
                ]
            ]
            yield! FormatHelpers.collegeContext game
            Elem.div [ Attr.id $"probbar-{game.Id}" ] [
                if game.Sport = "cfb" && latestOdds.IsNone && isLive then
                    Elem.span [ cls "unavailable-note" ] [ Text.raw "Live probability unavailable" ]
                if latestOdds.IsSome && game.Status <> "final" then
                    Elem.div [ cls "card-probability" ] [
                        ProbabilityBar.render true game.HomeTeamAbbreviation game.AwayTeamAbbreviation homeWinProb (1.0 - homeWinProb) colors
                    ]
            ]
            Elem.div [ Attr.id $"clock-{game.Id}" ] [
                if game.Sport = "cfb" && isLive then
                    Elem.div [ cls "live-clock" ] [
                        Text.enc (FormatHelpers.formatGameClock game.Period game.GameClock game.Sport)
                    ]
                elif isLive && latestOdds.IsSome then
                    let odds = latestOdds.Value
                    let clockText = FormatHelpers.formatGameClock odds.Period odds.GameClock game.Sport
                    if clockText <> "" then
                        Elem.div [ cls "live-clock" ] [
                            Text.raw clockText
                        ]
            ]
            if isLive || game.Status = "final" then
                Elem.div [ cls "metric-trend" ] [
                    Elem.create "nb-sparkline" [
                        Attr.create "game-id" game.Id
                        Attr.create "data-live-final" (if game.Status = "final" then "true" else "false")
                        Attr.create "color" colors.Home
                        cls "card-sparkline"
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
            Elem.div [ cls "page" ] [
                Elem.div [ cls "page-header" ] [
                    Elem.h1 [ cls "league-title" ] [
                        Elem.span [] [ Text.raw config.Meta.Emoji ]
                        Elem.span [] [ Text.raw config.Name ]
                    ]
                    match weekInfo with
                    | Some wi ->
                        Elem.p [ cls "meta-text" ] [
                            Text.raw (if String.IsNullOrEmpty wi.WeekLabel then $"{wi.SeasonYear} Season" else $"{wi.SeasonYear} {wi.WeekLabel}")
                        ]
                    | None ->
                        Elem.p [ cls "meta-text" ] [ Text.raw "No current season data" ]
                    match prevWeekUrl, nextWeekUrl with
                    | None, None -> ()
                    | _ ->
                        Elem.div [ cls "week-nav" ] [
                            match prevWeekUrl with
                            | Some url ->
                                Elem.a [ Attr.href url; cls "week-nav-link" ] [
                                    Text.raw navPrevLabel
                                ]
                            | None -> ()
                            match nextWeekUrl with
                            | Some url ->
                                Elem.a [ Attr.href url; cls "week-nav-link is-next" ] [
                                    Text.raw navNextLabel
                                ]
                            | None -> ()
                        ]
                ]
                match bracket with
                | Some bracketData ->
                    Elem.div [ cls "bracket-scroll" ] [
                        yield! PlayoffBracketRenderer.render bracketData
                    ]
                | None -> ()
                if games.Count = 0 then
                    Elem.div [ cls "empty-state" ] [
                        Elem.p [ cls "empty-state-text" ] [ Text.raw $"No {config.ShortName} games found." ]
                    ]
                else
                    let sportStr = sport.ToString().ToLowerInvariant()
                    Elem.div [ cls "league-games"; Attr.create "data-init" $"@get('/sse/live/{sportStr}', {{requestCancellation: 'cleanup'}})" ] [
                        for game in games do
                            gameRow game sport odds scores hypeScores teamRanks hypeCredits
                    ]
                yield! QualificationCredit.renderQualityForGames (fun g -> g.Status = "scheduled") games hypeScores hypeCredits
            ]
        ]

    let Render sport config games odds scores hypeScores weekInfo teamRanks bracket prevWeekUrl nextWeekUrl =
        RenderWithHypeCredits sport config games odds scores hypeScores weekInfo teamRanks bracket prevWeekUrl nextWeekUrl (dict [])
