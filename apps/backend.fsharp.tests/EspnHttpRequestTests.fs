module EspnHttpRequestTests

open System
open System.Net
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
open Xunit
open backend.Services
open backend.Services.Backfill
open backend.Services.Espn
open backend.Services.EspnF1
open backend.Services.EspnIndyCar
open backend.Services.EspnNascar
open backend.Services.Kalshi

type private CapturedRequest =
    { Uri: Uri
      UserAgent: string
      Accept: string }

type private CapturingHandler(statusCode: HttpStatusCode, json: string) =
    inherit HttpMessageHandler()

    let requests = ResizeArray<CapturedRequest>()

    member _.Requests = requests

    override _.SendAsync(request: HttpRequestMessage, _ct: CancellationToken) =
        requests.Add(
            { Uri = request.RequestUri
              UserAgent = request.Headers.UserAgent.ToString()
              Accept = request.Headers.Accept.ToString() }
        )

        let response = new HttpResponseMessage(statusCode)
        response.Content <- new StringContent(json, Encoding.UTF8, "application/json")
        Task.FromResult(response)

type private SequenceHandler(bodies: string list) =
    inherit HttpMessageHandler()

    let mutable remaining = bodies
    let mutable requests = 0

    member _.Requests = requests

    override _.SendAsync(_request: HttpRequestMessage, _ct: CancellationToken) =
        requests <- requests + 1
        let body = List.head remaining
        if not (List.isEmpty (List.tail remaining)) then remaining <- List.tail remaining
        let response = new HttpResponseMessage(HttpStatusCode.OK)
        response.Content <- new StringContent(body, Encoding.UTF8, "application/json")
        Task.FromResult(response)

