using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using backend.Data;
using backend.Models;
using backend.Services.EspnNascar;
using backend.Services.Nascar;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;
using Testcontainers.PostgreSql;
using Xunit;

namespace backend.tests;

[Collection(nameof(SentryGlobalHubCollection))]
public sealed class NascarDriverRefreshTests
{
    private const int StandardAttempts = 4;
    private const string CycleFailure = "Error during NASCAR schedule refresh cycle";
    private const string ScoreboardFailure = "Failed to fetch ESPN NASCAR scoreboard from {Url}";
    private const string NoCalendar = "No NASCAR scoreboard data returned";
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Retry = TimeSpan.FromHours(1);

    private static readonly Competitor Avery = new("9101", "Avery Synthetic", "A. Synthetic", "1", "Chevrolet", "Synthetic Motorsports");
    private static readonly Competitor Blake = new("9102", "Blake Fixture", "B. Fixture", "2", "Ford", "Fixture Racing");
    private static readonly Competitor Casey = new("9103", "Casey Example", "C. Example", "3", "Toyota", "Example Team");

    [Fact]
    public async Task First_tick_stores_the_roster_from_the_latest_final_race()
    {
        await using var app = await NascarApp.StartAsync();
        var first = await app.SeedFinalRaceAsync("nascar-r1", 1, days: -10, Avery);
        var second = await app.SeedFinalRaceAsync("nascar-r2", 2, days: -3, Avery, Blake);

        await app.StartWorkerAsync();

        Assert.Equal([second.Date], app.DriverRequests);
        Assert.DoesNotContain(first.Date, app.DriverRequests);
        Assert.Equal([
            new Roster("9101", "Avery Synthetic", "Avery Synthetic", "A. Synthetic", "1", "Chevrolet", "Synthetic Motorsports", app.Year),
            new Roster("9102", "Blake Fixture", "Blake Fixture", "B. Fixture", "2", "Ford", "Fixture Racing", app.Year)
        ], await app.RosterAsync());
        Assert.Empty(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Stored_roster_stops_a_refetch_on_later_ticks_and_after_a_restart()
    {
        await using var app = await NascarApp.StartAsync();
        var race = await app.SeedFinalRaceAsync("nascar-r1", 1, days: -3, Avery, Blake);
        await app.StartWorkerAsync();
        Assert.Single(app.DriverRequests);
        var calendarRequests = app.CalendarRequests;

        await app.TickAsync(Poll);
        await app.TickAsync(TimeSpan.FromDays(1));
        Assert.Equal(calendarRequests + 2, app.CalendarRequests);
        Assert.Equal([race.Date], app.DriverRequests);

        await app.RestartAsync();
        await app.TickAsync(Poll);
        Assert.Equal([race.Date], app.DriverRequests);
        Assert.Equal(2, (await app.RosterAsync()).Count);
        Assert.Empty(await app.SentEventsAsync());
    }

    [Fact]
    public async Task New_final_race_refreshes_the_roster_and_keeps_earlier_drivers()
    {
        await using var app = await NascarApp.StartAsync();
        var first = await app.SeedFinalRaceAsync("nascar-r1", 1, days: -10, Avery, Blake);
        await app.StartWorkerAsync();

        var second = await app.SeedFinalRaceAsync("nascar-r2", 2, days: -3, Avery with { Car = "11", Team = "Renumbered Motorsports" });
        await app.TickAsync(Poll);

        Assert.Equal([first.Date, second.Date], app.DriverRequests);
        Assert.Equal([
            new Roster("9101", "Avery Synthetic", "Avery Synthetic", "A. Synthetic", "11", "Chevrolet", "Renumbered Motorsports", app.Year),
            new Roster("9102", "Blake Fixture", "Blake Fixture", "B. Fixture", "2", "Ford", "Fixture Racing", app.Year)
        ], await app.RosterAsync());

        await app.TickAsync(Poll);
        Assert.Equal(2, app.DriverRequests.Count);
        Assert.Empty(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Scoreboard_without_vehicles_takes_them_from_core_competitors_and_keeps_what_it_lacks()
    {
        await using var app = await NascarApp.StartAsync();
        await app.SeedFinalRaceAsync("nascar-r1", 1, days: -10, Avery with { Flag = "https://flags.invalid/a.png" }, Blake);
        await app.StartWorkerAsync();

        var second = await app.SeedFinalRaceWithoutVehiclesAsync("nascar-r2", 2, days: -3,
            field: [Avery, Blake, Casey],
            core: [Avery with { Car = "11", Team = "Renumbered Motorsports" }, Casey]);
        await app.TickAsync(Poll);

        Assert.Equal(["nascar-r2"], app.CoreRequests);
        Assert.Equal(second.Date, app.DriverRequests[^1]);
        Assert.Equal([
            new Roster("9101", "Avery Synthetic", "Avery Synthetic", "A. Synthetic", "11", "Chevrolet", "Renumbered Motorsports", app.Year, "https://flags.invalid/a.png"),
            new Roster("9102", "Blake Fixture", "Blake Fixture", "B. Fixture", "2", "Ford", "Fixture Racing", app.Year),
            new Roster("9103", "Casey Example", "Casey Example", "C. Example", "3", "Toyota", "Example Team", app.Year)
        ], await app.RosterAsync());

        await app.RestartAsync();
        await app.TickAsync(Poll);
        Assert.Equal(2, app.DriverRequests.Count);
        Assert.Empty(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Entrant_the_scoreboard_never_lists_is_not_refetched_hourly()
    {
        await using var app = await NascarApp.StartAsync();
        await app.SeedFinalRaceAsync("nascar-r1", 1, days: -3, results: [Avery, Blake], listed: [Avery]);

        await app.StartWorkerAsync();
        await app.TickAsync(Retry);
        await app.TickAsync(Retry);

        Assert.Single(app.DriverRequests);
        Assert.Equal(["9101"], (await app.RosterAsync()).Select(d => d.Athlete));
        Assert.Empty(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Duplicate_entrant_is_stored_once()
    {
        await using var app = await NascarApp.StartAsync();
        await app.SeedFinalRaceAsync("nascar-r1", 1, days: -3, results: [Avery, Blake], listed: [Avery, Avery, Blake]);

        await app.StartWorkerAsync();

        Assert.Equal(["9101", "9102"], (await app.RosterAsync()).Select(d => d.Athlete));
        Assert.Empty(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Before_the_first_final_race_the_roster_comes_from_the_race_starting_within_a_week()
    {
        await using var app = await NascarApp.StartAsync();
        var opener = await app.SeedScheduledRaceAsync("nascar-opener", 1, days: 2, Avery, Blake);
        await app.SeedScheduledRaceAsync("nascar-finale", 36, days: 200);

        await app.StartWorkerAsync();
        await app.TickAsync(Poll);

        Assert.Equal([opener.Date], app.DriverRequests);
        Assert.Equal(["9101", "9102"], (await app.RosterAsync()).Select(d => d.Athlete));

        await app.FinishAsync(opener, Avery, Blake, Casey);
        await app.TickAsync(Poll);

        Assert.Equal([opener.Date, opener.Date], app.DriverRequests);
        Assert.Equal(["9101", "9102", "9103"], (await app.RosterAsync()).Select(d => d.Athlete));
        Assert.Empty(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Canceled_or_distant_race_before_the_first_final_race_makes_no_driver_request()
    {
        await using var app = await NascarApp.StartAsync();
        await app.SeedCanceledRaceAsync("nascar-canceled", 1, days: 2);
        await app.SeedScheduledRaceAsync("nascar-opener", 2, days: 9, Avery);

        await app.StartWorkerAsync();
        await app.TickAsync(Poll);

        Assert.Empty(app.DriverRequests);
        Assert.Empty(await app.RosterAsync());
        Assert.Empty(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Provider_failure_is_sent_once_at_error_and_retried_after_an_hour()
    {
        await using var app = await NascarApp.StartAsync();
        var race = await app.SeedFinalRaceAsync("nascar-r1", 1, days: -3, Avery, Blake);
        app.Provider.Answers[race.Date] = new Reply(HttpStatusCode.ServiceUnavailable, "{}");

        await app.StartWorkerAsync();

        Assert.Equal(StandardAttempts, app.DriverRequests.Count);
        var reported = Assert.Single(await app.SentEventsAsync());
        Assert.Equal((typeof(EspnNascarService).FullName, "error", ScoreboardFailure), (reported.Logger, reported.Level, reported.Template));
        Assert.Contains(reported.Exceptions, e => e.Type == typeof(HttpRequestException).FullName);

        await app.TickAsync(Poll);
        await app.TickAsync(Retry - Poll - TimeSpan.FromSeconds(1));
        Assert.Equal(StandardAttempts, app.DriverRequests.Count);

        app.Provider.Answers[race.Date] = race.Answer;
        await app.TickAsync(Poll);

        Assert.Equal(StandardAttempts + 1, app.DriverRequests.Count);
        Assert.Equal(2, (await app.RosterAsync()).Count);
        Assert.Single(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Hung_driver_fetch_ends_at_the_total_timeout_is_sent_once_at_error_and_the_loop_continues()
    {
        await using var app = await NascarApp.StartAsync();
        var race = await app.SeedFinalRaceAsync("nascar-r1", 1, days: -3, Avery, Blake);
        app.Provider.Answers[race.Date] = new Hanging();

        await app.StartWorkerAsync(waitForTick: false);
        await app.Provider.HangStarted.WaitAsync(TimeSpan.FromSeconds(30));
        await app.AdvanceUntilTheLoopWaitsAsync();

        var reported = Assert.Single(await app.SentEventsAsync());
        Assert.Equal((typeof(EspnNascarService).FullName, "error", ScoreboardFailure), (reported.Logger, reported.Level, reported.Template));
        Assert.Equal("Polly.Timeout.TimeoutRejectedException", reported.Exceptions[^1].Type);
        Assert.Empty(await app.RosterAsync());
        var calendarRequests = app.CalendarRequests;
        var driverRequests = app.DriverRequests.Count;

        await app.TickAsync(Poll);
        Assert.Equal(calendarRequests + 1, app.CalendarRequests);
        Assert.Equal(driverRequests, app.DriverRequests.Count);

        app.Provider.Answers[race.Date] = race.Answer;
        await app.TickAsync(Retry);
        Assert.Equal(2, (await app.RosterAsync()).Count);
        Assert.Single(await app.SentEventsAsync());
    }

    public static TheoryData<HttpStatusCode, string> UnusableAnswers() => new()
    {
        { HttpStatusCode.NotFound, "{}" },
        { HttpStatusCode.OK, """{"events":[""" },
        { HttpStatusCode.OK, "{}" },
        { HttpStatusCode.OK, """{"events":[{"id":"nascar-r1"}]}""" },
        { HttpStatusCode.OK, """{"events":[{"id":"nascar-r1","competitions":[{}]}]}""" },
        { HttpStatusCode.OK, """{"events":[{"id":"nascar-r1","competitions":[{"competitors":[{"id":"9101"}]}]}]}""" },
        { HttpStatusCode.OK, """{"events":[{"id":"nascar-r1","competitions":[{"competitors":[{"id":"9101","athlete":{"displayName":"A","shortName":"A"}}]}]}]}""" },
        { HttpStatusCode.OK, """{"events":[{"id":"nascar-r1","competitions":[{"competitors":[{"id":"9101","athlete":{"fullName":"A","shortName":"A"}}]}]}]}""" },
        { HttpStatusCode.OK, """{"events":[{"id":"nascar-r1","competitions":[{"competitors":[{"id":"9101","athlete":{"fullName":"A","displayName":"A"}}]}]}]}""" },
    };

    [Theory]
    [MemberData(nameof(UnusableAnswers))]
    public async Task Missing_or_unreadable_scoreboard_is_a_warning_without_an_event_and_retried_after_an_hour(HttpStatusCode status, string body)
    {
        await using var app = await NascarApp.StartAsync();
        var race = await app.SeedFinalRaceAsync("nascar-r1", 1, days: -3, Avery, Blake);
        app.Provider.Answers[race.Date] = new Reply(status, body);

        await app.StartWorkerAsync();
        await app.TickAsync(Poll);

        Assert.Single(app.DriverRequests);
        Assert.Empty(await app.RosterAsync());
        Assert.DoesNotContain(app.Logs, l => l.Level >= LogLevel.Error);
        Assert.Contains(app.Logs, l => l.Level == LogLevel.Warning && l.Template != NoCalendar);
        Assert.Empty(await app.SentEventsAsync());

        app.Provider.Answers[race.Date] = race.Answer;
        await app.TickAsync(Retry);

        Assert.Equal(2, app.DriverRequests.Count);
        Assert.Equal(2, (await app.RosterAsync()).Count);
        Assert.Empty(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Own_fault_storing_the_roster_is_sent_once_at_error_and_retried_after_an_hour()
    {
        var fault = new FaultOnce("INSERT INTO nascar_drivers");
        await using var app = await NascarApp.StartAsync(configureDb: db => db.AddInterceptors(fault));
        await app.SeedFinalRaceAsync("nascar-r1", 1, days: -3, Avery, Blake);

        await app.StartWorkerAsync();

        var reported = Assert.Single(await app.SentEventsAsync());
        Assert.Equal((typeof(NascarBackgroundService).FullName, "error", CycleFailure), (reported.Logger, reported.Level, reported.Template));
        Assert.Contains(reported.Exceptions, e => e.Type == typeof(InvalidOperationException).FullName);
        Assert.Empty(await app.RosterAsync());
        var calendarRequests = app.CalendarRequests;

        await app.TickAsync(Poll);
        Assert.Equal(calendarRequests + 1, app.CalendarRequests);
        Assert.Single(app.DriverRequests);

        await app.TickAsync(Retry);
        Assert.Equal(2, app.DriverRequests.Count);
        Assert.Equal(2, (await app.RosterAsync()).Count);
        Assert.Single(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Shutdown_during_the_driver_fetch_is_quiet()
    {
        await using var app = await NascarApp.StartAsync();
        var race = await app.SeedFinalRaceAsync("nascar-r1", 1, days: -3, Avery, Blake);
        app.Provider.Answers[race.Date] = new Hanging();

        await app.StartWorkerAsync(waitForTick: false);
        await app.Provider.HangStarted.WaitAsync(TimeSpan.FromSeconds(30));
        await app.StopWorkerAsync();

        Assert.True(app.Worker.ExecuteTask!.IsCompleted);
        Assert.False(app.Worker.ExecuteTask.IsFaulted);
        Assert.Single(app.DriverRequests);
        Assert.All(app.Logs.Where(l => l.Level >= LogLevel.Warning), l => Assert.Equal(NoCalendar, l.Template));
        Assert.Empty(await app.RosterAsync());
        Assert.Empty(await app.SentEventsAsync());
    }

    private sealed record Competitor(string Athlete, string Name, string ShortName, string Car, string Manufacturer, string Team, string? Flag = null);

    private sealed record Roster(string Athlete, string FullName, string DisplayName, string ShortName, string? Car, string? Manufacturer, string? Team, int Season, string? Flag = null);

    private sealed record SeededRace(string Id, string Date, Answer Answer);

    private abstract record Answer;

    private sealed record Reply(HttpStatusCode Status, string Body) : Answer;

    private sealed record Hanging : Answer;

    private sealed record SentEvent(string? Logger, string? Level, string? Template, (string? Type, string? Value)[] Exceptions);

    private sealed record LoggedEntry(string Category, LogLevel Level, string? Template, Exception? Exception);

    private sealed class NascarApp : IAsyncDisposable
    {
        private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        private readonly PostgreSqlContainer database = IsolatedPostgreSql.CreateContainer();
        private readonly AppFactory entryPoint = new();
        private readonly SentTransport transport = new();
        private readonly LogRecorder logs = new();
        private readonly List<NascarBackgroundService> workers = [];
        private readonly Action<DbContextOptionsBuilder>? configureDb;
        private WebApplicationFactory<Program> app = null!;

        private NascarApp(Action<DbContextOptionsBuilder>? configureDb) => this.configureDb = configureDb;

        public LoopClock Clock { get; } = new(new DateTimeOffset(DateTime.UtcNow.Date.AddHours(12), TimeSpan.Zero));

        public SyntheticEspn Provider { get; } = new();

        public int Year => Clock.GetUtcNow().Year;

        public IEnumerable<LoggedEntry> Logs => logs.Entries;

        public NascarBackgroundService Worker => workers[^1];

        public List<string> CoreRequests => Provider.Requests
            .Where(uri => uri.Host == "sports.core.api.espn.com").Select(uri => uri.Segments[^4].TrimEnd('/')).ToList();

        public List<string> DriverRequests => Provider.Requests
            .Where(uri => uri.Host == "site.web.api.espn.com" && uri.Query.StartsWith("?dates=", StringComparison.Ordinal))
            .Select(uri => uri.Query["?dates=".Length..]).ToList();

        public int CalendarRequests => Provider.Requests.Count(uri => uri.Host == "site.web.api.espn.com" && uri.Query == "");

        public static async Task<NascarApp> StartAsync(Action<DbContextOptionsBuilder>? configureDb = null)
        {
            var nascar = new NascarApp(configureDb);
            await nascar.database.StartAsync();
            nascar.app = nascar.CreateFactory();
            using var scope = nascar.app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Database.EnsureCreatedAsync();
            return nascar;
        }

        public Task<SeededRace> SeedFinalRaceAsync(string id, int number, int days, params Competitor[] field) =>
            SeedFinalRaceAsync(id, number, days, results: field, listed: field);

        public async Task<SeededRace> SeedFinalRaceAsync(string id, int number, int days, Competitor[] results, Competitor[] listed)
        {
            var race = Race(id, number, days, "final");
            await SaveAsync([race, .. results.Select(c => Result(id, c))]);
            return Serve(race, "STATUS_FINAL", listed);
        }

        public async Task<SeededRace> SeedFinalRaceWithoutVehiclesAsync(string id, int number, int days, Competitor[] field, Competitor[] core)
        {
            var race = Race(id, number, days, "final");
            await SaveAsync([race, .. field.Select(c => Result(id, c with { Car = null! }))]);
            Provider.CoreAnswers[id] = JsonSerializer.Serialize(new
            {
                count = core.Length,
                items = core.Select(c => new
                {
                    id = c.Athlete,
                    athlete = new { id = c.Athlete },
                    vehicle = new { number = c.Car, manufacturer = c.Manufacturer, team = c.Team }
                })
            });
            return Serve(race, "STATUS_FINAL", field, withVehicles: false);
        }

        public async Task<SeededRace> SeedScheduledRaceAsync(string id, int number, int days, params Competitor[] field)
        {
            var race = Race(id, number, days, "scheduled");
            await SaveAsync([race]);
            return Serve(race, "STATUS_SCHEDULED", field);
        }

        public async Task SeedCanceledRaceAsync(string id, int number, int days) =>
            await SaveAsync([Race(id, number, days, "canceled")]);

        public async Task FinishAsync(SeededRace seeded, params Competitor[] field)
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            var race = await db.NascarRaces.SingleAsync(r => r.Id == seeded.Id);
            race.Status = "final";
            db.NascarRaceResults.AddRange(field.Select(c => Result(seeded.Id, c)));
            await db.SaveChangesAsync();
            Serve(race, "STATUS_FINAL", field);
        }

        public async Task StartWorkerAsync(bool waitForTick = true)
        {
            var worker = ActivatorUtilities.CreateInstance<NascarBackgroundService>(app.Services);
            workers.Add(worker);
            await worker.StartAsync(CancellationToken.None);
            await TickAsync(TimeSpan.FromSeconds(10), waitForTick);
        }

        public async Task StopWorkerAsync() => await Worker.StopAsync(CancellationToken.None);

        public async Task RestartAsync()
        {
            await StopWorkersAsync();
            await app.DisposeAsync();
            app = CreateFactory();
            await StartWorkerAsync();
        }

        public async Task TickAsync(TimeSpan advance, bool waitForTick = true)
        {
            await NextWaitAsync();
            Clock.Advance(advance);
            if (waitForTick) await Clock.LoopWaits.Reader.WaitToReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));
        }

        public async Task AdvanceUntilTheLoopWaitsAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!Clock.LoopWaits.Reader.TryPeek(out _))
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("the NASCAR loop never waited again");
                Clock.Advance(TimeSpan.FromMilliseconds(500));
                await Task.Delay(5);
            }
        }

        public async Task<List<Roster>> RosterAsync()
        {
            using var scope = app.Services.CreateScope();
            return (await scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().NascarDrivers.AsNoTracking()
                    .OrderBy(d => d.EspnAthleteId).ToListAsync())
                .Select(d => new Roster(d.EspnAthleteId, d.FullName, d.DisplayName, d.ShortName, d.CarNumber, d.Manufacturer, d.TeamName, d.Season, d.CountryFlagHref))
                .ToList();
        }

        public async Task<IReadOnlyCollection<SentEvent>> SentEventsAsync()
        {
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));
            return transport.Events.ToArray();
        }

        private async Task NextWaitAsync() =>
            await Clock.LoopWaits.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));

        private NascarRace Race(string id, int number, int days, string status) => new()
        {
            Id = id, RaceName = id, Year = Year, RaceNumber = number, Status = status,
            ScheduledStart = Clock.GetUtcNow().UtcDateTime.Date.AddDays(days).AddHours(17)
        };

        private static NascarRaceResult Result(string raceId, Competitor c) => new()
        {
            RaceId = raceId, EspnAthleteId = c.Athlete, DisplayName = c.Name, ShortName = c.ShortName, CarNumber = c.Car
        };

        private async Task SaveAsync(IEnumerable<object> rows)
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            db.AddRange(rows);
            await db.SaveChangesAsync();
        }

        private SeededRace Serve(NascarRace race, string status, Competitor[] field, bool withVehicles = true)
        {
            var date = TimeZoneInfo.ConvertTimeFromUtc(race.ScheduledStart, Eastern).ToString("yyyyMMdd");
            var scoreboard = new
            {
                events = new[]
                {
                    new
                    {
                        id = race.Id,
                        date = race.ScheduledStart.ToString("yyyy-MM-ddTHH:mmZ"),
                        name = race.RaceName,
                        competitions = new[]
                        {
                            new
                            {
                                competitors = field.Select((c, i) => new
                                {
                                    id = c.Athlete,
                                    order = i + 1,
                                    winner = i == 0,
                                    athlete = new
                                    {
                                        fullName = c.Name, displayName = c.Name, shortName = c.ShortName,
                                        flag = c.Flag is null ? null : new { href = c.Flag }
                                    },
                                    vehicle = withVehicles ? new { number = c.Car, manufacturer = c.Manufacturer, team = c.Team } : null
                                })
                            }
                        },
                        status = new { period = 200, type = new { name = status, state = "post", completed = status == "STATUS_FINAL" } }
                    }
                }
            };
            var answer = new Reply(HttpStatusCode.OK, JsonSerializer.Serialize(scoreboard));
            Provider.Answers[date] = answer;
            return new SeededRace(race.Id, date, answer);
        }

        private WebApplicationFactory<Program> CreateFactory() => entryPoint.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDbContextOptionsConfiguration<ExcitementDbContext>>();
                services.RemoveAll<DbContextOptions<ExcitementDbContext>>();
                services.AddDbContext<ExcitementDbContext>(options =>
                {
                    options.UseNpgsql(database.GetConnectionString());
                    configureDb?.Invoke(options);
                });
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);
                services.ConfigureAll<HttpClientFactoryOptions>(options =>
                    options.HttpMessageHandlerBuilderActions.Add(handlers => handlers.PrimaryHandler = Provider));
                services.ConfigureAll<HttpStandardResilienceOptions>(options => options.Retry.Delay = TimeSpan.Zero);
                services.AddSingleton<ILoggerProvider>(logs);
                services.PostConfigure<SentryAspNetCoreOptions>(options =>
                {
                    options.Dsn = "https://public@sentry.invalid/1";
                    options.Transport = transport;
                });
            });
        });

        private async Task StopWorkersAsync()
        {
            foreach (var worker in workers)
            {
                await worker.StopAsync(CancellationToken.None);
                worker.Dispose();
            }
            workers.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            await StopWorkersAsync();
            if (app is not null) await app.DisposeAsync();
            await entryPoint.DisposeAsync();
            Provider.Dispose();
            await database.DisposeAsync();
        }
    }

    private sealed class LoopClock(DateTimeOffset now) : FakeTimeProvider(now)
    {
        public Channel<TimeSpan> LoopWaits { get; } = Channel.CreateUnbounded<TimeSpan>();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            if (state is not CancellationTokenSource && dueTime > TimeSpan.Zero) LoopWaits.Writer.TryWrite(dueTime);
            return timer;
        }
    }

    private sealed class SyntheticEspn : HttpMessageHandler
    {
        private const string NoCompetitors = """{"count":0,"items":[]}""";
        private readonly TaskCompletionSource hanging = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<Uri> Requests { get; } = new();

        public ConcurrentDictionary<string, Answer> Answers { get; } = new();

        public ConcurrentDictionary<string, string> CoreAnswers { get; } = new();

        public Task HangStarted => hanging.Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Enqueue(uri);
            var answer = uri switch
            {
                { Host: "site.web.api.espn.com", Query: "" } => new Reply(HttpStatusCode.OK, """{"leagues":[]}"""),
                { Host: "site.web.api.espn.com" } when uri.Query.StartsWith("?dates=", StringComparison.Ordinal) =>
                    Answers.GetValueOrDefault(uri.Query["?dates=".Length..], new Reply(HttpStatusCode.OK, """{"events":[]}""")),
                { Host: "sports.core.api.espn.com" } when uri.AbsolutePath.EndsWith("/competitors", StringComparison.Ordinal) =>
                    new Reply(HttpStatusCode.OK, CoreAnswers.GetValueOrDefault(uri.Segments[^4].TrimEnd('/'), NoCompetitors)),
                _ => throw new InvalidOperationException("Unexpected offline request: " + uri)
            };
            switch (answer)
            {
                case Reply reply:
                    return new HttpResponseMessage(reply.Status) { Content = new StringContent(reply.Body) };
                default:
                    hanging.TrySetResult();
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    throw new UnreachableException();
            }
        }
    }

    private sealed class FaultOnce(string command) : DbCommandInterceptor
    {
        private int faulted;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand cmd, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) =>
            cmd.CommandText.Contains(command, StringComparison.Ordinal) && Interlocked.Exchange(ref faulted, 1) == 0
                ? throw new InvalidOperationException("Synthetic own-code fault storing the NASCAR roster")
                : ValueTask.FromResult(result);
    }

    private sealed class LogRecorder : ILoggerProvider
    {
        public ConcurrentQueue<LoggedEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);

        public void Dispose() { }

        private sealed class Logger(string category, ConcurrentQueue<LoggedEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!category.StartsWith("backend.", StringComparison.Ordinal)) return;
                var template = state is IReadOnlyList<KeyValuePair<string, object?>> values
                    ? values.FirstOrDefault(v => v.Key == "{OriginalFormat}").Value as string
                    : null;
                entries.Enqueue(new LoggedEntry(category, logLevel, template, exception));
            }
        }
    }

    private sealed class SentTransport : ITransport
    {
        public ConcurrentQueue<SentEvent> Events { get; } = new();

        public async Task SendEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken = default)
        {
            using var stream = new MemoryStream();
            await envelope.SerializeAsync(stream, null, cancellationToken);
            var lines = System.Text.Encoding.UTF8.GetString(stream.ToArray()).Split('\n');
            for (var i = 1; i + 1 < lines.Length; i++)
            {
                using var header = JsonDocument.Parse(lines[i]);
                if (header.RootElement.GetProperty("type").GetString() != "event") continue;
                using var payload = JsonDocument.Parse(lines[++i]);
                var root = payload.RootElement;
                var exceptions = root.TryGetProperty("exception", out var exception)
                    ? exception.GetProperty("values").EnumerateArray().Select(e => (Text(e, "type"), Text(e, "value"))).ToArray()
                    : [];
                var template = root.TryGetProperty("logentry", out var logEntry) ? Text(logEntry, "message") : null;
                Events.Enqueue(new SentEvent(Text(root, "logger"), Text(root, "level"), template, exceptions));
            }
        }

        private static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) ? value.GetString() : null;
    }
}
