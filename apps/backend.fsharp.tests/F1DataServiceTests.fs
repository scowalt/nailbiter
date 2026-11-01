module F1DataServiceTests

open System
open System.Net
open System.Net.Http
open System.Linq
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Microsoft.EntityFrameworkCore
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging.Abstractions
open Xunit
open backend.Data
open backend.Models
open backend.Services.EspnF1
open backend.Services.F1

type private StaticJsonHandler(json: string) =
    inherit HttpMessageHandler()

    override _.SendAsync(_request: HttpRequestMessage, _ct: CancellationToken) =
        let response = new HttpResponseMessage(HttpStatusCode.OK)
        response.Content <- new StringContent(json, Encoding.UTF8, "application/json")
        Task.FromResult(response)

let private scoreboardJson (entries: (string * string * DateTime) list) =
    let calendarJson =
        entries
        |> List.map (fun (eventId, meetingName, date) ->
            $"""
            {{
              "label": "{meetingName}",
              "startDate": "{date:O}",
              "endDate": "{date.AddHours(3.0):O}",
              "event": {{
                "$ref": "https://sports.core.api.espn.com/v2/sports/racing/leagues/f1/events/{eventId}?lang=en&region=us"
              }}
            }}
            """)
        |> String.concat ","

    let eventsJson =
        entries
        |> List.distinctBy (fun (eventId, _, _) -> eventId)
        |> List.map (fun (eventId, meetingName, date) ->
            $"""
            {{
              "id": "{eventId}",
              "date": "{date:O}",
              "name": "{meetingName}",
              "competitions": [],
              "status": {{
                "period": null,
                "type": {{
                  "name": "STATUS_SCHEDULED",
                  "state": "pre",
                  "completed": false
                }}
              }}
            }}
            """)
        |> String.concat ","

    $"""
    {{
      "leagues": [{{ "calendar": [{calendarJson}] }}],
      "events": [{eventsJson}]
    }}
    """

let private createDatabase () =
    let connection = new SqliteConnection("Data Source=:memory:")
    connection.Open()
    let services = ServiceCollection()

    services.AddDbContext<ExcitementDbContext>(fun options ->
        options.UseSqlite(connection) |> ignore)
    |> ignore

    let provider = services.BuildServiceProvider()

    do
        use scope = provider.CreateScope()
        let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
        db.Database.EnsureCreated() |> ignore

    connection, provider

let private createService (provider: IServiceProvider) (json: string) =
    let httpClient = new HttpClient(new StaticJsonHandler(json))
    let espnService = EspnF1Service(httpClient, NullLogger<EspnF1Service>.Instance)

    httpClient,
    F1DataService(
        espnService,
        provider,
        NullLogger<F1DataService>.Instance
    )

let private race
    (stableId: string)
    (espnEventId: string)
    (meetingName: string)
    (roundNumber: int)
    (date: DateTime)
    =
    F1Race(
        Id = stableId,
        EspnEventId = espnEventId,
        MeetingName = meetingName,
        CircuitName = meetingName,
        CircuitShortName = meetingName.ToLowerInvariant().Replace(" ", "_"),
        Country = "",
        Year = 2026,
        RoundNumber = roundNumber,
        ScheduledStart = date,
        Status = "scheduled",
        TotalLaps = 0
    )

let private futureDate day =
    DateTime(2026, 12, day, 18, 0, 0, DateTimeKind.Utc)

[<Fact>]
let ``RefreshSchedule inserts a middle race without replacing later races or snapshots`` () =
    task {
        let connection, provider = createDatabase ()
        use connection = connection
        use provider = provider

        do
            use scope = provider.CreateScope()
            let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
            db.F1Races.Add(race "stable-a" "espn-a" "Race A" 1 (futureDate 1)) |> ignore
            db.F1Races.Add(race "stable-c" "espn-c" "Race C" 2 (futureDate 3)) |> ignore

            db.F1RaceSnapshots.Add(
                F1RaceSnapshot(
                    RaceId = "stable-c",
                    EspnAthleteId = "driver-1",
                    Timestamp = DateTime.UtcNow,
                    Position = Nullable(1),
                    Source = "espn"
                )
            )
            |> ignore

            db.SaveChanges() |> ignore

        let json =
            scoreboardJson
                [ "espn-a", "Race A", futureDate 1
                  "espn-b", "Race B", futureDate 2
                  "espn-c", "Race C", futureDate 3 ]

        let httpClient, service = createService provider json
        use _httpClient = httpClient
        let! created = service.RefreshScheduleAsync(2026)
        Assert.Equal(1, created)

        use verificationScope = provider.CreateScope()
        let db = verificationScope.ServiceProvider.GetRequiredService<ExcitementDbContext>()

        let races =
            db.F1Races.OrderBy(fun item -> item.RoundNumber).ToArray()

        Assert.Equal<string[]>([| "stable-a"; "espn-b"; "stable-c" |], races |> Array.map _.Id)
        Assert.Equal<int[]>([| 1; 2; 3 |], races |> Array.map _.RoundNumber)
        let snapshot = Assert.Single(db.F1RaceSnapshots)
        Assert.Equal("stable-c", snapshot.RaceId)
    }

