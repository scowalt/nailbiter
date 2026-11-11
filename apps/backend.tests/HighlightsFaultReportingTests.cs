using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text.Json;
using backend.Data;
using backend.Models;
using backend.Services;
using backend.Services.Highlights;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;
using Testcontainers.PostgreSql;
using Xunit;

namespace backend.tests;

[Collection(nameof(SentryGlobalHubCollection))]
public sealed class HighlightsFaultReportingTests(HighlightsFaultReportingTests.Database database) : IClassFixture<HighlightsFaultReportingTests.Database>
{
    private const string Fault = "backend.Services.Highlights.HighlightsFault";
    private const string LeakVideo = "leakVideo01";
    private const string LeakUrl = "https://www.youtube.com/watch?v=leakVideo01";
    private const string LeakTitle = "Sentinel Leaked Recap Title";
    private const string ApiKey = "sentinel-not-live";
    private static readonly string Leak = $"failed for {LeakUrl} ({LeakTitle})";
    private static readonly string[] Forbidden = [LeakVideo, LeakUrl, LeakTitle, ApiKey];
    private const string Channels = """{"items":[{"id":"UCoLrcjPV5PbUrUyXq5mjc_A","contentDetails":{"relatedPlaylists":{"uploads":"fixture-uploads"}}}]}""";
    private const string QuotaExceeded = """{"error":{"errors":[{"reason":"quotaExceeded"}]}}""";

