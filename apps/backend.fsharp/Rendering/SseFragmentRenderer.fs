namespace Backend.Rendering

open System
open Falco.Markup
open backend.GraphQL.Types

module SseFragmentRenderer =

    let private cls = Layout.cls

    let renderGameCardScores
        (gameId: string)
        (awayAbbr: string)
        (homeAbbr: string)
        (awayScore: int option)
        (homeScore: int option)
        (awayLogoUrlStr: string)   
        (homeLogoUrlStr: string) =
        Elem.div [ cls "space-y-2"; Attr.id $"scores-{gameId}" ] [
            Elem.div [ cls "flex items-center justify-between" ] [
                Elem.div [ cls "flex items-center gap-2" ] [
                    if not (String.IsNullOrEmpty awayLogoUrlStr) then
                        Elem.img [ Attr.src awayLogoUrlStr; Attr.alt awayAbbr; cls "w-6 h-6 rounded-full ring-1 ring-white/20" ]
                    Elem.span [ cls "font-medium text-text-primary" ] [ Text.enc awayAbbr ]
                ]
                match awayScore with
                | Some s ->
                    Elem.span [ cls "text-lg font-semibold tabular-nums text-text-primary" ] [
                        Text.raw $"{s}"
                    ]
                | None -> ()
            ]
            Elem.div [ cls "flex items-center justify-between" ] [
                Elem.div [ cls "flex items-center gap-2" ] [
                    if not (String.IsNullOrEmpty homeLogoUrlStr) then
                        Elem.img [ Attr.src homeLogoUrlStr; Attr.alt homeAbbr; cls "w-6 h-6 rounded-full ring-1 ring-white/20" ]
                    Elem.span [ cls "font-medium text-text-primary" ] [ Text.enc homeAbbr ]
                ]
                match homeScore with
                | Some s ->
                    Elem.span [ cls "text-lg font-semibold tabular-nums text-text-primary" ] [
                        Text.raw $"{s}"
                    ]
                | None -> ()
            ]
        ]

    let renderGameCardExcitement (gameId: string) (excitementScore: Nullable<float>) (isLive: bool) (isFinal: bool) =
        Elem.div [ Attr.id $"excitement-{gameId}" ] [
            if excitementScore.HasValue && (isLive || isFinal) then
                let level = FormatHelpers.getExcitementLevel excitementScore
                let levelClass = FormatHelpers.excitementLevelClass level
                Elem.div [ cls "text-right" ] [
                    Elem.span [ cls "block text-xs text-text-muted mb-0.5" ] [ Text.raw "Excitement" ]
                    Elem.span [ cls $"text-xl font-semibold tabular-nums {levelClass}" ] [
                        Text.raw $"{Math.Round(excitementScore.Value)}"
                    ]
                ]
        ]

    let renderGameCardProbBar
        (gameId: string)
        (homeWinProb: float)
        (homeAbbr: string)
        (awayAbbr: string)
        (colors: TeamColors.MatchupColors)
        (isFinal: bool) =
        Elem.div [ Attr.id $"probbar-{gameId}" ] [
            if not isFinal then
                Elem.div [ cls "mt-3" ] [
                    ProbabilityBar.render true homeAbbr awayAbbr homeWinProb (1.0 - homeWinProb) colors
                ]
        ]

    let renderGameCardClock (gameId: string) (period: int option) (gameClock: string) (sport: string) (isLive: bool) =
        Elem.div [ Attr.id $"clock-{gameId}" ] [
            if isLive then
                let periodNullable = match period with Some p -> Nullable p | None -> Nullable()
                let clockText = FormatHelpers.formatGameClock periodNullable gameClock sport
                if clockText <> "" then
                    Elem.div [ cls "mt-2 text-sm text-status-live font-medium text-center" ] [
                        Text.raw clockText
                    ]
        ]

    let renderGameCardStatus (gameId: string) (status: GameStatus) (statusLabel: string) (statusClass: string) =
        Elem.span [ cls $"text-xs font-medium px-1.5 py-0.5 rounded {statusClass}"; Attr.id $"status-{gameId}" ] [
            Text.raw statusLabel
        ]

    let renderDetailProbBar homeWinProb homeAbbr awayAbbr colors isFinal =
        Elem.div [ Attr.id "detail-probbar" ] [
            if not isFinal then
                Elem.div [ cls "mt-6" ] [
                    ProbabilityBar.render false homeAbbr awayAbbr homeWinProb (1.0 - homeWinProb) colors
                ]
        ]

    let renderDetailExcitement (excitementScore: Nullable<float>) (isLive: bool) (isFinal: bool) =
        Elem.div [ Attr.id "detail-excitement" ] [
            if excitementScore.HasValue && (isLive || isFinal) then
                let level = FormatHelpers.getExcitementLevel excitementScore
                let levelClass = FormatHelpers.excitementLevelClass level
                Elem.div [ cls "flex items-center gap-2" ] [
                    Elem.span [ cls "text-text-muted text-sm" ] [ Text.raw "Excitement" ]
                    Elem.span [ cls $"text-xl font-bold tabular-nums {levelClass}" ] [
                        Text.raw $"{Math.Round(excitementScore.Value)}"
                    ]
                ]
        ]

    let private renderCardFragments (preserveTeamContext: bool) (suppressScheduledScores: bool) (update: GameUpdate) =
        let sport = update.Sport
        let isLive = update.Status = GameStatus.IN_PROGRESS
        let isFinal = update.Status = GameStatus.FINAL

        let homeScore = if update.HomeScore.HasValue then Some update.HomeScore.Value else None
        let awayScore = if update.AwayScore.HasValue then Some update.AwayScore.Value else None

        let excitementScore = if update.ExcitementScore.HasValue then Nullable update.ExcitementScore.Value else Nullable()
        let homeWinProb = if update.HomeWinProbability.HasValue then update.HomeWinProbability.Value else 0.5

        let statusLabel = FormatHelpers.getStatusLabel update.Status
        let statusClass = FormatHelpers.getStatusClass update.Status
        let homeAbbr = if String.IsNullOrEmpty update.HomeTeamAbbreviation then update.HomeTeam else update.HomeTeamAbbreviation
        let awayAbbr = if String.IsNullOrEmpty update.AwayTeamAbbreviation then update.AwayTeam else update.AwayTeamAbbreviation

        let period = if update.Period.HasValue then Some update.Period.Value else None
        let gameClock = update.GameClock |> Option.ofObj |> Option.defaultValue ""
        let sportStr = sport.ToString()

        let homeLogoStr = if sport = Sport.CFB then backend.Services.CollegeFootballPolicy.Logo(update.HomeTeamId) else Logos.getTeamLogoUrl sport homeAbbr |> Option.defaultValue ""
        let awayLogoStr = if sport = Sport.CFB then backend.Services.CollegeFootballPolicy.Logo(update.AwayTeamId) else Logos.getTeamLogoUrl sport awayAbbr |> Option.defaultValue ""
        let colors = TeamColors.forUpdate update

        let fragments = [
            if preserveTeamContext || sport = Sport.CFB then
                for side, score in [ "away", awayScore; "home", homeScore ] do
                    Elem.span [ Attr.id $"{side}-score-{update.GameId}"; cls "text-lg font-semibold tabular-nums text-text-primary" ] [
                        match score with
                        | Some value when not suppressScheduledScores || update.Status <> GameStatus.SCHEDULED -> Text.raw $"{value}"
                        | _ -> ()
                    ]
            else renderGameCardScores update.GameId awayAbbr homeAbbr awayScore homeScore awayLogoStr homeLogoStr
            if ((not preserveTeamContext && sport <> Sport.CFB) || isLive || isFinal)
               && not (update.Status = GameStatus.SCHEDULED && backend.Scoring.ProHypePolicy.Supports(sport.ToDbValue())) then
                if sport = Sport.CFB && not excitementScore.HasValue then
                    Elem.div [ Attr.id $"excitement-{update.GameId}"; cls "text-xs text-text-muted" ] [ Text.raw "Excitement unavailable" ]
                else renderGameCardExcitement update.GameId excitementScore isLive isFinal
            if not preserveTeamContext || update.Status <> GameStatus.SCHEDULED then
                if sport = Sport.CFB && not update.HomeWinProbability.HasValue then
                    Elem.div [ Attr.id $"probbar-{update.GameId}"; cls "text-xs text-text-muted" ] [
                        if isLive then Text.raw "Live probability unavailable"
                    ]
                else renderGameCardProbBar update.GameId homeWinProb homeAbbr awayAbbr colors isFinal
            renderGameCardClock update.GameId period gameClock sportStr isLive
            renderGameCardStatus update.GameId update.Status statusLabel statusClass
        ]

        fragments
        |> List.map XmlNodeRenderer.renderNode
        |> String.concat ""

    let renderGameCardFragments (update: GameUpdate) = renderCardFragments false false update

    let renderHomeGameCardFragments (update: GameUpdate) = renderCardFragments true true update

    let renderLeagueGameCardFragments (update: GameUpdate) = renderCardFragments true false update

    let renderDetailAwayScore (awayScore: int option) =
        Elem.span [ cls "text-3xl font-bold tabular-nums text-text-primary"; Attr.id "detail-away-score" ] [
            match awayScore with
            | Some s -> Text.raw $"{s}"
            | None -> ()
        ]

    let renderDetailHomeScore (homeScore: int option) =
        Elem.span [ cls "text-3xl font-bold tabular-nums text-text-primary"; Attr.id "detail-home-score" ] [
            match homeScore with
            | Some s -> Text.raw $"{s}"
            | None -> ()
        ]

    let renderDetailClock (period: int option) (gameClock: string) (sport: string) (isLive: bool) =
        Elem.div [ Attr.id "detail-clock" ] [
            if isLive then
                let periodNullable = match period with Some p -> Nullable p | None -> Nullable()
                let clockText = FormatHelpers.formatGameClock periodNullable gameClock sport
                if clockText <> "" then
                    Elem.div [ cls "text-sm text-status-live font-medium" ] [
                        Text.raw clockText
                    ]
                else
                    Elem.span [ cls "text-text-muted text-sm" ] [ Text.raw "@" ]
            else
                Elem.span [ cls "text-text-muted text-sm" ] [ Text.raw "@" ]
        ]

    let renderDetailStatus (status: GameStatus) =
        let statusLabel = FormatHelpers.getStatusLabel status
        let statusClass = FormatHelpers.getStatusClass status
        Elem.span [ cls $"text-sm font-medium px-2 py-1 rounded {statusClass}"; Attr.id "detail-status" ] [
            Text.raw statusLabel
        ]

    let renderDetailFragments (update: GameUpdate) =
        let isLive = update.Status = GameStatus.IN_PROGRESS
        let isFinal = update.Status = GameStatus.FINAL

        let excitementScore = if update.ExcitementScore.HasValue then Nullable update.ExcitementScore.Value else Nullable()
        let homeWinProb = if update.HomeWinProbability.HasValue then update.HomeWinProbability.Value else 0.5

        let homeScore = if update.HomeScore.HasValue then Some update.HomeScore.Value else None
        let awayScore = if update.AwayScore.HasValue then Some update.AwayScore.Value else None

        let period = if update.Period.HasValue then Some update.Period.Value else None
        let gameClock = update.GameClock |> Option.ofObj |> Option.defaultValue ""
        let sportStr = update.Sport.ToString()

        let colors = TeamColors.forUpdate update
        let homeAbbr = if String.IsNullOrEmpty update.HomeTeamAbbreviation then update.HomeTeam else update.HomeTeamAbbreviation
        let awayAbbr = if String.IsNullOrEmpty update.AwayTeamAbbreviation then update.AwayTeam else update.AwayTeamAbbreviation
        let fragments = [
            if update.Sport = Sport.CFB && not excitementScore.HasValue && (isLive || isFinal) then
                Elem.div [ Attr.id "detail-excitement"; cls "text-sm text-text-muted" ] [ Text.raw "Excitement unavailable" ]
            else renderDetailExcitement excitementScore isLive isFinal
            if update.Sport = Sport.CFB && not update.HomeWinProbability.HasValue then
                Elem.div [ Attr.id "detail-probbar"; cls "text-sm text-text-muted" ] [
                    if isLive then Text.raw "Live probability unavailable"
                ]
            else renderDetailProbBar homeWinProb homeAbbr awayAbbr colors isFinal
            renderDetailAwayScore awayScore
            renderDetailHomeScore homeScore
            renderDetailClock period gameClock sportStr isLive
            renderDetailStatus update.Status
        ]

        fragments
        |> List.map XmlNodeRenderer.renderNode
        |> String.concat ""
