namespace backend.Services

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Linq
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open AngleSharp.Html.Parser
open backend.Data
open backend.Models
open Microsoft.EntityFrameworkCore
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open Microsoft.VisualBasic.FileIO

module private MoneyPuckQualification =
    let source = PlayoffRatingPolicy.moneyPuckQualificationSource
    let pageUrl = "https://moneypuck.com/predictions.htm"
    let csvUrl = "https://moneypuck.com/moneypuck/simulations/simulations_recent.csv"
    let markerUrl = "https://moneypuck.com/moneypuck/simulations/update_date.txt"
    let teams = NhlQualificationRoster.teams |> Set.toArray
    exception InvalidSnapshot of string
    type Observation = { Team: string; Probability: float; SimulatedId: int64 }
    type Batch =
        { PublishedAt: DateTime option
          ClockValidated: bool
          FetchedAt: DateTime
          PageObservedAt: DateTime
          Label: string
          Rows: Observation array
          Games: NhlSimulatedGameSeasonEvidence array
          Reason: string }

    let publication (text: string) =
        let value = text.Trim()
        if not (Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$")) then None
        else
            match DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None) with
            | true, value -> Some value.UtcDateTime
            | _ -> None

    let canonical team =
        match team with
        | "LAK" -> "LA" | "NJD" -> "NJ" | "SJS" -> "SJ" | "TBL" -> "TB"
        | "UTA" | "UTAH" -> "UTAH" | _ -> team

    let parseRows (csv: string) (ct: CancellationToken) =
        try
            use reader = new StringReader(csv)
            use parser = new TextFieldParser(reader)
            parser.TextFieldType <- FieldType.Delimited
            parser.HasFieldsEnclosedInQuotes <- true
            parser.SetDelimiters(",")
            let headers = parser.ReadFields()
            if isNull headers || (headers |> Array.distinct |> Array.length) <> headers.Length then Error "invalid_schema"
            else
                let indexes = [| "teamCode"; "scenerio"; "madePlayoffs"; "firstSimulatedId" |] |> Array.map (fun h -> Array.IndexOf(headers, h))
                if indexes |> Array.exists (fun i -> i < 0) then Error "invalid_schema"
                else
                    let rows = ResizeArray<Observation>()
                    let seen = HashSet<string>(StringComparer.Ordinal)
                    let mutable problem = None
                    while not parser.EndOfData do
                        ct.ThrowIfCancellationRequested()
                        let row = parser.ReadFields()
                        if isNull row || row.Length <> headers.Length then problem <- Some "malformed_row"
                        elif row[indexes[1]] = "ALL" then
                            let team = canonical row[indexes[0]]
                            let probability = Double.TryParse(row[indexes[2]], NumberStyles.Float, CultureInfo.InvariantCulture)
                            let idText = row[indexes[3]]
                            let gameId = Int64.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture)
                            if not (Array.contains team teams) then problem <- Some "unknown_team"
                            elif not (seen.Add team) then problem <- Some "duplicate_team"
                            else
                                match probability, gameId with
                                | (true, p), (true, id) when Double.IsFinite p && p >= 0.0 && p <= 1.0 && Regex.IsMatch(idText, @"^[1-9]\d{9}$") ->
                                    rows.Add { Team = team; Probability = p; SimulatedId = id }
                                | (true, p), _ when not (Double.IsFinite p) || p < 0.0 || p > 1.0 -> problem <- Some "invalid_probability"
                                | (false, _), _ -> problem <- Some "invalid_probability"
                                | _ -> problem <- Some "invalid_simulated_id"
                    match problem with
                    | Some reason -> Error reason
                    | None when rows.Count <> teams.Length -> Error "incomplete_snapshot"
                    | None -> Ok (rows.ToArray())
        with :? MalformedLineException -> Error "malformed_row"

    let pageSeason (html: string) (ct: CancellationToken) = task {
        let! document = HtmlParser().ParseDocumentAsync(html, ct)
        use document = document
        let headings =
            document.QuerySelectorAll("h1,h2,h3,caption")
            |> Seq.filter (fun h -> isNull (h.Closest("nav,header,footer,aside,script,template,a,[hidden],[aria-hidden='true']")) && isNull (h.QuerySelector("a")))
            |> Seq.map (fun h -> Regex.Replace(h.TextContent, @"\s+", " ").Trim())
            |> Seq.filter (fun text -> Regex.IsMatch(text, @"\bNHL\b", RegexOptions.IgnoreCase)
                                       && (text.Contains("predictions", StringComparison.OrdinalIgnoreCase)
                                           || (text.Contains("playoff", StringComparison.OrdinalIgnoreCase) && text.Contains("probabilit", StringComparison.OrdinalIgnoreCase))))
            |> Seq.toArray
        if headings.Length = 0 then return Error "missing_season_heading"
        else
            let labels = headings |> Array.map (fun text -> Regex.Matches(text, @"(?<!\d)(\d{4})\s*[-–]\s*(\d{4}|\d{2})(?!\d)") |> Seq.map (fun m -> m.Groups[1].Value + "-" + m.Groups[2].Value) |> Seq.toArray)
            if labels |> Array.exists (fun values -> values.Length <> 1 || (values[0] <> "2026-27" && values[0] <> "2026-2027")) then return Error "wrong_season"
            else return Ok (labels[0][0])
    }

    let gameEvidence (id: int64) (body: string) (now: DateTime) =
        try
            use json = JsonDocument.Parse(body)
            let root = json.RootElement
            let mutable gameId = Unchecked.defaultof<JsonElement>
            let mutable season = Unchecked.defaultof<JsonElement>
            let mutable returnedId = 0L
            let mutable returnedSeason = 0
            if root.ValueKind <> JsonValueKind.Object
               || (root.EnumerateObject() |> Seq.filter (fun p -> p.Name = "id") |> Seq.length) <> 1
               || (root.EnumerateObject() |> Seq.filter (fun p -> p.Name = "season") |> Seq.length) <> 1
               || not (root.TryGetProperty("id", &gameId)) || not (root.TryGetProperty("season", &season))
               || gameId.ValueKind <> JsonValueKind.Number || season.ValueKind <> JsonValueKind.Number
               || not (gameId.TryGetInt64(&returnedId)) || not (season.TryGetInt32(&returnedSeason)) then Error "unverified_game_season"
            elif returnedId <> id || returnedSeason <> 20262027 then Error "wrong_game_season"
            else Ok (NhlSimulatedGameSeasonEvidence(SimulatedId = id, GameId = returnedId, Season = returnedSeason,
                                                   Reference = $"https://api-web.nhle.com/v1/gamecenter/{id}/landing", ObservedAt = Nullable now))
        with :? JsonException -> Error "unverified_game_season"

