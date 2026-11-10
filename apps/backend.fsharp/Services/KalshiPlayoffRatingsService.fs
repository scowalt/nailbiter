namespace backend.Services.Kalshi

open System
open System.Collections.Generic
open System.Linq
open System.Net.Http
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open backend.Data
open backend.Models
open backend.Services
open Microsoft.EntityFrameworkCore
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging

module private PlayoffMarkets =
    type League =
        { Series: string
          Teams: string list
          Aliases: Map<string, string> }

    let league sport =
        let create series teams aliases =
            Some { Series = series; Teams = teams; Aliases = Map.ofList aliases }
        match sport with
        | "cfb" -> create "KXNCAAFPLAYOFF" [] [ "NW", "NU" ]
        | "nfl" ->
            create "KXNFLPLAYOFF"
                [ "ARI"; "ATL"; "BAL"; "BUF"; "CAR"; "CHI"; "CIN"; "CLE"; "DAL"; "DEN"; "DET"; "GB"
                  "HOU"; "IND"; "JAX"; "KC"; "LAC"; "LAR"; "LV"; "MIA"; "MIN"; "NE"; "NO"; "NYG"
                  "NYJ"; "PHI"; "PIT"; "SEA"; "SF"; "TB"; "TEN"; "WSH" ]
                [ "JAC", "JAX"; "WAS", "WSH"; "LA", "LAR" ]
        | "nba" ->
            create "KXNBAPLAYOFF"
                [ "ATL"; "BOS"; "BKN"; "CHA"; "CHI"; "CLE"; "DAL"; "DEN"; "DET"; "GS"; "HOU"; "IND"
                  "LAC"; "LAL"; "MEM"; "MIA"; "MIL"; "MIN"; "NO"; "NY"; "OKC"; "ORL"; "PHI"; "PHX"
                  "POR"; "SAC"; "SA"; "TOR"; "UTAH"; "WSH" ]
                [ "GSW", "GS"; "NOP", "NO"; "NYK", "NY"; "SAS", "SA"; "UTA", "UTAH"; "WAS", "WSH" ]
        | "nhl" ->
            create "KXNHLPLAYOFF"
                [ "ANA"; "BOS"; "BUF"; "CGY"; "CAR"; "CHI"; "COL"; "CBJ"; "DAL"; "DET"; "EDM"; "FLA"
                  "LA"; "MIN"; "MTL"; "NSH"; "NJ"; "NYI"; "NYR"; "OTT"; "PHI"; "PIT"; "SJ"; "SEA"
                  "STL"; "TB"; "TOR"; "UTAH"; "VAN"; "VGK"; "WSH"; "WPG" ]
                [ "UTA", "UTAH" ]
        | "mlb" ->
            create "KXMLBPLAYOFFS"
                [ "ARI"; "ATH"; "ATL"; "BAL"; "BOS"; "CHC"; "CHW"; "CIN"; "CLE"; "COL"; "DET"; "HOU"
                  "KC"; "LAA"; "LAD"; "MIA"; "MIL"; "MIN"; "NYM"; "NYY"; "PHI"; "PIT"; "SD"; "SF"
                  "SEA"; "STL"; "TB"; "TEX"; "TOR"; "WSH" ]
                [ "AZ", "ARI"; "CWS", "CHW" ]
        | _ -> None

    let eventTicker sport season league =
        let year = if sport = "nfl" then season + 1 else season
        $"{league.Series}-{year % 100:D2}"

    let seasonMatches sport season subtitle =
        if String.IsNullOrWhiteSpace(subtitle) then false
        elif sport = "mlb" then subtitle.Trim() = string season
        else
            let first = if sport = "nfl" || sport = "cfb" then season else season - 1
            let last = first + 1
            let ranges = Regex.Matches(subtitle, @"(?<!\d)\d{4}\s*[-/]\s*(?:\d{4}|\d{2})(?!\d)")
            ranges.Count = 1 && Regex.IsMatch(ranges[0].Value, $"^{first}\\s*[-/]\\s*(?:{last}|{last % 100:D2})$")

    let decimalValue = QualificationQuotePolicy.decimalValue

    let optionalValueMatches expected text =
        isNull text || decimalValue text = Some expected

    let assessment (now: DateTime) (market: KalshiMarket) =
        if (not (isNull market.MarketType) && market.MarketType <> "binary")
           || not (optionalValueMatches 1M market.NotionalValueDollars) then Error "unsupported_contract"
        else
            match market.Status, market.Result with
            | ("finalized" | "settled"), ("yes" | "no") ->
                let value = if market.Result = "yes" then 1M else 0M
                if optionalValueMatches value market.SettlementValueDollars then Ok(float value, true)
                else Error "exceptional_settlement"
            | ("finalized" | "settled"), _ -> Error "exceptional_settlement"
            | "active", result when String.IsNullOrEmpty(result) && isNull market.SettlementValueDollars
                                    && market.CloseTime.HasValue && market.CloseTime.Value > now ->
                QualificationQuotePolicy.evaluate (decimalValue market.YesBidDollars) (decimalValue market.YesAskDollars)
                    (decimalValue market.YesBidSizeFp) (decimalValue market.YesAskSizeFp)
                |> Result.map (fun midpoint -> float midpoint, false)
            | _ -> Error "awaiting_resolution"

    let probability now market = assessment now market |> Result.toOption

    let belongsTo eventTicker (market: KalshiMarket) =
        market.EventTicker = eventTicker && not (isNull market.Ticker)
        && market.Ticker.StartsWith(eventTicker + "-", StringComparison.Ordinal)
        && not (market.Ticker.Substring(eventTicker.Length + 1).Contains('-'))

    let groupMarkets league (markets: seq<KalshiMarket>) =
        markets
        |> Seq.choose (fun market ->
            if isNull (box market) || String.IsNullOrEmpty(market.Ticker) then None
            else
                let suffix = market.Ticker.Substring(market.Ticker.LastIndexOf('-') + 1)
                let team = Map.tryFind suffix league.Aliases |> Option.defaultValue suffix
                if List.contains team league.Teams then Some(team, market) else None)
        |> Seq.groupBy fst
        |> Seq.map (fun (team, entries) -> team, entries |> Seq.map snd |> Seq.toList)
        |> Map.ofSeq

    let completeTeamCoverage eventTicker (markets: ResizeArray<KalshiMarket>) (grouped: Map<string, KalshiMarket list>) =
        (markets |> Seq.forall (fun market -> not (isNull (box market)) && belongsTo eventTicker market))
        && (grouped |> Map.toSeq |> Seq.sumBy (fun (_, entries) -> entries.Length)) = markets.Count

    let quote eventTicker now = function
        | [market] when belongsTo eventTicker market -> probability now market
        | _ -> None

    let evidence sport eventTicker season team now entries =
        let e = PlayoffQuoteEvidence(Version = 1, ValidationPolicy = "kalshi-qualification-v1", Provider = "kalshi",
                                    Sport = sport, Team = team, Season = season, EventId = eventTicker,
                                    ObservedAt = Nullable now)
        match entries with
        | Some [market] when belongsTo eventTicker market ->
            e.MarketId <- market.Ticker
            e.ContractVerified <- true
            if market.Status = "finalized" || market.Status = "settled" then
                e.YesPayout <- decimalValue market.SettlementValueDollars |> Option.toNullable
                e.ResolutionReference <- market.Ticker + ":" + market.Status + ":" + market.Result
            e.Bid <- decimalValue market.YesBidDollars |> Option.toNullable
            e.Ask <- decimalValue market.YesAskDollars |> Option.toNullable
            e.BidSize <- decimalValue market.YesBidSizeFp |> Option.toNullable
            e.AskSize <- decimalValue market.YesAskSizeFp |> Option.toNullable
            e.BidExplicitlyEmpty <- e.Bid = Nullable 0M && e.BidSize = Nullable 0M
            e.AskExplicitlyEmpty <- e.Ask = Nullable 1M && e.AskSize = Nullable 0M
            match assessment now market with
            | Ok(value, isFinal) ->
                e.State <- if isFinal then "final" else "estimate"
                e.ReasonCode <- "accepted"
                e.EvidenceAt <- Nullable now
                if isFinal then
                    e.YesPayout <- Nullable(decimal value)
                    e.NoPayout <- Nullable(1M - decimal value)
            | Error reason -> e.ReasonCode <- reason
        | Some [_] -> e.ReasonCode <- "identity_mismatch"
        | Some _ -> e.ReasonCode <- "ambiguous_market"
        | None -> e.ReasonCode <- "missing_market"
        e

    let knownFinal season now (rows: TeamRating list) =
        rows
        |> List.sortByDescending (fun row -> row.FetchedAt, row.Id)
        |> List.tryHead
        |> Option.filter (fun row -> row.Source = "kalshi" && row.SourceSeason = Nullable season && row.PlayoffProbabilityIsFinal
                                     && row.FetchedAt <= now && row.PlayoffProbability.HasValue
                                     && (row.PlayoffProbability.Value = 0.0 || row.PlayoffProbability.Value = 1.0))
        |> Option.map (fun row -> row.PlayoffProbability.Value, row.FetchedAt)

