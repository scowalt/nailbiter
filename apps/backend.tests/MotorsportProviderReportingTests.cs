using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using backend.Services.EspnF1;
using backend.Services.EspnIndyCar;
using backend.Services.EspnNascar;
using backend.Services.F1;
using backend.Services.IndyCar;
using backend.Services.Kalshi;
using backend.Services.Nascar;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Polly.Timeout;
using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;
using Xunit;

namespace backend.tests;

[Collection(nameof(SentryGlobalHubCollection))]
public sealed class MotorsportProviderReportingTests
{
    private const int StandardAttempts = 4;
    private static readonly Answer Hang = new Hanging();
    private static readonly Answer Unavailable = new Reply(HttpStatusCode.ServiceUnavailable, "{}");
    private static readonly Answer Fault = new Faulting();

    private static readonly Dictionary<string, Fetch> Scoreboards = new()
    {
        ["F1"] = new(typeof(EspnF1Service), "Failed to fetch ESPN F1 scoreboard from {Url}",
            async (services, ct) => await services.GetRequiredService<EspnF1Service>().FetchScoreboardAsync(null, ct)),
        ["NASCAR"] = new(typeof(EspnNascarService), "Failed to fetch ESPN NASCAR scoreboard from {Url}",
            async (services, ct) => await services.GetRequiredService<EspnNascarService>().FetchScoreboardAsync(null, ct)),
        ["IndyCar"] = new(typeof(EspnIndyCarService), "Failed to fetch ESPN IndyCar scoreboard from {Url}",
            async (services, ct) => await services.GetRequiredService<EspnIndyCarService>().FetchScoreboardAsync(null, ct)),
    };

    private static readonly Dictionary<string, Fetch> Competitors = new()
    {
        ["F1"] = new(typeof(EspnF1Service), null,
            async (services, ct) => await services.GetRequiredService<EspnF1Service>().FetchCompetitorsAsync("9001", ct)),
        ["NASCAR"] = new(typeof(EspnNascarService), null,
            async (services, ct) => await services.GetRequiredService<EspnNascarService>().FetchCompetitorsAsync("9001", ct)),
        ["IndyCar"] = new(typeof(EspnIndyCarService), null,
            async (services, ct) => await services.GetRequiredService<EspnIndyCarService>().FetchCompetitorsAsync("9001", ct)),
    };

    private static readonly Dictionary<string, Fetch> MotorsportFeeds = new()
    {
        ["KalshiF1"] = new(typeof(KalshiF1OddsService), null,
            async (services, ct) => await services.GetRequiredService<KalshiF1OddsService>().GetWinProbabilitiesAsync(ct)),
        ["KalshiNascar"] = new(typeof(KalshiNascarOddsService), null,
            async (services, ct) => await services.GetRequiredService<KalshiNascarOddsService>().GetWinProbabilitiesAsync(ct)),
        ["NascarLiveFeed"] = new(typeof(NascarLiveFeedService), null,
            async (services, ct) => await services.GetRequiredService<NascarLiveFeedService>().GetLiveFeedAsync(ct)),
    };

    private const string NoCompetitions = """{"competitions":[]}""";
    private const string NoCompetitors = """{"count":0,"items":[]}""";
    private const string OneCompetitorRef = """{"count":1,"items":[{"$ref":"http://sports.core.api.espn.pvt/v2/sports/racing/leagues/synthetic/events/9001/competitions/9001/competitors/77?lang=en"}]}""";

