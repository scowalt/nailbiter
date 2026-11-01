namespace backend.Services.PlayoffStatus

open System
open System.Collections.Generic
open System.Globalization
open System.Linq
open System.Threading
open System.Threading.Tasks
open System.Text.Json
open System.Text.RegularExpressions
open AngleSharp
open AngleSharp.Dom
open backend.Data
open backend.Models
open backend.Services
open Microsoft.EntityFrameworkCore
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options

module private PlayoffStatusMappings =

    let standingsPages =
        let d = Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        d.["nfl"] <- [| "/nfl/afcstandings.html"; "/nfl/nfcstandings.html" |]
        d.["nba"] <- [| "/nba/easternstandings.html"; "/nba/westernstandings.html" |]
        d.["nhl"] <- [| "/nhl/easternstandings.html"; "/nhl/westernstandings.html" |]
        d.["mlb"] <- [| "/mlb/americanstandings.html"; "/mlb/nationalstandings.html" |]
        d

    let postseasonPages =
        let d = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        d.["nfl"] <- "/nfl/nflpostseasonprob.html"
        d.["nba"] <- "/nba/nbapostseasonprob.html"
        d.["nhl"] <- "/nhl/nhlpostseasonprob.html"
        d.["mlb"] <- "/mlb/mlbpostseasonprob.html"
        d

    let championshipColumnIndex =
        let d = Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        d.["nfl"] <- 5
        d.["nba"] <- 5
        d.["nhl"] <- 7
        d.["mlb"] <- 5
        d

[<AllowNullLiteral>]
type private PlayoffStatusTeamData() =
    member val TeamName = "" with get, set
    member val PlayoffProbability: Nullable<float> = Nullable() with get, set
    member val ChampionshipProbability: Nullable<float> = Nullable() with get, set

type private NhlModelObservation =
    { Team: string
      TeamName: string
      Probability: float option
      Reason: string }

type private NhlPostseasonObservation =
    { Team: string
      TeamName: string
      RoundOne: float option
      Reason: string }

type private NhlModelBatch =
    { Season: int
      PublishedAt: DateTime
      FetchedAt: DateTime
      Teams: Map<string, NhlModelObservation> }

[<AllowNullLiteral>]
type PlayoffStatusNhlRefreshGate(timeProvider: TimeProvider) =
    let gate = new SemaphoreSlim(1, 1)
    let mutable lastAttempt = Nullable<DateTime>()
    member _.TryEnterAsync(cooldown: TimeSpan, ct: CancellationToken) = task {
        let! entered = gate.WaitAsync(0, ct)
        if not entered then
            return false
        else
            let now = timeProvider.GetUtcNow().UtcDateTime
            if lastAttempt.HasValue && now - lastAttempt.Value < cooldown then
                gate.Release() |> ignore
                return false
            else
                lastAttempt <- Nullable now
                return true
    }
    member _.Exit() = gate.Release() |> ignore

