using System.Text.Json.Nodes;
using AngleSharp.Dom;
using Xunit;

namespace backend.tests;

public sealed class PlayoffSeriesEvidenceTests : IAsyncLifetime
{
    private readonly PlayoffSeriesFixture fixture = new();
    public Task InitializeAsync() => fixture.InitializeAsync();
    public Task DisposeAsync() => fixture.DisposeAsync();
    private JsonNode Event => fixture.Provider.Board["events"]![0]!;
    private JsonNode Competition => Event["competitions"]![0]!;

    [Fact]
    public async Task Successfully_received_malformed_event_evidence_revokes_only_its_series_instead_of_looking_like_transport_failure()
    {
        var changes = new (string Name, Action<JsonNode> Change)[]
        {
            ("string year", e => e["season"]!["year"] = "2026"),
            ("string phase", e => e["season"]!["type"] = "3"),
            ("string completion flag", e => e["status"]!["type"]!["completed"] = "false"),
            ("numeric status name", e => e["status"]!["type"]!["name"] = 1),
            ("numeric headline", e => e["competitions"]![0]!["notes"]![0]!["headline"] = 3),
            ("object notes", e => e["competitions"]![0]!["notes"] = new JsonObject())
        };
        foreach (var (name, change) in changes)
        {
            fixture.Provider.Board = PlayoffSeriesFixture.SyntheticBoard();
            fixture.Host.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.CollectAsync();
            using (var baseline = await fixture.HomeAsync()) Assert.Contains("Series tied 1–1", baseline.QuerySelector("a[href='/game/401907973']")!.TextContent);
            change(Event);
            var unaffected = PlayoffSeriesFixture.SyntheticBoard()["events"]![0]!.DeepClone();
            unaffected["id"] = "malformed-neighbor";
            unaffected["competitions"]![0]!["id"] = "malformed-neighbor";
            fixture.Provider.Board["events"]!.AsArray().Add(unaffected);
            fixture.Host.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.CollectAsync();
            using var page = await fixture.HomeAsync();
            var card = page.QuerySelector("a[href='/game/401907973']")!;
            Assert.NotNull(card);
            Assert.True(!card.TextContent.Contains("Series tied"), name);
            Assert.Equal("", card.QuerySelector("#home-score-401907973")!.TextContent);
            Assert.Contains("Series tied 1–1", page.QuerySelector("a[href='/game/malformed-neighbor']")!.TextContent);
        }
    }

    [Theory]
    [InlineData(3, 1, 1, "NLWC - Game 3", "Series tied 1–1", "NLWC · Game 3 · Best of 3")]
    [InlineData(5, 2, 2, "ALDS - Game 5", "Series tied 2–2", "ALDS · Game 5 · Best of 5")]
    [InlineData(7, 3, 3, "World Series - Game 7", "Series tied 3–3", "World Series · Game 7 · Best of 7")]
    [InlineData(5, 1, 2, "NLDS - Game 4", "PHI leads 2–1", "NLDS · Game 4 · Best of 5")]
    public async Task Scheduled_ordinals_include_only_completed_prior_games_for_each_supported_MLB_format(int bestOf, int homeWins, int awayWins, string headline, string summary, string context)
    {
        Competition["series"]!["totalCompetitions"] = bestOf;
        Competition["series"]!["competitors"]![0]!["wins"] = homeWins;
        Competition["series"]!["competitors"]![1]!["wins"] = awayWins;
        Competition["notes"]![0]!["headline"] = headline;
        await fixture.CollectAsync();
        using var page = await fixture.HomeAsync();
        var card = page.QuerySelector("a[href='/game/401907973']")!;
        Assert.Contains(summary, card.TextContent);
        Assert.Contains(context, card.TextContent);
        Assert.Equal("", card.QuerySelector("#home-score-401907973")!.TextContent);
        Assert.Equal("", card.QuerySelector("#away-score-401907973")!.TextContent);
    }