type private RecordingLogger<'T>() =
    let levels = ResizeArray<LogLevel>()

    member _.Levels = levels

    interface ILogger<'T> with
        member _.BeginScope<'TState>(_state: 'TState) =
            { new IDisposable with
                member _.Dispose() = () }

        member _.IsEnabled(_logLevel: LogLevel) = true

        member _.Log<'TState>
            (logLevel: LogLevel,
             _eventId: EventId,
             _state: 'TState,
             _exception: exn,
             _formatter: Func<'TState, exn, string>) =
            levels.Add(logLevel)

let private createClient statusCode json =
    let handler = new CapturingHandler(statusCode, json)
    new HttpClient(handler), handler

let private assertSiteApiRequest (expectedPath: string) (expectedQuery: string) (request: CapturedRequest) =
    Assert.Equal("https", request.Uri.Scheme)
    Assert.Equal("site.web.api.espn.com", request.Uri.Host)
    Assert.Equal(expectedPath, request.Uri.AbsolutePath)
    Assert.Equal(expectedQuery, request.Uri.Query)
    Assert.Equal("", request.UserAgent)

[<Fact>]
let ``team sport scoreboards use the working ESPN site API origin`` () =
    task {
        let disposableClient, handler = createClient HttpStatusCode.OK "{}"
        use client = disposableClient

        let service =
            EspnOddsService(
                client,
                NullLogger<EspnOddsService>.Instance,
                Unchecked.defaultof<IServiceProvider>,
                Unchecked.defaultof<IGameUpdateService>,
                Unchecked.defaultof<KalshiOddsService>
            )

        let sports =
            [ "nba", "/apis/site/v2/sports/basketball/nba/scoreboard"
              "nfl", "/apis/site/v2/sports/football/nfl/scoreboard"
              "nhl", "/apis/site/v2/sports/hockey/nhl/scoreboard"
              "mlb", "/apis/site/v2/sports/baseball/mlb/scoreboard"
              "world_cup", "/apis/site/v2/sports/soccer/fifa.world/scoreboard" ]

        for sport, _ in sports do
            let! _ = service.FetchScoreboardAsync(sport, date = "20260823")
            ()

        Assert.Equal(sports.Length, handler.Requests.Count)

        for (sport, expectedPath), request in Seq.zip sports handler.Requests do
            Assert.False(String.IsNullOrWhiteSpace(sport))
            assertSiteApiRequest expectedPath "?dates=20260823" request
    }

[<Fact>]
let ``motorsport scoreboards use the working ESPN site API origin`` () =
    task {
        let disposableF1Client, f1Handler = createClient HttpStatusCode.OK "{}"
        let disposableNascarClient, nascarHandler = createClient HttpStatusCode.OK "{}"
        let disposableIndyCarClient, indyCarHandler = createClient HttpStatusCode.OK "{}"
        use f1Client = disposableF1Client
        use nascarClient = disposableNascarClient
        use indyCarClient = disposableIndyCarClient

        let! _ =
            EspnF1Service(f1Client, NullLogger<EspnF1Service>.Instance)
                .FetchScoreboardAsync(date = "20260823")

        let! _ =
            EspnNascarService(nascarClient, NullLogger<EspnNascarService>.Instance)
                .FetchScoreboardAsync(date = "20260823")

        let! _ =
            EspnIndyCarService(indyCarClient, NullLogger<EspnIndyCarService>.Instance)
                .FetchScoreboardAsync(date = "20260823")

        assertSiteApiRequest "/apis/site/v2/sports/racing/f1/scoreboard" "?dates=20260823" f1Handler.Requests[0]
        assertSiteApiRequest "/apis/site/v2/sports/racing/nascar-premier/scoreboard" "?dates=20260823" nascarHandler.Requests[0]
        assertSiteApiRequest "/apis/site/v2/sports/racing/irl/scoreboard" "?dates=20260823" indyCarHandler.Requests[0]
    }

[<Fact>]
let ``standings clients use the working ESPN site API origin`` () =
    task {
        let disposableClient, handler = createClient HttpStatusCode.OK "{\"children\":null}"
        use client = disposableClient

        let service =
            EspnStandingsService(
                client,
                NullLogger<EspnStandingsService>.Instance,
                Unchecked.defaultof<IServiceProvider>
            )

        let! _ = service.RefreshNbaStandingsAsync()
        let! _ = service.RefreshNflStandingsAsync()
        let! _ = service.RefreshNhlStandingsAsync()
        let! _ = service.RefreshMlbStandingsAsync()

        let expectedPaths =
            [ "/apis/v2/sports/basketball/nba/standings"
              "/apis/v2/sports/football/nfl/standings"
              "/apis/v2/sports/hockey/nhl/standings"
              "/apis/v2/sports/baseball/mlb/standings" ]

        Assert.Equal(expectedPaths.Length, handler.Requests.Count)

        for expectedPath, request in Seq.zip expectedPaths handler.Requests do
            Assert.StartsWith("?season=", request.Uri.Query)
            assertSiteApiRequest expectedPath request.Uri.Query request
    }

[<Fact>]
let ``power index clients use the shared ESPN site API origin`` () =
    task {
        let disposableClient, handler = createClient HttpStatusCode.InternalServerError "{}"
        use client = disposableClient

        let service =
            EspnPowerIndexService(
                client,
                NullLogger<EspnPowerIndexService>.Instance,
                Unchecked.defaultof<IServiceProvider>,
                { new TimeProvider() with override _.GetUtcNow() = DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero) }
            )

        let! _ = service.RefreshNbaRatingsAsync()
        let! _ = service.RefreshNflRatingsAsync()

        Assert.Equal(2, handler.Requests.Count)
        assertSiteApiRequest "/apis/fitt/v3/sports/basketball/nba/powerindex" handler.Requests[0].Uri.Query handler.Requests[0]
        assertSiteApiRequest "/apis/fitt/v3/sports/football/nfl/powerindex" handler.Requests[1].Uri.Query handler.Requests[1]
        Assert.Contains("page=1", handler.Requests[0].Uri.Query)
        Assert.Contains("limit=50", handler.Requests[0].Uri.Query)
    }

[<Fact>]
let ``NFL backfill scoreboards use the working ESPN site API origin`` () =
    task {
        let disposableClient, handler = createClient HttpStatusCode.OK "{}"
        use client = disposableClient

        let service =
            NflBackfillService(
                client,
                NullLogger<NflBackfillService>.Instance,
                Unchecked.defaultof<IServiceProvider>,
                Unchecked.defaultof<KalshiOddsService>
            )

        let! _ = service.FetchScoreboardByWeekAsync(2025, 2, 7)

        assertSiteApiRequest
            "/apis/site/v2/sports/football/nfl/scoreboard"
            "?seasontype=2&week=7&dates=2025"
            handler.Requests[0]
    }