type PlayoffStatusService
    (
        httpClient: System.Net.Http.HttpClient,
        logger: ILogger<PlayoffStatusService>,
        serviceProvider: IServiceProvider,
        timeProvider: TimeProvider,
        optionsAccessor: IOptions<PlayoffRatingsOptions>,
        nhlRefreshGate: PlayoffStatusNhlRefreshGate
    ) =

    let playoffOptions = if isNull (box optionsAccessor) then PlayoffRatingsOptions() else optionsAccessor.Value

    static let baseUrl = "https://www.playoffstatus.com"
    static let userAgent = "excitement-score-bot/1.0"
    static let nhlTeams =
        [| "ANA"; "BOS"; "BUF"; "CGY"; "CAR"; "CHI"; "COL"; "CBJ"; "DAL"; "DET"; "EDM"; "FLA"
           "LA"; "MIN"; "MTL"; "NSH"; "NJ"; "NYI"; "NYR"; "OTT"; "PHI"; "PIT"; "SJ"; "SEA"
           "STL"; "TB"; "TOR"; "UTAH"; "VAN"; "VGK"; "WSH"; "WPG" |]

    do
        httpClient.DefaultRequestHeaders.Clear()
        httpClient.DefaultRequestHeaders.Add("User-Agent", userAgent)

    new(httpClient, logger, serviceProvider) =
        let clock = TimeProvider.System
        PlayoffStatusService(httpClient, logger, serviceProvider, clock, Options.Create(PlayoffRatingsOptions()), PlayoffStatusNhlRefreshGate(clock))

    member private _.TryParsePercent(text: string) =
        let text = text.Replace("%", "").Trim()

        match Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, percent when Double.IsFinite(percent) && percent >= 0.0 && percent <= 100.0 -> Some(percent / 100.0)
        | _ -> None

    member private _.TryParseLegacyPercent(text: string) =
        let text = text.Replace("%", "").Trim()

        match Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, percent -> Some(percent / 100.0)
        | _ -> None

    member private this.ParseNhlProbability(text: string) =
        let text = if isNull text then "" else Regex.Replace(text.Trim(), @"\s+", "")

        if text.Equals(">99%", StringComparison.OrdinalIgnoreCase) || text.Equals(">99", StringComparison.OrdinalIgnoreCase) then
            Some 0.995
        elif text.Equals("<1%", StringComparison.OrdinalIgnoreCase) || text.Equals("<1", StringComparison.OrdinalIgnoreCase) then
            Some 0.005
        elif text.StartsWith(">", StringComparison.Ordinal) || text.StartsWith("<", StringComparison.Ordinal) then
            None
        else
            this.TryParsePercent(text)

    member private this.ParseLegacyProbability(text: string) =
        let text = if isNull text then "" else text.Trim()

        if text.Equals("X", StringComparison.OrdinalIgnoreCase) then
            Some 0.0
        elif text = "^" then
            Some 1.0
        elif text.StartsWith(">", StringComparison.Ordinal) then
            let inner = text.Substring(1)
            match this.TryParseLegacyPercent(inner) with
            | Some high -> Some(Math.Min(high + 0.005, 1.0))
            | None -> None
        elif text.StartsWith("<", StringComparison.Ordinal) then
            let inner = text.Substring(1)
            match this.TryParseLegacyPercent(inner) with
            | Some low -> Some(Math.Max(low - 0.005, 0.0))
            | None -> None
        else
            this.TryParseLegacyPercent(text)

    member private _.NormalizeText(text: string) =
        if isNull text then "" else Regex.Replace(text, @"\s+", " ").Trim()

    member private _.ColumnMatches(actual: string, expected: string) =
        actual.Equals(expected, StringComparison.OrdinalIgnoreCase)
        || (expected = "No Playoffs" && actual.Equals("No Playoff", StringComparison.OrdinalIgnoreCase))
        || (expected = "Round 1" && actual.Equals("First Round", StringComparison.OrdinalIgnoreCase))

    member private this.FindHeaderIndexes(rows: IElement list, requiredColumns: string list) =
        rows
        |> List.tryPick (fun row ->
            let cells = row.QuerySelectorAll("th,td") |> Seq.map (fun c -> this.NormalizeText(c.TextContent)) |> Seq.toArray
            let pairs =
                requiredColumns
                |> List.choose (fun column ->
                    cells |> Array.tryFindIndex (fun h -> this.ColumnMatches(h, column)) |> Option.map (fun idx -> column, idx))
            if pairs.Length = requiredColumns.Length then Some(Map.ofList pairs) else None)

    member private this.SeasonAssertions(text: string) =
        let text = this.NormalizeText(text)
        let seasons = ResizeArray<int>()
        for m in Regex.Matches(text, @"(?<!\d)(\d{4})\s*[-/]\s*(\d{2}|\d{4})(?!\d)") do
            let first = Int32.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)
            let secondText = m.Groups[2].Value
            let ending =
                if secondText.Length = 2 then
                    let century = (first / 100) * 100
                    let candidate = century + Int32.Parse(secondText, CultureInfo.InvariantCulture)
                    if candidate < first then candidate + 100 else candidate
                else Int32.Parse(secondText, CultureInfo.InvariantCulture)
            seasons.Add(ending)
        for m in Regex.Matches(text, @"(?<!\d)(\d{4})\s+NHL\s+Postseason(?!\d)", RegexOptions.IgnoreCase) do
            seasons.Add(Int32.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
        seasons |> Seq.toList

    member private this.SeasonMatches(season: int, document: IDocument) =
        let assertedSeasons =
            document.QuerySelectorAll("h1,h2,title,caption,[data-nailbiter-source-season-title]")
            |> Seq.collect (fun element -> this.SeasonAssertions(element.TextContent))
            |> Seq.toList
        not assertedSeasons.IsEmpty && assertedSeasons |> List.forall ((=) season)

    member private this.ParseNhlTeamRowsAsync(html: string, season: int, requiredColumns: string list, requireWildCards: bool, missingReason: string, ct: CancellationToken) : Task<Result<((string * string * Map<string, string>) list * bool), string>> = task {
        let context = BrowsingContext.New(Configuration.Default)
        let! document = context.OpenAsync((fun req -> req.Content(html) |> ignore), ct)
        if not (this.SeasonMatches(season, document)) then return Error "wrong_season"
        elif requireWildCards && not (Regex.IsMatch(html, "Wild\\s*Cards?|Wildcard", RegexOptions.IgnoreCase)) then return Error "wild_cards_unverified"
        else
            let mappings = PlayoffStatusTeamMappings.Nhl
            let values = ResizeArray<string * string * Map<string, string>>()
            let mutable problem = None
            let columns = "Team" :: requiredColumns
            let completeSignal =
                not requireWildCards ||
                ((not (isNull (document.QuerySelector("table [data-nailbiter-supported-complete='nhl-standings']"))))
                 && Regex.IsMatch(html, @"</table>\s*</body>\s*</html>\s*$", RegexOptions.IgnoreCase))
            let tryTable (table: IElement) =
                let rows = table.QuerySelectorAll("tr") |> Seq.toList
                match this.FindHeaderIndexes(rows, columns) with
                | None -> false
                | Some header ->
                    let teamIndex = header["Team"]
                    let maxIndex = header |> Map.toSeq |> Seq.map snd |> Seq.max
                    for row in rows do
                        let cells = row.QuerySelectorAll("td")
                        if cells.Length > maxIndex then
                            let teamCell = cells.[teamIndex]
                            let link = teamCell.QuerySelector("a")
                            let teamName = this.NormalizeText(if isNull link then teamCell.TextContent else link.TextContent)
                            if not (String.IsNullOrWhiteSpace teamName) then
                                match mappings.TryGetValue(teamName) with
                                | false, _ -> problem <- Some $"unmapped_team:{teamName}"
                                | true, abbr ->
                                    let cellValues =
                                        requiredColumns
                                        |> List.map (fun column -> column, cells.[header[column]].TextContent)
                                        |> Map.ofList
                                    values.Add(backend.Scoring.ProHypePolicy.TeamKey("nhl", abbr), teamName, cellValues)
                    true
            let found = document.QuerySelectorAll("table") |> Seq.exists tryTable
            match problem with
            | Some p -> return Error p
            | None when not found -> return Error missingReason
            | None -> return Ok (values |> Seq.toList, completeSignal)
    }

    member private this.ParseApprovedPublishedAtAsync(htmls: string list, ct: CancellationToken) : Task<DateTime option> = task {
        let tryParseApprovedInstant (value: string) =
            let value = value.Trim()
            let hasCompleteIsoDateTime =
                Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase)
            if not hasCompleteIsoDateTime then None
            else
                match DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None) with
                | true, dto -> Some dto.UtcDateTime
                | _ -> None
        let! values =
            htmls
            |> List.map (fun html -> task {
                let context = BrowsingContext.New(Configuration.Default)
                let! document = context.OpenAsync((fun req -> req.Content(html) |> ignore), ct)
                let meta = document.QuerySelector("meta[name='nailbiter-approved-source-published-at'], [data-nailbiter-approved-source-published-at]")
                if isNull meta then return None
                else
                    let value =
                        let content = meta.GetAttribute("content")
                        if String.IsNullOrWhiteSpace content then meta.GetAttribute("data-nailbiter-approved-source-published-at") else content
                    if String.IsNullOrWhiteSpace value then return None
                    else return tryParseApprovedInstant value })
            |> Task.WhenAll
        let parsed = values |> Seq.toList
        let distinct = parsed |> Seq.choose id |> Seq.distinct |> Seq.toList
        return if parsed.Length = htmls.Length && parsed |> List.forall Option.isSome then
                   match distinct with [published] -> Some published | _ -> None
               else None
    }

    member private this.ParseNhlStandingsAsync(html: string, season: int, ct: CancellationToken) : Task<Result<NhlModelObservation list * bool, string>> = task {
        let! rows = this.ParseNhlTeamRowsAsync(html, season, ["No Playoffs"], true, "qualification_table_missing", ct)
        match rows with
        | Error reason -> return Error reason
        | Ok (rows, completeSignal) ->
            let observations =
                rows
                |> List.map (fun (team, teamName, cells) ->
                    let noPlayoffs = this.ParseNhlProbability(cells["No Playoffs"])
                    { Team = team
                      TeamName = teamName
                      Probability = noPlayoffs |> Option.map (fun p -> 1.0 - p)
                      Reason = if noPlayoffs.IsSome then "accepted" else "malformed_probability" })
            return Ok (observations, completeSignal)
    }

    member private this.ParseNhlPostseasonAsync(html: string, season: int, ct: CancellationToken) : Task<Result<NhlPostseasonObservation list, string>> = task {
        let! rows = this.ParseNhlTeamRowsAsync(html, season, ["Round 1"; "No Playoffs"], false, "postseason_table_missing", ct)
        match rows with
        | Error reason -> return Error reason
        | Ok (rows, _) ->
            return
                rows
                |> List.map (fun (team, teamName, cells) ->
                    let roundOne = this.ParseNhlProbability(cells["Round 1"])
                    let noPlayoffs = this.ParseNhlProbability(cells["No Playoffs"])
                    let reason =
                        match roundOne, noPlayoffs with
                        | Some r, Some n when Math.Abs(r - (1.0 - n)) > 0.000001 -> "qualification_meaning_mismatch"
                        | Some _, Some _ -> "accepted"
                        | None, _ -> "malformed_round1_probability"
                        | _, None -> "malformed_no_playoffs_probability"
                    { Team = team
                      TeamName = teamName
                      RoundOne = if reason = "accepted" then roundOne else None
                      Reason = reason })
                |> Ok
    }

    member private this.FetchNhlModelBatchAsync(season: int, ct: CancellationToken) : Task<Result<NhlModelBatch, string>> = task {
        ct.ThrowIfCancellationRequested()
        let! eastern = httpClient.GetStringAsync(baseUrl + "/nhl/easternstandings.html", ct)
        let! western = httpClient.GetStringAsync(baseUrl + "/nhl/westernstandings.html", ct)
        let! postseason = httpClient.GetStringAsync(baseUrl + "/nhl/nhlpostseasonprob.html", ct)
        let! published = this.ParseApprovedPublishedAtAsync([eastern; western; postseason], ct)
        match published with
        | None -> return Error "ambiguous_publication_time"
        | Some publishedAt when publishedAt > timeProvider.GetUtcNow().UtcDateTime -> return Error "future_publication"
        | Some publishedAt ->
            let! eastRows = this.ParseNhlStandingsAsync(eastern, season, ct)
            let! westRows = this.ParseNhlStandingsAsync(western, season, ct)
            let! postseasonRows = this.ParseNhlPostseasonAsync(postseason, season, ct)
            match eastRows, westRows, postseasonRows with
            | Error e, _, _ | _, Error e, _ | _, _, Error e -> return Error e
            | Ok (eastRows, eastComplete), Ok (westRows, westComplete), Ok postseasonRows ->
                let standingsComplete = eastComplete && westComplete
                let standings = eastRows @ westRows
                let grouped = standings |> List.groupBy (fun o -> o.Team)
                let duplicates = grouped |> List.choose (fun (team, rows) -> if rows.Length > 1 then Some team else None)
                let postseasonGrouped = postseasonRows |> List.groupBy (fun o -> o.Team)
                let postseasonDuplicates = postseasonGrouped |> List.choose (fun (team, rows) -> if rows.Length > 1 then Some team else None)
                if not duplicates.IsEmpty then return Error("duplicate_team:" + String.Join(",", duplicates))
                elif not postseasonDuplicates.IsEmpty then return Error("duplicate_postseason_team:" + String.Join(",", postseasonDuplicates))
                else
                    let byTeam = grouped |> List.map (fun (team, rows) -> team, rows.Head) |> Map.ofList
                    let postseasonByTeam = postseasonGrouped |> List.map (fun (team, rows) -> team, rows.Head) |> Map.ofList
                    let standingsExtra = byTeam |> Map.toSeq |> Seq.map fst |> Seq.filter (fun team -> not (Array.contains team nhlTeams)) |> Seq.toArray
                    let standingsMissing = nhlTeams |> Array.filter (fun team -> not (byTeam.ContainsKey team))
                    let postseasonMissing = nhlTeams |> Array.filter (fun team -> not (postseasonByTeam.ContainsKey team))
                    let postseasonExtra = postseasonByTeam |> Map.toSeq |> Seq.map fst |> Seq.filter (fun team -> not (Array.contains team nhlTeams)) |> Seq.toArray
                    if standingsExtra.Length > 0 then return Error("unexpected_team:" + String.Join(",", standingsExtra))
                    elif (standingsMissing.Length > 0 || byTeam.Count <> nhlTeams.Length) && not standingsComplete then return Error "standings_incomplete"
                    elif postseasonMissing.Length > 0 then return Error("postseason_missing_team:" + String.Join(",", postseasonMissing))
                    elif postseasonExtra.Length > 0 then return Error("postseason_unexpected_team:" + String.Join(",", postseasonExtra))
                    elif postseasonByTeam.Count <> nhlTeams.Length then return Error "postseason_incomplete"
                    else
                        let observations =
                            nhlTeams
                            |> Array.map (fun team ->
                                let standings = Map.tryFind team byTeam
                                let postseason = Map.tryFind team postseasonByTeam
                                let teamName =
                                    match standings, postseason with
                                    | Some s, _ -> s.TeamName
                                    | _, Some p -> p.TeamName
                                    | _ -> team
                                let probability, reason =
                                    match standings, postseason with
                                    | None, _ -> None, "missing_standings_probability"
                                    | Some s, Some p ->
                                        match s.Probability, p.RoundOne with
                                        | Some a, Some b when Math.Abs(a - b) <= 0.000001 -> Some a, "accepted"
                                        | Some _, Some _ -> None, "qualification_meaning_mismatch"
                                        | None, _ -> None, s.Reason
                                        | _, None -> None, p.Reason
                                    | Some s, None -> None, if s.Probability.IsSome then "missing_postseason_corroboration" else s.Reason
                                team, { Team = team; TeamName = teamName; Probability = probability; Reason = reason })
                            |> Map.ofArray
                        return Ok { Season = season; PublishedAt = publishedAt; FetchedAt = timeProvider.GetUtcNow().UtcDateTime; Teams = observations }
    }

    member private _.CommitNhlModelBatchAsync(batch: NhlModelBatch, ct: CancellationToken) : Task<int> = task {
        use scope = serviceProvider.CreateScope()
        let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
        let! existing = db.TeamRatings.Where(fun r -> r.Sport = "nhl" && r.Season = batch.Season && r.Source = PlayoffRatingPolicy.playoffStatusSource).ToListAsync(ct)
        let newestStoredPublication =
            existing
            |> Seq.choose (fun row ->
                let e = PlayoffModelEvidence.Parse(row.PlayoffEvidenceJson)
                if isNull e || not e.PublishedAt.HasValue then None else Some e.PublishedAt.Value)
            |> Seq.sortDescending
            |> Seq.tryHead
        match newestStoredPublication with
        | Some published when published > batch.PublishedAt -> return 0
        | _ ->
            use! tx = db.Database.BeginTransactionAsync(ct)
            let byTeam = existing |> Seq.groupBy (fun r -> r.TeamAbbreviation) |> Seq.map (fun (team, rows) -> team, rows |> Seq.head) |> dict
            let mutable accepted = 0
            let batchAccepted = batch.Teams |> Seq.filter (fun kvp -> kvp.Value.Probability.IsSome) |> Seq.length
            let refreshId = Guid.NewGuid().ToString("N")
            for team in nhlTeams do
                let obs = batch.Teams[team]
                let row =
                    match byTeam.TryGetValue(team) with
                    | true, row -> row
                    | _ ->
                        let row = TeamRating(Sport = "nhl", TeamAbbreviation = team, Source = PlayoffRatingPolicy.playoffStatusSource, Season = batch.Season)
                        db.TeamRatings.Add(row) |> ignore
                        row
                row.TeamName <- obs.TeamName
                row.SourceSeason <- Nullable batch.Season
                row.PlayoffProbability <- obs.Probability |> Option.toNullable
                row.PlayoffProbabilityIsFinal <- false
                row.FetchedAt <- batch.FetchedAt
                if obs.Probability.IsSome then accepted <- accepted + 1
                let evidence = PlayoffModelEvidence(Version = 2, EvidenceKind = "model_projection",
                                                     ValidationPolicy = PlayoffRatingPolicy.playoffStatusValidationPolicy,
                                                     Provider = PlayoffRatingPolicy.playoffStatusSource, Sport = "nhl", Team = team,
                                                     Season = batch.Season, SeasonVerified = true, TeamVerified = true,
                                                     State = (if obs.Probability.IsSome then "estimate" else "unavailable"),
                                                     ReasonCode = obs.Reason,
                                                     PublishedAt = Nullable batch.PublishedAt,
                                                     FetchedAt = Nullable batch.FetchedAt,
                                                     RefreshId = refreshId,
                                                     AcceptedTeamCount = batchAccepted,
                                                     TotalTeamCount = nhlTeams.Length)
                row.PlayoffEvidenceJson <- JsonSerializer.Serialize(evidence)
            let! _ = db.SaveChangesAsync(ct)
            do! tx.CommitAsync(ct)
            return accepted
    }

    member private this.RefreshNhlRatingsForSeasonCoreAsync(season: int, ct: CancellationToken) : Task<int> = task {
        if not (playoffOptions.RefreshEnabledFor("nhl", season)) then
            logger.LogInformation("NHL PlayoffStatus model refresh is disabled for season {Season}", season)
            return 0
        else
            let gate = if isNull nhlRefreshGate then PlayoffStatusNhlRefreshGate(timeProvider) else nhlRefreshGate
            let! entered = gate.TryEnterAsync(PlayoffRatingPolicy.modelRefreshCooldown, ct)
            if not entered then
                logger.LogInformation("NHL PlayoffStatus model refresh skipped by single-flight/cooldown for season {Season}", season)
                return 0
            else
                try
                    try
                        let! batch = this.FetchNhlModelBatchAsync(season, ct)
                        match batch with
                        | Error reason ->
                            logger.LogWarning("Rejected NHL PlayoffStatus model refresh for season {Season}: {Reason}", season, reason)
                            return 0
                        | Ok batch ->
                            let! count = this.CommitNhlModelBatchAsync(batch, ct)
                            let rejectionCounts =
                                batch.Teams
                                |> Seq.map (fun kvp -> kvp.Value.Reason)
                                |> Seq.countBy id
                                |> Seq.map (fun (reason, n) -> $"{reason}:{n}")
                                |> String.concat ","
                            let publicationAgeHours = (timeProvider.GetUtcNow().UtcDateTime - batch.PublishedAt).TotalHours
                            logger.LogInformation("Accepted NHL PlayoffStatus model refresh for {Count}/{Teams} teams, season {Season}, published {PublishedAt:O}, publicationAgeHours {PublicationAgeHours}, reasons {RejectionCounts}", count, nhlTeams.Length, season, batch.PublishedAt, publicationAgeHours, rejectionCounts)
                            return count
                    with
                    | :? OperationCanceledException when ct.IsCancellationRequested -> return raise (OperationCanceledException(ct))
                    | ex ->
                        logger.LogWarning(ex, "Failed NHL PlayoffStatus model refresh for season {Season}", season)
                        return 0
                finally
                    gate.Exit()
    }

    member private this.ParseStandingsTableAsync
        (
            html: string,
            sport: string,
            ct: CancellationToken
        ) : Task<ResizeArray<PlayoffStatusTeamData>> =
        task {
            let teams = ResizeArray<PlayoffStatusTeamData>()

            try
                let config = Configuration.Default
                let context = BrowsingContext.New(config)
                let! document = context.OpenAsync((fun req -> req.Content(html) |> ignore), ct)

                let table = document.QuerySelector("table")

                if isNull table then
                    logger.LogWarning(
                        "No table found in PlayoffStatus standings HTML for {Sport}",
                        sport
                    )
                else
                    let rows = table.QuerySelectorAll("tr[data-idx]")

                    if rows.Length = 0 then
                        logger.LogWarning(
                            "No data rows found in PlayoffStatus standings table for {Sport}",
                            sport
                        )
                    else
                        for row in rows do
                            let cells = row.QuerySelectorAll("td")

                            if cells.Length >= 5 then
                                let teamCell = cells.[0]
                                let teamLink = teamCell.QuerySelector("a")

                                let teamName =
                                    if not (isNull teamLink) then
                                        teamLink.TextContent.Trim()
                                    else
                                        teamCell.TextContent.Trim()

                                if not (String.IsNullOrWhiteSpace(teamName)) then
                                    let lastCell = cells.[cells.Length - 1]
                                    let noPlayoffsProb = this.ParseLegacyProbability(lastCell.TextContent)

                                    let playoffProb =
                                        noPlayoffsProb |> Option.map (fun p -> 1.0 - p)

                                    let team = PlayoffStatusTeamData()
                                    team.TeamName <- teamName

                                    team.PlayoffProbability <-
                                        match playoffProb with
                                        | Some v -> Nullable(v)
                                        | None -> Nullable()

                                    teams.Add(team)
            with
            | :? OperationCanceledException -> logger.LogDebug("PlayoffStatus standings HTML parsing cancelled for {Sport}", sport)
            | ex ->
                logger.LogError(
                    ex,
                    "Failed to parse PlayoffStatus standings HTML for {Sport}",
                    sport
                )

            return teams
        }

    member private this.EnrichWithChampionshipOddsAsync
        (
            html: string,
            sport: string,
            teams: ResizeArray<PlayoffStatusTeamData>,
            ct: CancellationToken
        ) : Task =
        task {
            try
                let config = Configuration.Default
                let context = BrowsingContext.New(config)
                let! document = context.OpenAsync((fun req -> req.Content(html) |> ignore), ct)

                let table = document.QuerySelector("table")

                if not (isNull table) then
                    let rows = table.QuerySelectorAll("tr")

                    match PlayoffStatusMappings.championshipColumnIndex.TryGetValue(sport) with
                    | true, champIdx ->
                        let champOdds =
                            Dictionary<string, Nullable<float>>(StringComparer.OrdinalIgnoreCase)

                        for i in 2 .. rows.Length - 1 do
                            let row = rows.[i]
                            let cells = row.QuerySelectorAll("td")

                            if cells.Length > champIdx then
                                let teamCell = cells.[0]
                                let teamLink = teamCell.QuerySelector("a")

                                let teamName =
                                    if not (isNull teamLink) then
                                        teamLink.TextContent.Trim()
                                    else
                                        teamCell.TextContent.Trim()

                                if not (String.IsNullOrWhiteSpace(teamName)) then
                                    let champProb =
                                        this.ParseLegacyProbability(cells.[champIdx].TextContent)

                                    champOdds.[teamName] <-
                                        match champProb with
                                        | Some v -> Nullable(v)
                                        | None -> Nullable()

                        for team in teams do
                            match champOdds.TryGetValue(team.TeamName) with
                            | true, prob -> team.ChampionshipProbability <- prob
                            | _ -> ()
                    | _ -> ()
            with
            | :? OperationCanceledException -> logger.LogDebug("PlayoffStatus postseason HTML parsing cancelled for {Sport}", sport)
            | ex ->
                logger.LogError(
                    ex,
                    "Failed to parse PlayoffStatus postseason HTML for {Sport}",
                    sport
                )
        }
        :> Task

    member private this.RefreshRatingsAsync
        (
            sport: string,
            season: int,
            ct: CancellationToken
        ) : Task<int> =
        task {
            match PlayoffStatusMappings.standingsPages.TryGetValue(sport) with
            | false, _ ->
                logger.LogWarning("Unsupported sport for PlayoffStatus: {Sport}", sport)
                return 0
            | true, standingsPaths ->
                try
                    let allTeams = ResizeArray<PlayoffStatusTeamData>()

                    for path in standingsPaths do
                        let url = $"{baseUrl}{path}"
                        let! html = httpClient.GetStringAsync(url, ct)
                        let! teams = this.ParseStandingsTableAsync(html, sport, ct)
                        allTeams.AddRange(teams)
                        do! Task.Delay(100, ct)

                    match PlayoffStatusMappings.postseasonPages.TryGetValue(sport) with
                    | true, postseasonPath ->
                        let url = $"{baseUrl}{postseasonPath}"
                        let! html = httpClient.GetStringAsync(url, ct)
                        do! this.EnrichWithChampionshipOddsAsync(html, sport, allTeams, ct)
                    | _ -> ()

                    if allTeams.Count = 0 then
                        logger.LogWarning("No teams parsed from PlayoffStatus for {Sport}", sport)
                        return 0
                    else
                        use scope = serviceProvider.CreateScope()

                        let db =
                            scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()

                        let mappings = PlayoffStatusTeamMappings.GetMappingForSport(sport)

                        if isNull mappings then
                            logger.LogWarning("No team mappings for sport: {Sport}", sport)
                            return 0
                        else
                            let mutable count = 0

                            for team in allTeams do
                                match mappings.TryGetValue(team.TeamName) with
                                | true, abbr ->
                                    let! existing =
                                        db.TeamRatings.FirstOrDefaultAsync(
                                            (fun r ->
                                                r.Sport = sport
                                                && r.TeamAbbreviation = abbr
                                                && r.Season = season
                                                && r.Source = "playoffstatus"),
                                            ct
                                        )

                                    if not (isNull (box existing)) then
                                        existing.PlayoffProbability <- team.PlayoffProbability
                                        existing.ChampionshipProbability <- team.ChampionshipProbability
                                        existing.FetchedAt <- DateTime.UtcNow
                                    else
                                        let rating = TeamRating(Sport = sport, TeamAbbreviation = abbr, Source = "playoffstatus")
                                        rating.TeamName <- team.TeamName
                                        rating.PlayoffProbability <- team.PlayoffProbability
                                        rating.ChampionshipProbability <- team.ChampionshipProbability
                                        rating.Season <- season
                                        rating.FetchedAt <- DateTime.UtcNow
                                        db.TeamRatings.Add(rating) |> ignore

                                    count <- count + 1
                                | _ ->
                                    logger.LogDebug(
                                        "Unmapped team from PlayoffStatus: {Team} ({Sport})",
                                        team.TeamName,
                                        sport
                                    )

                            let! _ = db.SaveChangesAsync(ct)

                            logger.LogInformation(
                                "Refreshed {Sport} PlayoffStatus ratings for {Count} teams (season {Season})",
                                sport.ToUpper(),
                                count,
                                season
                            )

                            return count
                with
                | :? OperationCanceledException ->
                    logger.LogDebug("PlayoffStatus {Sport} ratings refresh cancelled", sport)
                    return 0
                | ex ->
                    logger.LogError(
                        ex,
                        "Failed to refresh {Sport} ratings from PlayoffStatus",
                        sport
                    )

                    return 0
        }

    member this.RefreshNflRatingsAsync(?ct: CancellationToken) : Task<int> =
        let ct = defaultArg ct CancellationToken.None

        let season =
            if DateTime.UtcNow.Month >= 9 then
                DateTime.UtcNow.Year
            else
                DateTime.UtcNow.Year - 1

        this.RefreshRatingsAsync("nfl", season, ct)

    member this.RefreshNbaRatingsAsync(?ct: CancellationToken) : Task<int> =
        let ct = defaultArg ct CancellationToken.None

        let season =
            if DateTime.UtcNow.Month >= 10 then
                DateTime.UtcNow.Year + 1
            else
                DateTime.UtcNow.Year

        this.RefreshRatingsAsync("nba", season, ct)

    member this.RefreshNhlRatingsForSeasonAsync(season: int, ?ct: CancellationToken) : Task<int> =
        let ct = defaultArg ct CancellationToken.None
        this.RefreshNhlRatingsForSeasonCoreAsync(season, ct)

    member this.RefreshNhlRatingsAsync(?ct: CancellationToken) : Task<int> =
        let ct = defaultArg ct CancellationToken.None
        let now = timeProvider.GetUtcNow().UtcDateTime
        let season = if now.Month >= 10 then now.Year + 1 else now.Year
        this.RefreshNhlRatingsForSeasonCoreAsync(season, ct)

    member this.RefreshMlbRatingsAsync(?ct: CancellationToken) : Task<int> =
        let ct = defaultArg ct CancellationToken.None
        let month = DateTime.UtcNow.Month

        if month < 4 || month > 10 then
            logger.LogDebug("Skipping MLB PlayoffStatus refresh - offseason (month {Month})", month)
            Task.FromResult(0)
        else
            this.RefreshRatingsAsync("mlb", DateTime.UtcNow.Year, ct)

