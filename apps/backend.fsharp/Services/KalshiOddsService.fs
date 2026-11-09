namespace backend.Services.Kalshi

open System
open System.Collections.Generic
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open backend.Services

module private KalshiOddsServiceMappings =

    let sportSeriesTickers =
        let d = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        d.["nba"] <- "KXNBAGAME"
        d.["nfl"] <- "KXNFLGAME"
        d.["nhl"] <- "KXNHLGAME"
        d.["mlb"] <- "KXMLBGAME"
        d

    let espnToKalshiTeamMap =
        let d = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        d.["nba:WSH"] <- "WAS"
        d.["nfl:WSH"] <- "WAS"
        d.["nfl:NO"] <- "NO"
        d.["nfl:LAR"] <- "LAR"
        d.["world_cup:HAI"] <- "HTI"
        d.["world_cup:ALG"] <- "DZA"
        d.["world_cup:KSA"] <- "KSA"
        d.["world_cup:IRN"] <- "IRI"
        d.["PHX"] <- "PHX"
        d.["NO"] <- "NOP"
        d.["NY"] <- "NYK"
        d.["SA"] <- "SAS"
        d.["GS"] <- "GSW"
        d.["UTAH"] <- "UTA"
        d.["JAX"] <- "JAC"
        d.["LAR"] <- "LA"
        d.["mlb:ARI"] <- "AZ"
        d.["CHW"] <- "CWS"
        d

    type ParsedGameMarket =
        { Market: KalshiMarket
          EventTicker: string
          GameDate: string
          StartTime: string
          IsDoubleheader: bool
          AwayTeamAbbr: string
          HomeTeamAbbr: string
          OutcomeTeamAbbr: string }

    type GameMarketMatch =
        | NotListed
        | Unresolved
        | Matched of home: KalshiMarket * away: KalshiMarket

    let tickerRegex =
        Regex(
            @"^(?<event>KX(?:NBA|NFL|NHL|MLB)GAME-(?<date>\d{2}[A-Z]{3}\d{2})(?<time>\d{4})?(?<matchup>[A-Z]{4,6})(?<game>G[12])?)-(?<outcome>[A-Z]{2,3})$",
            RegexOptions.Compiled
        )

    let worldCupSeriesTicker = "KXWCGAME"
    let worldCupAdvanceSeriesTicker = "KXWCADVANCE"

    let worldCupTickerRegex =
        Regex(
            @"^KXWC(?:GAME|ADVANCE)-(?<date>\d{2}[A-Z]{3}\d{2})(?<matchup>[A-Z]{6})-(?<outcome>[A-Z]{2,3}|TIE)$",
            RegexOptions.Compiled
        )

    let cacheDuration = TimeSpan.FromSeconds(30.0)

