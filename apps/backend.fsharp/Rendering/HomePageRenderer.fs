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

    let render id (teams: string list) =
        let plural = teams.Length > 1
        Elem.p [ Layout.cls "credit-note"; Attr.id id ] [
            Text.enc (String.concat " / " teams + (if plural then " playoff probabilities: " else " playoff probability: "))
            Elem.a [ Attr.href "https://moneypuck.com/predictions.htm" ] [ Text.raw "MoneyPuck" ]
            Text.raw (if plural then " model estimates." else " model estimate.")
        ]

    let renderQuality id hasHype =
        Elem.footer [ Layout.cls "quality-credit"; Attr.id id ] [
            Text.raw (if hasHype then "Quality and hype use " else "Quality uses ")
            Elem.a [ Attr.href NhlPowerRankingsGuruService.RankingsUrl; Attr.style "text-decoration: underline; text-underline-offset: 3px" ] [ Text.raw "Power Rankings Guru" ]
            Text.raw " team-strength rankings."
        ]

    let renderQualityForGames (showsHype: Game -> bool) (games: seq<Game>) (hypeScores: IDictionary<string, Nullable<float>>) (hypeCredits: IDictionary<string, DerivedHypeCredit>) =
        let usesGuru =
            games |> Seq.exists (fun game ->
                match hypeScores.TryGetValue game.Id, hypeCredits.TryGetValue game.Id with
                | (true, score), (true, credit) ->
                    showsHype game && not game.IsHistoricalImport && score.HasValue && Double.IsFinite score.Value
                    && credit.QualitySource = NhlPowerRankingsGuruService.Source
                | _ -> false)
        [ if usesGuru then renderQuality "quality-credit" true ]

    let renderForGame (game: Game) (ranks: IDictionary<string, TeamRankInfo>) (hypeCredits: IDictionary<string, DerivedHypeCredit>) =
        let currentTeams = selectedTeams game ranks
        let derivedTeams =
            match hypeCredits.TryGetValue game.Id with
            | true, credit -> credit.MoneyPuckTeams
            | _ -> []
        [
            if not currentTeams.IsEmpty then
                render $"qualification-credit-{game.Id}" currentTeams
            if not derivedTeams.IsEmpty then
                Elem.p [ Layout.cls "credit-note"; Attr.id $"hype-credit-{game.Id}" ] [
                    Text.raw (if derivedTeams.Length > 1 then "Stakes and hype use model estimates for " else "Stakes and hype use a model estimate for ")
                    Text.enc (String.concat " / " derivedTeams + " from ")
                    Elem.a [ Attr.href "https://moneypuck.com/predictions.htm" ] [ Text.raw "MoneyPuck" ]
                    Text.raw "."
                ]
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
                  Elem.span [ cls "nowrap" ] [ Text.enc $"Game {series.CurrentGameNumber.Value}" ]
              Elem.span [ cls "nowrap" ] [ Text.enc $"Best of {series.BestOf}" ] ]
            |> List.mapi (fun index part -> if index = 0 then [ part ] else [ Text.enc " · "; part ])
            |> List.concat
        Elem.div [ cls "series-summary"; Attr.id $"series-{game.Id}" ] [
            Elem.div [ cls "series-standing" ] [ Text.enc standing ]
            Elem.div [ cls "series-context" ] context
        ]

    let private availableHype (game: Game) (hypeScores: IDictionary<string, Nullable<float>>) =
        match hypeScores.TryGetValue(game.Id) with
        | true, h when h.HasValue && Double.IsFinite h.Value -> h
        | _ -> Nullable<float>()

    let private finite (score: Nullable<float>) =
        if score.HasValue && Double.IsFinite score.Value then Some score.Value else None

    let private liveRank (scores: IDictionary<string, Nullable<float>>) hypeScores (game: Game) =
        let excitement =
            match scores.TryGetValue game.Id with
            | true, score -> finite score
            | _ -> None
        match excitement, finite (availableHype game hypeScores) with
        | Some e, Some h -> Some ((e + h) / 2.0)
        | Some value, None
        | None, Some value -> Some value
        | None, None -> None

    let private decided (odds: IDictionary<string, OddsSnapshot>) (game: Game) =
        match odds.TryGetValue game.Id with
        | true, o -> o.HomeWinProbability > 0.99 || o.HomeWinProbability < 0.01
        | _ -> false

    let private homeSection (game: Game) hypeScores =
        match game.Status with
        | "in_progress" -> "live"
        | "scheduled" ->
            let hype = availableHype game hypeScores
            if hype.HasValue && hype.Value >= 70.0 then "best"
            elif hype.HasValue && hype.Value >= 40.0 then "good"
            else "more"
        | _ -> "other"

    let private gameCard (game: Game) (odds: IDictionary<string, OddsSnapshot>) (scores: IDictionary<string, Nullable<float>>) (hypeScores: IDictionary<string, Nullable<float>>) (teamRanks: IDictionary<string, TeamRankInfo>) (hypeCredits: IDictionary<string, DerivedHypeCredit>) =
        let sport = SportExtensions.FromDbValue(game.Sport)
        let status = GameStatusExtensions.FromDbValue(game.Status)
        let excitementScore =
            match scores.TryGetValue(game.Id) with
            | true, s -> s
            | _ -> Nullable<float>()
        let hypeScore = availableHype game hypeScores
        let latestOdds =
            match odds.TryGetValue(game.Id) with
            | true, o -> Some o
            | _ -> None

        let statusLabel = if status.HasValue then FormatHelpers.getStatusLabel status.Value else Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(game.Status.Replace("_", " "))
        let statusClass = if status.HasValue then FormatHelpers.getStatusClass status.Value else ""
        let isLive = game.Status = "in_progress"
        let isPregame = game.Status = "scheduled"
        let series = PlayoffSeriesObservation.TryReadFor(game) |> Option.ofObj

        let colors = TeamColors.forGame game

        let homeLogoUrl = if game.Sport = "cfb" then CollegeFootballPolicy.Logo(game.HomeTeamId) |> Option.ofObj elif sport.HasValue then Logos.getTeamLogoUrl sport.Value game.HomeTeamAbbreviation else None
        let awayLogoUrl = if game.Sport = "cfb" then CollegeFootballPolicy.Logo(game.AwayTeamId) |> Option.ofObj elif sport.HasValue then Logos.getTeamLogoUrl sport.Value game.AwayTeamAbbreviation else None

        let homeWinProb = latestOdds |> Option.map (fun o -> o.HomeWinProbability) |> Option.defaultValue 0.5

        let card = Elem.a [ Attr.href $"/game/{game.Id}"; Attr.create "data-status" game.Status; cls "home-game-card" ] [
            Elem.div [ cls "home-card-top" ] [
                Elem.div [ cls "home-card-meta" ] [
                    Elem.div [ cls "home-card-league" ] [
                        match sport with
                        | s when s.HasValue ->
                            Elem.img [ Attr.src (Logos.getLeagueLogoUrl s.Value); Attr.alt ""; cls "league-icon" ]
                            Elem.span [] [ Text.enc (s.Value.ToString()) ]
                        | _ -> ()
                        Elem.span [ cls $"status-badge {statusClass}"; Attr.id $"status-{game.Id}" ] [
                            Text.enc statusLabel
                        ]
                    ]
                    Elem.div [ cls "home-card-time" ] [
                        FormatHelpers.scheduledTimeNode game.ScheduledStart
                        yield! FormatHelpers.broadcastNodes game
                    ]
                ]
                if isPregame then
                    Elem.div [ cls "home-card-hype"; Attr.id $"excitement-{game.Id}" ] [
                        Elem.span [ cls "home-metric-label" ] [
                            Text.raw "Hype"
                            if not hypeScore.HasValue then Elem.span [ cls "visually-hidden" ] [ Text.raw " unavailable" ]
                        ]
                        if hypeScore.HasValue then
                            let levelClass = FormatHelpers.getHypeLevel hypeScore |> FormatHelpers.hypeLevelClass
                            Elem.span [ cls $"home-metric-value {levelClass}" ] [ Text.raw $"{Math.Round(hypeScore.Value)}" ]
                        else
                            Elem.span [ cls "home-metric-value home-hype-unavailable"; Attr.create "aria-hidden" "true" ] [ Text.raw "—" ]
                    ]
            ]
            Elem.div [ cls "team-scores"; Attr.id $"scores-{game.Id}" ] [
                Elem.div [ cls "team-row" ] [
                    Elem.div [ cls "team-identity" ] [
                        match awayLogoUrl with
                        | Some url -> Elem.img [ Attr.src url; Attr.alt game.AwayTeamAbbreviation; cls "team-logo" ]
                        | None -> ()
                        Elem.div [ cls "team-label" ] [
                            Elem.span [ cls "team-name" ] [
                                Text.enc game.AwayTeamAbbreviation
                                if game.Sport = "cfb" then yield! FormatHelpers.pollRank game.RankPoll game.AwayPollRank
                            ]
                            match teamRanks.TryGetValue($"{game.Sport}:{PlayoffRatingPolicy.gameSeason game}:{game.SeasonType.GetValueOrDefault()}:{game.AwayTeamAbbreviation}") with
                            | true, rank ->
                                match rank.DisplayRank with
                                | Some text -> Elem.span [ cls "team-rank"; yield! FormatHelpers.playoffDisplayConventionAttributes rank.PlayoffDisplayOverride.IsSome ] [ Text.enc text ]
                                | None -> ()
                            | _ -> ()
                        ]
                    ]
                    Elem.span [ cls "team-score"; Attr.id $"away-score-{game.Id}" ] [
                        if not isPregame && game.AwayScore.HasValue then Text.raw $"{game.AwayScore.Value}"
                    ]
                ]
                Elem.div [ cls "team-row" ] [
                    Elem.div [ cls "team-identity" ] [
                        match homeLogoUrl with
                        | Some url -> Elem.img [ Attr.src url; Attr.alt game.HomeTeamAbbreviation; cls "team-logo" ]
                        | None -> ()
                        Elem.div [ cls "team-label" ] [
                            Elem.span [ cls "team-name" ] [
                                Text.enc game.HomeTeamAbbreviation
                                if game.Sport = "cfb" then yield! FormatHelpers.pollRank game.RankPoll game.HomePollRank
                            ]
                            match teamRanks.TryGetValue($"{game.Sport}:{PlayoffRatingPolicy.gameSeason game}:{game.SeasonType.GetValueOrDefault()}:{game.HomeTeamAbbreviation}") with
                            | true, rank ->
                                match rank.DisplayRank with
                                | Some text -> Elem.span [ cls "team-rank"; yield! FormatHelpers.playoffDisplayConventionAttributes rank.PlayoffDisplayOverride.IsSome ] [ Text.enc text ]
                                | None -> ()
                            | _ -> ()
                        ]
                    ]
                    Elem.span [ cls "team-score"; Attr.id $"home-score-{game.Id}" ] [
                        if not isPregame && game.HomeScore.HasValue then Text.raw $"{game.HomeScore.Value}"
                    ]
                ]
            ]
            let context = FormatHelpers.collegeContext game
            if not context.IsEmpty then Elem.div [ cls "home-card-context" ] context
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
            if isPregame && series.IsSome then
                Elem.div [ cls "home-card-context home-card-series" ] [
                    seriesSummary game series.Value
                ]
            if isLive || game.Status = "final" then
                Elem.div [ cls ("home-card-observed-metric" + (if series.IsSome then " has-series" else "")) ] [
                    Elem.div [ cls (if series.IsSome then "series-metric" else "metric-trend") ] [
                        if series.IsSome then seriesSummary game series.Value
                        let sparkline =
                            Elem.create "nb-sparkline" [
                                Attr.create "game-id" game.Id
                                Attr.create "data-live-final" (if game.Status = "final" then "true" else "false")
                                Attr.create "color" colors.Home
                                cls "card-sparkline"
                            ] []
                        if series.IsSome then Elem.div [ cls "metric-trend" ] [ sparkline ]
                        else sparkline
                    ]
                    Elem.div [ cls "observed-scores" ] [
                        if hypeScore.HasValue then
                            Elem.div [ cls "metric-block"; Attr.id $"hype-{game.Id}" ] [
                                Elem.span [ cls "metric-label" ] [ Text.raw "Hype" ]
                                Elem.span [ cls $"metric-value {FormatHelpers.getHypeLevel hypeScore |> FormatHelpers.hypeLevelClass}" ] [
                                    Text.raw $"{Math.Round(hypeScore.Value)}"
                                ]
                            ]
                        Elem.div [ yield Attr.id $"excitement-{game.Id}"
                                   if series.IsSome then yield cls "excitement-aside" ] [
                            if game.Sport = "cfb" && not excitementScore.HasValue then
                                Elem.span [ cls "unavailable-note" ] [ Text.raw "Excitement unavailable" ]
                            if excitementScore.HasValue then
                                let level = FormatHelpers.getExcitementLevel excitementScore
                                let levelClass = FormatHelpers.excitementLevelClass level
                                Elem.div [ cls "metric-block" ] [
                                    Elem.span [ cls "metric-label" ] [ Text.raw "Excitement" ]
                                    Elem.span [ cls $"metric-value {levelClass}" ] [
                                        Text.raw $"{Math.Round(excitementScore.Value)}"
                                    ]
                                ]
                        ]
                    ]
                ]
        ]
        let credits = QualificationCredit.renderForGame game teamRanks hypeCredits
        Elem.div [ cls "home-game-item" ] [ card; yield! credits ]

    let RenderWithHypeCredits (games: Collections.Generic.List<Game>) (odds: IDictionary<string, OddsSnapshot>) (scores: IDictionary<string, Nullable<float>>) (hypeScores: IDictionary<string, Nullable<float>>) (teamRanks: IDictionary<string, TeamRankInfo>) (hypeCredits: IDictionary<string, DerivedHypeCredit>) =
        [
            Elem.div [ cls "home-schedule page" ] [
                Elem.h1 [ cls "page-title" ] [ Text.raw "Today's Games" ]
                if games.Count = 0 then
                    Elem.div [ cls "empty-state" ] [
                        Elem.p [ cls "empty-state-text" ] [ Text.raw "No games scheduled for today." ]
                    ]
                else
                    Elem.div [ Attr.create "data-init" "@get('/sse/live', {requestCancellation: 'cleanup'})" ] [
                        for key, label in
                            [ "live", "Live now"; "best", "Best games"; "good", "Good games"; "more", "More games"; "other", "Other games" ] do
                            let cards = games |> Seq.filter (fun g -> homeSection g hypeScores = key) |> Seq.toList
                            let cards =
                                if key <> "live" then cards
                                else cards |> List.sortBy (fun g -> decided odds g, liveRank scores hypeScores g |> Option.map (~-) |> Option.defaultValue infinity)
                            if not cards.IsEmpty then
                                Elem.section [ cls "home-section"; Attr.id $"home-section-{key}"; Attr.create "aria-labelledby" $"home-heading-{key}"; Attr.create "data-single-game" (if cards.Length = 1 then "true" else "false") ] [
                                    Elem.h2 [ cls "home-section-title"; Attr.id $"home-heading-{key}" ] [ Text.raw label ]
                                    Elem.div [ cls "home-game-grid" ] [
                                        for game in cards do
                                            gameCard game odds scores hypeScores teamRanks hypeCredits
                                    ]
                                ]
                    ]
                yield! QualificationCredit.renderQualityForGames (fun g -> g.Status = "scheduled" || g.Status = "in_progress") games hypeScores hypeCredits
            ]
        ]

    let Render games odds scores hypeScores teamRanks =
        RenderWithHypeCredits games odds scores hypeScores teamRanks (dict [])
