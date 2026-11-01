namespace backend.Services.Kalshi

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging

type KalshiF1OddsService(httpClient: System.Net.Http.HttpClient, logger: ILogger<KalshiF1OddsService>) =

    static let baseUrl = "https://api.elections.kalshi.com/trade-api/v2"
    static let seriesTicker = "KXF1RACE"
    static let cacheDuration = TimeSpan.FromSeconds(30.0)

    let mutable cache: struct (ResizeArray<KalshiMarket> * DateTime) voption = ValueNone

    do
        httpClient.DefaultRequestHeaders.Clear()
        httpClient.DefaultRequestHeaders.Add("Accept", "application/json")

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

    member private this.FetchMarketsAsync(?ct: CancellationToken) : Task<ResizeArray<KalshiMarket>> =
        let ct = defaultArg ct CancellationToken.None

        task {
            match cache with
            | ValueSome struct (markets, expiry) when expiry > DateTime.UtcNow -> return markets
            | _ ->
                let url =
                    $"{baseUrl}/markets?series_ticker={seriesTicker}&status=open&limit=200"

                try
                    let! response = httpClient.GetStringAsync(url, ct)
                    let result = JsonSerializer.Deserialize<KalshiMarketsResponse>(response)

                    let markets =
                        if isNull (box result) || isNull result.Markets then
                            ResizeArray<KalshiMarket>()
                        else
                            result.Markets
                            |> Seq.filter (fun m -> String.IsNullOrEmpty(m.Result))
                            |> ResizeArray

                    cache <- ValueSome(struct (markets, DateTime.UtcNow.Add(cacheDuration)))
                    logger.LogDebug("Fetched {Count} F1 markets from Kalshi", markets.Count)
                    return markets
                with
                | :? OperationCanceledException -> logger.LogDebug("Operation cancelled: Kalshi F1 markets fetch"); return ResizeArray<KalshiMarket>()
                | ex ->
                    logger.LogWarning(ex, "Failed to fetch F1 markets from Kalshi")
                    return ResizeArray<KalshiMarket>()
        }

    member this.GetWinProbabilitiesAsync(?ct: CancellationToken) : Task<Dictionary<string, double>> =
        let ct = defaultArg ct CancellationToken.None

        task {
            let! markets = this.FetchMarketsAsync(ct)
            let result = Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)

            for market in markets do
                let driverName = market.NoSubTitle

                if not (String.IsNullOrWhiteSpace(driverName)) then
                    let prob =
                        this.ParseDollars(market.LastPriceDollars)
                        |> Option.orElse (this.GetMidPriceDollars(market.YesBidDollars, market.YesAskDollars))
                        |> Option.orElse (
                            if market.LastPrice.HasValue then
                                Some(float market.LastPrice.Value / 100.0)
                            else
                                None
                        )
                        |> Option.orElse (this.GetMidPrice(market.YesBid, market.YesAsk))

                    match prob with
                    | Some p -> result.[driverName] <- p
                    | None -> ()

            logger.LogDebug("Parsed {Count} F1 driver win probabilities from Kalshi", result.Count)
            return result
        }
