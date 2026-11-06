namespace backend.Services

open System
open System.Collections.Generic
open System.Diagnostics
open System.Linq
open System.Threading
open System.Threading.Tasks
open Microsoft.EntityFrameworkCore
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open backend.Data
open backend.Models
open backend.Scoring

type HypeScoreService
    (serviceProvider: IServiceProvider,
     logger: ILogger<HypeScoreService>,
     timeProvider: TimeProvider,
     optionsAccessor: IOptions<PlayoffRatingsOptions>,
     selectionCache: PlayoffRatingPolicy.PlayoffSelectionCache) =

    let playoffOptions = if isNull (box optionsAccessor) then PlayoffRatingsOptions() else optionsAccessor.Value

    static let activitySource = new ActivitySource("Nailbiter.HypeScore")

    static let weights =
        let d = Dictionary<string, struct (float * float * float)>(StringComparer.OrdinalIgnoreCase)
        d.["nfl"] <- struct (0.45, 0.35, 0.20)    
        d.["nba"] <- struct (0.30, 0.40, 0.30)    
        d.["mlb"] <- struct (0.35, 0.30, 0.35)    
        d.["nhl"] <- struct (0.35, 0.35, 0.30)    
        d

    static let eloMin = 1300.0
    static let eloMax = 1700.0

    static let bpiMin = -20.0
    static let bpiMax = 20.0

    static let nhlPointsMin = 60.0
    static let nhlPointsMax = 130.0

    static let coalesceRatingsInternal (ratings: Collections.Generic.List<TeamRating>) =
        if ratings.Count = 0 then
            Unchecked.defaultof<TeamRating>
        else
            let result = TeamRating(Sport = ratings.[0].Sport, TeamAbbreviation = ratings.[0].TeamAbbreviation, Source = "coalesced")
            result.Season <- ratings.[0].Season
            result.FetchedAt <- ratings.[0].FetchedAt

            for r in ratings do
                if not result.EloRating.HasValue && r.EloRating.HasValue then
                    result.EloRating <- r.EloRating
                if not result.BpiRating.HasValue && r.BpiRating.HasValue then
                    result.BpiRating <- r.BpiRating
                if not result.FpiRating.HasValue && r.FpiRating.HasValue then
                    result.FpiRating <- r.FpiRating
                if not result.PowerRank.HasValue && r.PowerRank.HasValue then
                    result.PowerRank <- r.PowerRank
                if not result.ChampionshipProbability.HasValue && r.ChampionshipProbability.HasValue then
                    result.ChampionshipProbability <- r.ChampionshipProbability
                if isNull result.TeamName && not (isNull r.TeamName) then
                    result.TeamName <- r.TeamName

            result

    static let seasonGamesTotal =
        let d = Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        d.["nfl"] <- 17
        d.["nba"] <- 82
        d.["nhl"] <- 82
        d.["mlb"] <- 162
        d

    static let urgencySteepness = 3.0

    static let getCurrentSeasonInternal (sport: string) =
        let now = DateTime.UtcNow

        match sport.ToLower() with
        | "nfl" -> if now.Month >= 9 then now.Year else now.Year - 1       
        | "nba" -> if now.Month >= 10 then now.Year + 1 else now.Year      
        | "nhl" -> if now.Month >= 10 then now.Year + 1 else now.Year      
        | "mlb" -> now.Year                                                 
        | _ -> now.Year

    static let getSeasonProgress (sport: string) (gameDate: DateTime) =
        let struct (seasonStart, seasonEnd) =
            match sport.ToLower() with
            | "nfl" -> struct (DateTime(gameDate.Year, 9, 1), DateTime(gameDate.Year + 1, 2, 15))
            | "nba" -> struct (DateTime(gameDate.Year, 10, 15), DateTime(gameDate.Year + 1, 4, 15))
            | "nhl" -> struct (DateTime(gameDate.Year, 10, 1), DateTime(gameDate.Year + 1, 4, 15))
            | "mlb" -> struct (DateTime(gameDate.Year, 3, 28), DateTime(gameDate.Year, 10, 1))
            | _ -> struct (gameDate.AddMonths(-3), gameDate.AddMonths(3))

        let struct (seasonStart, seasonEnd) =
            if gameDate < seasonStart && sport.ToLower() <> "mlb" then
                struct (seasonStart.AddYears(-1), seasonEnd.AddYears(-1))
            else
                struct (seasonStart, seasonEnd)

        let totalDays = (seasonEnd - seasonStart).TotalDays
        let elapsedDays = (gameDate - seasonStart).TotalDays

        Math.Clamp(elapsedDays / totalDays, 0.0, 1.0)

    static let calculateCloseness (homeWinProbability: float) =
        let deviation = Math.Abs(homeWinProbability - 0.5)
        (1.0 - (deviation * 2.0)) * 100.0

    static let calculateThreeWayCloseness (homeProbability: float) (drawProbability: float) (awayProbability: float) =
        let maxOutcome = [ homeProbability; drawProbability; awayProbability ] |> List.max
        Math.Clamp(((1.0 - maxOutcome) / (2.0 / 3.0)) * 100.0, 0.0, 100.0)

    static let calculateQuality (homeRating: Nullable<float>) (awayRating: Nullable<float>) (sport: string) =
        if not homeRating.HasValue || not awayRating.HasValue then
            50.0  
        else
            let struct (normalizedHome, normalizedAway) =
                if sport.Equals("mlb", StringComparison.OrdinalIgnoreCase) then
                    struct (
                        Math.Clamp((homeRating.Value - eloMin) / (eloMax - eloMin), 0.0, 1.0),
                        Math.Clamp((awayRating.Value - eloMin) / (eloMax - eloMin), 0.0, 1.0))
                elif sport.Equals("nhl", StringComparison.OrdinalIgnoreCase) then
                    struct (
                        Math.Clamp((homeRating.Value - nhlPointsMin) / (nhlPointsMax - nhlPointsMin), 0.0, 1.0),
                        Math.Clamp((awayRating.Value - nhlPointsMin) / (nhlPointsMax - nhlPointsMin), 0.0, 1.0))
                else
                    struct (
                        Math.Clamp((homeRating.Value - bpiMin) / (bpiMax - bpiMin), 0.0, 1.0),
                        Math.Clamp((awayRating.Value - bpiMin) / (bpiMax - bpiMin), 0.0, 1.0))

            if normalizedHome + normalizedAway < 0.001 then
                0.0
            else
                let harmonicMean = 2.0 * normalizedHome * normalizedAway / (normalizedHome + normalizedAway)
                harmonicMean * 100.0

    static let calculatePlayoffUrgency (gamesRemaining: int) (totalGames: int) =
        if totalGames <= 0 then
            1.0
        else
            let gamesPlayed = totalGames - gamesRemaining
            let progress = Math.Clamp(float gamesPlayed / float totalGames, 0.0, 1.0)
            let urgency = (Math.Exp(urgencySteepness * progress) - 1.0) / (Math.Exp(urgencySteepness) - 1.0)
            Math.Clamp(urgency, 0.0, 1.0)

    static let getGamesRemaining (standing: TeamStanding) (sport: string) (gameDate: DateTime) =
        let mutable totalGames = 0

        let totalGames =
            if seasonGamesTotal.TryGetValue(sport.ToLower(), &totalGames) then
                totalGames
            else
                82  

        if not (isNull standing) && standing.GamesPlayed > 0 then
            Math.Max(0, totalGames - standing.GamesPlayed)
        else
            let progress = getSeasonProgress sport gameDate
            Math.Max(0, int (float totalGames * (1.0 - progress)))

    static let collegeComponents (game: Game) homeQualification awayQualification (pregame: PregameOdds) now =
        let c = HypeComponents(StakesAvailable = false, QualityAvailable = false, ClosenessAvailable = false)
        if not game.IsHistoricalImport && game.Status = "scheduled" then
            c.HomePlayoffOdds <- homeQualification
            c.AwayPlayoffOdds <- awayQualification
            if CollegeFootballPolicy.Fresh(game.RatingsObservedAt, now, TimeSpan.FromHours(8.0)) then
                c.HomeTeamElo <- game.HomeFpi
                c.AwayTeamElo <- game.AwayFpi
            let quality = CollegeFootballPolicy.Quality(c.HomeTeamElo, c.AwayTeamElo)
            c.QualityAvailable <- quality.HasValue
            c.QualityScore <- quality.GetValueOrDefault()
            let stakes = CollegeFootballPolicy.Stakes(game, homeQualification, awayQualification)
            c.StakesAvailable <- stakes.HasValue
            c.StakesScore <- stakes.GetValueOrDefault()
            c.CfpRelevance <- CollegeFootballPolicy.CfpRelevance(game, homeQualification, awayQualification).GetValueOrDefault(1.0)
            if CollegeFootballPolicy.UsablePregameOdds(game, pregame, now) then
                c.ClosenessAvailable <- true
                c.ClosenessScore <- calculateCloseness pregame.HomeWinProbability
                let counted = int (Math.Round(c.CfpRelevance * 100.0))
                c.ClosenessExplanation <-
                    [ if GameWinnerProbabilityPolicy.IsBound(pregame.Source) then
                          "Closeness uses a near-certain market bound, not an exact quoted probability."
                      if counted < 100 then
                          $"Hype counts {counted}%% of Closeness, matching how much the result can affect CFP qualification." ]
                    |> String.concat " "
        c

    static let proEvaluation (game: Game) (ratings: seq<TeamRating>) (pregame: PregameOdds) completed now (options: PlayoffRatingsOptions) (selectionCache: PlayoffRatingPolicy.PlayoffSelectionCache) (logger: ILogger<HypeScoreService>) =
        let rows = ratings |> Seq.toArray
        let qualification team =
            let teamRows =
                rows |> Seq.filter (fun r -> ProHypePolicy.TeamKey(game.Sport, r.TeamAbbreviation) = ProHypePolicy.TeamKey(game.Sport, team))
            let request : PlayoffRatingPolicy.SelectionRequest =
                { Sport = game.Sport; Season = game.SeasonYear.GetValueOrDefault(); Team = team; Now = now; Options = options }
            let selection = PlayoffRatingPolicy.selectCached selectionCache request teamRows
            let evidenceKind = QualificationProvenance.evidenceKind selection.Source
            ProQualification(
                selection.Probability |> Option.toNullable,
                selection.IsFinal,
                selection.EvidenceAt |> Option.toNullable,
                selection.ExpiresAt |> Option.toNullable,
                selection.Source |> Option.defaultValue "qualification evidence",
                evidenceKind), selection
        let h, homeSelection = qualification game.HomeTeamAbbreviation
        let a, awaySelection = qualification game.AwayTeamAbbreviation
        if not (isNull logger) && logger.IsEnabled(LogLevel.Debug) then
            let selections = [ homeSelection; awaySelection ]
            let selectedCounts =
                selections
                |> Seq.countBy (fun s -> s.Source |> Option.defaultValue "none")
                |> Seq.map (fun (source, count) -> $"{source}:{count}")
                |> String.concat ","
            let providerReasons =
                selections
                |> Seq.collect (fun s -> s.Providers)
                |> Seq.countBy (fun p -> $"{p.Source}:{p.Reason}")
                |> Seq.map (fun (reason, count) -> $"{reason}:{count}")
                |> String.concat ","
            let modelFallbackUsed = selections |> List.filter (fun s -> s.Source = Some PlayoffRatingPolicy.playoffStatusSource || s.Source = Some PlayoffRatingPolicy.moneyPuckQualificationSource) |> List.length
            logger.LogDebug("Pro qualification selection for {Sport} season {Season}: selectedCounts {SelectedCounts}; providerReasons {ProviderReasons}; modelFallbackUsed {ModelFallbackUsed}",
                game.Sport, game.SeasonYear.GetValueOrDefault(), selectedCounts, providerReasons, modelFallbackUsed)
        let result = ProHypePolicy.Calculate(game, ProGameEvidence.Parse(game.ProEvidenceJson), rows, h, a, pregame, completed, now)
        let components = HypeComponents(
            StakesAvailable = result.Stakes.Value.HasValue, StakesScore = result.Stakes.Value.GetValueOrDefault(), StakesExplanation = result.Stakes.Explanation,
            QualityAvailable = result.Quality.Value.HasValue, QualityScore = result.Quality.Value.GetValueOrDefault(), QualityExplanation = result.Quality.Explanation,
            ClosenessAvailable = result.Closeness.Value.HasValue, ClosenessScore = result.Closeness.Value.GetValueOrDefault(), ClosenessExplanation = result.Closeness.Explanation,
            NflPlayoffLeverage = result.NflPlayoffLeverage,
            HomePlayoffOdds = h.Probability, AwayPlayoffOdds = a.Probability,
            HomeQualificationSource = (homeSelection.Source |> Option.defaultValue ""),
            AwayQualificationSource = (awaySelection.Source |> Option.defaultValue ""),
            HomeQualificationEvidenceKind = (if h.Probability.HasValue then h.EvidenceKind else ProQualificationEvidenceKind.Unavailable),
            AwayQualificationEvidenceKind = (if a.Probability.HasValue then a.EvidenceKind else ProQualificationEvidenceKind.Unavailable),
            HomeQualificationIsFinal = h.IsFinal, AwayQualificationIsFinal = a.IsFinal)
        let references team (selection: PlayoffRatingPolicy.Selection) =
            selection.Providers |> Seq.choose (fun p ->
                rows |> Seq.filter (fun r -> r.Source = p.Source && r.Sport = game.Sport && r.Season = game.SeasonYear.GetValueOrDefault()
                                            && ProHypePolicy.TeamKey(game.Sport, r.TeamAbbreviation) = ProHypePolicy.TeamKey(game.Sport, team))
                     |> Seq.sortByDescending (fun r -> struct (r.FetchedAt, r.Id)) |> Seq.tryHead
                     |> Option.map (fun row -> ProEvidenceReference.Qualification(row, p.Reason, p.EvidenceAt |> Option.toNullable, p.ResolutionBlocked)))
        let qualifications = Seq.append (references game.HomeTeamAbbreviation homeSelection) (references game.AwayTeamAbbreviation awaySelection) |> Seq.toArray
        let assessment = ProHypePolicy.Assess(game, ProGameEvidence.Parse(game.ProEvidenceJson), rows, result, qualifications,
                                             h, a, pregame, completed, now)
        components.QualitySource <-
            assessment.SupportingEvidence |> Array.tryFind (fun r -> r.Kind = ProEvidenceKind.Ranking)
            |> Option.map (fun r -> r.Source) |> Option.defaultValue ""
        ProCurrentHypeEvaluation(components, assessment)

    static let proComponents game ratings pregame completed now options selectionCache logger =
        (proEvaluation game ratings pregame completed now options selectionCache logger).Components

    new(serviceProvider, logger, timeProvider) =
        HypeScoreService(serviceProvider, logger, timeProvider, Options.Create(PlayoffRatingsOptions()), null)

    new(serviceProvider, logger) = HypeScoreService(serviceProvider, logger, TimeProvider.System)

    member private _.CalculateStakes
        (game: Game,
         homeRating: TeamRating,
         awayRating: TeamRating,
         homeStanding: TeamStanding,
         awayStanding: TeamStanding) =

        if game.SeasonType.HasValue && game.SeasonType.Value = 3 then
            100.0
        elif PlayoffRatingPolicy.isSupported game.Sport && (not homeRating.PlayoffProbability.HasValue || not awayRating.PlayoffProbability.HasValue) then
            50.0  
        else

        let homePlayoff = if homeRating.PlayoffProbability.HasValue then homeRating.PlayoffProbability.Value else 0.5
        let awayPlayoff = if awayRating.PlayoffProbability.HasValue then awayRating.PlayoffProbability.Value else 0.5
        let homeChamp = if homeRating.ChampionshipProbability.HasValue then homeRating.ChampionshipProbability.Value else 0.0
        let awayChamp = if awayRating.ChampionshipProbability.HasValue then awayRating.ChampionshipProbability.Value else 0.0

        let homeBubble = 1.0 - Math.Abs(homePlayoff - 0.5) * 2.0
        let awayBubble = 1.0 - Math.Abs(awayPlayoff - 0.5) * 2.0

        let contenderFactor = Math.Min(homePlayoff, awayPlayoff)

        let champBonus = (homeChamp + awayChamp) * 100.0

        let mutable tg = 0

        let totalGames =
            if seasonGamesTotal.TryGetValue(game.Sport.ToLower(), &tg) then tg else 82

        let homeGamesRemaining = getGamesRemaining homeStanding game.Sport game.ScheduledStart
        let awayGamesRemaining = getGamesRemaining awayStanding game.Sport game.ScheduledStart
        let avgGamesRemaining = (homeGamesRemaining + awayGamesRemaining) / 2

        let urgency = calculatePlayoffUrgency avgGamesRemaining totalGames

        let urgencyScaledBubble = ((homeBubble + awayBubble) / 2.0 * 40.0) * urgency
        let urgencyScaledContender = (contenderFactor * 30.0) * urgency
        let urgencyScaledChamp = (champBonus * 0.2) * urgency

        let seasonProgress = getSeasonProgress game.Sport game.ScheduledStart
        let lateSeasonBonus = seasonProgress * 20.0

        let stakes =
            urgencyScaledBubble +        
            urgencyScaledContender +      
            urgencyScaledChamp +          
            lateSeasonBonus              

        Math.Clamp(stakes, 0.0, 100.0)

    member private this.CalculateComponentsAsync(game: Game, ct: CancellationToken) : Task<HypeComponents> =
        task {
            use activity = activitySource.StartActivity("HypeScore.CalculateComponents")

            if not (isNull activity) then
                activity.SetTag("game.id", game.Id) |> ignore

            use scope = serviceProvider.CreateScope()
            let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()

            if ProHypePolicy.Supports(game.Sport) then
                let now = if isNull selectionCache then timeProvider.GetUtcNow().UtcDateTime else selectionCache.EvaluationTime
                let season = game.SeasonYear.GetValueOrDefault()
                let homeKey = ProHypePolicy.TeamKey(game.Sport, game.HomeTeamAbbreviation)
                let awayKey = ProHypePolicy.TeamKey(game.Sport, game.AwayTeamAbbreviation)
                let! rows = db.TeamRatings.Where(fun r -> r.Sport = game.Sport && r.Season = season
                                                         && (r.TeamAbbreviation = game.HomeTeamAbbreviation || r.TeamAbbreviation = game.AwayTeamAbbreviation
                                                             || r.TeamAbbreviation = homeKey || r.TeamAbbreviation = awayKey)).ToListAsync(ct)
                let! pregame = db.PregameOdds.FirstOrDefaultAsync((fun o -> o.GameId = game.Id), ct)
                let! completed = db.Games.Where(fun g -> g.Sport = game.Sport && g.SeasonYear = game.SeasonYear && g.Status = "final"
                                                        && g.SeasonType = game.SeasonType).Select(fun g -> g.Id).ToListAsync(ct)
                return proComponents game rows pregame completed now playoffOptions selectionCache logger
            elif game.Sport = "cfb" then
                let season = PlayoffRatingPolicy.gameSeason game
                let now = timeProvider.GetUtcNow().UtcDateTime
                let asOf = min now game.ScheduledStart
                let! rows = db.TeamRatings.Where(fun r -> r.Sport = "cfb" && r.Season = season
                                                        && (r.Source = "kalshi" || r.Source = PlayoffRatingPolicy.collegeNoMarketSource)).ToListAsync(ct)
                let qualification team =
                    rows |> Seq.filter (fun r -> r.TeamAbbreviation = team)
                    |> PlayoffRatingPolicy.playoffProbability "cfb" season asOf |> Option.toNullable
                let! pregame = db.PregameOdds.FirstOrDefaultAsync((fun o -> o.GameId = game.Id), ct)
                return collegeComponents game (qualification game.HomeTeamAbbreviation) (qualification game.AwayTeamAbbreviation) pregame now
            else

            let isNfl = game.Sport = "nfl"
            let hasPlayoffs = PlayoffRatingPolicy.isSupported game.Sport
            let season = if hasPlayoffs then PlayoffRatingPolicy.gameSeason game else getCurrentSeasonInternal game.Sport
            let now = timeProvider.GetUtcNow().UtcDateTime
            let selectRatings (ratings: Collections.Generic.List<TeamRating>) =
                let verified =
                    ratings
                    |> Seq.filter (fun r -> not isNfl || r.Source <> "powerrankingsguru" || NflRatingPolicy.isVerified season r)
                    |> Collections.Generic.List<TeamRating>
                let merged = coalesceRatingsInternal verified
                if not hasPlayoffs then merged
                else
                    let result =
                        if isNull merged then TeamRating(Sport = game.Sport, TeamAbbreviation = "", Source = "coalesced", Season = season)
                        else merged
                    result.PlayoffProbability <- PlayoffRatingPolicy.playoffProbability game.Sport season now ratings |> Option.toNullable
                    result

            let! homeRatings =
                db.TeamRatings
                    .Where(fun (r: TeamRating) ->
                        r.Sport = game.Sport.ToLower()
                        && r.TeamAbbreviation = game.HomeTeamAbbreviation
                        && r.Season = season)
                    .OrderByDescending(fun (r: TeamRating) -> r.FetchedAt)
                    .ToListAsync(ct)

            let homeRating = selectRatings homeRatings

            let! awayRatings =
                db.TeamRatings
                    .Where(fun (r: TeamRating) ->
                        r.Sport = game.Sport.ToLower()
                        && r.TeamAbbreviation = game.AwayTeamAbbreviation
                        && r.Season = season)
                    .OrderByDescending(fun (r: TeamRating) -> r.FetchedAt)
                    .ToListAsync(ct)

            let awayRating = selectRatings awayRatings

            let! pregameOdds =
                db.PregameOdds.FirstOrDefaultAsync((fun (o: PregameOdds) -> o.GameId = game.Id), ct)

            let components = HypeComponents()

            if isNull pregameOdds then
                logger.LogDebug(
                    "No pregame odds for {Home} vs {Away}, skipping hype score",
                    game.HomeTeamAbbreviation, game.AwayTeamAbbreviation)
                return Unchecked.defaultof<HypeComponents>
            else

            components.ClosenessScore <- calculateCloseness pregameOdds.HomeWinProbability

            let! homeStanding =
                db.TeamStandings
                    .Where(fun (s: TeamStanding) ->
                        s.Sport = game.Sport.ToLower()
                        && s.TeamAbbreviation = game.HomeTeamAbbreviation
                        && s.Season = season)
                    .OrderByDescending(fun (s: TeamStanding) -> s.FetchedAt)
                    .FirstOrDefaultAsync(ct)

            let! awayStanding =
                db.TeamStandings
                    .Where(fun (s: TeamStanding) ->
                        s.Sport = game.Sport.ToLower()
                        && s.TeamAbbreviation = game.AwayTeamAbbreviation
                        && s.Season = season)
                    .OrderByDescending(fun (s: TeamStanding) -> s.FetchedAt)
                    .FirstOrDefaultAsync(ct)

            if not (isNull homeRating) && not (isNull awayRating) then
                components.HomeTeamElo <-
                    if homeRating.EloRating.HasValue then homeRating.EloRating
                    elif homeRating.BpiRating.HasValue then homeRating.BpiRating
                    else homeRating.FpiRating

                components.AwayTeamElo <-
                    if awayRating.EloRating.HasValue then awayRating.EloRating
                    elif awayRating.BpiRating.HasValue then awayRating.BpiRating
                    else awayRating.FpiRating

                components.HomePlayoffOdds <- homeRating.PlayoffProbability
                components.AwayPlayoffOdds <- awayRating.PlayoffProbability

                components.QualityScore <-
                    calculateQuality components.HomeTeamElo components.AwayTeamElo game.Sport

                components.StakesScore <-
                    this.CalculateStakes(game, homeRating, awayRating, homeStanding, awayStanding)
            else
                logger.LogDebug(
                    "No ratings found for {Home} vs {Away}, using closeness fallback",
                    game.HomeTeamAbbreviation, game.AwayTeamAbbreviation)
                components.QualityScore <- components.ClosenessScore
                components.StakesScore <- 50.0  

            return components
        }

    static member CoalesceRatings(ratings: Collections.Generic.List<TeamRating>) =
        coalesceRatingsInternal ratings

    static member GetCurrentSeason(sport: string) =
        getCurrentSeasonInternal sport

    static member CalculateThreeWayClosenessScore(homeProbability: float, drawProbability: float, awayProbability: float) =
        calculateThreeWayCloseness homeProbability drawProbability awayProbability

    member _.EvaluateFromEvidence(game: Game, ratings: seq<TeamRating>, pregameOdds: PregameOdds, knownCompletedIds: seq<string>, now: DateTime) =
        proEvaluation game ratings pregameOdds knownCompletedIds now playoffOptions null logger

    member _.CalculateFromEvidence(game: Game, ratings: seq<TeamRating>, pregameOdds: PregameOdds, knownCompletedIds: seq<string>, now: DateTime) =
        proComponents game ratings pregameOdds knownCompletedIds now playoffOptions null logger

    member this.CalculateFromData
        (game: Game,
         homeRating: TeamRating,
         awayRating: TeamRating,
         pregameOdds: PregameOdds,
         homeStanding: TeamStanding,
         awayStanding: TeamStanding) : HypeComponents =

        if ProHypePolicy.Supports(game.Sport) then
            let ratings = [homeRating; awayRating] |> List.filter (isNull >> not)
            let now = if isNull selectionCache then timeProvider.GetUtcNow().UtcDateTime else selectionCache.EvaluationTime
            proComponents game ratings pregameOdds Seq.empty now playoffOptions selectionCache logger
        elif game.Sport = "cfb" then
            let now = timeProvider.GetUtcNow().UtcDateTime
            let qualification team (rating: TeamRating) =
                (if isNull rating || rating.TeamAbbreviation <> team then [] else [rating])
                |> PlayoffRatingPolicy.playoffProbability "cfb" (PlayoffRatingPolicy.gameSeason game) (min now game.ScheduledStart)
                |> Option.toNullable
            collegeComponents game (qualification game.HomeTeamAbbreviation homeRating) (qualification game.AwayTeamAbbreviation awayRating) pregameOdds now
        elif isNull pregameOdds then
            Unchecked.defaultof<HypeComponents>
        else

        let select team (rating: TeamRating) =
            if isNull rating then
                if PlayoffRatingPolicy.isSupported game.Sport then
                    TeamRating(Sport = game.Sport, TeamAbbreviation = team, Source = "coalesced", Season = PlayoffRatingPolicy.gameSeason game)
                else rating
            else
                let merged = coalesceRatingsInternal (Collections.Generic.List<TeamRating>([rating]))
                let qualificationRows = if rating.TeamAbbreviation = team then [rating] else []
                merged.PlayoffProbability <-
                    PlayoffRatingPolicy.playoffProbability game.Sport (PlayoffRatingPolicy.gameSeason game)
                        (timeProvider.GetUtcNow().UtcDateTime) qualificationRows |> Option.toNullable
                merged
        let homeRating = select game.HomeTeamAbbreviation homeRating
        let awayRating = select game.AwayTeamAbbreviation awayRating
        let components = HypeComponents()
        components.ClosenessScore <- calculateCloseness pregameOdds.HomeWinProbability

        if not (isNull homeRating) && not (isNull awayRating) then
            components.HomeTeamElo <-
                if homeRating.EloRating.HasValue then homeRating.EloRating
                elif homeRating.BpiRating.HasValue then homeRating.BpiRating
                else homeRating.FpiRating

            components.AwayTeamElo <-
                if awayRating.EloRating.HasValue then awayRating.EloRating
                elif awayRating.BpiRating.HasValue then awayRating.BpiRating
                else awayRating.FpiRating

            components.HomePlayoffOdds <- homeRating.PlayoffProbability
            components.AwayPlayoffOdds <- awayRating.PlayoffProbability

            components.QualityScore <-
                calculateQuality components.HomeTeamElo components.AwayTeamElo game.Sport

            components.StakesScore <-
                this.CalculateStakes(game, homeRating, awayRating, homeStanding, awayStanding)
        else
            components.QualityScore <- components.ClosenessScore
            components.StakesScore <- 50.0

        components

    member _.CalculateFinalScore(sport: string, components: HypeComponents) =
        if ProHypePolicy.Supports(sport) then
            let value available score = if available then Nullable score else Nullable()
            ProHypePolicy.Composite(sport, value components.StakesAvailable components.StakesScore,
                                    value components.QualityAvailable components.QualityScore,
                                    value components.ClosenessAvailable components.ClosenessScore)
                |> Option.ofNullable |> Option.defaultValue Double.NaN
        elif sport = "cfb" then
            if not components.IsComplete then Double.NaN
            else components.StakesScore * 0.45 + components.QualityScore * 0.35 + components.ClosenessScore * components.CfpRelevance * 0.20
        else
        let struct (stakesWeight, qualityWeight, closenessWeight) =
            match weights.TryGetValue(sport.ToLower()) with
            | true, w -> w
            | false, _ -> struct (0.33, 0.33, 0.34)  

        let homePlayoff = if components.HomePlayoffOdds.HasValue then components.HomePlayoffOdds.Value else 0.5
        let awayPlayoff = if components.AwayPlayoffOdds.HasValue then components.AwayPlayoffOdds.Value else 0.5

        let homeBubble = 1.0 - Math.Abs(homePlayoff - 0.5) * 2.0
        let awayBubble = 1.0 - Math.Abs(awayPlayoff - 0.5) * 2.0
        let bubbleRelevance = (homeBubble + awayBubble) / 2.0

        let contenderRelevance = Math.Min(homePlayoff, awayPlayoff)

        let relevance = Math.Max(bubbleRelevance, contenderRelevance)

        let effectiveCloseness = components.ClosenessScore * relevance

        let score =
            (components.StakesScore * stakesWeight) +
            (components.QualityScore * qualityWeight) +
            (effectiveCloseness * closenessWeight)

        Math.Clamp(score, 0.0, 100.0)

    member this.CalculateHypeScoreAsync(game: Game, ?ct: CancellationToken) : Task<Nullable<float>> =
        let ct = defaultArg ct CancellationToken.None

        task {
            if game.Status <> "scheduled" then
                return Nullable<float>()
            else

            use activity = activitySource.StartActivity("HypeScore.Calculate")

            if not (isNull activity) then
                activity.SetTag("game.id", game.Id) |> ignore
                activity.SetTag("game.sport", game.Sport) |> ignore
                activity.SetTag("game.teams", $"{game.AwayTeamAbbreviation}@{game.HomeTeamAbbreviation}") |> ignore

            let! components = this.CalculateComponentsAsync(game, ct)

            if isNull (box components) || not components.IsComplete then
                return Nullable<float>()
            else

            let score = this.CalculateFinalScore(game.Sport, components)

            if not (isNull activity) then
                activity.SetTag("hype.score", score) |> ignore

            return Nullable<float>(score)
        }

    member this.GetHypeComponentsAsync(game: Game, ?ct: CancellationToken) : Task<HypeComponents> =
        let ct = defaultArg ct CancellationToken.None

        task {
            if game.Status <> "scheduled" then
                return Unchecked.defaultof<HypeComponents>
            else

            return! this.CalculateComponentsAsync(game, ct)
        }
