using System.Text.Json.Nodes;
using Xunit;

namespace backend.tests;

public sealed class PlayoffSeriesResultTests : IAsyncLifetime
{
    private readonly PlayoffSeriesFixture fixture = new();
    public Task InitializeAsync() => fixture.InitializeAsync();
    public Task DisposeAsync() => fixture.DisposeAsync();

    private void UseSyntheticResult(string id, bool advance = true)
    {
        var cases = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ProHype", "postseason-rounds.json")))!.AsArray();
        var source = cases.SingleOrDefault(c => c!["event"]!["id"]!.GetValue<string>() == id);
        var item = source is not null ? source["event"]!.DeepClone()
            : JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ProHype", "nhl-board.json")))!["events"]![0]!.DeepClone();
        var competition = item["competitions"]![0]!;
        item["date"] = "2026-10-01T19:00:00Z";
        item["status"] = competition["status"]!.DeepClone();
        foreach (var participant in competition["competitors"]!.AsArray())
            participant!["team"]!["displayName"] = participant["team"]!["abbreviation"]!.DeepClone();
        fixture.Provider.Board = new JsonObject { ["events"] = new JsonArray(item) };
        if (advance) fixture.Host.Clock.Advance(TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData(3, 2, "NLWC - Game 3", "ATL wins series 2–1")]
    [InlineData(5, 3, "NLDS - Game 4", "ATL wins series 3–1")]
    [InlineData(7, 4, "NLCS - Game 5", "ATL wins series 4–1")]
    public async Task Confirmed_MLB_series_completion_uses_the_verified_formats_winning_threshold(int bestOf, int wins, string headline, string summary)
    {
        var item = fixture.Provider.Board["events"]![0]!;
        var competition = item["competitions"]![0]!;
        foreach (var status in new[] { item["status"]!, competition["status"]! })
        {
            status["type"]!["state"] = "post";
            status["type"]!["name"] = "STATUS_FINAL";
            status["type"]!["completed"] = true;
        }
        competition["competitors"]![0]!["winner"] = true;
        competition["competitors"]![1]!["winner"] = false;
        competition["competitors"]![0]!["score"] = "6";
        competition["competitors"]![1]!["score"] = "1";
        competition["series"]!["totalCompetitions"] = bestOf;
        competition["series"]!["competitors"]![0]!["wins"] = wins;
        competition["series"]!["completed"] = true;
        competition["notes"]![0]!["headline"] = headline;
        await fixture.CollectAsync();
        using (var page = await fixture.HomeAsync()) Assert.Contains(summary, page.QuerySelector("a[href='/game/401907973']")!.TextContent);
        fixture.Host.Clock.Advance(TimeSpan.FromSeconds(1));
        competition["series"]!["completed"] = false;
        await fixture.CollectAsync();
        using var contradicted = await fixture.HomeAsync();
        Assert.DoesNotContain("Best of", contradicted.QuerySelector("a[href='/game/401907973']")!.TextContent);
    }

    [Theory]
    [InlineData("nba", "401869369", "STATUS_FINAL_OT", "BOS leads 2–0")]
    [InlineData("nhl", "401777460", "STATUS_FINAL_SO", "FLA wins series 4–2")]
    public async Task Confirmed_extra_period_finals_are_supported_without_a_universal_four_win_assumption(string sport, string id, string statusName, string summary)
    {
        UseSyntheticResult(id);
        var item = fixture.Provider.Board["events"]![0]!;
        item["status"]!["type"]!["name"] = statusName;
        item["competitions"]![0]!["status"]!["type"]!["name"] = statusName;
        await fixture.CollectAsync(sport);
        using var page = await fixture.HomeAsync();
        Assert.Contains(summary, page.QuerySelector($"a[href='/game/{id}']")!.TextContent);
    }

    [Fact]
    public async Task Absent_optional_result_flags_do_not_replace_ordinal_inclusivity_with_a_guessed_win()
    {
        UseSyntheticResult("401809270");
        var competition = fixture.Provider.Board["events"]![0]!["competitions"]![0]!;
        foreach (var participant in competition["competitors"]!.AsArray()) participant!.AsObject().Remove("winner");
        await fixture.CollectAsync();
        using (var page = await fixture.HomeAsync()) Assert.Contains("PHI leads 2–1", page.QuerySelector("a[href='/game/401809270']")!.TextContent);
        competition["series"]!["competitors"]![1]!["wins"] = 1;
        fixture.Host.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.CollectAsync();
        using var contradicted = await fixture.HomeAsync();
        Assert.DoesNotContain("Best of", contradicted.QuerySelector("a[href='/game/401809270']")!.TextContent);
    }

    [Fact]
    public async Task Ordinary_discovery_live_refresh_and_confirmed_final_refresh_do_not_depend_on_odds_or_guess_wins()
    {
        await fixture.CollectAsync();
        using (var scheduled = await fixture.HomeAsync()) Assert.Contains("Series tied 1–1", scheduled.QuerySelector("a[href='/game/401907973']")!.TextContent);
        var item = fixture.Provider.Board["events"]![0]!;
        var competition = item["competitions"]![0]!;
        foreach (var status in new[] { item["status"]!, competition["status"]! })
        {
            status["type"]!["state"] = "in";
            status["type"]!["name"] = "STATUS_IN_PROGRESS";
        }
        competition["competitors"]![0]!["score"] = "0";
        competition["competitors"]![1]!["score"] = "99";
        fixture.Host.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.CollectAsync();
        using (var live = await fixture.HomeAsync())
        {
            var card = live.QuerySelector("a[href='/game/401907973']")!;
            Assert.Contains("Series tied 1–1", card.TextContent);
            Assert.Equal("99", card.QuerySelector("#away-score-401907973")!.TextContent);
        }
        foreach (var status in new[] { item["status"]!, competition["status"]! })
        {
            status["type"]!["state"] = "post";
            status["type"]!["name"] = "STATUS_FINAL";
            status["type"]!["completed"] = true;
        }
        competition["competitors"]![0]!["score"] = "6";
        competition["competitors"]![1]!["score"] = "1";
        competition["competitors"]![0]!["winner"] = true;
        competition["competitors"]![1]!["winner"] = false;
        competition["series"]!["competitors"]![0]!["wins"] = 2;
        competition["series"]!["completed"] = true;
        fixture.Host.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.CollectAsync();
        var requests = fixture.Provider.Requests;
        using var final = await fixture.HomeAsync();
        var finalCard = final.QuerySelector("a[href='/game/401907973']")!;
        Assert.Contains("ATL wins series 2–1", finalCard.TextContent);
        Assert.Contains("NLWC · Game 3 · Best of 3", finalCard.TextContent);
        Assert.Equal("6", finalCard.QuerySelector("#home-score-401907973")!.TextContent);
        Assert.Equal("1", finalCard.QuerySelector("#away-score-401907973")!.TextContent);
        Assert.Equal(requests, fixture.Provider.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Older_or_equal_live_observation_cannot_erase_a_newer_persisted_final_standing(bool equalClock)
    {
        UseSyntheticResult("401809270");
        var item = fixture.Provider.Board["events"]![0]!;
        var competition = item["competitions"]![0]!;
        foreach (var status in new[] { item["status"]!, competition["status"]! })
        {
            status["type"]!["state"] = "in";
            status["type"]!["name"] = "STATUS_IN_PROGRESS";
            status["type"]!["completed"] = false;
        }
        foreach (var participant in competition["competitors"]!.AsArray()) participant!.AsObject().Remove("winner");
        competition["series"]!["competitors"]![1]!["wins"] = 1;
        await fixture.CollectAsync();
        using (var live = await fixture.HomeAsync()) Assert.Contains("Series tied 1–1", live.QuerySelector("a[href='/game/401809270']")!.TextContent);
        fixture.Host.Clock.Advance(TimeSpan.FromSeconds(1));
        var held = fixture.Provider.HoldNextBoard();
        var older = fixture.CollectAsync();
        try
        {
            await held.WaitUntilRequestedAsync();
            UseSyntheticResult("401809270", advance: !equalClock);
            await fixture.CollectAsync();
        }
        finally { held.Release(); await older; }
        using var page = await fixture.HomeAsync();
        var card = page.QuerySelector("a[href='/game/401809270']")!;
        Assert.Contains("PHI leads 2–1", card.TextContent);
        Assert.Contains("Final", card.TextContent);
        Assert.Equal("2", card.QuerySelector("#home-score-401809270")!.TextContent);
        Assert.Equal("5", card.QuerySelector("#away-score-401809270")!.TextContent);
    }

    [Fact]
    public async Task Final_result_contradictions_invalidate_after_a_confirmed_result_inclusive_observation()
    {
        var changes = new (string Name, Action<JsonNode> Change)[]
        {
            ("pre result tally", e => e["competitions"]![0]!["series"]!["competitors"]![1]!["wins"] = 1),
            ("later tally on earlier game", e => e["competitions"]![0]!["series"]!["competitors"]![1]!["wins"] = 3),
            ("nonfinal game completed", e => e["status"]!["type"]!["completed"] = false),
            ("unconfirmed completed series", e => e["competitions"]![0]!["series"]!["completed"] = true),
            ("result winner has zero series wins", e =>
            {
                var competition = e["competitions"]![0]!;
                competition["series"]!["totalCompetitions"] = 7;
                competition["series"]!["competitors"]![0]!["wins"] = 3;
                competition["series"]!["competitors"]![1]!["wins"] = 0;
                competition["notes"]![0]!["headline"] = "ALCS - Game 3";
            }),
            ("two result winners", e => e["competitions"]![0]!["competitors"]![0]!["winner"] = true),
            ("no result winner supplied", e => e["competitions"]![0]!["competitors"]![1]!["winner"] = false),
            ("malformed result winner", e => e["competitions"]![0]!["competitors"]![1]!["winner"] = null),
            ("winner disagrees with score", e => { e["competitions"]![0]!["competitors"]![0]!["winner"] = true; e["competitions"]![0]!["competitors"]![1]!["winner"] = false; }),
            ("postponed description", e => e["status"]!["type"]!["description"] = "Postponed"),
            ("suspended detail", e => e["competitions"]![0]!["status"]!["detail"] = "Suspended"),
            ("canceled short detail", e => e["status"]!["shortDetail"] = "Canceled"),
            ("missing final name", e => e["status"]!["type"]!.AsObject().Remove("name")),
            ("MLB final OT name", e => e["status"]!["type"]!["name"] = "STATUS_FINAL_OT")
        };
        foreach (var (name, change) in changes)
        {
            UseSyntheticResult("401809270");
            await fixture.CollectAsync();
            using (var baseline = await fixture.HomeAsync()) Assert.Contains("PHI leads 2–1", baseline.QuerySelector("a[href='/game/401809270']")!.TextContent);
            change(fixture.Provider.Board["events"]![0]!);
            fixture.Host.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.CollectAsync();
            using var page = await fixture.HomeAsync();
            var card = page.QuerySelector("a[href='/game/401809270']")!;
            Assert.True(!card.TextContent.Contains("Best of"), name);
            Assert.Equal("2", card.QuerySelector("#home-score-401809270")!.TextContent);
            Assert.Equal("5", card.QuerySelector("#away-score-401809270")!.TextContent);
        }
    }

    [Theory]
    [InlineData("mlb", "401809252", "Series tied 1–1", "ALWC · Game 2 · Best of 3", "2", "1")]
    [InlineData("mlb", "401809270", "PHI leads 2–1", "ALDS · Game 3 · Best of 5", "2", "5")]
    [InlineData("mlb", "401809292", "ATL leads 3–0", "NLCS · Game 3 · Best of 7", "4", "1")]
    [InlineData("mlb", "401809300", "Series tied 2–2", "World Series · Game 4 · Best of 7", "2", "3")]
    [InlineData("nba", "401869369", "BOS leads 2–0", "East 1st Round · Game 2 · Best of 7", "110", "100")]
    [InlineData("nba", "401871162", "MIA wins series 4–0", "East Semifinals · Game 4 · Best of 7", "101", "111")]
    [InlineData("nba", "401873199", "MIA leads 2–1", "West Finals · Game 3 · Best of 7", "100", "110")]
    [InlineData("nhl", "401871407", "Series tied 2–2", "East 2nd Round · Game 4 · Best of 7", "2", "3")]
    [InlineData("nhl", "401872904", "EDM leads 2–0", "West Final · Game 2 · Best of 7", "1", "3")]
    [InlineData("nhl", "401777460", "FLA wins series 4–2", "Stanley Cup Final · Game 6 · Best of 7", "4", "2")]
    public async Task Confirmed_constructed_completed_tallies_are_result_inclusive_and_separate_from_game_scores(
        string sport, string id, string summary, string context, string homeScore, string awayScore)
    {
        UseSyntheticResult(id);
        await fixture.CollectAsync(sport);
        var requests = fixture.Provider.Requests;
        for (var load = 0; load < 2; load++)
        {
            using var page = await fixture.HomeAsync();
            var card = page.QuerySelector($"a[href='/game/{id}']")!;
            Assert.NotNull(card);
            Assert.Contains(summary, card.TextContent);
            Assert.Contains(context, card.TextContent);
            Assert.Equal(homeScore, card.QuerySelector($"#home-score-{id}")!.TextContent);
            Assert.Equal(awayScore, card.QuerySelector($"#away-score-{id}")!.TextContent);
            Assert.NotNull(card.QuerySelector("nb-sparkline"));
            Assert.Empty(card.QuerySelector($"#excitement-{id}")!.TextContent);
        }
        Assert.Equal(requests, fixture.Provider.Requests);
    }
}
