namespace backend.Services.Nascar

open System
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging

[<CLIMutable>]
type NascarLiveFeed =
    { [<JsonPropertyName("flag_state")>] FlagState: int
      [<JsonPropertyName("lap_number")>] LapNumber: int
      [<JsonPropertyName("laps_in_race")>] LapsInRace: int
      [<JsonPropertyName("laps_to_go")>] LapsToGo: int
      [<JsonPropertyName("run_name")>] RunName: string
      [<JsonPropertyName("vehicles")>] Vehicles: ResizeArray<NascarLiveFeedVehicle> }

and [<CLIMutable>] NascarLiveFeedVehicle =
    { [<JsonPropertyName("vehicle_number")>] VehicleNumber: string
      [<JsonPropertyName("is_on_track")>] IsOnTrack: bool }

type NascarLiveFeedService(httpClient: System.Net.Http.HttpClient, logger: ILogger<NascarLiveFeedService>) =

    static let feedUrl = "https://cf.nascar.com/cacher/live/live-feed.json"

    member _.GetLiveFeedAsync(?ct: CancellationToken) : Task<NascarLiveFeed> =
        let ct = defaultArg ct CancellationToken.None

        task {
            try
                let! json = httpClient.GetStringAsync(feedUrl, ct)
                return JsonSerializer.Deserialize<NascarLiveFeed>(json)
            with
            | :? OperationCanceledException -> logger.LogDebug("Operation cancelled: NASCAR.com live feed fetch"); return Unchecked.defaultof<NascarLiveFeed>
            | ex ->
                logger.LogWarning(ex, "Failed to fetch NASCAR.com live feed")
                return Unchecked.defaultof<NascarLiveFeed>
        }
