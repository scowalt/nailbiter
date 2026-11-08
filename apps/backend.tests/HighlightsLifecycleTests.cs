using System.Data.Common;
using System.Net;
using System.Text.Json.Nodes;
using backend.Data;
using backend.Models;
using backend.Services;
using backend.Services.Highlights;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace backend.tests;

public sealed class HighlightsLifecycleTests : IAsyncLifetime
{
    private readonly IsolatedPostgreSql database = new IsolatedPostgreSql();
    private readonly AppFactory entry = new();
    private readonly Clock clock = new();
    private WebApplicationFactory<Program> host = null!;
    private bool enabled = true;
    private bool workerEnabled = true;
    private bool launch;
    private bool startHosted;
    private int limit = 1000;
    private int maxGames = 100;
    private int maxCatchupPages = 16;
    private string? apiKey = "sentinel-not-live";
    private bool malformed;
    private bool quotaError;
    private int uploadPages = 1;
    private int maxUploadPages = 1;
    private int youtubeCalls;
    private string? continuation;
    private HttpStatusCode status = HttpStatusCode.OK;
    private bool removed;
    private string uploadVideoId = "fixtureVid1";
    private string? removedVideoId;
    private bool regionLimited;
    private bool channelUnverified;
    private bool pendingUpload;
    private readonly System.Collections.Concurrent.ConcurrentQueue<DateOnly> requestDays = new();
    private bool identityFailure;
    private Func<CancellationToken, Task>? providerBarrier;
    private DbTransactionInterceptor? transactionBoundary;
    private readonly List<string> tokens = [];

    private sealed class Clock : TimeProvider
    {
        private readonly FakeTimeProvider provider = new(new DateTimeOffset(2026, 10, 7, 3, 0, 0, TimeSpan.Zero));
        public TaskCompletionSource DelayScheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTimeOffset Now { get => provider.GetUtcNow(); set => provider.SetUtcNow(value); }
        public override DateTimeOffset GetUtcNow() => provider.GetUtcNow();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = provider.CreateTimer(callback, state, dueTime, period);
            DelayScheduled.TrySetResult();
            return timer;
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }

    private sealed class TransactionBoundary(Func<CancellationToken, Task> committed) : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default) =>
            committed(cancellationToken);
    }

    private WebApplicationFactory<Program> CreateHost(bool interceptTransactions = true) => entry.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
    {
        services.RemoveAll<IDbContextOptionsConfiguration<ExcitementDbContext>>();
        services.RemoveAll<DbContextOptions<ExcitementDbContext>>();
        services.AddDbContext<ExcitementDbContext>(o =>
        {
            o.UseNpgsql(database.GetConnectionString());
            if (interceptTransactions && transactionBoundary is not null) o.AddInterceptors(transactionBoundary);
        });
        services.RemoveAll<TimeProvider>();
        services.AddSingleton<TimeProvider>(clock);
        services.Configure<YouTubeHighlightsOptions>(o => { o.Enabled = enabled; o.ApiKey = apiKey; o.DailyUnitLimit = limit; o.MaxUploadPages = maxUploadPages; });
        services.Configure<HighlightsWorkerOptions>(o => { o.Enabled = workerEnabled; o.AuthorizeLaunchCatchup = launch; o.MaxGamesPerCycle = maxGames; o.MaxCatchupPages = maxCatchupPages; });
        if (startHosted) services.AddHostedService<HighlightsBackgroundService>();
        services.AddHttpClient(YouTubeHighlightsClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new Handler(async (request, ct) =>
        {
            Interlocked.Increment(ref youtubeCalls);
            requestDays.Enqueue(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.Now, TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles")).DateTime));
            if (providerBarrier is not null) await providerBarrier(ct);
            if (status != HttpStatusCode.OK) return new(status) { Content = new StringContent(quotaError ? """{"error":{"errors":[{"reason":"quotaExceeded"}]}}""" : "{}") };
            if (malformed) return new(HttpStatusCode.OK) { Content = new StringContent("invalid JSON") };
            var path = request.RequestUri!.AbsolutePath;
            var body = path.EndsWith("channels")
                ? """{"items":[{"id":"UCoLrcjPV5PbUrUyXq5mjc_A","contentDetails":{"relatedPlaylists":{"uploads":"fixture-uploads"}}}]}"""
                : path.EndsWith("playlistItems") ? new JsonObject { ["items"] = new JsonArray(new JsonObject { ["contentDetails"] = new JsonObject { ["videoId"] = uploadVideoId } }), ["nextPageToken"] = continuation }.ToJsonString()
                : removed ? """{"items":[]}""" : """{"items":[{"id":"fixtureVid1","snippet":{"channelId":"UCoLrcjPV5PbUrUyXq5mjc_A","title":"Dodgers vs. Braves Highlights (10/6/26) | MLB Highlights","description":"Single game recap","publishedAt":"2026-10-07T02:00:00Z","liveBroadcastContent":"none"},"contentDetails":{"duration":"PT8M"},"status":{"privacyStatus":"public","uploadStatus":"processed","embeddable":true,"madeForKids":false}}]}""";
            if (path.EndsWith("playlistItems"))
            {
                if (pendingUpload)
                {
                    var uploads = JsonNode.Parse(body)!;
                    uploads["items"]!.AsArray().Insert(0, new JsonObject { ["contentDetails"] = new JsonObject { ["videoId"] = "pending0001" } });
                    body = uploads.ToJsonString();
                }
                tokens.Add(request.RequestUri.Query);
                if (uploadPages > 1)
                {
                    var token = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["pageToken"];
                    var page = token is null ? 0 : int.Parse(token.Replace("page-", ""));
                    var items = new JsonArray(Enumerable.Range(page * 50, 50).Select(i => (JsonNode)new JsonObject
                        { ["contentDetails"] = new JsonObject { ["videoId"] = $"video{i:D6}" } }).ToArray());
                    var uploads = new JsonObject { ["items"] = items };
                    if (page + 1 < uploadPages) uploads["nextPageToken"] = $"page-{page + 1}";
                    body = uploads.ToJsonString();
                }
            }
            if (path.EndsWith("videos") && !removed)
            {
                var template = JsonNode.Parse(body)!["items"]![0]!;
                if (regionLimited) template["contentDetails"]!["regionRestriction"] = new JsonObject { ["allowed"] = new JsonArray("US") };
                var ids = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["id"]!.Split(',').Where(id => id != removedVideoId);
                body = new JsonObject { ["items"] = new JsonArray(ids.Select(id =>
                {
                    var video = template.DeepClone();
                    video["id"] = id;
                    if (pendingUpload && id == "pending0001")
                    {
                        video["snippet"]!["liveBroadcastContent"] = "upcoming";
                        video["contentDetails"]!["duration"] = "P0D";
                        video["status"]!["uploadStatus"] = "uploaded";
                    }
                    return video;
                }).ToArray()) }.ToJsonString();
            }
            if (path.EndsWith("channels") && channelUnverified) body = body.Replace(YouTubeHighlightsClient.ApprovedChannelId, "unverified-channel");
            if (path.EndsWith("i18nRegions")) body = """{"items":[{"id":"US"}]}""";
            return new(HttpStatusCode.OK) { Content = new StringContent(body.Replace(",\"nextPageToken\":null", "")) };
        }));
        services.AddHttpClient(MlbHighlightsIdentityClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new Handler((request, ct) =>
        {
            if (identityFailure) throw new HttpRequestException("synthetic failure");
            var id = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["event"] ?? "fixture-game";
            var body = request.RequestUri.Host == "statsapi.mlb.com"
                ? """{"dates":[{"date":"2026-10-06","games":[{"gamePk":849819,"gameDate":"2026-10-06T22:00:00Z","officialDate":"2026-10-06","season":"2026","doubleHeader":"N","gameNumber":1,"status":{"abstractGameState":"Final","detailedState":"Final","codedGameState":"F"},"teams":{"home":{"team":{"id":144}},"away":{"team":{"id":119}}}}]}]}"""
                : """{"header":{"id":"GAME","season":{"year":2026},"competitions":[{"id":"GAME","date":"2026-10-06T22:00:00Z","status":{"type":{"name":"STATUS_FINAL","state":"post","completed":true}},"competitors":[{"homeAway":"home","team":{"id":"15","abbreviation":"ATL"}},{"homeAway":"away","team":{"id":"19","abbreviation":"LAD"}}]}]}}""".Replace("GAME", id);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }));
    }));

    public async Task InitializeAsync()
    {
        await database.StartAsync();
        host = CreateHost();
        await Seed(db => { db.Games.Add(Game("fixture-game")); });
    }

    private Game Game(string id) => new() { Id = id, Sport = "mlb", Status = "final", HomeTeam = "Atlanta Braves", AwayTeam = "Los Angeles Dodgers", HomeTeamAbbreviation = "ATL", AwayTeamAbbreviation = "LAD", HomeTeamId = "15", AwayTeamId = "19", SeasonYear = 2026, ScheduledStart = new DateTime(2026, 10, 6, 22, 0, 0, DateTimeKind.Utc), CompletionConfirmedAt = clock.Now.UtcDateTime, HighlightsDiscoveryExpiresAt = new DateTime(2026, 10, 10, 3, 0, 0, DateTimeKind.Utc) };

    private async Task Seed(Action<ExcitementDbContext> seed)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        await db.Database.EnsureCreatedAsync();
        seed(db);
        await db.SaveChangesAsync();
    }

    private async Task<HighlightsLifecycleResult> Run(CancellationToken ct = default)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<HighlightsLifecycleService>().RunAsync(ct);
    }

    private async Task<GameHighlightsInspection> Inspect(string id = "fixture-game")
    {
        using var scope = host.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<HighlightsApplicationService>().InspectAsync(id))!;
    }

    private async Task<HighlightsOperationState> Operations()
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<GameHighlightsInspectionService>().InspectOperationsAsync();
    }

    private async Task Restart()
    {
        await host.DisposeAsync();
        host = CreateHost();
    }

    [Fact]
    public async Task Pending_upload_does_not_prevent_normal_game_matching_or_consume_the_whole_reservation()
    {
        pendingUpload = true;
        var result = await Run();
        Assert.Equal("Processed", result.Reason);
        Assert.Equal(1, result.Selected);
        Assert.Equal("fixtureVid1", (await Inspect()).Highlights!.SelectedVideoId);
        var operations = await Operations();
        Assert.Equal(3, operations.QuotaUnitsUsed);
        Assert.Equal(0, operations.WorkerReservedUnits);
        Assert.Null(operations.BackoffUntil);
        Assert.Null(operations.LaunchBoundaryAt);
    }

    [Fact]
    public async Task Disabled_discovery_still_deletes_expired_provider_payload_and_preserves_manual_suppression()
    {
        workerEnabled = false;
        await Seed(db => db.GameHighlights.Add(new GameHighlights { GameId = "fixture-game", SelectedVideoId = "fixtureVid1", Ownership = HighlightsOwnership.Manual, Suppressed = true, SelectedAt = clock.Now.AddDays(-31).UtcDateTime, Availability = HighlightsAvailability.Playable, Title = "stale title", ChannelId = "stale channel", SelectionEvidenceJson = "{}", AllowedRegions = ["US"], MadeForKids = false, ProviderDataRefreshedAt = clock.Now.AddDays(-31).UtcDateTime, ProviderDataExpiresAt = clock.Now.AddDays(-1).UtcDateTime }));
        await Restart();
        await Run();
        var selection = (await Inspect()).Highlights!;
        Assert.Null(selection.SelectedVideoId);
        Assert.Null(selection.Title);
        Assert.Null(selection.ChannelId);
        Assert.Null(selection.SelectionEvidenceJson);
        Assert.Null(selection.AllowedRegions);
        Assert.Equal(HighlightsOwnership.Manual, selection.Ownership);
        Assert.True(selection.Suppressed);
        Assert.Equal(HighlightsAvailability.Unavailable, selection.Availability);
        Assert.Equal(0, youtubeCalls);
    }

    [Fact]
    public async Task Shared_poll_selects_once_at_fifteen_minutes_and_persists_cadence_across_restart()
    {
        Assert.Equal(1, (await Run()).Selected);
        Assert.Equal("fixtureVid1", (await Inspect()).Highlights!.SelectedVideoId);
        Assert.Equal(3, (await Operations()).QuotaUnitsUsed);
        clock.Now = clock.Now.AddMinutes(14);
        await Run();
        Assert.Equal(3, youtubeCalls);
        await Restart();
        await Run();
        Assert.Equal(3, youtubeCalls);
        clock.Now = clock.Now.AddMinutes(1);
        await Run();
        Assert.Equal(6, youtubeCalls);
        Assert.Equal("fixtureVid1", (await Inspect()).Highlights!.SelectedVideoId);
        Assert.Equal(new DateTime(2026, 10, 10, 3, 0, 0, DateTimeKind.Utc), (await Inspect()).Game.HighlightsDiscoveryExpiresAt);
    }

    [Fact]
    public async Task Daily_revalidation_stops_definitively_removed_playback_but_not_transient_failure()
    {
        await Run();
        clock.Now = clock.Now.AddHours(23);
        await Run();
        Assert.Equal(new DateTime(2026, 10, 7, 3, 0, 0, DateTimeKind.Utc), (await Inspect()).Highlights!.ValidatedAt);
        clock.Now = clock.Now.AddHours(1);
        status = HttpStatusCode.ServiceUnavailable;
        await Run();
        Assert.Equal(HighlightsAvailability.Playable, (await Inspect()).Highlights!.Availability);
        await Restart();
        clock.Now = clock.Now.AddMinutes(15);
        status = HttpStatusCode.OK;
        removed = true;
        await Run();
        Assert.Equal(HighlightsAvailability.Unavailable, (await Inspect()).Highlights!.Availability);
        Assert.Equal(new DateTime(2026, 10, 10, 3, 0, 0, DateTimeKind.Utc), (await Inspect()).Game.HighlightsDiscoveryExpiresAt);
    }

    [Fact]
    public async Task Launch_snapshot_resumes_pages_without_gaining_later_imports_or_new_windows()
    {
        launch = true;
        continuation = "page-two";
        await Seed(db =>
        {
            var game = db.Games.Single();
            game.HighlightsDiscoveryExpiresAt = clock.Now.AddDays(-1).UtcDateTime;
            game.CompletionConfirmedAt = null;
            var outside = Game("outside"); outside.ScheduledStart = clock.Now.AddDays(-7).AddTicks(-1).UtcDateTime;
            outside.HighlightsDiscoveryExpiresAt = null;
            db.Games.Add(outside);
        });
        await Restart();
        var first = await Run();
        Assert.Equal(1, first.Selected);
        var boundary = (await Operations()).LaunchBoundaryAt;
        Assert.Equal(new DateTime(2026, 10, 7, 3, 0, 0, DateTimeKind.Utc), boundary);
        Assert.Equal(["fixture-game"], (await Operations()).CatchupRemainingGameIds!);
        Assert.Null((await Inspect("outside")).Highlights);
        await Seed(db => { var imported = Game("later-import"); imported.HighlightsDiscoveryExpiresAt = null; db.Games.Add(imported); });
        await Restart();
        continuation = null;
        clock.Now = clock.Now.AddMinutes(15);
        await Run();
        Assert.NotNull((await Operations()).CatchupCompletedAt);
        Assert.Null((await Inspect("later-import")).Highlights);
        Assert.Equal(boundary, (await Operations()).LaunchBoundaryAt);
        Assert.Equal(new DateTime(2026, 10, 6, 3, 0, 0, DateTimeKind.Utc), (await Inspect()).Game.HighlightsDiscoveryExpiresAt);
        await Restart();
        clock.Now = clock.Now.AddDays(1);
        await Run();
        Assert.Equal(boundary, (await Operations()).LaunchBoundaryAt);
        Assert.Null((await Inspect("later-import")).Highlights);
        Assert.Contains(tokens, t => t.Contains("pageToken=page-two"));
    }

    [Fact]
    public async Task Launch_cohort_cannot_replace_a_daily_invalidated_automatic_decision_at_original_deadline()
    {
        await Run();
        clock.Now = clock.Now.AddDays(1);
        await Seed(db => db.Games.Single().HighlightsDiscoveryExpiresAt = clock.Now.UtcDateTime);
        launch = true;
        uploadVideoId = "fixtureVid2";
        removedVideoId = "fixtureVid1";
        await Restart();
        var result = await Run();
        Assert.Equal(1, result.Revalidated);
        Assert.Equal(0, result.Selected);
        var inspected = await Inspect();
        Assert.Equal("fixtureVid1", inspected.Highlights!.SelectedVideoId);
        Assert.Equal(HighlightsAvailability.Unavailable, inspected.Highlights.Availability);
        Assert.Equal(HighlightsOwnership.Automatic, inspected.Highlights.Ownership);
        Assert.Equal(clock.Now.UtcDateTime, inspected.Game.HighlightsDiscoveryExpiresAt);
        Assert.Equal(clock.Now.UtcDateTime, (await Operations()).LaunchBoundaryAt);
        Assert.NotNull((await Operations()).CatchupCompletedAt);
        var page = await host.CreateClient().GetStringAsync("/game/fixture-game");
        Assert.Contains("Highlights unavailable", page);
        Assert.DoesNotContain("highlights-load", page);
    }

    [Theory]
    [InlineData(HighlightsOwnership.Automatic, false)]
    [InlineData(HighlightsOwnership.Manual, false)]
    [InlineData(HighlightsOwnership.Automatic, true)]
    public async Task Launch_cohort_preserves_expired_decisions_after_provider_payload_deletion(HighlightsOwnership ownership, bool suppressed)
    {
        await Run();
        clock.Now = clock.Now.AddDays(1);
        await Seed(db =>
        {
            db.Games.Single().HighlightsDiscoveryExpiresAt = clock.Now.AddMinutes(-1).UtcDateTime;
            var selection = db.GameHighlights.Single();
            selection.Ownership = ownership;
            selection.Suppressed = suppressed;
            selection.ProviderDataExpiresAt = clock.Now.UtcDateTime;
        });
        launch = true;
        uploadVideoId = "fixtureVid2";
        await Restart();
        var result = await Run();
        Assert.Equal(1, result.Expired);
        Assert.Equal(0, result.Selected);
        var selectionAfter = (await Inspect()).Highlights!;
        Assert.Null(selectionAfter.SelectedVideoId);
        Assert.Equal(ownership, selectionAfter.Ownership);
        Assert.Equal(suppressed, selectionAfter.Suppressed);
        Assert.Equal(new DateTime(2026, 10, 7, 3, 0, 0, DateTimeKind.Utc), selectionAfter.SelectedAt);
        Assert.NotNull((await Operations()).CatchupCompletedAt);
        Assert.Contains("Highlights unavailable", await host.CreateClient().GetStringAsync("/game/fixture-game"));
    }

    [Fact]
    public async Task Launch_cohort_can_replace_invalid_automatic_choice_inside_original_window()
    {
        await Run();
        clock.Now = clock.Now.AddDays(1);
        launch = true;
        uploadVideoId = "fixtureVid2";
        removedVideoId = "fixtureVid1";
        await Restart();
        var result = await Run();
        Assert.Equal(1, result.Revalidated);
        Assert.Equal(1, result.Selected);
        var inspected = await Inspect();
        Assert.Equal("fixtureVid2", inspected.Highlights!.SelectedVideoId);
        Assert.Equal(HighlightsOwnership.Automatic, inspected.Highlights.Ownership);
        Assert.Equal(new DateTime(2026, 10, 10, 3, 0, 0, DateTimeKind.Utc), inspected.Game.HighlightsDiscoveryExpiresAt);
        Assert.NotNull((await Operations()).CatchupCompletedAt);
        Assert.Contains("fixtureVid2", await host.CreateClient().GetStringAsync("/game/fixture-game"));
    }

    [Fact]
    public async Task Explicit_worker_disable_never_advertises_active_search_at_detail_boundary()
    {
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<HighlightsApplicationService>().ProcessCandidatesAsync(["fixture-game"], []);
        workerEnabled = false;
        await Restart();
        using var read = host.Services.CreateScope();
        var detail = await read.ServiceProvider.GetRequiredService<HighlightsApplicationService>().GetDetailAsync("fixture-game");
        Assert.Equal(HighlightsDetailStatus.Unavailable, detail.Status);
        var before = youtubeCalls;
        await Run();
        Assert.Equal(before, youtubeCalls);
        var page = await host.CreateClient().GetStringAsync("/game/fixture-game");
        Assert.Contains("Highlights unavailable", page);
        Assert.DoesNotContain("Highlights not available yet", page);
        Assert.Equal(before, youtubeCalls);
    }

    [Fact]
    public async Task Pacific_rollover_reserves_inflight_worker_calls_before_concurrent_operator_work()
    {
        clock.Now = new(2026, 10, 7, 6, 59, 59, TimeSpan.Zero);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        providerBarrier = async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        var worker = Run();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(4, (await Operations()).QuotaUnitsUsed);
        clock.Now = new(2026, 10, 7, 7, 0, 0, TimeSpan.Zero);
        using var scope = host.Services.CreateScope();
        var command = scope.ServiceProvider.GetRequiredService<HighlightsApplicationService>().OperateAsync(new(HighlightsOperatorAction.Attach, "fixture-game", "fixtureVid1"));
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while ((await Operations()).QuotaDay != new DateOnly(2026, 10, 7)) await Task.Delay(10, deadline.Token);
            Assert.Equal(7, (await Operations()).QuotaUnitsUsed);
        }
        finally { release.TrySetResult(); await worker; await command; }
        Assert.Equal(6, (await Operations()).QuotaUnitsUsed);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(1000)]
    public async Task Independent_operator_hosts_cannot_overrun_daily_ceiling_across_Pacific_midnight(int ceiling)
    {
        limit = ceiling;
        regionLimited = true;
        clock.Now = new(2026, 10, 7, 6, 59, 59, TimeSpan.Zero);
        await Restart();
        await using var other = CreateHost();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        providerBarrier = async ct =>
        {
            if (Interlocked.Increment(ref requests) == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
        };
        using var firstScope = host.Services.CreateScope();
        var first = firstScope.ServiceProvider.GetRequiredService<HighlightsApplicationService>().OperateAsync(new(HighlightsOperatorAction.Attach, "fixture-game", "fixtureVid1"));
        HighlightsOperatorResult firstResult;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            clock.Now = new(2026, 10, 7, 7, 0, 0, TimeSpan.Zero);
            if (ceiling == 1000) await Seed(db =>
            {
                var state = db.HighlightsOperations.Single();
                state.QuotaDay = new DateOnly(2026, 10, 7);
                state.QuotaUnitsUsed = 997;
            });
            using var secondScope = other.Services.CreateScope();
            var second = await secondScope.ServiceProvider.GetRequiredService<HighlightsApplicationService>().OperateAsync(new(HighlightsOperatorAction.Attach, "fixture-game", "fixtureVid1"));
            Assert.True(second.Accepted);
        }
        finally { release.TrySetResult(); firstResult = await first; }
        Assert.Equal(3, requestDays.Count(day => day == new DateOnly(2026, 10, 7)));
        Assert.False(firstResult.Accepted);
        Assert.Equal("BudgetExhausted", firstResult.Reason);
        Assert.Equal(ceiling, (await Operations()).QuotaUnitsUsed);
        Assert.Equal(new DateOnly(2026, 10, 7), (await Operations()).QuotaDay);
        Assert.Null((await Inspect()).Highlights);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(1000)]
    public async Task Quota_commit_rollover_cannot_dispatch_a_channel_after_independent_host_spends_current_day_budget(int ceiling)
    {
        limit = ceiling;
        regionLimited = true;
        clock.Now = new(2026, 10, 7, 6, 59, 59, TimeSpan.Zero);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commits = 0;
        transactionBoundary = new TransactionBoundary(async ct =>
        {
            var commit = Interlocked.Increment(ref commits);
            if (commit == 1) clock.Now = new(2026, 10, 7, 7, 0, 0, TimeSpan.Zero);
            if (commit == 2)
            {
                clock.Now = new(2026, 10, 8, 7, 0, 0, TimeSpan.Zero);
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
        });
        await Restart();
        await using var other = CreateHost(interceptTransactions: false);
        using var firstScope = host.Services.CreateScope();
        var first = firstScope.ServiceProvider.GetRequiredService<HighlightsApplicationService>().OperateAsync(new(HighlightsOperatorAction.Attach, "fixture-game", "fixtureVid1"));
        HighlightsOperatorResult firstResult;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, youtubeCalls);
            var charged = await Operations();
            Assert.Equal(new DateOnly(2026, 10, 7), charged.QuotaDay);
            Assert.Equal(1, charged.QuotaUnitsUsed);
            using var secondScope = other.Services.CreateScope();
            var secondApplication = secondScope.ServiceProvider.GetRequiredService<HighlightsApplicationService>();
            if (ceiling == 1000)
            {
                for (var command = 0; command < 331; command++)
                    Assert.True((await secondApplication.OperateAsync(new(HighlightsOperatorAction.Attach, "fixture-game", "fixtureVid1"))).Accepted);
                regionLimited = false;
                for (var command = 0; command < 2; command++)
                    Assert.True((await secondApplication.OperateAsync(new(HighlightsOperatorAction.Attach, "fixture-game", "fixtureVid1"))).Accepted);
                regionLimited = true;
                Assert.Equal(997, youtubeCalls);
                Assert.Equal(997, (await Operations()).QuotaUnitsUsed);
            }
            var second = await secondApplication.OperateAsync(new(HighlightsOperatorAction.Attach, "fixture-game", "fixtureVid1"));
            Assert.True(second.Accepted);
        }
        finally { release.TrySetResult(); firstResult = await first; }
        Assert.Equal(ceiling, requestDays.Count(day => day == new DateOnly(2026, 10, 8)));
        Assert.Equal(ceiling, youtubeCalls);
        Assert.Equal("BudgetExhausted", firstResult.Reason);
        Assert.False(firstResult.Accepted);
        var operation = await Operations();
        Assert.Equal(new DateOnly(2026, 10, 8), operation.QuotaDay);
        Assert.Equal(ceiling, operation.QuotaUnitsUsed);
        Assert.Equal(0, operation.WorkerReservedUnits);
        Assert.Null((await Inspect()).Highlights);
    }

    [Fact]
    public async Task Quota_commit_rollover_reauthorizes_before_dispatch_when_new_day_has_capacity()
    {
        limit = 3;
        regionLimited = true;
        clock.Now = new(2026, 10, 7, 6, 59, 59, TimeSpan.Zero);
        var commits = 0;
        transactionBoundary = new TransactionBoundary(ct =>
        {
            var commit = Interlocked.Increment(ref commits);
            if (commit == 1) clock.Now = new(2026, 10, 7, 7, 0, 0, TimeSpan.Zero);
            if (commit == 2) clock.Now = new(2026, 10, 8, 7, 0, 0, TimeSpan.Zero);
            return Task.CompletedTask;
        });
        await Restart();
        using var scope = host.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<HighlightsApplicationService>().OperateAsync(new(HighlightsOperatorAction.Attach, "fixture-game", "fixtureVid1", true));
        Assert.True(result.Applied);
        Assert.Equal(3, youtubeCalls);
        Assert.Equal(3, requestDays.Count(day => day == new DateOnly(2026, 10, 8)));
        var operation = await Operations();
        Assert.Equal(new DateOnly(2026, 10, 8), operation.QuotaDay);
        Assert.Equal(3, operation.QuotaUnitsUsed);
        Assert.Equal(HighlightsOwnership.Manual, (await Inspect()).Highlights!.Ownership);
        Assert.Equal(new DateTime(2026, 10, 10, 3, 0, 0, DateTimeKind.Utc), (await Inspect()).Game.HighlightsDiscoveryExpiresAt);
    }

    [Fact]
    public async Task Continuously_changing_quota_commit_days_back_off_without_dispatch_or_permanent_reservation()
    {
        limit = 3;
        regionLimited = true;
        clock.Now = new(2026, 10, 7, 6, 59, 59, TimeSpan.Zero);
        transactionBoundary = new TransactionBoundary(ct => { clock.Now = clock.Now.AddDays(1); return Task.CompletedTask; });
        await Restart();
        using (var scope = host.Services.CreateScope())
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await scope.ServiceProvider.GetRequiredService<HighlightsApplicationService>().OperateAsync(new(HighlightsOperatorAction.Attach, "fixture-game", "fixtureVid1"), deadline.Token);
            Assert.Equal("Backoff", result.Reason);
            Assert.False(result.Accepted);
        }
        Assert.Equal(0, youtubeCalls);
        var retained = await Operations();
        Assert.Equal(new DateOnly(2026, 10, 9), retained.QuotaDay);
        Assert.Equal(1, retained.QuotaUnitsUsed);
        Assert.NotNull(retained.BackoffUntil);
        Assert.Equal(0, retained.WorkerReservedUnits);
        transactionBoundary = null;
        await Restart();
        clock.Now = clock.Now.AddDays(1);
        using var restarted = host.Services.CreateScope();
        Assert.True((await restarted.ServiceProvider.GetRequiredService<HighlightsApplicationService>().OperateAsync(new(HighlightsOperatorAction.Attach, "fixture-game", "fixtureVid1", true))).Applied);
        Assert.Equal(3, youtubeCalls);
        Assert.Equal(3, (await Operations()).QuotaUnitsUsed);
    }

    [Fact]
    public async Task Cancellation_during_quota_commit_keeps_charge_without_dispatch_and_restart_can_use_future_days()
    {
        limit = 3;
        regionLimited = true;
        clock.Now = new(2026, 10, 7, 6, 59, 59, TimeSpan.Zero);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commits = 0;
        transactionBoundary = new TransactionBoundary(async ct =>
        {
            var commit = Interlocked.Increment(ref commits);
            if (commit == 1) clock.Now = new(2026, 10, 7, 7, 0, 0, TimeSpan.Zero);
            if (commit == 2) { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
        });
        await Restart();
        using (var scope = host.Services.CreateScope())
        {
            using var cancellation = new CancellationTokenSource();
            var work = scope.ServiceProvider.GetRequiredService<HighlightsApplicationService>().OperateAsync(new(HighlightsOperatorAction.Attach, "fixture-game", "fixtureVid1"), cancellation.Token);
            try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { cancellation.Cancel(); }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        }
        Assert.Equal(0, youtubeCalls);
        Assert.Equal(new DateOnly(2026, 10, 7), (await Operations()).QuotaDay);
        Assert.Equal(1, (await Operations()).QuotaUnitsUsed);
        transactionBoundary = null;
        await Restart();
        await using var other = CreateHost(interceptTransactions: false);
        using var secondScope = other.Services.CreateScope();
        var application = secondScope.ServiceProvider.GetRequiredService<HighlightsApplicationService>();
        Assert.Equal("BudgetExhausted", (await application.OperateAsync(new(HighlightsOperatorAction.Attach, "fixture-game", "fixtureVid1"))).Reason);
        Assert.Equal(0, youtubeCalls);
        clock.Now = clock.Now.AddDays(1);
        Assert.True((await application.OperateAsync(new(HighlightsOperatorAction.Attach, "fixture-game", "fixtureVid1", true))).Applied);
        Assert.Equal(3, youtubeCalls);
        Assert.Equal(3, (await Operations()).QuotaUnitsUsed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Interrupted_operator_keeps_conservative_charge_but_does_not_block_future_days_after_restart(bool rollover)
    {
        limit = 3;
        regionLimited = true;
        clock.Now = new(2026, 10, 7, 6, 59, 59, TimeSpan.Zero);
        await Restart();
        await using var other = CreateHost();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        providerBarrier = async ct =>
        {
            if (Interlocked.Increment(ref requests) == 1 && rollover)
                clock.Now = new(2026, 10, 7, 7, 0, 0, TimeSpan.Zero);
            else
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
        };
        using var cancellation = new CancellationTokenSource();
        using (var firstScope = host.Services.CreateScope())
        {
            var first = firstScope.ServiceProvider.GetRequiredService<HighlightsApplicationService>().OperateAsync(new(HighlightsOperatorAction.Attach, "fixture-game", "fixtureVid1"), cancellation.Token);
            try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { cancellation.Cancel(); }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        }
        providerBarrier = null;
        await Restart();
        Assert.Equal(rollover ? 1 : 3, (await Operations()).QuotaUnitsUsed);
        using (var secondScope = other.Services.CreateScope())
        {
            var denied = await secondScope.ServiceProvider.GetRequiredService<HighlightsApplicationService>().OperateAsync(new(HighlightsOperatorAction.Attach, "fixture-game", "fixtureVid1"));
            Assert.Equal("BudgetExhausted", denied.Reason);
        }
        Assert.Equal(rollover ? 2 : 1, youtubeCalls);
        clock.Now = clock.Now.AddDays(1);
        await Restart();
        using var restarted = host.Services.CreateScope();
        var recovered = await restarted.ServiceProvider.GetRequiredService<HighlightsApplicationService>().OperateAsync(new(HighlightsOperatorAction.Attach, "fixture-game", "fixtureVid1", true));
        Assert.True(recovered.Applied);
        Assert.Equal(3, (await Operations()).QuotaUnitsUsed);
        var recoveredDay = (await Operations()).QuotaDay;
        Assert.Equal(3, requestDays.Count(day => day == recoveredDay));
        Assert.Equal(HighlightsOwnership.Manual, (await Inspect()).Highlights!.Ownership);
    }

    [Fact]
    public async Task Operator_rollover_requests_share_worker_accounting_without_new_day_refunds()
    {
        limit = 5;
        regionLimited = true;
        clock.Now = new(2026, 10, 7, 6, 59, 59, TimeSpan.Zero);
        await Restart();
        await using var other = CreateHost();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        providerBarrier = async ct =>
        {
            var request = Interlocked.Increment(ref requests);
            if (request == 1) clock.Now = new(2026, 10, 7, 7, 0, 0, TimeSpan.Zero);
            if (request == 2) { entered.TrySetResult(); await release.Task.WaitAsync(ct); }
        };
        using var firstScope = host.Services.CreateScope();
        var first = firstScope.ServiceProvider.GetRequiredService<HighlightsApplicationService>().OperateAsync(new(HighlightsOperatorAction.Attach, "fixture-game", "fixtureVid1"));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, (await Operations()).QuotaUnitsUsed);
            using var workerScope = other.Services.CreateScope();
            Assert.Equal(1, (await workerScope.ServiceProvider.GetRequiredService<HighlightsLifecycleService>().RunAsync()).Selected);
            Assert.Equal(5, (await Operations()).QuotaUnitsUsed);
        }
        finally { release.TrySetResult(); await first; }
        Assert.Equal("BudgetExhausted", (await first).Reason);
        Assert.Equal(5, (await Operations()).QuotaUnitsUsed);
        Assert.Equal(5, requestDays.Count(day => day == new DateOnly(2026, 10, 7)));
        Assert.Equal(HighlightsOwnership.Automatic, (await Inspect()).Highlights!.Ownership);
        Assert.Equal(0, (await Operations()).WorkerReservedUnits);
    }

    [Fact]
    public async Task Operator_return_after_midnight_never_refunds_an_old_day_even_before_another_rollover()
    {
        limit = 3;
        status = HttpStatusCode.ServiceUnavailable;
        clock.Now = new(2026, 10, 7, 6, 59, 59, TimeSpan.Zero);
        await Restart();
        providerBarrier = ct => { clock.Now = new(2026, 10, 7, 7, 0, 0, TimeSpan.Zero); return Task.CompletedTask; };
        using var scope = host.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<HighlightsApplicationService>().OperateAsync(new(HighlightsOperatorAction.Attach, "fixture-game", "fixtureVid1"));
        Assert.Equal("TemporaryFailure", result.Reason);
        Assert.Equal(3, (await Operations()).QuotaUnitsUsed);
        Assert.Equal(new DateOnly(2026, 10, 6), (await Operations()).QuotaDay);
        Assert.Equal(1, youtubeCalls);
    }

    [Theory]
    [InlineData("regions")]
    [InlineData("publication")]
    [InlineData("kids")]
    public async Task Metadata_only_legacy_payload_is_deleted_without_credentials(string variant)
    {
        enabled = false;
        await Seed(db =>
        {
            db.GameHighlights.Add(new GameHighlights { GameId = "fixture-game", Ownership = HighlightsOwnership.Manual,
                BlockedRegions = variant == "regions" ? ["US"] : null, PublishedAt = variant == "publication" ? clock.Now.AddDays(-40).UtcDateTime : null,
                MadeForKids = variant == "kids" ? true : null });
            var state = db.HighlightsOperations.Single();
            state.UploadsPlaylistId = "expired-playlist"; state.UploadsPageToken = "expired-token";
        });
        await Restart();
        await Run();
        var selection = (await Inspect()).Highlights!;
        Assert.Null(selection.BlockedRegions); Assert.Null(selection.PublishedAt); Assert.Null(selection.MadeForKids);
        Assert.Equal(HighlightsOwnership.Manual, selection.Ownership);
        Assert.Null((await Operations()).UploadsPlaylistId); Assert.Null((await Operations()).UploadsPageToken);
        Assert.Equal(0, youtubeCalls);
    }

    [Fact]
    public async Task Hosted_lifecycle_uses_controlled_timer_and_stops_without_more_provider_work()
    {
        startHosted = true;
        await Restart();
        _ = host.Services;
        await clock.DelayScheduled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("fixtureVid1", (await Inspect()).Highlights!.SelectedVideoId);
        Assert.Equal(3, youtubeCalls);
        clock.Now = clock.Now.AddMinutes(15);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while ((await Operations()).NextPollAt != new DateTime(2026, 10, 7, 3, 30, 0, DateTimeKind.Utc)) await Task.Delay(10, deadline.Token);
        await host.DisposeAsync();
        var before = youtubeCalls;
        clock.Now = clock.Now.AddDays(1);
        Assert.Equal(before, youtubeCalls);
        startHosted = false;
        host = CreateHost();
    }

    [Fact]
    public async Task Cancellation_keeps_pre_reservation_and_restart_does_not_refund_or_repeat_immediately()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        providerBarrier = async ct => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); };
        using var cancellation = new CancellationTokenSource();
        var work = Run(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(4, (await Operations()).QuotaUnitsUsed);
        Assert.Equal("Busy", (await Run()).Reason);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        providerBarrier = null;
        await Restart();
        Assert.Equal("NotDue", (await Run()).Reason);
        Assert.Equal(4, (await Operations()).QuotaUnitsUsed);
        Assert.Equal(1, youtubeCalls);
        clock.Now = clock.Now.AddMinutes(15);
        await Run();
        Assert.Equal(7, (await Operations()).QuotaUnitsUsed);
        Assert.Equal("fixtureVid1", (await Inspect()).Highlights!.SelectedVideoId);
    }

    [Theory]
    [InlineData(3, 0)]
    [InlineData(4, 3)]
    [InlineData(1000, 3)]
    public async Task Durable_lower_allocation_is_honored_before_any_worker_request(int budget, int expectedCalls)
    {
        limit = budget;
        await Restart();
        await Run();
        Assert.Equal(expectedCalls, youtubeCalls);
        if (expectedCalls > 0)
        {
            await Restart();
            clock.Now = clock.Now.AddMinutes(15);
            await Run();
            Assert.Equal(budget == 4 ? 3 : 6, youtubeCalls);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Provider_failure_backoff_survives_restart_and_does_not_break_game_pages(HttpStatusCode failure)
    {
        status = failure;
        await Run();
        Assert.Equal(1, (await Operations()).QuotaUnitsUsed);
        var until = (await Operations()).BackoffUntil;
        Assert.Equal(new DateTime(2026, 10, 7, 3, 15, 0, DateTimeKind.Utc), until);
        await Restart();
        clock.Now = clock.Now.AddMinutes(14);
        Assert.Equal("Backoff", (await Run()).Reason);
        Assert.Equal(1, youtubeCalls);
        var page = await host.CreateClient().GetAsync("/game/fixture-game");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        clock.Now = clock.Now.AddMinutes(1);
        await Run();
        Assert.Equal(2, youtubeCalls);
        Assert.Equal(new DateTime(2026, 10, 7, 3, 45, 0, DateTimeKind.Utc), (await Operations()).BackoffUntil);
    }

    [Fact]
    public async Task Authoritative_failure_and_original_deadline_close_never_gain_a_retry_window()
    {
        identityFailure = true;
        await Run();
        Assert.Null((await Inspect()).Highlights?.SelectedVideoId);
        identityFailure = false;
        clock.Now = new(2026, 10, 10, 3, 0, 0, TimeSpan.Zero);
        await Run();
        Assert.Null((await Inspect()).Highlights?.SelectedVideoId);
        Assert.Equal(new DateTime(2026, 10, 10, 3, 0, 0, DateTimeKind.Utc), (await Inspect()).Game.HighlightsDiscoveryExpiresAt);
    }

    [Fact]
    public async Task Thirty_day_boundary_deletes_even_during_prolonged_outage_without_undoing_ownership()
    {
        await Run();
        await Seed(db => { var selection = db.GameHighlights.Single(); selection.Ownership = HighlightsOwnership.Manual; selection.Suppressed = true; });
        status = HttpStatusCode.ServiceUnavailable;
        clock.Now = clock.Now.AddDays(29).AddHours(23);
        await Run();
        Assert.Equal("fixtureVid1", (await Inspect()).Highlights!.SelectedVideoId);
        clock.Now = clock.Now.AddHours(1);
        await Run();
        var selection = (await Inspect()).Highlights!;
        Assert.Null(selection.SelectedVideoId); Assert.Null(selection.SelectionEvidenceJson); Assert.Null(selection.Title);
        Assert.Equal(HighlightsOwnership.Manual, selection.Ownership); Assert.True(selection.Suppressed);
        Assert.Equal(5, youtubeCalls);
    }

    [Fact]
    public async Task Additive_sql_preserves_cohort_progress_and_quota_on_reapplication()
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE highlights_operations DROP COLUMN catchup_remaining_game_ids, DROP COLUMN catchup_pages_scanned, DROP COLUMN worker_reserved_units, DROP COLUMN next_validation_attempt_at, DROP COLUMN provider_configuration_rejected");
        await db.Database.ExecuteSqlRawAsync("UPDATE highlights_operations SET last_reason = 'InvalidConfiguration'");
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "031_HighlightsLifecycle.sql"));
        await db.Database.ExecuteSqlRawAsync(sql);
        var state = await db.HighlightsOperations.SingleAsync();
        Assert.Null(state.CatchupRemainingGameIds); Assert.Null(state.NextValidationAttemptAt); Assert.Equal(0, state.WorkerReservedUnits);
        Assert.True(state.ProviderConfigurationRejected);
        state.LaunchBoundaryAt = clock.Now.UtcDateTime; state.CatchupWindowStart = clock.Now.AddDays(-7).UtcDateTime;
        state.CatchupRemainingGameIds = ["fixture-game"]; state.CatchupPagesScanned = 2; state.WorkerReservedUnits = 4;
        state.NextValidationAttemptAt = clock.Now.AddMinutes(15).UtcDateTime;
        state.QuotaUnitsUsed = 900; state.QuotaDay = new DateOnly(2026, 10, 6);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync(sql);
        var retained = await Operations();
        Assert.Equal(["fixture-game"], retained.CatchupRemainingGameIds!); Assert.Equal(2, retained.CatchupPagesScanned);
        Assert.Equal(900, retained.QuotaUnitsUsed); Assert.Equal(4, retained.WorkerReservedUnits);
        Assert.True(retained.ProviderConfigurationRejected);
        Assert.Equal(new DateTime(2026, 10, 7, 3, 15, 0, DateTimeKind.Utc), retained.NextValidationAttemptAt);
        Assert.Equal("Atlanta Braves", (await Inspect()).Game.HomeTeam);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE highlights_operations SET worker_reserved_units = 11"));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE highlights_operations SET catchup_pages_scanned = 17"));
    }

    [Fact]
    public async Task Interrupted_daily_validation_has_durable_retry_spacing_after_restart()
    {
        await Run();
        clock.Now = clock.Now.AddDays(1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        providerBarrier = async ct => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); };
        using var cancellation = new CancellationTokenSource();
        var work = Run(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        providerBarrier = null;
        await Restart();
        await Run();
        Assert.Equal(new DateTime(2026, 10, 7, 3, 0, 0, DateTimeKind.Utc), (await Inspect()).Highlights!.ValidatedAt);
        clock.Now = clock.Now.AddMinutes(15);
        await Run();
        Assert.Equal(new DateTime(2026, 10, 8, 3, 15, 0, DateTimeKind.Utc), (await Inspect()).Highlights!.ValidatedAt);
    }

    [Theory]
    [InlineData("missing-key")]
    [InlineData("invalid-key")]
    [InlineData("invalid-batch")]
    [InlineData("invalid-pages")]
    [InlineData("zero-budget")]
    public async Task Invalid_or_missing_configuration_never_calls_upstream_or_initializes_launch(string variant)
    {
        launch = true;
        if (variant == "missing-key") apiKey = null;
        if (variant == "invalid-key") apiKey = "unsafe key";
        if (variant == "invalid-batch") maxGames = 101;
        if (variant == "invalid-pages") maxCatchupPages = 17;
        if (variant == "zero-budget") limit = 0;
        await Restart();
        await Run();
        Assert.Equal(0, youtubeCalls); Assert.Null((await Operations()).LaunchBoundaryAt);
        using var scope = host.Services.CreateScope();
        var application = scope.ServiceProvider.GetRequiredService<HighlightsApplicationService>();
        await application.ProcessCandidatesAsync(["fixture-game"], []);
        Assert.NotEqual(HighlightsDetailStatus.Pending, (await application.GetDetailAsync("fixture-game")).Status);
    }

    [Theory]
    [InlineData("disabled", YouTubeOutcome.Disabled)]
    [InlineData("missing-key", YouTubeOutcome.MissingApiKey)]
    [InlineData("whitespace-key", YouTubeOutcome.MissingApiKey)]
    [InlineData("invalid-key", YouTubeOutcome.InvalidConfiguration)]
    [InlineData("long-key", YouTubeOutcome.InvalidConfiguration)]
    [InlineData("unicode-key", YouTubeOutcome.InvalidConfiguration)]
    [InlineData("negative-budget", YouTubeOutcome.InvalidConfiguration)]
    [InlineData("excess-budget", YouTubeOutcome.InvalidConfiguration)]
    [InlineData("zero-pages", YouTubeOutcome.InvalidConfiguration)]
    [InlineData("excess-pages", YouTubeOutcome.InvalidConfiguration)]
    [InlineData("zero-budget", YouTubeOutcome.BudgetExhausted)]
    public async Task Provider_configuration_policy_agrees_at_application_worker_adapter_and_detail_boundaries(string variant, YouTubeOutcome expected)
    {
        using (var initial = host.Services.CreateScope())
            await initial.ServiceProvider.GetRequiredService<HighlightsApplicationService>().ProcessCandidatesAsync(["fixture-game"], []);
        if (variant == "disabled") enabled = false;
        if (variant == "missing-key") apiKey = null;
        if (variant == "whitespace-key") apiKey = " ";
        if (variant == "invalid-key") apiKey = "unsafe key";
        if (variant == "long-key") apiKey = new string('a', 513);
        if (variant == "unicode-key") apiKey = "clé";
        if (variant == "negative-budget") limit = -1;
        if (variant == "excess-budget") limit = 1001;
        if (variant == "zero-budget") limit = 0;
        if (variant == "zero-pages") maxUploadPages = 0;
        if (variant == "excess-pages") maxUploadPages = 5;
        await Restart();
        using var scope = host.Services.CreateScope();
        var application = scope.ServiceProvider.GetRequiredService<HighlightsApplicationService>();
        Assert.Equal(expected.ToString(), (await application.ProcessCandidatesAsync(["fixture-game"], [])).Reason);
        Assert.Equal(expected.ToString(), (await application.OperateAsync(new(HighlightsOperatorAction.Attach, "fixture-game", "fixtureVid1"))).Reason);
        Assert.Equal(expected.ToString(), (await Run()).Reason);
        Assert.Equal(expected, (await scope.ServiceProvider.GetRequiredService<YouTubeHighlightsClient>().ValidateVideosAsync(["fixtureVid1"])).Outcome);
        Assert.Equal(HighlightsDetailStatus.Unavailable, (await application.GetDetailAsync("fixture-game")).Status);
        Assert.Contains("Highlights unavailable", await host.CreateClient().GetStringAsync("/game/fixture-game"));
        Assert.Equal(0, youtubeCalls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Operator_and_worker_share_durable_rejection_transition_through_failure_and_success(bool operatorFirst, bool unverified)
    {
        using (var initial = host.Services.CreateScope())
            await initial.ServiceProvider.GetRequiredService<HighlightsApplicationService>().ProcessCandidatesAsync(["fixture-game"], []);
        channelUnverified = unverified;
        status = unverified ? HttpStatusCode.OK : HttpStatusCode.Unauthorized;
        if (operatorFirst)
        {
            using var scope = host.Services.CreateScope();
            Assert.False((await scope.ServiceProvider.GetRequiredService<HighlightsApplicationService>().OperateAsync(new(HighlightsOperatorAction.Attach, "fixture-game", "fixtureVid1"))).Accepted);
        }
        else await Run();
        Assert.True((await Operations()).ProviderConfigurationRejected);
        await Restart();
        Assert.Contains("Highlights unavailable", await host.CreateClient().GetStringAsync("/game/fixture-game"));
        clock.Now = clock.Now.AddMinutes(15);
        channelUnverified = false;
        status = HttpStatusCode.ServiceUnavailable;
        if (operatorFirst) await Run();
        else
        {
            using var scope = host.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<HighlightsApplicationService>().OperateAsync(new(HighlightsOperatorAction.Attach, "fixture-game", "fixtureVid1"));
        }
        Assert.True((await Operations()).ProviderConfigurationRejected);
        clock.Now = clock.Now.AddHours(1);
        status = HttpStatusCode.OK;
        await Restart();
        if (operatorFirst) await Run();
        else
        {
            using var scope = host.Services.CreateScope();
            Assert.True((await scope.ServiceProvider.GetRequiredService<HighlightsApplicationService>().OperateAsync(new(HighlightsOperatorAction.Attach, "fixture-game", "fixtureVid1"))).Accepted);
        }
        Assert.False((await Operations()).ProviderConfigurationRejected);
        await Restart();
        using var read = host.Services.CreateScope();
        Assert.NotEqual(HighlightsDetailStatus.Unavailable, (await read.ServiceProvider.GetRequiredService<HighlightsApplicationService>().GetDetailAsync("fixture-game")).Status);
    }

    [Fact]
    public async Task Malformed_provider_response_has_bounded_backoff_and_no_selection()
    {
        malformed = true;
        Assert.Equal("InvalidResponse", (await Run()).Reason);
        Assert.Equal(1, youtubeCalls); Assert.Null((await Inspect()).Highlights);
        await Restart();
        await Run();
        Assert.Equal(1, youtubeCalls);
        Assert.Equal(HttpStatusCode.OK, (await host.CreateClient().GetAsync("/game/fixture-game")).StatusCode);
    }

    [Fact]
    public async Task Catchup_snapshot_includes_just_inside_not_outside_future_other_sports_or_live()
    {
        launch = true; continuation = "next-page";
        await Seed(db =>
        {
            var inside = Game("inside"); inside.ScheduledStart = clock.Now.AddDays(-7).AddTicks(10).UtcDateTime; inside.HighlightsDiscoveryExpiresAt = null;
            var outside = Game("outside"); outside.ScheduledStart = clock.Now.AddDays(-7).AddTicks(-10).UtcDateTime; outside.HighlightsDiscoveryExpiresAt = null;
            var future = Game("future"); future.ScheduledStart = clock.Now.AddTicks(10).UtcDateTime; future.HighlightsDiscoveryExpiresAt = null;
            var nfl = Game("nfl"); nfl.Sport = "nfl";
            var live = Game("live"); live.Status = "in_progress";
            db.Games.AddRange(inside, outside, future, nfl, live);
        });
        await Restart(); await Run();
        Assert.Equal(["fixture-game", "inside"], (await Operations()).CatchupRemainingGameIds!);
        Assert.Null((await Inspect("inside")).Game.HighlightsDiscoveryExpiresAt);
        Assert.Null((await Inspect("outside")).Highlights); Assert.Null((await Inspect("future")).Highlights);
        Assert.Null((await Inspect("nfl")).Highlights); Assert.Null((await Inspect("live")).Highlights);
    }

    [Fact]
    public async Task Cancellation_before_first_upload_preserves_original_launch_cohort_not_a_new_backfill()
    {
        launch = true;
        await Seed(db => db.Games.Single().HighlightsDiscoveryExpiresAt = null);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        providerBarrier = async ct => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); };
        await Restart();
        using var cancellation = new CancellationTokenSource();
        var work = Run(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        await Seed(db => { var imported = Game("imported-after-crash"); imported.HighlightsDiscoveryExpiresAt = null; db.Games.Add(imported); });
        providerBarrier = null; await Restart(); clock.Now = clock.Now.AddMinutes(15);
        await Run();
        Assert.Equal("fixtureVid1", (await Inspect()).Highlights!.SelectedVideoId);
        Assert.Null((await Inspect("imported-after-crash")).Highlights);
        Assert.Equal(new DateTime(2026, 10, 7, 3, 0, 0, DateTimeKind.Utc), (await Operations()).LaunchBoundaryAt);
        Assert.Null((await Inspect()).Game.HighlightsDiscoveryExpiresAt);
        Assert.NotNull((await Operations()).CatchupCompletedAt);
    }

    [Fact]
    public async Task Catchup_pagination_stops_at_configured_cap_without_a_second_launch()
    {
        launch = true; maxCatchupPages = 2; continuation = "second-page";
        await Restart(); await Run();
        Assert.Null((await Operations()).CatchupCompletedAt);
        continuation = "third-page";
        clock.Now = clock.Now.AddMinutes(15);
        await Run();
        Assert.NotNull((await Operations()).CatchupCompletedAt);
        Assert.Null((await Operations()).UploadsPageToken);
        var before = youtubeCalls;
        clock.Now = clock.Now.AddMinutes(15); await Run();
        Assert.Equal(before + 3, youtubeCalls);
    }

    [Fact]
    public async Task Two_hundred_shared_uploads_are_batched_and_durably_accounted_without_per_game_search()
    {
        uploadPages = 4; maxUploadPages = 4;
        await Restart();
        Assert.Equal(1, (await Run()).Selected);
        Assert.Equal("video000000", (await Inspect()).Highlights!.SelectedVideoId);
        Assert.Equal(9, youtubeCalls); Assert.Equal(9, (await Operations()).QuotaUnitsUsed);
        Assert.Equal(4, tokens.Count);
        using var scope = host.Services.CreateScope();
        Assert.Equal(HighlightsDetailStatus.Playable, (await scope.ServiceProvider.GetRequiredService<HighlightsApplicationService>().GetDetailAsync("fixture-game")).Status);
        Assert.Equal(9, youtubeCalls);
    }

    [Fact]
    public async Task Optional_detail_storage_failure_does_not_break_scoreboard_page()
    {
        await Run();
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Database.ExecuteSqlRawAsync("DROP TABLE game_highlights");
        var page = await host.CreateClient().GetAsync("/game/fixture-game");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Atlanta Braves", html);
        Assert.DoesNotContain("highlights-player", html);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Provider_rejected_credentials_do_not_advertise_active_search_after_restart(HttpStatusCode rejection)
    {
        limit = 4;
        await Restart();
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<HighlightsApplicationService>().ProcessCandidatesAsync(["fixture-game"], []);
        status = rejection;
        Assert.Equal("InvalidConfiguration", (await Run()).Reason);
        await Restart();
        using var read = host.Services.CreateScope();
        var detail = await read.ServiceProvider.GetRequiredService<HighlightsApplicationService>().GetDetailAsync("fixture-game");
        Assert.Equal(HighlightsDetailStatus.Unavailable, detail.Status);
        Assert.Equal(1, youtubeCalls);
        clock.Now = clock.Now.AddMinutes(15);
        Assert.Equal("BudgetExhausted", (await Run()).Reason);
        Assert.Equal(HighlightsDetailStatus.Unavailable, (await read.ServiceProvider.GetRequiredService<HighlightsApplicationService>().GetDetailAsync("fixture-game")).Status);
        var page = await host.CreateClient().GetStringAsync("/game/fixture-game");
        Assert.Contains("Highlights unavailable", page);
        Assert.DoesNotContain("Highlights not available yet", page);
        Assert.DoesNotContain("sentinel-not-live", page);
        Assert.Equal(1, youtubeCalls);
    }

    [Fact]
    public async Task Provider_quota_exhaustion_waits_for_Pacific_next_day_without_discarding_or_extending_state()
    {
        status = HttpStatusCode.Forbidden; quotaError = true;
        Assert.Equal("QuotaExhausted", (await Run()).Reason);
        Assert.Equal(new DateTime(2026, 10, 7, 7, 0, 0, DateTimeKind.Utc), (await Operations()).BackoffUntil);
        await Restart(); clock.Now = clock.Now.AddMinutes(15);
        await Run(); Assert.Equal(1, youtubeCalls);
        status = HttpStatusCode.OK; quotaError = false;
        clock.Now = new(2026, 10, 7, 7, 0, 0, TimeSpan.Zero);
        Assert.Equal(1, (await Run()).Selected);
        Assert.Equal(3, (await Operations()).QuotaUnitsUsed);
        Assert.Equal(4, youtubeCalls);
        Assert.Equal(new DateTime(2026, 10, 10, 3, 0, 0, DateTimeKind.Utc), (await Inspect()).Game.HighlightsDiscoveryExpiresAt);
    }

    [Fact]
    public async Task Operator_and_worker_share_quota_without_double_charging_and_manual_choice_is_preserved()
    {
        using (var scope = host.Services.CreateScope())
        {
            var attached = await scope.ServiceProvider.GetRequiredService<HighlightsApplicationService>().OperateAsync(new(HighlightsOperatorAction.Attach, "fixture-game", "fixtureVid1", true));
            Assert.True(attached.Applied);
        }
        Assert.Equal(2, (await Operations()).QuotaUnitsUsed);
        await Run();
        Assert.Equal(5, (await Operations()).QuotaUnitsUsed);
        Assert.Equal(5, youtubeCalls);
        Assert.Equal(HighlightsOwnership.Manual, (await Inspect()).Highlights!.Ownership);
        Assert.Equal("fixtureVid1", (await Inspect()).Highlights!.SelectedVideoId);
    }

    [Fact]
    public async Task Catchup_page_budget_never_overshoots_a_smaller_remaining_batch_budget()
    {
        launch = true; maxCatchupPages = 5; maxUploadPages = 4; uploadPages = 8;
        await Restart(); await Run();
        Assert.NotNull((await Operations()).CatchupCompletedAt);
        Assert.Equal(9, youtubeCalls);
        clock.Now = clock.Now.AddMinutes(15); await Run();
        Assert.Equal(18, youtubeCalls);
        Assert.Equal(18, (await Operations()).QuotaUnitsUsed);
    }

    public async Task DisposeAsync()
    {
        await host.DisposeAsync();
        await entry.DisposeAsync();
        await database.DisposeAsync();
    }
}