    [Fact]
    public async Task Missing_event_season_can_use_verified_board_season_without_inventing_optional_context()
    {
        fixture.Provider.Board["season"] = Event["season"]!.DeepClone();
        Event.AsObject().Remove("season");
        Event["status"]!["type"]!.AsObject().Remove("name");
        Competition["status"]!["type"]!.AsObject().Remove("name");
        Competition["competitors"]![0]!["team"]!.AsObject().Remove("id");
        Competition["competitors"]![1]!["team"]!.AsObject().Remove("id");
        await fixture.CollectAsync();
        using var page = await fixture.HomeAsync();
        Assert.Contains("Series tied 1–1", page.QuerySelector("a[href='/game/401907973']")!.TextContent);
    }

    [Fact]
    public async Task Synthetic_NBA_plural_round_label_is_verified_on_a_scheduled_observation()
    {
        Competition["series"]!["totalCompetitions"] = 7;
        Competition["notes"]![0]!["headline"] = "West Finals - Game 3";
        await fixture.CollectAsync("nba");
        using var page = await fixture.HomeAsync();
        var card = page.QuerySelector("a[href='/game/401907973']")!;
        Assert.Contains("Series tied 1–1", card.TextContent);
        Assert.Contains("West Finals · Game 3 · Best of 7", card.TextContent);
    }

    [Fact]
    public async Task Explicit_play_in_context_cannot_be_overridden_by_a_postseason_phase_number()
    {
        Competition["series"]!["totalCompetitions"] = 7;
        Competition["notes"]![0]!["headline"] = "NBA Play-In - East - 8th Seed Game";
        await fixture.CollectAsync("nba");
        using var page = await fixture.HomeAsync();
        Assert.DoesNotContain("Series tied", page.QuerySelector("a[href='/game/401907973']")!.TextContent);
    }

    [Theory]
    [InlineData("nba", "nba-board.json")]
    [InlineData("nba", "nba-decider-board.json")]
    [InlineData("nfl", "nfl-board.json")]
    public async Task Synthetic_play_in_and_single_game_football_remain_without_series_even_if_structured_tallies_are_supplied(string sport, string file)
    {
        var board = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ProHype", file)))!;
        var item = board["events"]![0]!.DeepClone();
        item["date"] = "2026-10-01T19:00:00Z";
        var competition = item["competitions"]![0]!;
        foreach (var participant in competition["competitors"]!.AsArray())
            participant!["team"]!["displayName"] = participant["team"]!["abbreviation"]!.DeepClone();
        competition["series"] = JsonNode.Parse("{\"type\":\"playoff\",\"completed\":false,\"totalCompetitions\":7,\"competitors\":[]}");
        foreach (var participant in competition["competitors"]!.AsArray())
            competition["series"]!["competitors"]!.AsArray().Add(new JsonObject { ["id"] = participant!["id"]!.DeepClone(), ["wins"] = 1 });
        fixture.Provider.Board = new JsonObject { ["events"] = new JsonArray(item) };
        await fixture.CollectAsync(sport);
        using var page = await fixture.HomeAsync();
        var card = page.QuerySelector($"a[href='/game/{item["id"]!.GetValue<string>()}']")!;
        Assert.NotNull(card);
        Assert.DoesNotContain("Best of", card.TextContent);
        Assert.DoesNotContain("Series tied", card.TextContent);
    }

    [Fact]
    public async Task Free_text_and_current_game_scores_never_replace_explicit_series_wins()
    {
        Competition["series"]!["summary"] = "PHI wins series 99-0";
        Competition["competitors"]![0]!["score"] = "42";
        Competition["competitors"]![1]!["score"] = "37";
        await fixture.CollectAsync();
        using (var page = await fixture.HomeAsync())
        {
            var card = page.QuerySelector("a[href='/game/401907973']")!;
            Assert.Contains("Series tied 1–1", card.TextContent);
            Assert.Equal("", card.QuerySelector("#home-score-401907973")!.TextContent);
            Assert.Equal("", card.QuerySelector("#away-score-401907973")!.TextContent);
        }
        Competition["series"]!["competitors"]![0]!.AsObject().Remove("wins");
        fixture.Host.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.CollectAsync();
        using var refreshed = await fixture.HomeAsync();
        Assert.DoesNotContain("Best of", refreshed.QuerySelector("a[href='/game/401907973']")!.TextContent);
    }

