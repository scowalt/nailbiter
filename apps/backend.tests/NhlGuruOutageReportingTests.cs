using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using backend.Services;
using backend.Services.PowerRankingsGuru;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;
using Xunit;

namespace backend.tests;

[Collection(nameof(SentryGlobalHubCollection))]
public sealed class NhlGuruOutageReportingTests
{
    private const string Template = "NHL Power Rankings Guru collection interrupted; original observations preserved";
    private static readonly string Page = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NhlGuru", "rankings.html"));

    [Theory]
    [InlineData("503")]
    [InlineData("429")]
    [InlineData("connection refused")]
    public async Task Site_outage_is_sent_once_at_error(string outage)
    {
        await using var app = await GuruApp.StartAsync(_ => outage switch
        {
            "503" => new Reply(HttpStatusCode.ServiceUnavailable, ""),
            "429" => new Reply(HttpStatusCode.TooManyRequests, ""),
            _ => new Refused()
        });

        Assert.Equal(0, await app.RefreshAsync(CancellationToken.None));

        AssertSentOnceAtError(await app.SentEventsAsync(), typeof(HttpRequestException));
    }

    [Fact]
    public async Task Own_deadline_on_a_stalled_site_is_sent_once_at_error()
    {
        await using var app = await GuruApp.StartAsync(_ => new Hanging(), services =>
            services.AddHttpClient(nameof(NhlPowerRankingsGuruService)).ConfigureHttpClient(client => client.Timeout = TimeSpan.FromMilliseconds(100)));

        Assert.Equal(0, await app.RefreshAsync(CancellationToken.None));

        AssertSentOnceAtError(await app.SentEventsAsync(), typeof(TaskCanceledException));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Refusal_is_a_warning_without_an_event(HttpStatusCode status)
    {
        await using var app = await GuruApp.StartAsync(_ => new Reply(status, ""));

        Assert.Equal(0, await app.RefreshAsync(CancellationToken.None));

        var refusal = Assert.Single(app.Logs, l => l.Category == typeof(NhlPowerRankingsGuruService).FullName && l.Level >= LogLevel.Warning);
        Assert.Equal(LogLevel.Warning, refusal.Level);
        Assert.Empty(await app.SentEventsAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html><body><main><div data-component='PageSheet'><header><h1>")]
    [InlineData("non-ASCII season digits")]
    public async Task Malformed_page_is_a_warning_without_an_event(string body)
    {
        if (body == "non-ASCII season digits") body = Page.Replace("2026-27 NHL", "٢٠٢٦-٢٧ NHL", StringComparison.Ordinal);
        await using var app = await GuruApp.StartAsync(_ => new Reply(HttpStatusCode.OK, body));

        Assert.Equal(0, await app.RefreshAsync(CancellationToken.None));

        var rejected = Assert.Single(app.Logs, l => l.Category == typeof(NhlPowerRankingsGuruService).FullName && l.Level >= LogLevel.Warning);
        Assert.Equal(LogLevel.Warning, rejected.Level);
        Assert.Empty(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Caller_cancellation_propagates_without_an_event()
    {
        await using var app = await GuruApp.StartAsync(_ => new Hanging());
        using var caller = new CancellationTokenSource();

        var refreshing = app.RefreshAsync(caller.Token);
        await app.Provider.HangStarted.WaitAsync(TimeSpan.FromSeconds(10));
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refreshing);
        Assert.DoesNotContain(app.Logs, l => l.Category == typeof(NhlPowerRankingsGuruService).FullName && l.Level >= LogLevel.Warning);
        Assert.Empty(await app.SentEventsAsync());
    }

    [Fact]
    public async Task Own_fault_below_the_catch_is_sent_once_at_error_by_the_daily_refresh()
    {
        await using var app = await GuruApp.StartAsync(
            uri => uri.AbsoluteUri == NhlPowerRankingsGuruService.RankingsUrl ? new Reply(HttpStatusCode.OK, Page) : new Reply(HttpStatusCode.NotFound, ""),
            configureDb: db => db.AddInterceptors(new FaultOnGuruRowsRead()));

        await app.Background.RefreshAllRatingsAsync(CancellationToken.None);

        var sent = await app.SentEventsAsync();
        var reported = Assert.Single(sent, e => e.Exceptions.Any(x => x.Type == typeof(InvalidOperationException).FullName));
        Assert.Equal("error", reported.Level);
        Assert.Equal(typeof(RatingsRefreshBackgroundService).FullName, reported.Logger);
        Assert.Equal("Failed to refresh PowerRankingsGuru NHL rankings", reported.Template);
        Assert.DoesNotContain(sent, e => e.Logger == typeof(NhlPowerRankingsGuruService).FullName);
    }

    private static void AssertSentOnceAtError(IReadOnlyCollection<SentEvent> sent, Type exception)
    {
        var reported = Assert.Single(sent);
        Assert.Equal(typeof(NhlPowerRankingsGuruService).FullName, reported.Logger);
        Assert.Equal("error", reported.Level);
        Assert.Equal(Template, reported.Template);
        Assert.Contains(reported.Exceptions, e => e.Type == exception.FullName);
    }

    private abstract record Answer;

    private sealed record Reply(HttpStatusCode Status, string Body) : Answer;

    private sealed record Hanging : Answer;

    private sealed record Refused : Answer;

    private sealed record SentEvent(string? Logger, string? Level, string? Template, (string? Type, string? Value)[] Exceptions);

    private sealed record LoggedEntry(string Category, LogLevel Level, Exception? Exception);

    private sealed class GuruApp : IAsyncDisposable
    {
        private readonly MoneyPuckQualificationHarness harness = new();
        private readonly SentTransport transport = new();
        private readonly LogRecorder logs = new();

        private GuruApp(Func<Uri, Answer> answer) => Provider = new SyntheticProvider(answer);

        public SyntheticProvider Provider { get; }

        public IEnumerable<LoggedEntry> Logs => logs.Entries;

        public RatingsRefreshBackgroundService Background =>
            new(harness.App.Services, harness.App.Services.GetRequiredService<ILogger<RatingsRefreshBackgroundService>>());

        public static async Task<GuruApp> StartAsync(Func<Uri, Answer> answer, Action<IServiceCollection>? configure = null,
            Action<Microsoft.EntityFrameworkCore.DbContextOptionsBuilder>? configureDb = null)
        {
            var app = new GuruApp(answer);
            await app.harness.StartAsync(configureDb, enabled: false, configuredTransport: true, configureServices: services =>
            {
                services.ConfigureAll<HttpClientFactoryOptions>(options =>
                    options.HttpMessageHandlerBuilderActions.Add(handlers => handlers.PrimaryHandler = app.Provider));
                services.AddSingleton<ILoggerProvider>(app.logs);
                services.PostConfigure<SentryAspNetCoreOptions>(options =>
                {
                    options.Dsn = "https://public@sentry.invalid/1";
                    options.Transport = app.transport;
                });
                configure?.Invoke(services);
            });
            return app;
        }

        public async Task<int> RefreshAsync(CancellationToken ct)
        {
            using var scope = harness.App.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<PowerRankingsGuruService>().RefreshNhlRatingsAsync(ct);
        }

        public async Task<IReadOnlyCollection<SentEvent>> SentEventsAsync()
        {
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));
            return transport.Events.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            await harness.DisposeAsync();
            Provider.Dispose();
        }
    }

    private sealed class SyntheticProvider(Func<Uri, Answer> answer) : HttpMessageHandler
    {
        private readonly TaskCompletionSource hanging = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task HangStarted => hanging.Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            switch (answer(request.RequestUri!))
            {
                case Reply reply:
                    return new HttpResponseMessage(reply.Status) { Content = new StringContent(reply.Body) };
                case Refused:
                    throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused", new SocketException((int)SocketError.ConnectionRefused));
                default:
                    hanging.TrySetResult();
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    throw new UnreachableException();
            }
        }
    }

    private sealed class FaultOnGuruRowsRead : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) =>
            command.CommandText.Contains(NhlPowerRankingsGuruService.Source, StringComparison.Ordinal)
                ? throw new InvalidOperationException("Synthetic own-code fault below the NHL guru catch")
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
