using System.Text.Json.Nodes;
using Xunit;

namespace backend.tests;

public sealed class PlayoffSeriesLifecycleTests : IAsyncLifetime
{
    private readonly PlayoffSeriesFixture fixture = new();
    public Task InitializeAsync() => fixture.InitializeAsync();
    public Task DisposeAsync() => fixture.DisposeAsync();

    [Fact]
    public async Task Discovery_far_outside_the_odds_window_and_refresh_without_a_market_survive_new_requests()
    {
        fixture.Provider.Date = "20261007";
        fixture.Provider.Board["events"]![0]!["date"] = "2026-10-07T23:00:00Z";
        await fixture.CollectAsync();
        fixture.Host.Clock.Advance(TimeSpan.FromDays(6));
        var requests = fixture.Provider.Requests;
        using (var page = await fixture.HomeAsync())
        {
            var card = page.QuerySelector("a[href='/game/401907973']")!;
            Assert.Contains("Series tied 1–1", card.TextContent);
            Assert.Contains("Hype unavailable", card.TextContent);
        }
        Assert.Equal(requests, fixture.Provider.Requests);
        var competition = fixture.Provider.Board["events"]![0]!["competitions"]![0]!;
        competition["series"]!["totalCompetitions"] = 5;
        competition["series"]!["competitors"]![0]!["wins"] = 2;
        competition["notes"]![0]!["headline"] = "NLDS - Game 4";
        await fixture.CollectAsync();
        requests = fixture.Provider.Requests;
        using var refreshed = await fixture.HomeAsync();
        Assert.Contains("ATL leads 2–1", refreshed.QuerySelector("a[href='/game/401907973']")!.TextContent);
        Assert.Equal(requests, fixture.Provider.Requests);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task Older_and_equal_overlapping_boards_cannot_replace_the_first_newer_persisted_observation(bool equalClock, bool invalidNewer, bool invalidOlder)
    {
        await fixture.CollectAsync();
        if (invalidOlder) fixture.Provider.Board["events"]![0]!["competitions"]![0]!.AsObject().Remove("series");
        fixture.Host.Clock.Advance(TimeSpan.FromSeconds(1));
        var held = fixture.Provider.HoldNextBoard();
        var older = fixture.CollectAsync();
        try
        {
            await held.WaitUntilRequestedAsync();
            if (!equalClock) fixture.Host.Clock.Advance(TimeSpan.FromSeconds(1));
            fixture.Provider.Board = PlayoffSeriesFixture.SyntheticBoard();
            var competition = fixture.Provider.Board["events"]![0]!["competitions"]![0]!;
            if (invalidNewer) competition.AsObject().Remove("series");
            else
            {
                competition["series"]!["totalCompetitions"] = 5;
                competition["series"]!["competitors"]![0]!["wins"] = 2;
                competition["notes"]![0]!["headline"] = "NLDS - Game 4";
            }
            await fixture.CollectAsync();
        }
        finally { held.Release(); await older; }
        var requests = fixture.Provider.Requests;
        for (var load = 0; load < 2; load++)
        {
            using var page = await fixture.HomeAsync();
            var card = page.QuerySelector("a[href='/game/401907973']")!;
            if (invalidNewer) Assert.DoesNotContain("Best of", card.TextContent);
            else Assert.Contains("ATL leads 2–1", card.TextContent);
            Assert.DoesNotContain("Series tied 1–1", card.TextContent);
        }
        Assert.Equal(requests, fixture.Provider.Requests);
    }

    [Fact]
    public async Task Removed_scheduled_game_cannot_be_resurrected_by_an_older_board()
    {
        await fixture.CollectAsync();
        fixture.Host.Clock.Advance(TimeSpan.FromSeconds(1));
        var held = fixture.Provider.HoldNextBoard();
        var older = fixture.CollectAsync();
        try
        {
            await held.WaitUntilRequestedAsync();
            fixture.Host.Clock.Advance(TimeSpan.FromSeconds(1));
            fixture.Provider.Board["events"] = new JsonArray();
            await fixture.CollectAsync();
            using var canceled = await fixture.HomeAsync();
            Assert.Null(canceled.QuerySelector("a[href='/game/401907973']"));
        }
        finally { held.Release(); await older; }
        using var page = await fixture.HomeAsync();
        Assert.Null(page.QuerySelector("a[href='/game/401907973']"));
    }
}