[<Fact>]
let ``RefreshSchedule reconciles a changed ESPN event ID and is idempotent`` () =
    task {
        let connection, provider = createDatabase ()
        use connection = connection
        use provider = provider

        do
            use scope = provider.CreateScope()
            let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()

            db.F1Races.Add(
                race "stable-race" "old-espn-id" "Replacement Grand Prix" 1 (futureDate 4)
            )
            |> ignore

            db.SaveChanges() |> ignore

        let json =
            scoreboardJson
                [ "new-espn-id", "Replacement Grand-Prix", futureDate 4 ]

        let httpClient, service = createService provider json
        use _httpClient = httpClient
        let! firstCreated = service.RefreshScheduleAsync(2026)
        let! secondCreated = service.RefreshScheduleAsync(2026)
        Assert.Equal(0, firstCreated)
        Assert.Equal(0, secondCreated)

        use verificationScope = provider.CreateScope()
        let db = verificationScope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
        let updatedRace = Assert.Single(db.F1Races)
        Assert.Equal("stable-race", updatedRace.Id)
        Assert.Equal("new-espn-id", updatedRace.EspnEventId)
        Assert.Equal(1, updatedRace.RoundNumber)
    }

[<Fact>]
let ``RefreshSchedule leaves round numbers untouched for an unchanged calendar`` () =
    task {
        let connection, provider = createDatabase ()
        use connection = connection
        use provider = provider

        do
            use scope = provider.CreateScope()
            let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
            db.F1Races.Add(race "stable-a" "espn-a" "Race A" 1 (futureDate 5)) |> ignore
            db.F1Races.Add(race "stable-b" "espn-b" "Race B" 2 (futureDate 6)) |> ignore
            db.SaveChanges() |> ignore

            db.Database.ExecuteSqlRaw(
                "CREATE TABLE round_updates (race_id TEXT NOT NULL); "
                + "CREATE TRIGGER audit_f1_round_updates AFTER UPDATE OF round_number ON f1_races "
                + "WHEN OLD.round_number <> NEW.round_number BEGIN "
                + "INSERT INTO round_updates (race_id) VALUES (NEW.id); END;"
            )
            |> ignore

        let json =
            scoreboardJson
                [ "espn-a", "Race A", futureDate 5
                  "espn-b", "Race B", futureDate 6 ]

        let httpClient, service = createService provider json
        use _httpClient = httpClient
        let! created = service.RefreshScheduleAsync(2026)
        Assert.Equal(0, created)

        use command = connection.CreateCommand()
        command.CommandText <- "SELECT COUNT(*) FROM round_updates"
        let roundUpdates = command.ExecuteScalar() |> Convert.ToInt32
        Assert.Equal(0, roundUpdates)
    }

[<Fact>]
let ``RefreshSchedule retains missing feed races after incoming races`` () =
    task {
        let connection, provider = createDatabase ()
        use connection = connection
        use provider = provider

        do
            use scope = provider.CreateScope()
            let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
            db.F1Races.Add(race "stable-a" "espn-a" "Race A" 1 (futureDate 7)) |> ignore
            db.F1Races.Add(race "stable-b" "espn-b" "Race B" 2 (futureDate 8)) |> ignore
            db.F1Races.Add(race "stable-c" "espn-c" "Race C" 3 (futureDate 9)) |> ignore
            db.SaveChanges() |> ignore

        let json =
            scoreboardJson
                [ "espn-a", "Race A", futureDate 7
                  "espn-c", "Race C", futureDate 9 ]

        let httpClient, service = createService provider json
        use _httpClient = httpClient
        let! created = service.RefreshScheduleAsync(2026)
        Assert.Equal(0, created)

        use verificationScope = provider.CreateScope()
        let db = verificationScope.ServiceProvider.GetRequiredService<ExcitementDbContext>()

        let orderedIds =
            db.F1Races
                .OrderBy(fun item -> item.RoundNumber)
                .Select(fun item -> item.Id)
                .ToArray()

        Assert.Equal<string[]>([| "stable-a"; "stable-c"; "stable-b" |], orderedIds)
    }

[<Fact>]
let ``RefreshSchedule rejects duplicate ESPN IDs without database mutation`` () =
    task {
        let connection, provider = createDatabase ()
        use connection = connection
        use provider = provider

        do
            use scope = provider.CreateScope()
            let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()

            db.F1Races.Add(
                race "stable-existing" "existing-event" "Existing Race" 1 (futureDate 10)
            )
            |> ignore

            db.SaveChanges() |> ignore

        let json =
            scoreboardJson
                [ "duplicate-event", "First Duplicate", futureDate 11
                  "duplicate-event", "Second Duplicate", futureDate 12 ]

        let httpClient, service = createService provider json
        use _httpClient = httpClient
        let! created = service.RefreshScheduleAsync(2026)
        Assert.Equal(0, created)

        use verificationScope = provider.CreateScope()
        let db = verificationScope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
        let unchangedRace = Assert.Single(db.F1Races)
        Assert.Equal("stable-existing", unchangedRace.Id)
        Assert.Equal("existing-event", unchangedRace.EspnEventId)
        Assert.Equal("Existing Race", unchangedRace.MeetingName)
        Assert.Equal(1, unchangedRace.RoundNumber)
    }
