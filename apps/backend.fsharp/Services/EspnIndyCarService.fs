namespace backend.Services.EspnIndyCar

open System
open System.Text.Json
open Backend.FSharp
open backend.Services.Espn
open backend.Services.EspnNascar
open Microsoft.Extensions.Logging

type EspnIndyCarService
    (httpClient: Net.Http.HttpClient,
     logger: ILogger<EspnIndyCarService>) =

    static let baseUrl = EspnSiteApi.url "/apis/site/v2/sports/racing/irl"

    static let easternTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York")


    member _.FetchScoreboardAsync(?date: string) =
        task {
            let mutable url = $"{baseUrl}/scoreboard"
            match date with
            | Some d when not (String.IsNullOrEmpty(d)) -> url <- url + $"?dates={d}"
            | _ -> ()

            try
                logger.LogDebug("Fetching ESPN IndyCar scoreboard: {Url}", url)
                let! response = httpClient.GetAsync(url)
                response.EnsureSuccessStatusCode() |> ignore
                let! json = response.Content.ReadAsStringAsync()
                return JsonSerializer.Deserialize<EspnNascarScoreboardResponse>(json, HttpHelpers.jsonOptions)
            with
            | :? OperationCanceledException -> logger.LogDebug("Operation cancelled: ESPN IndyCar scoreboard fetch"); return Unchecked.defaultof<EspnNascarScoreboardResponse>
            | ex ->
                logger.LogError(ex, "Failed to fetch ESPN IndyCar scoreboard from {Url}", url)
                return Unchecked.defaultof<EspnNascarScoreboardResponse>
        }

    member this.FetchScoreboardByDateAsync(date: DateTime) =
        task {
            let utcDate =
                if date.Kind = DateTimeKind.Utc then date
                else DateTime.SpecifyKind(date, DateTimeKind.Utc)
            let easternDate = TimeZoneInfo.ConvertTimeFromUtc(utcDate, easternTimeZone)
            return! this.FetchScoreboardAsync(easternDate.ToString("yyyyMMdd"))
        }

    member _.FetchCompetitorsAsync(eventId: string) =
        task {
            let listUrl = $"https://sports.core.api.espn.com/v2/sports/racing/leagues/irl/events/{eventId}/competitions/{eventId}/competitors?limit=50"
            try
                logger.LogDebug("Fetching ESPN core IndyCar competitor list: {Url}", listUrl)
                let! listResponse = httpClient.GetAsync(listUrl)
                listResponse.EnsureSuccessStatusCode() |> ignore
                let! listJson = listResponse.Content.ReadAsStringAsync()
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
                                let! compResponse = httpClient.GetAsync(resolvedUrl)
                                compResponse.EnsureSuccessStatusCode() |> ignore
                                let! compJson = compResponse.Content.ReadAsStringAsync()
                                let comp = JsonSerializer.Deserialize<EspnCoreCompetitorItem>(compJson, HttpHelpers.jsonOptions)
                                if not (isNull (box comp)) then
                                    competitors.Add(comp)
                            with
                            | :? OperationCanceledException -> logger.LogDebug("Operation cancelled: resolving IndyCar competitor ref")
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
            | :? OperationCanceledException -> logger.LogDebug("Operation cancelled: ESPN core IndyCar competitors fetch for event {EventId}", eventId); return Unchecked.defaultof<EspnCoreCompetitorsResponse>
            | ex ->
                logger.LogWarning(ex, "Failed to fetch ESPN core IndyCar competitors for event {EventId} (falling back to scoreboard data)", eventId)
                return Unchecked.defaultof<EspnCoreCompetitorsResponse>
        }
