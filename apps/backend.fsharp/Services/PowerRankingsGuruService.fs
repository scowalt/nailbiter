namespace backend.Services.PowerRankingsGuru

open System
open System.Collections.Generic
open System.Globalization
open System.Linq
open System.Threading
open System.Threading.Tasks
open AngleSharp
open AngleSharp.Dom
open backend.Data
open backend.Models
open backend.Services
open Microsoft.EntityFrameworkCore
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging

module private PowerRankingsGuruMappings =

    let sportPaths =
        let d = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        d.["nfl"] <- "/nfl/team-power-rankings.php"
        d.["nba"] <- "/nba/team-power-rankings.php"
        d.["nhl"] <- "/nhl/team-power-rankings.php"
        d.["mlb"] <- "/mlb/team-power-rankings.php"
        d

    let normalizeAbbreviation (abbr: string) (sport: string) =
        match sport, abbr.ToUpper() with
        | "nba", "BKN" -> "BKN"
        | "nba", "BRK" -> "BKN"
        | "nba", "GS" -> "GS"
        | "nba", "GSW" -> "GS"
        | "nba", "NY" -> "NY"
        | "nba", "NYK" -> "NY"
        | "nba", "NO" -> "NO"
        | "nba", "NOP" -> "NO"
        | "nba", "SA" -> "SA"
        | "nba", "SAS" -> "SA"
        | "nba", "UTAH" -> "UTAH"
        | "nba", "UTA" -> "UTAH"
        | "nfl", "JAX" -> "JAX"
        | "nfl", "JAC" -> "JAX"
        | "nfl", "LV" -> "LV"
        | "nfl", "OAK" -> "LV"
        | "nfl", "LAR" -> "LAR"
        | "nfl", "LA" -> "LAR"
        | "nfl", "WSH" -> "WSH"
        | "nfl", "WAS" -> "WSH"
        | "nhl", "LA" -> "LA"
        | "nhl", "LAK" -> "LA"
        | "nhl", "NJ" -> "NJ"
        | "nhl", "NJD" -> "NJ"
        | "nhl", "SJ" -> "SJ"
        | "nhl", "SJS" -> "SJ"
        | "nhl", "TB" -> "TB"
        | "nhl", "TBL" -> "TB"
        | "nhl", "VGK" -> "VGK"
        | "nhl", "VGS" -> "VGK"
        | "nhl", "WSH" -> "WSH"
        | "nhl", "WAS" -> "WSH"
        | "nhl", "UTA" -> "UTAH"
        | "mlb", "OAK" -> "ATH"
        | _ -> abbr.ToUpper()

[<AllowNullLiteral>]
type private PowerRankingTeamData() =
    member val Rank = 0 with get, set
    member val TeamName = "" with get, set
    member val Abbreviation = "" with get, set
    member val PlayoffProbability: Nullable<float> = Nullable() with get, set
    member val ChampionshipProbability: Nullable<float> = Nullable() with get, set

