using System.Net;
using backend.Data;
using backend.Services.Highlights;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace backend.tests;

public sealed class YouTubeHighlightsDatabase : IAsyncLifetime
{
    public IsolatedPostgreSql Database { get; } = new IsolatedPostgreSql();
    public Task InitializeAsync() => Database.StartAsync();
    public Task DisposeAsync() => Database.DisposeAsync().AsTask();
}

public sealed class YouTubeHighlightsTests(YouTubeHighlightsDatabase fixture) : IClassFixture<YouTubeHighlightsDatabase>
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Provider : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new(HttpStatusCode.OK) { Content = new StringContent("{}") };
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? RespondAsync { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            Assert.DoesNotContain("sentinel-secret", request.RequestUri.ToString());
            Assert.Equal("sentinel-secret", Assert.Single(request.Headers.GetValues("x-goog-api-key")));
            return RespondAsync is null ? Task.FromResult(Respond(request)) : RespondAsync(request, cancellationToken);
        }
    }

    private WebApplicationFactory<Program> Host(AppFactory entry, Provider provider, Clock clock, Action<YouTubeHighlightsOptions>? configure = null, Logs? logs = null)
        => entry.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDbContextOptionsConfiguration<ExcitementDbContext>>();
            services.RemoveAll<DbContextOptions<ExcitementDbContext>>();
            services.AddDbContext<ExcitementDbContext>(options => options.UseNpgsql(fixture.Database.GetConnectionString()));
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
            services.Configure<YouTubeHighlightsOptions>(options =>
            {
                options.ApiKey = "sentinel-secret";
                configure?.Invoke(options);
            });
            services.AddHttpClient("YouTubeHighlights").ConfigurePrimaryHttpMessageHandler(() => provider);
            if (logs is not null) services.AddLogging(logging => logging.AddProvider(logs));
        }));

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private const string Channel = "UCoLrcjPV5PbUrUyXq5mjc_A";
    private const string VideoId = "EQMJSt1j-uA";
    private static string ChannelBody => """{"items":[{"id":"UCoLrcjPV5PbUrUyXq5mjc_A","contentDetails":{"relatedPlaylists":{"uploads":"UUoLrcjPV5PbUrUyXq5mjc_A"}}}]}""";
    private static string VideoBody => """{"items":[{"id":"EQMJSt1j-uA","snippet":{"channelId":"UCoLrcjPV5PbUrUyXq5mjc_A","title":"Dodgers vs. Braves Highlights (10/6/26) | MLB Highlights","description":"Single game recap","publishedAt":"2026-10-07T01:00:00Z","liveBroadcastContent":"none"},"contentDetails":{"duration":"PT8M12S"},"status":{"privacyStatus":"public","uploadStatus":"processed","embeddable":true,"madeForKids":false}}]}""";

    [Fact]
    public async Task Approved_channel_uploads_return_validated_metadata_not_an_upload_date_match()
    {
        using var entry = new AppFactory();
        using var provider = new Provider
        {
            Respond = request => request.RequestUri!.AbsolutePath.Split('/').Last() switch
            {
                "channels" => Json(ChannelBody),
                "playlistItems" => Json("""{"items":[{"contentDetails":{"videoId":"EQMJSt1j-uA"}}]}"""),
                "videos" => Json(VideoBody),
                _ => throw new InvalidOperationException("Unsupported API")
            }
        };
        var clock = new Clock();
        await using var host = Host(entry, provider, clock, options => options.Enabled = true);
        var result = await host.Services.GetRequiredService<YouTubeHighlightsClient>().DiscoverUploadsAsync();
        Assert.Equal(YouTubeOutcome.Success, result.Outcome);
        var video = Assert.Single(result.Videos);
        Assert.Equal(VideoId, video.VideoId);
        Assert.Equal(YouTubeAvailability.Playable, video.Availability);
        Assert.Equal(Channel, video.ChannelId);
        Assert.Equal("Dodgers vs. Braves Highlights (10/6/26) | MLB Highlights", video.Title);
        Assert.Equal("Single game recap", video.Description);
        Assert.Equal(TimeSpan.FromSeconds(492), video.Duration);
        Assert.False(video.MadeForKids);
        Assert.Equal(clock.Now, video.ValidatedAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 1, 0, 0, TimeSpan.Zero), video.PublishedAtUtc);
        Assert.Equal(3, result.UnitsConsumed);
        Assert.Null(result.NextPageToken);
        Assert.All(provider.Requests, url => Assert.StartsWith("https://www.googleapis.com/youtube/v3/", url));
        Assert.Contains(provider.Requests, url => url.Contains("id=" + Channel));
        Assert.Contains(provider.Requests, url => url.Contains("playlistId=UUoLrcjPV5PbUrUyXq5mjc_A") && url.Contains("maxResults=50"));
    }

    [Theory]
    [InlineData("missing-key", YouTubeOutcome.MissingApiKey)]
    [InlineData("oversized-budget", YouTubeOutcome.InvalidConfiguration)]
    [InlineData("negative-budget", YouTubeOutcome.InvalidConfiguration)]
    [InlineData("oversized-pages", YouTubeOutcome.InvalidConfiguration)]
    [InlineData("invalid-key", YouTubeOutcome.InvalidConfiguration)]
    public async Task Configuration_fails_closed_without_provider_requests(string variant, YouTubeOutcome expected)
    {
        using var entry = new AppFactory();
        using var provider = new Provider();
        await using var host = Host(entry, provider, new Clock(), options =>
        {
            options.Enabled = true;
            if (variant == "missing-key") options.ApiKey = " ";
            if (variant == "invalid-key") options.ApiKey = "sentinel-secret\r\n";
            if (variant == "oversized-budget") options.DailyUnitLimit = 1001;
            if (variant == "negative-budget") options.DailyUnitLimit = -1;
            if (variant == "oversized-pages") options.MaxUploadPages = 5;
        });
        var result = await host.Services.GetRequiredService<YouTubeHighlightsClient>().DiscoverUploadsAsync();
        Assert.Equal(expected, result.Outcome);
        Assert.Empty(provider.Requests);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("different-id")]
    [InlineData("missing-playlist")]
    public async Task Channel_identity_must_be_verified_before_reading_uploads(string variant)
    {
        using var entry = new AppFactory();
        using var provider = new Provider { Respond = _ => Json(variant switch
        {
            "missing" => "{\"items\":[]}",
            "different-id" => ChannelBody.Replace(Channel, "UCunapproved"),
            _ => "{\"items\":[{\"id\":\"" + Channel + "\"}]}"
        }) };
        await using var host = Host(entry, provider, new Clock(), options => options.Enabled = true);
        var result = await host.Services.GetRequiredService<YouTubeHighlightsClient>().DiscoverUploadsAsync();
        Assert.Equal(YouTubeOutcome.ChannelUnverified, result.Outcome);
        Assert.Empty(result.Videos);
        Assert.Single(provider.Requests);
    }

    [Theory]
    [InlineData("network", YouTubeOutcome.TemporaryFailure)]
    [InlineData("server", YouTubeOutcome.TemporaryFailure)]
    [InlineData("throttle", YouTubeOutcome.Throttled)]
    [InlineData("quota", YouTubeOutcome.QuotaExhausted)]
    [InlineData("rate-limit", YouTubeOutcome.Throttled)]
    [InlineData("forbidden", YouTubeOutcome.InvalidConfiguration)]
    [InlineData("malformed", YouTubeOutcome.InvalidResponse)]
    [InlineData("missing-items", YouTubeOutcome.InvalidResponse)]
    public async Task Provider_failures_return_safe_nondefinitive_reasons(string variant, YouTubeOutcome expected)
    {
        using var entry = new AppFactory();
        using var provider = new Provider { Respond = _ => variant switch
        {
            "network" => throw new HttpRequestException("https://example.test?key=sentinel-secret"),
            "server" => new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("sentinel-secret") },
            "throttle" => new(HttpStatusCode.TooManyRequests),
            "quota" => new(HttpStatusCode.Forbidden) { Content = new StringContent("{\"error\":{\"errors\":[{\"reason\":\"quotaExceeded\"}]}}") },
            "rate-limit" => new(HttpStatusCode.Forbidden) { Content = new StringContent("{\"error\":{\"errors\":[{\"reason\":\"rateLimitExceeded\"}]}}") },
            "forbidden" => new(HttpStatusCode.Forbidden),
            "malformed" => Json("sentinel-secret"),
            _ => Json("{}")
        } };
        await using var host = Host(entry, provider, new Clock(), options => options.Enabled = true);
        var result = await host.Services.GetRequiredService<YouTubeHighlightsClient>().DiscoverUploadsAsync();
        Assert.Equal(expected, result.Outcome);
        Assert.Empty(result.Videos);
        Assert.Equal(1, result.UnitsConsumed);
        Assert.DoesNotContain("sentinel-secret", System.Text.Json.JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Lower_daily_allocation_stops_mid_operation_and_resets_at_Pacific_midnight()
    {
        using var entry = new AppFactory();
        using var provider = new Provider { Respond = request => request.RequestUri!.AbsolutePath.EndsWith("channels")
            ? Json(ChannelBody) : Json("{\"items\":[{\"contentDetails\":{\"videoId\":\"EQMJSt1j-uA\"}}]}") };
        var clock = new Clock { Now = new(2026, 10, 7, 6, 55, 0, TimeSpan.Zero) };
        await using var host = Host(entry, provider, clock, options => { options.Enabled = true; options.DailyUnitLimit = 2; });
        var adapter = host.Services.GetRequiredService<YouTubeHighlightsClient>();
        var result = await adapter.DiscoverUploadsAsync();
        Assert.Equal(YouTubeOutcome.BudgetExhausted, result.Outcome);
        Assert.Empty(result.Videos);
        Assert.Equal(2, result.UnitsConsumed);
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 7, 0, 0, TimeSpan.Zero), result.RetryAtUtc);
        Assert.Equal(2, provider.Requests.Count);
        result = await adapter.DiscoverUploadsAsync();
        Assert.Equal(YouTubeOutcome.BudgetExhausted, result.Outcome);
        Assert.Equal(0, result.UnitsConsumed);
        Assert.Equal(2, provider.Requests.Count);
        clock.Now = clock.Now.AddMinutes(5);
        result = await adapter.DiscoverUploadsAsync();
        Assert.Equal(2, result.UnitsConsumed);
        Assert.Equal(4, provider.Requests.Count);
    }

    [Theory]
    [InlineData(false, YouTubeOutcome.Throttled)]
    [InlineData(true, YouTubeOutcome.QuotaExhausted)]
    public async Task Provider_backoff_is_shared_and_does_not_retry_or_report_removed_videos(bool quota, YouTubeOutcome expected)
    {
        using var entry = new AppFactory();
        using var provider = new Provider { Respond = _ =>
        {
            var response = quota
                ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"error\":{\"errors\":[{\"reason\":\"quotaExceeded\"}]}}") }
                : new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(1));
            return response;
        } };
        var clock = new Clock();
        await using var host = Host(entry, provider, clock, options => options.Enabled = true);
        var adapter = host.Services.GetRequiredService<YouTubeHighlightsClient>();
        var failed = await adapter.DiscoverUploadsAsync();
        Assert.Equal(expected, failed.Outcome);
        Assert.Empty(failed.Videos);
        Assert.Equal(quota ? new DateTimeOffset(2026, 10, 8, 7, 0, 0, TimeSpan.Zero) : clock.Now.AddHours(1), failed.RetryAtUtc);
        var waiting = await adapter.DiscoverUploadsAsync();
        Assert.Equal(YouTubeOutcome.Backoff, waiting.Outcome);
        Assert.Equal(0, waiting.UnitsConsumed);
        Assert.Single(provider.Requests);
        clock.Now = failed.RetryAtUtc!.Value;
        provider.Respond = _ => Json("{\"items\":[]}");
        var retried = await adapter.DiscoverUploadsAsync();
        Assert.Equal(YouTubeOutcome.ChannelUnverified, retried.Outcome);
        Assert.Equal(2, provider.Requests.Count);
    }

    [Theory]
    [InlineData("removed", YouTubeAvailability.Removed)]
    [InlineData("private", YouTubeAvailability.Private)]
    [InlineData("not-embeddable", YouTubeAvailability.NotEmbeddable)]
    [InlineData("wrong-publisher", YouTubeAvailability.UnapprovedPublisher)]
    [InlineData("not-processed", YouTubeAvailability.NotProcessed)]
    [InlineData("made-for-kids", YouTubeAvailability.MadeForKids)]
    [InlineData("unknown-kids", YouTubeAvailability.UnknownMadeForKids)]
    public async Task Definitive_video_evidence_is_not_presented_as_playable(string variant, YouTubeAvailability expected)
    {
        var body = System.Text.Json.Nodes.JsonNode.Parse(VideoBody)!;
        var item = body["items"]![0]!;
        switch (variant)
        {
            case "removed": body["items"] = new System.Text.Json.Nodes.JsonArray(); break;
            case "private": item["status"]!["privacyStatus"] = "private"; break;
            case "not-embeddable": item["status"]!["embeddable"] = false; break;
            case "wrong-publisher": item["snippet"]!["channelId"] = "UCunapproved"; break;
            case "not-processed": item["status"]!["uploadStatus"] = "failed"; break;
            case "made-for-kids": item["status"]!["madeForKids"] = true; break;
            case "unknown-kids": item["status"]!.AsObject().Remove("madeForKids"); break;
        }
        using var entry = new AppFactory();
        using var provider = new Provider { Respond = request => request.RequestUri!.AbsolutePath.Split('/').Last() switch
        {
            "channels" => Json(ChannelBody),
            "playlistItems" => Json("{\"items\":[{\"contentDetails\":{\"videoId\":\"EQMJSt1j-uA\"}}]}"),
            _ => Json(body.ToJsonString())
        } };
        await using var host = Host(entry, provider, new Clock(), options => options.Enabled = true);
        var result = await host.Services.GetRequiredService<YouTubeHighlightsClient>().DiscoverUploadsAsync();
        Assert.Equal(YouTubeOutcome.Success, result.Outcome);
        Assert.Equal(expected, Assert.Single(result.Videos).Availability);
    }

    [Fact]
    public async Task Selected_video_validation_batches_at_most_50_ids_and_reports_missing_only_after_success()
    {
        var ids = Enumerable.Range(1, 121).Select(i => i.ToString("D11")).ToArray();
        using var entry = new AppFactory();
        using var provider = new Provider { Respond = request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("channels")) return Json(ChannelBody);
            Assert.EndsWith("/videos", request.RequestUri.AbsolutePath);
            var batch = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query)["id"].ToString().Split(',');
            Assert.InRange(batch.Length, 1, 50);
            var items = new System.Text.Json.Nodes.JsonArray();
            foreach (var id in batch.Where(id => id != ids.Last()))
            {
                var item = System.Text.Json.Nodes.JsonNode.Parse(VideoBody)!["items"]![0]!.DeepClone();
                item["id"] = id;
                items.Add(item);
            }
            return Json(new System.Text.Json.Nodes.JsonObject { ["items"] = items }.ToJsonString());
        } };
        await using var host = Host(entry, provider, new Clock(), options => options.Enabled = true);
        var result = await host.Services.GetRequiredService<YouTubeHighlightsClient>().ValidateVideosAsync(ids);
        Assert.Equal(YouTubeOutcome.Success, result.Outcome);
        Assert.Equal(121, result.Videos.Count);
        Assert.Equal(120, result.Videos.Count(video => video.Availability == YouTubeAvailability.Playable));
        Assert.Equal(ids.Last(), Assert.Single(result.Videos, video => video.Availability == YouTubeAvailability.Removed).VideoId);
        Assert.Equal(4, result.UnitsConsumed);
        Assert.InRange(provider.Requests.Count, 1, 4);
    }

    [Fact]
    public async Task Upload_pagination_is_bounded_and_returns_continuation_instead_of_extending_history()
    {
        using var entry = new AppFactory();
        var uploadPages = 0;
        using var provider = new Provider { Respond = request =>
        {
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri!.Query);
            if (request.RequestUri.AbsolutePath.EndsWith("channels")) return Json(ChannelBody);
            if (request.RequestUri.AbsolutePath.EndsWith("playlistItems"))
            {
                uploadPages++;
                Assert.Equal(uploadPages == 1 ? "initial+token=" : "second", query["pageToken"].ToString());
                Assert.Equal("50", query["maxResults"].ToString());
                return Json("{\"items\":[{\"contentDetails\":{\"videoId\":\"" + (uploadPages == 1 ? VideoId : "pamkQqdcdlI") + "\"}}],\"nextPageToken\":\"" + (uploadPages == 1 ? "second" : "third") + "\"}");
            }
            var items = new System.Text.Json.Nodes.JsonArray();
            foreach (var id in query["id"].ToString().Split(','))
            {
                var item = System.Text.Json.Nodes.JsonNode.Parse(VideoBody)!["items"]![0]!.DeepClone();
                item["id"] = id; items.Add(item);
            }
            return Json(new System.Text.Json.Nodes.JsonObject { ["items"] = items }.ToJsonString());
        } };
        await using var host = Host(entry, provider, new Clock(), options => options.Enabled = true);
        var result = await host.Services.GetRequiredService<YouTubeHighlightsClient>().DiscoverUploadsAsync("initial+token=");
        Assert.Equal(YouTubeOutcome.Success, result.Outcome);
        Assert.Equal(2, result.Videos.Count);
        Assert.Equal("third", result.NextPageToken);
        Assert.Equal(2, uploadPages);
        Assert.InRange(result.UnitsConsumed, 4, 5);
    }

    [Theory]
    [InlineData("bad-id")]
    [InlineData("too-many-ids")]
    [InlineData("too-long-token")]
    public async Task Caller_work_is_bounded_before_any_provider_request(string variant)
    {
        using var entry = new AppFactory();
        using var provider = new Provider();
        await using var host = Host(entry, provider, new Clock(), options => options.Enabled = true);
        var adapter = host.Services.GetRequiredService<YouTubeHighlightsClient>();
        var result = variant == "too-long-token" ? await adapter.DiscoverUploadsAsync(new string('x', 513))
            : await adapter.ValidateVideosAsync(variant == "bad-id" ? ["invalid&id=oops"] : Enumerable.Range(1, 201).Select(i => i.ToString("D11")).ToArray());
        Assert.Equal(YouTubeOutcome.InvalidRequest, result.Outcome);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task Pending_uploads_do_not_abort_discovery_of_playable_game_highlights()
    {
        var body = System.Text.Json.Nodes.JsonNode.Parse(VideoBody)!;
        var pending = body["items"]![0]!.DeepClone();
        pending["id"] = "pending0001";
        pending["snippet"]!["liveBroadcastContent"] = "upcoming";
        pending["contentDetails"]!["duration"] = "P0D";
        pending["status"]!["uploadStatus"] = "uploaded";
        body["items"]!.AsArray().Insert(0, pending);
        using var entry = new AppFactory();
        using var provider = new Provider { Respond = request => request.RequestUri!.AbsolutePath.Split('/').Last() switch
        {
            "channels" => Json(ChannelBody),
            "playlistItems" => Json("""{"items":[{"contentDetails":{"videoId":"pending0001"}},{"contentDetails":{"videoId":"EQMJSt1j-uA"}}]}"""),
            "videos" => Json(body.ToJsonString()),
            _ => throw new InvalidOperationException("Unsupported API")
        } };
        await using var host = Host(entry, provider, new Clock(), options => options.Enabled = true);
        var result = await host.Services.GetRequiredService<YouTubeHighlightsClient>().DiscoverUploadsAsync();
        Assert.Equal(YouTubeOutcome.Success, result.Outcome);
        Assert.Equal(2, result.Videos.Count);
        Assert.Equal(YouTubeAvailability.NotProcessed, result.Videos.Single(v => v.VideoId == "pending0001").Availability);
        Assert.Equal(YouTubeAvailability.Playable, result.Videos.Single(v => v.VideoId == VideoId).Availability);
        Assert.Equal(3, result.UnitsConsumed);
        Assert.Equal(3, provider.Requests.Count);
    }

    [Theory]
    [InlineData("upcoming", "uploaded", "P0D")]
    [InlineData("upcoming", "processed", "P0D")]
    [InlineData("live", "processed", "PT8M12S")]
    [InlineData("live", "uploaded", "P0D")]
    [InlineData("none", "uploaded", "P0D")]
    [InlineData("none", "failed", "P0D")]
    [InlineData("none", "rejected", "P0D")]
    [InlineData("none", "deleted", "P0D")]
    public async Task Known_not_ready_video_states_are_nonselectable_evidence(string broadcast, string uploadStatus, string duration)
    {
        var body = System.Text.Json.Nodes.JsonNode.Parse(VideoBody)!;
        var item = body["items"]![0]!;
        item["snippet"]!["liveBroadcastContent"] = broadcast;
        item["status"]!["uploadStatus"] = uploadStatus;
        item["contentDetails"]!["duration"] = duration;
        using var entry = new AppFactory();
        using var provider = new Provider { Respond = request => request.RequestUri!.AbsolutePath.Split('/').Last() switch
        {
            "channels" => Json(ChannelBody),
            "videos" => Json(body.ToJsonString()),
            _ => throw new InvalidOperationException("Unsupported API")
        } };
        await using var host = Host(entry, provider, new Clock(), options => options.Enabled = true);
        var result = await host.Services.GetRequiredService<YouTubeHighlightsClient>().ValidateVideosAsync([VideoId]);
        Assert.Equal(YouTubeOutcome.Success, result.Outcome);
        Assert.Equal(YouTubeAvailability.NotProcessed, Assert.Single(result.Videos).Availability);
    }

    [Theory]
    [InlineData("allowed", "[]", YouTubeAvailability.GloballyBlocked)]
    [InlineData("allowed", "[\"US\"]", YouTubeAvailability.Playable)]
    [InlineData("blocked", "[\"US\"]", YouTubeAvailability.Playable)]
    [InlineData("blocked", "[\"US\",\"CA\",\"GB\"]", YouTubeAvailability.Playable)]
    [InlineData("blocked", "[]", YouTubeAvailability.Playable)]
    public async Task Region_evidence_distinguishes_global_blocking_from_partial_restrictions(string property, string countries, YouTubeAvailability expected)
    {
        var body = System.Text.Json.Nodes.JsonNode.Parse(VideoBody)!;
        body["items"]![0]!["contentDetails"]!["regionRestriction"] = System.Text.Json.Nodes.JsonNode.Parse("{\"" + property + "\":" + countries + "}");
        using var entry = new AppFactory();
        using var provider = new Provider { Respond = request => request.RequestUri!.AbsolutePath.Split('/').Last() switch
        {
            "channels" => Json(ChannelBody),
            "i18nRegions" => Json("{\"items\":[{\"id\":\"US\"},{\"id\":\"CA\"},{\"id\":\"GB\"}]}"),
            _ => Json(body.ToJsonString())
        } };
        await using var host = Host(entry, provider, new Clock(), options => options.Enabled = true);
        var result = await host.Services.GetRequiredService<YouTubeHighlightsClient>().ValidateVideosAsync([VideoId]);
        Assert.Equal(YouTubeOutcome.Success, result.Outcome);
        var video = Assert.Single(result.Videos);
        Assert.Equal(expected, video.Availability);
        Assert.Equal(System.Text.Json.JsonSerializer.Deserialize<string[]>(countries), property == "allowed" ? video.AllowedRegions : video.BlockedRegions);
        Assert.InRange(provider.Requests.Count, 2, 3);
    }

    [Theory]
    [InlineData("missing-embeddable")]
    [InlineData("missing-channel")]
    [InlineData("missing-title")]
    [InlineData("unknown-broadcast")]
    [InlineData("zero-duration")]
    [InlineData("zero-day-duration")]
    [InlineData("negative-duration")]
    [InlineData("malformed-pending-duration")]
    [InlineData("bad-allowed-region")]
    [InlineData("conflicting-regions")]
    public async Task Malformed_or_incomplete_video_evidence_fails_closed_without_claiming_removal(string variant)
    {
        var body = System.Text.Json.Nodes.JsonNode.Parse(VideoBody)!;
        var video = body["items"]![0]!;
        switch (variant)
        {
            case "missing-embeddable": video["status"]!.AsObject().Remove("embeddable"); break;
            case "missing-channel": video["snippet"]!.AsObject().Remove("channelId"); break;
            case "missing-title": video["snippet"]!.AsObject().Remove("title"); break;
            case "unknown-broadcast": video["snippet"]!["liveBroadcastContent"] = "unknown"; break;
            case "zero-duration": video["contentDetails"]!["duration"] = "PT0S"; break;
            case "zero-day-duration": video["contentDetails"]!["duration"] = "P0D"; break;
            case "negative-duration": video["snippet"]!["liveBroadcastContent"] = "upcoming"; video["contentDetails"]!["duration"] = "-PT1S"; break;
            case "malformed-pending-duration": video["snippet"]!["liveBroadcastContent"] = "upcoming"; video["contentDetails"]!["duration"] = "invalid"; break;
            case "bad-allowed-region": video["contentDetails"]!["regionRestriction"] = System.Text.Json.Nodes.JsonNode.Parse("{\"allowed\":[\"XX\"]}"); break;
            case "conflicting-regions": video["contentDetails"]!["regionRestriction"] = System.Text.Json.Nodes.JsonNode.Parse("{\"allowed\":[\"US\"],\"blocked\":[\"US\"]}"); break;
        }
        using var entry = new AppFactory();
        using var provider = new Provider { Respond = request => request.RequestUri!.AbsolutePath.Split('/').Last() switch
        {
            "channels" => Json(ChannelBody),
            "i18nRegions" => Json("{\"items\":[{\"id\":\"US\"},{\"id\":\"CA\"},{\"id\":\"GB\"}]}"),
            _ => Json(body.ToJsonString())
        } };
        await using var host = Host(entry, provider, new Clock(), options => options.Enabled = true);
        var result = await host.Services.GetRequiredService<YouTubeHighlightsClient>().ValidateVideosAsync([VideoId]);
        Assert.Equal(YouTubeOutcome.InvalidResponse, result.Outcome);
        Assert.Empty(result.Videos);
    }

    [Theory]
    [InlineData("oversized-body")]
    [InlineData("pagination-gap")]
    [InlineData("looping-cursor")]
    [InlineData("invalid-cursor")]
    [InlineData("extra-video")]
    [InlineData("duplicate-video")]
    [InlineData("video-pagination")]
    public async Task Malformed_batches_and_pagination_never_establish_missing_video_evidence(string variant)
    {
        using var entry = new AppFactory();
        using var provider = new Provider { Respond = request =>
        {
            if (variant == "oversized-body") return Json("{\"items\":[],\"padding\":\"" + new string('x', 2 * 1024 * 1024) + "\"}");
            if (request.RequestUri!.AbsolutePath.EndsWith("channels")) return Json(ChannelBody);
            if (request.RequestUri.AbsolutePath.EndsWith("playlistItems")) return Json(variant switch
            {
                "pagination-gap" => "{\"items\":[{}]}",
                "looping-cursor" => "{\"items\":[],\"nextPageToken\":\"repeat\"}",
                "invalid-cursor" => "{\"items\":[],\"nextPageToken\":42}",
                _ => "{\"items\":[{\"contentDetails\":{\"videoId\":\"EQMJSt1j-uA\"}}]}"
            });
            var body = System.Text.Json.Nodes.JsonNode.Parse(VideoBody)!;
            if (variant == "extra-video") body["items"]![0]!["id"] = "pamkQqdcdlI";
            if (variant == "duplicate-video") body["items"]!.AsArray().Add(body["items"]![0]!.DeepClone());
            if (variant == "video-pagination") { body["items"] = new System.Text.Json.Nodes.JsonArray(); body["nextPageToken"] = "more"; }
            return Json(body.ToJsonString());
        } };
        await using var host = Host(entry, provider, new Clock(), options => options.Enabled = true);
        var result = await host.Services.GetRequiredService<YouTubeHighlightsClient>().DiscoverUploadsAsync();
        Assert.Equal(YouTubeOutcome.InvalidResponse, result.Outcome);
        Assert.Empty(result.Videos);
        Assert.InRange(provider.Requests.Count, 1, 4);
    }

    private sealed class Logs : ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Capture(Entries);
        public void Dispose() { }
        private sealed class Capture(System.Collections.Concurrent.ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Enqueue(formatter(state, exception) + exception);
        }
    }

    [Fact]
    public async Task Cancellation_is_sanitized_releases_the_shared_gate_and_does_not_create_removal_evidence()
    {
        using var entry = new AppFactory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = new Provider { RespondAsync = async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new HttpRequestException("sentinel-secret");
        } };
        var logs = new Logs();
        await using var host = Host(entry, provider, new Clock(), options => options.Enabled = true, logs);
        var adapter = host.Services.GetRequiredService<YouTubeHighlightsClient>();
        using var cancellation = new CancellationTokenSource();
        var pending = adapter.ValidateVideosAsync([VideoId], cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var queued = adapter.DiscoverUploadsAsync(cancellationToken: cancellation.Token);
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.DoesNotContain("sentinel-secret", error.ToString());
        Assert.Single(provider.Requests);
        provider.RespondAsync = null;
        provider.Respond = request => request.RequestUri!.AbsolutePath.EndsWith("channels") ? Json(ChannelBody) : Json(VideoBody);
        var retry = await adapter.ValidateVideosAsync([VideoId]);
        Assert.Equal(YouTubeOutcome.Success, retry.Outcome);
        Assert.Equal(YouTubeAvailability.Playable, Assert.Single(retry.Videos).Availability);
        Assert.DoesNotContain("sentinel-secret", string.Join('\n', logs.Entries));
    }

    [Fact]
    public async Task Failed_provider_operation_cannot_break_rendering_or_leak_key_or_trigger_renderer_requests()
    {
        using var entry = new AppFactory();
        using var provider = new Provider { Respond = _ => throw new HttpRequestException("https://www.googleapis.com/youtube/v3/videos?key=sentinel-secret") };
        var logs = new Logs();
        var clock = new Clock();
        await using var host = Host(entry, provider, clock, options => options.Enabled = true, logs);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Games.Add(new backend.Models.Game
            {
                Id = "youtube-adapter-render", Sport = "mlb", Status = "final", HomeTeam = "Atlanta Braves", AwayTeam = "Los Angeles Dodgers",
                HomeTeamAbbreviation = "ATL", AwayTeamAbbreviation = "LAD", ScheduledStart = clock.Now.UtcDateTime.AddDays(-1), SeasonYear = 2026,
                HomeScore = 4, AwayScore = 3
            });
            await db.SaveChangesAsync();
        }
        var failed = await host.Services.GetRequiredService<YouTubeHighlightsClient>().ValidateVideosAsync([VideoId]);
        Assert.Equal(YouTubeOutcome.TemporaryFailure, failed.Outcome);
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var html = await client.GetStringAsync("/game/youtube-adapter-render");
        Assert.Contains("Atlanta Braves", html);
        Assert.DoesNotContain("sentinel-secret", html);
        Assert.DoesNotContain("sentinel-secret", string.Join('\n', logs.Entries));
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task Temporary_service_unavailability_honors_provider_retry_deadline()
    {
        using var entry = new AppFactory();
        var clock = new Clock();
        using var provider = new Provider { Respond = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(clock.Now.AddHours(2));
            return response;
        } };
        await using var host = Host(entry, provider, clock, options => options.Enabled = true);
        var result = await host.Services.GetRequiredService<YouTubeHighlightsClient>().ValidateVideosAsync([VideoId]);
        Assert.Equal(YouTubeOutcome.TemporaryFailure, result.Outcome);
        Assert.Equal(clock.Now.AddHours(2), result.RetryAtUtc);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task Large_upload_scan_shares_country_evidence_and_stays_within_ten_nonsearch_units()
    {
        using var entry = new AppFactory();
        var page = 0;
        using var provider = new Provider { Respond = request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("channels")) return Json(ChannelBody);
            if (path.EndsWith("i18nRegions")) return Json("{\"items\":[{\"id\":\"US\"},{\"id\":\"CA\"}]}");
            if (path.EndsWith("playlistItems"))
            {
                var items = new System.Text.Json.Nodes.JsonArray();
                foreach (var id in Enumerable.Range(page++ * 50 + 1, 50).Select(i => i.ToString("D11")))
                    items.Add(new System.Text.Json.Nodes.JsonObject { ["contentDetails"] = new System.Text.Json.Nodes.JsonObject { ["videoId"] = id } });
                return Json(new System.Text.Json.Nodes.JsonObject { ["items"] = items, ["nextPageToken"] = "page" + page }.ToJsonString());
            }
            var batch = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query)["id"].ToString().Split(',');
            Assert.InRange(batch.Length, 1, 50);
            var videos = new System.Text.Json.Nodes.JsonArray();
            foreach (var id in batch)
            {
                var video = System.Text.Json.Nodes.JsonNode.Parse(VideoBody)!["items"]![0]!.DeepClone();
                video["id"] = id;
                video["contentDetails"]!["regionRestriction"] = System.Text.Json.Nodes.JsonNode.Parse("{\"blocked\":[\"US\"]}");
                videos.Add(video);
            }
            return Json(new System.Text.Json.Nodes.JsonObject { ["items"] = videos }.ToJsonString());
        } };
        await using var host = Host(entry, provider, new Clock(), options => { options.Enabled = true; options.MaxUploadPages = 4; });
        var result = await host.Services.GetRequiredService<YouTubeHighlightsClient>().DiscoverUploadsAsync();
        Assert.Equal(YouTubeOutcome.Success, result.Outcome);
        Assert.Equal(200, result.Videos.Count);
        Assert.All(result.Videos, video => Assert.Equal(YouTubeAvailability.Playable, video.Availability));
        Assert.Equal("page4", result.NextPageToken);
        Assert.Equal(10, result.UnitsConsumed);
        Assert.Equal(10, provider.Requests.Count);
    }

    [Fact]
    public async Task Blocking_every_ISO_country_excludes_playback_not_merely_every_preferred_content_locale()
    {
        const string countryCodes = "ZW ZM ZA YT YE WS WF VU VN VI VG VE VC VA UZ UY US UM UG UA TZ TW TV TT TR TO TN TM TL TK TJ TH TG TF TD TC SZ SY SX SV ST SS SR SO SN SM SL SK SJ SI SH SG SE SD SC SB SA RW RU RS RO RE QA PY PW PT PS PR PN PM PL PK PH PG PF PE PA OM NZ NU NR NP NO NL NI NG NF NE NC NA MZ MY MX MW MV MU MT MS MR MQ MP MO MN MM ML MK MH MG MF ME MD MC MA LY LV LU LT LS LR LK LI LC LB LA KZ KY KW KR KP KN KM KI KH KG KE JP JO JM JE IT IS IR IQ IO IN IM IL IE ID HU HT HR HN HM HK GY GW GU GT GS GR GQ GP GN GM GL GI GH GG GF GE GD GB GA FR FO FM FK FJ FI ET ES ER EH EG EE EC DZ DO DM DK DJ DE CZ CY CX CW CV CU CR CO CN CM CL CK CI CH CG CF CD CC CA BZ BY BW BV BT BS BR BQ BO BN BM BL BJ BI BH BG BF BE BD BB BA AZ AX AW AU AT AS AR AQ AO AM AL AI AG AF AE AD";
        var body = System.Text.Json.Nodes.JsonNode.Parse(VideoBody)!;
        body["items"]![0]!["contentDetails"]!["regionRestriction"] = new System.Text.Json.Nodes.JsonObject
        {
            ["blocked"] = System.Text.Json.JsonSerializer.SerializeToNode(countryCodes.Split(' '))
        };
        using var entry = new AppFactory();
        using var provider = new Provider { Respond = request => request.RequestUri!.AbsolutePath.Split('/').Last() switch
        {
            "channels" => Json(ChannelBody),
            "i18nRegions" => Json("{\"items\":[{\"id\":\"US\"},{\"id\":\"CA\"},{\"id\":\"GB\"}]}"),
            _ => Json(body.ToJsonString())
        } };
        await using var host = Host(entry, provider, new Clock(), options => options.Enabled = true);
        var result = await host.Services.GetRequiredService<YouTubeHighlightsClient>().ValidateVideosAsync([VideoId]);
        Assert.Equal(YouTubeOutcome.Success, result.Outcome);
        Assert.Equal(YouTubeAvailability.GloballyBlocked, Assert.Single(result.Videos).Availability);
    }

    [Fact]
    public async Task Disabled_discovery_does_not_contact_YouTube()
    {
        using var entry = new AppFactory();
        using var provider = new Provider();
        await using var host = Host(entry, provider, new Clock());
        var result = await host.Services.GetRequiredService<YouTubeHighlightsClient>().DiscoverUploadsAsync();
        Assert.Equal(YouTubeOutcome.Disabled, result.Outcome);
        Assert.Empty(provider.Requests);
    }
}
