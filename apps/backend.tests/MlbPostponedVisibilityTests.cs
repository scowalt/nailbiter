using System.Net;
using System.Text.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using backend.Data;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace backend.tests;

public sealed class MlbPostponedVisibilityTests : IClassFixture<SharedPostgreSql>, IAsyncLifetime
{
    private const string Id = "991300105";
    private const string TodayId = "991300106";
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private readonly HighlightsFaultReportingTests.Logs logs = new();
    private readonly HostedGameFixture host;

    public MlbPostponedVisibilityTests(SharedPostgreSql server) =>
        host = new(new DateTimeOffset(ProHypeTests.Now), services => services.AddSingleton<ILoggerProvider>(logs), server);

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
        var wallToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Eastern));
        await host.SeedAsync(new Game
        {
            Id = TodayId, Sport = "mlb", Status = "postponed", StatusDetail = "Postponed", HomeScore = 0, AwayScore = 0,
            HomeTeam = "Synthetic SEA", AwayTeam = "Synthetic TEX", HomeTeamAbbreviation = "SEA", AwayTeamAbbreviation = "TEX",
            HomeTeamId = "9125", AwayTeamId = "9128", SeasonYear = wallToday.Year, SeasonType = 2,
            ScheduledStart = TimeZoneInfo.ConvertTimeToUtc(wallToday.ToDateTime(new TimeOnly(12, 0)), Eastern)
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

            var easternDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(at.UtcDateTime, Eastern));
            foreach (var (path, game, listed) in new[]
            {
                ("/", Id, easternDay == new DateOnly(2027, 7, 10)),
                ("/mlb", TodayId, true),
                ("/mlb?date=2027-07-10", Id, true),
            })
            {
                using var page = await Page(path);
                AssertNoScore(page, path, $"away-score-{game}", $"home-score-{game}");
                var badge = page.GetElementById($"status-{game}")?.TextContent.Trim();
                Assert.True(badge == (listed ? "Postponed" : null), $"{path} at {at:O} shows {game} with the status '{badge}'");
            }
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
                var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
                Assert.False(await db.ProHypeSnapshots.AnyAsync(s => s.GameId == Id && s.SnapshotJson != null));
                Assert.False(await db.StartHypes.AnyAsync(h => h.GameId == Id));
            }
            Assert.Equal(requests, host.RejectedOutboundRequests);
        }

        Assert.Empty(logs.Entries.Where(e => e.Level >= LogLevel.Error));
    }

    [Theory]
    [InlineData("scheduled", true)]
    [InlineData("postponed", false)]
    public async Task Only_a_scheduled_game_is_captured_for_pregame_hype(string status, bool captured)
    {
        var game = ProHypeTests.Game("mlb");
        game.ScheduledStart = ProHypeTests.Now.AddDays(6);
        game.Status = status;
        game.ProEvidenceJson = JsonSerializer.Serialize(ProHypeContinuityInputs.Evidence(game));
        await host.SeedAsync(game, []);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            db.PregameOdds.Add(ProHypeTests.Odds(game));
            db.TeamRatings.AddRange(ProHypeTests.Ranks(game));
            foreach (var team in new[] { game.HomeTeamAbbreviation, game.AwayTeamAbbreviation })
                db.TeamRatings.Add(new TeamRating { Sport = "mlb", TeamAbbreviation = team, Season = game.SeasonYear!.Value,
                    SourceSeason = game.SeasonYear, Source = "kalshi", PlayoffProbability = .5, FetchedAt = ProHypeTests.Now });
            await db.SaveChangesAsync();
        }

        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IProHypeLifecycle>().CaptureAsync();

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            Assert.Equal(captured, await db.ProHypeSnapshots.AnyAsync(s => s.GameId == game.Id && s.SnapshotJson != null));
            Assert.Equal(captured, await db.StartHypes.AnyAsync(h => h.GameId == game.Id));
        }
        Assert.Equal(0, host.RejectedOutboundRequests);
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
