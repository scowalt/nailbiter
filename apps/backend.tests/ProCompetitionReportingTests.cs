using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using backend.Data;
using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;
using Testcontainers.PostgreSql;
using Xunit;

namespace backend.tests;

[Collection(nameof(SentryGlobalHubCollection))]
public sealed class ProCompetitionReportingTests
{
    private const string GroupTemplate = "Pro context request failed for {Sport}/{Season}; previous timestamps preserved";
    private const string ForecastTemplate = "NFL conditional forecast request failed for {GameId}; previous evidence clocks preserved";
    private const string RejectedTemplate = "Rejected NFL conditional forecast observation for {GameId}";
    private const string PreviousEvidence = """{"GameId":"previous","Reason":"previous observation"}""";
    private static readonly DateTimeOffset GroupNow = new(2026, 11, 7, 0, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ForecastNow = new(2026, 10, 4, 19, 56, 0, TimeSpan.Zero);
    private static readonly DateTime PreviousForecast = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    private static readonly Game Basketball = new()
    {
        Id = "401900101", Sport = "nba", Status = "scheduled", SeasonYear = 2027, SeasonType = 2,
        HomeTeam = "Boston Celtics", AwayTeam = "New York Knicks", HomeTeamAbbreviation = "BOS", AwayTeamAbbreviation = "NY",
        ScheduledStart = GroupNow.UtcDateTime.AddDays(1), ProEvidenceJson = PreviousEvidence
    };

    private static readonly Game Hockey = new()
    {
        Id = "401900102", Sport = "nhl", Status = "scheduled", SeasonYear = 2027, SeasonType = 2,
        HomeTeam = "Boston Bruins", AwayTeam = "Toronto Maple Leafs", HomeTeamAbbreviation = "BOS", AwayTeamAbbreviation = "TOR",
        ScheduledStart = GroupNow.UtcDateTime.AddDays(1).AddHours(1), ProEvidenceJson = PreviousEvidence
    };

    [Fact]
    public async Task Provider_timeout_in_one_group_is_sent_once_and_the_next_group_still_refreshes()
    {
        await using var app = await GroupAppAsync(Hang, services => services.ConfigureAll<HttpStandardResilienceOptions>(options =>
            options.AttemptTimeout.Timeout = TimeSpan.FromMilliseconds(500)));

        await app.RefreshAsync(CancellationToken.None);

        await AssertGroupFailureSentOnceAsync(app, typeof(TimeoutRejectedException));
    }

    [Fact]
    public async Task Open_circuit_is_sent_once_per_group_without_ending_the_cycle()
    {
        await using var app = await GroupAppAsync(Unreachable, services => services.ConfigureAll<HttpStandardResilienceOptions>(options =>
            options.CircuitBreaker.MinimumThroughput = 4), failHockey: true);

        await app.RefreshAsync(CancellationToken.None);

        var sent = await app.SentEventsAsync();
        Assert.Equal(2, sent.Count);
        Assert.All(sent, e => Assert.Equal((typeof(ProCompetitionEvidenceService).FullName, "error", GroupTemplate), (e.Logger, e.Level, e.Template)));
        Assert.Single(sent, e => e.ExceptionTypes.Contains(typeof(BrokenCircuitException).FullName));
        Assert.Single(sent, e => e.ExceptionTypes[^1] == typeof(HttpRequestException).FullName);
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(KeyNotFoundException))]
    public async Task Own_fault_in_one_group_is_sent_once_and_the_next_group_still_refreshes(Type fault)
    {
        await using var app = await GroupAppAsync((_, _) => throw (Exception)Activator.CreateInstance(fault, "synthetic fault below the fetch")!);

        await app.RefreshAsync(CancellationToken.None);

        await AssertGroupFailureSentOnceAsync(app, fault);
    }

