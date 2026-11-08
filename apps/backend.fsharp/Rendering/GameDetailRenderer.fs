namespace Backend.Rendering

open System
open System.Collections.Generic
open System.Text.Json
open Falco.Markup
open backend.GraphQL.Types
open backend.Models
open backend.Services

module GameDetailRenderer =

    let private cls = Layout.cls

    let private missingHypeInputs (game: Game) (hype: HypeComponents) =
        [
            if not hype.StakesAvailable then
                let missingTeams =
                    [ game.AwayTeam, hype.AwayPlayoffOdds; game.HomeTeam, hype.HomePlayoffOdds ]
                    |> List.choose (fun (team, odds) -> if odds.HasValue then None else Some team)
                if game.Sport = "nfl" && game.SeasonType = Nullable 2 then
                    let forecast = hype.NflPlayoffLeverage
                    let missingScenarios =
                        [ game.AwayTeam, (if isNull (box forecast) then null else forecast.AwayOdds), hype.AwayQualificationIsFinal
                          game.HomeTeam, (if isNull (box forecast) then null else forecast.HomeOdds), hype.HomeQualificationIsFinal ]
                        |> List.choose (fun (team, odds, final) -> if not final && isNull (box odds) then Some team else None)
                    if missingScenarios.IsEmpty then
                        yield "Stakes need verified competition and playoff context."
                    else
                        let teams = String.Join(" and ", missingScenarios)
                        yield $"Stakes need current win/loss playoff scenarios for {teams}."
                elif game.SeasonType = Nullable 2 && PlayoffRatingPolicy.isSupported game.Sport && not missingTeams.IsEmpty then
                    let qualification = if game.Sport = "cfb" then "CFP qualification odds" else "playoff odds"
                    let teams = String.Join(" and ", missingTeams)
                    yield $"Hype needs reliable {qualification} for {teams}."
                else
                    yield "Stakes need verified competition and playoff context."
            if not hype.QualityAvailable then
                yield "Current team-strength rankings are unavailable."
            if not hype.ClosenessAvailable then
                yield "Current game-winner odds are unavailable."
        ]

    let private comparisonRow (label: string) (away: string) (home: string) =
        Elem.div [ cls "comparison-row" ] [
            Elem.div [ cls "comparison-away" ] [ Text.enc away ]
            Elem.div [ cls "comparison-label" ] [ Text.enc label ]
            Elem.div [ cls "comparison-home" ] [ Text.enc home ]
        ]

    let private teamHeader (game: Game) =
        Elem.div [ cls "comparison-header" ] [
            Elem.div [ cls "comparison-header-away" ] [ Text.enc game.AwayTeamAbbreviation ]
            Elem.div [ cls "comparison-header-label" ] [ Text.raw "vs" ]
            Elem.div [ cls "comparison-header-home" ] [ Text.enc game.HomeTeamAbbreviation ]
        ]

    let private renderHighlights (state: HighlightsDetailState) =
        let validVideoId =
            not (isNull state.VideoId) && state.VideoId.Length = 11
            && state.VideoId |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '-' || c = '_')
        let playable = state.Status = HighlightsDetailStatus.Playable && validVideoId && not (String.IsNullOrWhiteSpace state.Title)
        if state.Status = HighlightsDetailStatus.Hidden then []
        else
            [
                Elem.section [ Attr.id "game-highlights"; Attr.create "aria-labelledby" "highlights-heading"; cls "panel" ] [
                    Elem.h2 [ Attr.id "highlights-heading"; cls "panel-title" ] [ Text.raw "Game Highlights" ]
                    if not playable then
                        Elem.p [ Attr.id "highlights-status"; cls "highlights-note" ] [
                            Text.raw (if state.Status = HighlightsDetailStatus.Pending then "Highlights not available yet" else "Highlights unavailable")
                        ]
                    else
                      Elem.h3 [ Attr.id "highlights-title"; cls "highlights-title" ] [ Text.enc state.Title ]
                      Elem.p [ cls "highlights-source" ] [ Text.raw "Published by MLB · Video served by YouTube" ]
                      Elem.p [ Attr.id "highlights-consent"; cls "highlights-copy" ] [
                          Text.raw "Loading the player contacts YouTube. YouTube may collect data when the player loads or you play the video. "
                          Elem.a [ Attr.id "highlights-terms"; Attr.href "https://www.youtube.com/t/terms" ] [ Text.raw "YouTube Terms of Service" ]
                          Text.raw " and "
                          Elem.a [ Attr.id "highlights-privacy"; Attr.href "https://policies.google.com/privacy" ] [ Text.raw "Google Privacy Policy" ]
                          Text.raw "."
                      ]
                      if (not (isNull state.AllowedRegions) && state.AllowedRegions.Count > 0)
                         || (not (isNull state.BlockedRegions) && state.BlockedRegions.Count > 0) then
                          Elem.p [ Attr.id "highlights-regions"; cls "highlights-copy" ] [
                              if not (isNull state.AllowedRegions) && state.AllowedRegions.Count > 0 then
                                  let countries = String.Join(", ", state.AllowedRegions)
                                  Text.enc $"Playback is limited to these countries: {countries}. "
                              if not (isNull state.BlockedRegions) && state.BlockedRegions.Count > 0 then
                                  let countries = String.Join(", ", state.BlockedRegions)
                                  Text.enc $"Playback is restricted in these countries: {countries}."
                          ]
                      Elem.create "nb-game-highlights" [ Attr.create "data-video-id" state.VideoId; cls "highlights-player" ] [
                          Elem.button [ Attr.id "highlights-load"; Attr.create "type" "button"; Attr.create "aria-describedby" "highlights-consent"; cls "button-primary" ] [ Text.raw "Load highlights from YouTube" ]
                      ]
                      Elem.p [ cls "highlights-watch-row" ] [
                          Elem.a [ Attr.id "highlights-watch"; Attr.href $"https://www.youtube.com/watch?v={state.VideoId}" ] [ Text.raw "Watch on YouTube" ]
                      ]
                      Elem.p [ Attr.id "highlights-fallback"; cls "highlights-fallback" ] [
                          Text.raw "If embedded playback fails, try Watch on YouTube. Availability may depend on your region; this link does not bypass restrictions."
                      ]
                      Elem.create "noscript" [] [ Elem.p [ cls "highlights-fallback" ] [ Text.raw "JavaScript is required to load the embedded player. You can still Watch on YouTube." ] ]
                ]
            ]

    let RenderWithHighlightsAtTime
        (now: DateTime)
        (game: Game)
        (snapshots: Collections.Generic.List<OddsSnapshot>)
        (pregameOdds: PregameOdds option)
        (hypeComponents: HypeComponents)
        (hypeScore: Nullable<float>)
        (homeTeamRank: TeamRankInfo option)
        (awayTeamRank: TeamRankInfo option)
        (highlights: HighlightsDetailState) =
        let sport = SportExtensions.FromDbValue(game.Sport)
        let status = GameStatusExtensions.FromDbValue(game.Status)
        let isLive = game.Status = "in_progress"
        let isFinal = game.Status = "final"
        let isScheduled = game.Status = "scheduled"
        let hasTeamRanks = homeTeamRank.IsSome || awayTeamRank.IsSome
        let showQualification =
            (game.SeasonType <> Nullable 3 || game.Sport = "cfb") && PlayoffRatingPolicy.isSupported game.Sport

        let pregameOdds =
            if game.Sport = "cfb" && isScheduled then
                pregameOdds |> Option.filter (fun odds -> CollegeFootballPolicy.UsablePregameOdds(game, odds, now))
            elif backend.Scoring.ProHypePolicy.Supports(game.Sport) && isScheduled && not game.IsHistoricalImport then
                pregameOdds |> Option.filter (fun odds -> backend.Scoring.ProHypePolicy.EligibleOdds(game, odds, now))
            else pregameOdds

        let colors = TeamColors.forGame game

        let homeLogoUrl = if game.Sport = "cfb" then CollegeFootballPolicy.Logo(game.HomeTeamId) |> Option.ofObj elif sport.HasValue then Logos.getTeamLogoUrl sport.Value game.HomeTeamAbbreviation else None
        let awayLogoUrl = if game.Sport = "cfb" then CollegeFootballPolicy.Logo(game.AwayTeamId) |> Option.ofObj elif sport.HasValue then Logos.getTeamLogoUrl sport.Value game.AwayTeamAbbreviation else None

        let latestOdds =
            (if snapshots.Count > 0 then Some snapshots[snapshots.Count - 1] else None)
            |> Option.filter (fun s -> GameWinnerProbabilityPolicy.FreshForDisplay(s, now)
                                       && (game.Sport <> "cfb" || CollegeFootballPolicy.UsableLiveOdds(game, s, now)))
        let homeWinProb = latestOdds |> Option.map (fun o -> o.HomeWinProbability) |> Option.defaultValue 0.5

        let excitementScore =
            if game.IsHistoricalImport then Nullable<float>()
            else
                let probabilities =
                    snapshots
                    |> Seq.filter (fun s -> not s.IsHistorical)
                    |> Seq.sortBy (fun s -> struct (s.Timestamp, s.Id))
                    |> Seq.map (fun s -> s.HomeWinProbability)
                ExcitementScore.Calculate game.Sport probabilities

        let statusLabel = if status.HasValue then FormatHelpers.getStatusLabel status.Value else ""
        let statusClass = if status.HasValue then FormatHelpers.getStatusClass status.Value else ""

        [
            Elem.div [
                cls "page-narrow"
                Attr.create "data-live-terminal" (if isFinal then "true" else "false")
                if not isFinal then Attr.create "data-init" $"@get('/sse/game/{game.Id}', {{requestCancellation: 'cleanup'}})"
            ] [
                Elem.div [ cls "detail-topbar" ] [
                    Elem.div [ cls "detail-league" ] [
                        match sport with
                        | s when s.HasValue ->
                            Elem.img [ Attr.src (Logos.getLeagueLogoUrl s.Value); Attr.alt (s.Value.ToString()); cls "detail-league-logo" ]
                        | _ -> ()
                        Elem.span [ cls $"detail-status-badge {statusClass}"; Attr.id "detail-status" ] [
                            Text.raw statusLabel
                        ]
                    ]
                    Elem.div [ Attr.id "detail-excitement" ] [
                        if game.Sport = "cfb" && (isLive || isFinal) && not excitementScore.HasValue then
                            Elem.span [ cls "meta-text" ] [ Text.raw "Excitement unavailable" ]
                        if excitementScore.HasValue && (isLive || isFinal) then
                            let level = FormatHelpers.getExcitementLevel excitementScore
                            let levelClass = FormatHelpers.excitementLevelClass level
                            Elem.div [ cls "excitement-inline" ] [
                                Elem.span [ cls "meta-text" ] [ Text.raw "Excitement" ]
                                Elem.span [ cls $"detail-excitement-value {levelClass}" ] [
                                    Text.raw $"{Math.Round(excitementScore.Value)}"
                                ]
                            ]
                    ]
                ]
                Elem.div [ cls "panel"; Attr.id "detail-scoreboard" ] [
                    Elem.div [ cls "scoreboard" ] [
                        Elem.div [ cls "scoreboard-team" ] [
                            match awayLogoUrl with
                            | Some url -> Elem.img [ Attr.src url; Attr.alt game.AwayTeamAbbreviation; cls "scoreboard-logo" ]
                            | None -> ()
                            Elem.span [ cls "scoreboard-abbr" ] [
                                Text.enc game.AwayTeamAbbreviation
                                if game.Sport = "cfb" then yield! FormatHelpers.pollRank game.RankPoll game.AwayPollRank
                            ]
                            Elem.span [ cls "meta-text" ] [ Text.enc game.AwayTeam ]
                            Elem.span [ cls "detail-score"; Attr.id "detail-away-score" ] [
                                if game.AwayScore.HasValue then
                                    Text.raw $"{game.AwayScore.Value}"
                            ]
                        ]
                        Elem.div [ cls "scoreboard-center" ] [
                            if isScheduled then
                                Elem.span [ cls "scoreboard-vs" ] [ Text.raw "VS" ]
                                Elem.span [ cls "meta-text" ] [
                                    FormatHelpers.scheduledTimeNode game.ScheduledStart
                                    yield! FormatHelpers.broadcastNodes game
                                ]
                            else
                                if isLive then
                                    match FormatHelpers.broadcastNodes game with
                                    | [] -> ()
                                    | nodes -> Elem.span [ cls "meta-text" ] (List.tail nodes)
                                Elem.div [ Attr.id "detail-clock" ] [
                                    if game.Sport = "cfb" && isLive then
                                        Elem.span [ cls "detail-clock-cfb" ] [ Text.enc (FormatHelpers.formatGameClock game.Period game.GameClock game.Sport) ]
                                    elif isLive && latestOdds.IsSome then
                                        let odds = latestOdds.Value
                                        let clockText = FormatHelpers.formatGameClock odds.Period odds.GameClock game.Sport
                                        if clockText <> "" then
                                            Elem.div [ cls "detail-clock-text" ] [
                                                Text.raw clockText
                                            ]
                                        else
                                            Elem.span [ cls "meta-text" ] [ Text.raw "@" ]
                                    else
                                        Elem.span [ cls "meta-text" ] [ Text.raw "@" ]
                                ]
                        ]
                        Elem.div [ cls "scoreboard-team" ] [
                            match homeLogoUrl with
                            | Some url -> Elem.img [ Attr.src url; Attr.alt game.HomeTeamAbbreviation; cls "scoreboard-logo" ]
                            | None -> ()
                            Elem.span [ cls "scoreboard-abbr" ] [
                                Text.enc game.HomeTeamAbbreviation
                                if game.Sport = "cfb" then yield! FormatHelpers.pollRank game.RankPoll game.HomePollRank
                            ]
                            Elem.span [ cls "meta-text" ] [ Text.enc game.HomeTeam ]
                            Elem.span [ cls "detail-score"; Attr.id "detail-home-score" ] [
                                if game.HomeScore.HasValue then
                                    Text.raw $"{game.HomeScore.Value}"
                            ]
                        ]
                    ]
                    yield! FormatHelpers.collegeContext game
                    Elem.div [ Attr.id "detail-probbar" ] [
                        if game.Sport = "cfb" && latestOdds.IsNone && isLive then
                            Elem.span [ cls "meta-text" ] [ Text.raw "Live probability unavailable" ]
                        if latestOdds.IsSome && not isFinal then
                            Elem.div [ cls "detail-probability" ] [
                                ProbabilityBar.render false game.HomeTeamAbbreviation game.AwayTeamAbbreviation homeWinProb (1.0 - homeWinProb) colors
                            ]
                    ]
                ]
                if snapshots.Count >= 2 then
                    let chartData =
                        snapshots
                        |> Seq.map (fun s ->
                            {| homeWinProbability = s.HomeWinProbability
                               awayWinProbability = s.AwayWinProbability
                               source = s.Source
                               isProbabilityBound = backend.Services.GameWinnerProbabilityPolicy.IsBound(s.Source)
                               timestamp = s.Timestamp.ToString("o")
                               label = FormatHelpers.formatGameClock s.Period s.GameClock game.Sport
                               homeScore = s.HomeScore
                               awayScore = s.AwayScore |})
                        |> Seq.toArray
                    let chartJson = JsonSerializer.Serialize(chartData, JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase))
                    let chartJsonEncoded = System.Net.WebUtility.HtmlEncode(chartJson)
                    Elem.div [ cls "panel" ] [
                        Elem.h2 [ cls "panel-title" ] [
                            Text.raw (if snapshots |> Seq.exists (fun s -> s.IsHistorical) then "Historical market chart · 1-minute trade closes" else "Win Probability")
                        ]
                        if game.IsHistoricalImport then
                            Elem.p [ cls "meta-text" ] [ Text.raw "Historical market samples, not live-collected odds. Hype and excitement unavailable." ]
                        Elem.create "nb-win-probability-chart" [
                            Attr.create "game-id" game.Id
                            Attr.create "data-live-final" (if isFinal then "true" else "false")
                            Attr.create "data" chartJsonEncoded
                            Attr.create "home-color" colors.Home
                            Attr.create "away-color" colors.Away
                            Attr.create "home-abbr" game.HomeTeamAbbreviation
                            Attr.create "away-abbr" game.AwayTeamAbbreviation
                            cls "odds-chart"
                        ] []
                    ]
                yield! renderHighlights highlights
                if isScheduled && pregameOdds.IsSome then
                    let po = pregameOdds.Value
                    if po.HomeWinProbability > 0.0 || po.AwayWinProbability > 0.0 then
                        Elem.div [ cls "panel" ] [
                            ProbabilityBar.render false game.HomeTeamAbbreviation game.AwayTeamAbbreviation po.HomeWinProbability po.AwayWinProbability colors
                            if game.Sport = "cfb" && not po.HomeMoneyline.HasValue && not po.AwayMoneyline.HasValue then
                                let fetched = po.FetchedAt.ToString("yyyy-MM-dd HH:mm:ss")
                                Elem.p [ cls "source-note" ] [ Text.enc $"{po.Source} · {fetched} UTC" ]
                        ]
                if (isScheduled || game.IsHistoricalImport) && pregameOdds.IsSome
                   && (pregameOdds.Value.HomeMoneyline.HasValue || pregameOdds.Value.AwayMoneyline.HasValue
                       || pregameOdds.Value.Spread.HasValue || pregameOdds.Value.OverUnder.HasValue) then
                    let po = pregameOdds.Value
                    Elem.div [ cls "panel" ] [
                        Elem.h2 [ cls "panel-title" ] [ Text.raw (if game.IsHistoricalImport then "Historical closing odds" else "Pregame Odds") ]
                        if game.Sport = "cfb" then
                            let fetched = po.FetchedAt.ToString("yyyy-MM-dd HH:mm")
                            Elem.p [ cls "fine-print" ] [ Text.enc $"{po.Source} · {fetched} UTC" ]
                        Elem.div [ cls "odds-grid" ] [
                            if po.HomeMoneyline.HasValue && po.AwayMoneyline.HasValue then
                                Elem.div [] [
                                    Elem.div [ cls "odds-label" ] [ Text.raw "Moneyline" ]
                                    Elem.div [ cls "odds-value" ] [
                                        Text.raw $"{FormatHelpers.formatMoneyline po.HomeMoneyline.Value} / {FormatHelpers.formatMoneyline po.AwayMoneyline.Value}"
                                    ]
                                ]
                            if po.Spread.HasValue then
                                Elem.div [] [
                                    Elem.div [ cls "odds-label" ] [ Text.raw "Spread" ]
                                    Elem.div [ cls "odds-value" ] [
                                        Text.raw (FormatHelpers.formatSpread po.Spread.Value game.HomeTeamAbbreviation)
                                    ]
                                ]
                            if po.OverUnder.HasValue then
                                Elem.div [] [
                                    Elem.div [ cls "odds-label" ] [ Text.raw "Over/Under" ]
                                    Elem.div [ cls "odds-value" ] [ Text.raw $"{po.OverUnder.Value}" ]
                                ]
                        ]
                    ]
                if isScheduled && not (isNull (box hypeComponents)) then
                    let hypeLevel = FormatHelpers.getHypeLevel hypeScore
                    let hypeLevelCls = FormatHelpers.hypeLevelClass hypeLevel
                    let renderHypeBar (label: string) (score: float) (available: bool) =
                        Elem.div [ cls "hype-component" ] [
                            Elem.div [ cls "hype-component-header" ] [
                                Elem.span [ cls "hype-component-label" ] [ Text.raw label ]
                                Elem.span [ cls "hype-component-value" ] [ Text.raw (if available then $"{Math.Round(score)}" else "Unavailable") ]
                            ]
                            if available then
                              Elem.div [ cls "hype-meter"; Attr.style "background: var(--bg-card-hover)" ] [
                                Elem.div [
                                    cls "hype-meter-fill"
                                    Attr.style $"width: {score:F1}%%; background: var(--color-accent, #6366f1)"
                                ] []
                            ]
                        ]
                    Elem.div [ cls "panel"; Attr.id "detail-hype" ] [
                        Elem.div [ cls "hype-header" ] [
                            Elem.h2 [ cls "hype-title" ] [ Text.raw "Hype Score" ]
                            if hypeScore.HasValue then
                                Elem.span [ cls $"hype-score {hypeLevelCls}" ] [
                                    Text.raw (FormatHelpers.formatHype hypeScore)
                                ]
                        ]
                        if not hypeScore.HasValue then
                            Elem.div [ cls "hype-status"; Attr.id "detail-hype-status" ] [
                                Elem.p [ cls "hype-status-title" ] [ Text.raw "Hype unavailable" ]
                                for message in missingHypeInputs game hypeComponents do
                                    Elem.p [] [ Text.enc message ]
                            ]
                        renderHypeBar "Stakes" hypeComponents.StakesScore hypeComponents.StakesAvailable
                        renderHypeBar "Quality" hypeComponents.QualityScore hypeComponents.QualityAvailable
                        renderHypeBar "Closeness" hypeComponents.ClosenessScore hypeComponents.ClosenessAvailable
                        if game.Sport = "nfl" && not (isNull (box hypeComponents.NflPlayoffLeverage)) then
                            let forecast = hypeComponents.NflPlayoffLeverage
                            let probability (lower: Nullable<float>) (upper: Nullable<float>) =
                                if not lower.HasValue || not upper.HasValue then "Unavailable"
                                elif lower.Value = upper.Value then $"{Math.Round(lower.Value * 100.0)}%%"
                                elif lower.Value = 0.0 && upper.Value <= 0.010000001 then "<1%"
                                elif lower.Value >= 0.989999999 && upper.Value = 1.0 then ">99%"
                                else $"{Math.Round(lower.Value * 100.0)}–{Math.Round(upper.Value * 100.0)}%%"
                            let scenario win (odds: NflConditionalPlayoffOdds) =
                                if isNull (box odds) then "Unavailable"
                                elif win then probability odds.WinLower odds.WinUpper
                                else probability odds.LossLower odds.LossUpper
                            Elem.div [ Attr.id "nfl-playoff-leverage"; cls "leverage" ] [
                                Elem.h3 [ cls "leverage-title" ] [ Text.raw "Playoff chances by result" ]
                                teamHeader game
                                comparisonRow "If win" (scenario true forecast.AwayOdds) (scenario true forecast.HomeOdds)
                                comparisonRow "If loss" (scenario false forecast.AwayOdds) (scenario false forecast.HomeOdds)
                                Elem.p [ cls "leverage-source" ] [
                                    Text.raw "Model estimates from "
                                    Elem.a [ Attr.href backend.Scoring.NflPlayoffLeveragePolicy.ScheduleUrl ] [ Text.raw "PlayoffStatus" ]
                                    Text.raw ". Stakes uses the larger playoff qualification swing."
                                ]
                            ]
                        if backend.Scoring.ProHypePolicy.Supports game.Sport && hypeComponents.StakesAvailable then
                            let modelTeams =
                                [ game.AwayTeamAbbreviation, hypeComponents.AwayQualificationEvidenceKind
                                  game.HomeTeamAbbreviation, hypeComponents.HomeQualificationEvidenceKind ]
                                |> List.choose (fun (team, kind) -> if kind = backend.Scoring.ProQualificationEvidenceKind.MoneyPuckModel then Some team else None)
                            if not modelTeams.IsEmpty then
                                Elem.p [ cls "model-credit" ] [
                                    Text.raw (if hypeScore.HasValue then "Stakes and hype use " else "Stakes use ")
                                    Text.raw (if modelTeams.Length > 1 then "model estimates for " else "a model estimate for ")
                                    Text.enc (String.concat " / " modelTeams + " from ")
                                    Elem.a [ Attr.href "https://moneypuck.com/predictions.htm" ] [ Text.raw "MoneyPuck" ]
                                    Text.raw "."
                                ]
                        if game.Sport = "cfb" && hypeComponents.ClosenessAvailable && hypeComponents.ClosenessExplanation <> "" then
                            Elem.p [ cls "fine-print" ] [ Text.enc hypeComponents.ClosenessExplanation ]
                    ]
                if isScheduled && hasTeamRanks then
                    let powerRank (rank: TeamRankInfo option) = rank |> Option.bind (fun r -> r.PowerRank)
                    let powerText rank = powerRank rank |> Option.map (fun n -> $"#{n}") |> Option.defaultValue "—"
                    Elem.div [ cls "panel"; Attr.id "detail-comparison" ] [
                        Elem.h2 [ cls "panel-title" ] [ Text.raw "Team comparison" ]
                        teamHeader game
                        if (powerRank homeTeamRank).IsSome || (powerRank awayTeamRank).IsSome then
                            comparisonRow "Power Rank" (powerText awayTeamRank) (powerText homeTeamRank)
                        if showQualification then
                            let outlook (rank: TeamRankInfo option) =
                                rank |> Option.bind (fun r ->
                                    r.PlayoffOdds |> Option.map (fun p ->
                                        let text = FormatHelpers.formatPlayoffOutlook r.PlayoffIsFinal r.PlayoffUpperBound p
                                        if r.PlayoffEvidenceKind = backend.Scoring.ProQualificationEvidenceKind.MoneyPuckModel then $"{text} (model estimate)" else text))
                                |> Option.defaultValue "Unavailable"
                            comparisonRow (if game.Sport = "cfb" then "CFP qualification" else "Playoff outlook") (outlook awayTeamRank) (outlook homeTeamRank)
                        if not (isNull (box hypeComponents)) && (hypeComponents.HomeTeamElo.HasValue || hypeComponents.AwayTeamElo.HasValue) then
                            let rating (value: Nullable<float>) = if value.HasValue then $"{value.Value:F0}" else "—"
                            comparisonRow (if game.Sport = "cfb" then "FPI" else "Elo / Rating") (rating hypeComponents.AwayTeamElo) (rating hypeComponents.HomeTeamElo)
                        if showQualification then
                            let modelTeams =
                                [ game.AwayTeamAbbreviation, awayTeamRank; game.HomeTeamAbbreviation, homeTeamRank ]
                                |> List.choose (fun (team, rank) ->
                                    if rank |> Option.exists (fun r -> r.PlayoffOdds.IsSome && r.PlayoffSource = PlayoffRatingPolicy.moneyPuckQualificationSource
                                                                       && r.PlayoffEvidenceKind = backend.Scoring.ProQualificationEvidenceKind.MoneyPuckModel) then Some team else None)
                            if not modelTeams.IsEmpty then
                                QualificationCredit.render "detail-qualification-credit" modelTeams
                        if showQualification && game.Sport <> "nhl" then
                            let sources =
                                [ game.AwayTeamAbbreviation, awayTeamRank; game.HomeTeamAbbreviation, homeTeamRank ]
                                |> List.choose (fun (team, rank) ->
                                    if rank |> Option.exists (fun r -> r.PlayoffIsAssumed) then Some(team, "Nailbiter estimate")
                                    elif rank |> Option.exists (fun r -> r.PlayoffFetchedAt.IsSome) then Some(team, "Kalshi")
                                    else None)
                            if not sources.IsEmpty then
                                let names = sources |> List.map snd |> List.distinct
                                let attribution =
                                    if names.Length = 1 then $"Source: {names.Head}"
                                    else sources |> List.map (fun (team, source) -> $"{team}: {source}") |> String.concat " · "
                                Elem.p [ cls "source-note" ] [ Text.enc attribution ]
                    ]
                if isScheduled && not (isNull (box hypeComponents)) && hypeComponents.QualityAvailable
                   && hypeComponents.QualitySource = NhlPowerRankingsGuruService.Source then
                    QualificationCredit.renderQuality "quality-credit" hypeScore.HasValue
            ]
        ]

    let RenderAtTime now game snapshots pregameOdds hypeComponents hypeScore homeTeamRank awayTeamRank =
        RenderWithHighlightsAtTime now game snapshots pregameOdds hypeComponents hypeScore homeTeamRank awayTeamRank (HighlightsDetailState(HighlightsDetailStatus.Hidden))

    let Render game snapshots pregameOdds hypeComponents hypeScore homeTeamRank awayTeamRank =
        RenderAtTime DateTime.UtcNow game snapshots pregameOdds hypeComponents hypeScore homeTeamRank awayTeamRank