type KalshiOddsService [<Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor>]
    (httpClient: System.Net.Http.HttpClient, logger: ILogger<KalshiOddsService>, timeProvider: TimeProvider) =

    let marketsCache = Dictionary<string, struct (ResizeArray<KalshiMarket> * DateTime)>()
    let mutable accessRefused = false

    do
        httpClient.DefaultRequestHeaders.Clear()
        httpClient.DefaultRequestHeaders.Add("Accept", "application/json")

    static let baseUrl = "https://api.elections.kalshi.com/trade-api/v2"

    new(httpClient, logger) = KalshiOddsService(httpClient, logger, TimeProvider.System)

    static member SupportsSport(sport: string) =
        KalshiOddsServiceMappings.sportSeriesTickers.ContainsKey(sport)

    member private this.ParseDollars(dollars: string) =
        if String.IsNullOrEmpty(dollars) then
            None
        else
            match
                Double.TryParse(
                    dollars,
                    Globalization.NumberStyles.Float,
                    Globalization.CultureInfo.InvariantCulture
                )
            with
            | true, v -> Some v
            | _ -> None

    member private this.GetMidPriceDollars(bid: string, ask: string) =
        let b = this.ParseDollars(bid)
        let a = this.ParseDollars(ask)

        match b, a with
        | Some bv, Some av -> Some((bv + av) / 2.0)
        | Some _, None -> b
        | None, Some _ -> a
        | None, None -> None

    member private _.GetMidPrice(bid: Nullable<int>, ask: Nullable<int>) =
        if bid.HasValue && ask.HasValue then
            Some(float (bid.Value + ask.Value) / 200.0)
        elif bid.HasValue then
            Some(float bid.Value / 100.0)
        elif ask.HasValue then
            Some(float ask.Value / 100.0)
        else
            None

    member private _.MapEspnToKalshi(sport: string, espnAbbr: string) =
        match KalshiOddsServiceMappings.espnToKalshiTeamMap.TryGetValue($"{sport}:{espnAbbr}") with
        | true, kalshiAbbr -> kalshiAbbr
        | _ ->
            match KalshiOddsServiceMappings.espnToKalshiTeamMap.TryGetValue(espnAbbr) with
            | true, kalshiAbbr -> kalshiAbbr
            | _ -> espnAbbr

    member private _.ProbabilityToMoneyline(probability: float) =
        if probability <= 0.0 then
            10000
        elif probability >= 1.0 then
            -10000
        elif probability >= 0.5 then
            int (Math.Round(-100.0 * probability / (1.0 - probability)))
        else
            int (Math.Round(100.0 * (1.0 - probability) / probability))

    member private this.ParseMarketTicker(market: KalshiMarket) : KalshiOddsServiceMappings.ParsedGameMarket option =
        let m = KalshiOddsServiceMappings.tickerRegex.Match(market.Ticker)

        if not m.Success then
            logger.LogDebug("Could not parse Kalshi ticker: {Ticker}", market.Ticker)
            None
        else
            let matchup = m.Groups.["matchup"].Value
            let outcome = m.Groups.["outcome"].Value

            let parsed =
                if matchup.StartsWith(outcome, StringComparison.OrdinalIgnoreCase) then
                    let awayTeam = outcome
                    let homeTeam = matchup.Substring(outcome.Length)
                    Some(awayTeam, homeTeam)
                elif matchup.EndsWith(outcome, StringComparison.OrdinalIgnoreCase) then
                    let homeTeam = outcome
                    let awayTeam = matchup.Substring(0, matchup.Length - outcome.Length)
                    Some(awayTeam, homeTeam)
                else
                    logger.LogDebug(
                        "Could not split matchup {Matchup} using outcome {Outcome}",
                        matchup,
                        outcome
                    )

                    None

            parsed
            |> Option.map (fun (awayTeam, homeTeam) ->
                { Market = market
                  EventTicker = m.Groups.["event"].Value
                  GameDate = m.Groups.["date"].Value
                  StartTime = m.Groups.["time"].Value
                  IsDoubleheader = m.Groups.["game"].Success
                  AwayTeamAbbr = awayTeam
                  HomeTeamAbbr = homeTeam
                  OutcomeTeamAbbr = outcome })

    member private this.GameMarketsForDate
        (markets: ResizeArray<KalshiMarket>, kalshiHome: string, kalshiAway: string, easternStart: DateTime) =
        let date = easternStart.ToString("yyMMMdd", Globalization.CultureInfo.InvariantCulture).ToUpperInvariant()
        markets
        |> Seq.choose this.ParseMarketTicker
        |> Seq.filter (fun m ->
            m.GameDate = date
            && ((m.AwayTeamAbbr.Equals(kalshiAway, StringComparison.OrdinalIgnoreCase)
                 && m.HomeTeamAbbr.Equals(kalshiHome, StringComparison.OrdinalIgnoreCase))
                || (m.AwayTeamAbbr.Equals(kalshiHome, StringComparison.OrdinalIgnoreCase)
                    && m.HomeTeamAbbr.Equals(kalshiAway, StringComparison.OrdinalIgnoreCase))))
        |> Seq.groupBy (fun m -> m.EventTicker)
        |> Seq.map (fun (_, outcomes) -> Seq.toList outcomes)
        |> Seq.toList

    member private this.MatchGameMarkets
        (markets: ResizeArray<KalshiMarket>, kalshiHome: string, kalshiAway: string, easternStart: DateTime) =
        let events = this.GameMarketsForDate(markets, kalshiHome, kalshiAway, easternStart)
        let time = easternStart.ToString("HHmm", Globalization.CultureInfo.InvariantCulture)
        let selected =
            match events |> List.filter (fun outcomes -> outcomes.Head.StartTime = time) with
            | [ outcomes ] -> Some outcomes
            | [] ->
                match events with
                | [ outcomes ] when outcomes.Head.StartTime = "" && not outcomes.Head.IsDoubleheader -> Some outcomes
                | _ -> None
            | _ -> None

        match events, selected with
        | [], _ -> KalshiOddsServiceMappings.NotListed
        | _, Some outcomes ->
            let home = outcomes |> List.tryFind (fun m -> m.OutcomeTeamAbbr.Equals(kalshiHome, StringComparison.OrdinalIgnoreCase))
            let away = outcomes |> List.tryFind (fun m -> m.OutcomeTeamAbbr.Equals(kalshiAway, StringComparison.OrdinalIgnoreCase))
            match home, away with
            | Some hm, Some am -> KalshiOddsServiceMappings.Matched(hm.Market, am.Market)
            | _ -> KalshiOddsServiceMappings.Unresolved
        | _ ->
            logger.LogDebug(
                "No unambiguous Kalshi event for {Away} @ {Home} at Eastern start {Start}",
                kalshiAway, kalshiHome, easternStart)
            KalshiOddsServiceMappings.Unresolved

    member private this.TrySameDayMlbOddsAsync
        (markets: ResizeArray<KalshiMarket>, kalshiHome: string, kalshiAway: string,
         homeTeamAbbr: string, awayTeamAbbr: string, gameDate: DateTime, easternStart: DateTime, ct: CancellationToken) = task {
        let events = this.GameMarketsForDate(markets, kalshiHome, kalshiAway, easternStart)
        match events with
        | [ outcomes ] when outcomes.Length = 2
                           && (outcomes |> List.forall (fun m ->
                               m.EventTicker.StartsWith("KXMLBGAME-", StringComparison.Ordinal)
                               && not m.IsDoubleheader && m.Market.EventTicker = m.EventTicker
                               && m.HomeTeamAbbr.Equals(kalshiHome, StringComparison.OrdinalIgnoreCase)
                               && m.AwayTeamAbbr.Equals(kalshiAway, StringComparison.OrdinalIgnoreCase))) ->
            let home = outcomes |> List.tryFind (fun m -> m.OutcomeTeamAbbr.Equals(kalshiHome, StringComparison.OrdinalIgnoreCase))
            let away = outcomes |> List.tryFind (fun m -> m.OutcomeTeamAbbr.Equals(kalshiAway, StringComparison.OrdinalIgnoreCase))
            match home, away with
            | Some hm, Some am when not accessRefused && KalshiMlbSchedule.canReconcile hm.GameDate hm.StartTime (gameDate.ToUniversalTime())
                                       (timeProvider.GetUtcNow().UtcDateTime) hm.Market am.Market ->
                try
                    let url = $"{baseUrl}/milestones?limit=2&related_event_ticker={Uri.EscapeDataString(hm.EventTicker)}"
                    let! body = httpClient.GetStringAsync(url, ct)
                    if KalshiMlbSchedule.confirmsStart body hm.EventTicker (gameDate.ToUniversalTime()) hm.Market am.Market then
                        logger.LogInformation("Verified same-day MLB schedule change for {Away} @ {Home}: {EventTicker} now starts at {Start}",
                                              awayTeamAbbr, homeTeamAbbr, hm.EventTicker, gameDate)
                        return this.BuildOddsFromMarkets(homeTeamAbbr, awayTeamAbbr, kalshiHome, kalshiAway, hm.Market, am.Market)
                    else
                        return Unchecked.defaultof<KalshiMoneylineOdds>
                with
                | :? OperationCanceledException -> return Unchecked.defaultof<KalshiMoneylineOdds>
                | ex ->
                    if ex :? KalshiAccessRefusedException then accessRefused <- true
                    logger.Log((if KalshiFailure.Is ex then LogLevel.Error else LogLevel.Warning), ex, "Could not verify MLB schedule for {EventTicker}", hm.EventTicker)
                    return Unchecked.defaultof<KalshiMoneylineOdds>
            | _ -> return Unchecked.defaultof<KalshiMoneylineOdds>
        | _ -> return Unchecked.defaultof<KalshiMoneylineOdds>
    }

    member private this.ParseWorldCupMarketTicker(market: KalshiMarket) : KalshiGameMarket option =
        let m = KalshiOddsServiceMappings.worldCupTickerRegex.Match(market.Ticker)

        if not m.Success then
            logger.LogDebug("Could not parse Kalshi World Cup ticker: {Ticker}", market.Ticker)
            None
        else
            let matchup = m.Groups.["matchup"].Value
            let firstTeam = matchup.Substring(0, 3)
            let secondTeam = matchup.Substring(3, 3)

            Some
                { Ticker = market.Ticker
                  GameDate = m.Groups.["date"].Value
                  AwayTeamAbbr = firstTeam
                  HomeTeamAbbr = secondTeam
                  OutcomeTeamAbbr = m.Groups.["outcome"].Value
                  YesBid = market.YesBid
                  YesAsk = market.YesAsk
                  YesBidDollars = market.YesBidDollars
                  YesAskDollars = market.YesAskDollars }

    member private this.GetMarketProbability(market: KalshiMarket) =
        this.GetMidPriceDollars(market.YesBidDollars, market.YesAskDollars)
        |> Option.orElse (this.GetMidPrice(market.YesBid, market.YesAsk))
        |> Option.orElse (this.ParseDollars(market.LastPriceDollars))
        |> Option.orElse (
            if market.LastPrice.HasValue then
                Some(float market.LastPrice.Value / 100.0)
            else
                None
        )

    member private this.BuildThreeWayOddsFromMarkets
        (
            homeTeamAbbr: string,
            awayTeamAbbr: string,
            homeMarket: KalshiMarket,
            drawMarket: KalshiMarket,
            awayMarket: KalshiMarket
        ) : KalshiThreeWayOdds =
        let homeJson = JsonSerializer.SerializeToElement(homeMarket)
        let drawJson = JsonSerializer.SerializeToElement(drawMarket)
        let awayJson = JsonSerializer.SerializeToElement(awayMarket)
        let fallback = backend.Services.GameWinnerProbabilityPolicy.ThreeWayFallback(homeJson, drawJson, awayJson, DateTime.UtcNow)
        let rejectedEvidence =
            isNull fallback &&
            (([homeMarket; drawMarket; awayMarket] |> List.exists (fun m -> not (String.IsNullOrEmpty(m.Result))))
             || ([homeJson; drawJson; awayJson] |> List.exists backend.Services.GameWinnerProbabilityPolicy.RequiresBound))
        let probability market bound =
            if not (isNull fallback) then Some bound
            elif rejectedEvidence then None
            else this.GetMarketProbability(market)
        let homeProb = probability homeMarket (if isNull fallback then 0.0 else fallback.HomeProbability)
        let drawProb = probability drawMarket (if isNull fallback then 0.0 else fallback.DrawProbability)
        let awayProb = probability awayMarket (if isNull fallback then 0.0 else fallback.AwayProbability)

        match homeProb, drawProb, awayProb with
        | Some hp, Some dp, Some ap ->
            let total = hp + dp + ap

            if total <= 0.0 then
                Unchecked.defaultof<KalshiThreeWayOdds>
            else
                let normalizedHomeProb = hp / total
                let normalizedDrawProb = dp / total
                let normalizedAwayProb = ap / total

                { GameId = $"{awayTeamAbbr}@{homeTeamAbbr}"
                  HomeMoneyline = this.ProbabilityToMoneyline(normalizedHomeProb)
                  DrawMoneyline = this.ProbabilityToMoneyline(normalizedDrawProb)
                  AwayMoneyline = this.ProbabilityToMoneyline(normalizedAwayProb)
                  HomeTeamAbbr = homeTeamAbbr
                  AwayTeamAbbr = awayTeamAbbr
                  HomeWinProbability = normalizedHomeProb
                  DrawWinProbability = normalizedDrawProb
                  AwayWinProbability = normalizedAwayProb
                  Source = if isNull fallback then "Kalshi" else fallback.Source
                  IsLive = true }
        | _ ->
            logger.LogDebug(
                "Missing World Cup Kalshi prices for {Away} @ {Home}: home={HomeProb}, draw={DrawProb}, away={AwayProb}",
                awayTeamAbbr,
                homeTeamAbbr,
                homeProb,
                drawProb,
                awayProb
            )

            Unchecked.defaultof<KalshiThreeWayOdds>

    member private this.FetchWorldCupMarketsAsync
        (seriesTicker: string, cacheKey: string, ?ct: CancellationToken)
        : Task<struct (ResizeArray<KalshiMarket> * bool)> =
        let ct = defaultArg ct CancellationToken.None
        let maxPages = 20

        task {
            match marketsCache.TryGetValue(cacheKey) with
            | true, struct (markets, expiry) when expiry > DateTime.UtcNow -> return struct (markets, true)
            | _ when accessRefused -> return struct (ResizeArray<KalshiMarket>(), false)
            | _ ->
                try
                    let allMarkets = ResizeArray<KalshiMarket>()
                    let seenCursors = HashSet<string>(StringComparer.Ordinal)
                    let mutable cursor = ""
                    let mutable hasMore = true
                    let mutable pageCount = 0

                    while hasMore && pageCount < maxPages do
                        pageCount <- pageCount + 1

                        let url =
                            if String.IsNullOrEmpty(cursor) then
                                $"{baseUrl}/markets?series_ticker={seriesTicker}&limit=200"
                            else
                                $"{baseUrl}/markets?series_ticker={seriesTicker}&limit=200&cursor={cursor}"

                        let! response = httpClient.GetStringAsync(url, ct)
                        let result = JsonSerializer.Deserialize<KalshiMarketsResponse>(response)

                        if isNull (box result) || isNull result.Markets || result.Markets.Count = 0 then
                            hasMore <- false
                        else
                            for m in result.Markets do
                                allMarkets.Add(m)

                            if String.IsNullOrEmpty(result.Cursor) then
                                hasMore <- false
                            elif seenCursors.Contains(result.Cursor) then
                                logger.LogWarning(
                                    "Kalshi World Cup markets cursor repeated after {PageCount} pages; returning {Count} markets fetched so far",
                                    pageCount,
                                    allMarkets.Count)
                                hasMore <- false
                            else
                                seenCursors.Add(result.Cursor) |> ignore
                                cursor <- result.Cursor

                    if pageCount >= maxPages && hasMore then
                        logger.LogWarning(
                            "Kalshi World Cup markets pagination reached {MaxPages} pages; returning {Count} markets fetched so far",
                            maxPages,
                            allMarkets.Count)

                    marketsCache.[cacheKey] <- struct (allMarkets, DateTime.UtcNow.Add(KalshiOddsServiceMappings.cacheDuration))
                    logger.LogDebug("Fetched {Count} World Cup markets from Kalshi", allMarkets.Count)
                    return struct (allMarkets, true)
                with
                | :? OperationCanceledException -> logger.LogDebug("Operation cancelled: Kalshi World Cup markets fetch"); return struct (ResizeArray<KalshiMarket>(), false)
                | ex ->
                    if ex :? KalshiAccessRefusedException then accessRefused <- true
                    logger.Log((if KalshiFailure.Is ex then LogLevel.Error else LogLevel.Warning), ex, "Failed to fetch World Cup markets from Kalshi")
                    return struct (ResizeArray<KalshiMarket>(), false)
        }

    member private this.FetchWorldCupOutcomeAsync(prefix: string, outcome: string, ct: CancellationToken) =
        this.FetchMarketByTickerAsync($"{prefix}-{outcome}", ct)

    member private this.TryDirectWorldCupMarketLookupAsync
        (
            dateStr: string,
            kalshiHome: string,
            kalshiAway: string,
            homeTeamAbbr: string,
            awayTeamAbbr: string,
            ct: CancellationToken
        ) : Task<KalshiThreeWayOdds> =
        task {
            let tryPrefix prefix = task {
                let! homeMarket = this.FetchWorldCupOutcomeAsync(prefix, kalshiHome, ct)
                let! drawMarket = this.FetchWorldCupOutcomeAsync(prefix, "TIE", ct)
                let! awayMarket = this.FetchWorldCupOutcomeAsync(prefix, kalshiAway, ct)

                match homeMarket, drawMarket, awayMarket with
                | Some hm, Some dm, Some am ->
                    return Some(this.BuildThreeWayOddsFromMarkets(homeTeamAbbr, awayTeamAbbr, hm, dm, am))
                | _ -> return None
            }

            let prefix1 = $"{KalshiOddsServiceMappings.worldCupSeriesTicker}-{dateStr}{kalshiHome}{kalshiAway}"
            let! result1 = tryPrefix prefix1

            match result1 with
            | Some odds -> return odds
            | None ->
                let prefix2 = $"{KalshiOddsServiceMappings.worldCupSeriesTicker}-{dateStr}{kalshiAway}{kalshiHome}"
                let! result2 = tryPrefix prefix2

                match result2 with
                | Some odds -> return odds
                | None ->
                    logger.LogDebug(
                        "No Kalshi World Cup market exists for {Away} @ {Home} on {Date} (tried prefixes: {Prefix1}, {Prefix2})",
                        awayTeamAbbr, homeTeamAbbr, dateStr, prefix1, prefix2)
                    return Unchecked.defaultof<KalshiThreeWayOdds>
        }

    member private this.FetchMarketsAsync(sport: string, ?ct: CancellationToken) : Task<ResizeArray<KalshiMarket>> =
        let ct = defaultArg ct CancellationToken.None

        task {
            match KalshiOddsServiceMappings.sportSeriesTickers.TryGetValue(sport) with
            | false, _ ->
                logger.LogDebug("Unsupported sport for Kalshi: {Sport}", sport)
                return ResizeArray<KalshiMarket>()
            | true, seriesTicker ->
                match marketsCache.TryGetValue(sport) with
                | true, struct (markets, expiry) when expiry > DateTime.UtcNow -> return markets
                | _ when accessRefused -> return ResizeArray<KalshiMarket>()
                | _ ->
                    try
                        let allMarkets = ResizeArray<KalshiMarket>()
                        let mutable cursor = ""
                        let mutable hasMore = true

                        while hasMore do
                            let url =
                                if String.IsNullOrEmpty(cursor) then
                                    $"{baseUrl}/markets?series_ticker={seriesTicker}&limit=200"
                                else
                                    $"{baseUrl}/markets?series_ticker={seriesTicker}&limit=200&cursor={cursor}"

                            let! response = httpClient.GetStringAsync(url, ct)
                            let result = JsonSerializer.Deserialize<KalshiMarketsResponse>(response)

                            if isNull (box result) || isNull result.Markets || result.Markets.Count = 0 then
                                hasMore <- false
                            else
                                for m in result.Markets do
                                    allMarkets.Add(m)

                                if String.IsNullOrEmpty(result.Cursor) then
                                    hasMore <- false
                                else
                                    cursor <- result.Cursor

                        marketsCache.[sport] <- struct (allMarkets, DateTime.UtcNow.Add(KalshiOddsServiceMappings.cacheDuration))

                        logger.LogDebug(
                            "Fetched {Count} active markets from Kalshi for {Sport}",
                            allMarkets.Count,
                            sport
                        )

                        return allMarkets
                    with
                    | :? OperationCanceledException -> logger.LogDebug("Operation cancelled: Kalshi markets fetch for {Sport}", sport); return ResizeArray<KalshiMarket>()
                    | ex ->
                        if ex :? KalshiAccessRefusedException then accessRefused <- true
                        logger.Log((if KalshiFailure.Is ex then LogLevel.Error else LogLevel.Warning), ex, "Failed to fetch markets from Kalshi for {Sport}", sport)
                        return ResizeArray<KalshiMarket>()
        }

    member private this.FetchMarketByTickerAsync(ticker: string, ?ct: CancellationToken) : Task<KalshiMarket option> =
        let ct = defaultArg ct CancellationToken.None

        task {
            if accessRefused then return None else
            try
                let url = $"{baseUrl}/markets/{ticker}"
                let! response = httpClient.GetStringAsync(url, ct)
                let wrapper = JsonSerializer.Deserialize<KalshiSingleMarketResponse>(response)

                if isNull (box wrapper) || isNull (box wrapper.Market) then
                    return None
                else
                    return Some wrapper.Market
            with
            | :? System.Net.Http.HttpRequestException as ex when
                ex.StatusCode = Nullable(System.Net.HttpStatusCode.NotFound) ->
                return None
            | :? OperationCanceledException -> return None
            | ex ->
                if ex :? KalshiAccessRefusedException then accessRefused <- true
                logger.Log((if KalshiFailure.Is ex then LogLevel.Error else LogLevel.Debug), ex, "Failed to fetch Kalshi market {Ticker}", ticker)
                return None
        }

    member private this.BuildOddsFromMarkets
        (
            homeTeamAbbr: string,
            awayTeamAbbr: string,
            kalshiHome: string,
            kalshiAway: string,
            homeMarket: KalshiMarket,
            awayMarket: KalshiMarket
        ) : KalshiMoneylineOdds =
        let homeJson = JsonSerializer.SerializeToElement(homeMarket)
        let awayJson = JsonSerializer.SerializeToElement(awayMarket)
        let fallback =
            backend.Services.GameWinnerProbabilityPolicy.Fallback(homeJson, awayJson, DateTime.UtcNow)
        let rejectedEvidence =
            isNull fallback &&
            (not (String.IsNullOrEmpty(homeMarket.Result)) || not (String.IsNullOrEmpty(awayMarket.Result))
             || backend.Services.GameWinnerProbabilityPolicy.RequiresBound(homeJson)
             || backend.Services.GameWinnerProbabilityPolicy.RequiresBound(awayJson))
        let homeProb =
            if not (isNull fallback) then Some fallback.HomeProbability
            elif rejectedEvidence then None
            else this.GetMidPriceDollars(homeMarket.YesBidDollars, homeMarket.YesAskDollars)
                 |> Option.orElse (this.GetMidPrice(homeMarket.YesBid, homeMarket.YesAsk))
        let awayProb =
            if not (isNull fallback) then Some (1.0 - fallback.HomeProbability)
            elif rejectedEvidence then None
            else this.GetMidPriceDollars(awayMarket.YesBidDollars, awayMarket.YesAskDollars)
                 |> Option.orElse (this.GetMidPrice(awayMarket.YesBid, awayMarket.YesAsk))

        match homeProb, awayProb with
        | None, _ | _, None ->
            logger.LogDebug(
                "Missing prices for {Away} @ {Home}: homeProb={HomeProb}, awayProb={AwayProb}",
                awayTeamAbbr,
                homeTeamAbbr,
                homeProb,
                awayProb
            )

            Unchecked.defaultof<KalshiMoneylineOdds>
        | Some hp, Some ap ->
            let total = hp + ap
            let normalizedHomeProb = if total > 0.0 then hp / total else 0.5
            let normalizedAwayProb = if total > 0.0 then ap / total else 0.5

            let homeMoneyline = this.ProbabilityToMoneyline(normalizedHomeProb)
            let awayMoneyline = this.ProbabilityToMoneyline(normalizedAwayProb)

            let result =
                { GameId = $"{awayTeamAbbr}@{homeTeamAbbr}"
                  HomeMoneyline = homeMoneyline
                  AwayMoneyline = awayMoneyline
                  HomeTeamAbbr = homeTeamAbbr
                  AwayTeamAbbr = awayTeamAbbr
                  HomeWinProbability = normalizedHomeProb
                  AwayWinProbability = normalizedAwayProb
                  Source = if isNull fallback then "Kalshi" else fallback.Source
                  IsLive = true }

            logger.LogDebug(
                "Kalshi odds for {Away} @ {Home}: {HomeAbbr} {HomeOdds} ({HomeProb:P1}), {AwayAbbr} {AwayOdds} ({AwayProb:P1})",
                awayTeamAbbr,
                homeTeamAbbr,
                homeTeamAbbr,
                result.HomeMoneyline,
                result.HomeWinProbability,
                awayTeamAbbr,
                result.AwayMoneyline,
                result.AwayWinProbability
            )

            result

    member private this.TryDirectMarketLookupAsync
        (
            seriesTicker: string,
            dateStr: string,
            kalshiHome: string,
            kalshiAway: string,
            homeTeamAbbr: string,
            awayTeamAbbr: string,
            ct: CancellationToken
        ) : Task<KalshiMoneylineOdds> =
        task {
            let homeTicker = $"{seriesTicker}-{dateStr}{kalshiAway}{kalshiHome}-{kalshiHome}"
            let awayTicker = $"{seriesTicker}-{dateStr}{kalshiAway}{kalshiHome}-{kalshiAway}"

            let! homeMarket = this.FetchMarketByTickerAsync(homeTicker, ct)
            let! awayMarket = this.FetchMarketByTickerAsync(awayTicker, ct)

            match homeMarket, awayMarket with
            | Some hm, Some am ->
                logger.LogDebug(
                    "Found Kalshi markets via direct lookup for {Away} @ {Home} (ticker: {Ticker})",
                    awayTeamAbbr, homeTeamAbbr, homeTicker)
                return this.BuildOddsFromMarkets(homeTeamAbbr, awayTeamAbbr, kalshiHome, kalshiAway, hm, am)
            | _ ->
                let homeTicker2 = $"{seriesTicker}-{dateStr}{kalshiHome}{kalshiAway}-{kalshiHome}"
                let awayTicker2 = $"{seriesTicker}-{dateStr}{kalshiHome}{kalshiAway}-{kalshiAway}"

                let! homeMarket2 = this.FetchMarketByTickerAsync(homeTicker2, ct)
                let! awayMarket2 = this.FetchMarketByTickerAsync(awayTicker2, ct)

                match homeMarket2, awayMarket2 with
                | Some hm, Some am ->
                    logger.LogDebug(
                        "Found Kalshi markets via direct lookup (reversed) for {Away} @ {Home} (ticker: {Ticker})",
                        awayTeamAbbr, homeTeamAbbr, homeTicker2)
                    return this.BuildOddsFromMarkets(homeTeamAbbr, awayTeamAbbr, kalshiHome, kalshiAway, hm, am)
                | _ ->
                    logger.LogDebug(
                        "No Kalshi market exists for {Away} @ {Home} on {Date} (tried: {Ticker1}, {Ticker2})",
                        awayTeamAbbr, homeTeamAbbr, dateStr, homeTicker, homeTicker2)
                    return Unchecked.defaultof<KalshiMoneylineOdds>
        }

    member private this.FetchPregameMarketsAsync(sport: string, now: DateTime, ct: CancellationToken) = task {
        let cacheKey = $"pregame:{sport}"
        match marketsCache.TryGetValue(cacheKey) with
        | true, struct (markets, expiry) when expiry > now -> return markets
        | _ ->
            let series = KalshiOddsServiceMappings.sportSeriesTickers.[sport]
            let markets = ResizeArray<KalshiMarket>()
            let cursors = HashSet<string>()
            let mutable cursor = ""
            let mutable complete = false
            while not complete do
                ct.ThrowIfCancellationRequested()
                let suffix = if cursor = "" then "" else $"&cursor={Uri.EscapeDataString(cursor)}"
                let! body = httpClient.GetStringAsync($"{baseUrl}/markets?series_ticker={series}&status=open&limit=200{suffix}", ct)
                let response =
                    try JsonSerializer.Deserialize<KalshiMarketsResponse>(body)
                    with :? JsonException -> Unchecked.defaultof<KalshiMarketsResponse>
                if isNull (box response) || isNull response.Markets then
                    markets.Clear()
                    complete <- true
                else
                    markets.AddRange(response.Markets)
                    cursor <- response.Cursor
                    complete <- String.IsNullOrEmpty(cursor)
                    if not complete && (not (cursors.Add(cursor)) || cursors.Count >= 20) then
                        invalidOp "Kalshi pregame catalog pagination is incomplete"
            marketsCache.[cacheKey] <- struct (markets, now.Add(KalshiOddsServiceMappings.cacheDuration))
            return markets
    }

    member private this.BuildPregameOdds
        (sport: string, homeTeam: string, awayTeam: string, gameDate: DateTime, now: DateTime,
         homeMarket: KalshiMarket, awayMarket: KalshiMarket) =
        let series = KalshiOddsServiceMappings.sportSeriesTickers.[sport]
        let midpoint (market: KalshiMarket) =
            let price dollars (cents: Nullable<int>) =
                if String.IsNullOrEmpty(dollars) then
                    if cents.HasValue then Some(float cents.Value / 100.0) else None
                else this.ParseDollars(dollars)
            let split = market.Ticker.LastIndexOf('-')
            let eventTicker = if split > 0 then market.Ticker.Substring(0, split) else ""
            match price market.YesBidDollars market.YesBid, price market.YesAskDollars market.YesAsk,
                  this.ParseDollars(market.YesBidSizeFp), this.ParseDollars(market.YesAskSizeFp) with
            | Some bid, Some ask, Some bidSize, Some askSize
                when market.Status = "active" && String.IsNullOrEmpty(market.Result)
                     && market.MarketType = "binary" && this.ParseDollars(market.NotionalValueDollars) = Some 1.0
                     && market.EventTicker = eventTicker && eventTicker.StartsWith(series + "-", StringComparison.Ordinal)
                     && market.CloseTime.HasValue && market.CloseTime.Value > now && market.CloseTime.Value > gameDate
                     && Double.IsFinite(bid) && Double.IsFinite(ask) && bid > 0.0 && ask < 1.0 && bid <= ask
                     && Double.IsFinite(bidSize) && Double.IsFinite(askSize) && bidSize > 0.0 && askSize > 0.0 ->
                Some((bid + ask) / 2.0)
            | _ -> None
        match midpoint homeMarket, midpoint awayMarket with
        | Some home, Some away when homeMarket.EventTicker = awayMarket.EventTicker ->
            let total = home + away
            let homeProbability, awayProbability = home / total, away / total
            { GameId = $"{awayTeam}@{homeTeam}"
              HomeTeamAbbr = homeTeam; AwayTeamAbbr = awayTeam
              HomeMoneyline = this.ProbabilityToMoneyline(homeProbability)
              AwayMoneyline = this.ProbabilityToMoneyline(awayProbability)
              HomeWinProbability = homeProbability; AwayWinProbability = awayProbability
              Source = "kalshi"; IsLive = false }
        | _ -> Unchecked.defaultof<KalshiMoneylineOdds>

    member this.GetPregameOddsAsync
        (sport: string, homeTeamAbbr: string, awayTeamAbbr: string, gameDate: DateTime, now: DateTime, ?ct: CancellationToken)
        : Task<KalshiMoneylineOdds> = task {
        let ct = defaultArg ct CancellationToken.None
        let sport = sport.ToLowerInvariant()
        if not (KalshiOddsService.SupportsSport(sport)) || gameDate <= now then
            return Unchecked.defaultof<KalshiMoneylineOdds>
        else
            let! catalog = this.FetchPregameMarketsAsync(sport, now, ct)
            let series = KalshiOddsServiceMappings.sportSeriesTickers.[sport]
            let markets = catalog |> Seq.filter (fun m -> not (isNull (box m)) && not (isNull m.Ticker)
                                                          && m.Ticker.StartsWith(series + "-", StringComparison.Ordinal)) |> ResizeArray
            let easternStart = TimeZoneInfo.ConvertTimeFromUtc(gameDate.ToUniversalTime(), TimeZoneInfo.FindSystemTimeZoneById("America/New_York"))
            let home = this.MapEspnToKalshi(sport, homeTeamAbbr)
            let away = this.MapEspnToKalshi(sport, awayTeamAbbr)
            match this.MatchGameMarkets(markets, home, away, easternStart) with
            | KalshiOddsServiceMappings.Matched(hm, am)
                when (markets |> Seq.filter (fun m -> m.Ticker = hm.Ticker) |> Seq.length) = 1
                     && (markets |> Seq.filter (fun m -> m.Ticker = am.Ticker) |> Seq.length) = 1 ->
                return this.BuildPregameOdds(sport, homeTeamAbbr, awayTeamAbbr, gameDate, now, hm, am)
            | _ -> return Unchecked.defaultof<KalshiMoneylineOdds>
    }

    member this.GetLiveOddsAsync
        (
            sport: string,
            homeTeamAbbr: string,
            awayTeamAbbr: string,
            gameDate: DateTime,
            ?ct: CancellationToken
        ) : Task<KalshiMoneylineOdds> =
        let ct = defaultArg ct CancellationToken.None

        task {
            let kalshiHome = this.MapEspnToKalshi(sport, homeTeamAbbr)
            let kalshiAway = this.MapEspnToKalshi(sport, awayTeamAbbr)

            let easternZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York")
            let easternDate = TimeZoneInfo.ConvertTimeFromUtc(gameDate.ToUniversalTime(), easternZone)
            let expectedDateStr = easternDate.ToString("yyMMMdd", Globalization.CultureInfo.InvariantCulture).ToUpperInvariant()

            let! markets = this.FetchMarketsAsync(sport, ct)

            match this.MatchGameMarkets(markets, kalshiHome, kalshiAway, easternDate) with
            | KalshiOddsServiceMappings.Matched(hm, am) ->
                return this.BuildOddsFromMarkets(homeTeamAbbr, awayTeamAbbr, kalshiHome, kalshiAway, hm, am)
            | KalshiOddsServiceMappings.Unresolved when sport.Equals("mlb", StringComparison.OrdinalIgnoreCase) ->
                return! this.TrySameDayMlbOddsAsync(markets, kalshiHome, kalshiAway, homeTeamAbbr, awayTeamAbbr, gameDate, easternDate, ct)
            | KalshiOddsServiceMappings.Unresolved ->
                return Unchecked.defaultof<KalshiMoneylineOdds>
            | KalshiOddsServiceMappings.NotListed ->
                match KalshiOddsServiceMappings.sportSeriesTickers.TryGetValue(sport) with
                | true, seriesTicker ->
                    return! this.TryDirectMarketLookupAsync(
                        seriesTicker, expectedDateStr, kalshiHome, kalshiAway,
                        homeTeamAbbr, awayTeamAbbr, ct)
                | _ ->
                    return Unchecked.defaultof<KalshiMoneylineOdds>
        }

    member this.GetWorldCupThreeWayLiveOddsAsync
        (
            homeTeamAbbr: string,
            awayTeamAbbr: string,
            gameDate: DateTime,
            ?ct: CancellationToken
        ) : Task<KalshiThreeWayOdds> =
        let ct = defaultArg ct CancellationToken.None

        task {
            let sport = "world_cup"
            let kalshiHome = this.MapEspnToKalshi(sport, homeTeamAbbr)
            let kalshiAway = this.MapEspnToKalshi(sport, awayTeamAbbr)

            let easternZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York")
            let easternDate = TimeZoneInfo.ConvertTimeFromUtc(gameDate.ToUniversalTime(), easternZone)
            let expectedDateStr = easternDate.ToString("yyMMMdd").ToUpperInvariant()

            let! struct (markets, marketsFetchSucceeded) =
                this.FetchWorldCupMarketsAsync(
                    KalshiOddsServiceMappings.worldCupSeriesTicker,
                    "world_cup:three_way",
                    ct)

            if markets.Count = 0 && not marketsFetchSucceeded then
                return Unchecked.defaultof<KalshiThreeWayOdds>
            else
                let marketByTicker = markets |> Seq.map (fun m -> m.Ticker, m) |> dict

                let gameMarkets =
                    markets
                    |> Seq.choose this.ParseWorldCupMarketTicker
                    |> Seq.filter (fun m ->
                        m.GameDate = expectedDateStr
                        && ((m.AwayTeamAbbr.Equals(kalshiHome, StringComparison.OrdinalIgnoreCase)
                             && m.HomeTeamAbbr.Equals(kalshiAway, StringComparison.OrdinalIgnoreCase))
                            || (m.AwayTeamAbbr.Equals(kalshiAway, StringComparison.OrdinalIgnoreCase)
                                && m.HomeTeamAbbr.Equals(kalshiHome, StringComparison.OrdinalIgnoreCase))))
                    |> Seq.toList

                let rawMarket (parsed: KalshiGameMarket) =
                    match marketByTicker.TryGetValue(parsed.Ticker) with
                    | true, market -> Some market
                    | _ -> None

                let homeMarket =
                    gameMarkets
                    |> List.tryFind (fun m -> m.OutcomeTeamAbbr.Equals(kalshiHome, StringComparison.OrdinalIgnoreCase))
                    |> Option.bind rawMarket

                let drawMarket =
                    gameMarkets
                    |> List.tryFind (fun m -> m.OutcomeTeamAbbr.Equals("TIE", StringComparison.OrdinalIgnoreCase))
                    |> Option.bind rawMarket

                let awayMarket =
                    gameMarkets
                    |> List.tryFind (fun m -> m.OutcomeTeamAbbr.Equals(kalshiAway, StringComparison.OrdinalIgnoreCase))
                    |> Option.bind rawMarket

                match homeMarket, drawMarket, awayMarket with
                | Some hm, Some dm, Some am ->
                    let odds = this.BuildThreeWayOddsFromMarkets(homeTeamAbbr, awayTeamAbbr, hm, dm, am)

                    if not (isNull (box odds)) then
                        logger.LogDebug(
                            "Kalshi World Cup odds for {Away} @ {Home}: home {HomeProb:P1}, draw {DrawProb:P1}, away {AwayProb:P1}",
                            awayTeamAbbr,
                            homeTeamAbbr,
                            odds.HomeWinProbability,
                            odds.DrawWinProbability,
                            odds.AwayWinProbability)

                    return odds
                | _ ->
                    return! this.TryDirectWorldCupMarketLookupAsync(
                        expectedDateStr,
                        kalshiHome,
                        kalshiAway,
                        homeTeamAbbr,
                        awayTeamAbbr,
                        ct)
        }

    member this.GetWorldCupAdvanceLiveOddsAsync
        (
            homeTeamAbbr: string,
            awayTeamAbbr: string,
            gameDate: DateTime,
            ?ct: CancellationToken
        ) : Task<KalshiMoneylineOdds> =
        let ct = defaultArg ct CancellationToken.None

        task {
            let sport = "world_cup"
            let kalshiHome = this.MapEspnToKalshi(sport, homeTeamAbbr)
            let kalshiAway = this.MapEspnToKalshi(sport, awayTeamAbbr)

            let easternZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York")
            let easternDate = TimeZoneInfo.ConvertTimeFromUtc(gameDate.ToUniversalTime(), easternZone)
            let expectedDateStr = easternDate.ToString("yyMMMdd").ToUpperInvariant()

            let! struct (markets, marketsFetchSucceeded) =
                this.FetchWorldCupMarketsAsync(
                    KalshiOddsServiceMappings.worldCupAdvanceSeriesTicker,
                    "world_cup:advance",
                    ct)

            if markets.Count = 0 && not marketsFetchSucceeded then
                return Unchecked.defaultof<KalshiMoneylineOdds>
            else
                let marketByTicker = markets |> Seq.map (fun m -> m.Ticker, m) |> dict

                let gameMarkets =
                    markets
                    |> Seq.choose this.ParseWorldCupMarketTicker
                    |> Seq.filter (fun m ->
                        m.GameDate = expectedDateStr
                        && ((m.AwayTeamAbbr.Equals(kalshiHome, StringComparison.OrdinalIgnoreCase)
                             && m.HomeTeamAbbr.Equals(kalshiAway, StringComparison.OrdinalIgnoreCase))
                            || (m.AwayTeamAbbr.Equals(kalshiAway, StringComparison.OrdinalIgnoreCase)
                                && m.HomeTeamAbbr.Equals(kalshiHome, StringComparison.OrdinalIgnoreCase))))
                    |> Seq.toList

                let rawMarket (parsed: KalshiGameMarket) =
                    match marketByTicker.TryGetValue(parsed.Ticker) with
                    | true, market -> Some market
                    | _ -> None

                let homeMarket =
                    gameMarkets
                    |> List.tryFind (fun m -> m.OutcomeTeamAbbr.Equals(kalshiHome, StringComparison.OrdinalIgnoreCase))
                    |> Option.bind rawMarket

                let awayMarket =
                    gameMarkets
                    |> List.tryFind (fun m -> m.OutcomeTeamAbbr.Equals(kalshiAway, StringComparison.OrdinalIgnoreCase))
                    |> Option.bind rawMarket

                match homeMarket, awayMarket with
                | Some hm, Some am ->
                    return this.BuildOddsFromMarkets(homeTeamAbbr, awayTeamAbbr, kalshiHome, kalshiAway, hm, am)
                | _ ->
                    logger.LogDebug(
                        "No Kalshi World Cup advance market exists for {Away} @ {Home} on {Date}",
                        awayTeamAbbr,
                        homeTeamAbbr,
                        expectedDateStr)
                    return Unchecked.defaultof<KalshiMoneylineOdds>
        }

    member this.GetHistoricalOddsAsync
        (
            sport: string,
            homeTeamAbbr: string,
            awayTeamAbbr: string,
            gameDate: DateTime,
            ?periodInterval: int,
            ?ct: CancellationToken
        ) : Task<ResizeArray<KalshiHistoricalOdds>> =
        let periodInterval = defaultArg periodInterval 60
        let ct = defaultArg ct CancellationToken.None

        task {
            match KalshiOddsServiceMappings.sportSeriesTickers.TryGetValue(sport) with
            | false, _ ->
                logger.LogDebug("Unsupported sport for Kalshi historical data: {Sport}", sport)
                return ResizeArray<KalshiHistoricalOdds>()
            | true, seriesTicker ->
                let kalshiHome = this.MapEspnToKalshi(sport, homeTeamAbbr)
                let kalshiAway = this.MapEspnToKalshi(sport, awayTeamAbbr)

                let easternZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York")

                let easternDate =
                    TimeZoneInfo.ConvertTimeFromUtc(gameDate.ToUniversalTime(), easternZone)

                let dateStr = easternDate.ToString("yyMMMdd").ToUpper()

                let homeTicker =
                    $"{seriesTicker}-{dateStr}{kalshiAway}{kalshiHome}-{kalshiHome}"

                let startTs =
                    DateTimeOffset(gameDate.Date.AddHours(-6.0)).ToUnixTimeSeconds()

                let endTs =
                    DateTimeOffset(gameDate.Date.AddHours(30.0)).ToUnixTimeSeconds()

                try
                    let homeUrl =
                        $"{baseUrl}/series/{seriesTicker}/markets/{homeTicker}/candlesticks?start_ts={startTs}&end_ts={endTs}&period_interval={periodInterval}"

                    let! homeResponse = httpClient.GetStringAsync(homeUrl, ct)

                    let homeData =
                        JsonSerializer.Deserialize<KalshiCandlesticksResponse>(homeResponse)

                    if
                        isNull (box homeData)
                        || isNull homeData.Candlesticks
                        || homeData.Candlesticks.Count = 0
                    then
                        logger.LogDebug(
                            "No Kalshi candlestick data found for {Away} @ {Home} on {Date}",
                            awayTeamAbbr,
                            homeTeamAbbr,
                            gameDate.ToShortDateString()
                        )

                        return ResizeArray<KalshiHistoricalOdds>()
                    else
                        let result =
                            homeData.Candlesticks
                            |> Seq.choose (fun c ->
                                if isNull (box c.Price) then
                                    None
                                else
                                    this.ParseDollars(c.Price.CloseDollars)
                                    |> Option.orElse (
                                        if c.Price.Close.HasValue then Some(float c.Price.Close.Value / 100.0)
                                        else None)
                                    |> Option.filter (fun probability -> probability >= 0.0 && probability <= 1.0)
                                    |> Option.map (fun probability ->
                                        { Timestamp =
                                            DateTimeOffset.FromUnixTimeSeconds(c.EndPeriodTs).UtcDateTime
                                          HomeWinProbability = probability
                                          AwayWinProbability = 1.0 - probability
                                          Volume = c.Volume }))
                            |> Seq.sortBy (fun o -> o.Timestamp)
                            |> ResizeArray

                        logger.LogDebug(
                            "Retrieved {Count} historical odds snapshots for {Away} @ {Home} on {Date}",
                            result.Count,
                            awayTeamAbbr,
                            homeTeamAbbr,
                            gameDate.ToShortDateString()
                        )

                        return result
                with
                | :? System.Net.Http.HttpRequestException as ex when
                    ex.StatusCode = System.Nullable(System.Net.HttpStatusCode.NotFound)
                    ->
                    logger.LogDebug(
                        "Kalshi market not found for {Away} @ {Home} on {Date} (ticker: {Ticker})",
                        awayTeamAbbr,
                        homeTeamAbbr,
                        gameDate.ToShortDateString(),
                        homeTicker
                    )

                    return ResizeArray<KalshiHistoricalOdds>()
                | ex ->
                    logger.LogWarning(
                        ex,
                        "Failed to fetch Kalshi historical data for {Away} @ {Home}",
                        awayTeamAbbr,
                        homeTeamAbbr
                    )

                    return ResizeArray<KalshiHistoricalOdds>()
        }

    member this.FindMarketTickerAsync
        (
            sport: string,
            homeTeamAbbr: string,
            awayTeamAbbr: string,
            approximateDate: DateTime,
            ?ct: CancellationToken
        ) : Task<string> =
        let ct = defaultArg ct CancellationToken.None

        task {
            match KalshiOddsServiceMappings.sportSeriesTickers.TryGetValue(sport) with
            | false, _ -> return null
            | true, seriesTicker ->
                let kalshiHome = this.MapEspnToKalshi(sport, homeTeamAbbr)
                let kalshiAway = this.MapEspnToKalshi(sport, awayTeamAbbr)

                let easternZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York")
                let easternDate = TimeZoneInfo.ConvertTimeFromUtc(approximateDate.ToUniversalTime(), easternZone)
                let dateStr = easternDate.ToString("yyMMMdd", Globalization.CultureInfo.InvariantCulture).ToUpperInvariant()
                let directTicker = $"{seriesTicker}-{dateStr}{kalshiAway}{kalshiHome}-{kalshiHome}"

                let! directResult = this.FetchMarketByTickerAsync(directTicker, ct)

                match directResult with
                | Some _ -> return directTicker
                | None ->
                    let reversedTicker = $"{seriesTicker}-{dateStr}{kalshiHome}{kalshiAway}-{kalshiHome}"
                    let! reversedResult = this.FetchMarketByTickerAsync(reversedTicker, ct)

                    match reversedResult with
                    | Some _ -> return reversedTicker
                    | None ->
                        let! markets = this.FetchMarketsAsync(sport, ct)

                        match this.MatchGameMarkets(markets, kalshiHome, kalshiAway, easternDate) with
                        | KalshiOddsServiceMappings.Matched(home, _) -> return home.Ticker
                        | _ -> return null
        }
