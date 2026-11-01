module IndyCarDataServiceTests

open System
open System.Net
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.EntityFrameworkCore
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging.Abstractions
open Xunit
open backend.Data
open backend.Models
open backend.Services.EspnIndyCar
open backend.Services.IndyCar

type private StaticJsonHandler(json: string) =
    inherit HttpMessageHandler()

    override _.SendAsync(_request: HttpRequestMessage, _ct: CancellationToken) =
        let response = new HttpResponseMessage(HttpStatusCode.OK)
        response.Content <- new StringContent(json, Encoding.UTF8, "application/json")
        Task.FromResult(response)

let private scoreboardJson
    (eventId: string)
    (date: DateTime)
    (status: string)
    (period: int option)
    =
    let periodJson = period |> Option.map string |> Option.defaultValue "null"
    let completed = if status = "STATUS_FINAL" then "true" else "false"

    $"""
    {{
      "leagues": [
        {{
          "calendar": [
            {{
              "label": "Grand Prix of Nashville",
              "startDate": "{date:O}",
              "endDate": "{date.AddHours(3.0):O}",
              "event": {{
                "$ref": "https://sports.core.api.espn.com/v2/sports/racing/leagues/irl/events/{eventId}?lang=en&region=us"
              }}
            }}
          ]
        }}
      ],
      "events": [
        {{
          "id": "{eventId}",
          "date": "{date:O}",
          "name": "Grand Prix of Nashville",
          "competitions": [],
          "status": {{
            "period": {periodJson},
            "type": {{
              "name": "{status}",
              "state": "post",
              "completed": {completed}
            }}
          }}
        }}
      ]
    }}
    """

let private createProvider (databaseName: string) =
    let services = ServiceCollection()

    services.AddDbContext<ExcitementDbContext>(fun options ->
        options.UseInMemoryDatabase(databaseName) |> ignore)
    |> ignore

    services.BuildServiceProvider()

let private createService (provider: IServiceProvider) (json: string) =
    let httpClient = new HttpClient(new StaticJsonHandler(json))

    let espnService =
        EspnIndyCarService(
            httpClient,
            NullLogger<EspnIndyCarService>.Instance
        )

    httpClient,
    IndyCarDataService(
        espnService,
        provider,
        NullLogger<IndyCarDataService>.Instance
    )

[<Fact>]
let ``RefreshSchedule reconciles a changed ESPN event ID without replacing the race`` () =
    task {
        let oldEventId = "202607193999"
        let newEventId = "202607203999"
        let eventDate = DateTime(2026, 7, 20, 19, 0, 0, DateTimeKind.Utc)
        use provider = createProvider (Guid.NewGuid().ToString())

        do
            use scope = provider.CreateScope()
            let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()

            db.IndyCarRaces.Add(
                IndyCarRace(
                    Id = oldEventId,
                    EspnEventId = oldEventId,
                    RaceName = "Grand Prix of Nashville",
                    Year = 2026,
                    RaceNumber = 1,
                    ScheduledStart = DateTime(2026, 7, 20, 22, 0, 0, DateTimeKind.Utc),
                    Status = "scheduled",
                    TotalLaps = 0
                )
            )
            |> ignore

            db.IndyCarRaceResults.Add(
                IndyCarRaceResult(
                    RaceId = oldEventId,
                    EspnAthleteId = "5632",
                    DisplayName = "Alex Palou",
                    ShortName = "A. Palou",
                    FinishPosition = 1,
                    Winner = true
                )
            )
            |> ignore

            db.SaveChanges() |> ignore

        let json = scoreboardJson newEventId eventDate "STATUS_FINAL" (Some 225)
        let httpClient, service = createService provider json
        use _httpClient = httpClient

        let! created = service.RefreshScheduleAsync(2026)

        Assert.Equal(0, created)

        use verificationScope = provider.CreateScope()
        let db = verificationScope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
        let race = Assert.Single(db.IndyCarRaces)
        let result = Assert.Single(db.IndyCarRaceResults)

        Assert.Equal(oldEventId, race.Id)
        Assert.Equal(newEventId, race.EspnEventId)
        Assert.Equal("final", race.Status)
        Assert.Equal(eventDate, race.ScheduledStart)
        Assert.Equal(225, race.CompletedLaps.Value)
        Assert.Equal(225, race.TotalLaps)
        Assert.Equal(oldEventId, result.RaceId)
    }

[<Fact>]
let ``RefreshSchedule initializes both IDs for a new race`` () =
    task {
        let eventId = "new-espn-event"
        let eventDate = DateTime.UtcNow.AddDays(30.0)
        let year = eventDate.Year
        use provider = createProvider (Guid.NewGuid().ToString())
        let json = scoreboardJson eventId eventDate "STATUS_SCHEDULED" None
        let httpClient, service = createService provider json
        use _httpClient = httpClient

        let! created = service.RefreshScheduleAsync(year)

        Assert.Equal(1, created)

        use verificationScope = provider.CreateScope()
        let db = verificationScope.ServiceProvider.GetRequiredService<ExcitementDbContext>()
        let race = Assert.Single(db.IndyCarRaces)

        Assert.Equal(eventId, race.Id)
        Assert.Equal(eventId, race.EspnEventId)
        Assert.Equal(year, race.Year)
        Assert.Equal(1, race.RaceNumber)
    }
