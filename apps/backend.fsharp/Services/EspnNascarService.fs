namespace backend.Services.EspnNascar

open System
open System.Text.Json
open Backend.FSharp
open backend.Services.Espn
open Microsoft.Extensions.Logging

type EspnNascarService
    (httpClient: Net.Http.HttpClient,
     logger: ILogger<EspnNascarService>) =

    static let baseUrl = EspnSiteApi.url "/apis/site/v2/sports/racing/nascar-premier"

    static let easternTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York")


    member _.FetchScoreboardAsync(?date: string) =
        task {
            let mutable url = $"{baseUrl}/scoreboard"
            match date with
            | Some d when not (String.IsNullOrEmpty(d)) -> url <- url + $"?dates={d}"
            | _ -> ()

            try
                logger.LogDebug("Fetching ESPN NASCAR scoreboard: {Url}", url)
                let! response = httpClient.GetAsync(url)
                response.EnsureSuccessStatusCode() |> ignore
                let! json = response.Content.ReadAsStringAsync()
                return JsonSerializer.Deserialize<EspnNascarScoreboardResponse>(json, HttpHelpers.jsonOptions)
            with
            | :? OperationCanceledException -> logger.LogDebug("Operation cancelled: ESPN NASCAR scoreboard fetch"); return Unchecked.defaultof<EspnNascarScoreboardResponse>
            | ex ->
                logger.LogError(ex, "Failed to fetch ESPN NASCAR scoreboard from {Url}", url)
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
            let url = $"https://sports.core.api.espn.com/v2/sports/racing/leagues/nascar-premier/events/{eventId}/competitions/{eventId}/competitors?limit=50"
            try
                logger.LogDebug("Fetching ESPN core NASCAR competitors: {Url}", url)
                let! response = httpClient.GetAsync(url)
                response.EnsureSuccessStatusCode() |> ignore
                let! json = response.Content.ReadAsStringAsync()
                return JsonSerializer.Deserialize<EspnCoreCompetitorsResponse>(json, HttpHelpers.jsonOptions)
            with
            | :? OperationCanceledException -> logger.LogDebug("Operation cancelled: ESPN core NASCAR competitors fetch for event {EventId}", eventId); return Unchecked.defaultof<EspnCoreCompetitorsResponse>
            | ex ->
                logger.LogWarning(ex, "Failed to fetch ESPN core NASCAR competitors for event {EventId}", eventId)
                return Unchecked.defaultof<EspnCoreCompetitorsResponse>
        }