type PlayoffStatusNhlRefreshBackgroundService(serviceProvider: IServiceProvider, logger: ILogger<PlayoffStatusNhlRefreshBackgroundService>, timeProvider: TimeProvider) =
    inherit Microsoft.Extensions.Hosting.BackgroundService()

    override _.ExecuteAsync(stoppingToken: CancellationToken) = task {
        logger.LogInformation("NHL PlayoffStatus model refresh background service starting")
        while not stoppingToken.IsCancellationRequested do
            try
                use scope = serviceProvider.CreateScope()
                let service = scope.ServiceProvider.GetRequiredService<PlayoffStatusService>()
                let! _ = service.RefreshNhlRatingsAsync(stoppingToken)
                let now = timeProvider.GetUtcNow().UtcDateTime
                let nextRun = now.Add(PlayoffRatingPolicy.modelRefreshCooldown)
                logger.LogInformation("Next NHL PlayoffStatus model refresh no earlier than {NextRun:O}", nextRun)
                do! Task.Delay(PlayoffRatingPolicy.modelRefreshCooldown, timeProvider, stoppingToken)
            with
            | :? OperationCanceledException -> logger.LogDebug("NHL PlayoffStatus model refresh background service cancelled")
            | ex ->
                logger.LogError(ex, "Error during NHL PlayoffStatus model refresh background cycle")
                do! Task.Delay(TimeSpan.FromHours(1.0), timeProvider, stoppingToken)
    }
