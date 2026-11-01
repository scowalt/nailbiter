using System.Text.Json.Nodes;
using Xunit;

namespace backend.tests;

public sealed class PlayoffSeriesReviewFixTests : IAsyncLifetime
{
    private readonly PlayoffSeriesFixture fixture = new();
    public Task InitializeAsync() => fixture.InitializeAsync();
    public Task DisposeAsync() => fixture.DisposeAsync();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_final_standing_and_its_result_remain_visible_after_an_older_loaded_game_resumes_market_HTTP(bool equalClock)
    {
        var item = fixture.Provider.Board["events"]![0]!;
        var competition = item["competitions"]![0]!;
        competition["series"]!["totalCompetitions"] = 7;
        competition["notes"]![0]!["headline"] = "East 1st Round - Game 3";
        await fixture.CollectAsync("nba");
        using (var scheduled = await fixture.HomeAsync())
            Assert.Contains("Series tied 1–1", scheduled.QuerySelector("a[href='/game/401907973']")!.TextContent);
        foreach (var status in new[] { item["status"]!, competition["status"]! })
        {
            status["type"]!["state"] = "in";
            status["type"]!["name"] = "STATUS_IN_PROGRESS";
        }
        competition["competitors"]![0]!["score"] = "42";
        competition["competitors"]![1]!["score"] = "37";
        fixture.Host.Clock.Advance(TimeSpan.FromSeconds(1));
        var held = fixture.Provider.HoldNextMarket();
        var older = fixture.CollectAsync("nba");
        try
        {
            await held.WaitUntilRequestedAsync();
            if (!equalClock) fixture.Host.Clock.Advance(TimeSpan.FromSeconds(1));
            foreach (var status in new[] { item["status"]!, competition["status"]! })
            {
                status["type"]!["state"] = "post";
                status["type"]!["name"] = "STATUS_FINAL";
                status["type"]!["completed"] = true;
            }
            competition["competitors"]![0]!["score"] = "114";
            competition["competitors"]![1]!["score"] = "105";
            competition["competitors"]![0]!["winner"] = true;
            competition["competitors"]![1]!["winner"] = false;
            competition["series"]!["competitors"]![0]!["wins"] = 2;
            await fixture.CollectAsync("nba");
            using var final = await fixture.HomeAsync();
            Assert.Contains("ATL leads 2–1", final.QuerySelector("a[href='/game/401907973']")!.TextContent);
        }
        finally { held.Release(); await older; }
        var requests = fixture.Provider.Requests;
        for (var load = 0; load < 2; load++)
        {
            using var page = await fixture.HomeAsync();
            var card = page.QuerySelector("a[href='/game/401907973']")!;
            Assert.Contains("ATL leads 2–1", card.TextContent);
            Assert.Contains("Final", card.TextContent);
            Assert.Contains("East 1st Round · Game 3 · Best of 7", card.TextContent);
            Assert.Equal("114", card.QuerySelector("#home-score-401907973")!.TextContent);
            Assert.Equal("105", card.QuerySelector("#away-score-401907973")!.TextContent);
        }
        Assert.Equal(requests, fixture.Provider.Requests);
    }