[<Fact>]
let ``ESPN Core API requests retain their existing origin`` () =
    task {
        let disposableClient, handler = createClient HttpStatusCode.OK "{\"count\":0,\"items\":[]}"
        use client = disposableClient

        let! _ =
            EspnNascarService(client, NullLogger<EspnNascarService>.Instance)
                .FetchCompetitorsAsync("401999999")

        Assert.Single(handler.Requests) |> ignore
        Assert.Equal("sports.core.api.espn.com", handler.Requests[0].Uri.Host)
        Assert.Equal(
            "/v2/sports/racing/leagues/nascar-premier/events/401999999/competitions/401999999/competitors",
            handler.Requests[0].Uri.AbsolutePath
        )
    }

[<Fact>]
let ``non-ESPN clients retain their existing origin and headers`` () =
    task {
        let disposableClient, handler = createClient HttpStatusCode.OK "{\"markets\":[],\"cursor\":\"\"}"
        use client = disposableClient
        let service = KalshiOddsService(client, NullLogger<KalshiOddsService>.Instance)

        let! _ =
            service.GetLiveOddsAsync(
                "nba",
                "BOS",
                "NYK",
                DateTime(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc)
            )

        Assert.NotEmpty(handler.Requests)

        for request in handler.Requests do
            Assert.Equal("api.elections.kalshi.com", request.Uri.Host)
            Assert.Equal("application/json", request.Accept)
    }

let private scoreboardWith (bodies: string list) =
    let handler = new SequenceHandler(bodies)
    let logger = RecordingLogger<EspnOddsService>()

    let service =
        EspnOddsService(
            new HttpClient(handler),
            logger,
            Unchecked.defaultof<IServiceProvider>,
            Unchecked.defaultof<IGameUpdateService>,
            Unchecked.defaultof<KalshiOddsService>
        )

    task {
        let! result = service.FetchScoreboardAsync("nba", date = "20260823")
        return result, handler.Requests, logger.Levels
    }

[<Theory>]
[<InlineData("")>]
[<InlineData("<html><body>Service Unavailable</body></html>")>]
[<InlineData("{\"leagues\":[],\"events\":[{\"id\":\"9")>]
let ``one malformed ESPN scoreboard body is refetched without an error log`` (body: string) =
    task {
        let! result, requests, levels = scoreboardWith [ body; "{\"leagues\":[],\"events\":[]}" ]

        Assert.NotNull(box result)
        Assert.Empty(result.Events)
        Assert.Equal(2, requests)
        Assert.DoesNotContain(LogLevel.Error, levels)
        Assert.Contains(LogLevel.Warning, levels)
    }

[<Theory>]
[<InlineData("")>]
[<InlineData("<html><body>Service Unavailable</body></html>")>]
[<InlineData("{\"leagues\":[],\"events\":[{\"id\":\"9")>]
let ``repeated malformed ESPN scoreboard bodies are fetched twice and logged as warnings`` (body: string) =
    task {
        let! result, requests, levels = scoreboardWith [ body ]

        Assert.Null(box result)
        Assert.Equal(2, requests)
        Assert.DoesNotContain(LogLevel.Error, levels)
        Assert.Contains(LogLevel.Warning, levels)
    }

[<Fact>]
let ``forbidden ESPN responses are not retried and logged as warnings`` () =
    task {
        let disposableClient, handler = createClient HttpStatusCode.Forbidden "{}"
        use client = disposableClient
        let logger = RecordingLogger<EspnOddsService>()

        let service =
            EspnOddsService(
                client,
                logger,
                Unchecked.defaultof<IServiceProvider>,
                Unchecked.defaultof<IGameUpdateService>,
                Unchecked.defaultof<KalshiOddsService>
            )

        let! result = service.FetchScoreboardAsync("nba", date = "20260823")

        Assert.Null(box result)
        Assert.Single(handler.Requests) |> ignore
        Assert.DoesNotContain(LogLevel.Error, logger.Levels)
        Assert.Contains(LogLevel.Warning, logger.Levels)
    }
