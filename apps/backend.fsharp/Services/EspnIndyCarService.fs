namespace backend.Services.EspnIndyCar

open System
open System.Text.Json
open System.Threading
open Backend.FSharp
open backend.Services
open backend.Services.Espn
open backend.Services.EspnNascar
open Microsoft.Extensions.Logging

type EspnIndyCarService
    (httpClient: Net.Http.HttpClient,
     logger: ILogger<EspnIndyCarService>) =

    static let baseUrl = EspnSiteApi.url "/apis/site/v2/sports/racing/irl"

    static let easternTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York")


    member _.FetchScoreboardAsync(?date: string, ?ct: CancellationToken) =
        let ct = defaultArg ct CancellationToken.None

        task {
            let mutable url = $"{baseUrl}/scoreboard"
            match date with
            | Some d when not (String.IsNullOrEmpty(d)) -> url <- url + $"?dates={d}"
            | _ -> ()

            try
                logger.LogDebug("Fetching ESPN IndyCar scoreboard: {Url}", url)
                let! response = httpClient.GetAsync(url, ct)
                response.EnsureSuccessStatusCode() |> ignore
                let! json = response.Content.ReadAsStringAsync(ct)
                return JsonSerializer.Deserialize<EspnNascarScoreboardResponse>(json, HttpHelpers.jsonOptions)
            with
            | :? OperationCanceledException when ct.IsCancellationRequested -> return raise (OperationCanceledException(ct))
            | ex ->
                logger.Log((if ProviderFailure.Is ex then LogLevel.Error else LogLevel.Warning), ex, "Failed to fetch ESPN IndyCar scoreboard from {Url}", url)
                return Unchecked.defaultof<EspnNascarScoreboardResponse>
        }

    member this.FetchScoreboardByDateAsync(date: DateTime, ?ct: CancellationToken) =
        let ct = defaultArg ct CancellationToken.None

        task {
            let utcDate =
                if date.Kind = DateTimeKind.Utc then date
                else DateTime.SpecifyKind(date, DateTimeKind.Utc)
            let easternDate = TimeZoneInfo.ConvertTimeFromUtc(utcDate, easternTimeZone)
            return! this.FetchScoreboardAsync(easternDate.ToString("yyyyMMdd"), ct)
        }

    member _.FetchCompetitorsAsync(eventId: string, ?ct: CancellationToken) =
        let ct = defaultArg ct CancellationToken.None

        task {
            let listUrl = $"https://sports.core.api.espn.com/v2/sports/racing/leagues/irl/events/{eventId}/competitions/{eventId}/competitors?limit=50"
            try
                logger.LogDebug("Fetching ESPN core IndyCar competitor list: {Url}", listUrl)
                let! listResponse = httpClient.GetAsync(listUrl, ct)
                listResponse.EnsureSuccessStatusCode() |> ignore
                let! listJson = listResponse.Content.ReadAsStringAsync(ct)
                use listDoc = JsonDocument.Parse(listJson)
                let items = listDoc.RootElement.GetProperty("items")

                let competitors = ResizeArray<EspnCoreCompetitorItem>()
                for item in items.EnumerateArray() do
                    match item.TryGetProperty("$ref") with
                    | true, refElement ->
                        let refUrl = refElement.GetString()
                        if not (String.IsNullOrEmpty(refUrl)) then
                            let resolvedUrl = refUrl.Replace("sports.core.api.espn.pvt", "sports.core.api.espn.com")
                            try
                                let! compResponse = httpClient.GetAsync(resolvedUrl, ct)
                                compResponse.EnsureSuccessStatusCode() |> ignore
                                let! compJson = compResponse.Content.ReadAsStringAsync(ct)
                                let comp = JsonSerializer.Deserialize<EspnCoreCompetitorItem>(compJson, HttpHelpers.jsonOptions)
                                if not (isNull (box comp)) then
                                    competitors.Add(comp)
                            with
                            | :? OperationCanceledException when ct.IsCancellationRequested -> raise (OperationCanceledException(ct))
                            | ex ->
                                logger.LogWarning(ex, "Failed to resolve competitor ref: {Url}", resolvedUrl)
                    | false, _ ->
                        let comp = JsonSerializer.Deserialize<EspnCoreCompetitorItem>(item.GetRawText(), HttpHelpers.jsonOptions)
                        if not (isNull (box comp)) then
                            competitors.Add(comp)

                let mutable result = Unchecked.defaultof<EspnCoreCompetitorsResponse>
                result <- { Count = competitors.Count; Items = competitors }
                return result
            with
            | :? OperationCanceledException when ct.IsCancellationRequested -> return raise (OperationCanceledException(ct))
            | ex ->
                logger.LogWarning(ex, "Failed to fetch ESPN core IndyCar competitors for event {EventId} (falling back to scoreboard data)", eventId)
                return Unchecked.defaultof<EspnCoreCompetitorsResponse>
        }
