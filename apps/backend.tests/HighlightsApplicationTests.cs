using System.Net;
using System.Text.Json.Nodes;
using backend.Data;
using backend.Models;
using backend.Services.Highlights;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;
using Xunit;

namespace backend.tests;

public sealed class HighlightsApplicationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer database = IsolatedPostgreSql.CreateContainer();
    private readonly AppFactory entry = new();
    private readonly Clock clock = new();
    private readonly GameUpdateGate gameUpdates = new();
    private WebApplicationFactory<Program> host = null!;
    private JsonObject summary = JsonNode.Parse("""
        {"header":{"id":"fixture-game","season":{"year":2026},"competitions":[{
        "id":"fixture-game","date":"2026-10-06T22:00:00Z",
        "status":{"type":{"name":"STATUS_FINAL","state":"post","completed":true}},
        "competitors":[{"homeAway":"home","team":{"id":"15","abbreviation":"ATL"}},
        {"homeAway":"away","team":{"id":"19","abbreviation":"LAD"}}]}]}}
        """)!.AsObject();
    private JsonObject schedule = JsonNode.Parse("""
        {"dates":[{"date":"2026-10-06","games":[{"gamePk":849819,"gameDate":"2026-10-06T22:00:00Z",
        "officialDate":"2026-10-06","season":"2026","doubleHeader":"N","gameNumber":1,
        "status":{"abstractGameState":"Final","detailedState":"Final","codedGameState":"F"},
        "teams":{"home":{"team":{"id":144}},"away":{"team":{"id":119}}}}]}]}
        """)!.AsObject();
    private int upstreamCalls;
    private Func<Task>? identityBarrier;
    private string providerTitle = "Dodgers vs. Braves Highlights (10/6/26) | MLB Highlights";
    private string providerAvailability = "public";
    private bool enabled = true;
    private bool identityFailure;
    private bool identityStreamFailure;
    private string channelId = YouTubeHighlightsClient.ApprovedChannelId;
    private Action? providerHook;
    private Action<YouTubeHighlightsOptions>? configure;

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 7, 3, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    private WebApplicationFactory<Program> CreateHost() => entry.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
    {
        services.RemoveAll<IDbContextOptionsConfiguration<ExcitementDbContext>>();
        services.RemoveAll<DbContextOptions<ExcitementDbContext>>();
        services.AddDbContext<ExcitementDbContext>(options => options.UseNpgsql(database.GetConnectionString()).AddInterceptors(gameUpdates));
        services.RemoveAll<TimeProvider>();
        services.AddSingleton<TimeProvider>(clock);
        services.Configure<YouTubeHighlightsOptions>(o => { o.Enabled = enabled; o.ApiKey = "sentinel-not-live"; configure?.Invoke(o); });
        services.Configure<HighlightsWorkerOptions>(o => o.Enabled = true);
        services.AddHttpClient("MlbHighlightsIdentity").ConfigurePrimaryHttpMessageHandler(() => new Handler(async request =>
        {
            Interlocked.Increment(ref upstreamCalls);
            if (identityFailure) throw new HttpRequestException("fixture failure");
            if (identityStreamFailure) throw new IOException("fixture stream interruption");
            if (identityBarrier is not null) await identityBarrier();
            return new(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri!.Host == "statsapi.mlb.com" ? schedule.ToJsonString() : summary.ToJsonString()) };
        }));
        services.AddHttpClient("YouTubeHighlights").ConfigurePrimaryHttpMessageHandler(() => new Handler(request =>
        {
            Interlocked.Increment(ref upstreamCalls);
            providerHook?.Invoke();
            var body = request.RequestUri!.AbsolutePath.EndsWith("channels")
                ? """{"items":[{"id":"UCoLrcjPV5PbUrUyXq5mjc_A","contentDetails":{"relatedPlaylists":{"uploads":"fixture-uploads"}}}]}"""
                : new JsonObject { ["items"] = new JsonArray(new JsonObject
                {
                    ["id"] = "manualVideo", ["snippet"] = new JsonObject { ["channelId"] = channelId, ["title"] = providerTitle, ["description"] = "Single game recap", ["publishedAt"] = "2026-10-07T02:00:00Z", ["liveBroadcastContent"] = "none" },
                    ["contentDetails"] = new JsonObject { ["duration"] = "PT8M12S" },
                    ["status"] = new JsonObject { ["privacyStatus"] = providerAvailability, ["uploadStatus"] = "processed", ["embeddable"] = true, ["madeForKids"] = false }
                }) }.ToJsonString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }));
    }));

    public async Task InitializeAsync()
    {
        await database.StartAsync();
        host = CreateHost();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.Games.Add(new Game { Id = "fixture-game", Sport = "mlb", Status = "final", HomeTeam = "Atlanta Braves", AwayTeam = "Los Angeles Dodgers", HomeTeamAbbreviation = "ATL", AwayTeamAbbreviation = "LAD", HomeTeamId = "15", AwayTeamId = "19", SeasonYear = 2026, ScheduledStart = new DateTime(2026, 10, 6, 22, 0, 0, DateTimeKind.Utc), CompletionConfirmedAt = clock.Now.UtcDateTime, HighlightsDiscoveryExpiresAt = new DateTime(2026, 10, 10, 3, 0, 0, DateTimeKind.Utc) });
        await db.SaveChangesAsync();
    }

    private YouTubeVideoEvidence Video(string id = "autoVideo", string? title = null, string description = "Single game recap") =>
        new(id, YouTubeAvailability.Playable, YouTubeHighlightsClient.ApprovedChannelId, title ?? providerTitle, description,
            clock.Now.AddDays(1), TimeSpan.FromMinutes(8), false, [], [], clock.Now);

    private async Task<T> Use<T>(Func<HighlightsApplicationService, Task<T>> operation)
    {
        using var scope = host.Services.CreateScope();
        return await operation(scope.ServiceProvider.GetRequiredService<HighlightsApplicationService>());
    }

    [Fact]
    public async Task Completed_authoritative_contest_selects_once_and_detail_reads_never_contact_upstream()
    {
        var result = await Use(s => s.ProcessCandidatesAsync(["fixture-game"], [Video()]));
        Assert.Equal(1, result.Selected);
        var before = upstreamCalls;
        var detail = await Use(s => s.GetDetailAsync("fixture-game"));
        Assert.Equal(HighlightsDetailStatus.Playable, detail.Status);
        Assert.Equal("autoVideo", detail.VideoId);
        Assert.Equal(before, upstreamCalls);
        await Use(s => s.ProcessCandidatesAsync(["fixture-game"], [Video("secondVideo")]));
        Assert.Equal("autoVideo", (await Use(s => s.InspectAsync("fixture-game")))!.Highlights!.SelectedVideoId);
        Assert.Equal(849819, (await Use(s => s.InspectAsync("fixture-game")))!.Game.MlbGamePk);
    }

    [Fact]
    public async Task Detail_HTTP_offers_persisted_selection_without_provider_calls_and_keeps_final_sync_terminal()
    {
        await Use(s => s.ProcessCandidatesAsync(["fixture-game"], []));
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var beforeInitialVisit = upstreamCalls;
        using var initial = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(await client.GetStringAsync("/game/fixture-game"));
        Assert.Equal("Highlights not available yet", initial.QuerySelector("#highlights-status")!.TextContent);
        Assert.Equal(beforeInitialVisit, upstreamCalls);
        var result = await Use(s => s.ProcessCandidatesAsync(["fixture-game"], [Video("EQMJSt1j-uA")]));
        Assert.Equal(1, result.Selected);
        var before = upstreamCalls;
        var html = await client.GetStringAsync("/game/fixture-game");
        using var doc = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
        var section = doc.QuerySelector("#game-highlights");
        Assert.NotNull(section);
        Assert.Equal(providerTitle, section.QuerySelector("#highlights-title")!.TextContent);
        Assert.Equal("EQMJSt1j-uA", section.QuerySelector("nb-game-highlights")!.GetAttribute("data-video-id"));
        Assert.Equal("https://www.youtube.com/watch?v=EQMJSt1j-uA", section.QuerySelector("#highlights-watch")!.GetAttribute("href"));
        Assert.Null(section.QuerySelector("iframe, img, link, [src]"));
        Assert.Equal("detail-scoreboard", section.PreviousElementSibling!.Id);
        Assert.Equal("true", doc.QuerySelector("[data-live-terminal]")!.GetAttribute("data-live-terminal"));
        Assert.DoesNotContain("sentinel-not-live", html);
        Assert.Equal(before, upstreamCalls);
        var inspection = await Use(s => s.InspectAsync("fixture-game"));
        Assert.Equal("EQMJSt1j-uA", inspection!.Highlights!.SelectedVideoId);
    }

    [Theory]
    [InlineData("active", "Highlights not available yet")]
    [InlineData("closed", "Highlights unavailable")]
    [InlineData("disabled", "Highlights unavailable")]
    [InlineData("missing-key", "Highlights unavailable")]
    [InlineData("suppressed", "Highlights unavailable")]
    [InlineData("known-unusable", "Highlights unavailable")]
    public async Task Detail_HTTP_reports_honest_application_availability_without_an_empty_player(string variant, string copy)
    {
        await Use(s => s.ProcessCandidatesAsync(["fixture-game"], []));
        if (variant == "closed") clock.Now = clock.Now.AddDays(4);
        if (variant == "suppressed") await Use(s => s.OperateAsync(new(HighlightsOperatorAction.Remove, "fixture-game", Apply: true)));
        if (variant == "known-unusable")
        {
            await Use(s => s.ProcessCandidatesAsync(["fixture-game"], [Video("EQMJSt1j-uA")]));
            await Use(s => s.RevalidateSelectionsAsync([Video("EQMJSt1j-uA") with { Availability = YouTubeAvailability.Removed }]));
        }
        if (variant is "disabled" or "missing-key")
        {
            if (variant == "disabled") enabled = false;
            else configure = o => o.ApiKey = "";
            await host.DisposeAsync();
            host = CreateHost();
        }
        var before = upstreamCalls;
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var doc = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(await client.GetStringAsync("/game/fixture-game"));
        var section = doc.QuerySelector("#game-highlights");
        Assert.NotNull(section);
        Assert.Equal(copy, section.QuerySelector("#highlights-status")!.TextContent);
        Assert.Null(section.QuerySelector("iframe, button, nb-game-highlights, #highlights-watch"));
        Assert.Equal(before, upstreamCalls);
    }

    [Theory]
    [InlineData("nhl")]
    [InlineData("scheduled")]
    [InlineData("in_progress")]
    [InlineData("canceled")]
    [InlineData("postponed")]
    [InlineData("suspended")]
    [InlineData("post-false")]
    [InlineData("missing-evidence")]
    public async Task Detail_HTTP_excludes_ineligible_games_and_game_lists_never_gain_highlights(string variant)
    {
        if (variant == "post-false") summary["header"]!["competitions"]![0]!["status"]!["type"]!["completed"] = false;
        if (variant != "missing-evidence") await Use(s => s.ProcessCandidatesAsync(["fixture-game"], []));
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            if (variant == "nhl") await db.Games.ExecuteUpdateAsync(s => s.SetProperty(g => g.Sport, "nhl"));
            else if (variant is not "post-false" and not "missing-evidence") await db.Games.ExecuteUpdateAsync(s => s.SetProperty(g => g.Status, variant));
        }
        var before = upstreamCalls;
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var detail = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(await client.GetStringAsync("/game/fixture-game"));
        Assert.Null(detail.QuerySelector("#game-highlights"));
        using var list = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(await client.GetStringAsync("/mlb?date=2026-10-06"));
        Assert.Null(list.QuerySelector("#game-highlights, nb-game-highlights"));
        Assert.Equal(before, upstreamCalls);
    }

    [Theory]
    [InlineData("doubleheader")]
    [InlineData("makeup")]
    [InlineData("same-day-repeat")]
    [InlineData("conflicting-gamepk")]
    [InlineData("multiple-gamepk")]
    [InlineData("commentary")]
    [InlineData("isolated-play")]
    [InlineData("multi-game")]
    [InlineData("full-replay")]
    [InlineData("wrong-team")]
    [InlineData("previous-season")]
    [InlineData("wrong-date")]
    [InlineData("nearest-start")]
    [InlineData("missing-game-number")]
    [InlineData("missing-final")]
    [InlineData("post-false")]
    [InlineData("conflicting-status")]
    [InlineData("conflicting-description-date")]
    public async Task Unsafe_identity_or_content_never_selects(string variant)
    {
        var row = schedule["dates"]![0]!["games"]![0]!;
        var title = providerTitle;
        var description = "Single game recap";
        switch (variant)
        {
            case "doubleheader": row["doubleHeader"] = "Y"; row["gameNumber"] = 2; description = "Game 2"; break;
            case "makeup": row["rescheduledFromDate"] = "2026-10-05"; break;
            case "same-day-repeat":
                var second = row.DeepClone(); second["gamePk"] = 849820; second["gameDate"] = "2026-10-07T01:00:00Z";
                schedule["dates"]![0]!["games"]!.AsArray().Add(second); break;
            case "conflicting-gamepk": description = "gamePk: 849820"; break;
            case "multiple-gamepk": description = "gamePk: 849819 gamePk: 849820"; break;
            case "commentary": description = "Commentary and analysis only"; break;
            case "isolated-play": description = "An isolated play clip of one home run"; break;
            case "multi-game": description = "All games highlights roundup"; break;
            case "full-replay": description = "Full game replay"; break;
            case "wrong-team": title = title.Replace("Dodgers", "Mets"); break;
            case "previous-season": title = title.Replace("10/6/26", "10/6/25"); break;
            case "wrong-date": title = title.Replace("10/6/26", "10/5/26"); break;
            case "nearest-start": row["gameDate"] = "2026-10-06T22:01:00Z"; break;
            case "missing-game-number": row.AsObject().Remove("gameNumber"); break;
            case "missing-final": row["status"]!["detailedState"] = "Suspended"; break;
            case "post-false": summary["header"]!["competitions"]![0]!["status"]!["type"]!["completed"] = false; break;
            case "conflicting-status": summary["header"]!["competitions"]![0]!["status"]!["type"]!["detail"] = "Postponed"; break;
            case "conflicting-description-date": description = "Highlights from 10/5/2026"; break;
        }
        var result = await Use(s => s.ProcessCandidatesAsync(["fixture-game"], [Video(title: title, description: description)]));
        Assert.Equal(0, result.Selected);
        Assert.NotEqual(HighlightsDetailStatus.Playable, (await Use(s => s.GetDetailAsync("fixture-game"))).Status);
    }

    [Fact]
    public async Task Official_date_not_utc_or_upload_day_drives_matching_and_explicit_gamepk_can_distinguish_doubleheader()
    {
        var row = schedule["dates"]![0]!["games"]![0]!;
        row["gameDate"] = "2026-10-07T01:00:00Z";
        row["doubleHeader"] = "Y"; row["gameNumber"] = 2;
        summary["header"]!["competitions"]![0]!["date"] = "2026-10-07T01:00:00Z";
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Games.ExecuteUpdateAsync(s => s.SetProperty(g => g.ScheduledStart, new DateTime(2026, 10, 7, 1, 0, 0, DateTimeKind.Utc)));
        var result = await Use(s => s.ProcessCandidatesAsync(["fixture-game"], [Video(description: "Single game recap. gamePk: 849819")]));
        Assert.Equal(1, result.Selected);
        Assert.Equal(new DateOnly(2026, 10, 6), (await Use(s => s.InspectAsync("fixture-game")))!.Game.MlbOfficialDate);
    }

    [Fact]
    public async Task Operator_dry_runs_late_attach_remove_resume_and_restart_preserve_decisions_and_deadline()
    {
        clock.Now = new(2026, 10, 12, 3, 0, 0, TimeSpan.Zero);
        var dry = await Use(s => s.OperateAsync(new(HighlightsOperatorAction.Attach, "fixture-game", "manualVideo")));
        Assert.True(dry.Accepted); Assert.False(dry.Applied);
        var unchanged = await Use(s => s.InspectAsync("fixture-game"));
        Assert.Null(unchanged!.Highlights); Assert.Null(unchanged.Game.MlbGamePk); Assert.Null(unchanged.Game.EspnCompletionEvidenceJson);
        var attached = await Use(s => s.OperateAsync(new(HighlightsOperatorAction.Attach, "fixture-game", "manualVideo", true)));
        Assert.True(attached.Applied);
        Assert.Equal(HighlightsOwnership.Manual, attached.Inspection!.Highlights!.Ownership);
        Assert.Equal(HighlightsDetailStatus.Playable, (await Use(s => s.GetDetailAsync("fixture-game"))).Status);
        var removeDry = await Use(s => s.OperateAsync(new(HighlightsOperatorAction.Remove, "fixture-game")));
        Assert.True(removeDry.Accepted); Assert.False(removeDry.Inspection!.Highlights!.Suppressed);
        var removed = await Use(s => s.OperateAsync(new(HighlightsOperatorAction.Remove, "fixture-game", Apply: true)));
        Assert.True(removed.Inspection!.Highlights!.Suppressed); Assert.Null(removed.Inspection.Highlights.SelectedVideoId);
        await Use(s => s.OperateAsync(new(HighlightsOperatorAction.Attach, "fixture-game", "manualVideo", true)));
        await host.DisposeAsync(); host = CreateHost();
        var retained = await Use(s => s.InspectAsync("fixture-game"));
        Assert.True(retained!.Highlights!.Suppressed); Assert.Equal(HighlightsOwnership.Manual, retained.Highlights.Ownership);
        var resumed = await Use(s => s.OperateAsync(new(HighlightsOperatorAction.Resume, "fixture-game", Apply: true)));
        Assert.False(resumed.Inspection!.Highlights!.Suppressed);
        Assert.Equal(new DateTime(2026, 10, 10, 3, 0, 0, DateTimeKind.Utc), resumed.Inspection.Game.HighlightsDiscoveryExpiresAt);
        Assert.Equal("manualVideo", resumed.Inspection.Highlights.SelectedVideoId);
        Assert.Equal(0, (await Use(s => s.ProcessCandidatesAsync(["fixture-game"], [Video()]))).Selected);
    }

    [Theory]
    [InlineData("publisher")]
    [InlineData("private")]
    [InlineData("content")]
    [InlineData("completion")]
    public async Task Manual_game_choice_cannot_bypass_safety(string variant)
    {
        if (variant == "private") providerAvailability = "private";
        if (variant == "content") providerTitle = "Dodgers commentary";
        if (variant == "publisher") channelId = "UCnotApproved";
        if (variant == "completion") summary["header"]!["competitions"]![0]!["status"]!["type"]!["completed"] = false;
        var result = await Use(s => s.OperateAsync(new(HighlightsOperatorAction.Attach, "fixture-game", "manualVideo", true)));
        Assert.False(result.Accepted); Assert.False(result.Applied);
        Assert.Null((await Use(s => s.InspectAsync("fixture-game")))!.Highlights);
    }

    [Fact]
    public async Task Attach_wont_overwrite_any_existing_decision_replace_is_explicit_even_after_payload_deletion()
    {
        await Use(s => s.ProcessCandidatesAsync(["fixture-game"], [Video()]));
        Assert.False((await Use(s => s.OperateAsync(new(HighlightsOperatorAction.Attach, "fixture-game", "manualVideo", true)))).Accepted);
        Assert.True((await Use(s => s.OperateAsync(new(HighlightsOperatorAction.Replace, "fixture-game", "manualVideo", true)))).Applied);
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().GameHighlights.ExecuteUpdateAsync(s => s
                .SetProperty(h => h.SelectedVideoId, (string?)null).SetProperty(h => h.Title, (string?)null).SetProperty(h => h.Availability, HighlightsAvailability.Unavailable));
        Assert.Equal(0, (await Use(s => s.ProcessCandidatesAsync(["fixture-game"], [Video()]))).Selected);
        Assert.False((await Use(s => s.OperateAsync(new(HighlightsOperatorAction.Attach, "fixture-game", "manualVideo", true)))).Accepted);
        Assert.True((await Use(s => s.OperateAsync(new(HighlightsOperatorAction.Replace, "fixture-game", "manualVideo", true)))).Applied);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Definitive_invalidity_never_replaces_manual_and_automatic_recovery_uses_original_window(bool manual)
    {
        if (manual) await Use(s => s.OperateAsync(new(HighlightsOperatorAction.Attach, "fixture-game", "manualVideo", true)));
        else await Use(s => s.ProcessCandidatesAsync(["fixture-game"], [Video()]));
        var id = manual ? "manualVideo" : "autoVideo";
        clock.Now = clock.Now.AddDays(1);
        var invalid = Video(id) with { Availability = YouTubeAvailability.Removed, Title = null, Description = null };
        Assert.Equal(1, (await Use(s => s.RevalidateSelectionsAsync([invalid]))).Revalidated);
        var unavailable = await Use(s => s.GetDetailAsync("fixture-game"));
        Assert.Equal(HighlightsDetailStatus.Unavailable, unavailable.Status);
        Assert.Equal(id, (await Use(s => s.InspectAsync("fixture-game")))!.Highlights!.SelectedVideoId);
        var replace = await Use(s => s.ProcessCandidatesAsync(["fixture-game"], [Video("replacement")]));
        Assert.Equal(manual ? 0 : 1, replace.Selected);
        clock.Now = new(2026, 10, 11, 3, 0, 0, TimeSpan.Zero);
        await Use(s => s.RevalidateSelectionsAsync([Video(manual ? id : "replacement") with { Availability = YouTubeAvailability.NotEmbeddable }]));
        Assert.Equal(0, (await Use(s => s.ProcessCandidatesAsync(["fixture-game"], [Video("tooLate")]))).Selected);
        Assert.Equal(new DateTime(2026, 10, 10, 3, 0, 0, DateTimeKind.Utc), (await Use(s => s.InspectAsync("fixture-game")))!.Game.HighlightsDiscoveryExpiresAt);
    }

    [Fact]
    public async Task Stale_revalidation_cannot_overwrite_concurrent_manual_correction()
    {
        await Use(s => s.ProcessCandidatesAsync(["fixture-game"], [Video()]));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        identityBarrier = () => { entered.TrySetResult(); return release.Task; };
        var revalidation = Use(s => s.RevalidateSelectionsAsync([Video()]));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        identityBarrier = null;
        await Use(s => s.OperateAsync(new(HighlightsOperatorAction.Replace, "fixture-game", "manualVideo", true)));
        release.SetResult();
        await revalidation;
        Assert.Equal("manualVideo", (await Use(s => s.GetDetailAsync("fixture-game"))).VideoId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inflight_automatic_work_cannot_overwrite_operator_ownership_or_suppression(bool remove)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        identityBarrier = () => { entered.TrySetResult(); return release.Task; };
        var discovery = Use(s => s.ProcessCandidatesAsync(["fixture-game"], [Video()]));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        identityBarrier = null;
        await Use(s => s.OperateAsync(new(remove ? HighlightsOperatorAction.Remove : HighlightsOperatorAction.Attach, "fixture-game", remove ? null : "manualVideo", true)));
        release.SetResult();
        Assert.Equal(0, (await discovery).Selected);
        var state = await Use(s => s.InspectAsync("fixture-game"));
        if (remove) { Assert.True(state!.Highlights!.Suppressed); Assert.Null(state.Highlights.SelectedVideoId); }
        else Assert.Equal("manualVideo", state!.Highlights!.SelectedVideoId);
    }

    [Fact]
    public async Task Concurrent_discovery_keeps_one_first_selection()
    {
        var results = await Task.WhenAll(Use(s => s.ProcessCandidatesAsync(["fixture-game"], [Video("candidate1")])),
            Use(s => s.ProcessCandidatesAsync(["fixture-game"], [Video("candidate2")])));
        Assert.Equal(1, results.Sum(r => r.Selected));
        Assert.Contains((await Use(s => s.GetDetailAsync("fixture-game"))).VideoId, new[] { "candidate1", "candidate2" });
    }

    [Theory]
    [InlineData("ordinary", false)]
    [InlineData("bounded", true)]
    [InlineData("uninitialized", false)]
    [InlineData("completed", false)]
    [InlineData("outside", false)]
    public async Task Launch_catchup_has_a_separate_fixed_boundary_and_never_initializes_legacy_retry_window(string variant, bool expected)
    {
        clock.Now = new(2026, 10, 12, 3, 0, 0, TimeSpan.Zero);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            await db.Games.ExecuteUpdateAsync(s => s.SetProperty(g => g.CompletionConfirmedAt, (DateTime?)null)
                .SetProperty(g => g.HighlightsDiscoveryExpiresAt, (DateTime?)null));
            if (variant != "uninitialized")
            {
                var operations = await db.HighlightsOperations.SingleAsync();
                operations.LaunchBoundaryAt = variant == "outside" ? new DateTime(2026, 10, 14, 3, 0, 0, DateTimeKind.Utc) : clock.Now.UtcDateTime;
                operations.CatchupWindowStart = operations.LaunchBoundaryAt.Value.AddDays(-7);
                operations.CatchupRemainingGameIds = ["fixture-game"];
                if (variant == "completed") operations.CatchupCompletedAt = clock.Now.UtcDateTime;
                await db.SaveChangesAsync();
            }
        }
        var result = await Use(s => s.ProcessCandidatesAsync(["fixture-game"], [Video()], catchup: variant != "ordinary"));
        Assert.Equal(expected ? 1 : 0, result.Selected);
        var persisted = await Use(s => s.InspectAsync("fixture-game"));
        Assert.Null(persisted!.Game.HighlightsDiscoveryExpiresAt);
        await host.DisposeAsync(); host = CreateHost();
        Assert.Null((await Use(s => s.InspectAsync("fixture-game")))!.Game.HighlightsDiscoveryExpiresAt);
    }

    [Theory]
    [InlineData("expired-provider")]
    [InlineData("malformed-identity")]
    [InlineData("wrong-identity")]
    public async Task Readonly_detail_never_presents_stale_or_conflicting_persisted_payload(string variant)
    {
        await Use(s => s.ProcessCandidatesAsync(["fixture-game"], [Video()]));
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            if (variant == "expired-provider") await db.GameHighlights.ExecuteUpdateAsync(s => s.SetProperty(h => h.ProviderDataExpiresAt, clock.Now.AddDays(-1).UtcDateTime));
            else await db.Games.ExecuteUpdateAsync(s => s.SetProperty(g => g.HighlightsIdentityEvidenceJson, variant == "malformed-identity" ? "{}" : "{\"GamePk\":42}"));
        }
        var before = upstreamCalls;
        Assert.NotEqual(HighlightsDetailStatus.Playable, (await Use(s => s.GetDetailAsync("fixture-game"))).Status);
        Assert.Equal(before, upstreamCalls);
    }

    [Fact]
    public async Task Operator_cli_is_nonpublic_dry_run_by_default_and_requires_explicit_apply()
    {
        using var scope = host.Services.CreateScope();
        var command = scope.ServiceProvider.GetRequiredService<HighlightsOperatorCommand>();
        using var output = new StringWriter();
        Assert.Equal(0, await command.RunAsync(["remove", "fixture-game"], output));
        Assert.Contains("DryRun", output.ToString());
        Assert.Null((await Use(s => s.InspectAsync("fixture-game")))!.Highlights);
        Assert.Equal(0, await command.RunAsync(["remove", "fixture-game", "--apply"], output));
        Assert.True((await Use(s => s.InspectAsync("fixture-game")))!.Highlights!.Suppressed);
        Assert.Equal(2, await command.RunAsync(["attach", "fixture-game", "manualVideo", "--unknown"], output));
        Assert.Equal(0, await command.RunAsync(["inspect", "fixture-game"], output));
        Assert.Contains("Suppressed", output.ToString()); Assert.DoesNotContain("sentinel-not-live", output.ToString());
        using var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/highlights/fixture-game", new StringContent("{}"))).StatusCode);
    }

    [Fact]
    public async Task Operator_validation_respects_durable_quota_after_host_restart()
    {
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            var state = await db.HighlightsOperations.SingleAsync();
            state.QuotaDay = new DateOnly(2026, 10, 6); state.QuotaUnitsUsed = 999;
            await db.SaveChangesAsync();
        }
        await host.DisposeAsync(); host = CreateHost();
        var result = await Use(s => s.OperateAsync(new(HighlightsOperatorAction.Attach, "fixture-game", "manualVideo", true)));
        Assert.False(result.Applied); Assert.Equal("BudgetExhausted", result.Reason);
        Assert.Null((await Use(s => s.InspectAsync("fixture-game")))!.Highlights);
    }

    [Theory]
    [InlineData("resumed")]
    [InlineData("other-matchup")]
    [InlineData("iso-conflict")]
    [InlineData("provider-stale")]
    [InlineData("unknown-team-id")]
    [InlineData("conflicting-game-number")]
    [InlineData("duplicate-event-contest")]
    [InlineData("different-mlb-teams")]
    public async Task Additional_conflicting_or_stale_evidence_stays_unselected(string variant)
    {
        var video = Video();
        if (variant == "resumed") schedule["dates"]![0]!["games"]![0]!["resumedFromDate"] = "2026-10-05";
        if (variant == "other-matchup") video = video with { Description = "Dodgers vs. Braves and Mets vs. Phillies Highlights" };
        if (variant == "iso-conflict") video = video with { Description = "Single game highlights 2026-10-05" };
        if (variant == "provider-stale") video = video with { ValidatedAtUtc = clock.Now.AddDays(-31) };
        if (variant == "unknown-team-id") summary["header"]!["competitions"]![0]!["competitors"]![0]!["team"]!["id"] = "999";
        if (variant == "conflicting-game-number") video = video with { Description = "gamePk: 849819 Game 2" };
        if (variant == "duplicate-event-contest") schedule["dates"]![0]!["games"]!.AsArray().Add(schedule["dates"]![0]!["games"]![0]!.DeepClone());
        if (variant == "different-mlb-teams") schedule["dates"]![0]!["games"]![0]!["teams"]!["home"]!["team"]!["id"] = 121;
        Assert.Equal(0, (await Use(s => s.ProcessCandidatesAsync(["fixture-game"], [video]))).Selected);
    }

    [Fact]
    public async Task Narrow_iso_title_is_supported_but_ambiguous_description_never_qualifies()
    {
        var video = Video(title: "Dodgers vs. Braves Highlights (2026-10-06) | MLB Highlights");
        Assert.Equal(1, (await Use(s => s.ProcessCandidatesAsync(["fixture-game"], [video]))).Selected);
    }

    [Fact]
    public async Task Disabled_discovery_never_calls_upstream_or_advertises_pending_and_transient_identity_preserves_selection()
    {
        await Use(s => s.ProcessCandidatesAsync(["fixture-game"], [Video()]));
        identityFailure = true;
        await Use(s => s.RevalidateSelectionsAsync([Video()]));
        Assert.Equal(HighlightsDetailStatus.Playable, (await Use(s => s.GetDetailAsync("fixture-game"))).Status);
        identityFailure = false;
        await Use(s => s.OperateAsync(new(HighlightsOperatorAction.Remove, "fixture-game", Apply: true)));
        await Use(s => s.OperateAsync(new(HighlightsOperatorAction.Resume, "fixture-game", Apply: true)));
        Assert.Equal(HighlightsDetailStatus.Pending, (await Use(s => s.GetDetailAsync("fixture-game"))).Status);
        enabled = false; await host.DisposeAsync(); host = CreateHost();
        var before = upstreamCalls;
        Assert.Equal(0, (await Use(s => s.ProcessCandidatesAsync(["fixture-game"], [Video()]))).Selected);
        Assert.Equal(HighlightsDetailStatus.Unavailable, (await Use(s => s.GetDetailAsync("fixture-game"))).Status);
        Assert.Equal(before, upstreamCalls);
    }

    [Fact]
    public async Task Expired_payload_and_changed_authoritative_identity_never_claim_playable_or_pending()
    {
        await Use(s => s.ProcessCandidatesAsync(["fixture-game"], [Video()]));
        clock.Now = clock.Now.AddDays(31);
        Assert.Equal(HighlightsDetailStatus.Unavailable, (await Use(s => s.GetDetailAsync("fixture-game"))).Status);
        clock.Now = clock.Now.AddDays(-30);
        schedule["dates"]![0]!["games"]![0]!["gamePk"] = 42;
        Assert.Equal(0, (await Use(s => s.ProcessCandidatesAsync(["fixture-game"], [Video("newContest")]))).Selected);
        Assert.Equal(HighlightsDetailStatus.Unavailable, (await Use(s => s.GetDetailAsync("fixture-game"))).Status);
    }

    [Fact]
    public async Task Multiple_event_ids_cannot_claim_the_same_authoritative_contest()
    {
        await Use(s => s.ProcessCandidatesAsync(["fixture-game"], [Video()]));
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            db.Games.Add(new Game { Id = "other-event", Sport = "mlb", Status = "final", HomeTeam = "Atlanta Braves", AwayTeam = "Los Angeles Dodgers", HomeTeamAbbreviation = "ATL", AwayTeamAbbreviation = "LAD", HomeTeamId = "15", AwayTeamId = "19", SeasonYear = 2026, ScheduledStart = new DateTime(2026, 10, 6, 22, 0, 0, DateTimeKind.Utc), CompletionConfirmedAt = clock.Now.UtcDateTime, HighlightsDiscoveryExpiresAt = new DateTime(2026, 10, 10, 3, 0, 0, DateTimeKind.Utc) });
            await db.SaveChangesAsync();
        }
        summary["header"]!["id"] = "other-event";
        summary["header"]!["competitions"]![0]!["id"] = "other-event";
        Assert.Equal(0, (await Use(s => s.ProcessCandidatesAsync(["other-event"], [Video()]))).Selected);
        Assert.Equal("autoVideo", (await Use(s => s.GetDetailAsync("fixture-game"))).VideoId);
    }

    [Fact]
    public async Task Operator_unused_quota_refund_never_subtracts_from_a_new_day_reservation()
    {
        var rolledOver = false;
        providerHook = () =>
        {
            if (rolledOver) return;
            rolledOver = true;
            clock.Now = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);
            using var scope = host.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().HighlightsOperations.ExecuteUpdate(s => s
                .SetProperty(o => o.QuotaDay, new DateOnly(2026, 10, 7)).SetProperty(o => o.QuotaUnitsUsed, 900));
        };
        Assert.True((await Use(s => s.OperateAsync(new(HighlightsOperatorAction.Attach, "fixture-game", "manualVideo", true)))).Applied);
        using var read = host.Services.CreateScope();
        var operations = await read.ServiceProvider.GetRequiredService<backend.Services.GameHighlightsInspectionService>().InspectOperationsAsync();
        Assert.Equal(901, operations.QuotaUnitsUsed);
    }

    [Theory]
    [InlineData("missing-key")]
    [InlineData("zero-budget")]
    [InlineData("bad-pages")]
    [InlineData("bad-key")]
    public async Task Invalid_configuration_cannot_publish_or_advertise_a_search(string variant)
    {
        await Use(s => s.ProcessCandidatesAsync(["fixture-game"], []));
        configure = o =>
        {
            if (variant == "missing-key") o.ApiKey = null;
            if (variant == "zero-budget") o.DailyUnitLimit = 0;
            if (variant == "bad-pages") o.MaxUploadPages = 5;
            if (variant == "bad-key") o.ApiKey = "sentinel\r\n";
        };
        await host.DisposeAsync(); host = CreateHost();
        var before = upstreamCalls;
        Assert.Equal(0, (await Use(s => s.ProcessCandidatesAsync(["fixture-game"], [Video()]))).Selected);
        Assert.Equal(HighlightsDetailStatus.Unavailable, (await Use(s => s.GetDetailAsync("fixture-game"))).Status);
        Assert.Equal(before, upstreamCalls);
    }

    [Fact]
    public async Task Manual_game_choice_resolves_ambiguous_contest_without_relaxing_provider_or_content()
    {
        schedule["dates"]![0]!["games"]![0]!["doubleHeader"] = "Y";
        schedule["dates"]![0]!["games"]![0]!["gameNumber"] = 2;
        Assert.Equal(0, (await Use(s => s.ProcessCandidatesAsync(["fixture-game"], [Video()]))).Selected);
        Assert.True((await Use(s => s.OperateAsync(new(HighlightsOperatorAction.Attach, "fixture-game", "manualVideo", true)))).Applied);
        Assert.Equal(HighlightsDetailStatus.Playable, (await Use(s => s.GetDetailAsync("fixture-game"))).Status);
    }

    [Fact]
    public async Task Refreshed_official_date_cannot_make_an_old_selection_for_another_date_playable()
    {
        await Use(s => s.ProcessCandidatesAsync(["fixture-game"], [Video()]));
        schedule["dates"]![0]!["games"]![0]!["officialDate"] = "2026-10-05";
        Assert.Equal(0, (await Use(s => s.ProcessCandidatesAsync(["fixture-game"], [Video("wrongDate")]))).Selected);
        Assert.Equal(HighlightsDetailStatus.Unavailable, (await Use(s => s.GetDetailAsync("fixture-game"))).Status);
        Assert.Equal(1, (await Use(s => s.ProcessCandidatesAsync(["fixture-game"], [Video("corrected", providerTitle.Replace("10/6/26", "10/5/26"))]))).Selected);
    }

    [Theory]
    [InlineData("Suspended")]
    [InlineData("Postponed")]
    [InlineData("Cancelled")]
    public async Task Explicit_MLB_noncompletion_overrides_stored_and_ESPN_final_for_detail_eligibility(string status)
    {
        schedule["dates"]![0]!["games"]![0]!["status"]!["detailedState"] = status;
        await Use(s => s.ProcessCandidatesAsync(["fixture-game"], [Video()]));
        Assert.Equal(HighlightsDetailStatus.Hidden, (await Use(s => s.GetDetailAsync("fixture-game"))).Status);
        Assert.False((await Use(s => s.InspectAsync("fixture-game")))!.IsActuallyCompleted);
        await Use(s => s.OperateAsync(new(HighlightsOperatorAction.Remove, "fixture-game", Apply: true)));
        await Use(s => s.OperateAsync(new(HighlightsOperatorAction.Resume, "fixture-game", Apply: true)));
        Assert.Equal(HighlightsDetailStatus.Hidden, (await Use(s => s.GetDetailAsync("fixture-game"))).Status);
        await host.DisposeAsync(); host = CreateHost();
        Assert.False((await Use(s => s.InspectAsync("fixture-game")))!.IsActuallyCompleted);
    }

    [Fact]
    public async Task Interrupted_authoritative_response_is_transient_and_preserves_selection()
    {
        await Use(s => s.ProcessCandidatesAsync(["fixture-game"], [Video()]));
        identityStreamFailure = true;
        Assert.Equal(0, (await Use(s => s.RevalidateSelectionsAsync([Video()]))).Revalidated);
        Assert.Equal("autoVideo", (await Use(s => s.GetDetailAsync("fixture-game"))).VideoId);
    }

    [Fact]
    public async Task One_manual_candidate_cannot_remain_chosen_for_two_distinct_doubleheader_contests()
    {
        schedule["dates"]![0]!["games"]![0]!["doubleHeader"] = "Y";
        Assert.True((await Use(s => s.OperateAsync(new(HighlightsOperatorAction.Attach, "fixture-game", "manualVideo", true)))).Applied);
        var row = schedule["dates"]![0]!["games"]![0]!.DeepClone();
        row["gamePk"] = 849820; row["gameNumber"] = 2; row["gameDate"] = "2026-10-06T23:00:00Z";
        schedule["dates"]![0]!["games"]!.AsArray().Add(row);
        summary["header"]!["id"] = "second-game";
        summary["header"]!["competitions"]![0]!["id"] = "second-game";
        summary["header"]!["competitions"]![0]!["date"] = "2026-10-06T23:00:00Z";
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            db.Games.Add(new Game { Id = "second-game", Sport = "mlb", Status = "final", HomeTeam = "Atlanta Braves", AwayTeam = "Los Angeles Dodgers", HomeTeamAbbreviation = "ATL", AwayTeamAbbreviation = "LAD", HomeTeamId = "15", AwayTeamId = "19", SeasonYear = 2026, ScheduledStart = new DateTime(2026, 10, 6, 23, 0, 0, DateTimeKind.Utc) });
            await db.SaveChangesAsync();
        }
        var rejected = await Use(s => s.OperateAsync(new(HighlightsOperatorAction.Attach, "second-game", "manualVideo", true)));
        Assert.False(rejected.Applied); Assert.Equal("VideoAlreadySelected", rejected.Reason);
        Assert.Equal("manualVideo", (await Use(s => s.GetDetailAsync("fixture-game"))).VideoId);
    }

    [Fact]
    public async Task Identity_recording_and_a_concurrent_pro_evidence_write_to_the_same_game_both_commit()
    {
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Database.ExecuteSqlRawAsync(
                await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "035_ProHypeLifecycle.sql")));
        gameUpdates.Arm();
        var processing = Use(s => s.ProcessCandidatesAsync(["fixture-game"], []));
        await gameUpdates.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));

        await using var refresh = new ExcitementDbContext(new DbContextOptionsBuilder<ExcitementDbContext>().UseNpgsql(database.GetConnectionString()).Options);
        var stored = await refresh.Games.SingleAsync(g => g.Id == "fixture-game");
        stored.ProEvidenceJson = "{}";
        var writing = refresh.SaveChangesAsync();
        await GameUpdateGate.WaitForLockWaitAsync(database.GetConnectionString());
        gameUpdates.Release();

        await Task.WhenAll(processing, writing);
        var saved = (await Use(s => s.InspectAsync("fixture-game")))!.Game;
        Assert.Equal(849819, saved.MlbGamePk);
        Assert.Equal("{}", saved.ProEvidenceJson);
    }

    public async Task DisposeAsync()
    {
        if (host is not null) await host.DisposeAsync();
        await entry.DisposeAsync();
        await database.DisposeAsync();
    }
}
