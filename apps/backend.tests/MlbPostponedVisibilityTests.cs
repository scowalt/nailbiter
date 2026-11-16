using System.Net;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using backend.Models;
using backend.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace backend.tests;

public sealed class MlbPostponedVisibilityTests : IClassFixture<SharedPostgreSql>, IAsyncLifetime
{
    private const string Id = "991300105";
    private readonly HighlightsFaultReportingTests.Logs logs = new();
    private readonly HostedGameFixture host;

    public MlbPostponedVisibilityTests(SharedPostgreSql server) =>
        host = new(new DateTimeOffset(2027, 7, 10, 23, 30, 0, TimeSpan.Zero), services => services.AddSingleton<ILoggerProvider>(logs), server);

    public Task InitializeAsync() => host.InitializeAsync();
    public Task DisposeAsync() => host.DisposeAsync();

    [Fact]
    public async Task A_postponed_game_serves_every_route_without_a_score_a_provider_request_or_a_pregame_capture()
    {
        await host.SeedAsync(new Game
        {
            Id = Id, Sport = "mlb", Status = "postponed", StatusDetail = "Postponed", HomeScore = 0, AwayScore = 0,
            HomeTeam = "Synthetic SEA", AwayTeam = "Synthetic TEX", HomeTeamAbbreviation = "SEA", AwayTeamAbbreviation = "TEX",
            HomeTeamId = "9125", AwayTeamId = "9128", ScheduledStart = new DateTime(2027, 7, 11, 2, 10, 0, DateTimeKind.Utc),
            SeasonYear = 2027, SeasonType = 2
        }, []);

        foreach (var at in new[]
        {
            new DateTimeOffset(2027, 7, 10, 23, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 7, 11, 3, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 7, 11, 5, 30, 0, TimeSpan.Zero),
        })
        {
            host.Clock.SetUtcNow(at);
            var requests = host.RejectedOutboundRequests;

            foreach (var path in new[] { "/", "/mlb", "/mlb?date=2027-07-10" })
            {
                using var page = await Page(path);
                AssertNoScore(page, path, $"away-score-{Id}", $"home-score-{Id}");
                Assert.NotEqual("Final", page.GetElementById($"status-{Id}")?.TextContent.Trim());
            }
            using (var dated = await Page("/mlb?date=2027-07-10")) Assert.NotNull(dated.QuerySelector($"a[href='/game/{Id}']"));
            using (var detail = await Page($"/game/{Id}"))
            {
                AssertNoScore(detail, $"/game/{Id}", "detail-away-score", "detail-home-score");
                Assert.NotEqual("Final", detail.GetElementById("detail-status")!.TextContent.Trim());
            }
            using (var stream = await host.Client.GetAsync($"/sse/game/{Id}", HttpCompletionOption.ResponseHeadersRead))
                Assert.Equal(HttpStatusCode.OK, stream.StatusCode);

            using (var scope = host.Services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<ProCompetitionEvidenceService>().RefreshAsync(CancellationToken.None);
                var lifecycle = scope.ServiceProvider.GetRequiredService<IProHypeLifecycle>();
                await lifecycle.CaptureAsync();
                Assert.Null((await lifecycle.ResolveAsync(Id)).Total);
            }
            Assert.Equal(requests, host.RejectedOutboundRequests);
        }

        Assert.Empty(logs.Entries.Where(e => e.Level >= LogLevel.Error));
    }

    private async Task<IDocument> Page(string path)
    {
        using var response = await host.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync());
    }

    private static void AssertNoScore(IDocument page, string path, params string[] ids)
    {
        foreach (var id in ids)
            Assert.True(page.GetElementById(id) is not { } score || score.TextContent.Trim() == "", $"{path} renders {id} as {page.GetElementById(id)?.TextContent}");
    }
}