    [Fact]
    public async Task Unsupported_or_contradictory_season_phase_and_identity_are_unavailable()
    {
        var changes = new (string Name, Action<JsonNode> Change)[]
        {
            ("regular season", b => b["events"]![0]!["season"]!["type"] = 2),
            ("preseason", b => { b["events"]![0]!["season"]!["type"] = 1; b["events"]![0]!["season"]!["slug"] = "pre-season"; }),
            ("unclassified", b => b["events"]![0]!.AsObject().Remove("season")),
            ("unclassified numeric phase", b => b["events"]![0]!["season"]!["type"] = 0),
            ("play in", b => b["events"]![0]!["season"]!["type"] = 5),
            ("wrong year", b => b["events"]![0]!["season"]!["year"] = 2025),
            ("root year contradiction", b => b["season"] = JsonNode.Parse("{\"year\":2025,\"type\":3}")),
            ("root phase contradiction", b => b["season"] = JsonNode.Parse("{\"year\":2026,\"type\":2}")),
            ("slug contradiction", b => b["events"]![0]!["season"]!["slug"] = "regular-season"),
            ("root slug contradiction", b => b["season"] = JsonNode.Parse("{\"year\":2026,\"type\":3,\"slug\":\"regular-season\"}")),
            ("duplicate event identity", b => b["events"]!.AsArray().Add(b["events"]![0]!.DeepClone()))
        };
        foreach (var (name, change) in changes)
        {
            fixture.Provider.Board = PlayoffSeriesFixture.SyntheticBoard();
            fixture.Host.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.CollectAsync();
            using (var baseline = await fixture.HomeAsync()) Assert.Contains("Series tied 1–1", baseline.QuerySelector("a[href='/game/401907973']")!.TextContent);
            change(fixture.Provider.Board);
            fixture.Host.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.CollectAsync();
            using var page = await fixture.HomeAsync();
            var card = page.QuerySelector("a[href='/game/401907973']");
            Assert.True(card is null || !card.TextContent.Contains("Best of"), name);
        }
    }

    [Fact]
    public async Task Supplied_malformed_season_and_sport_metadata_is_not_a_fallback_to_optional_absence()
    {
        var changes = new (string Name, Action<JsonNode> Change)[]
        {
            ("root string year", b => b["season"] = JsonNode.Parse("{\"year\":\"2026\",\"type\":3}")),
            ("root string phase", b => b["season"] = JsonNode.Parse("{\"year\":2026,\"type\":\"3\"}")),
            ("root fractional phase", b => b["season"] = JsonNode.Parse("{\"year\":2026,\"type\":3.5}")),
            ("root scalar season", b => b["season"] = "post-season"),
            ("league numeric slug", b => b["leagues"] = JsonNode.Parse("[{\"slug\":1}]")),
            ("league string year", b => b["leagues"] = JsonNode.Parse("[{\"slug\":\"mlb\",\"season\":{\"year\":\"2026\"}}]")),
            ("league string phase", b => b["leagues"] = JsonNode.Parse("[{\"slug\":\"mlb\",\"season\":{\"year\":2026,\"type\":{\"type\":\"3\"}}}]")),
            ("league scalar phase", b => b["leagues"] = JsonNode.Parse("[{\"slug\":\"mlb\",\"season\":{\"year\":2026,\"type\":3}}]")),
            ("event scalar season", b => { b["season"] = JsonNode.Parse("{\"year\":2026,\"type\":3}"); b["events"]![0]!["season"] = "post-season"; }),
            ("event numeric slug", b => b["events"]![0]!["season"]!["slug"] = 3)
        };
        foreach (var (name, change) in changes)
        {
            fixture.Provider.Board = PlayoffSeriesFixture.SyntheticBoard();
            fixture.Host.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.CollectAsync();
            using (var baseline = await fixture.HomeAsync()) Assert.Contains("Series tied 1–1", baseline.QuerySelector("a[href='/game/401907973']")!.TextContent);
            change(fixture.Provider.Board);
            fixture.Host.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.CollectAsync();
            using var page = await fixture.HomeAsync();
            Assert.True(!page.QuerySelector("a[href='/game/401907973']")!.TextContent.Contains("Best of"), name);
        }
    }