type internal MoneyPuckQualificationCollector(source: MoneyPuckHttpClient, logger: ILogger, serviceProvider: IServiceProvider,
                                             timeProvider: TimeProvider, optionsAccessor: IOptions<PlayoffRatingsOptions>) =
    let options = optionsAccessor.Value
    let now () = timeProvider.GetUtcNow().UtcDateTime

    member private this.FetchAsync(ct: CancellationToken) = task {
        let mutable requests = 0
        let read url limit =
            requests <- requests + 1
            if requests > 36 then raise (MoneyPuckQualification.InvalidSnapshot "request_budget_exceeded")
            source.ReadAsync(url, limit, ct)
        let! before = read MoneyPuckQualification.markerUrl 4096
        let! pageAt, label, rows, games, initialReason = task {
            try
                let! page = read MoneyPuckQualification.pageUrl (1024 * 1024)
                let pageAt = now ()
                let! csv = read MoneyPuckQualification.csvUrl (2 * 1024 * 1024)
                let! label = MoneyPuckQualification.pageSeason page ct
                let rows = MoneyPuckQualification.parseRows csv ct
                let games = ResizeArray<NhlSimulatedGameSeasonEvidence>()
                let mutable reason = "accepted"
                match label, rows with
                | Error e, _ | _, Error e -> reason <- e
                | Ok _, Ok rows ->
                    for id in rows |> Array.map (fun r -> r.SimulatedId) |> Array.distinct do
                        if reason = "accepted" then
                            let! body = read $"https://api-web.nhle.com/v1/gamecenter/{id}/landing" (512 * 1024)
                            match MoneyPuckQualification.gameEvidence id body (now ()) with
                            | Ok game -> games.Add game
                            | Error e -> reason <- e
                return pageAt, label, rows, games.ToArray(), reason
            with
            | MoneyPuckQualification.InvalidSnapshot reason
            | MoneyPuckInvalidSource reason ->
                return now (), Error reason, Error reason, [||], reason
            | :? DecoderFallbackException ->
                return now (), Error "invalid_encoding", Error "invalid_encoding", [||], "invalid_encoding"
        }
        let mutable reason = initialReason
        let! after = read MoneyPuckQualification.markerUrl 4096
        let published = MoneyPuckQualification.publication before
        let afterPublication = MoneyPuckQualification.publication after
        let fetched = now ()
        if published.IsSome && afterPublication.IsSome && published <> afterPublication then return None
        else
            if pageAt > fetched || games |> Seq.exists (fun game -> game.ObservedAt.Value > fetched) then reason <- "future_observation"
            elif published.IsNone || afterPublication.IsNone then reason <- "invalid_publication"
            elif published.Value > fetched then reason <- "future_publication"
            elif reason = "accepted" && fetched - published.Value >= PlayoffRatingPolicy.moneyPuckQualificationMaxAge then reason <- "expired"
            let batch: MoneyPuckQualification.Batch =
                { PublishedAt = published
                  ClockValidated = published.IsSome && published = afterPublication && published.Value <= fetched
                                   && pageAt <= fetched && games |> Array.forall (fun g -> g.ObservedAt.Value <= fetched)
                  FetchedAt = fetched; PageObservedAt = pageAt
                  Label = (match label with Ok l -> l | _ -> "")
                  Rows = (match rows with Ok r -> r | _ -> [||]); Games = games; Reason = reason }
            return Some batch
    }

    member private _.CommitAsync(season: int, batch: MoneyPuckQualification.Batch, ct: CancellationToken) = task {
        use scope = serviceProvider.CreateScope()
        let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
        use! tx = db.Database.BeginTransactionAsync(ct)
        let! existing = db.TeamRatings.Where(fun r -> r.Sport = "nhl" && r.Season = season && r.Source = MoneyPuckQualification.source).ToListAsync(ct)
        let batch = if batch.FetchedAt > now () then { batch with Reason = "future_observation" } else batch
        let highWater =
            existing |> Seq.choose (fun row ->
                let e = PlayoffModelEvidence.Parse(row.PlayoffEvidenceJson)
                let clockValid (at: Nullable<DateTime>) =
                    at.HasValue && at.Value.Kind = DateTimeKind.Utc && at.Value <> DateTime.MinValue
                    && e.FetchedAt.HasValue && e.FetchedAt.Value.Kind = DateTimeKind.Utc
                    && at.Value <= e.FetchedAt.Value && e.FetchedAt.Value <= now ()
                if isNull e || e.Provider <> MoneyPuckQualification.source || not e.ProviderVerified
                   || e.ValidationPolicy <> PlayoffRatingPolicy.moneyPuckQualificationValidationPolicy
                   || e.EvidenceKind <> "model_projection" || e.Sport <> "nhl" || e.Season <> season
                   || e.Team <> row.TeamAbbreviation || not e.TeamVerified || e.TotalTeamCount <> 32
                   || row.SourceSeason <> Nullable season || row.FetchedAt.Kind <> DateTimeKind.Utc || row.FetchedAt > now ()
                   || not e.FetchedAt.HasValue || e.FetchedAt.Value.Ticks / TimeSpan.TicksPerMicrosecond <> row.FetchedAt.Ticks / TimeSpan.TicksPerMicrosecond
                   || String.IsNullOrWhiteSpace e.RefreshId then None
                elif e.Version = PlayoffRatingPolicy.moneyPuckQualificationEvidenceVersion && clockValid e.PublicationBoundary then
                    Option.ofNullable e.PublicationBoundary
                elif e.Version = 3 && clockValid e.PublishedAt
                     && e.ReasonCode <> "invalid_publication" && e.ReasonCode <> "future_observation" then
                    Option.ofNullable e.PublishedAt
                else None) |> Seq.sortDescending |> Seq.tryHead
        let rejectedPublications =
            existing |> Seq.collect (fun row ->
                let e = PlayoffModelEvidence.Parse(row.PlayoffEvidenceJson)
                if isNull e || (e.Version <> 3 && e.Version <> PlayoffRatingPolicy.moneyPuckQualificationEvidenceVersion)
                   || e.Provider <> MoneyPuckQualification.source || not e.ProviderVerified
                   || e.ValidationPolicy <> PlayoffRatingPolicy.moneyPuckQualificationValidationPolicy
                   || e.EvidenceKind <> "model_projection" || e.Sport <> "nhl" || e.Season <> season
                   || e.Team <> row.TeamAbbreviation || not e.TeamVerified || e.TotalTeamCount <> 32
                   || row.SourceSeason <> Nullable season || String.IsNullOrWhiteSpace e.RefreshId then Seq.empty
                else
                    Seq.append
                        (if e.Version = PlayoffRatingPolicy.moneyPuckQualificationEvidenceVersion && not (isNull e.MoneyPuckRejectedPublications)
                         then e.MoneyPuckRejectedPublications :> seq<DateTime> else Seq.empty)
                        (if e.State = "unavailable" && e.ReasonCode <> "expired" then Option.ofNullable e.PublishedAt |> Option.toList else []))
            |> Seq.filter (fun at -> at.Kind = DateTimeKind.Utc && at <> DateTime.MinValue)
            |> Set.ofSeq
        let knownRejected = batch.PublishedAt |> Option.exists rejectedPublications.Contains
        let clockValidated = batch.ClockValidated && batch.FetchedAt <= now ()
        let regressed = clockValidated && (match batch.PublishedAt, highWater with Some value, Some stored -> value < stored | _ -> false)
        let boundary =
            Seq.append (Option.toList highWater) (if clockValidated && not knownRejected then Option.toList batch.PublishedAt else [])
            |> Seq.sortDescending |> Seq.tryHead
        let batch = if regressed then { batch with Reason = "publication_regression" } else batch
        let blockedPublication =
            (batch.Reason = "accepted" || batch.Reason = "expired") && batch.PublishedAt.IsSome
            && (knownRejected || (highWater.IsSome && highWater = batch.PublishedAt
                && existing |> Seq.exists (fun row ->
                    let e = PlayoffModelEvidence.Parse(row.PlayoffEvidenceJson)
                    not (isNull e) && e.State = "unavailable" && e.ReasonCode <> "expired")))
        let batch = if blockedPublication then { batch with Reason = "rejected_publication" } else batch
        let unchanged =
            (batch.Reason = "accepted" || batch.Reason = "expired") && existing.Count = 32 && existing |> Seq.forall (fun row ->
                let e = PlayoffModelEvidence.Parse(row.PlayoffEvidenceJson)
                not (isNull e) && ((e.State = "estimate" && e.ReasonCode = "accepted")
                    || (e.State = "unavailable" && e.ReasonCode = "expired" && e.MoneyPuckExpiredProbability.HasValue))
                && Option.ofNullable e.PublishedAt = batch.PublishedAt)
        let changedPublication =
            unchanged && existing |> Seq.exists (fun row ->
                let observation = batch.Rows |> Array.tryFind (fun obs -> obs.Team = row.TeamAbbreviation)
                let e = PlayoffModelEvidence.Parse(row.PlayoffEvidenceJson)
                match observation with
                | None -> true
                | Some obs -> (if e.State = "estimate" then row.PlayoffProbability else e.MoneyPuckExpiredProbability) <> Nullable obs.Probability
                              || isNull e.MoneyPuckSeasonProof || e.MoneyPuckSeasonProof.FirstSimulatedId <> obs.SimulatedId)
        let batch = if changedPublication then { batch with Reason = "inconsistent_publication" } else batch
        let rejectedPublications =
            Set.union rejectedPublications (if batch.Reason <> "accepted" && batch.Reason <> "expired" then Set.ofList (Option.toList batch.PublishedAt) else Set.empty)
            |> Set.filter (fun at -> boundary |> Option.forall (fun trusted -> at > trusted))
            |> Set.toArray
        let refreshId = Guid.NewGuid().ToString("N")
        for team in (if batch.Reason = "accepted" && unchanged && not changedPublication then [||] else MoneyPuckQualification.teams) do
            let row =
                match existing |> Seq.tryFind (fun r -> r.TeamAbbreviation = team) with
                | Some r -> r
                | None ->
                    let r = TeamRating(Sport = "nhl", Season = season, Source = MoneyPuckQualification.source, TeamAbbreviation = team)
                    db.TeamRatings.Add(r) |> ignore
                    r
            row.TeamName <- team
            row.SourceSeason <- Nullable season
            row.PlayoffProbability <- if batch.Reason = "accepted" then Nullable((batch.Rows |> Array.find (fun r -> r.Team = team)).Probability) else Nullable()
            row.PlayoffProbabilityIsFinal <- false
            row.FetchedAt <- batch.FetchedAt
            let evidence = PlayoffModelEvidence(Version = PlayoffRatingPolicy.moneyPuckQualificationEvidenceVersion,
                                               ValidationPolicy = PlayoffRatingPolicy.moneyPuckQualificationValidationPolicy, Provider = MoneyPuckQualification.source,
                                               ProviderVerified = true, Sport = "nhl", Team = team, Season = season,
                                               SeasonVerified = (batch.Reason = "accepted"), TeamVerified = true,
                                               State = (if batch.Reason = "accepted" then "estimate" else "unavailable"), ReasonCode = batch.Reason,
                                               PublishedAt = Option.toNullable batch.PublishedAt, PublicationBoundary = Option.toNullable boundary,
                                               MoneyPuckRejectedPublications = rejectedPublications, FetchedAt = Nullable batch.FetchedAt,
                                               RefreshId = refreshId, AcceptedTeamCount = (if batch.Reason = "accepted" then 32 else 0), TotalTeamCount = 32)
            if batch.Reason = "expired" then
                evidence.MoneyPuckExpiredProbability <- Nullable((batch.Rows |> Array.find (fun r -> r.Team = team)).Probability)
            if batch.Reason = "accepted" || batch.Reason = "expired" then
                evidence.MoneyPuckSeasonProof <- MoneyPuckSeasonProof(PageSeasonLabel = batch.Label, PageSeasonStartYear = 2026, PageSeasonEndYear = 2027,
                                                                    PageReference = MoneyPuckQualification.pageUrl, PageObservedAt = Nullable batch.PageObservedAt,
                                                                    FirstSimulatedId = (batch.Rows |> Array.find (fun r -> r.Team = team)).SimulatedId, VerifiedGames = batch.Games)
            row.PlayoffEvidenceJson <- JsonSerializer.Serialize(evidence)
        let! _ = db.SaveChangesAsync(ct)
        do! tx.CommitAsync(ct)
        return if batch.Reason = "accepted" && not blockedPublication then 32 else 0
    }

    member this.RefreshAsync(season: int, ct: CancellationToken) : Task<int> = task {
        if not (options.MoneyPuckRefreshEnabledFor("nhl", season)) then return 0
        else
            let! batch = task {
                try return! this.FetchAsync(ct)
                with
                | MoneyPuckQualification.InvalidSnapshot reason
                | MoneyPuckInvalidSource reason ->
                    return Some { PublishedAt = None; ClockValidated = false; FetchedAt = now (); PageObservedAt = now (); Label = ""; Rows = [||]; Games = [||]; Reason = reason }
                | :? DecoderFallbackException ->
                    return Some { PublishedAt = None; ClockValidated = false; FetchedAt = now (); PageObservedAt = now (); Label = ""; Rows = [||]; Games = [||]; Reason = "invalid_encoding" }
            }
            match batch with
            | Some batch -> return! this.CommitAsync(season, batch, ct)
            | None -> return 0
    }
