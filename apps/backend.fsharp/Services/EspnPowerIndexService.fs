namespace backend.Services.Espn

open System
open System.Collections.Generic
open System.Linq
open System.Text.Json
open backend.Data
open backend.Models
open backend.Services
open Microsoft.EntityFrameworkCore
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging

type EspnPowerIndexService [<ActivatorUtilitiesConstructor>]
    (httpClient: Net.Http.HttpClient,
     logger: ILogger<EspnPowerIndexService>,
     serviceProvider: IServiceProvider,
     timeProvider: TimeProvider) =

    static let baseUrl = EspnSiteApi.url "/apis/fitt/v3/sports"

    static let sportPaths =
        dict [
            "nba", "basketball/nba"
            "nfl", "football/nfl"
        ]

    static let extractRating (team: EspnPowerIndexTeam) (sport: string) : Nullable<float> =
        let categoryName = if sport = "nfl" then "fpi" else "bpi"
        let category =
            if isNull (box team.Categories) then None
            else team.Categories |> Seq.tryFind (fun c -> c.Name = categoryName)
        match category with
        | Some cat when not (isNull (box cat.Values)) && cat.Values.Count > 0 ->
            cat.Values.[0]
        | _ -> Nullable()

    static let buildColumnMappings (rootCategories: ResizeArray<EspnCategory>) =
        let mappings = Dictionary<struct (string * string), int>()
        if not (isNull (box rootCategories)) then
            for category in rootCategories do
                if not (isNull (box category.Names)) then
                    for i in 0 .. category.Names.Count - 1 do
                        let fieldName = category.Names.[i]
                        if not (String.IsNullOrEmpty(fieldName)) then
                            mappings.[struct (category.Name, fieldName)] <- i
        mappings

    static let extractRank (team: EspnPowerIndexTeam) (sport: string) (columnMappings: Dictionary<struct (string * string), int>) : Nullable<int> =
        let categoryName = if sport = "nfl" then "fpi" else "bpi"
        let rankFieldName = if sport = "nfl" then "fpirank" else "bpirank"
        match columnMappings.TryGetValue(struct (categoryName, rankFieldName)) with
        | true, index ->
            let category =
                if isNull (box team.Categories) then None
                else team.Categories |> Seq.tryFind (fun c -> c.Name = categoryName)
            match category with
            | Some cat when not (isNull (box cat.Values)) && index < cat.Values.Count ->
                let rankValue = cat.Values.[index]
                if rankValue.HasValue && Double.IsFinite(rankValue.Value) && rankValue.Value = Math.Floor(rankValue.Value)
                   && rankValue.Value >= 1.0 && rankValue.Value <= 32.0 then Nullable(int rankValue.Value) else Nullable()
            | _ -> Nullable()
        | _ -> Nullable()

    new(httpClient, logger, serviceProvider) = EspnPowerIndexService(httpClient, logger, serviceProvider, TimeProvider.System)

    member private _.RefreshRatingsAsync(sport: string, season: int, ct: Threading.CancellationToken) =
        task {
            match sportPaths.TryGetValue(sport) with
            | false, _ ->
                logger.LogWarning("Unsupported sport for ESPN Power Index: {Sport}", sport)
                return 0
            | true, path ->

            let allTeams = ResizeArray<EspnPowerIndexTeam>()
            let mutable rootCategories: ResizeArray<EspnCategory> = null
            let mutable page = 1
            let mutable totalPages = 1
            let mutable verified = true

            try
                let mutable keepGoing = true
                while keepGoing && page <= totalPages do
                    let url = $"{baseUrl}/{path}/powerindex?region=us&lang=en&season={season}&page={page}&limit=50"
                    let! response = httpClient.GetStringAsync(url, ct)
                    let data =
                        try
                            use document = JsonDocument.Parse(response)
                            let mutable sourceSeason = Unchecked.defaultof<JsonElement>
                            let mutable year = Unchecked.defaultof<JsonElement>
                            let mutable value = 0
                            let mutable currentValues = Unchecked.defaultof<JsonElement>
                            let mutable dataYear = Unchecked.defaultof<JsonElement>
                            let mutable returnedSeason = 0
                            verified <- verified && document.RootElement.ValueKind = JsonValueKind.Object
                                        && document.RootElement.TryGetProperty("requestedSeason", &sourceSeason)
                                        && sourceSeason.ValueKind = JsonValueKind.Object && sourceSeason.TryGetProperty("year", &year)
                                        && year.ValueKind = JsonValueKind.Number && year.TryGetInt32(&value) && value = season
                                        && document.RootElement.TryGetProperty("currentValues", &currentValues) && currentValues.ValueKind = JsonValueKind.Object
                                        && currentValues.TryGetProperty("season", &dataYear) && dataYear.ValueKind = JsonValueKind.Number
                                        && dataYear.TryGetInt32(&returnedSeason) && returnedSeason = season
                            JsonSerializer.Deserialize<EspnPowerIndexResponse>(response)
                        with :? JsonException ->
                            verified <- false
                            keepGoing <- false
                            Unchecked.defaultof<EspnPowerIndexResponse>

                    if not (isNull (box data)) && not (isNull (box data.Teams)) then
                        allTeams.AddRange(data.Teams)

                    if page = 1 && not (isNull (box data)) && not (isNull (box data.Categories)) then
                        rootCategories <- data.Categories

                    if not (isNull (box data)) && not (isNull (box data.Pagination)) then
                        totalPages <- data.Pagination.Pages
                        if totalPages < 1 || totalPages > 5 then
                            verified <- false
                            keepGoing <- false

                    page <- page + 1

                    do! Threading.Tasks.Task.Delay(100, ct)

                    if page > totalPages then
                        keepGoing <- false

                let columnMappings = buildColumnMappings rootCategories

                use scope = serviceProvider.CreateScope()
                let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()

                let source = if sport = "nfl" then "espn_fpi" else "espn_bpi"
                let mutable count = 0
                let expected = backend.Scoring.ProHypePolicy.TeamCount(sport, season)
                verified <- verified && expected.HasValue && allTeams.Count = expected.Value
                            && (allTeams |> Seq.forall (fun t -> not (isNull (box t)) && not (isNull (box t.Team)) && not (String.IsNullOrWhiteSpace(t.Team.Abbreviation))))
                            && (allTeams |> Seq.map (fun t -> t.Team.Abbreviation) |> Seq.distinct |> Seq.length) = allTeams.Count
                let! previous = db.TeamRatings.Where(fun r -> r.Sport = sport && r.Season = season && r.Source = source).ToListAsync(ct)
                for row in previous do
                    row.PowerRank <- Nullable()
                    row.SourceSeason <- Nullable()
                    row.FetchedAt <- timeProvider.GetUtcNow().UtcDateTime

                for team in (if verified then allTeams else ResizeArray()) do
                    let abbr = team.Team.Abbreviation
                    if not (String.IsNullOrEmpty(abbr)) then
                        let! existing =
                            db.TeamRatings.FirstOrDefaultAsync(
                                (fun r -> r.Sport = sport && r.TeamAbbreviation = abbr && r.Season = season && r.Source = source), ct)

                        let rating = extractRating team sport
                        let rank = extractRank team sport columnMappings

                        if not (isNull (box existing)) then
                            if sport = "nfl" then
                                existing.FpiRating <- rating
                            else
                                existing.BpiRating <- rating

                            existing.PowerRank <- rank
                            existing.FetchedAt <- timeProvider.GetUtcNow().UtcDateTime
                            existing.SourceSeason <- Nullable season
                        else
                            let newRating = TeamRating(Sport = sport, TeamAbbreviation = abbr, Source = source)
                            newRating.TeamName <- team.Team.DisplayName
                            newRating.PowerRank <- rank
                            newRating.Season <- season
                            newRating.FetchedAt <- timeProvider.GetUtcNow().UtcDateTime
                            newRating.SourceSeason <- Nullable season

                            if sport = "nfl" then
                                newRating.FpiRating <- rating
                            else
                                newRating.BpiRating <- rating

                            db.TeamRatings.Add(newRating) |> ignore

                        count <- count + 1

                let! _ = db.SaveChangesAsync(ct)
                logger.LogInformation("Refreshed {Sport} {Source} ratings for {Count} teams (season {Season})",
                    sport.ToUpper(), source, count, season)

                return count
            with
            | :? OperationCanceledException when ct.IsCancellationRequested -> return raise (OperationCanceledException(ct))
            | ex ->
                logger.Log((if DatabaseFailure.Is ex || ProviderFailure.Is ex then LogLevel.Error else LogLevel.Warning), ex, "Failed to refresh {Sport} ratings from ESPN", sport)
                return 0
        }

    member this.RefreshNbaRatingsAsync(?ct: Threading.CancellationToken) =
        let ct = defaultArg ct Threading.CancellationToken.None
        task {
            let mutable count = 0
            for season in backend.Services.PlayoffRatingPolicy.refreshSeasons "nba" (timeProvider.GetUtcNow().UtcDateTime) do
                let! updated = this.RefreshRatingsAsync("nba", season, ct)
                count <- count + updated
            return count
        }

    member this.RefreshNflRatingsAsync(?ct: Threading.CancellationToken) =
        let ct = defaultArg ct Threading.CancellationToken.None
        let season = backend.Services.PlayoffRatingPolicy.seasonAt "nfl" (timeProvider.GetUtcNow().UtcDateTime)
        this.RefreshRatingsAsync("nfl", season, ct)
