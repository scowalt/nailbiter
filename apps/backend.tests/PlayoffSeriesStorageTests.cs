using System.Text.Json.Nodes;
using backend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.tests;

public sealed class PlayoffSeriesStorageTests : IAsyncLifetime
{
    private readonly PlayoffSeriesFixture fixture = new();
    public Task InitializeAsync() => fixture.InitializeAsync();
    public Task DisposeAsync() => fixture.DisposeAsync();

    [Fact]
    public async Task Existing_NHL_abbreviation_normalization_keeps_provider_ID_association_on_reload_and_refresh()
    {
        var competition = fixture.Provider.Board["events"]![0]!["competitions"]![0]!;
        competition["notes"]![0]!["headline"] = "West 1st Round - Game 4";
        competition["series"]!["totalCompetitions"] = 7;
        competition["series"]!["competitors"]![0]!["id"] = "28";
        competition["series"]!["competitors"]![0]!["wins"] = 2;
        competition["series"]!["competitors"]![1]!["id"] = "6";
        competition["competitors"]![0]!["id"] = "28";
        competition["competitors"]![0]!["team"]!["id"] = "28";
        competition["competitors"]![0]!["team"]!["abbreviation"] = "UTA";
        competition["competitors"]![0]!["team"]!["displayName"] = "Utah";
        competition["competitors"]![1]!["id"] = "6";
        competition["competitors"]![1]!["team"]!["id"] = "6";
        competition["competitors"]![1]!["team"]!["abbreviation"] = "EDM";
        competition["competitors"]![1]!["team"]!["displayName"] = "Edmonton";
        await fixture.CollectAsync("nhl");
        using (var page = await fixture.HomeAsync()) Assert.Contains("UTA leads 2–1", page.QuerySelector("a[href='/game/401907973']")!.TextContent);
        using (var scope = fixture.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            var game = await db.Games.SingleAsync();
            game.HomeTeamAbbreviation = "UTAH";
            await db.SaveChangesAsync();
        }
        using (var reloaded = await fixture.HomeAsync()) Assert.Contains("UTAH leads 2–1", reloaded.QuerySelector("a[href='/game/401907973']")!.TextContent);
        fixture.Host.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.CollectAsync("nhl");
        using var refreshed = await fixture.HomeAsync();
        Assert.Contains("UTAH leads 2–1", refreshed.QuerySelector("a[href='/game/401907973']")!.TextContent);
    }

    [Theory]
    [InlineData("nfl")]
    [InlineData("cfb")]
    public async Task Matching_single_game_football_identity_still_cannot_make_a_playoff_series_available(string sport)
    {
        await fixture.CollectAsync();
        using (var scope = fixture.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            var game = await db.Games.SingleAsync();
            var observation = JsonNode.Parse(game.SeriesObservationJson!)!;
            observation["Snapshot"]!["Sport"] = sport;
            game.Sport = sport;
            game.SeriesObservationJson = observation.ToJsonString();
            await db.SaveChangesAsync();
        }
        using var page = await fixture.HomeAsync();
        var card = page.QuerySelector("a[href='/game/401907973']")!;
        Assert.NotNull(card);
        Assert.DoesNotContain("Best of", card.TextContent);
    }

    [Fact]
    public async Task Persisted_source_version_presence_and_identity_are_revalidated_on_new_page_requests()
    {
        var changes = new (string Name, Action<JsonNode> Change)[]
        {
            ("unsupported version", o => o["Version"] = 2),
            ("missing version", o => o.AsObject().Remove("Version")),
            ("unapproved source", o => o["Source"] = "bracket"),
            ("missing source", o => o.AsObject().Remove("Source")),
            ("wrong envelope game", o => o["GameId"] = "other"),
            ("missing observation clock", o => o.AsObject().Remove("ObservedAt")),
            ("default observation clock", o => o["ObservedAt"] = "0001-01-01T00:00:00Z"),
            ("missing snapshot", o => o.AsObject().Remove("Snapshot")),
            ("wrong snapshot game", o => o["Snapshot"]!["GameId"] = "other"),
            ("wrong competition", o => o["Snapshot"]!["CompetitionId"] = "other"),
            ("wrong sport", o => o["Snapshot"]!["Sport"] = "nba"),
            ("single game football", o => o["Snapshot"]!["Sport"] = "nfl"),
            ("CFP", o => o["Snapshot"]!["Sport"] = "cfb"),
            ("wrong year", o => o["Snapshot"]!["SeasonYear"] = 2025),
            ("wrong phase", o => o["Snapshot"]!["SeasonType"] = 2),
            ("wrong provider team", o => o["Snapshot"]!["HomeTeamId"] = "99"),
            ("wrong abbreviation", o => o["Snapshot"]!["Home"] = "SEA"),
            ("duplicate provider team", o => o["Snapshot"]!["HomeTeamId"] = o["Snapshot"]!["AwayTeamId"]!.DeepClone()),
            ("missing wins", o => o["Snapshot"]!.AsObject().Remove("HomeWins")),
            ("negative wins", o => o["Snapshot"]!["HomeWins"] = -1),
            ("missing completion", o => o["Snapshot"]!.AsObject().Remove("SeriesCompleted")),
            ("inconsistent status", o => o["Snapshot"]!["GameStatus"] = "final"),
            ("inconsistent completion", o => o["Snapshot"]!["GameCompleted"] = true),
            ("wrong ordinal", o => o["Snapshot"]!["CurrentGameNumber"] = 2)
        };
        foreach (var (name, change) in changes)
        {
            fixture.Host.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.CollectAsync();
            using (var baseline = await fixture.HomeAsync()) Assert.Contains("Series tied 1–1", baseline.QuerySelector("a[href='/game/401907973']")!.TextContent);
            using (var scope = fixture.Host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
                var game = await db.Games.SingleAsync();
                var observation = JsonNode.Parse(game.SeriesObservationJson!)!;
                change(observation);
                game.SeriesObservationJson = observation.ToJsonString();
                await db.SaveChangesAsync();
            }
            var requests = fixture.Provider.Requests;
            using var page = await fixture.HomeAsync();
            var card = page.QuerySelector("a[href='/game/401907973']")!;
            Assert.NotNull(card);
            Assert.True(!card.TextContent.Contains("Series tied"), name);
            Assert.Contains("Hype unavailable", card.TextContent);
            Assert.Equal(requests, fixture.Provider.Requests);
        }
    }
}