    private static readonly Dictionary<string, Site> Sites = new()
    {
        ["F1 scoreboard"] = new(Scoreboards["F1"], target => _ => target),
        ["NASCAR scoreboard"] = new(Scoreboards["NASCAR"], target => _ => target),
        ["IndyCar scoreboard"] = new(Scoreboards["IndyCar"], target => _ => target),
        ["F1 competition"] = new(Competitors["F1"] with { Template = "Could not resolve F1 competition ID for event {EventId}, using event ID" },
            target => uri => uri.AbsolutePath.EndsWith("/events/9001", StringComparison.Ordinal) ? target : new Reply(HttpStatusCode.OK, NoCompetitors)),
        ["F1 competitors"] = new(Competitors["F1"] with { Template = "Failed to fetch ESPN core F1 competitors for event {EventId} (falling back to scoreboard data)" },
            target => uri => uri.AbsolutePath.EndsWith("/competitors", StringComparison.Ordinal) ? target : new Reply(HttpStatusCode.OK, NoCompetitions)),
        ["F1 competitor ref"] = new(Competitors["F1"] with { Template = "Failed to resolve competitor ref: {Url}" }, target => uri => uri.AbsolutePath switch
        {
            var path when path.Contains("/competitors/", StringComparison.Ordinal) => target,
            var path when path.EndsWith("/competitors", StringComparison.Ordinal) => new Reply(HttpStatusCode.OK, OneCompetitorRef),
            _ => new Reply(HttpStatusCode.OK, NoCompetitions),
        }),
        ["NASCAR competitors"] = new(Competitors["NASCAR"] with { Template = "Failed to fetch ESPN core NASCAR competitors for event {EventId}" }, target => _ => target),
        ["IndyCar competitors"] = new(Competitors["IndyCar"] with { Template = "Failed to fetch ESPN core IndyCar competitors for event {EventId} (falling back to scoreboard data)" },
            target => _ => target),
        ["IndyCar competitor ref"] = new(Competitors["IndyCar"] with { Template = "Failed to resolve competitor ref: {Url}" },
            target => uri => uri.AbsolutePath.Contains("/competitors/", StringComparison.Ordinal) ? target : new Reply(HttpStatusCode.OK, OneCompetitorRef)),
        ["KalshiF1"] = new(MotorsportFeeds["KalshiF1"] with { Template = "Failed to fetch F1 markets from Kalshi" }, target => _ => target),
        ["KalshiNascar"] = new(MotorsportFeeds["KalshiNascar"] with { Template = "Failed to fetch NASCAR markets from Kalshi" }, target => _ => target),
        ["NascarLiveFeed"] = new(MotorsportFeeds["NascarLiveFeed"] with { Template = "Failed to fetch NASCAR.com live feed" }, target => _ => target),
    };

    public static TheoryData<string> SiteNames() => new(Sites.Keys);

    public static TheoryData<string, HttpStatusCode, string> ProviderAnswers()
    {
        var cases = new TheoryData<string, HttpStatusCode, string>();
        foreach (var site in Sites.Keys)
        {
            cases.Add(site, HttpStatusCode.NotFound, "{}");
            cases.Add(site, HttpStatusCode.OK, """{"leagues":[""");
        }
        return cases;
    }

    private static readonly Dictionary<string, (Type Service, Func<IServiceProvider, Task<int>> Refresh)> ScheduleRefreshes = new()
    {
        ["F1"] = (typeof(EspnF1Service), services => services.GetRequiredService<F1DataService>().RefreshScheduleAsync(2026, CancellationToken.None)),
        ["NASCAR"] = (typeof(EspnNascarService), services => services.GetRequiredService<NascarDataService>().RefreshScheduleAsync(2026, CancellationToken.None)),
        ["IndyCar"] = (typeof(EspnIndyCarService), services => services.GetRequiredService<IndyCarDataService>().RefreshScheduleAsync(2026, CancellationToken.None)),
    };

    public static TheoryData<string, HttpStatusCode, string> UnreadableScoreboards()
    {
        var cases = new TheoryData<string, HttpStatusCode, string>();
        foreach (var sport in Scoreboards.Keys)
        {
            cases.Add(sport, HttpStatusCode.OK, """{"leagues":[""");
            cases.Add(sport, HttpStatusCode.OK, "");
            cases.Add(sport, HttpStatusCode.NotFound, "{}");
        }
        return cases;
    }

    [Theory]
    [InlineData("F1")]
    [InlineData("NASCAR")]
    [InlineData("IndyCar")]
    public async Task Caller_cancellation_mid_scoreboard_fetch_propagates_without_an_event(string sport)
    {
        await AssertCallerCancellationPropagatesAsync(Scoreboards[sport], _ => Hang);
    }

    [Theory]
    [MemberData(nameof(UnreadableScoreboards))]
    public async Task Unreadable_or_refused_scoreboard_is_a_warning_without_an_event(string sport, HttpStatusCode status, string body)
    {
        var scoreboard = Scoreboards[sport];
        await using var app = new MotorsportApp(_ => new Reply(status, body));

        Assert.Null(await scoreboard.Run(app.Services, CancellationToken.None));

        var failure = Assert.Single(app.Logs, l => l.Category == scoreboard.Service.FullName && l.Exception is not null);
        Assert.Equal(LogLevel.Warning, failure.Level);
        if (status == HttpStatusCode.OK)
            Assert.Equal("JsonReaderException", Assert.IsAssignableFrom<JsonException>(failure.Exception).InnerException?.GetType().Name);
        Assert.Empty(await app.SentEventsAsync());
    }

    [Theory]
    [InlineData("F1")]
    [InlineData("NASCAR")]
    [InlineData("IndyCar")]
    public async Task Scoreboard_server_errors_after_retries_are_sent_once_at_error(string sport)
    {
        var scoreboard = Scoreboards[sport];
        await using var app = new MotorsportApp(_ => Unavailable);

        Assert.Null(await scoreboard.Run(app.Services, CancellationToken.None));

        Assert.Equal(StandardAttempts, app.Provider.Requests);
        AssertSentOnceAtError(await app.SentEventsAsync(), scoreboard, typeof(HttpRequestException));
    }