    [Fact]
    public async Task The_latest_loaded_observation_persists_unchanged_identity_and_scores_with_its_standing()
    {
        var item = fixture.Provider.Board["events"]![0]!;
        var competition = item["competitions"]![0]!;
        competition["series"]!["totalCompetitions"] = 7;
        competition["notes"]![0]!["headline"] = "East 1st Round - Game 3";
        await fixture.CollectAsync("nba");
        var live = fixture.Provider.Board.DeepClone();
        var final = fixture.Provider.Board.DeepClone();
        var finalCompetition = final["events"]![0]!["competitions"]![0]!;
        foreach (var status in new[] { final["events"]![0]!["status"]!, finalCompetition["status"]! })
        {
            status["type"]!["state"] = "post";
            status["type"]!["name"] = "STATUS_FINAL";
            status["type"]!["completed"] = true;
        }
        finalCompetition["competitors"]![0]!["homeAway"] = "away";
        finalCompetition["competitors"]![1]!["homeAway"] = "home";
        finalCompetition["competitors"]![0]!["score"] = "114";
        finalCompetition["competitors"]![1]!["score"] = "105";
        finalCompetition["competitors"]![0]!["winner"] = true;
        finalCompetition["competitors"]![1]!["winner"] = false;
        finalCompetition["series"]!["competitors"]![0]!["wins"] = 2;
        fixture.Provider.Board = final;
        fixture.Host.Clock.Advance(TimeSpan.FromSeconds(1));
        var boardHeld = fixture.Provider.HoldNextBoard();
        var earlier = fixture.CollectAsync("nba");
        var marketHeld = fixture.Provider.HoldNextMarket();
        Task? latest = null;
        try
        {
            await boardHeld.WaitUntilRequestedAsync();
            foreach (var status in new[] { live["events"]![0]!["status"]!, live["events"]![0]!["competitions"]![0]!["status"]! })
            {
                status["type"]!["state"] = "in";
                status["type"]!["name"] = "STATUS_IN_PROGRESS";
            }
            fixture.Provider.Board = live;
            fixture.Host.Clock.Advance(TimeSpan.FromSeconds(1));
            latest = fixture.CollectAsync("nba");
            await marketHeld.WaitUntilRequestedAsync();
            boardHeld.Release();
            await earlier;
            using var first = await fixture.HomeAsync();
            Assert.Contains("ATL leads 2–1", first.QuerySelector("a[href='/game/401907973']")!.TextContent);
        }
        finally
        {
            boardHeld.Release();
            marketHeld.Release();
            await earlier;
            if (latest is not null) await latest;
        }
        using var page = await fixture.HomeAsync();
        var card = page.QuerySelector("a[href='/game/401907973']")!;
        Assert.Contains("Series tied 1–1", card.TextContent);
        Assert.Contains("Live", card.TextContent);
        Assert.Equal("0", card.QuerySelector("#home-score-401907973")!.TextContent);
        Assert.Equal("0", card.QuerySelector("#away-score-401907973")!.TextContent);
        Assert.Contains("ATL", card.QuerySelector("#home-score-401907973")!.ParentElement!.TextContent);
    }

    [Theory]
    [InlineData("competition")]
    [InlineData("participant")]
    [InlineData("team")]
    [InlineData("event")]
    public async Task Null_successful_collection_elements_revoke_identifiable_standings_and_keep_valid_neighbors(string element)
    {
        await fixture.CollectAsync();
        using (var baseline = await fixture.HomeAsync())
            Assert.Contains("Series tied 1–1", baseline.QuerySelector("a[href='/game/401907973']")!.TextContent);
        var events = fixture.Provider.Board["events"]!.AsArray();
        var item = events[0]!;
        var neighbor = item.DeepClone();
        neighbor["id"] = "null-neighbor";
        neighbor["competitions"]![0]!["id"] = "null-neighbor";
        switch (element)
        {
            case "competition": item["competitions"]![0] = null; break;
            case "participant": item["competitions"]![0]!["competitors"]![0] = null; break;
            case "team": item["competitions"]![0]!["competitors"]![0]!["team"] = null; break;
            case "event":
                item["competitions"] = new JsonArray();
                events.Insert(0, null);
                break;
        }
        events.Add(neighbor);
        fixture.Host.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.CollectAsync();
        var requests = fixture.Provider.Requests;
        using var page = await fixture.HomeAsync();
        var invalid = page.QuerySelector("a[href='/game/401907973']")!;
        Assert.DoesNotContain("Series tied", invalid.TextContent);
        Assert.Equal("0", invalid.QuerySelector("#home-score-401907973")!.TextContent);
        Assert.Contains("Series tied 1–1", page.QuerySelector("a[href='/game/null-neighbor']")!.TextContent);
        Assert.Equal(requests, fixture.Provider.Requests);
    }
}
