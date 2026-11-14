using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AngleSharp.Html.Parser;
using backend.Data;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace backend.tests;

public sealed class NbaScheduleProgressPageTests
{
    private sealed class ScheduleWire : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Requests++;
            var url = request.RequestUri!;
            string body;
            if (url.AbsolutePath.EndsWith("/teams"))
                body = JsonSerializer.Serialize(new { sports = new[] { new { leagues = new[] { new
                {
                    season = new { year = 2027 },
                    teams = new[] { new { team = new { id = "2", abbreviation = "BOS" } }, new { team = new { id = "14", abbreviation = "MIA" } } }
                } } } } });
            else if (url.AbsolutePath.EndsWith("/schedule"))
                body = NbaScheduleProgressTests.ScheduleBody(url.Segments[^2].TrimEnd('/') == "2" ? "BOS" : "MIA").ToJsonString();
            else
            {
                Assert.EndsWith("/scoreboard", url.AbsolutePath);
                body = JsonSerializer.Serialize(new { events = new[] { NbaScheduleProgressTests.Upcoming(NbaScheduleProgressTests.ScheduleBody("BOS")) } });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task Registered_refresh_builds_NBA_progress_and_capture_without_fabricated_future_games()
    {
        var wire = new ScheduleWire();
        var host = new HostedGameFixture(new DateTimeOffset(NbaScheduleProgressTests.Now), services =>
        {
            services.RemoveAll<ProCompetitionEvidenceService>();
            services.AddScoped(sp => new ProCompetitionEvidenceService(new HttpClient(wire, disposeHandler: false),
                sp.GetRequiredService<ExcitementDbContext>(), sp.GetRequiredService<TimeProvider>(), NullLogger<ProCompetitionEvidenceService>.Instance));
        });
        await host.InitializeAsync();
        try
        {
            var game = NbaScheduleProgressTests.Game();
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
                await db.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "035_ProHypeLifecycle.sql")));
                db.Games.Add(game);
                var ranks = ProHypeTests.Ranks(game); foreach (var rank in ranks) rank.FetchedAt = NbaScheduleProgressTests.Now;
                db.TeamRatings.AddRange(ranks);
                var odds = ProHypeTests.Odds(game); odds.FetchedAt = NbaScheduleProgressTests.Now; db.PregameOdds.Add(odds);
                foreach (var team in new[] { "BOS", "MIA" }) db.TeamRatings.Add(new TeamRating
                {
                    Sport = "nba", TeamAbbreviation = team, Source = "kalshi", Season = 2027, SourceSeason = 2027,
                    PlayoffProbability = .5, FetchedAt = NbaScheduleProgressTests.Now
                });
                await db.SaveChangesAsync();
                await scope.ServiceProvider.GetRequiredService<ProCompetitionEvidenceService>().RefreshAsync(default);
                var raw = await scope.ServiceProvider.GetRequiredService<HypeScoreService>().GetHypeComponentsAsync(game, null);
                Assert.True(raw.IsComplete, raw.StakesExplanation);
                Assert.Equal(6.629286034000698, raw.StakesScore, 10);
                Assert.Equal(96.63793103448276, raw.QualityScore, 10);
                var lifecycle = scope.ServiceProvider.GetRequiredService<IProHypeLifecycle>();
                await lifecycle.CaptureAsync();
                Assert.Equal(56.134040784265835, (await lifecycle.ResolveAsync(game.Id)).Total!.Value, 10);
            }
            Assert.Equal(4, wire.Requests);
            foreach (var path in new[] { "/", "/nba?date=2026-11-16", "/game/" + game.Id })
            {
                using var document = await new HtmlParser().ParseDocumentAsync(await host.Client.GetStringAsync(path));
                var score = document.QuerySelector(path.StartsWith("/game/") ? "#detail-hype" : "#excitement-" + game.Id)!;
                Assert.NotNull(score); Assert.DoesNotContain("unavailable", score.TextContent, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("56", score.TextContent);
                ProHypeOutputAssertions.NoDiagnostics(document.DocumentElement.OuterHtml);
            }
            host.Clock.Advance(TimeSpan.FromHours(2));
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
                var stored = await db.Games.SingleAsync(g => g.Id == game.Id);
                var raw = await scope.ServiceProvider.GetRequiredService<HypeScoreService>().GetHypeComponentsAsync(stored, null);
                Assert.False(raw.StakesAvailable);
                var retained = await scope.ServiceProvider.GetRequiredService<IProHypeLifecycle>().ResolveAsync(game.Id);
                Assert.Equal(56.134040784265835, retained.Total!.Value, 10);
                Assert.Equal(NbaScheduleProgressTests.Now, retained.EvaluatedAt);
            }
            Assert.Equal(4, wire.Requests);
        }
        finally { await host.DisposeAsync(); }
    }
}