    [Fact]
    public async Task Client_deadline_in_one_group_is_sent_once_and_the_next_group_still_refreshes()
    {
        await using var app = await GroupAppAsync(Hang, services => services.ConfigureAll<HttpClientFactoryOptions>(options =>
            options.HttpClientActions.Add(client => client.Timeout = TimeSpan.FromSeconds(2))));

        await app.RefreshAsync(CancellationToken.None);

        await AssertGroupFailureSentOnceAsync(app, typeof(TaskCanceledException));
    }

    [Fact]
    public async Task Missing_espn_resource_is_a_warning_without_an_event()
    {
        await using var app = await GroupAppAsync((_, _) => Task.FromResult(Status(HttpStatusCode.NotFound)));

        await app.RefreshAsync(CancellationToken.None);

        var failure = Assert.Single(app.Logs, l => l.Category == typeof(ProCompetitionEvidenceService).FullName && l.Exception is not null);
        Assert.Equal(LogLevel.Warning, failure.Level);
        Assert.Equal(HttpStatusCode.NotFound, Assert.IsType<HttpRequestException>(failure.Exception).StatusCode);
        Assert.Empty(await app.SentEventsAsync());
        Assert.Equal(PreviousEvidence, await app.EvidenceAsync(Basketball.Id));
        Assert.NotEqual(PreviousEvidence, await app.EvidenceAsync(Hockey.Id));
    }

    [Fact]
    public async Task Unparseable_espn_body_is_read_as_missing_without_an_event()
    {
        await using var app = await GroupAppAsync((_, _) => Task.FromResult(Ok("""{"sports":[""")));

        await app.RefreshAsync(CancellationToken.None);

        Assert.DoesNotContain(app.Logs, l => l.Category == typeof(ProCompetitionEvidenceService).FullName && l.Exception is not null);
        Assert.Empty(await app.SentEventsAsync());
        Assert.NotEqual(PreviousEvidence, await app.EvidenceAsync(Basketball.Id));
        Assert.NotEqual(PreviousEvidence, await app.EvidenceAsync(Hockey.Id));
    }

    [Fact]
    public async Task Duplicate_espn_event_is_a_warning_without_an_event()
    {
        var duplicate = $$"""{"events":[{"id":"{{Basketball.Id}}"},{"id":"{{Basketball.Id}}"}]}""";
        await using var app = await GroupAppAsync((request, _) => Task.FromResult(Ok(
            request.RequestUri!.AbsolutePath.EndsWith("/scoreboard", StringComparison.Ordinal) ? duplicate : """{"events":[]}""")));

        await app.RefreshAsync(CancellationToken.None);

        var failure = Assert.Single(app.Logs, l => l.Category == typeof(ProCompetitionEvidenceService).FullName && l.Exception is not null);
        Assert.Equal(LogLevel.Warning, failure.Level);
        Assert.IsType<JsonException>(failure.Exception);
        Assert.Empty(await app.SentEventsAsync());
        Assert.Equal(PreviousEvidence, await app.EvidenceAsync(Basketball.Id));
        Assert.NotEqual(PreviousEvidence, await app.EvidenceAsync(Hockey.Id));
    }

