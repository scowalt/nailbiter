using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using backend.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Npgsql;
using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;
using Xunit;

namespace backend.tests;

[Collection(nameof(SentryGlobalHubCollection))]
public sealed class RatingsRefreshReportingTests
{
    private const string Path = "/api/ratings/refresh";

    [Fact]
    public async Task Failed_step_is_reported_once_at_error_and_the_response_keeps_its_shape()
    {
        await using var app = await RefreshApp.StartAsync();
        await app.ExecuteSqlAsync("DROP TABLE college_teams");

        var response = await app.Client.PostAsync(Path, null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.StartsWith("error: ", body.RootElement.GetProperty("kalshiPlayoffs").GetString());
        Assert.Equal(0, body.RootElement.GetProperty("nfl_powerRankings").GetInt32());
        var reported = Assert.Single(app.Events, e => e.Level == SentryLevel.Error && e.Message?.Contains("kalshiPlayoffs") == true);
        var failure = Assert.IsType<PostgresException>(reported.Exception);
        Assert.Equal(PostgresErrorCodes.UndefinedTable, failure.SqlState);
    }

    [Fact]
    public async Task Provider_timeout_in_a_step_is_a_step_error_while_the_client_stays_connected()
    {
        await using var app = await RefreshApp.StartAsync(db => db.AddInterceptors(new ProviderTimeoutOnCollegeTeamsRead()));

        var response = await app.Client.PostAsync(Path, null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var steps = body.RootElement.EnumerateObject().ToArray();
        Assert.Equal("kalshiPlayoffs", steps[0].Name);
        Assert.StartsWith("error: The request was canceled due to the configured HttpClient.Timeout", steps[0].Value.GetString());
        Assert.Equal("mlb_playoffStatus", steps[^1].Name);
        Assert.All(steps[1..], step => Assert.Equal(JsonValueKind.Number, step.Value.ValueKind));
        var sent = await app.SentEventsAsync();
        var reported = Assert.Single(sent, e => e.Level == "error" && e.ExceptionTypes.Contains(typeof(TaskCanceledException).FullName));
        Assert.Equal("Ratings refresh step kalshiPlayoffs failed", reported.Message);
    }

    [Fact]
    public async Task Client_abort_during_a_step_stops_the_refresh_without_an_error_event()
    {
        await using var app = await RefreshApp.StartAsync();
        await using var teamsLock = await app.LockCollegeTeamsAsync();

        using var abort = new CancellationTokenSource();
        var request = app.Client.PostAsync(Path, null, abort.Token);
        await teamsLock.WaitForBlockedQueryAsync();
        abort.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        var status = await app.RequestCompleted.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(StatusCodes.Status499ClientClosedRequest, status);
        Assert.DoesNotContain(app.Events, e => e.Level >= SentryLevel.Error);
    }

    private sealed record ReportedEvent(SentryLevel? Level, string? Message, Exception? Exception);

    private sealed record SentEvent(string? Level, string? Message, string?[] ExceptionTypes);

    private sealed class RefreshApp : IAsyncDisposable
    {
        private readonly MoneyPuckQualificationHarness harness = new();
        private readonly SentryRecorder recorder = new();
        private readonly TaskCompletionSource<int> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HttpClient Client => harness.Client;
        public EmptyProviders Providers { get; } = new();
        public IEnumerable<ReportedEvent> Events => recorder.Events;
        public Task<int> RequestCompleted => completed.Task;

        public static async Task<RefreshApp> StartAsync(Action<DbContextOptionsBuilder>? configureDb = null)
        {
            var app = new RefreshApp();
            await app.harness.StartAsync(configureDb, enabled: false, configuredTransport: true, configureServices: services =>
            {
                services.ConfigureAll<HttpClientFactoryOptions>(options =>
                    options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = app.Providers));
                services.PostConfigure<SentryAspNetCoreOptions>(options =>
                {
                    options.Dsn = "https://public@sentry.invalid/1";
                    options.Transport = app.recorder;
                    options.AddEventProcessor(app.recorder);
                });
                services.Insert(0, ServiceDescriptor.Singleton<IStartupFilter>(new CompletionSignal(app.completed)));
            });
            return app;
        }

        public async Task<IReadOnlyCollection<SentEvent>> SentEventsAsync()
        {
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));
            return recorder.Sent.ToArray();
        }

        public async Task ExecuteSqlAsync(string sql)
        {
            using var scope = harness.App.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Database.ExecuteSqlRawAsync(sql);
        }

        public async Task<TableLock> LockCollegeTeamsAsync()
        {
            using var scope = harness.App.Services.CreateScope();
            var connectionString = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Database.GetConnectionString()!;
            var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            var transaction = await connection.BeginTransactionAsync();
            await using var command = new NpgsqlCommand("LOCK TABLE college_teams IN ACCESS EXCLUSIVE MODE", connection, transaction);
            await command.ExecuteNonQueryAsync();
            return new TableLock(connectionString, connection, transaction);
        }

        public async ValueTask DisposeAsync()
        {
            await harness.DisposeAsync();
            Providers.Dispose();
        }
    }

    private sealed class TableLock(string connectionString, NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public async Task WaitForBlockedQueryAsync()
        {
            await using var observer = new NpgsqlConnection(connectionString);
            await observer.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query ILIKE '%college_teams%'",
                observer);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while ((long)(await command.ExecuteScalarAsync(timeout.Token))! == 0)
                await Task.Delay(50, timeout.Token);
        }

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class ProviderTimeoutOnCollegeTeamsRead : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("college_teams", StringComparison.Ordinal))
            {
                using var provider = new HttpClient(new HangingProvider()) { Timeout = TimeSpan.FromMilliseconds(100) };
                await provider.GetAsync("https://provider.invalid/", cancellationToken);
            }
            return result;
        }
    }

    private sealed class HangingProvider : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        }
    }

    private sealed class EmptyProviders : HttpMessageHandler
    {
        public ConcurrentQueue<string> Urls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Enqueue(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") });
        }
    }

    private sealed class CompletionSignal(TaskCompletionSource<int> completed) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, inner) =>
            {
                try { await inner(context); }
                finally
                {
                    if (context.Request.Path == Path) completed.TrySetResult(context.Response.StatusCode);
                }
            });
            next(app);
        };
    }

    private sealed class SentryRecorder : ITransport, ISentryEventProcessor
    {
        public ConcurrentQueue<ReportedEvent> Events { get; } = new();
        public ConcurrentQueue<SentEvent> Sent { get; } = new();

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
                var message = root.TryGetProperty("logentry", out var m) && m.TryGetProperty("formatted", out var f) ? f.GetString() : null;
                var exceptionTypes = root.TryGetProperty("exception", out var exception)
                    ? exception.GetProperty("values").EnumerateArray().Select(e => e.GetProperty("type").GetString()).ToArray()
                    : [];
                Sent.Enqueue(new SentEvent(root.GetProperty("level").GetString(), message, exceptionTypes));
            }
        }

        public SentryEvent? Process(SentryEvent sentryEvent)
        {
            Events.Enqueue(new ReportedEvent(sentryEvent.Level, sentryEvent.Message?.Formatted, sentryEvent.Exception));
            return sentryEvent;
        }
    }
}