type KalshiPlayoffRatingsService
    (httpClient: HttpClient,
     logger: ILogger<KalshiPlayoffRatingsService>,
     serviceProvider: IServiceProvider,
     timeProvider: TimeProvider) =

    let baseUrl = "https://api.elections.kalshi.com/trade-api/v2"
    let mutable accessRefused = false

    let failureLevel (ex: exn) =
        match ex with
        | :? HttpRequestException when not (KalshiFailure.Is ex) -> LogLevel.Warning
        | :? JsonException -> LogLevel.Warning
        | _ -> LogLevel.Error

    let archivedMarkets (eventTicker: string) (ct: CancellationToken) = task {
        let markets = ResizeArray<KalshiMarket>()
        let seen = HashSet<string>(StringComparer.Ordinal)
        let mutable cursor = ""
        let mutable complete = false
        let mutable pages = 0
        while not complete do
            ct.ThrowIfCancellationRequested()
            if pages >= 3 then invalidOp "Kalshi playoff archive exceeded the three-page bound"
            let suffix = if cursor = "" then "" else "&cursor=" + Uri.EscapeDataString(cursor)
            let! json = httpClient.GetStringAsync(
                $"{baseUrl}/historical/markets?event_ticker={Uri.EscapeDataString(eventTicker)}&limit=1000{suffix}", ct)
            let response = JsonSerializer.Deserialize<KalshiMarketsResponse>(json)
            if isNull (box response) || isNull response.Markets || isNull response.Cursor then
                invalidOp "Kalshi playoff archive omitted markets or pagination metadata"
            if response.Markets |> Seq.exists (fun market -> isNull (box market) || not (PlayoffMarkets.belongsTo eventTicker market)) then
                invalidOp "Kalshi playoff archive returned mismatched market metadata"
            markets.AddRange(response.Markets)
            pages <- pages + 1
            cursor <- response.Cursor
            complete <- cursor = ""
            if not complete && not (seen.Add(cursor)) then invalidOp "Kalshi playoff archive repeated a cursor"
        return markets
    }

    member _.RefreshAsync(sport: string, season: int, ct: CancellationToken) : Task<int> = task {
        ct.ThrowIfCancellationRequested()
        let! collegeTeams = task {
            if sport <> "cfb" then return []
            else
                use scope = serviceProvider.CreateScope()
                let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
                let! teams = db.CollegeTeams.Where(fun t -> t.Season = season).ToListAsync(ct)
                return teams |> Seq.map (fun t -> t.Abbreviation) |> Seq.distinct |> Seq.toList
        }
        let league =
            PlayoffMarkets.league sport
            |> Option.filter (fun _ -> sport <> "cfb" || not collegeTeams.IsEmpty)
            |> Option.map (fun league ->
                if sport <> "cfb" then league
                else { league with Teams = collegeTeams
                                   Aliases = collegeTeams |> List.fold (fun aliases team ->
                                       Map.add (CollegeFootballPolicy.KalshiCode(team)) team aliases) league.Aliases })
        match league with
        | None ->
            logger.LogWarning("Unsupported Kalshi playoff sport {Sport}", sport)
            return 0
        | Some league when season < 1 || season > 9998 ->
            logger.LogWarning("Invalid Kalshi playoff season {Season} for {Sport}", season, sport)
            return 0
        | Some _ when accessRefused -> return 0
        | Some league ->
            let eventTicker = PlayoffMarkets.eventTicker sport season league
            try
                let! json = httpClient.GetStringAsync($"{baseUrl}/events/{eventTicker}?with_nested_markets=true", ct)
                let response = JsonSerializer.Deserialize<KalshiEventResponse>(json)
                let event = if isNull (box response) then Unchecked.defaultof<KalshiEvent> else response.Event
                let markets =
                    if not (isNull (box event)) && not (isNull event.Markets) then event.Markets
                    elif not (isNull (box response)) then response.Markets
                    else null
                if isNull (box event) || event.EventTicker <> eventTicker || event.SeriesTicker <> league.Series
                   || not (PlayoffMarkets.seasonMatches sport season event.SubTitle) || isNull markets then
                    logger.LogWarning("Rejected Kalshi {Sport} playoff response for season {Season}: missing or mismatched event metadata", sport, season)
                    return 0
                else
                    let now = timeProvider.GetUtcNow().UtcDateTime
                    let live = PlayoffMarkets.groupMarkets league markets
                    use scope = serviceProvider.CreateScope()
                    let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
                    let! existing = db.TeamRatings.Where(fun r -> r.Sport = sport && r.Season = season
                                                                   && (r.Source = "kalshi" || (sport = "cfb" && r.Source = PlayoffRatingPolicy.collegeNoMarketSource))).ToListAsync(ct)
                    let rowsByTeam = existing |> Seq.groupBy (fun r -> r.TeamAbbreviation) |> Seq.map (fun (team, rows) -> team, Seq.toList rows) |> Map.ofSeq
                    let rowsFor team = Map.tryFind team rowsByTeam |> Option.defaultValue []
                    let needsArchive =
                        league.Teams |> List.exists (fun team -> not (Map.containsKey team live)
                                                               && (PlayoffMarkets.knownFinal season now (rowsFor team)).IsNone)
                    let! archive = task {
                        if not needsArchive then return Some(Map.empty, true)
                        else
                            try
                                let! markets = archivedMarkets eventTicker ct
                                let grouped = PlayoffMarkets.groupMarkets league markets
                                return Some(grouped, PlayoffMarkets.completeTeamCoverage eventTicker markets grouped)
                            with
                            | :? OperationCanceledException when ct.IsCancellationRequested -> return raise (OperationCanceledException(ct))
                            | ex ->
                                if ex :? KalshiAccessRefusedException then accessRefused <- true
                                logger.Log(failureLevel ex, ex, "Failed to recover archived {Sport} playoff finals for season {Season}; missing nonfinal probabilities are cleared without advancing their timestamps", sport, season)
                                return None
                    }
                    let canAssumeNoMarket =
                        sport = "cfb" && season = PlayoffRatingPolicy.seasonAt sport now
                        && PlayoffMarkets.completeTeamCoverage eventTicker markets live
                        && (archive |> Option.exists snd)
                    let mutable count = 0
                    let mutable assumedCount = 0
                    let refreshId = Guid.NewGuid().ToString("N")
                    for team in league.Teams do
                        let rows = rowsFor team
                        let previousEvidence =
                            if sport <> "nhl" then None
                            else rows |> List.sortByDescending (fun r -> r.FetchedAt, r.Id) |> List.tryHead
                                      |> Option.map (fun r -> PlayoffQuoteEvidence.Parse(r.PlayoffEvidenceJson))
                        let previousAssessment =
                            if sport <> "nhl" then None
                            else
                                let request : PlayoffRatingPolicy.SelectionRequest =
                                    { Sport = sport; Season = season; Team = team; Now = now
                                      Options = PlayoffRatingsOptions(NhlModelMode = "fallback", ApprovedSeasons = [|season|]) }
                                (PlayoffRatingPolicy.select request rows).Providers |> List.tryHead
                        let observed =
                            match Map.tryFind team live with
                            | Some markets -> Some(PlayoffMarkets.quote eventTicker now markets)
                            | None ->
                                archive |> Option.bind (fun (markets, _) -> Map.tryFind team markets)
                                |> Option.map (fun markets -> PlayoffMarkets.quote eventTicker now markets |> Option.filter snd)
                        match PlayoffMarkets.knownFinal season now rows, observed with
                        | Some(value, fetchedAt), None ->
                            for row in rows do
                                row.Source <- "kalshi"
                                row.SourceSeason <- Nullable season
                                row.PlayoffProbability <- Nullable value
                                row.PlayoffProbabilityIsFinal <- true
                                row.FetchedAt <- fetchedAt
                            count <- count + 1
                        | _ ->
                            let update = observed |> Option.orElseWith (fun () -> archive |> Option.map (fun _ -> None))
                            match update with
                            | None ->
                                for row in rows do
                                    row.PlayoffProbability <- Nullable()
                                    row.PlayoffProbabilityIsFinal <- false
                            | Some value ->
                                if value.IsSome then count <- count + 1
                                let assumed = canAssumeNoMarket && observed.IsNone
                                let value = if assumed then Some(PlayoffRatingPolicy.collegeNoMarketProbability, false) else value
                                if assumed then assumedCount <- assumedCount + 1
                                let rows =
                                    if rows.IsEmpty then
                                        let row = TeamRating(Sport = sport, TeamAbbreviation = team, Source = "kalshi", Season = season)
                                        db.TeamRatings.Add(row) |> ignore
                                        [row]
                                    else rows
                                for row in rows do
                                    row.Source <- if assumed then PlayoffRatingPolicy.collegeNoMarketSource else "kalshi"
                                    row.SourceSeason <- Nullable season
                                    row.PlayoffProbability <- value |> Option.map fst |> Option.toNullable
                                    row.PlayoffProbabilityIsFinal <- value |> Option.exists snd
                                    row.FetchedAt <- now
                        if sport <> "nhl" then
                            let evidence = PlayoffMarkets.evidence sport eventTicker season team now (Map.tryFind team live)
                            evidence.RefreshId <- refreshId
                            let json =
                                if (evidence.State = "estimate" && evidence.BidExplicitlyEmpty)
                                   || (evidence.State = "unavailable" && evidence.ReasonCode = "insufficient_depth") then JsonSerializer.Serialize(evidence)
                                else null
                            for row in db.TeamRatings.Local |> Seq.filter (fun r -> r.Sport = sport && r.Season = season && r.TeamAbbreviation = team && r.Source = "kalshi") do
                                row.PlayoffEvidenceJson <- json
                        if sport = "nhl" then
                            let entries =
                                Map.tryFind team live
                                |> Option.orElseWith (fun () -> archive |> Option.bind (fun (markets, _) -> Map.tryFind team markets))
                            let evidence = PlayoffMarkets.evidence sport eventTicker season team now entries
                            evidence.RefreshId <- refreshId
                            if not (Map.containsKey team live) && evidence.State = "estimate" then
                                evidence.State <- "unavailable"
                                evidence.ReasonCode <- "awaiting_resolution"
                                evidence.EvidenceAt <- Nullable()
                            let priorBlocked = previousAssessment |> Option.exists (fun p -> p.ResolutionBlocked)
                            let priorFinal = previousAssessment |> Option.exists (fun p -> p.IsFinal)
                            let reopened =
                                match entries with
                                | Some [market] -> evidence.ContractVerified && market.Status = "active" && priorFinal
                                | _ -> false
                            let changedContract = evidence.ReasonCode = "unsupported_contract"
                                                  && (previousAssessment |> Option.exists (fun p -> p.Probability.IsSome))
                            if evidence.ContractVerified && (evidence.ReasonCode = "exceptional_settlement" || reopened || changedContract) then
                                evidence.State <- "resolution_blocked"
                                evidence.ResolutionBlocked <- true
                                evidence.ReasonCode <- if reopened then "final_retracted" elif changedContract then "rules_changed" else "exceptional_settlement"
                                evidence.EvidenceAt <- Nullable()
                            elif evidence.State = "final" && priorFinal then
                                let previous = previousAssessment.Value
                                if previous.Probability = (evidence.YesPayout |> Option.ofNullable |> Option.map float) then
                                    evidence.EvidenceAt <- previous.EvidenceAt |> Option.toNullable
                            let evidence =
                                if priorBlocked && evidence.State <> "final" then previousEvidence.Value
                                else evidence
                            for row in db.TeamRatings.Local |> Seq.filter (fun r -> r.Sport = sport && r.Season = season && r.TeamAbbreviation = team && r.Source = "kalshi") do
                                if entries.IsSome || not row.PlayoffProbabilityIsFinal then
                                    row.PlayoffEvidenceJson <- JsonSerializer.Serialize(evidence)
                    let! _ = db.SaveChangesAsync(ct)
                    logger.LogInformation("Refreshed Kalshi {Sport} playoff probabilities for {Count}/{Teams} teams (season {Season}); {AssumedCount} separately sourced no-market assumptions", sport, count, league.Teams.Length, season, assumedCount)
                    return count
            with
            | :? OperationCanceledException when ct.IsCancellationRequested -> return raise (OperationCanceledException(ct))
            | ex ->
                if ex :? KalshiAccessRefusedException then accessRefused <- true
                logger.Log(failureLevel ex, ex, "Failed to refresh Kalshi {Sport} playoff probabilities for season {Season}", sport, season)
                return 0
    }

    member this.RefreshAsync(season: int, ct: CancellationToken) =
        this.RefreshAsync("nfl", season, ct)

    member this.RefreshCurrentAsync(ct: CancellationToken) : Task<int> = task {
        let now = timeProvider.GetUtcNow().UtcDateTime
        let mutable count = 0
        for sport in PlayoffRatingPolicy.sports do
            for season in PlayoffRatingPolicy.refreshSeasons sport now |> List.distinct do
                let! refreshed = this.RefreshAsync(sport, season, ct)
                count <- count + refreshed
        return count
    }
