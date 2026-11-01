using System.Net;
using System.Text.Json;
using System.Web;
using backend.Data;
using backend.Models;
using backend.Services;
using backend.Services.Highlights;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BrowserFixture;

internal sealed class HighlightsFixture : TimeProvider
{
    public const string Sentinel = "fixture-sentinel-no-live-key-653";
    public static readonly string[] Completed = ["mlb-chart", "mlb-no-chart", "mlb-pending", "mlb-closed", "mlb-suppressed", "mlb-unusable"];
    public static readonly string[] Hidden = ["scheduled", "in_progress", "canceled", "postponed", "suspended", "post-false", "missing-evidence"];
    public DateTimeOffset Now { get; private set; } = new(2026, 10, 7, 3, 0, 0, TimeSpan.Zero);
    public int UpstreamCalls { get; private set; }
    private bool discovered;
    private bool removed;
    public override DateTimeOffset GetUtcNow() => Now;

    public void Configure(IServiceCollection services)
    {
        services.RemoveAll<TimeProvider>();
        services.AddSingleton<TimeProvider>(this);
        services.Configure<YouTubeHighlightsOptions>(o => { o.Enabled = true; o.ApiKey = Sentinel; o.MaxUploadPages = 1; });
        services.Configure<HighlightsWorkerOptions>(o => o.Enabled = true);
        services.AddHttpClient(YouTubeHighlightsClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new Handler(this));
        services.AddHttpClient(MlbHighlightsIdentityClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new Handler(this));
    }

    public void Seed(ExcitementDbContext db)
    {
        for (var i = 0; i < Completed.Length; i++)
        {
            var game = Game(Completed[i], i + 1);
            db.Games.Add(game);
        }
        foreach (var status in Hidden)
        {
            var game = Game($"mlb-{status}", 6);
            if (status is not "post-false" and not "missing-evidence") game.Status = status;
            db.Games.Add(game);
        }
        db.SaveChanges();
        foreach (var game in db.Games.Where(g => g.Sport == "mlb").ToArray())
        {
            if (game.Id == "mlb-missing-evidence") continue;
            var status = new EspnCompletionStatus("STATUS_FINAL", "post", game.Id != "mlb-post-false", null, null, null);
            var observed = game.Id == "mlb-closed" ? Now.AddHours(-73).UtcDateTime : Now.UtcDateTime;
            new MlbCompletionEvidence(game.Id, game.Id, status, status, observed).RecordAsync(db, true).GetAwaiter().GetResult();
        }
        db.OddsSnapshots.AddRange(new[] { 0.4, 0.6, 1.0 }.Select((probability, index) => new OddsSnapshot
        {
            GameId = "mlb-chart", Timestamp = Now.AddHours(-3).AddMinutes(index * 30).UtcDateTime,
            HomeWinProbability = probability, AwayWinProbability = 1 - probability,
            HomeMoneyline = -150, AwayMoneyline = 130, Period = index + 7, HomeScore = index + 3, AwayScore = 2,
            Source = "Fixture"
        }));
        db.SaveChanges();
    }

    private static DateTime Start(int day) => new(2026, 10, day, day == 4 ? 1 : 22, 0, 0, DateTimeKind.Utc);

    private static Game Game(string id, int day) => new()
    {
        Id = id, Sport = "mlb", Status = "final", HomeTeam = "Atlanta Braves", AwayTeam = "Los Angeles Dodgers",
        HomeTeamAbbreviation = "ATL", AwayTeamAbbreviation = "LAD", HomeTeamId = "15", AwayTeamId = "19",
        SeasonYear = 2026, SeasonType = 3, ScheduledStart = Start(day),
        HomeScore = 5, AwayScore = 2, Period = 9, GameClock = "0:00"
    };

