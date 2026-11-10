namespace backend.Services.Espn

open System
open System.Net.Http
open System.Text.Json
open backend.Data
open backend.Models
open backend.Services
open Microsoft.EntityFrameworkCore
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging

type EspnStandingsService
    (httpClient: Net.Http.HttpClient,
     logger: ILogger<EspnStandingsService>,
     serviceProvider: IServiceProvider) =

    static let standingsBaseUrl = EspnSiteApi.url "/apis/v2/sports"

    static let formatDivisionName (sport: string) (conference: string) (division: string) =
        if String.IsNullOrEmpty(division) then
            conference
        else
            match sport with
            | "nfl" ->
                let confAbbr = if conference.Contains("American", StringComparison.OrdinalIgnoreCase) then "AFC" else "NFC"
                $"{confAbbr} {division}"
            | "mlb" ->
                let leagueAbbr = if conference.Contains("American", StringComparison.OrdinalIgnoreCase) then "AL" else "NL"
                $"{leagueAbbr} {division}"
            | _ -> division

    static let getNflSeason () =
        let now = DateTime.UtcNow
        if now.Month >= 9 then now.Year else now.Year - 1

    static let getNbaSeason () =
        let now = DateTime.UtcNow
        if now.Month >= 10 then now.Year + 1 else now.Year

    static let getNhlSeason () =
        let now = DateTime.UtcNow
        if now.Month >= 10 then now.Year + 1 else now.Year

    member private _.ProcessDivisionStandings
        (db: ExcitementDbContext,
         sport: string,
         divisionName: string,
         conferenceName: string,
         standings: EspnStandingsData,
         season: int,
         ct: Threading.CancellationToken) =
        task {
            if isNull (box standings) || isNull (box standings.Entries) then
                return 0
            else

            let mutable count = 0
            let mutable rank = 1

            for entry in standings.Entries do
                if not (isNull (box entry.Team)) && not (isNull entry.Team.Abbreviation) then
                    let abbr = entry.Team.Abbreviation
                    let! existing =
                        db.TeamStandings.FirstOrDefaultAsync(
                            (fun s -> s.Sport = sport && s.TeamAbbreviation = abbr && s.Season = season), ct)

                    let mutable conferenceRank: Nullable<int> = Nullable()
                    if not (isNull (box entry.Stats)) then
                        let playoffSeed = entry.Stats |> Seq.tryFind (fun s -> s.Name = "playoffSeed")
                        match playoffSeed with
                        | Some seed -> conferenceRank <- Nullable(int seed.Value)
                        | None -> ()

                    let mutable wins: Nullable<int> = Nullable()
                    let mutable losses: Nullable<int> = Nullable()
                    let mutable ties: Nullable<int> = Nullable()

                    if not (isNull (box entry.Stats)) then
                        let winsStat = entry.Stats |> Seq.tryFind (fun s -> s.Name = "wins")
                        let lossesStat = entry.Stats |> Seq.tryFind (fun s -> s.Name = "losses")
                        let tiesStat = entry.Stats |> Seq.tryFind (fun s -> s.Name = "ties")

                        match winsStat with Some w -> wins <- Nullable(int w.Value) | None -> ()
                        match lossesStat with Some l -> losses <- Nullable(int l.Value) | None -> ()
                        match tiesStat with Some t -> ties <- Nullable(int t.Value) | None -> ()

                    if not (isNull (box existing)) then
                        existing.Division <- divisionName
                        existing.Conference <- conferenceName
                        existing.DivisionRank <- rank
                        existing.ConferenceRank <- conferenceRank
                        existing.TeamName <- entry.Team.DisplayName
                        existing.Wins <- wins
                        existing.Losses <- losses
                        existing.Ties <- ties
                        existing.FetchedAt <- DateTime.UtcNow
                    else
                        let standing = TeamStanding(Sport = sport, TeamAbbreviation = abbr)
                        standing.TeamName <- entry.Team.DisplayName
                        standing.Division <- divisionName
                        standing.Conference <- conferenceName
                        standing.DivisionRank <- rank
                        standing.ConferenceRank <- conferenceRank
                        standing.Wins <- wins
                        standing.Losses <- losses
                        standing.Ties <- ties
                        standing.Season <- season
                        standing.FetchedAt <- DateTime.UtcNow
                        db.TeamStandings.Add(standing) |> ignore

                    rank <- rank + 1
                    count <- count + 1

            return count
        }

    member private this.RefreshProStandingsAsync(sport: string, path: string, season: int, ct: Threading.CancellationToken) =
        task {
            try
                let url = $"{standingsBaseUrl}/{path}/standings?season={season}"
                let! response = httpClient.GetStringAsync(url, ct)
                let data = JsonSerializer.Deserialize<EspnStandingsResponse>(response)

                if isNull (box data) || isNull (box data.Children) then
                    logger.LogWarning("No standings data returned for {Sport}", sport)
                    return 0
                else

                use scope = serviceProvider.CreateScope()
                let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()

                let mutable count = 0

                for conference in data.Children do
                    let conferenceName =
                        if not (isNull conference.Name) then conference.Name
                        elif not (isNull conference.Abbreviation) then conference.Abbreviation
                        else ""

                    if not (isNull (box conference.Children)) then
                        for division in conference.Children do
                            let divisionName = formatDivisionName sport conferenceName division.Name
                            let! divCount = this.ProcessDivisionStandings(db, sport, divisionName, conferenceName, division.Standings, season, ct)
                            count <- count + divCount
                    elif not (isNull (box conference.Standings)) then
                        let! divCount = this.ProcessDivisionStandings(db, sport, conferenceName, conferenceName, conference.Standings, season, ct)
                        count <- count + divCount

                let! _ = db.SaveChangesAsync(ct)
                logger.LogInformation("Refreshed {Sport} standings for {Count} teams (season {Season})", sport.ToUpper(), count, season)

                return count
            with
            | :? OperationCanceledException when ct.IsCancellationRequested -> return raise (OperationCanceledException(ct))
            | :? HttpRequestException as ex when not (ProviderFailure.Is ex) ->
                logger.LogWarning(ex, "Failed to refresh {Sport} standings from ESPN", sport)
                return 0
            | :? JsonException as ex ->
                logger.LogWarning(ex, "Failed to refresh {Sport} standings from ESPN", sport)
                return 0
            | ex ->
                logger.LogError(ex, "Failed to refresh {Sport} standings from ESPN", sport)
                return 0
        }

    member this.RefreshNflStandingsAsync(?ct: Threading.CancellationToken) =
        let ct = defaultArg ct Threading.CancellationToken.None
        let season = getNflSeason ()
        this.RefreshProStandingsAsync("nfl", "football/nfl", season, ct)

    member this.RefreshNbaStandingsAsync(?ct: Threading.CancellationToken) =
        let ct = defaultArg ct Threading.CancellationToken.None
        let season = getNbaSeason ()
        this.RefreshProStandingsAsync("nba", "basketball/nba", season, ct)

    member this.RefreshNhlStandingsAsync(?ct: Threading.CancellationToken) =
        let ct = defaultArg ct Threading.CancellationToken.None
        let season = getNhlSeason ()
        this.RefreshProStandingsAsync("nhl", "hockey/nhl", season, ct)

    member this.RefreshMlbStandingsAsync(?ct: Threading.CancellationToken) =
        let ct = defaultArg ct Threading.CancellationToken.None
        let season = DateTime.UtcNow.Year
        this.RefreshProStandingsAsync("mlb", "baseball/mlb", season, ct)