    [Fact]
    public async Task Supplied_league_season_and_nested_team_identity_cannot_be_treated_as_absent()
    {
        var changes = new (string Name, Action<JsonNode> Change)[]
        {
            ("different league year", b => b["leagues"] = JsonNode.Parse("[{\"slug\":\"mlb\",\"season\":{\"year\":2025}}]")),
            ("different league phase", b => b["leagues"] = JsonNode.Parse("[{\"slug\":\"mlb\",\"season\":{\"year\":2026,\"type\":{\"type\":2}}}]")),
            ("null nested id", b => b["events"]![0]!["competitions"]![0]!["competitors"]![0]!["team"]!["id"] = null)
        };
        foreach (var (name, change) in changes)
        {
            fixture.Provider.Board = PlayoffSeriesFixture.SyntheticBoard();
            change(fixture.Provider.Board);
            fixture.Host.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.CollectAsync();
            using var page = await fixture.HomeAsync();
            Assert.True(!page.QuerySelector("a[href='/game/401907973']")!.TextContent.Contains("Series tied"), name);
        }
    }

    [Fact]
    public async Task Absent_optional_context_is_omitted_but_known_round_without_ordinal_remains_verified()
    {
        Competition.AsObject().Remove("notes");
        Event.AsObject().Remove("week");
        await fixture.CollectAsync();
        using (var page = await fixture.HomeAsync())
        {
            var card = page.QuerySelector("a[href='/game/401907973']")!;
            Assert.Contains("Series tied 1–1", card.TextContent);
            Assert.Contains("Best of 3", card.TextContent);
            Assert.DoesNotContain("Game 3", card.TextContent);
            Assert.DoesNotContain("NLWC", card.TextContent);
        }
        Competition["notes"] = JsonNode.Parse("[{\"headline\":\"NLWC\"}]");
        fixture.Host.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.CollectAsync();
        using var refreshed = await fixture.HomeAsync();
        Assert.Contains("NLWC · Best of 3", refreshed.QuerySelector("a[href='/game/401907973']")!.TextContent);
    }

    [Fact]
    public async Task Collection_cannot_attach_another_sports_payload_to_an_existing_game()
    {
        await fixture.CollectAsync();
        fixture.Host.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.CollectAsync("nba");
        using var page = await fixture.HomeAsync();
        Assert.DoesNotContain("Series tied", page.QuerySelector("a[href='/game/401907973']")!.TextContent);
    }

