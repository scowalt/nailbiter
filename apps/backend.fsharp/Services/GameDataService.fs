namespace backend.Services

open System
open System.Collections.Generic
open System.Linq
open System.Threading
open System.Threading.Tasks
open backend.Data
open backend.Models
open backend.GraphQL.Types
open Backend.FSharp
open Microsoft.EntityFrameworkCore
open Microsoft.Extensions.Options

type GameDataService(db: ExcitementDbContext, timeProvider: TimeProvider, optionsAccessor: IOptions<PlayoffRatingsOptions>, selectionCache: PlayoffRatingPolicy.PlayoffSelectionCache) =

    let playoffOptions = if isNull (box optionsAccessor) then PlayoffRatingsOptions() else optionsAccessor.Value
    let publicGames = db.Games.Where(GameVisibilityPolicy.Visible)

    new(db, timeProvider) = GameDataService(db, timeProvider, Options.Create(PlayoffRatingsOptions()), null)

    new(db) = GameDataService(db, TimeProvider.System)

    member _.GetTodaysGamesAsync(ct: CancellationToken) = task {
        let eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York")
        let nowEt = TimeZoneInfo.ConvertTimeFromUtc(timeProvider.GetUtcNow().UtcDateTime, eastern)
        let todayEt = nowEt.Date
        let etOffset = eastern.GetUtcOffset(DateTime.SpecifyKind(todayEt, DateTimeKind.Unspecified))
        let todayStart = DateTimeOffset(todayEt, etOffset).UtcDateTime
        let todayEnd = todayStart.AddDays(1.0)

        let! games =
            publicGames
                .Where(fun g -> g.ScheduledStart >= todayStart && g.ScheduledStart < todayEnd && g.Status <> "canceled")
                .OrderBy(fun g -> g.ScheduledStart)
                .ToListAsync(ct)

        return games
    }

    member _.GetLiveGamesAsync(ct: CancellationToken) = task {
        let! games =
            publicGames
                .Where(fun g -> g.Status = "in_progress")
                .OrderByDescending(fun g -> g.ScheduledStart)
                .ToListAsync(ct)
        return games
    }

    member private _.BuildWorldCupCardsAsync(games: Game list, ct: CancellationToken) = task {
        let result = Collections.Generic.List<WorldCupCardData>()
        let gameIds = games |> List.map (fun g -> g.Id)

        if gameIds.IsEmpty then
            return result
        else
            let! latestSnapshots =
                db.OddsSnapshots
                    .Where(fun o -> gameIds.Contains(o.GameId))
                    .ToListAsync(ct)

            let snapshotsByGame =
                latestSnapshots
                |> Seq.groupBy (fun o -> o.GameId)
                |> Seq.map (fun (gameId, snapshots) -> gameId, snapshots |> Seq.toArray)
                |> dict

            let! pregameOdds =
                db.PregameOdds
                    .Where(fun o -> gameIds.Contains(o.GameId))
                    .ToListAsync(ct)

            let pregameByGame =
                pregameOdds
                |> Seq.filter (fun o -> o.DrawWinProbability.HasValue)
                |> Seq.groupBy (fun o -> o.GameId)
                |> Seq.map (fun (gameId, odds) -> gameId, odds |> Seq.maxBy (fun o -> o.FetchedAt))
                |> dict

            let score home draw away =
                Nullable<float>(HypeScoreService.CalculateThreeWayClosenessScore(home, draw, away))

            let emptyCard (game: Game) =
                { Game = game
                  HomeProbability = Nullable<float>()
                  DrawProbability = Nullable<float>()
                  AwayProbability = Nullable<float>()
                  HomeMoneyline = Nullable<int>()
                  DrawMoneyline = Nullable<int>()
                  AwayMoneyline = Nullable<int>()
                  NailbiterScore = Nullable<float>()
                  GameClock = null
                  Period = Nullable<int>()
                  Source = null
                  OddsTimestamp = Nullable<DateTime>() }

            let hasCompleteThreeWayOdds (snapshot: OddsSnapshot) =
                snapshot.DrawWinProbability.HasValue && snapshot.DrawMoneyline.HasValue

            let hasCompleteAdvanceOdds (snapshot: OddsSnapshot) =
                not snapshot.DrawWinProbability.HasValue
                && snapshot.HomeMoneyline <> 0
                && snapshot.AwayMoneyline <> 0

            let fromSnapshot (game: Game) (snapshot: OddsSnapshot) =
                if game.IsOvertime && hasCompleteAdvanceOdds snapshot then
                    { Game = game
                      HomeProbability = Nullable<float>(snapshot.HomeWinProbability)
                      DrawProbability = Nullable<float>()
                      AwayProbability = Nullable<float>(snapshot.AwayWinProbability)
                      HomeMoneyline = Nullable<int>(snapshot.HomeMoneyline)
                      DrawMoneyline = Nullable<int>()
                      AwayMoneyline = Nullable<int>(snapshot.AwayMoneyline)
                      NailbiterScore = Nullable<float>()
                      GameClock = snapshot.GameClock
                      Period = snapshot.Period
                      Source = snapshot.Source
                      OddsTimestamp = Nullable<DateTime>(snapshot.Timestamp) }
                elif not game.IsOvertime && hasCompleteThreeWayOdds snapshot then
                    { Game = game
                      HomeProbability = Nullable<float>(snapshot.HomeWinProbability)
                      DrawProbability = Nullable<float>(snapshot.DrawWinProbability.Value)
                      AwayProbability = Nullable<float>(snapshot.AwayWinProbability)
                      HomeMoneyline = Nullable<int>(snapshot.HomeMoneyline)
                      DrawMoneyline = Nullable<int>(snapshot.DrawMoneyline.Value)
                      AwayMoneyline = Nullable<int>(snapshot.AwayMoneyline)
                      NailbiterScore = score snapshot.HomeWinProbability snapshot.DrawWinProbability.Value snapshot.AwayWinProbability
                      GameClock = snapshot.GameClock
                      Period = snapshot.Period
                      Source = snapshot.Source
                      OddsTimestamp = Nullable<DateTime>(snapshot.Timestamp) }
                else emptyCard game

            let fromPregame (game: Game) (pregame: PregameOdds) =
                if pregame.DrawWinProbability.HasValue && pregame.DrawMoneyline.HasValue then
                    { Game = game
                      HomeProbability = Nullable<float>(pregame.HomeWinProbability)
                      DrawProbability = Nullable<float>(pregame.DrawWinProbability.Value)
                      AwayProbability = Nullable<float>(pregame.AwayWinProbability)
                      HomeMoneyline = pregame.HomeMoneyline
                      DrawMoneyline = Nullable<int>(pregame.DrawMoneyline.Value)
                      AwayMoneyline = pregame.AwayMoneyline
                      NailbiterScore = score pregame.HomeWinProbability pregame.DrawWinProbability.Value pregame.AwayWinProbability
                      GameClock = null
                      Period = Nullable<int>()
                      Source = pregame.Source
                      OddsTimestamp = Nullable<DateTime>(pregame.FetchedAt) }
                else emptyCard game

            let latestSnapshotFor (game: Game) =
                match snapshotsByGame.TryGetValue(game.Id) with
                | true, snapshots ->
                    snapshots
                    |> Seq.filter (if game.IsOvertime then hasCompleteAdvanceOdds else hasCompleteThreeWayOdds)
                    |> Seq.sortByDescending (fun snapshot -> struct (snapshot.Timestamp, snapshot.Id))
                    |> Seq.tryHead
                    |> Option.filter (fun snapshot -> GameWinnerProbabilityPolicy.FreshForDisplay(snapshot, timeProvider.GetUtcNow().UtcDateTime))
                | _ -> None

            for game in games do
                match latestSnapshotFor game with
                | Some snapshot -> result.Add(fromSnapshot game snapshot)
                | None when game.Status = "scheduled" && not game.IsOvertime ->
                    match pregameByGame.TryGetValue(game.Id) with
                    | true, pregame -> result.Add(fromPregame game pregame)
                    | _ -> result.Add(emptyCard game)
                | None -> result.Add(emptyCard game)

            return result
    }

    member this.GetWorldCupHomepageCardsAsync(ct: CancellationToken) = task {
        let sport = SportExtensions.ToDbValue(Sport.WORLD_CUP)
        let now = DateTime.UtcNow
        let scheduledGraceCutoff = now.AddHours(-3.0)

        let! liveGames =
            publicGames
                .Where(fun g -> g.Sport = sport && g.Status = "in_progress")
                .OrderBy(fun g -> g.ScheduledStart)
                .ToListAsync(ct)

        let! upcomingGames =
            publicGames
                .Where(fun g -> g.Sport = sport && g.Status = "scheduled" && g.ScheduledStart >= scheduledGraceCutoff)
                .OrderBy(fun g -> g.ScheduledStart)
                .Take(3)
                .ToListAsync(ct)

        let games =
            Seq.append liveGames upcomingGames
            |> Seq.distinctBy (fun g -> g.Id)
            |> Seq.sortBy (fun g -> (if g.Status = "in_progress" then 0 else 1), g.ScheduledStart)
            |> Seq.toList

        return! this.BuildWorldCupCardsAsync(games, ct)
    }

    member this.GetWorldCupPageCardsAsync(ct: CancellationToken) = task {
        let sport = SportExtensions.ToDbValue(Sport.WORLD_CUP)
        let now = DateTime.UtcNow
        let scheduledGraceCutoff = now.AddHours(-3.0)
        let eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York")
        let nowEt = TimeZoneInfo.ConvertTimeFromUtc(now, eastern)
        let todayEt = nowEt.Date
        let todayStart = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(todayEt, DateTimeKind.Unspecified), eastern)
        let todayEnd = todayStart.AddDays(1.0)

        let! games =
            publicGames
                .Where(fun g ->
                    g.Sport = sport
                    && g.Status <> "canceled"
                    && (g.Status = "in_progress"
                        || (g.Status = "scheduled" && g.ScheduledStart >= scheduledGraceCutoff)
                        || (g.Status = "final" && g.ScheduledStart >= todayStart && g.ScheduledStart < todayEnd)))
                .ToListAsync(ct)

        let orderedGames =
            games
            |> Seq.sortBy (fun g ->
                let bucket =
                    match g.Status with
                    | "in_progress" -> 0
                    | "scheduled" -> 1
                    | "final" -> 2
                    | _ -> 3
                bucket, g.ScheduledStart)
            |> Seq.toList

        return! this.BuildWorldCupCardsAsync(orderedGames, ct)
    }

    member this.GetWorldCupMatchCardAsync(gameId: string, ct: CancellationToken) = task {
        let sport = SportExtensions.ToDbValue(Sport.WORLD_CUP)
        let! game =
            publicGames
                .FirstOrDefaultAsync((fun g -> g.Id = gameId && g.Sport = sport && g.Status <> "canceled"), ct)

        if isNull game then
            return None
        else
            let! cards = this.BuildWorldCupCardsAsync([ game ], ct)
            return cards |> Seq.tryHead
    }

    member _.GetGamesByFilterAsync(sport: string, seasonYear: Nullable<int>, seasonType: Nullable<int>, weekNumber: Nullable<int>, ct: CancellationToken) = task {
        let query =
            publicGames.Where(fun g -> g.Sport = sport && g.Status <> "canceled")
            |> fun q -> if seasonYear.HasValue then q.Where(fun g -> g.SeasonYear = seasonYear) else q
            |> fun q -> if seasonType.HasValue then q.Where(fun g -> g.SeasonType = seasonType) else q
            |> fun q -> if weekNumber.HasValue then q.Where(fun g -> g.WeekNumber = weekNumber) else q

        let ordered = query.OrderBy(fun g -> g.ScheduledStart) :> IQueryable<Game>
        let bounded = if sport = "cfb" && weekNumber.HasValue then ordered else ordered.Take(100)
        let! games = bounded.ToListAsync(ct)
        return games
    }

    member _.GetGamesByDateRangeAsync(sport: string, startUtc: DateTime, endUtc: DateTime, ct: CancellationToken) = task {
        let! games =
            db.Games
                .FromSqlRaw(
                    ("SELECT * FROM games WHERE " + GameVisibilityPolicy.PublicSqlPredicate + " AND sport = {0} AND status <> 'canceled' AND (scheduled_start >= {1} AND scheduled_start < {2} OR status = 'in_progress') ORDER BY scheduled_start"),
                    sport :> obj, startUtc :> obj, endUtc :> obj)
                .AsNoTracking()
                .ToListAsync(ct)
        return games
    }

    member _.GetGameAsync(gameId: string, ct: CancellationToken) = task {
        let! game = publicGames.FirstOrDefaultAsync((fun g -> g.Id = gameId), ct)
        return game |> ofObj
    }

    member _.GetLatestOddsForGamesAsync(gameIds: string list, ct: CancellationToken) = task {
        if gameIds.IsEmpty then return Dictionary<string, OddsSnapshot>()
        else
        let ids = gameIds |> List.distinct |> List.toArray
        let! odds =
            db.OddsSnapshots
                .FromSqlRaw("SELECT DISTINCT ON (game_id) * FROM odds_snapshots WHERE game_id = ANY ({0}) ORDER BY game_id, timestamp DESC, id DESC", ids :> obj)
                .AsNoTracking()
                .ToDictionaryAsync((fun o -> o.GameId), ct)
        let expiredBounds = odds |> Seq.filter (fun entry -> not (GameWinnerProbabilityPolicy.FreshForDisplay(entry.Value, timeProvider.GetUtcNow().UtcDateTime))) |> Seq.map (fun entry -> entry.Key) |> Seq.toArray
        for id in expiredBounds do odds.Remove(id) |> ignore
        let! collegeGames = db.Games.AsNoTracking().Where(fun g -> gameIds.Contains(g.Id) && g.Sport = "cfb").ToListAsync(ct)
        for game in collegeGames do
            match odds.TryGetValue(game.Id) with
            | true, snapshot when not (CollegeFootballPolicy.UsableLiveOdds(game, snapshot, timeProvider.GetUtcNow().UtcDateTime)) -> odds.Remove(game.Id) |> ignore
            | _ -> ()
        return odds
    }

    member _.GetExcitementScoresForGamesAsync(gameIds: string list, ct: CancellationToken) = task {
        let! allSnapshots =
            db.OddsSnapshots
                .Where(fun o -> gameIds.Contains(o.GameId) && not o.IsHistorical && not o.Game.IsHistoricalImport)
                .OrderBy(fun o -> o.GameId)
                .ThenBy(fun o -> o.Timestamp)
                .ThenBy(fun o -> o.Id)
                .Select(fun o -> {| GameId = o.GameId; HomeWinProbability = o.HomeWinProbability; Sport = o.Game.Sport |})
                .ToListAsync(ct)

        let grouped =
            allSnapshots
            |> Seq.groupBy (fun o -> o.GameId)
            |> Seq.map (fun (k, v) -> k, ((Seq.head v).Sport, v |> Seq.map (fun o -> o.HomeWinProbability) |> Seq.toList))
            |> dict

        let result = Dictionary<string, Nullable<float>>()
        for id in gameIds do
            match grouped.TryGetValue(id) with
            | true, (sport, probs) -> result[id] <- ExcitementScore.Calculate sport probs
            | _ -> result[id] <- Nullable<float>()

        return result :> IDictionary<string, Nullable<float>>
    }

    member _.GetOddsSnapshotsAsync(gameId: string, ct: CancellationToken) = task {
        let! snapshots =
            db.OddsSnapshots
                .Where(fun o -> o.GameId = gameId)
                .OrderBy(fun o -> o.Timestamp)
                .ToListAsync(ct)
        return snapshots
    }

    member _.GetPregameOddsAsync(gameId: string, ct: CancellationToken) = task {
        let! odds = db.PregameOdds.FirstOrDefaultAsync((fun o -> o.GameId = gameId), ct)
        return odds |> ofObj
    }

    member _.GetCurrentWeekAsync(sport: string, ct: CancellationToken) = task {
        let now = timeProvider.GetUtcNow().UtcDateTime
        let weekAgo = now.AddDays(-7.0)
        let weekAhead = now.AddDays(7.0)

        let! currentGames =
            if sport = "nfl" || sport = "cfb" then
                db.Games
                    .FromSqlRaw(("SELECT * FROM games WHERE " + GameVisibilityPolicy.PublicSqlPredicate + " AND sport = {0} AND season_year IS NOT NULL AND season_type IS NOT NULL AND week_number IS NOT NULL AND status <> 'canceled' AND scheduled_start >= {1} AND scheduled_start <= {2} ORDER BY CASE WHEN status = 'in_progress' THEN 0 WHEN scheduled_start >= {3} THEN 1 ELSE 2 END, CASE WHEN scheduled_start >= {3} THEN scheduled_start END ASC, scheduled_start DESC LIMIT 1"), sport :> obj, weekAgo :> obj, weekAhead :> obj, now :> obj)
                    .AsNoTracking()
                    .ToListAsync(ct)
            else
                db.Games
                    .FromSqlRaw(("SELECT * FROM games WHERE " + GameVisibilityPolicy.PublicSqlPredicate + " AND sport = {0} AND season_year IS NOT NULL AND season_type IS NOT NULL AND scheduled_start >= {1} AND scheduled_start <= {2} ORDER BY scheduled_start DESC LIMIT 1"), sport :> obj, weekAgo :> obj, weekAhead :> obj)
                    .AsNoTracking()
                    .ToListAsync(ct)

        match currentGames |> Seq.tryHead with
        | Some g ->
            return Some {
                SeasonYear = g.SeasonYear.Value
                SeasonType = Nullable(g.SeasonType.Value)
                WeekNumber = g.WeekNumber |> Option.ofNullable |> Option.defaultValue 0
                WeekLabel = g.WeekLabel |> Option.ofObj |> Option.defaultValue ""
            }
        | None ->
            let! latestGames =
                db.Games
                    .FromSqlRaw(("SELECT * FROM games WHERE " + GameVisibilityPolicy.PublicSqlPredicate + " AND sport = {0} AND season_year IS NOT NULL AND season_type IS NOT NULL AND ({0} NOT IN ('nfl', 'cfb') OR (week_number IS NOT NULL AND status <> 'canceled')) ORDER BY scheduled_start DESC LIMIT 1"), sport :> obj)
                    .AsNoTracking()
                    .ToListAsync(ct)

            match latestGames |> Seq.tryHead with
            | Some g ->
                return Some {
                    SeasonYear = g.SeasonYear.Value
                    SeasonType = Nullable(g.SeasonType.Value)
                    WeekNumber = g.WeekNumber |> Option.ofNullable |> Option.defaultValue 0
                    WeekLabel = g.WeekLabel |> Option.ofObj |> Option.defaultValue ""
                }
            | None -> return None
    }

    member _.GetPlayoffBracketAsync(sport: string, seasonYear: int, ct: CancellationToken) = task {
        let sportLower = sport.ToLower()
        if sportLower = "cfb" then return None
        else

        let! postseasonGames =
            db.Games
                .FromSqlRaw(("SELECT * FROM games WHERE " + GameVisibilityPolicy.PublicSqlPredicate + " AND sport = {0} AND season_year = {1} AND season_type = 3 ORDER BY scheduled_start"), sportLower :> obj, seasonYear :> obj)
                .AsNoTracking()
                .ToListAsync(ct)

        if postseasonGames.Count = 0 then return None
        else

        let! allStandings =
            db.TeamStandings
                .Where(fun s -> s.Sport = sportLower)
                .GroupBy(fun s -> s.TeamAbbreviation)
                .Select(fun g -> g.OrderByDescending(fun s -> s.FetchedAt).First())
                .ToDictionaryAsync((fun s -> s.TeamAbbreviation), ct)

        let roundLabels = dict [ 1, "First Round"; 2, "Conference Semifinals"; 3, "Conference Finals"; 4, "Finals" ]

        let isRealTeam (abbr: string) =
            not (String.IsNullOrEmpty abbr) && abbr <> "TBD" && not (abbr.Contains('/'))

        let rounds =
            postseasonGames
            |> Seq.filter (fun g -> g.WeekNumber.HasValue)
            |> Seq.groupBy (fun g -> g.WeekNumber.Value)
            |> Seq.map (fun (roundNumber, games) ->
                let games = games |> Seq.toList

                let seriesFromRealGames =
                    games
                    |> List.filter (fun g -> isRealTeam g.HomeTeamAbbreviation && isRealTeam g.AwayTeamAbbreviation)
                    |> List.groupBy (fun g ->
                        let teams = [| g.HomeTeamAbbreviation; g.AwayTeamAbbreviation |]
                        Array.Sort(teams)
                        $"{teams[0]}|{teams[1]}")
                    |> List.map (fun (key, seriesGames) ->
                        let teamAbbrs = key.Split('|')
                        let teamA = teamAbbrs[0]
                        let teamB = teamAbbrs[1]

                        let teamAWins =
                            seriesGames |> List.filter (fun g ->
                                g.Status = "final" && g.HomeScore.HasValue && g.AwayScore.HasValue &&
                                ((g.HomeTeamAbbreviation = teamA && g.HomeScore.Value > g.AwayScore.Value) ||
                                 (g.AwayTeamAbbreviation = teamA && g.AwayScore.Value > g.HomeScore.Value)))
                            |> List.length

                        let teamBWins =
                            seriesGames |> List.filter (fun g ->
                                g.Status = "final" && g.HomeScore.HasValue && g.AwayScore.HasValue &&
                                ((g.HomeTeamAbbreviation = teamB && g.HomeScore.Value > g.AwayScore.Value) ||
                                 (g.AwayTeamAbbreviation = teamB && g.AwayScore.Value > g.HomeScore.Value)))
                            |> List.length

                        let infoA = TeamDivisionMappings.GetDivisionInfo(sport, teamA)
                        let infoB = TeamDivisionMappings.GetDivisionInfo(sport, teamB)
                        let conference = if not (String.IsNullOrEmpty infoA.Conference) then infoA.Conference else infoB.Conference

                        let seedA = match allStandings.TryGetValue(teamA) with | true, s -> s.ConferenceRank |> Option.ofNullable |> Option.defaultValue 0 | _ -> 0
                        let seedB = match allStandings.TryGetValue(teamB) with | true, s -> s.ConferenceRank |> Option.ofNullable |> Option.defaultValue 0 | _ -> 0

                        let higherTeam, lowerTeam, higherSeed, lowerSeed, higherWins, lowerWins =
                            if seedA > 0 && seedB > 0 && seedA < seedB then
                                teamA, teamB, seedA, seedB, teamAWins, teamBWins
                            elif seedA > 0 && seedB > 0 && seedB < seedA then
                                teamB, teamA, seedB, seedA, teamBWins, teamAWins
                            else
                                teamA, teamB, seedA, seedB, teamAWins, teamBWins

                        let winner =
                            if higherWins >= 4 then Some higherTeam
                            elif lowerWins >= 4 then Some lowerTeam
                            else None

                        { HigherSeedTeam = higherTeam
                          LowerSeedTeam = lowerTeam
                          HigherSeedNumber = higherSeed
                          LowerSeedNumber = lowerSeed
                          HigherSeedWins = higherWins
                          LowerSeedWins = lowerWins
                          Completed = higherWins >= 4 || lowerWins >= 4
                          Winner = winner
                          Conference = conference |> Option.ofObj |> Option.defaultValue "" })

                let label = match roundLabels.TryGetValue(roundNumber) with | true, l -> l | _ -> $"Round {roundNumber}"

                { Number = roundNumber
                  Label = label
                  Series = seriesFromRealGames |> List.sortBy (fun s -> s.HigherSeedNumber) })
            |> Seq.toList

        return Some {
            Sport = sport.ToUpper()
            SeasonYear = seasonYear
            Rounds = rounds
        }
    }

    member _.GetTeamRankInfosAsync(sport: string, teams: string list, seasonYear: Nullable<int>, seasonType: Nullable<int>, ct: CancellationToken) = task {
        let result = Dictionary<string, TeamRankInfo>()
        if teams.IsEmpty then return result
        else
        let teamKeys = teams |> List.collect (fun t -> [t; backend.Scoring.ProHypePolicy.TeamKey(sport, t)]) |> List.distinct
        let hasPlayoffs = PlayoffRatingPolicy.isSupported sport
        let now = if isNull selectionCache then timeProvider.GetUtcNow().UtcDateTime else selectionCache.EvaluationTime
        let season = seasonYear |> Option.ofNullable |> Option.defaultWith (fun () -> PlayoffRatingPolicy.seasonAt sport now)
        let rankSource = if sport = "nhl" then MoneyPuckRankings.Source else "powerrankingsguru"
        let! ratings =
            db.TeamRatings
                .Where(fun r -> r.Sport = sport && teamKeys.Contains(r.TeamAbbreviation)
                                && ((r.Source = rankSource && (not hasPlayoffs || r.Season = season))
                                    || (hasPlayoffs && r.Season = season && (r.Source = "kalshi" || (sport = "nhl" && (r.Source = PlayoffRatingPolicy.playoffStatusSource || r.Source = PlayoffRatingPolicy.moneyPuckQualificationSource)) || (sport = "cfb" && r.Source = PlayoffRatingPolicy.collegeNoMarketSource)))))
                .OrderByDescending(fun r -> r.FetchedAt)
                .ToListAsync(ct)
        let! standings =
            db.TeamStandings
                .Where(fun s -> s.Sport = sport && teams.Contains(s.TeamAbbreviation) && (not hasPlayoffs || s.Season = season))
                .OrderByDescending(fun s -> s.FetchedAt)
                .ToListAsync(ct)

        let ordinalSuffix n =
            if n >= 11 && n <= 13 then "th"
            else match n % 10 with | 1 -> "st" | 2 -> "nd" | 3 -> "rd" | _ -> "th"

        for team in teams do
            let teamRatings = ratings |> Seq.filter (fun r -> backend.Scoring.ProHypePolicy.TeamKey(sport, r.TeamAbbreviation) = backend.Scoring.ProHypePolicy.TeamKey(sport, team)) |> Seq.toList
            let ranking =
                teamRatings |> List.tryFind (fun r -> r.Source = rankSource)
                |> Option.filter (fun r -> not (backend.Scoring.ProHypePolicy.Supports sport) || backend.Scoring.ProHypePolicy.EligibleRank(r, sport, season, now))
            let powerRank = ranking |> Option.bind (fun r -> r.PowerRank |> Option.ofNullable)
            let playoffSelection =
                let request : PlayoffRatingPolicy.SelectionRequest = { Sport = sport; Season = season; Team = team; Now = now; Options = playoffOptions }
                PlayoffRatingPolicy.selectCached selectionCache request teamRatings
            let playoffSnapshot : PlayoffRatingPolicy.Snapshot option =
                PlayoffRatingPolicy.selectionToSnapshot playoffSelection
            let playoffOdds = playoffSnapshot |> Option.bind (fun s -> s.Probability)
            let playoffSeed =
                standings |> Seq.tryFind (fun s -> s.TeamAbbreviation = team)
                |> Option.bind (fun s -> s.ConferenceRank |> Option.ofNullable)
            let displayRank =
                if sport <> "cfb" && seasonType.HasValue && seasonType.Value = 3 then
                    playoffSeed |> Option.map (fun seed -> $"#{seed} seed")
                else
                    let parts = ResizeArray<string>()
                    powerRank |> Option.iter (fun pr -> parts.Add($"{pr}{ordinalSuffix pr} in {sport.ToUpper()}"))
                    match playoffOdds with
                    | Some po ->
                        let isFinal = playoffSnapshot |> Option.exists (fun s -> s.IsFinal)
                        let outlook = Backend.Rendering.FormatHelpers.formatPlayoffOutlook isFinal po
                        parts.Add(if sport = "cfb" then $"CFP {outlook}" elif isFinal then outlook
                                  elif playoffSelection.Source = Some PlayoffRatingPolicy.moneyPuckQualificationSource then $"{outlook} playoff odds (model estimate)"
                                  else $"{outlook} playoff odds")
                    | None when sport = "cfb" -> parts.Add("CFP unavailable")
                    | None when hasPlayoffs -> parts.Add("Playoff odds unavailable")
                    | _ -> ()
                    if parts.Count > 0 then Some(String.Join(" | ", parts)) else None
            if hasPlayoffs || powerRank.IsSome || playoffOdds.IsSome || playoffSeed.IsSome then
                result[team] <-
                    { PowerRank = powerRank; PlayoffOdds = playoffOdds
                      PlayoffFetchedAt = playoffSnapshot |> Option.map (fun s -> s.FetchedAt)
                      PlayoffIsFinal = playoffSnapshot |> Option.exists (fun s -> s.IsFinal)
                      PlayoffIsAssumed = playoffSnapshot |> Option.exists (fun s -> s.IsAssumed)
                      PlayoffSource = playoffSelection.Source |> Option.toObj
                      PlayoffEvidenceKind = QualificationProvenance.evidenceKind playoffSelection.Source
                      PlayoffSeed = playoffSeed; DisplayRank = displayRank }
        return result
    }

    member this.GetTeamRankInfoAsync(sport: string, teamAbbr: string, seasonYear: Nullable<int>, seasonType: Nullable<int>, ct: CancellationToken) : Task<TeamRankInfo option> = task {
        let! ranks = this.GetTeamRankInfosAsync(sport, [teamAbbr], seasonYear, seasonType, ct)
        return match ranks.TryGetValue(teamAbbr) with | true, rank -> Some rank | _ -> None
    }

    member _.GetAdjacentWeeksAsync(sport: string, seasonYear: int, seasonType: int, weekNumber: int, ct: CancellationToken) = task {
        let! distinctWeeks =
            db.Database
                .SqlQueryRaw<int>(
                    ("SELECT DISTINCT week_number AS \"Value\" FROM games WHERE " + GameVisibilityPolicy.PublicSqlPredicate + " AND sport = {0} AND season_year = {1} AND season_type = {2} AND week_number IS NOT NULL ORDER BY week_number"),
                    sport :> obj, seasonYear :> obj, seasonType :> obj)
                .ToListAsync(ct)

        let weekList = distinctWeeks |> Seq.toList
        let idx = weekList |> List.tryFindIndex (fun w -> w = weekNumber)

        let prev = idx |> Option.bind (fun i -> if i > 0 then Some weekList[i-1] else None)
        let next = idx |> Option.bind (fun i -> if i < weekList.Length - 1 then Some weekList[i+1] else None)

        return {| PrevWeek = prev; NextWeek = next |}
    }

    member this.GetDisplayRanksAsync(sport: string, teamAbbreviations: string list, seasonYear: Nullable<int>, seasonType: Nullable<int>, ct: CancellationToken) : Task<IDictionary<string, string>> = task {
        let! ranks = this.GetTeamRankInfosAsync(sport, teamAbbreviations, seasonYear, seasonType, ct)
        let result = Dictionary<string, string>()
        for entry in ranks do
            entry.Value.DisplayRank |> Option.iter (fun rank -> result[entry.Key] <- rank)
        return result :> IDictionary<string, string>
    }
