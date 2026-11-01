using AngleSharp.Html.Parser;
using backend.Models;
using Xunit;
using static backend.tests.ExcitementWiringSupport;

namespace backend.tests;

public sealed class ExcitementWiringTests : IAsyncLifetime
{
    private readonly HostedGameFixture host = new(new DateTimeOffset(2026, 10, 10, 18, 0, 0, TimeSpan.Zero));

    public Task InitializeAsync() => host.InitializeAsync();
    public Task DisposeAsync() => host.DisposeAsync();

    [Theory]
    [InlineData("cfb", "in_progress")]
    [InlineData("nfl", "final")]
    [InlineData("nba", "in_progress")]
    [InlineData("nhl", "in_progress")]
    [InlineData("mlb", "final")]
    public async Task Full_game_so_far_excitement_agrees_on_home_league_and_detail_HTTP(string sport, string status)
    {
        var game = Game("drama", sport);
        game.Status = status;
        var samples = History(host.Clock.GetUtcNow(), game.Id, new double[] { .1, .9, .1, .9, .1, .9 }
            .Concat(Enumerable.Repeat(.9, 110))).Select((sample, index) =>
            {
                sample.Id = 1000 + index;
                if (index < 6) sample.Timestamp = host.Clock.GetUtcNow().UtcDateTime.AddMinutes(-120);
                return sample;
            }).ToList();
        samples.Add(new OddsSnapshot { Id = 2000, GameId = game.Id, IsHistorical = true,
            Timestamp = host.Clock.GetUtcNow().UtcDateTime.AddMinutes(-110),
            HomeWinProbability = .1, AwayWinProbability = .9, Source = "historical" });
        await host.SeedAsync(game, samples.AsEnumerable().Reverse());

        foreach (var path in Paths(sport, game.Id))
        {
            using var page = await new HtmlParser().ParseDocumentAsync(await host.Client.GetStringAsync(path));
            var region = AssertScore(page, path.StartsWith("/game/") ? "detail-excitement" : "excitement-drama", "33", "medium");
            Assert.Contains("Excitement", region.TextContent);
        }
    }

    [Theory]
    [InlineData("cfb")]
    [InlineData("nfl")]
    public async Task HTTP_keeps_unavailable_evidence_distinct_from_observed_zero(string sport)
    {
        await host.SeedAsync(Game("missing", sport), []);
        await host.SeedAsync(Game("single", sport), History(host.Clock.GetUtcNow(), "single", [.5]));
        await host.SeedAsync(Game("flat", sport), History(host.Clock.GetUtcNow(), "flat", [.5, .5]));

        foreach (var id in new[] { "missing", "single", "flat" })
            foreach (var path in Paths(sport, id))
            {
                using var page = await new HtmlParser().ParseDocumentAsync(await host.Client.GetStringAsync(path));
                var regionId = path.StartsWith("/game/") ? "detail-excitement" : "excitement-" + id;
                if (id == "flat")
                {
                    var region = AssertScore(page, regionId, "0", "low");
                    Assert.Contains("Excitement", region.TextContent);
                }
                else
                {
                    var region = page.QuerySelector("#" + regionId);
                    Assert.NotNull(region);
                    Assert.Null(region.QuerySelector("[class*=text-excitement-]"));
                    Assert.Equal(sport == "cfb" ? "Excitement unavailable" : "", region.TextContent.Trim());
                }
            }
    }

    private static string[] Paths(string sport, string id) =>
        ["/", sport is "cfb" or "nfl" ? $"/{sport}?week=5&season=2026&type=2" : $"/{sport}?date=2026-10-10", $"/game/{id}"];

    private Game Game(string id, string sport) => new()
    {
        Id = id, Sport = sport, Status = "in_progress",
        HomeTeam = "Home", AwayTeam = "Away", HomeTeamAbbreviation = "HOM", AwayTeamAbbreviation = "AWY",
        ScheduledStart = host.Clock.GetUtcNow().UtcDateTime.AddHours(-1),
        SeasonYear = 2026, SeasonType = 2, WeekNumber = 5,
        HomeScore = 14, AwayScore = 10, Period = 2, GameClock = "5:00"
    };
}
