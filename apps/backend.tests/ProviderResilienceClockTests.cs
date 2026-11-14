using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using backend.Services;
using backend.Services.Backfill;
using backend.Services.Espn;
using backend.Services.EspnF1;
using backend.Services.EspnIndyCar;
using backend.Services.EspnNascar;
using backend.Services.Nascar;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Polly.Timeout;
using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;
using Xunit;

namespace backend.tests;

[Collection(nameof(SentryGlobalHubCollection))]
public sealed class ProviderResilienceClockTests
{
    private const int StandardAttempts = 4;
    private const int HungAttemptsWithinTotal = 3;
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TotalRequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan DisposalStep = TimeSpan.FromSeconds(1);

    private static readonly Dictionary<string, Fetch> Scoreboards = new()
    {
        ["EspnOdds"] = new(typeof(EspnOddsService), "Failed to fetch ESPN scoreboard for {Sport} on {Date}",
            async (services, ct) => await services.GetRequiredService<EspnOddsService>().FetchScoreboardAsync("nba", "20261107", ct)),
        ["NflBackfill"] = new(typeof(NflBackfillService), "Failed to fetch ESPN scoreboard for {Year} ST{Type} Week {Week}",
            async (services, ct) => await services.GetRequiredService<NflBackfillService>().FetchScoreboardByWeekAsync(2026, 2, 1, ct)),
        ["EspnF1"] = new(typeof(EspnF1Service), "Failed to fetch ESPN F1 scoreboard from {Url}",
            async (services, ct) => await services.GetRequiredService<EspnF1Service>().FetchScoreboardAsync(null, ct)),
        ["EspnNascar"] = new(typeof(EspnNascarService), "Failed to fetch ESPN NASCAR scoreboard from {Url}",
            async (services, ct) => await services.GetRequiredService<EspnNascarService>().FetchScoreboardAsync(null, ct)),
        ["EspnIndyCar"] = new(typeof(EspnIndyCarService), "Failed to fetch ESPN IndyCar scoreboard from {Url}",
            async (services, ct) => await services.GetRequiredService<EspnIndyCarService>().FetchScoreboardAsync(null, ct)),
    };

    public static TheoryData<string> ScoreboardNames => [.. Scoreboards.Keys];

    public static TheoryData<string, HttpStatusCode?> RetryableAnswers => Cross<HttpStatusCode?>(
        HttpStatusCode.ServiceUnavailable, HttpStatusCode.InternalServerError, HttpStatusCode.TooManyRequests, HttpStatusCode.RequestTimeout, null);

    public static TheoryData<string, HttpStatusCode> NonRetryableAnswers => Cross(
        HttpStatusCode.NotFound, HttpStatusCode.Forbidden, HttpStatusCode.BadRequest);

    private static TheoryData<string, T> Cross<T>(params T[] answers)
    {
        var data = new TheoryData<string, T>();
        foreach (var name in Scoreboards.Keys)
            foreach (var answer in answers)
                data.Add(name, answer);
        return data;
    }

    [Theory]
    [MemberData(nameof(ScoreboardNames))]
    public async Task Hung_scoreboard_ends_at_the_total_timeout_and_is_sent_once_at_error(string name)
    {
        var scoreboard = Scoreboards[name];
        await UsingAsync(new ClockedApp(Answer.Hang), async app =>
        {
            var fetch = scoreboard.Run(app.Services, CancellationToken.None);

            Assert.Equal(TotalRequestTimeout, await app.RunAsync(fetch));
            Assert.Null(await fetch);
            AssertHungAttempts(app.Provider.Attempts);
            var reported = Assert.Single(await app.SentEventsAsync());
            Assert.Equal((scoreboard.Service.FullName, "error", scoreboard.Template), (reported.Logger, reported.Level, reported.Template));
            Assert.Equal(typeof(TimeoutRejectedException).FullName, reported.ExceptionTypes[^1]);
            Assert.DoesNotContain(app.Logs, l => l.Category == "Polly" && l.Level >= LogLevel.Error);
        });
    }