    [Theory]
    [InlineData("F1")]
    [InlineData("NASCAR")]
    [InlineData("IndyCar")]
    public async Task Scoreboard_timeout_after_retries_is_sent_once_at_error(string sport)
    {
        var scoreboard = Scoreboards[sport];
        await using var app = new MotorsportApp(_ => Hang, services => services.ConfigureAll<HttpStandardResilienceOptions>(options =>
            options.AttemptTimeout.Timeout = TimeSpan.FromMilliseconds(50)));

        Assert.Null(await scoreboard.Run(app.Services, CancellationToken.None));

        Assert.Equal(StandardAttempts, app.Provider.Requests);
        AssertSentOnceAtError(await app.SentEventsAsync(), scoreboard, typeof(TimeoutRejectedException));
    }

    [Theory]
    [InlineData("F1")]
    [InlineData("NASCAR")]
    [InlineData("IndyCar")]
    public async Task Caller_cancellation_on_the_first_competitors_request_propagates_without_an_event(string sport)
    {
        await AssertCallerCancellationPropagatesAsync(Competitors[sport], _ => Hang);
    }

    [Theory]
    [InlineData("F1")]
    [InlineData("IndyCar")]
    public async Task Caller_cancellation_while_resolving_a_competitor_ref_propagates_without_an_event(string sport)
    {
        const string eventJson = """{"competitions":[{"$ref":"http://sports.core.api.espn.pvt/v2/sports/racing/leagues/synthetic/events/9001/competitions/9002?lang=en"}]}""";
        const string listJson = """{"count":1,"items":[{"$ref":"http://sports.core.api.espn.pvt/v2/sports/racing/leagues/synthetic/events/9001/competitions/9002/competitors/77?lang=en"}]}""";

        await AssertCallerCancellationPropagatesAsync(Competitors[sport], uri => uri.AbsolutePath switch
        {
            var path when path.EndsWith("/competitors", StringComparison.Ordinal) => new Reply(HttpStatusCode.OK, listJson),
            var path when path.Contains("/competitors/", StringComparison.Ordinal) => Hang,
            _ => new Reply(HttpStatusCode.OK, eventJson),
        });
    }

    [Theory]
    [InlineData("F1")]
    [InlineData("NASCAR")]
    [InlineData("IndyCar")]
    public async Task Competitors_server_errors_are_a_warning_without_an_event(string sport)
    {
        var competitors = Competitors[sport];
        await using var app = new MotorsportApp(_ => Unavailable);

        Assert.Null(await competitors.Run(app.Services, CancellationToken.None));

        var failures = app.Logs.Where(l => l.Category == competitors.Service.FullName && l.Exception is not null).ToArray();
        Assert.NotEmpty(failures);
        Assert.All(failures, failure => Assert.Equal(LogLevel.Warning, failure.Level));
        Assert.Empty(await app.SentEventsAsync());
    }

    [Theory]
    [InlineData("KalshiF1")]
    [InlineData("KalshiNascar")]
    [InlineData("NascarLiveFeed")]
    public async Task Caller_cancellation_mid_motorsport_feed_fetch_propagates_without_an_event(string feed)
    {
        await AssertCallerCancellationPropagatesAsync(MotorsportFeeds[feed], _ => Hang);
    }

    [Theory]
    [InlineData("KalshiF1")]
    [InlineData("KalshiNascar")]
    public async Task Kalshi_motorsport_server_errors_are_a_warning_without_an_event(string feed)
    {
        var odds = MotorsportFeeds[feed];
        await using var app = new MotorsportApp(_ => Unavailable);

        var probabilities = Assert.IsAssignableFrom<IReadOnlyDictionary<string, double>>(await odds.Run(app.Services, CancellationToken.None));

        Assert.Empty(probabilities);
        Assert.Equal(LogLevel.Warning, Assert.Single(app.Logs, l => l.Category == odds.Service.FullName && l.Exception is not null).Level);
        Assert.Empty(await app.SentEventsAsync());
    }

    [Theory]
    [InlineData("F1")]
    [InlineData("NASCAR")]
    [InlineData("IndyCar")]
    public async Task Schedule_refresh_leaves_the_scoreboard_failure_to_the_fetch_that_sent_it(string sport)
    {
        var (service, refresh) = ScheduleRefreshes[sport];
        await using var app = new MotorsportApp(_ => Unavailable);
        using var scope = app.Services.CreateScope();

        Assert.Equal(0, await refresh(scope.ServiceProvider));

        var reported = Assert.Single(await app.SentEventsAsync());
        Assert.Equal((service.FullName, "error"), (reported.Logger, reported.Level));
    }