    [Fact]
    public async Task Detail_own_fault_is_reported_once_without_its_text_and_the_page_still_renders()
    {
        var id = await database.AddCompletedGameAsync("fault-detail");
        var failing = false;
        await using var app = new App(database.ConnectionString, services =>
            services.PostConfigure<HighlightsWorkerOptions>(_ => { if (failing) throw new InvalidOperationException(Leak); }));
        failing = true;

        using var response = await app.Client.GetAsync($"/game/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertReportedOnce(app, await app.SentEventsAsync(expected: 1), typeof(HighlightsApplicationService), "System.InvalidOperationException");
    }

    [Fact]
    public async Task Unreadable_stored_identity_evidence_is_reported_once_and_the_page_still_renders()
    {
        var id = await database.AddCompletedGameAsync("fault-stored-identity", game => game.HighlightsIdentityEvidenceJson = $$"""{"GamePk":"{{LeakUrl}}""");
        await using var app = new App(database.ConnectionString);

        using var response = await app.Client.GetAsync($"/game/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertReportedOnce(app, await app.SentEventsAsync(expected: 1), typeof(HighlightsApplicationService), "System.Text.Json.");
    }

    [Fact]
    public async Task Unreadable_stored_selection_evidence_is_reported_once_and_the_player_stays_hidden()
    {
        var id = await database.AddSelectedGameAsync("fault-stored-selection", 849901, $$"""{"GamePk":"{{LeakUrl}}""");
        await using var app = new App(database.ConnectionString);

        using var response = await app.Client.GetAsync($"/game/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(LeakVideo, await response.Content.ReadAsStringAsync());
        AssertReportedOnce(app, await app.SentEventsAsync(expected: 1), typeof(HighlightsApplicationService), "System.Text.Json.JsonException");
    }

    [Fact]
    public async Task Unreadable_stored_completion_evidence_is_reported_once_and_the_page_still_renders()
    {
        var id = await database.AddCompletedGameAsync("fault-stored-completion", game => game.EspnCompletionEvidenceJson = $$"""{"EventId":"{{LeakUrl}} {{LeakTitle}}",""");
        await using var app = new App(database.ConnectionString);

        using var response = await app.Client.GetAsync($"/game/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertReportedOnce(app, await app.SentEventsAsync(expected: 1), typeof(HighlightsApplicationService), "System.Text.Json.JsonException");
    }

    [Fact]
    public async Task Unreadable_stored_completion_and_identity_evidence_are_each_reported_once_per_view()
    {
        var id = await database.AddCompletedGameAsync("fault-stored-both", game =>
        {
            game.EspnCompletionEvidenceJson = $$"""{"EventId":"{{LeakUrl}}",""";
            game.HighlightsIdentityEvidenceJson = $$"""{"GamePk":"{{LeakTitle}}""";
        });
        await using var app = new App(database.ConnectionString);

        using var response = await app.Client.GetAsync($"/game/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertLoggedAsync(app, typeof(HighlightsApplicationService), LogLevel.Error, LogLevel.Error);
        var errors = app.Logs.Entries.Where(e => e.Level == LogLevel.Error && e.Category == typeof(HighlightsApplicationService).FullName).ToArray();
        Assert.Single(errors, e => e.Text.Contains(nameof(Game.EspnCompletionEvidenceJson), StringComparison.Ordinal));
        Assert.Single(errors, e => e.Text.Contains(nameof(Game.HighlightsIdentityEvidenceJson), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Operator_inspect_of_unreadable_stored_completion_evidence_is_reported_once()
    {
        var id = await database.AddCompletedGameAsync("fault-stored-completion-cli", game => game.EspnCompletionEvidenceJson = $$"""{"EventId":"{{LeakUrl}}",""");
        await using var app = new App(database.ConnectionString);

        var (exit, output) = await app.OperateAsync("inspect", id);

        Assert.Equal(0, exit);
        Assert.Contains("\"IsActuallyCompleted\":false", output);
        AssertReportedOnce(app, await app.SentEventsAsync(expected: 1), typeof(HighlightsApplicationService), "System.Text.Json.JsonException");
    }

    [Fact]
    public async Task Lifecycle_rewrites_unreadable_completion_evidence_before_reading_it()
    {
        var id = await database.AddCompletedGameAsync("fault-stored-completion-lifecycle", game => game.EspnCompletionEvidenceJson = $$"""{"EventId":"{{LeakUrl}}",""");
        await using var app = new App(database.ConnectionString, services => services.Identity(VerifiedIdentity(id, "\"gamePk\":849819", "\"gamePk\":849903")));

        Assert.Equal("Processed", (await app.ProcessAsync([id])).Reason);
        using var response = await app.Client.GetAsync($"/game/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await app.SentEventsAsync(expected: 0));
        Assert.DoesNotContain(app.Logs.Entries, e => e.Level >= LogLevel.Warning && e.Category.StartsWith("backend.Services.Highlights", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Readable_stored_selection_still_shows_the_player()
    {
        var id = await database.AddSelectedGameAsync("fault-stored-valid", 849902, null);
        await using var app = new App(database.ConnectionString);

        using var response = await app.Client.GetAsync($"/game/{id}");

        Assert.Contains(LeakVideo, await response.Content.ReadAsStringAsync());
        Assert.Empty(await app.SentEventsAsync(expected: 0));
    }

    [Fact]
    public async Task Lifecycle_own_fault_is_reported_once_without_its_text_and_the_next_run_proceeds()
    {
        var failing = false;
        await using var app = new App(database.ConnectionString, services =>
            services.PostConfigure<HighlightsWorkerOptions>(_ => { if (failing) throw new InvalidOperationException(Leak); }));
        failing = true;

        var first = await app.RunLifecycleAsync();
        var firstEvents = await app.SentEventsAsync(expected: 1);
        var second = await app.RunLifecycleAsync();

        Assert.Equal(("OperationFailure", "OperationFailure"), (first.Reason, second.Reason));
        AssertReportedOnce(app, firstEvents, typeof(HighlightsLifecycleService), "System.InvalidOperationException");
        Assert.Equal(2, (await app.SentEventsAsync(expected: 2)).Count);
    }

    [Fact]
    public async Task Lifecycle_unlock_failure_after_a_finished_run_is_reported_once()
    {
        await using var app = new App(database.ConnectionString, services =>
            services.PostConfigure<HighlightsWorkerOptions>(options => { database.TerminateLifecycleLockHolder(); options.Enabled = false; }));

        var result = await app.RunLifecycleAsync();

        Assert.Equal("WorkerDisabled", result.Reason);
        var reported = Assert.Single(await app.SentEventsAsync(expected: 1));
        Assert.Equal((typeof(HighlightsLifecycleService).FullName, "error"), (reported.Logger, reported.Level));
        Assert.StartsWith("Npgsql.", Assert.Single(reported.Exceptions).Type);
        Assert.Contains("UnlockFailure", reported.Json);
    }

    [Fact]
    public async Task Lifecycle_unlock_failure_after_a_reported_fault_is_not_reported_again()
    {
        await using var app = new App(database.ConnectionString, services =>
            services.PostConfigure<HighlightsWorkerOptions>(_ => { database.TerminateLifecycleLockHolder(); throw new InvalidOperationException(Leak); }));

        var result = await app.RunLifecycleAsync();

        Assert.Equal("OperationFailure", result.Reason);
        AssertReportedOnce(app, await app.SentEventsAsync(expected: 2), typeof(HighlightsLifecycleService), "System.InvalidOperationException");
    }

    [Fact]
    public async Task Lifecycle_loop_own_fault_is_reported_once_per_cycle_and_the_loop_keeps_running()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 3, 0, 0, TimeSpan.Zero));
        var attempts = 0;
        await using var app = new App(database.ConnectionString, services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
            services.RemoveAll<HighlightsLifecycleService>();
            services.AddScoped<HighlightsLifecycleService>(_ =>
            {
                Interlocked.Increment(ref attempts);
                throw new InvalidOperationException(Leak);
            });
        }, worker: typeof(HighlightsBackgroundService));
        _ = app.Client;

        var first = await app.SentEventsAsync(expected: 1);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (Volatile.Read(ref attempts) < 2 && !deadline.IsCancellationRequested)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(50, CancellationToken.None);
        }

        AssertReportedOnce(app, first, typeof(HighlightsBackgroundService), "System.InvalidOperationException");
        Assert.True(Volatile.Read(ref attempts) >= 2);
        Assert.Equal(Volatile.Read(ref attempts), (await app.SentEventsAsync(expected: attempts)).Count);
    }

    [Fact]
    public async Task YouTube_own_fault_is_reported_once_without_its_text()
    {
        await using var app = new App(database.ConnectionString, services =>
            services.YouTube((request, _) => request.RequestUri!.AbsolutePath.EndsWith("/channels")
                ? Task.FromResult(Ok(Channels)) : throw new InvalidOperationException(Leak)));

        var result = await app.ValidateAsync();

        Assert.Equal(YouTubeOutcome.InvalidResponse, result.Outcome);
        AssertReportedOnce(app, await app.SentEventsAsync(expected: 1), typeof(YouTubeHighlightsClient), "System.InvalidOperationException");
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{}", YouTubeOutcome.TemporaryFailure, LogLevel.Error)]
    [InlineData(HttpStatusCode.TooManyRequests, "{}", YouTubeOutcome.Throttled, LogLevel.Error)]
    [InlineData(HttpStatusCode.NotFound, "{}", YouTubeOutcome.TemporaryFailure, LogLevel.Warning)]
    [InlineData(HttpStatusCode.Forbidden, QuotaExceeded, YouTubeOutcome.QuotaExhausted, LogLevel.Warning)]
    [InlineData(HttpStatusCode.Unauthorized, "{}", YouTubeOutcome.InvalidConfiguration, LogLevel.Warning)]
    [InlineData(HttpStatusCode.OK, "not json " + LeakUrl, YouTubeOutcome.InvalidResponse, LogLevel.Warning)]
    [InlineData(HttpStatusCode.OK, """{"items":"not a list"}""", YouTubeOutcome.InvalidResponse, LogLevel.Warning)]
    [InlineData(HttpStatusCode.OK, """{"items":[{"id":"leakVideo01","snippet":{"channelId":"UCoLrcjPV5PbUrUyXq5mjc_A","title":"Sentinel Leaked Recap Title","description":"","publishedAt":"2026-10-07T02:00:00Z","liveBroadcastContent":"none"},"contentDetails":{"duration":"eight minutes"},"status":{"privacyStatus":"public","uploadStatus":"processed","embeddable":true,"madeForKids":false}}]}""", YouTubeOutcome.InvalidResponse, LogLevel.Warning)]
    public async Task YouTube_provider_answer_is_logged_at_its_level(HttpStatusCode status, string body, YouTubeOutcome outcome, LogLevel level)
    {
        await using var app = new App(database.ConnectionString, services =>
            services.YouTube((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/channels")
                ? Ok(Channels) : new HttpResponseMessage(status) { Content = new StringContent(body) })));

        var result = await app.ValidateAsync();

        Assert.Equal(outcome, result.Outcome);
        await AssertLoggedAsync(app, typeof(YouTubeHighlightsClient), level);
    }

    [Fact]
    public async Task YouTube_connection_failure_is_reported_once()
    {
        await using var app = new App(database.ConnectionString, services =>
            services.YouTube((_, _) => throw new HttpRequestException(Leak)));

        Assert.Equal(YouTubeOutcome.TemporaryFailure, (await app.ValidateAsync()).Outcome);
        AssertReportedOnce(app, await app.SentEventsAsync(expected: 1), typeof(YouTubeHighlightsClient), "System.Net.Http.HttpRequestException");
    }

    [Fact]
    public async Task YouTube_rejected_request_exception_is_a_warning()
    {
        await using var app = new App(database.ConnectionString, services =>
            services.YouTube((_, _) => throw new HttpRequestException(Leak, null, HttpStatusCode.NotFound)));

        Assert.Equal(YouTubeOutcome.TemporaryFailure, (await app.ValidateAsync()).Outcome);
        await AssertLoggedAsync(app, typeof(YouTubeHighlightsClient), LogLevel.Warning);
    }

    [Fact]
    public async Task YouTube_client_timeout_is_reported_once()
    {
        await using var app = new App(database.ConnectionString, services =>
            services.YouTube(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Ok("{}"); }, TimeSpan.FromMilliseconds(200)));

        Assert.Equal(YouTubeOutcome.TemporaryFailure, (await app.ValidateAsync()).Outcome);
        AssertReportedOnce(app, await app.SentEventsAsync(expected: 1), typeof(YouTubeHighlightsClient), "System.Threading.Tasks.TaskCanceledException");
    }

    [Fact]
    public async Task YouTube_caller_cancellation_is_rethrown_and_not_reported()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = new App(database.ConnectionString, services =>
            services.YouTube(async (_, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return Ok("{}"); }));
        using var caller = new CancellationTokenSource();

        var validation = app.ValidateAsync(caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => validation);
        Assert.Empty(await app.SentEventsAsync(expected: 0));
        Assert.DoesNotContain(app.Logs.Entries, e => e.Level >= LogLevel.Warning && e.Category.StartsWith("backend.Services.Highlights", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{}", LogLevel.Error)]
    [InlineData(HttpStatusCode.NotFound, "{}", LogLevel.Warning)]
    [InlineData(HttpStatusCode.OK, "not json " + LeakUrl, LogLevel.Warning)]
    [InlineData(HttpStatusCode.OK, """{"header":{"id":7}}""", LogLevel.Warning)]
    public async Task Identity_provider_answer_is_logged_at_its_level_and_every_game_is_still_tried(HttpStatusCode status, string body, LogLevel level)
    {
        var games = await database.AddFinalGamesAsync($"fault-identity-{(int)status}-{body.Length}", 2);
        var requests = 0;
        await using var app = new App(database.ConnectionString, services => services.Identity((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }));

        var result = await app.ProcessAsync(games);

        Assert.Equal("Processed", result.Reason);
        Assert.Equal(2, requests);
        await AssertLoggedAsync(app, typeof(MlbHighlightsIdentityClient), level, LogLevel.Warning);
    }

    [Fact]
    public async Task Identity_connection_failure_is_reported_once_per_pass()
    {
        var games = await database.AddFinalGamesAsync("fault-identity-connection", 3);
        await using var app = new App(database.ConnectionString, services => services.Identity((_, _) => throw new HttpRequestException(Leak)));

        Assert.Equal("Processed", (await app.ProcessAsync(games)).Reason);
        AssertReportedOnce(app, await app.SentEventsAsync(expected: 1), typeof(MlbHighlightsIdentityClient), "System.Net.Http.HttpRequestException");
        Assert.Equal(2, app.Logs.Entries.Count(e => e.Level == LogLevel.Warning && e.Category == typeof(MlbHighlightsIdentityClient).FullName));
    }

    [Fact]
    public async Task Identity_client_timeout_is_reported_once()
    {
        var games = await database.AddFinalGamesAsync("fault-identity-timeout", 1);
        await using var app = new App(database.ConnectionString, services =>
            services.Identity(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Ok("{}"); }, TimeSpan.FromMilliseconds(200)));

        Assert.Equal("Processed", (await app.ProcessAsync(games)).Reason);
        AssertReportedOnce(app, await app.SentEventsAsync(expected: 1), typeof(MlbHighlightsIdentityClient), "System.Threading.Tasks.TaskCanceledException");
    }

    [Fact]
    public async Task Identity_caller_cancellation_is_rethrown_and_not_reported()
    {
        var games = await database.AddFinalGamesAsync("fault-identity-cancel", 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = new App(database.ConnectionString, services =>
            services.Identity(async (_, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return Ok("{}"); }));
        using var caller = new CancellationTokenSource();

        var processing = app.ProcessAsync(games, caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
        Assert.Empty(await app.SentEventsAsync(expected: 0));
        Assert.DoesNotContain(app.Logs.Entries, e => e.Level >= LogLevel.Warning && e.Category.StartsWith("backend.Services.Highlights", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Non_ascii_year_in_a_provider_description_is_not_a_fault()
    {
        var id = await database.AddCompletedGameAsync("fault-non-ascii-year");
        await using var app = new App(database.ConnectionString, services => services.Identity(VerifiedIdentity(id)));
        var now = DateTimeOffset.UtcNow;
        var video = new YouTubeVideoEvidence("asciiYear01", YouTubeAvailability.Playable, YouTubeHighlightsClient.ApprovedChannelId,
            "Dodgers vs. Braves Highlights (10/6/26) | MLB Highlights", "Single game recap 20\u0662\u0666", now.AddDays(-1),
            TimeSpan.FromMinutes(8), false, [], [], now.AddMinutes(-1));

        var result = await app.ProcessAsync([id], [video]);

        Assert.Equal(("Processed", 1), (result.Reason, result.Selected));
        Assert.Empty(await app.SentEventsAsync(expected: 0));
    }

    [Theory]
    [InlineData("\"season\":\"2026\"", "\"season\":\"\\u0662\\u0660\\u0662\\u0666\"")]
    [InlineData("\"season\":\"2026\"", "\"season\":null")]
    [InlineData("\"gameDate\":\"2026-10-06T22:00:00Z\"", "\"gameDate\":null")]
    [InlineData("\"officialDate\":\"2026-10-06\"", "\"officialDate\":null")]
    [InlineData("\"date\":\"2026-10-06T22:00:00Z\"", "\"date\":null")]
    public async Task Identity_malformed_field_is_a_warning_and_the_pass_continues(string field, string malformed)
    {
        var id = await database.AddCompletedGameAsync($"fault-identity-field-{Guid.NewGuid():N}");
        await using var app = new App(database.ConnectionString, services => services.Identity(VerifiedIdentity(id, field, malformed)));

        Assert.Equal("Processed", (await app.ProcessAsync([id])).Reason);
        await AssertLoggedAsync(app, typeof(MlbHighlightsIdentityClient), LogLevel.Warning);
    }

    [Theory]
    [InlineData("\"gamePk\":849819", "\"gamePk\":\"849819\"")]
    [InlineData("\"gameNumber\":1", "\"gameNumber\":2147483648")]
    [InlineData("\"homeAway\":\"home\"", "\"homeAway\":5")]
    [InlineData("{\"type\":{\"name\":\"STATUS_FINAL\",\"state\":\"post\",\"completed\":true}}", "{\"type\":\"final\"}")]
    [InlineData("{\"home\":{\"team\":{\"id\":144}},\"away\":{\"team\":{\"id\":119}}}", "[]")]
    [InlineData("\"officialDate\":\"2026-10-06\"", "\"officialDate\":\"October 6\"")]
    [InlineData("\"gameDate\":\"2026-10-06T22:00:00Z\"", "\"gameDate\":\"not a date\"")]
    public async Task Identity_field_of_the_wrong_shape_is_a_warning(string field, string malformed)
    {
        var id = await database.AddCompletedGameAsync($"fault-identity-shape-{Guid.NewGuid():N}");
        await using var app = new App(database.ConnectionString, services => services.Identity(VerifiedIdentity(id, field, malformed)));

        Assert.Equal("Processed", (await app.ProcessAsync([id])).Reason);
        await AssertLoggedAsync(app, typeof(MlbHighlightsIdentityClient), LogLevel.Warning);
    }

    [Fact]
    public async Task Identity_own_fault_is_reported_once_without_its_text_and_every_game_is_still_tried()
    {
        var games = await database.AddFinalGamesAsync("fault-identity-own", 2);
        var requests = 0;
        await using var app = new App(database.ConnectionString, services => services.Identity((_, _) =>
        {
            if (Interlocked.Increment(ref requests) == 1) throw new InvalidOperationException(Leak);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }));

        Assert.Equal("Processed", (await app.ProcessAsync(games)).Reason);
        Assert.Equal(2, requests);
        await AssertLoggedAsync(app, typeof(MlbHighlightsIdentityClient), LogLevel.Error, LogLevel.Warning);
        Assert.Contains("System.InvalidOperationException", Assert.Single(await app.SentEventsAsync(expected: 1)).Exceptions[0].Value);
    }

    [Fact]
    public async Task Every_identity_own_fault_in_a_pass_is_its_own_error()
    {
        var games = await database.AddFinalGamesAsync("fault-identity-own-each", 2);
        var requests = 0;
        await using var app = new App(database.ConnectionString, services => services.Identity((_, _) =>
        {
            Interlocked.Increment(ref requests);
            throw new InvalidOperationException(Leak);
        }));

        Assert.Equal("Processed", (await app.ProcessAsync(games)).Reason);

        Assert.Equal(2, requests);
        await AssertLoggedAsync(app, typeof(MlbHighlightsIdentityClient), LogLevel.Error, LogLevel.Error);
        Assert.All(await app.SentEventsAsync(expected: 2), sent => Assert.Contains("System.InvalidOperationException", sent.Exceptions[0].Value));
    }

    [Fact]
    public async Task Identity_duplicate_team_is_reported_once()
    {
        var id = await database.AddCompletedGameAsync("fault-identity-duplicate-team");
        await using var app = new App(database.ConnectionString, services => services.Identity(VerifiedIdentity(id)));

        var before = await database.StoredAsync(id);

        using (IdentityTeams.Duplicate("ATL", over: "BOS"))
            Assert.Equal("Processed", (await app.ProcessAsync([id])).Reason);

        AssertReportedOnce(app, await app.SentEventsAsync(expected: 1), typeof(MlbHighlightsIdentityClient), "System.InvalidOperationException");
        var after = await database.StoredAsync(id);
        Assert.Equal((before.GamePk, before.IdentityEvidence, before.CompletionEvidence), (after.GamePk, after.IdentityEvidence, after.CompletionEvidence));
        Assert.Equal((HighlightsAvailability.Unknown, null), (after.Availability, after.UnavailableReason));
    }

    [Theory]
    [InlineData(typeof(KeyNotFoundException))]
    [InlineData(typeof(FormatException))]
    [InlineData(typeof(OverflowException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task Identity_own_fault_of_any_type_is_one_error_and_no_warning(Type type)
    {
        var id = await database.AddCompletedGameAsync($"fault-identity-own-{type.Name}");
        await using var app = new App(database.ConnectionString, services => services.Identity((_, _) => throw (Exception)Activator.CreateInstance(type, Leak)!));

        Assert.Equal("Processed", (await app.ProcessAsync([id])).Reason);

        await AssertLoggedAsync(app, typeof(MlbHighlightsIdentityClient), LogLevel.Error);
        Assert.Contains(type.FullName!, Assert.Single(await app.SentEventsAsync(expected: 1)).Exceptions[0].Value);
    }

    [Theory]
    [InlineData(HighlightsOwnership.Manual)]
    [InlineData(HighlightsOwnership.Automatic)]
    public async Task Identity_own_fault_during_revalidation_keeps_a_playable_selection_and_its_stored_state(HighlightsOwnership ownership)
    {
        var video = $"ownFault{ownership.ToString()[0]}01";
        var id = await database.AddSelectedGameAsync($"fault-revalidate-own-{ownership}", 849910 + (int)ownership, null, ownership, video);
        var before = await database.StoredAsync(id);
        await using var app = new App(database.ConnectionString, services => services.Identity((_, _) => throw new InvalidOperationException(Leak)));

        Assert.Equal(new HighlightsApplicationResult(0, 0, "Revalidated"), await app.RevalidateAsync([Playable(video)]));

        AssertReportedOnce(app, await app.SentEventsAsync(expected: 1), typeof(MlbHighlightsIdentityClient), "System.InvalidOperationException");
        Assert.Equal(before, await database.StoredAsync(id));
        Assert.Equal(HighlightsAvailability.Playable, before.Availability);
    }

    [Theory]
    [InlineData(HighlightsOwnership.Manual)]
    [InlineData(HighlightsOwnership.Automatic)]
    public async Task Malformed_identity_body_during_revalidation_still_revokes_without_an_event(HighlightsOwnership ownership)
    {
        var video = $"malformd{ownership.ToString()[0]}01";
        var id = await database.AddSelectedGameAsync($"fault-revalidate-malformed-{ownership}", 849920 + (int)ownership, null, ownership, video);
        await using var app = new App(database.ConnectionString, services => services.Identity((_, _) => Task.FromResult(Ok("""{"header":5}"""))));

        Assert.Equal(new HighlightsApplicationResult(0, 1, "Revalidated"), await app.RevalidateAsync([Playable(video)]));

        await AssertLoggedAsync(app, typeof(MlbHighlightsIdentityClient), LogLevel.Warning);
        var after = await database.StoredAsync(id);
        Assert.Equal(HighlightsAvailability.Unavailable, after.Availability);
        Assert.Contains("\"FailureReason\":\"MalformedIdentity\"", after.IdentityEvidence);
    }

    private static YouTubeVideoEvidence Playable(string video)
    {
        var now = DateTimeOffset.UtcNow;
        return new(video, YouTubeAvailability.Playable, YouTubeHighlightsClient.ApprovedChannelId, LeakTitle, "Game recap",
            now.AddDays(-1), TimeSpan.FromMinutes(8), false, [], [], now.AddMinutes(-1));
    }

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> VerifiedIdentity(string id, string field = "", string replacement = "") =>
        (request, _) => Task.FromResult(Ok((request.RequestUri!.Host == "statsapi.mlb.com"
            ? """{"dates":[{"date":"2026-10-06","games":[{"gamePk":849819,"gameDate":"2026-10-06T22:00:00Z","officialDate":"2026-10-06","season":"2026","doubleHeader":"N","gameNumber":1,"status":{"abstractGameState":"Final","detailedState":"Final","codedGameState":"F"},"teams":{"home":{"team":{"id":144}},"away":{"team":{"id":119}}}}]}]}"""
            : $$$"""{"header":{"id":"{{{id}}}","season":{"year":2026},"competitions":[{"id":"{{{id}}}","date":"2026-10-06T22:00:00Z","status":{"type":{"name":"STATUS_FINAL","state":"post","completed":true}},"competitors":[{"homeAway":"home","team":{"id":"15","abbreviation":"ATL"}},{"homeAway":"away","team":{"id":"19","abbreviation":"LAD"}}]}]}}""")
            .Replace(field.Length == 0 ? "\0" : field, replacement)));

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static void AssertReportedOnce(App app, IReadOnlyCollection<SentEvent> sent, Type logger, string originalType)
    {
        var reported = Assert.Single(sent);
        Assert.Equal((logger.FullName, "error"), (reported.Logger, reported.Level));
        var exception = Assert.Single(reported.Exceptions);
        Assert.Equal(Fault, exception.Type);
        Assert.Contains(originalType, exception.Value);
        AssertNothingLeaked(app, sent);
    }

    private static async Task AssertLoggedAsync(App app, Type logger, params LogLevel[] levels)
    {
        var errors = levels.Count(level => level == LogLevel.Error);
        var sent = await app.SentEventsAsync(expected: errors);
        Assert.Equal(levels, app.Logs.Entries.Where(e => e.Level >= LogLevel.Warning && e.Category == logger.FullName).Select(e => e.Level));
        Assert.Equal(errors, sent.Count);
        Assert.All(sent, reported =>
        {
            Assert.Equal((logger.FullName, "error"), (reported.Logger, reported.Level));
            Assert.Equal(Fault, Assert.Single(reported.Exceptions).Type);
        });
        AssertNothingLeaked(app, sent);
    }

    private static void AssertNothingLeaked(App app, IReadOnlyCollection<SentEvent> sent)
    {
        foreach (var value in Forbidden)
        {
            Assert.All(sent, e => Assert.DoesNotContain(value, e.Json));
            Assert.All(app.Logs.Entries.Where(e => e.Category.StartsWith("backend.Services.Highlights", StringComparison.Ordinal)),
                e => Assert.DoesNotContain(value, e.Text));
        }
    }

    public sealed class Database : IAsyncLifetime
    {
        private readonly PostgreSqlContainer container = IsolatedPostgreSql.CreateContainer();

        public string ConnectionString => container.GetConnectionString();

        public async Task InitializeAsync()
        {
            await container.StartAsync();
            await using var db = Context();
            await db.Database.EnsureCreatedAsync();
        }

        public Task DisposeAsync() => container.DisposeAsync().AsTask();

        public void TerminateLifecycleLockHolder()
        {
            using var connection = new NpgsqlConnection(ConnectionString);
            connection.Open();
            const string holder = "FROM pg_locks WHERE locktype = 'advisory' AND classid = 651 AND objid = 647 AND objsubid = 2";
            using (var terminate = new NpgsqlCommand($"SELECT count(pg_terminate_backend(pid)) {holder}", connection))
                Assert.Equal(1L, terminate.ExecuteScalar());
            using var held = new NpgsqlCommand($"SELECT count(*) {holder}", connection);
            for (var attempt = 0; attempt < 100 && (long)held.ExecuteScalar()! > 0; attempt++) Thread.Sleep(50);
        }

        private ExcitementDbContext Context() => new(new DbContextOptionsBuilder<ExcitementDbContext>().UseNpgsql(ConnectionString).Options);

        private static Game FinalGame(string id) => new()
        {
            Id = id, Sport = "mlb", Status = "final", SeasonYear = 2026, SeasonType = 2,
            HomeTeam = "Atlanta Braves", AwayTeam = "Los Angeles Dodgers", HomeTeamAbbreviation = "ATL", AwayTeamAbbreviation = "LAD",
            HomeTeamId = "15", AwayTeamId = "19", ScheduledStart = new DateTime(2026, 10, 6, 22, 0, 0, DateTimeKind.Utc),
            HighlightsDiscoveryExpiresAt = DateTime.UtcNow.AddDays(2)
        };

        public async Task<string[]> AddFinalGamesAsync(string prefix, int count)
        {
            var ids = Enumerable.Range(1, count).Select(i => $"{prefix}-{i}").ToArray();
            await using var db = Context();
            db.Games.AddRange(ids.Select(FinalGame));
            await db.SaveChangesAsync();
            return ids;
        }

        public async Task<string> AddCompletedGameAsync(string id, Action<Game>? configure = null)
        {
            var game = FinalGame(id);
            var status = new EspnCompletionStatus("STATUS_FINAL", "post", true, null, null, null);
            game.EspnCompletionEvidenceJson = JsonSerializer.Serialize(new MlbCompletionEvidence(id, id, status, status, DateTime.UtcNow.AddHours(-1)));
            game.EspnCompletionObservedAt = DateTime.UtcNow.AddHours(-1);
            game.CompletionConfirmedAt = DateTime.UtcNow.AddHours(-1);
            configure?.Invoke(game);
            await using var db = Context();
            db.Games.Add(game);
            await db.SaveChangesAsync();
            return id;
        }

        public async Task<string> AddSelectedGameAsync(string id, long gamePk, string? selectionEvidence,
            HighlightsOwnership ownership = HighlightsOwnership.Manual, string video = LeakVideo)
        {
            var identity = $$"""{"GamePk":{{gamePk}},"OfficialDate":"2026-10-06","Season":2026,"HomeTeamId":144,"AwayTeamId":119,"DoubleHeader":"N","GameNumber":1,"ScheduledStart":"2026-10-06T22:00:00Z","SameDayContests":1,"ObservedAt":"2026-10-07T03:00:00Z"}""";
            await AddCompletedGameAsync(id, game =>
            {
                game.MlbGamePk = gamePk;
                game.MlbOfficialDate = new DateOnly(2026, 10, 6);
                game.HighlightsIdentityEvidenceJson = identity;
            });
            await using var db = Context();
            db.GameHighlights.Add(new GameHighlights
            {
                GameId = id, SelectedVideoId = video, Title = LeakTitle, Ownership = ownership,
                SelectedAt = DateTime.UtcNow.AddHours(-1), Availability = HighlightsAvailability.Playable,
                ChannelId = YouTubeHighlightsClient.ApprovedChannelId, MadeForKids = false, AllowedRegions = [], BlockedRegions = [],
                SelectionEvidenceJson = selectionEvidence
                    ?? $$"""{"GamePk":{{gamePk}},"OfficialDate":"2026-10-06","Season":2026,"Manual":{{(ownership == HighlightsOwnership.Manual ? "true" : "false")}}}""",
                ValidatedAt = DateTime.UtcNow.AddHours(-1), ProviderDataRefreshedAt = DateTime.UtcNow.AddHours(-1),
                ProviderDataExpiresAt = DateTime.UtcNow.AddDays(29)
            });
            await db.SaveChangesAsync();
            return id;
        }

        public async Task<Stored> StoredAsync(string id)
        {
            await using var db = Context();
            var game = await db.Games.AsNoTracking().SingleAsync(g => g.Id == id);
            var selection = await db.GameHighlights.AsNoTracking().SingleOrDefaultAsync(h => h.GameId == id);
            return new(game.MlbGamePk, game.MlbOfficialDate, game.HighlightsIdentityEvidenceJson, game.EspnCompletionEvidenceJson,
                selection?.SelectedVideoId, selection?.Ownership, selection?.Availability, selection?.UnavailableReason,
                selection?.SelectionEvidenceJson, selection?.ValidatedAt, selection?.Version);
        }
    }

    public sealed record Stored(long? GamePk, DateOnly? OfficialDate, string? IdentityEvidence, string? CompletionEvidence,
        string? VideoId, HighlightsOwnership? Ownership, HighlightsAvailability? Availability, string? UnavailableReason,
        string? SelectionEvidence, DateTime? ValidatedAt, uint? Version);

    private sealed class App : IAsyncDisposable
    {
        private readonly AppFactory entry = new();
        private readonly WebApplicationFactory<Program> app;
        private readonly SentTransport transport = new();
        private HttpClient? client;

        public App(string connectionString, Action<IServiceCollection>? configure = null, Type? worker = null)
        {
            app = entry.WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IDbContextOptionsConfiguration<ExcitementDbContext>>();
                    services.RemoveAll<DbContextOptions<ExcitementDbContext>>();
                    services.AddDbContext<ExcitementDbContext>(options => options.UseNpgsql(connectionString));
                    foreach (var other in services.Where(s => s.ServiceType == typeof(IHostedService)
                        && s.ImplementationType?.Namespace?.StartsWith("backend", StringComparison.Ordinal) == true).ToArray())
                        services.Remove(other);
                    if (worker is not null) services.AddSingleton(typeof(IHostedService), worker);
                    services.AddSingleton<ILoggerProvider>(Logs);
                    services.Configure<YouTubeHighlightsOptions>(o => { o.Enabled = true; o.ApiKey = ApiKey; });
                    services.Configure<HighlightsWorkerOptions>(o => o.Enabled = true);
                    services.PostConfigure<SentryAspNetCoreOptions>(options =>
                    {
                        options.Dsn = "https://public@sentry.invalid/1";
                        options.Transport = transport;
                    });
                    configure?.Invoke(services);
                });
            });
        }

        public Logs Logs { get; } = new();

        public HttpClient Client => client ??= app.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

        public async Task<HighlightsLifecycleResult> RunLifecycleAsync()
        {
            _ = Client;
            using var scope = app.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<HighlightsLifecycleService>().RunAsync();
        }

        public Task<YouTubeHighlightsResult> ValidateAsync(CancellationToken cancellationToken = default)
        {
            _ = Client;
            return app.Services.GetRequiredService<YouTubeHighlightsClient>().ValidateVideosAsync([LeakVideo], cancellationToken);
        }

        public Task<HighlightsApplicationResult> ProcessAsync(string[] games, CancellationToken cancellationToken = default) =>
            ProcessAsync(games, [], cancellationToken);

        public async Task<HighlightsApplicationResult> ProcessAsync(string[] games, YouTubeVideoEvidence[] videos, CancellationToken cancellationToken = default)
        {
            _ = Client;
            using var scope = app.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<HighlightsApplicationService>().ProcessCandidatesAsync(games, videos, cancellationToken: cancellationToken);
        }

        public async Task<HighlightsApplicationResult> RevalidateAsync(YouTubeVideoEvidence[] videos)
        {
            _ = Client;
            using var scope = app.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<HighlightsApplicationService>().RevalidateSelectionsAsync(videos);
        }

        public async Task<(int Exit, string Output)> OperateAsync(params string[] arguments)
        {
            _ = Client;
            using var scope = app.Services.CreateScope();
            using var output = new StringWriter();
            var exit = await scope.ServiceProvider.GetRequiredService<HighlightsOperatorCommand>().RunAsync(arguments, output);
            return (exit, output.ToString());
        }

        public async Task<IReadOnlyCollection<SentEvent>> SentEventsAsync(int expected)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(expected > 0 ? 15 : 1));
            while (transport.Events.Count < expected && !deadline.IsCancellationRequested)
                await Task.Delay(50, CancellationToken.None);
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));
            var events = transport.Events.ToArray();
            foreach (var orphan in events.Where(e => string.IsNullOrEmpty(e.Logger)))
                Console.WriteLine($"Logger-less Sentry event: level={orphan.Level} {orphan.Json}");
            return events;
        }

        public async ValueTask DisposeAsync()
        {
            client?.Dispose();
            await app.DisposeAsync();
            await entry.DisposeAsync();
        }
    }

    internal sealed record LogEntry(string Category, LogLevel Level, string Text);

    internal sealed class Logs : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Capture(categoryName, Entries);
        public void Dispose() { }

        private sealed class Capture(string category, ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new(category, level, formatter(state, exception) + exception));
        }
    }

    private sealed record SentEvent(string? Logger, string? Level, (string Type, string Value)[] Exceptions, string Json);

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
                var json = lines[++i];
                using var payload = JsonDocument.Parse(json);
                var root = payload.RootElement;
                var exceptions = root.TryGetProperty("exception", out var exception)
                    ? exception.GetProperty("values").EnumerateArray()
                        .Select(e => (e.GetProperty("type").GetString()!, e.TryGetProperty("value", out var v) ? v.GetString()! : "")).ToArray()
                    : [];
                Events.Enqueue(new SentEvent(Text(root, "logger"), Text(root, "level"), exceptions, json));
            }
        }

        private static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) ? value.GetString() : null;
    }
}

internal static class IdentityTeams
{
    public static IDisposable Duplicate(string abbreviation, string over)
    {
        var teams = (Array)typeof(MlbHighlightsIdentityClient).GetField("Teams", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var entries = teams.Cast<object>().ToArray();
        string Abbreviation(object team) => (string)team.GetType().GetProperty("Abbreviation")!.GetValue(team)!;
        var target = System.Array.FindIndex(entries, team => Abbreviation(team) == over);
        teams.SetValue(entries.Single(team => Abbreviation(team) == abbreviation), target);
        return new Restore(() => teams.SetValue(entries[target], target));
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}

internal static class HighlightsFaultProviders
{
    public static void YouTube(this IServiceCollection services, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond, TimeSpan? timeout = null) =>
        services.AddHttpClient(YouTubeHighlightsClient.HttpClientName)
            .ConfigureHttpClient(client => client.Timeout = timeout ?? TimeSpan.FromSeconds(100))
            .ConfigurePrimaryHttpMessageHandler(() => new Handler(respond));

    public static void Identity(this IServiceCollection services, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond, TimeSpan? timeout = null) =>
        services.AddHttpClient(MlbHighlightsIdentityClient.HttpClientName)
            .ConfigureHttpClient(client => client.Timeout = timeout ?? TimeSpan.FromSeconds(100))
            .ConfigurePrimaryHttpMessageHandler(() => new Handler(respond));

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
}