    [Fact]
    public async Task Every_resilient_provider_client_ends_a_hung_request_at_the_total_timeout()
    {
        await UsingAsync(new ClockedApp(Answer.Hang), async app =>
        {
            var clients = app.Services.GetRequiredService<IHttpClientFactory>();
            var names = HttpClientRegistrations.Names(app.Services).Except(HttpClientRegistrations.DeadlineBound).Order().ToArray();
            Assert.Contains(nameof(EspnOddsService), names);
            Assert.Contains(nameof(NascarLiveFeedService), names);

            foreach (var name in names)
            {
                app.Provider.Reset();
                var started = app.Clock.Elapsed;
                var request = clients.CreateClient(name).GetStringAsync("https://provider.invalid/hung");

                var ended = await app.RunAsync(request);

                Assert.True(ended - started == TotalRequestTimeout, $"{name} ended after {ended - started}");
                Assert.IsType<TimeoutRejectedException>(await Record.ExceptionAsync(() => request));
                AssertHungAttempts(app.Provider.Attempts, started);
            }
        });
    }

    [Theory]
    [MemberData(nameof(RetryableAnswers))]
    public async Task Retryable_scoreboard_failure_stops_after_the_standard_attempts_and_is_sent_once_at_error(string name, HttpStatusCode? status)
    {
        var scoreboard = Scoreboards[name];
        await UsingAsync(new ClockedApp(status is { } code ? Answer.Status(code) : Answer.Unreachable), async app =>
        {
            var fetch = scoreboard.Run(app.Services, CancellationToken.None);

            Assert.InRange(await app.RunAsync(fetch), TimeSpan.Zero, TotalRequestTimeout - AttemptTimeout);
            Assert.Null(await fetch);
            Assert.Equal(StandardAttempts, app.Provider.Attempts.Count);
            var reported = Assert.Single(await app.SentEventsAsync());
            Assert.Equal((scoreboard.Service.FullName, "error", scoreboard.Template), (reported.Logger, reported.Level, reported.Template));
            Assert.Equal(typeof(HttpRequestException).FullName, reported.ExceptionTypes[^1]);
            var finalAttempt = Assert.Single(app.Logs, l => l.Category == "Polly" && l.Message.Contains($"Attempt: '{StandardAttempts - 1}'", StringComparison.Ordinal));
            Assert.Equal(LogLevel.Warning, finalAttempt.Level);
        });
    }

    [Fact]
    public async Task Retry_reaches_the_provider_when_the_thread_that_armed_its_attempt_timeout_is_descheduled()
    {
        var scoreboard = Scoreboards["EspnOdds"];
        await UsingAsync(new ClockedApp(Answer.Status(HttpStatusCode.TooManyRequests), descheduleAfterArming: TimeSpan.FromMilliseconds(50)), async app =>
        {
            var fetch = scoreboard.Run(app.Services, CancellationToken.None);
            var elapsed = await app.RunAsync(fetch);

            Assert.Equal(StandardAttempts, app.Provider.Attempts.Count);
            Assert.InRange(elapsed, TimeSpan.Zero, TotalRequestTimeout - AttemptTimeout);
        });
    }

    [Fact]
    public async Task Disposing_the_app_ends_a_retry_that_waits_on_the_stopped_clock()
    {
        var app = new ClockedApp(Answer.Hang);
        var fetch = Scoreboards["EspnOdds"].Run(app.Services, CancellationToken.None);
        await app.RunUntilAsync(fetch, () => app.Provider.Attempts is [{ Ended: not null }], AttemptTimeout * 2);

        await Task.Run(() => app.DisposeAsync().AsTask()).WaitAsync(Guard);
        await fetch.WaitAsync(Guard);
    }

    [Fact]
    public async Task Disposing_the_app_keeps_the_clock_moving_while_disposal_is_pending_with_no_live_timer()
    {
        var app = new ClockedApp(Answer.Hang, services => services.AddSingleton<LateTimerDisposal>());
        var disposal = app.Services.GetRequiredService<LateTimerDisposal>();
        Assert.Equal(0, app.Clock.LiveTimers);

        await Task.Run(() => app.DisposeAsync().AsTask()).WaitAsync(Guard);

        Assert.True(disposal.Finished);
    }

    [Fact]
    public async Task A_failed_test_body_stays_the_primary_failure_when_disposal_also_fails()
    {
        var failure = Record.Exception(() => Assert.Equal(StandardAttempts, HungAttemptsWithinTotal));
        var disposal = new TimeoutException("still disposing at 00:00:20 with 1 live timers");

        var reported = await Record.ExceptionAsync(() => UsingAsync(new FailingDisposal(disposal), _ => throw failure));

        Assert.StartsWith(failure.Message, reported.Message, StringComparison.Ordinal);
        Assert.Equal([failure, disposal], Assert.IsAssignableFrom<AggregateException>(reported).InnerExceptions);
    }