    [Fact]
    public async Task Malformed_series_rejects_only_that_game_and_successfully_revokes_its_previous_standing()
    {
        var changes = new (string Name, Action<JsonNode> Change)[]
        {
            ("missing series", c => c.AsObject().Remove("series")),
            ("null series", c => c["series"] = null),
            ("string series", c => c["series"] = "ATL leads 1-1"),
            ("array series", c => c["series"] = new JsonArray()),
            ("missing series type", c => c["series"]!.AsObject().Remove("type")),
            ("wrong series type", c => c["series"]!["type"] = "regular"),
            ("missing completed", c => c["series"]!.AsObject().Remove("completed")),
            ("null completed", c => c["series"]!["completed"] = null),
            ("string completed", c => c["series"]!["completed"] = "false"),
            ("false completion claim", c => c["series"]!["completed"] = true),
            ("missing format", c => c["series"]!.AsObject().Remove("totalCompetitions")),
            ("fractional format", c => c["series"]!["totalCompetitions"] = 3.5),
            ("single game format", c => c["series"]!["totalCompetitions"] = 1),
            ("even format", c => c["series"]!["totalCompetitions"] = 4),
            ("unsupported format", c => c["series"]!["totalCompetitions"] = 9),
            ("missing wins", c => c["series"]!["competitors"]![0]!.AsObject().Remove("wins")),
            ("null wins", c => c["series"]!["competitors"]![0]!["wins"] = null),
            ("string wins", c => c["series"]!["competitors"]![0]!["wins"] = "1"),
            ("fractional wins", c => c["series"]!["competitors"]![0]!["wins"] = 1.5),
            ("negative wins", c => c["series"]!["competitors"]![0]!["wins"] = -1),
            ("overflow wins", c => c["series"]!["competitors"]![0]!["wins"] = long.MaxValue),
            ("beyond threshold", c => c["series"]!["competitors"]![0]!["wins"] = 3),
            ("unconfirmed threshold", c => c["series"]!["competitors"]![0]!["wins"] = 2),
            ("two winners", c => { c["series"]!["completed"] = true; c["series"]!["competitors"]![0]!["wins"] = 2; c["series"]!["competitors"]![1]!["wins"] = 2; }),
            ("wrong series member", c => c["series"]!["competitors"]![0]!["id"] = "99"),
            ("duplicate series members", c => c["series"]!["competitors"]![0]!["id"] = c["series"]!["competitors"]![1]!["id"]!.DeepClone()),
            ("missing series member", c => c["series"]!["competitors"]!.AsArray().RemoveAt(1)),
            ("third series member", c => c["series"]!["competitors"]!.AsArray().Add(JsonNode.Parse("{\"id\":\"99\",\"wins\":0}"))),
            ("null members", c => c["series"]!["competitors"] = null),
            ("wrong competition", c => c["id"] = "another-game"),
            ("duplicate competition participants", c => c["competitors"]![0]!["id"] = c["competitors"]![1]!["id"]!.DeepClone()),
            ("nested team mismatch", c => c["competitors"]![0]!["team"]!["id"] = "99"),
            ("abbreviation mismatch", c => c["competitors"]![0]!["team"]!["abbreviation"] = "SEA"),
            ("missing role", c => c["competitors"]![0]!.AsObject().Remove("homeAway")),
            ("duplicate roles", c => c["competitors"]![0]!["homeAway"] = "away"),
            ("second competition", c => Event["competitions"]!.AsArray().Add(c.DeepClone())),
            ("earlier ordinal", c => c["notes"]![0]!["headline"] = "NLWC - Game 2"),
            ("duplicate context", c => c["notes"]!.AsArray().Add(c["notes"]![0]!.DeepClone()))
        };
        foreach (var (name, change) in changes)
        {
            fixture.Provider.Board = PlayoffSeriesFixture.SyntheticBoard();
            var unaffected = Event.DeepClone();
            unaffected["id"] = "unaffected";
            unaffected["competitions"]![0]!["id"] = "unaffected";
            fixture.Provider.Board["events"]!.AsArray().Add(unaffected);
            fixture.Host.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.CollectAsync();
            using (var baseline = await fixture.HomeAsync()) Assert.Contains("Series tied 1–1", baseline.QuerySelector("a[href='/game/401907973']")!.TextContent);
            change(Competition);
            fixture.Host.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.CollectAsync();
            using var page = await fixture.HomeAsync();
            var card = page.QuerySelector("a[href='/game/401907973']")!;
            Assert.NotNull(card);
            Assert.True(!card.TextContent.Contains("Series tied") && !card.TextContent.Contains("Best of"), name);
            Assert.Contains("Hype unavailable", card.TextContent);
            Assert.Contains("Series tied 1–1", page.QuerySelector("a[href='/game/unaffected']")!.TextContent);
        }
    }

    [Fact]
    public async Task Participant_order_and_coherent_venue_corrections_keep_wins_with_provider_team_identity()
    {
        Competition["series"]!["competitors"]![0]!["wins"] = 1;
        Competition["series"]!["competitors"]![1]!["wins"] = 0;
        Competition["notes"]![0]!["headline"] = "NLWC - Game 2";
        await fixture.CollectAsync();
        using (var page = await fixture.HomeAsync()) Assert.Contains("ATL leads 1–0", page.QuerySelector("a[href='/game/401907973']")!.TextContent);
        var participants = Competition["competitors"]!.AsArray();
        var members = Competition["series"]!["competitors"]!.AsArray();
        Competition["competitors"] = new JsonArray(participants.Reverse().Select(p => p!.DeepClone()).ToArray());
        Competition["series"]!["competitors"] = new JsonArray(members.Reverse().Select(p => p!.DeepClone()).ToArray());
        fixture.Host.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.CollectAsync();
        using (var page = await fixture.HomeAsync()) Assert.Contains("ATL leads 1–0", page.QuerySelector("a[href='/game/401907973']")!.TextContent);
        foreach (var participant in Competition["competitors"]!.AsArray())
            participant!["homeAway"] = participant["homeAway"]!.GetValue<string>() == "home" ? "away" : "home";
        fixture.Host.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.CollectAsync();
        using var corrected = await fixture.HomeAsync();
        var card = corrected.QuerySelector("a[href='/game/401907973']")!;
        Assert.Contains("ATL leads 1–0", card.TextContent);
        Assert.Contains("ATL", card.QuerySelector("#scores-401907973")!.Children[0].TextContent);
        Assert.Contains("PHI", card.QuerySelector("#scores-401907973")!.Children[1].TextContent);
    }