type PowerRankingsGuruService [<ActivatorUtilitiesConstructor>]
    (
        httpClient: System.Net.Http.HttpClient,
        logger: ILogger<PowerRankingsGuruService>,
        serviceProvider: IServiceProvider,
        timeProvider: TimeProvider
    ) =

    static let baseUrl = "https://powerrankingsguru.com"
    static let userAgent = "excitement-score-bot/1.0"

    do
        httpClient.DefaultRequestHeaders.Clear()
        httpClient.DefaultRequestHeaders.Add("User-Agent", userAgent)

    new(httpClient, logger, serviceProvider) =
        PowerRankingsGuruService(httpClient, logger, serviceProvider, TimeProvider.System)

    member private _.TryParsePercent(text: string) =
        let text = text.Replace("%", "").Trim()

        match Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, percent -> Some(percent / 100.0)
        | _ -> None

    member private this.ParseProbability(text: string) =
        if String.IsNullOrWhiteSpace(text) then
            None
        else
            let mutable text = text.Trim()

            if text.StartsWith(">", StringComparison.Ordinal) then
                text <- text.Substring(1)

                match this.TryParsePercent(text) with
                | Some high -> Some(Math.Min(high + 0.005, 1.0))
                | None -> None
            elif text.StartsWith("<", StringComparison.Ordinal) then
                text <- text.Substring(1)

                match this.TryParsePercent(text) with
                | Some low -> Some(Math.Max(low - 0.005, 0.0))
                | None -> None
            else
                this.TryParsePercent(text)

    member private _.BuildHeatChartAbbrLookup(document: IDocument) =
        let lookup = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        let heatTable = document.QuerySelector("table.heat-chart")

        if not (isNull heatTable) then
            for row in heatTable.QuerySelectorAll("tbody tr") do
                let nameEl = row.QuerySelector("span.full-name")
                let abbrEl = row.QuerySelector("span.abbrv")

                if not (isNull nameEl) && not (isNull abbrEl) then
                    let name = nameEl.TextContent.Trim()
                    let abbr = abbrEl.TextContent.Trim()

                    if not (String.IsNullOrEmpty(name)) && not (String.IsNullOrEmpty(abbr)) then
                        lookup.[name] <- abbr

        lookup

    member private this.ParseRankingsTableAsync
        (
            html: string,
            sport: string,
            season: int,
            ct: CancellationToken
        ) : Task<ResizeArray<PowerRankingTeamData>> =
        task {
            let teams = ResizeArray<PowerRankingTeamData>()

            try
                let config = Configuration.Default
                let context = BrowsingContext.New(config)
                let! document = context.OpenAsync((fun req -> req.Content(html) |> ignore), ct)

                let heading = document.QuerySelector("h1.content-title")
                let sourceMatches =
                    not (isNull heading)
                    && System.Text.RegularExpressions.Regex.IsMatch(
                        heading.TextContent.Trim(), $"^{season}\\s+Composite\\s+{sport.ToUpperInvariant()}\\s+Power\\s+Rankings$")

                if not sourceMatches then
                    logger.LogWarning("Rejected PowerRankingsGuru {Sport} rankings: source season does not match {Season}", sport, season)
                else

                let table =
                    let t = document.QuerySelector("table.rankings-table")
                    if not (isNull t) then t else document.QuerySelector("table.rankings-table-v2")

                if isNull table then
                    logger.LogWarning(
                        "No rankings table found in PowerRankingsGuru HTML for {Sport}",
                        sport
                    )
                else
                    let isV2 = table.ClassList.Contains("rankings-table-v2")

                    let abbrLookup =
                        if isV2 then this.BuildHeatChartAbbrLookup(document)
                        else Dictionary<string, string>()

                    let rows = table.QuerySelectorAll("tbody tr")

                    for row in rows do
                        let rankCell = row.QuerySelector("td.rank-v2")

                        if not (isNull rankCell) then
                            match Int32.TryParse(rankCell.TextContent.Trim()) with
                            | true, rank ->
                                let fullNameEl = row.QuerySelector("span.full-name")
                                let abbrEl = row.QuerySelector("span.abbrv")

                                let fullName, abbr =
                                    if not (isNull fullNameEl) && not (isNull abbrEl) then
                                        fullNameEl.TextContent.Trim(), abbrEl.TextContent.Trim()
                                    elif isV2 then
                                        let cells = row.QuerySelectorAll("td")

                                        if cells.Length > 1 then
                                            let divEl = cells.[1].QuerySelector("div")
                                            let rawText =
                                                if not (isNull divEl) then divEl.TextContent.Trim()
                                                else cells.[1].TextContent.Trim()

                                            let teamName =
                                                System.Text.RegularExpressions.Regex.Replace(
                                                    rawText, @"\s+\d+-\d+(-\d+)?$", ""
                                                ).Trim()

                                            let lookupAbbr =
                                                match abbrLookup.TryGetValue(teamName) with
                                                | true, a -> a
                                                | _ -> ""

                                            teamName, lookupAbbr
                                        else
                                            "", ""
                                    else
                                        (if isNull fullNameEl then "" else fullNameEl.TextContent.Trim()),
                                        (if isNull abbrEl then "" else abbrEl.TextContent.Trim())

                                if not (String.IsNullOrEmpty(abbr)) then
                                    let normalizedAbbr =
                                        PowerRankingsGuruMappings.normalizeAbbreviation abbr sport

                                    let playoffOddsCell = row.QuerySelector("td.playoff-odds")

                                    let playoffProb =
                                        if not (isNull playoffOddsCell) then
                                            this.ParseProbability(playoffOddsCell.TextContent)
                                        elif isV2 then
                                            let cells = row.QuerySelectorAll("td")
                                            if cells.Length >= 9 then this.ParseProbability(cells.[cells.Length - 2].TextContent)
                                            else None
                                        else
                                            None

                                    let champOddsCell = row.QuerySelector("td.champ-odds")

                                    let champProb =
                                        if not (isNull champOddsCell) then
                                            this.ParseProbability(champOddsCell.TextContent)
                                        elif isV2 then
                                            let cells = row.QuerySelectorAll("td")
                                            if cells.Length >= 9 then this.ParseProbability(cells.[cells.Length - 1].TextContent)
                                            else None
                                        else
                                            None

                                    let team = PowerRankingTeamData()
                                    team.Rank <- rank

                                    team.TeamName <-
                                        if String.IsNullOrEmpty(fullName) then abbr else fullName

                                    team.Abbreviation <- normalizedAbbr

                                    team.PlayoffProbability <-
                                        match playoffProb with
                                        | Some v -> Nullable(v)
                                        | None -> Nullable()

                                    team.ChampionshipProbability <-
                                        match champProb with
                                        | Some v -> Nullable(v)
                                        | None -> Nullable()

                                    teams.Add(team)
                                elif isV2 && not (String.IsNullOrEmpty(fullName)) then
                                    logger.LogWarning(
                                        "No abbreviation found for team '{TeamName}' in PowerRankingsGuru v2 heat chart for {Sport}",
                                        fullName,
                                        sport
                                    )
                            | _ -> ()
            with
            | :? OperationCanceledException -> logger.LogDebug("PowerRankingsGuru HTML parsing cancelled for {Sport}", sport)
            | ex ->
                logger.LogError(ex, "Failed to parse PowerRankingsGuru HTML for {Sport}", sport)

            return teams
        }

    member private this.RefreshRatingsAsync
        (
            sport: string,
            season: int,
            ct: CancellationToken
        ) : Task<int> =
        task {
            match PowerRankingsGuruMappings.sportPaths.TryGetValue(sport) with
            | false, _ ->
                logger.LogWarning("Unsupported sport for PowerRankingsGuru: {Sport}", sport)
                return 0
            | true, path ->
                try
                    let url = $"{baseUrl}{path}"
                    let! html = httpClient.GetStringAsync(url, ct)
                    let! teams = this.ParseRankingsTableAsync(html, sport, season, ct)

                    use invalidationScope = serviceProvider.CreateScope()
                    let invalidationDb = invalidationScope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
                    let! previous = invalidationDb.TeamRatings.Where(fun r -> r.Sport = sport && r.Season = season && r.Source = "powerrankingsguru").ToListAsync(ct)
                    let expected = backend.Scoring.ProHypePolicy.TeamCount(sport, season)
                    let complete = expected.HasValue && teams.Count > 0
                                   && (teams |> Seq.map (fun t -> t.Abbreviation) |> Seq.distinct |> Seq.length) = teams.Count
                                   && (teams |> Seq.forall (fun t -> t.Rank >= 1 && t.Rank <= expected.Value))
                    for row in previous do
                        if not complete || not (teams |> Seq.exists (fun t -> t.Abbreviation = row.TeamAbbreviation)) then
                            row.PowerRank <- Nullable()
                            row.SourceSeason <- Nullable()
                            row.FetchedAt <- timeProvider.GetUtcNow().UtcDateTime
                    let! _ = invalidationDb.SaveChangesAsync(ct)

                    if not complete then
                        logger.LogWarning(
                            "No teams parsed from PowerRankingsGuru for {Sport}",
                            sport
                        )

                        return 0
                    else
                        use scope = serviceProvider.CreateScope()

                        let db =
                            scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()

                        let uniqueTeams =
                            teams
                            |> Seq.filter (fun t -> not (String.IsNullOrEmpty(t.Abbreviation)))
                            |> Seq.distinctBy (fun t -> t.Abbreviation)
                            |> Seq.toList

                        let mutable count = 0

                        for team in uniqueTeams do
                            let! existing =
                                db.TeamRatings.FirstOrDefaultAsync(
                                    (fun r ->
                                        r.Sport = sport
                                        && r.TeamAbbreviation = team.Abbreviation
                                        && r.Season = season
                                        && r.Source = "powerrankingsguru"),
                                    ct
                                )

                            if not (isNull (box existing)) then
                                existing.PowerRank <- team.Rank
                                existing.PlayoffProbability <- team.PlayoffProbability
                                existing.ChampionshipProbability <- team.ChampionshipProbability
                                existing.FetchedAt <- timeProvider.GetUtcNow().UtcDateTime
                                existing.SourceSeason <- Nullable season
                            else
                                let rating = TeamRating(Sport = sport, TeamAbbreviation = team.Abbreviation, Source = "powerrankingsguru")
                                rating.TeamName <- team.TeamName
                                rating.PowerRank <- team.Rank
                                rating.PlayoffProbability <- team.PlayoffProbability
                                rating.ChampionshipProbability <- team.ChampionshipProbability
                                rating.Season <- season
                                rating.FetchedAt <- timeProvider.GetUtcNow().UtcDateTime
                                rating.SourceSeason <- Nullable season
                                db.TeamRatings.Add(rating) |> ignore

                            count <- count + 1

                        let! _ = db.SaveChangesAsync(ct)

                        logger.LogInformation(
                            "Refreshed {Sport} PowerRankingsGuru ratings for {Count} teams (season {Season})",
                            sport.ToUpper(),
                            count,
                            season
                        )

                        return count
                with
                | :? OperationCanceledException ->
                    logger.LogDebug("PowerRankingsGuru {Sport} ratings refresh cancelled", sport)
                    return 0
                | ex ->
                    logger.LogError(
                        ex,
                        "Failed to refresh {Sport} ratings from PowerRankingsGuru",
                        sport
                    )

                    return 0
        }

    member this.RefreshNflRatingsAsync(?ct: CancellationToken) : Task<int> =
        let ct = defaultArg ct CancellationToken.None

        let season = NflRatingPolicy.seasonAt (timeProvider.GetUtcNow().UtcDateTime)
        this.RefreshRatingsAsync("nfl", season, ct)

    member this.RefreshNbaRatingsAsync(?ct: CancellationToken) : Task<int> =
        let ct = defaultArg ct CancellationToken.None

        task {
            let mutable count = 0
            for season in PlayoffRatingPolicy.refreshSeasons "nba" (timeProvider.GetUtcNow().UtcDateTime) do
                let! updated = this.RefreshRatingsAsync("nba", season, ct)
                count <- count + updated
            return count
        }

    member this.RefreshNhlRatingsAsync(?ct: CancellationToken) : Task<int> =
        let ct = defaultArg ct CancellationToken.None

        task {
            let mutable count = 0
            for season in PlayoffRatingPolicy.refreshSeasons "nhl" (timeProvider.GetUtcNow().UtcDateTime) do
                let! updated = this.RefreshRatingsAsync("nhl", season, ct)
                count <- count + updated
            return count
        }

    member this.RefreshMlbRatingsAsync(?ct: CancellationToken) : Task<int> =
        let ct = defaultArg ct CancellationToken.None
        this.RefreshRatingsAsync("mlb", timeProvider.GetUtcNow().Year, ct)