    [Fact]
    public async Task A_disposal_failure_after_a_passing_test_body_still_fails_the_test()
    {
        var disposal = new TimeoutException("still disposing at 00:00:20 with 1 live timers");

        var reported = await Record.ExceptionAsync(() => UsingAsync(new FailingDisposal(disposal), _ => Task.CompletedTask));

        Assert.Same(disposal, reported);
    }

    [Theory]
    [MemberData(nameof(NonRetryableAnswers))]
    public async Task Non_retryable_scoreboard_answer_is_fetched_once_and_stays_a_warning(string name, HttpStatusCode status)
    {
        var scoreboard = Scoreboards[name];
        await UsingAsync(new ClockedApp(Answer.Status(status)), async app =>
        {
            var fetch = scoreboard.Run(app.Services, CancellationToken.None);

            Assert.Equal(TimeSpan.Zero, await app.RunAsync(fetch));
            Assert.Null(await fetch);
            Assert.Single(app.Provider.Attempts);
            Assert.Equal(LogLevel.Warning, Assert.Single(app.Logs, l => l.Category == scoreboard.Service.FullName && l.Level > LogLevel.Debug).Level);
            Assert.Empty(await app.SentEventsAsync());
        });
    }

    [Theory]
    [MemberData(nameof(ScoreboardNames))]
    public async Task Caller_cancellation_during_a_hung_attempt_stops_at_once_without_a_retry_or_a_log(string name)
    {
        var scoreboard = Scoreboards[name];
        await UsingAsync(new ClockedApp(Answer.Hang), async app =>
        {
            using var caller = new CancellationTokenSource();

            var fetch = scoreboard.Run(app.Services, caller.Token);
            await app.RunUntilAsync(fetch, () => app.Clock.Elapsed >= AttemptTimeout / 2, AttemptTimeout);
            caller.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch.WaitAsync(Guard));
            await AssertNothingRetriesAsync(app, fetch);
            Assert.Equal(AttemptTimeout / 2, Assert.Single(app.Provider.Attempts).Ended);
            Assert.DoesNotContain(app.Logs, l => l.Level >= LogLevel.Warning);
            Assert.Empty(await app.SentEventsAsync());
        });
    }

    [Theory]
    [MemberData(nameof(ScoreboardNames))]
    public async Task Caller_cancellation_during_the_retry_delay_stops_at_once_without_a_retry_or_a_log(string name)
    {
        var scoreboard = Scoreboards[name];
        await UsingAsync(new ClockedApp(Answer.Hang), async app =>
        {
            using var caller = new CancellationTokenSource();

            var fetch = scoreboard.Run(app.Services, caller.Token);
            await app.RunUntilAsync(fetch, () => app.Provider.Attempts is [{ Ended: not null }], AttemptTimeout * 2);
            caller.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch.WaitAsync(Guard));
            await AssertNothingRetriesAsync(app, fetch);
            Assert.Equal(AttemptTimeout, Assert.Single(app.Provider.Attempts).Duration);
            Assert.DoesNotContain(app.Logs, l => l.Category == scoreboard.Service.FullName && l.Level > LogLevel.Debug);
            Assert.Empty(await app.SentEventsAsync());
        });
    }

    private static async Task UsingAsync<T>(T resource, Func<T, Task> body) where T : IAsyncDisposable
    {
        try
        {
            await body(resource);
        }
        finally
        {
            await resource.DisposeAsync();
        }
    }

    private static async Task AssertNothingRetriesAsync(ClockedApp app, Task fetch)
    {
        Assert.Equal(0, app.Clock.LiveTimers);
        var attempts = app.Provider.Attempts.Count;
        await app.RunUntilAsync(fetch, () => false, TotalRequestTimeout);
        Assert.Equal(attempts, app.Provider.Attempts.Count);
    }

    private static void AssertHungAttempts(IReadOnlyList<Attempt> attempts, TimeSpan started = default)
    {
        Assert.Equal(HungAttemptsWithinTotal, attempts.Count);
        Assert.All(attempts.SkipLast(1), attempt => Assert.Equal(AttemptTimeout, attempt.Duration));
        var last = attempts[^1];
        Assert.Equal(started + TotalRequestTimeout, last.Ended);
        Assert.True(last.Duration < AttemptTimeout, $"the last attempt ran {last.Duration}, so the total timeout restarted with a retry");
    }

    private sealed record Fetch(Type Service, string Template, Func<IServiceProvider, CancellationToken, Task<object?>> Run);

    private sealed record SentEvent(string? Logger, string? Level, string? Template, string?[] ExceptionTypes);

    private sealed record LoggedEntry(string Category, LogLevel Level, string Message);

    private sealed record Attempt(TimeSpan Entered, TimeSpan? Ended = null, int TimersAtEnd = 0)
    {
        public TimeSpan? Duration => Ended - Entered;
    }

    private abstract record Answer
    {
        public static readonly Answer Hang = new Hanging();
        public static readonly Answer Unreachable = new Failing();

        public static Answer Status(HttpStatusCode status) => new Reply(status);

        public sealed record Hanging : Answer;
        public sealed record Failing : Answer;
        public sealed record Reply(HttpStatusCode Code) : Answer;
    }

    private sealed class SteppedClock(TimeSpan descheduleAfterArming) : FakeTimeProvider(Origin), IAsyncDisposable
    {
        private static readonly DateTimeOffset Origin = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
        private readonly List<TrackedTimer> timers = [];
        private readonly TaskCompletionSource returned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool lent;

        public TimeSpan Elapsed => GetUtcNow() - Origin;

        public Task Returned => lent ? returned.Task : Task.CompletedTask;

        public TimeProvider Lend()
        {
            lent = true;
            return this;
        }

        public ValueTask DisposeAsync()
        {
            returned.TrySetResult();
            return default;
        }

        public int TimersCreated
        {
            get { lock (timers) return timers.Count; }
        }

        public int LiveTimers
        {
            get { lock (timers) return timers.Count(t => t.Live); }
        }

        public bool AwaitsFirstTimerSince(int created)
        {
            lock (timers)
            {
                var since = timers.Skip(created).ToArray();
                return since.Any(t => t.Live) && !since.Any(t => t.HasFired);
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new TrackedTimer(descheduleAfterArming);
            timer.Arm(dueTime, period);
            timer.Inner = base.CreateTimer(s =>
            {
                timer.Fired();
                callback(s);
            }, state, dueTime, period);
            lock (timers) timers.Add(timer);
            timer.Deschedule(dueTime);
            return timer;
        }

        private sealed class TrackedTimer(TimeSpan descheduleAfterArming) : ITimer
        {
            private TimeSpan period;
            private volatile bool live;
            private volatile bool fired;

            public ITimer Inner { get; set; } = null!;

            public bool Live => live;

            public bool HasFired => fired;

            public void Arm(TimeSpan dueTime, TimeSpan period)
            {
                this.period = period;
                live = dueTime != Timeout.InfiniteTimeSpan;
            }

            public void Deschedule(TimeSpan dueTime)
            {
                if (dueTime != Timeout.InfiniteTimeSpan && descheduleAfterArming > TimeSpan.Zero) Thread.Sleep(descheduleAfterArming);
            }

            public void Fired()
            {
                fired = true;
                live = period != Timeout.InfiniteTimeSpan && period != TimeSpan.Zero;
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Arm(dueTime, period);
                var changed = Inner.Change(dueTime, period);
                Deschedule(dueTime);
                return changed;
            }

            public void Dispose()
            {
                live = false;
                Inner.Dispose();
            }

            public ValueTask DisposeAsync()
            {
                live = false;
                return Inner.DisposeAsync();
            }
        }
    }

    private sealed class FailingDisposal(Exception failure) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.FromException(failure);
    }

    private sealed class LateTimerDisposal(TimeProvider clock) : IAsyncDisposable
    {
        private static readonly TimeSpan RealWork = TimeSpan.FromMilliseconds(200);

        public bool Finished { get; private set; }

        public async ValueTask DisposeAsync()
        {
            await Task.Delay(RealWork);
            await Task.Delay(DisposalStep, clock);
            Finished = true;
        }
    }

    private sealed class ClockedApp : IAsyncDisposable
    {
        private readonly AppFactory entry = new();
        private readonly WebApplicationFactory<Program> app;
        private readonly SentTransport transport = new();
        private readonly LogRecorder logs = new();

        public ClockedApp(Answer answer, Action<IServiceCollection>? configure = null, TimeSpan descheduleAfterArming = default)
        {
            Clock = new SteppedClock(descheduleAfterArming);
            Provider = new ControlledProvider(answer, Clock);
            app = entry.WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.ConfigureServices(services =>
                {
                    foreach (var worker in services.Where(s => s.ServiceType == typeof(IHostedService)
                        && s.ImplementationType?.Namespace?.StartsWith("backend", StringComparison.Ordinal) == true).ToArray())
                        services.Remove(worker);
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(_ => Clock.Lend());
                    services.ConfigureAll<HttpClientFactoryOptions>(options =>
                        options.HttpMessageHandlerBuilderActions.Add(handlers => handlers.PrimaryHandler = Provider));
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

        public SteppedClock Clock { get; }

        public async Task<TimeSpan> RunAsync(Task work)
        {
            await RunUntilAsync(work, () => work.IsCompleted, TotalRequestTimeout * 2);
            Assert.True(work.IsCompleted, $"still running after {Clock.Elapsed}: {string.Join(", ", Provider.Attempts)}");
            return Clock.Elapsed;
        }

        public async Task RunUntilAsync(Task work, Func<bool> done, TimeSpan limit)
        {
            var end = Clock.Elapsed + limit;
            while (true)
            {
                await WaitForTimerAsync(work);
                if (done() || Clock.Elapsed >= end) return;
                Clock.Advance(Step);
            }
        }

        private async Task WaitForTimerAsync(Task work)
        {
            var deadline = DateTime.UtcNow + Guard;
            while (!work.IsCompleted && !WaitingOnTimer())
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"nothing waited on a timer at {Clock.Elapsed}");
                await Task.Delay(1);
            }
        }

        private bool WaitingOnTimer() => Provider.Attempts switch
        {
            [] => false,
            [.., { Ended: null }] => Provider.Hangs,
            [.., var last] => Clock.AwaitsFirstTimerSince(last.TimersAtEnd)
        };

        public ControlledProvider Provider { get; }

        public IServiceProvider Services => app.Services;

        public IEnumerable<LoggedEntry> Logs => logs.Entries;

        public async Task<IReadOnlyCollection<SentEvent>> SentEventsAsync()
        {
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));
            return transport.Events.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            var disposing = Task.Run(async () =>
            {
                await app.DisposeAsync();
                await entry.DisposeAsync();
                await Clock.Returned;
            });
            var deadline = DateTime.UtcNow + Guard;
            while (!disposing.IsCompleted || Clock.LiveTimers > 0)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"still disposing at {Clock.Elapsed} with {Clock.LiveTimers} live timers");
                Clock.Advance(DisposalStep);
                await Task.Delay(1);
            }
            await disposing;
            Provider.Dispose();
        }
    }

    private sealed class ControlledProvider(Answer answer, SteppedClock clock) : HttpMessageHandler
    {
        private readonly List<Attempt> attempts = [];

        public bool Hangs => answer is Answer.Hanging;

        public IReadOnlyList<Attempt> Attempts
        {
            get { lock (attempts) return attempts.ToArray(); }
        }

        public void Reset()
        {
            lock (attempts) attempts.Clear();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int index;
            lock (attempts)
            {
                index = attempts.Count;
                attempts.Add(new Attempt(clock.Elapsed));
            }
            void End()
            {
                lock (attempts) attempts[index] = attempts[index] with { Ended = clock.Elapsed, TimersAtEnd = clock.TimersCreated };
            }
            switch (answer)
            {
                case Answer.Hanging:
                    var hung = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                    await using (cancellationToken.Register(() =>
                    {
                        End();
                        hung.TrySetCanceled(cancellationToken);
                    }))
                        return await hung.Task;
                case Answer.Failing:
                    End();
                    throw new HttpRequestException("synthetic provider connection failure");
                case Answer.Reply reply:
                    End();
                    return new HttpResponseMessage(reply.Code) { Content = new StringContent("{}") };
                default:
                    throw new InvalidOperationException(answer.ToString());
            }
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
                entries.Enqueue(new LoggedEntry(category, logLevel, formatter(state, exception)));
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
