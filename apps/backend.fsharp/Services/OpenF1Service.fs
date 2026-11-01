namespace backend.Services.OpenF1

open System
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks
open Backend.FSharp
open Microsoft.Extensions.Logging

type OpenF1Service(httpClient: HttpClient, logger: ILogger<OpenF1Service>) =

    static let baseUrl = "https://api.openf1.org/v1"


    member private this.FetchAsync<'T>(url: string) : Task<'T> =
        task {
            try
                logger.LogDebug("Fetching OpenF1: {Url}", url)
                let! response = httpClient.GetAsync(url)
                response.EnsureSuccessStatusCode() |> ignore
                let! json = response.Content.ReadAsStringAsync()
                return JsonSerializer.Deserialize<'T>(json, HttpHelpers.jsonOptions)
            with
            | :? OperationCanceledException -> logger.LogDebug("Operation cancelled: OpenF1 data fetch"); return Unchecked.defaultof<'T>
            | ex ->
                logger.LogError(ex, "Failed to fetch OpenF1 data from {Url}", url)
                return Unchecked.defaultof<'T>
        }

    member this.FetchMeetingsAsync(year: int) : Task<ResizeArray<OpenF1Meeting>> =
        task {
            let url = $"{baseUrl}/meetings?year={year}"
            let! result = this.FetchAsync<ResizeArray<OpenF1Meeting>>(url)

            return
                if isNull (box result) then
                    ResizeArray<OpenF1Meeting>()
                else
                    result
        }

    member this.FetchSessionsAsync(meetingKey: int, ?sessionType: string) : Task<ResizeArray<OpenF1Session>> =
        task {
            let mutable url = $"{baseUrl}/sessions?meeting_key={meetingKey}"

            match sessionType with
            | Some st -> url <- url + $"&session_type={Uri.EscapeDataString(st)}"
            | None -> ()

            let! result = this.FetchAsync<ResizeArray<OpenF1Session>>(url)

            return
                if isNull (box result) then
                    ResizeArray<OpenF1Session>()
                else
                    result
        }

    member this.FetchDriversAsync(sessionKey: int) : Task<ResizeArray<OpenF1Driver>> =
        task {
            let url = $"{baseUrl}/drivers?session_key={sessionKey}"
            let! result = this.FetchAsync<ResizeArray<OpenF1Driver>>(url)

            return
                if isNull (box result) then
                    ResizeArray<OpenF1Driver>()
                else
                    result
        }

    member this.FetchPositionsAsync(sessionKey: int, ?since: DateTime) : Task<ResizeArray<OpenF1Position>> =
        task {
            let mutable url = $"{baseUrl}/position?session_key={sessionKey}"

            match since with
            | Some dt -> url <- url + $"&date>={dt:O}"
            | None -> ()

            let! result = this.FetchAsync<ResizeArray<OpenF1Position>>(url)

            return
                if isNull (box result) then
                    ResizeArray<OpenF1Position>()
                else
                    result
        }

    member this.FetchIntervalsAsync(sessionKey: int, ?since: DateTime) : Task<ResizeArray<OpenF1Interval>> =
        task {
            let mutable url = $"{baseUrl}/intervals?session_key={sessionKey}"

            match since with
            | Some dt -> url <- url + $"&date>={dt:O}"
            | None -> ()

            let! result = this.FetchAsync<ResizeArray<OpenF1Interval>>(url)

            return
                if isNull (box result) then
                    ResizeArray<OpenF1Interval>()
                else
                    result
        }

    member this.FetchRaceControlAsync(sessionKey: int, ?since: DateTime) : Task<ResizeArray<OpenF1RaceControl>> =
        task {
            let mutable url = $"{baseUrl}/race_control?session_key={sessionKey}"

            match since with
            | Some dt -> url <- url + $"&date>={dt:O}"
            | None -> ()

            let! result = this.FetchAsync<ResizeArray<OpenF1RaceControl>>(url)

            return
                if isNull (box result) then
                    ResizeArray<OpenF1RaceControl>()
                else
                    result
        }

    member this.FetchLapsAsync(sessionKey: int, ?driverNumber: int) : Task<ResizeArray<OpenF1Lap>> =
        task {
            let mutable url = $"{baseUrl}/laps?session_key={sessionKey}"

            match driverNumber with
            | Some dn -> url <- url + $"&driver_number={dn}"
            | None -> ()

            let! result = this.FetchAsync<ResizeArray<OpenF1Lap>>(url)

            return
                if isNull (box result) then
                    ResizeArray<OpenF1Lap>()
                else
                    result
        }