    [Fact]
    public async Task Supplied_status_and_result_contradictions_revoke_instead_of_becoming_optional_absence()
    {
        var changes = new (string Name, Action<JsonNode> Change)[]
        {
            ("pre with final name", e => e["status"]!["type"]!["name"] = "STATUS_FINAL"),
            ("contest canceled", e => e["competitions"]![0]!["status"]!["type"]!["name"] = "STATUS_CANCELED"),
            ("pre completed", e => e["status"]!["type"]!["completed"] = true),
            ("different status states", e => e["competitions"]![0]!["status"]!["type"]!["state"] = "in"),
            ("scheduled winner", e => e["competitions"]![0]!["competitors"]![0]!["winner"] = true),
            ("malformed winner", e => e["competitions"]![0]!["competitors"]![0]!["winner"] = "true"),
            ("missing completion flag", e => e["status"]!["type"]!.AsObject().Remove("completed")),
            ("missing event status", e => e.AsObject().Remove("status")),
            ("missing event status type", e => e["status"]!.AsObject().Remove("type")),
            ("missing contest status", e => e["competitions"]![0]!.AsObject().Remove("status"))
        };
        foreach (var (name, change) in changes)
        {
            fixture.Provider.Board = PlayoffSeriesFixture.SyntheticBoard();
            fixture.Host.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.CollectAsync();
            using (var baseline = await fixture.HomeAsync()) Assert.Contains("Series tied 1–1", baseline.QuerySelector("a[href='/game/401907973']")!.TextContent);
            change(Event);
            fixture.Host.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.CollectAsync();
            using var page = await fixture.HomeAsync();
            var card = page.QuerySelector("a[href='/game/401907973']")!;
            Assert.NotNull(card);
            Assert.True(!card.TextContent.Contains("Series tied"), name);
            Assert.Equal("", card.QuerySelector("#home-score-401907973")!.TextContent);
        }
    }

    [Fact]
    public async Task Scoreboard_league_identity_must_match_the_collected_sport()
    {
        fixture.Provider.Board["leagues"] = JsonNode.Parse("[{\"slug\":\"nba\"}]");
        await fixture.CollectAsync();
        using var page = await fixture.HomeAsync();
        Assert.DoesNotContain("Series tied", page.QuerySelector("a[href='/game/401907973']")!.TextContent);
    }

    [Theory]
    [InlineData("NLWC - Game 0")]
    [InlineData("NLWC - Game 8")]
    [InlineData("NLWC - Game 2.5")]
    [InlineData("NLWC - Game unknown")]
    [InlineData("NBA Finals - Game 3")]
    public async Task Supplied_series_context_is_not_silently_discarded_when_contradictory(string headline)
    {
        Competition["notes"]![0]!["headline"] = headline;
        await fixture.CollectAsync();
        using var page = await fixture.HomeAsync();
        var card = page.QuerySelector("a[href='/game/401907973']")!;
        Assert.NotNull(card);
        Assert.DoesNotContain("Series tied", card.TextContent);
    }

    [Fact]
    public async Task Supplied_Wild_Card_round_cannot_contradict_the_verified_format()
    {
        Competition["series"]!["totalCompetitions"] = 5;
        await fixture.CollectAsync();
        using var page = await fixture.HomeAsync();
        var card = page.QuerySelector("a[href='/game/401907973']")!;
        Assert.NotNull(card);
        Assert.DoesNotContain("Series tied", card.TextContent);
        Assert.DoesNotContain("Best of", card.TextContent);
    }
}
