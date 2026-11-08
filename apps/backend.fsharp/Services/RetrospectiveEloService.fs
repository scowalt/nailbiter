namespace backend.Services.Backfill

open System
open System.Collections.Generic
open System.Linq
open System.Threading
open System.Threading.Tasks
open backend.Data
open backend.Models
open Microsoft.EntityFrameworkCore
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging

type RetrospectiveEloService(logger: ILogger<RetrospectiveEloService>, serviceProvider: IServiceProvider) =

    static let initialElo = 1500.0
    static let kFactor = 20.0  
    static let homeAdvantage = 48.0  

    static let nflTeams =
        [| "ARI"; "ATL"; "BAL"; "BUF"; "CAR"; "CHI"; "CIN"; "CLE"
           "DAL"; "DEN"; "DET"; "GB"; "HOU"; "IND"; "JAX"; "KC"
           "LAC"; "LAR"; "LV"; "MIA"; "MIN"; "NE"; "NO"; "NYG"
           "NYJ"; "PHI"; "PIT"; "SEA"; "SF"; "TB"; "TEN"; "WSH" |]

    static let calculateExpected (ratingA: float) (ratingB: float) =
        1.0 / (1.0 + Math.Pow(10.0, (ratingB - ratingA) / 400.0))

    static let calculatePowerRank (teamElos: Dictionary<string, float>) (team: string) =
        let sortedTeams =
            teamElos
            |> Seq.sortByDescending (fun kvp -> kvp.Value)
            |> Seq.map (fun kvp -> kvp.Key)
            |> Seq.toList

        sortedTeams |> List.findIndex (fun t -> t = team) |> (+) 1

    static let normalizeTeamAbbr (abbr: string) =
        match abbr.ToUpper() with
        | "JAC" -> "JAX"
        | "LA" -> "LAR"
        | "WAS" -> "WSH"
        | upper -> upper

    member _.CalculateSeasonEloAsync(seasonYear: int, ?ct: CancellationToken) : Task<int> =
        let ct = defaultArg ct CancellationToken.None

        task {
            logger.LogInformation(
                "Calculating retrospective Elo ratings for {Year} NFL season",
                seasonYear
            )

            use scope = serviceProvider.CreateScope()
            let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()

            let teamElos = Dictionary<string, float>()

            for team in nflTeams do
                teamElos.[team] <- initialElo

            let! games =
                db.Games
                    .Where(fun (g: Game) ->
                        g.Sport = "nfl"
                        && g.SeasonYear = Nullable seasonYear
                        && g.Status = "final"
                        && g.HomeScore <> Nullable()
                        && g.AwayScore <> Nullable())
                    .OrderBy(fun (g: Game) -> g.ScheduledStart)
                    .ToListAsync(ct)

            logger.LogInformation("Processing {Count} completed games for Elo calculation", games.Count)

            let mutable ratingsCreated = 0
            let processedDates = HashSet<DateTime>()

            for game in games do
                if game.HomeScore.HasValue && game.AwayScore.HasValue then
                    let homeTeam = normalizeTeamAbbr game.HomeTeamAbbreviation
                    let awayTeam = normalizeTeamAbbr game.AwayTeamAbbreviation

                    if not (teamElos.ContainsKey(homeTeam)) || not (teamElos.ContainsKey(awayTeam)) then
                        logger.LogWarning(
                            "Unknown team in game {GameId}: {Home} vs {Away}",
                            game.Id,
                            homeTeam,
                            awayTeam
                        )
                    else
                        let homeElo = teamElos.[homeTeam]
                        let awayElo = teamElos.[awayTeam]

                        let homeExpected = calculateExpected (homeElo + homeAdvantage) awayElo
                        let awayExpected = 1.0 - homeExpected

                        let homeActual, awayActual =
                            if game.HomeScore.Value > game.AwayScore.Value then 1.0, 0.0
                            elif game.HomeScore.Value < game.AwayScore.Value then 0.0, 1.0
                            else 0.5, 0.5

                        let homeNewElo = homeElo + kFactor * (homeActual - homeExpected)
                        let awayNewElo = awayElo + kFactor * (awayActual - awayExpected)

                        teamElos.[homeTeam] <- homeNewElo
                        teamElos.[awayTeam] <- awayNewElo

                        let gameDate = game.ScheduledStart.Date

                        if not (processedDates.Contains(gameDate)) then
                            processedDates.Add(gameDate) |> ignore

                            for kvp in teamElos do
                                let rating =
                                    TeamRating(
                                        Sport = "nfl",
                                        TeamAbbreviation = kvp.Key,
                                        EloRating = Nullable kvp.Value,
                                        PowerRank = Nullable(calculatePowerRank teamElos kvp.Key),
                                        Source = "retrospective_elo",
                                        Season = seasonYear,
                                        FetchedAt = gameDate
                                    )

                                db.TeamRatings.Add(rating) |> ignore
                                ratingsCreated <- ratingsCreated + 1

                        logger.LogDebug(
                            "Game {GameId}: {Away} ({AwayElo:F0}) @ {Home} ({HomeElo:F0}) -> {AwayNew:F0} / {HomeNew:F0}",
                            game.Id,
                            awayTeam,
                            awayElo,
                            homeTeam,
                            homeElo,
                            awayNewElo,
                            homeNewElo
                        )

            let lastGame = games |> Seq.tryLast

            let finalDate =
                match lastGame with
                | Some g -> g.ScheduledStart.Date.AddDays(1.0)
                | None -> DateTime.UtcNow.Date

            for kvp in teamElos do
                let rating =
                    TeamRating(
                        Sport = "nfl",
                        TeamAbbreviation = kvp.Key,
                        EloRating = Nullable kvp.Value,
                        PowerRank = Nullable(calculatePowerRank teamElos kvp.Key),
                        Source = "retrospective_elo",
                        Season = seasonYear,
                        FetchedAt = finalDate
                    )

                db.TeamRatings.Add(rating) |> ignore
                ratingsCreated <- ratingsCreated + 1

            let! _ = db.SaveChangesAsync(ct)

            logger.LogInformation(
                "Created {Count} retrospective Elo ratings for {Year} NFL season",
                ratingsCreated,
                seasonYear
            )

            return ratingsCreated
        }
