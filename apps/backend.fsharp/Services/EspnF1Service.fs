namespace backend.Services.EspnF1

open System
open System.Text.Json
open System.Threading
open Backend.FSharp
open backend.Services.Espn
open backend.Services.EspnNascar
open Microsoft.Extensions.Logging

type EspnF1Service
    (httpClient: Net.Http.HttpClient,
     logger: ILogger<EspnF1Service>) =

    static let baseUrl = EspnSiteApi.url "/apis/site/v2/sports/racing/f1"

    static let easternTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York")


    member _.FetchScoreboardAsync(?date: string, ?ct: CancellationToken) =
        let ct = defaultArg ct CancellationToken.None

        task {
            let mutable url = $"{baseUrl}/scoreboard"
            match date with
            | Some d when not (String.IsNullOrEmpty(d)) -> url <- url + $"?dates={d}"
            | _ -> ()

            try
                logger.LogDebug("Fetching ESPN F1 scoreboard: {Url}", url)
                let! response = httpClient.GetAsync(url, ct)
                response.EnsureSuccessStatusCode() |> ignore
                let! json = response.Content.ReadAsStringAsync(ct)
                return JsonSerializer.Deserialize<EspnNascarScoreboardResponse>(json, HttpHelpers.jsonOptions)
            with
            | :? OperationCanceledException -> logger.LogDebug("Operation cancelled: ESPN F1 scoreboard fetch"); return Unchecked.defaultof<EspnNascarScoreboardResponse>
            | ex ->
                logger.LogError(ex, "Failed to fetch ESPN F1 scoreboard from {Url}", url)
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
            let eventUrl = $"https://sports.core.api.espn.com/v2/sports/racing/leagues/f1/events/{eventId}"
            let mutable competitionId = eventId
            try
                let! eventResponse = httpClient.GetAsync(eventUrl, ct)
                eventResponse.EnsureSuccessStatusCode() |> ignore
                let! eventJson = eventResponse.Content.ReadAsStringAsync(ct)
                use eventDoc = JsonDocument.Parse(eventJson)
                let comps = eventDoc.RootElement.GetProperty("competitions")
                let mutable lastRef = ""
                for comp in comps.EnumerateArray() do
                    match comp.TryGetProperty("$ref") with
                    | true, refEl ->
                        lastRef <- refEl.GetString()
                    | _ -> ()
                if not (System.String.IsNullOrEmpty(lastRef)) then
                    let parts = lastRef.Split('/')
                    let compIdx = parts |> Array.tryFindIndex (fun p -> p = "competitions")
                    match compIdx with
                    | Some idx when idx + 1 < parts.Length ->
                        competitionId <- parts.[idx + 1].Split('?').[0]
                    | _ -> ()
            with
            | ex -> logger.LogWarning(ex, "Could not resolve F1 competition ID for event {EventId}, using event ID", eventId)

            let listUrl = $"https://sports.core.api.espn.com/v2/sports/racing/leagues/f1/events/{eventId}/competitions/{competitionId}/competitors?limit=50"
            try
                logger.LogDebug("Fetching ESPN core F1 competitor list: {Url}", listUrl)
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
                            | :? OperationCanceledException -> logger.LogDebug("Operation cancelled: resolving F1 competitor ref")
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
            | :? OperationCanceledException -> logger.LogDebug("Operation cancelled: ESPN core F1 competitors fetch for event {EventId}", eventId); return Unchecked.defaultof<EspnCoreCompetitorsResponse>
            | ex ->
                logger.LogWarning(ex, "Failed to fetch ESPN core F1 competitors for event {EventId} (falling back to scoreboard data)", eventId)
                return Unchecked.defaultof<EspnCoreCompetitorsResponse>
        }