    public async Task InitializeAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var app = scope.ServiceProvider.GetRequiredService<HighlightsApplicationService>();
        await app.ProcessCandidatesAsync(Completed, []);
        await app.OperateAsync(new(HighlightsOperatorAction.Remove, "mlb-suppressed", Apply: true));
        await scope.ServiceProvider.GetRequiredService<HighlightsLifecycleService>().RunAsync();
        removed = true;
        var validation = await services.GetRequiredService<YouTubeHighlightsClient>().ValidateVideosAsync(["fixture0006"]);
        await app.RevalidateSelectionsAsync(validation.Videos);
    }

    public async Task<object> ControlAsync(IServiceProvider services, string command)
    {
        using var scope = services.CreateScope();
        var app = scope.ServiceProvider.GetRequiredService<HighlightsApplicationService>();
        switch (command)
        {
            case "discover":
                discovered = true;
                Now = Now.AddMinutes(15);
                await scope.ServiceProvider.GetRequiredService<HighlightsLifecycleService>().RunAsync();
                break;
            case "worker-off": services.GetRequiredService<IOptions<HighlightsWorkerOptions>>().Value.Enabled = false; break;
            case "adapter-off": services.GetRequiredService<IOptions<YouTubeHighlightsOptions>>().Value.Enabled = false; break;
            case "missing-key": services.GetRequiredService<IOptions<YouTubeHighlightsOptions>>().Value.ApiKey = null; break;
            case "enabled":
                services.GetRequiredService<IOptions<HighlightsWorkerOptions>>().Value.Enabled = true;
                services.GetRequiredService<IOptions<YouTubeHighlightsOptions>>().Value.Enabled = true;
                services.GetRequiredService<IOptions<YouTubeHighlightsOptions>>().Value.ApiKey = Sentinel;
                break;
            case "inspect": break;
            default: throw new ArgumentException("Unknown local fixture operation.");
        }
        var states = new List<object>();
        foreach (var id in Completed)
        {
            var detail = await app.GetDetailAsync(id);
            var inspection = await app.InspectAsync(id);
            states.Add(new { id, status = detail.Status.ToString(), videoId = detail.VideoId,
                ownership = inspection?.Highlights?.Ownership?.ToString(), suppressed = inspection?.Highlights?.Suppressed });
        }
        return new { upstreamCalls = UpstreamCalls, states };
    }

    private string Respond(HttpRequestMessage request)
    {
        UpstreamCalls++;
        var uri = request.RequestUri!;
        if (uri.Host == "statsapi.mlb.com")
            return JsonSerializer.Serialize(new { dates = Enumerable.Range(1, 6).Select(day => new
            {
                date = $"2026-10-{day:D2}", games = new[] { new
                {
                    gamePk = 910000 + day, gameDate = Start(day).ToString("O"), officialDate = $"2026-10-{day:D2}",
                    season = "2026", doubleHeader = "N", gameNumber = 1,
                    status = new { abstractGameState = "Final", detailedState = "Final", codedGameState = "F" },
                    teams = new { home = new { team = new { id = 144 } }, away = new { team = new { id = 119 } } }
                } }
            }) });
        if (uri.Host == "site.web.api.espn.com")
        {
            var id = HttpUtility.ParseQueryString(uri.Query)["event"]!;
            var day = Array.IndexOf(Completed, id) + 1;
            if (day <= 0) throw new InvalidOperationException("Unexpected identity request.");
            return JsonSerializer.Serialize(new { header = new
            {
                id, season = new { year = 2026 }, competitions = new[] { new
                {
                    id, date = Start(day).ToString("O"), status = new { type = new { name = "STATUS_FINAL", state = "post", completed = true } },
                    competitors = new[] { new { homeAway = "home", team = new { id = "15", abbreviation = "ATL" } }, new { homeAway = "away", team = new { id = "19", abbreviation = "LAD" } } }
                } }
            } });
        }
        if (uri.Host != "www.googleapis.com") throw new InvalidOperationException("Unexpected outbound fixture HTTP.");
        if (!request.Headers.TryGetValues("X-Goog-Api-Key", out var keys) || keys.Single() != Sentinel)
            throw new InvalidOperationException("Expected only the fake server-side credential.");
        if (uri.AbsolutePath.EndsWith("channels"))
            return """{"items":[{"id":"UCoLrcjPV5PbUrUyXq5mjc_A","contentDetails":{"relatedPlaylists":{"uploads":"fixture-uploads"}}}]}""";
        if (uri.AbsolutePath.EndsWith("playlistItems"))
            return JsonSerializer.Serialize(new { items = (discovered ? new[] { 1, 2, 3, 6 } : new[] { 1, 2, 6 })
                .Select(day => new { contentDetails = new { videoId = $"fixture{day:D4}" } }) });
        if (uri.AbsolutePath.EndsWith("i18nRegions")) return """{"items":[{"id":"US"},{"id":"CA"}]}""";
        if (!uri.AbsolutePath.EndsWith("videos")) throw new InvalidOperationException("Unexpected provider operation.");
        var ids = HttpUtility.ParseQueryString(uri.Query)["id"]!.Split(',');
        return JsonSerializer.Serialize(new { items = ids.Where(id => !removed || id != "fixture0006").Select(id => new
        {
            id, snippet = new { channelId = YouTubeHighlightsClient.ApprovedChannelId,
                title = $"Dodgers vs. Braves Highlights (10/{int.Parse(id[^4..])}/26) | MLB Highlights",
                description = "Single game recap", publishedAt = "2026-10-07T02:00:00Z", liveBroadcastContent = "none" },
            contentDetails = new { duration = "PT8M", regionRestriction = new { blocked = new[] { "CA" } } },
            status = new { privacyStatus = "public", uploadStatus = "processed", embeddable = true, madeForKids = false }
        }) });
    }

    private sealed class Handler(HighlightsFixture fixture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(fixture.Respond(request)) });
    }
}