    [Theory]
    [MemberData(nameof(SiteNames))]
    public async Task Own_fault_below_a_motorsport_fetch_is_sent_once_at_error(string name)
    {
        var site = Sites[name];
        await using var app = new MotorsportApp(site.At(Fault));

        await site.Fetch.Run(app.Services, CancellationToken.None);

        AssertSentOnceAtError(await app.SentEventsAsync(), site.Fetch, typeof(InvalidOperationException));
    }

    [Theory]
    [MemberData(nameof(SiteNames))]
    public async Task Client_deadline_on_a_motorsport_fetch_is_sent_once_at_error(string name)
    {
        var site = Sites[name];
        await using var app = new MotorsportApp(site.At(Hang), services => services.ConfigureAll<HttpClientFactoryOptions>(options =>
            options.HttpClientActions.Add(client => client.Timeout = TimeSpan.FromMilliseconds(200))));

        await site.Fetch.Run(app.Services, CancellationToken.None);

        AssertSentOnceAtError(await app.SentEventsAsync(), site.Fetch, typeof(TaskCanceledException));
    }

    [Theory]
    [MemberData(nameof(ProviderAnswers))]
    public async Task Missing_or_unreadable_motorsport_answer_is_a_warning_without_an_event(string name, HttpStatusCode status, string body)
    {
        var site = Sites[name];
        await using var app = new MotorsportApp(site.At(new Reply(status, body)));

        await site.Fetch.Run(app.Services, CancellationToken.None);

        var failure = Assert.Single(app.Logs, l => l.Category == site.Fetch.Service.FullName && l.Exception is not null);
        Assert.Equal(LogLevel.Warning, failure.Level);
        Assert.Empty(await app.SentEventsAsync());
    }

    private static async Task AssertCallerCancellationPropagatesAsync(Fetch fetch, Func<Uri, Answer> answer)
    {
        await using var app = new MotorsportApp(answer);
        using var caller = new CancellationTokenSource();

        var fetching = fetch.Run(app.Services, caller.Token);
        await app.Provider.HangStarted.WaitAsync(TimeSpan.FromSeconds(10));
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetching);
        Assert.DoesNotContain(app.Logs, l => l.Category == fetch.Service.FullName && l.Level >= LogLevel.Warning);
        Assert.Empty(await app.SentEventsAsync());
    }

    private static void AssertSentOnceAtError(IReadOnlyCollection<SentEvent> sent, Fetch fetch, Type exception)
    {
        var reported = Assert.Single(sent);
        Assert.Equal(fetch.Service.FullName, reported.Logger);
        Assert.Equal("error", reported.Level);
        Assert.Equal(fetch.Template, reported.Template);
        Assert.Contains(reported.Exceptions, e => e.Type == exception.FullName);
    }

    private sealed record Fetch(Type Service, string? Template, Func<IServiceProvider, CancellationToken, Task<object?>> Run);

    private abstract record Answer;

    private sealed record Reply(HttpStatusCode Status, string Body) : Answer;

    private sealed record Hanging : Answer;

    private sealed record Faulting : Answer;

    private sealed record Site(Fetch Fetch, Func<Answer, Func<Uri, Answer>> At);

    private sealed record SentEvent(string? Logger, string? Level, string? Template, (string? Type, string? Value)[] Exceptions);

    private sealed record LoggedEntry(string Category, LogLevel Level, Exception? Exception);

    private sealed class MotorsportApp : IAsyncDisposable
    {
        private readonly AppFactory entry = new();
        private readonly WebApplicationFactory<Program> app;
        private readonly SentTransport transport = new();
        private readonly LogRecorder logs = new();

        public MotorsportApp(Func<Uri, Answer> answer, Action<IServiceCollection>? configure = null)
        {
            Provider = new SyntheticProvider(answer);
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
                    configure?.Invoke(services);
                });
            });
        }

        public SyntheticProvider Provider { get; }

        public IServiceProvider Services => app.Services;

        public IEnumerable<LoggedEntry> Logs => logs.Entries;

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
        }
    }

    private sealed class SyntheticProvider(Func<Uri, Answer> answer) : HttpMessageHandler
    {
        private readonly TaskCompletionSource hanging = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int requests;

        public Task HangStarted => hanging.Task;

        public int Requests => requests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requests);
            switch (answer(request.RequestUri!))
            {
                case Reply reply:
                    return new HttpResponseMessage(reply.Status) { Content = new StringContent(reply.Body) };
                case Faulting:
                    throw new InvalidOperationException("Synthetic fault below the fetch");
            }
            hanging.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
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