    [Fact]
    public async Task Caller_cancellation_mid_refresh_propagates_without_an_event()
    {
        await using var app = await GroupAppAsync(Hang);
        using var caller = new CancellationTokenSource();

        var refresh = app.RefreshAsync(caller.Token);
        await app.Provider.FirstRequest.WaitAsync(TimeSpan.FromSeconds(30));
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        Assert.DoesNotContain(app.Logs, l => l.Exception is not null && l.Category.StartsWith("backend.", StringComparison.Ordinal));
        Assert.Empty(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Fault_raised_after_caller_cancellation_propagates_without_an_event()
    {
        using var caller = new CancellationTokenSource();
        await using var app = await GroupAppAsync((_, _) =>
        {
            caller.Cancel();
            throw new ObjectDisposedException("synthetic service disposed during shutdown");
        });

        await Assert.ThrowsAnyAsync<Exception>(() => app.RefreshAsync(caller.Token));

        Assert.DoesNotContain(app.Logs, l => l.Exception is not null && l.Category.StartsWith("backend.", StringComparison.Ordinal));
        Assert.Empty(await app.SentEventsAsync());
        Assert.Equal(PreviousEvidence, await app.EvidenceAsync(Basketball.Id));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "System.Net.Http.HttpRequestException")]
    [InlineData(HttpStatusCode.TooManyRequests, "backend.Services.NflPlayoffLeverageCollector+DeniedException")]
    public async Task Forecast_provider_failure_is_sent_once_by_the_collector_for_every_game_it_affects(HttpStatusCode status, string exception)
    {
        await using var app = await ForecastAppAsync((_, _) => Task.FromResult(Status(status)), games: 2);

        await app.RefreshAsync(CancellationToken.None);

        await AssertForecastFailureSentOnceAsync(app, exception);
    }

    [Fact]
    public async Task Forecast_client_deadline_is_sent_once_by_the_collector()
    {
        await using var app = await ForecastAppAsync(Hang, games: 2, configure: services =>
            services.Configure<HttpClientFactoryOptions>(nameof(NflPlayoffLeverageCollector), options =>
                options.HttpClientActions.Add(client => client.Timeout = TimeSpan.FromSeconds(2))));

        await app.RefreshAsync(CancellationToken.None);

        await AssertForecastFailureSentOnceAsync(app, typeof(TaskCanceledException).FullName!);
    }

    [Fact]
    public async Task Own_fault_below_the_forecast_request_is_sent_once_and_rejects_each_observation()
    {
        await using var app = await ForecastAppAsync((_, _) => throw new InvalidOperationException("synthetic fault below the fetch"), games: 2);

        await app.RefreshAsync(CancellationToken.None);

        var reported = Assert.Single(await app.SentEventsAsync());
        Assert.Equal((typeof(NflPlayoffLeverageCollector).FullName, "error", RejectedTemplate), (reported.Logger, reported.Level, reported.Template));
        Assert.Contains(typeof(InvalidOperationException).FullName, reported.ExceptionTypes);
        Assert.Single(app.Logs, l => l.Level >= LogLevel.Warning && l.Category.StartsWith("backend.", StringComparison.Ordinal));
        Assert.Single(app.Provider.Requests, IsForecastRequest);
        for (var i = 0; i < 2; i++)
        {
            var rejected = ProGameEvidence.Parse(await app.EvidenceAsync(ForecastGameId(i)))!.NflPlayoffLeverage!;
            Assert.False(rejected.ScheduleVerified);
            Assert.Equal("Malformed or contradictory NFL source/schedule observation", rejected.Reason);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Forecast_refusal_is_a_warning_without_an_event_and_stops_the_batch(HttpStatusCode status)
    {
        await using var app = await ForecastAppAsync((_, _) => Task.FromResult(Status(status)), games: 2);

        await app.RefreshAsync(CancellationToken.None);

        var refusal = Assert.Single(app.Logs, l => l.Category == typeof(NflPlayoffLeverageCollector).FullName && l.Exception is not null);
        Assert.Equal(LogLevel.Warning, refusal.Level);
        Assert.Single(app.Provider.Requests, IsForecastRequest);
        Assert.Empty(await app.SentEventsAsync());
        await AssertPreviousForecastsKeptAsync(app, games: 2);
    }

    [Fact]
    public async Task Malformed_espn_schedule_in_a_forecast_is_a_warning_without_an_event()
    {
        var source = new NflPlayoffLeverageTests.Handler();
        using var invoker = new HttpMessageInvoker(source, disposeHandler: false);
        await using var app = await ReportingApp.StartAsync((request, ct) => invoker.SendAsync(request, ct),
            services => PinClock(services, new DateTimeOffset(NflPlayoffLeverageTests.Now)));
        var game = NflPlayoffLeverageTests.Game();
        var schedules = NflPlayoffLeverageTests.Schedules();
        var broken = schedules[0].Events[0];
        var copy = JsonNode.Parse(broken.GetRawText())!;
        copy["competitions"]![0]!["competitors"]![0]!.AsObject().Remove("homeAway");
        schedules[0] = schedules[0] with { Events = [JsonSerializer.SerializeToElement(copy), .. schedules[0].Events.Skip(1)] };

        var result = await app.Services.GetRequiredService<NflPlayoffLeverageCollector>().CollectAsync([game], schedules, CancellationToken.None);

        var rejected = result[game.Id]!;
        Assert.False(rejected.ScheduleVerified);
        Assert.Equal("Malformed or contradictory NFL source/schedule observation", rejected.Reason);
        var warning = Assert.Single(app.Logs, l => l.Category == typeof(NflPlayoffLeverageCollector).FullName && l.Exception is not null);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.IsType<KeyNotFoundException>(Assert.IsType<JsonException>(warning.Exception).InnerException);
        Assert.Empty(await app.SentEventsAsync());
    }

    [Theory]
    [InlineData("<td>\u0661\u0668\u201313</td><td><a href=\"#commanders\">WSH</a></td><td>Sun Sep 6, 1:00 pm</td>")]
    [InlineData("<td>18\u2013\u0661\u0663</td><td><a href=\"#commanders\">WSH</a></td><td>Sun Sep 6, 1:00 pm</td>")]
    [InlineData("<td>18\u201313</td><td><a href=\"#commanders\">WSH</a></td><td>Sun Sep \u0666, 1:00 pm</td>")]
    [InlineData("<td>18\u201313</td><td><a href=\"#commanders\">WSH</a></td><td>Sun Sep 6, \u0661:00 pm</td>")]
    public async Task Schedule_row_in_non_ascii_digits_rejects_the_source_schedule_without_an_event(string row)
    {
        const string original = "<td>18\u201313</td><td><a href=\"#commanders\">WSH</a></td><td>Sun Sep 6, 1:00 pm</td>";
        var source = new NflPlayoffLeverageTests.Handler
        {
            Transform = (path, body) =>
            {
                if (!path.EndsWith("nflschedule.html", StringComparison.Ordinal)) return body;
                Assert.Contains(original, body, StringComparison.Ordinal);
                return body.Replace(original, row, StringComparison.Ordinal);
            }
        };
        using var invoker = new HttpMessageInvoker(source, disposeHandler: false);
        await using var app = await ReportingApp.StartAsync((request, ct) => invoker.SendAsync(request, ct),
            services => PinClock(services, new DateTimeOffset(NflPlayoffLeverageTests.Now)));
        var game = NflPlayoffLeverageTests.Game();

        var result = await app.Services.GetRequiredService<NflPlayoffLeverageCollector>()
            .CollectAsync([game], NflPlayoffLeverageTests.Schedules(), CancellationToken.None);

        Assert.Empty(await app.SentEventsAsync());
        Assert.DoesNotContain(app.Logs, l => l.Level >= LogLevel.Warning && l.Category.StartsWith("backend.", StringComparison.Ordinal));
        var rejected = result[game.Id]!;
        Assert.False(rejected.ScheduleVerified);
        Assert.Equal("NFL source schedule, season or publication unavailable", rejected.Reason);
    }

    [Fact]
    public async Task Malformed_espn_event_behind_a_kickoff_difference_is_a_warning_without_an_event()
    {
        var source = new NflPlayoffLeverageTests.Handler();
        using var invoker = new HttpMessageInvoker(source, disposeHandler: false);
        await using var app = await ReportingApp.StartAsync((request, ct) => invoker.SendAsync(request, ct),
            services => PinClock(services, new DateTimeOffset(NflPlayoffLeverageTests.Now)));
        var game = NflCorroboratedKickoffTests.Game();
        var schedules = NflCorroboratedKickoffTests.Schedules(game).Select(s => s with { Events = s.Events.Select(e =>
        {
            if (e.GetProperty("id").GetString() != game.Id) return e;
            var copy = JsonNode.Parse(e.GetRawText())!;
            copy.AsObject().Remove("season");
            return JsonSerializer.SerializeToElement(copy);
        }).ToArray() }).ToArray();

        var result = await app.Services.GetRequiredService<NflPlayoffLeverageCollector>().CollectAsync([game], schedules, CancellationToken.None);

        Assert.Empty(await app.SentEventsAsync());
        var warning = Assert.Single(app.Logs, l => l.Category == typeof(NflPlayoffLeverageCollector).FullName && l.Exception is not null);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.IsType<KeyNotFoundException>(Assert.IsType<JsonException>(warning.Exception).InnerException);
        var rejected = result[game.Id]!;
        Assert.False(rejected.ScheduleVerified);
        Assert.Equal("Malformed or contradictory NFL source/schedule observation", rejected.Reason);
    }

    [Fact]
    public async Task Caller_cancellation_mid_forecast_propagates_without_an_event()
    {
        await using var app = await ForecastAppAsync(Hang, games: 1);
        using var caller = new CancellationTokenSource();

        var refresh = app.RefreshAsync(caller.Token);
        await app.Provider.FirstForecastRequest.WaitAsync(TimeSpan.FromSeconds(30));
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        Assert.DoesNotContain(app.Logs, l => l.Exception is not null && l.Category.StartsWith("backend.", StringComparison.Ordinal));
        Assert.Empty(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Fault_raised_after_caller_cancellation_mid_forecast_propagates_without_an_event()
    {
        using var caller = new CancellationTokenSource();
        await using var app = await ForecastAppAsync((_, _) =>
        {
            caller.Cancel();
            throw new ObjectDisposedException("synthetic service disposed during shutdown");
        }, games: 1);

        await Assert.ThrowsAnyAsync<Exception>(() => app.RefreshAsync(caller.Token));

        Assert.DoesNotContain(app.Logs, l => l.Exception is not null && l.Category.StartsWith("backend.", StringComparison.Ordinal));
        Assert.Empty(await app.SentEventsAsync());
        Assert.Equal("previous", ProGameEvidence.Parse(await app.EvidenceAsync(ForecastGameId(0)))?.Source);
    }

    private static async Task AssertGroupFailureSentOnceAsync(ReportingApp app, Type exception)
    {
        var reported = Assert.Single(await app.SentEventsAsync());
        Assert.Equal((typeof(ProCompetitionEvidenceService).FullName, "error", GroupTemplate), (reported.Logger, reported.Level, reported.Template));
        Assert.Contains(exception.FullName, reported.ExceptionTypes);
        Assert.Equal(PreviousEvidence, await app.EvidenceAsync(Basketball.Id));
        Assert.NotEqual(PreviousEvidence, await app.EvidenceAsync(Hockey.Id));
    }

    private static async Task AssertForecastFailureSentOnceAsync(ReportingApp app, string exception)
    {
        var reported = Assert.Single(await app.SentEventsAsync());
        Assert.Equal((typeof(NflPlayoffLeverageCollector).FullName, "error", ForecastTemplate), (reported.Logger, reported.Level, reported.Template));
        Assert.Contains(exception, reported.ExceptionTypes);
        Assert.Single(app.Logs, l => l.Level >= LogLevel.Warning && l.Category.StartsWith("backend.", StringComparison.Ordinal));
        Assert.Single(app.Provider.Requests, IsForecastRequest);
        await AssertPreviousForecastsKeptAsync(app, games: 2);
    }

    private static async Task AssertPreviousForecastsKeptAsync(ReportingApp app, int games)
    {
        for (var i = 0; i < games; i++)
        {
            var evidence = ProGameEvidence.Parse(await app.EvidenceAsync(ForecastGameId(i)));
            Assert.True(evidence?.VerifiedUnstarted, "the ESPN observation was refreshed");
            Assert.Equal(PreviousForecast, evidence!.NflPlayoffLeverage?.ObservedAt);
        }
    }

    private static async Task<ReportingApp> GroupAppAsync(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> basketball,
        Action<IServiceCollection>? configure = null, bool failHockey = false)
    {
        var app = await ReportingApp.StartAsync((request, ct) =>
            request.RequestUri!.AbsolutePath.Contains("/basketball/nba/", StringComparison.Ordinal)
            || failHockey && request.RequestUri.AbsolutePath.Contains("/hockey/nhl/", StringComparison.Ordinal)
                ? basketball(request, ct)
                : Task.FromResult(Ok("""{"events":[]}""")), services =>
            {
                PinClock(services, GroupNow);
                configure?.Invoke(services);
            });
        await app.SeedAsync(Basketball, Hockey);
        return app;
    }

    private static string ForecastGameId(int index) => $"40187298{5 + index}";

    private static async Task<ReportingApp> ForecastAppAsync(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> forecast,
        int games, Action<IServiceCollection>? configure = null)
    {
        var board = JsonNode.Parse(ProHypeTests.Fixture("nfl-pregame-401872985").GetProperty("board").GetProperty("body").GetRawText())!;
        var events = board["events"]!.AsArray();
        for (var i = 1; i < games; i++)
        {
            var copy = events[0]!.DeepClone();
            copy["id"] = ForecastGameId(i);
            events.Add(copy);
        }
        var app = await ReportingApp.StartAsync((request, ct) => IsForecastRequest(request.RequestUri!)
            ? forecast(request, ct)
            : Task.FromResult(Ok(request.RequestUri!.AbsolutePath.EndsWith("/scoreboard", StringComparison.Ordinal)
                && request.RequestUri.Query.Contains("dates=20261011", StringComparison.Ordinal) ? board.ToJsonString() : """{"events":[]}""")),
            services =>
            {
                PinClock(services, ForecastNow);
                configure?.Invoke(services);
            });
        var previous = new ProGameEvidence
        {
            GameId = "previous", Sport = "nfl", Season = 2026, Phase = 2, ObservedAt = PreviousForecast, Source = "previous",
            NflPlayoffLeverage = new NflPlayoffLeverageEvidence
            {
                Version = 1, Source = "previous", GameId = "previous", Season = 2026, Home = "PIT", Away = "IND",
                ScheduledStart = new DateTime(2026, 10, 11, 17, 0, 0, DateTimeKind.Utc), ObservedAt = PreviousForecast,
                ScheduleVerified = true, Reason = "previous forecast"
            }
        };
        await app.SeedAsync(Enumerable.Range(0, games).Select(i => new Game
        {
            Id = ForecastGameId(i), Sport = "nfl", Status = "scheduled", SeasonYear = 2026, SeasonType = 2, WeekNumber = 6,
            HomeTeam = "Pittsburgh Steelers", AwayTeam = "Indianapolis Colts", HomeTeamAbbreviation = "PIT", AwayTeamAbbreviation = "IND",
            ScheduledStart = new DateTime(2026, 10, 11, 17, 0, 0, DateTimeKind.Utc), ProEvidenceJson = JsonSerializer.Serialize(previous)
        }).ToArray());
        return app;
    }

    private static bool IsForecastRequest(Uri uri) => uri.Host == "www.playoffstatus.com";

    private static void PinClock(IServiceCollection services, DateTimeOffset now)
    {
        services.RemoveAll<TimeProvider>();
        services.AddSingleton<TimeProvider>(new PinnedClock(now));
    }

    private static Task<HttpResponseMessage> Unreachable(HttpRequestMessage request, CancellationToken ct) =>
        throw new HttpRequestException("synthetic provider connection failure");

    private static async Task<HttpResponseMessage> Hang(HttpRequestMessage request, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new UnreachableException();
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new StringContent("{}") };

    private sealed class PinnedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record SentEvent(string? Logger, string? Level, string? Template, string?[] ExceptionTypes);

    private sealed record LoggedEntry(string Category, LogLevel Level, Exception? Exception);

    private sealed class ReportingApp : IAsyncDisposable
    {
        private readonly AppFactory entry = new();
        private readonly PostgreSqlContainer database;
        private readonly WebApplicationFactory<Program> app;
        private readonly SentTransport transport = new();
        private readonly LogRecorder logs = new();

        private ReportingApp(PostgreSqlContainer database, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
            Action<IServiceCollection> configure)
        {
            this.database = database;
            Provider = new SyntheticProvider(respond);
            app = entry.WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.ConfigureServices(services =>
                {
                    foreach (var worker in services.Where(s => s.ServiceType == typeof(IHostedService)
                        && s.ImplementationType?.Namespace?.StartsWith("backend", StringComparison.Ordinal) == true).ToArray())
                        services.Remove(worker);
                    services.ConfigureAll<HttpClientFactoryOptions>(options =>
                        options.HttpMessageHandlerBuilderActions.Add(handlers => handlers.PrimaryHandler = Provider));
                    services.ConfigureAll<HttpStandardResilienceOptions>(options => options.Retry.Delay = TimeSpan.Zero);
                    services.AddSingleton<ILoggerProvider>(logs);
                    services.PostConfigure<SentryAspNetCoreOptions>(options =>
                    {
                        options.Dsn = "https://public@sentry.invalid/1";
                        options.Transport = transport;
                    });
                    services.RemoveAll<IDbContextOptionsConfiguration<ExcitementDbContext>>();
                    services.RemoveAll<DbContextOptions<ExcitementDbContext>>();
                    services.AddDbContext<ExcitementDbContext>(options => options.UseNpgsql(database.GetConnectionString()));
                    configure(services);
                });
            });
        }

        public static async Task<ReportingApp> StartAsync(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
            Action<IServiceCollection> configure)
        {
            var database = IsolatedPostgreSql.CreateContainer();
            await database.StartAsync();
            var app = new ReportingApp(database, respond, configure);
            using var scope = app.Services.CreateScope();
            await TestSchema.CreateAsync(scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Database);
            return app;
        }

        public SyntheticProvider Provider { get; }

        public IServiceProvider Services => app.Services;

        public IEnumerable<LoggedEntry> Logs => logs.Entries;

        public async Task SeedAsync(params Game[] games)
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            foreach (var game in games)
            {
                db.Games.Add(game);
                await db.SaveChangesAsync();
            }
        }

        public async Task RefreshAsync(CancellationToken ct)
        {
            using var scope = app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ProCompetitionEvidenceService>().RefreshAsync(ct);
        }

        public async Task<string?> EvidenceAsync(string gameId)
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            return await db.Games.Where(g => g.Id == gameId).Select(g => g.ProEvidenceJson).SingleAsync();
        }

        public async Task<IReadOnlyCollection<SentEvent>> SentEventsAsync()
        {
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));
            return transport.Events.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            await app.DisposeAsync();
            await entry.DisposeAsync();
            Provider.Dispose();
            await database.DisposeAsync();
        }
    }

    private sealed class SyntheticProvider(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private readonly TaskCompletionSource firstRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource firstForecastRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<Uri> Requests { get; } = new();

        public Task FirstRequest => firstRequest.Task;

        public Task FirstForecastRequest => firstForecastRequest.Task;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.RequestUri!);
            firstRequest.TrySetResult();
            if (IsForecastRequest(request.RequestUri!)) firstForecastRequest.TrySetResult();
            return respond(request, cancellationToken);
        }
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

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new LoggedEntry(category, logLevel, exception));
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
                var exceptionTypes = root.TryGetProperty("exception", out var exception)
                    ? exception.GetProperty("values").EnumerateArray().Select(e => Text(e, "type")).ToArray()
                    : [];
                var template = root.TryGetProperty("logentry", out var logEntry) ? Text(logEntry, "message") : null;
                Events.Enqueue(new SentEvent(Text(root, "logger"), Text(root, "level"), template, exceptionTypes));
            }
        }

        private static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) ? value.GetString() : null;
    }
}
