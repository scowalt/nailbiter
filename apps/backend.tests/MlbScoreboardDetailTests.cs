using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using backend.GraphQL.Types;
using backend.Models;
using backend.Services;
using Backend.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.FSharp.Core;
using Xunit;

namespace backend.tests;

public sealed class MlbScoreboardDetailTests : IClassFixture<SharedPostgreSql>, IAsyncLifetime
{
    private const string Doubleheader = "991300104";
    private static readonly DateTimeOffset Start = new(2027, 7, 10, 23, 0, 0, TimeSpan.Zero);
    private static readonly JsonObject Espn = Load("espn-basic.json");
    private static readonly JsonObject Rich = Load("rich-frames.json");

    private static readonly Dictionary<string, string> States = new()
    {
        ["STATUS_SCHEDULED"] = "scheduled",
        ["STATUS_IN_PROGRESS"] = "in_progress",
        ["STATUS_FINAL"] = "final",
        ["STATUS_POSTPONED"] = "postponed",
        ["STATUS_CANCELED"] = "canceled",
        ["STATUS_RAIN_DELAY"] = "delayed",
        ["STATUS_SUSPENDED"] = "suspended",
    };

    private readonly ReadyBroadcaster broadcaster = new();
    private readonly HostedGameFixture host;

    public MlbScoreboardDetailTests(SharedPostgreSql server) =>
        host = new(Start, services =>
        {
            services.RemoveAll<ISseBroadcaster>();
            services.AddSingleton<ISseBroadcaster>(broadcaster);
        }, server);

    public Task InitializeAsync() => host.InitializeAsync();
    public Task DisposeAsync() => host.DisposeAsync();

    [Fact]
    public async Task A_live_rich_frame_renders_strip_situation_odds_chart_then_line_score_in_reading_order()
    {
        await host.SeedAsync(MlbGame(Doubleheader), [Odds(Doubleheader, "2027-07-10T23:20:00Z", 0.45), Odds(Doubleheader, "2027-07-10T23:29:00Z", 0.4)]);
        At("2027-07-10T23:30:00Z");
        Accepted(await AcceptAsync(Frame("active-complete", "opening")));
        At("2027-07-10T23:31:00Z");

        using var page = await PageAsync(Doubleheader);

        var narrow = page.QuerySelector(".page-narrow")!;
        var scoreboard = narrow.QuerySelector("#detail-scoreboard")!;
        Assert.Equal("Score", scoreboard.QuerySelector("h2.visually-hidden")?.TextContent.Trim());
        var strip = scoreboard.QuerySelector(".scoreboard")!;
        var situation = scoreboard.QuerySelector("#mlb-situation");
        Assert.NotNull(situation);
        var probbar = scoreboard.QuerySelector("#detail-probbar")!;
        var chart = page.QuerySelector("nb-win-probability-chart")!.Closest(".panel")!;
        var lineScore = page.QuerySelector("section#mlb-line-score");
        Assert.NotNull(lineScore);
        AssertInDocumentOrder(page, strip, situation, probbar, chart, lineScore);
        Assert.Same(narrow, lineScore.ParentElement);
        Assert.Same(chart, lineScore.PreviousElementSibling);

        Assert.Equal(["Away", "Home"], strip.QuerySelectorAll(".scoreboard-team .scoreboard-side").Select(e => e.TextContent.Trim()));
        Assert.Equal("3", page.QuerySelector("#detail-away-score")!.TextContent.Trim());
        Assert.Equal("1", page.QuerySelector("#detail-home-score")!.TextContent.Trim());
        Assert.Equal("Top 5th", Clock(page));
        Assert.NotNull(probbar.QuerySelector(".detail-probability"));

        Assert.Equal("Situation", situation.QuerySelector("h3.visually-hidden")?.TextContent.Trim());
        var facts = Facts(situation);
        Assert.Equal(["Count", "Outs", "Bases", "Batting", "Pitching"], facts.Keys);
        AssertCount(facts["Count"], "2-1", "2 balls, 1 strike");
        Assert.Equal("1", facts["Outs"].TextContent.Trim());
        AssertBases(facts["Bases"], [true, false, true], "Runners on 1st and 3rd", "1st: Synthetic Runner A", "3rd: Synthetic Runner B");
        Assert.Equal("Synthetic Batter 7", facts["Batting"].TextContent.Trim());
        Assert.Equal("Synthetic Pitcher 3", facts["Pitching"].TextContent.Trim());

        var table = AssertLineScoreShell(page, 5);
        AssertRow(table, 0, "ATL", "Synthetic ATL", ["0", "2", "0", "0", "1"], "3", "6", "0");
        AssertRow(table, 1, "PHI", "Synthetic PHI", ["1", "0", "0", "0", Pending], "1", "3", "1");
    }

    [Fact]
    public async Task A_zero_count_frame_keeps_zeros_and_marks_a_missing_inning_not_recorded()
    {
        await host.SeedAsync(MlbGame(Doubleheader), []);
        var report = Frame("zero-count-empty-bases", "advance");
        report["home"]!["innings"]![1] = null;
        At("2027-07-10T23:50:00Z");
        Accepted(await AcceptAsync(report));
        At("2027-07-10T23:50:30Z");

        using var page = await PageAsync(Doubleheader);

        Assert.Equal("Bottom 6th", Clock(page));
        var facts = Facts(Situation(page));
        Assert.Equal(["Count", "Outs", "Bases", "Batting", "Pitching"], facts.Keys);
        AssertCount(facts["Count"], "0-0", "0 balls, 0 strikes");
        Assert.Equal("0", facts["Outs"].TextContent.Trim());
        AssertBases(facts["Bases"], [false, false, false], "Bases empty");
        Assert.DoesNotContain(":", facts["Bases"].TextContent);
        Assert.Equal("Synthetic Batter 2", facts["Batting"].TextContent.Trim());
        Assert.Equal("Synthetic Pitcher 5", facts["Pitching"].TextContent.Trim());

        var table = AssertLineScoreShell(page, 6);
        AssertRow(table, 0, "ATL", "Synthetic ATL", ["0", "2", "0", "0", "1", "0"], "3", "7", "0");
        AssertRow(table, 1, "PHI", "Synthetic PHI", ["1", NotRecorded, "0", "0", "0", "1"], "2", "5", "1");
    }

    [Fact]
    public async Task A_partial_frame_shows_only_the_verified_count_outs_and_batter()
    {
        await host.SeedAsync(MlbGame(Doubleheader), []);
        At("2027-07-11T00:00:00Z");
        Accepted(await AcceptAsync(Frame("partial-optional", "partial")));
        At("2027-07-11T00:00:30Z");

        using var page = await PageAsync(Doubleheader);

        Assert.Equal("Bottom 6th", Clock(page));
        var situation = Situation(page);
        var facts = Facts(situation);
        Assert.Equal(["Count", "Outs", "Batting"], facts.Keys);
        AssertCount(facts["Count"], "1-0", "1 ball, 0 strikes");
        Assert.Equal("2", facts["Outs"].TextContent.Trim());
        Assert.Equal("Synthetic Batter 12", facts["Batting"].TextContent.Trim());
        Assert.Null(situation.QuerySelector(".bases-diamond"));
        Assert.DoesNotContain("Pitcher", situation.TextContent);

        var table = AssertLineScoreShell(page, 6);
        AssertRow(table, 0, "ATL", "Synthetic ATL", ["0", "2", "0", "0", "1", "0"], "3", NotRecorded, "0");
        AssertRow(table, 1, "PHI", "Synthetic PHI", [Pending, Pending, Pending, Pending, Pending, Pending], "2", NotRecorded, NotRecorded);
    }

    [Fact]
    public async Task An_occupied_base_with_an_unknown_runner_shows_the_runner_without_a_name()
    {
        await host.SeedAsync(MlbGame(Doubleheader), []);
        At("2027-07-11T00:05:00Z");
        Accepted(await AcceptAsync(Frame("runner-unknown", "advance")));
        At("2027-07-11T00:05:10Z");

        using var page = await PageAsync(Doubleheader);

        Assert.Equal("Bottom 6th", Clock(page));
        var situation = Situation(page);
        var facts = Facts(situation);
        Assert.Equal(["Count", "Outs", "Bases"], facts.Keys);
        AssertCount(facts["Count"], "0-1", "0 balls, 1 strike");
        Assert.Equal("2", facts["Outs"].TextContent.Trim());
        AssertBases(facts["Bases"], [true, false, false], "Runner on 1st");
        Assert.DoesNotContain("1st:", facts["Bases"].TextContent);
        Assert.DoesNotContain("Synthetic", situation.TextContent);
    }

    [Fact]
    public async Task A_live_frame_without_rich_context_says_so_and_shows_the_inning_without_a_half()
    {
        await host.SeedAsync(MlbGame(Doubleheader), []);
        At("2027-07-11T00:10:00Z");
        Accepted(await AcceptAsync(Frame("rich-unavailable", "partial")));
        At("2027-07-11T00:10:10Z");

        using var page = await PageAsync(Doubleheader);

        Assert.Equal("6th inning", Clock(page));
        var situation = Situation(page);
        Assert.Null(situation.QuerySelector(".situation-facts"));
        Assert.Equal("Count, runners and matchup unavailable", situation.TextContent.Trim());
        Assert.NotNull(page.QuerySelector("section#mlb-line-score"));
    }

    [Fact]
    public async Task The_situation_expires_exactly_two_minutes_after_observation_while_odds_and_chart_remain()
    {
        await host.SeedAsync(MlbGame(Doubleheader), [Odds(Doubleheader, "2027-07-10T23:20:00Z", 0.45), Odds(Doubleheader, "2027-07-10T23:29:00Z", 0.4)]);
        At("2027-07-10T23:30:00Z");
        Accepted(await AcceptAsync(Frame("active-complete", "opening")));

        host.Clock.SetUtcNow(Instant("2027-07-10T23:32:00Z").AddMilliseconds(-1));
        using (var fresh = await PageAsync(Doubleheader))
        {
            Assert.Equal("Top 5th", Clock(fresh));
            Assert.Equal(["Count", "Outs", "Bases", "Batting", "Pitching"], Facts(Situation(fresh)).Keys);
        }

        At("2027-07-10T23:32:00Z");
        using var expired = await PageAsync(Doubleheader);

        var situation = Situation(expired);
        Assert.Null(situation.QuerySelector(".situation-facts"));
        Assert.Equal("Last known score · live situation unavailable", situation.TextContent.Trim());
        Assert.Equal("5th inning", Clock(expired));
        Assert.DoesNotContain("Synthetic", expired.QuerySelector("#detail-scoreboard")!.TextContent);
        Assert.Equal("3", expired.QuerySelector("#detail-away-score")!.TextContent.Trim());
        Assert.Equal("1", expired.QuerySelector("#detail-home-score")!.TextContent.Trim());
        Assert.NotNull(expired.QuerySelector("#detail-probbar .detail-probability"));
        Assert.NotNull(expired.QuerySelector("nb-win-probability-chart"));
        Assert.NotNull(expired.QuerySelector("section#mlb-line-score"));
    }

    [Fact]
    public async Task A_recorded_frame_shows_the_recorded_score_without_a_live_situation()
    {
        await host.SeedAsync(MlbGame(Doubleheader), []);
        At("2027-07-10T23:30:00Z");
        var report = Frame("active-complete", "opening");
        report["provenance"] = "recorded";
        Accepted(await AcceptAsync(report));
        At("2027-07-10T23:30:10Z");

        using var page = await PageAsync(Doubleheader);

        var situation = Situation(page);
        Assert.Null(situation.QuerySelector(".situation-facts"));
        Assert.Equal("Recorded score · no live situation", situation.TextContent.Trim());
        Assert.Equal("5th inning", Clock(page));
        Assert.NotNull(page.QuerySelector("section#mlb-line-score"));
    }

    [Theory]
    [InlineData("991300101", "retained-after-final", "final", "Final", "Final", "status-final", 9)]
    [InlineData("991300102", null, "final", "Final · 11 innings", "Final", "status-final", 11)]
    [InlineData("991300108", "retained-while-delayed", "in_progress", "Delayed · 6th inning", "Delayed", "status-scheduled", 6)]
    [InlineData("991300109", "retained-while-suspended", "in_progress", "Suspended · 7th inning", "Suspended", "status-scheduled", 7)]
    [InlineData("991300105", null, "postponed", "Postponed", "Postponed", "status-scheduled", 0)]
    [InlineData("991300107", null, "canceled", "Canceled", "Canceled", "status-final", 0)]
    public async Task Interrupted_and_ended_games_show_a_distinct_clock_label_a_matching_badge_and_no_situation_facts(
        string gameId, string? frameId, string status, string clock, string badge, string badgeClass, int innings)
    {
        await host.SeedAsync(MlbGame(gameId, status), []);
        At("2027-07-10T23:30:00Z");
        Accepted(await AcceptAsync(frameId is null ? Report(gameId, "opening", Instant("2027-07-10T23:30:00Z")) : Frame(frameId, "opening")));
        At("2027-07-10T23:30:10Z");

        using var page = await PageAsync(gameId);

        var badgeElement = page.QuerySelector("#detail-status")!;
        Assert.Equal(badge, badgeElement.TextContent.Trim());
        Assert.Contains(badgeClass, badgeElement.ClassList);
        Assert.Equal(clock, Clock(page));
        var situation = page.QuerySelector("#mlb-situation");
        Assert.NotNull(situation);
        Assert.Equal(0, situation.ChildElementCount);
        Assert.Equal("", situation.TextContent.Trim());
        foreach (var person in new[] { "Synthetic Batter", "Synthetic Pitcher", "Synthetic Runner" })
            Assert.DoesNotContain(person, page.Body!.TextContent);
        var competitors = Competition(gameId, "opening")["competitors"]!.AsArray();
        Assert.Equal(Score(competitors, "away"), page.QuerySelector("#detail-away-score")!.TextContent.Trim());
        Assert.Equal(Score(competitors, "home"), page.QuerySelector("#detail-home-score")!.TextContent.Trim());
        if (innings == 0)
            Assert.Null(page.QuerySelector("#mlb-line-score"));
        else
            AssertLineScoreShell(page, innings);
    }

    [Theory]
    [InlineData("991300105", true, "postponed", "Postponed")]
    [InlineData("991300105", false, "postponed", "Postponed")]
    [InlineData("991300107", true, "canceled", "Canceled")]
    public async Task A_postponed_or_canceled_game_shows_no_current_quote_despite_a_fresh_snapshot(
        string gameId, bool withFrame, string status, string badge)
    {
        await host.SeedAsync(MlbGame(gameId, status), [Odds(gameId, "2027-07-10T23:29:30Z", 0.4)]);
        At("2027-07-10T23:30:00Z");
        if (withFrame)
            Accepted(await AcceptAsync(Report(gameId, "opening", Instant("2027-07-10T23:30:00Z"))));
        At("2027-07-10T23:30:10Z");

        using var page = await PageAsync(gameId);

        Assert.Equal(badge, page.QuerySelector("#detail-status")!.TextContent.Trim());
        var probbar = page.QuerySelector("#detail-probbar");
        Assert.Null(probbar?.QuerySelector(".detail-probability"));
        Assert.DoesNotContain("%", probbar?.TextContent ?? "");
        Assert.Null(page.QuerySelector(".situation-facts"));
    }

    [Fact]
    public async Task A_final_with_the_home_team_ahead_marks_the_unplayed_bottom_ninth_and_shows_no_current_quote()
    {
        await host.SeedAsync(MlbGame("991300101", "final"), [Odds("991300101", "2027-07-10T20:00:00Z", 0.55), Odds("991300101", "2027-07-10T20:10:00Z", 0.7)]);
        At("2027-07-10T23:30:00Z");
        Accepted(await AcceptAsync(Frame("retained-after-final", "opening")));

        using var page = await PageAsync("991300101");

        Assert.Equal("Final", Clock(page));
        var table = AssertLineScoreShell(page, 9);
        AssertRow(table, 0, "NYY", "Synthetic NYY", ["0", "1", "0", "0", "2", "0", "0", "0", "0"], "3", "7", "1");
        AssertRow(table, 1, "BOS", "Synthetic BOS", ["1", "0", "2", "0", "0", "1", "0", "0", NotPlayed], "4", "9", "0");
        var probbar = page.QuerySelector("#detail-probbar")!;
        Assert.Null(probbar.QuerySelector(".detail-probability"));
        Assert.Equal("", probbar.TextContent.Trim());
        Assert.NotNull(page.QuerySelector("nb-win-probability-chart"));
    }

    [Fact]
    public async Task A_live_game_without_odds_says_live_probability_is_unavailable_instead_of_an_even_bar()
    {
        await host.SeedAsync(MlbGame(Doubleheader), []);
        At("2027-07-10T23:30:00Z");
        Accepted(await AcceptAsync(Frame("active-complete", "opening")));
        At("2027-07-10T23:30:10Z");

        using var page = await PageAsync(Doubleheader);

        Assert.NotNull(page.QuerySelector("#mlb-situation .situation-facts"));
        var probbar = page.QuerySelector("#detail-probbar")!;
        Assert.Equal("Live probability unavailable", probbar.TextContent.Trim());
        Assert.Null(probbar.QuerySelector(".detail-probability"));
        Assert.DoesNotContain("50%", page.QuerySelector("#detail-scoreboard")!.OuterHtml);
        Assert.Null(page.QuerySelector("nb-win-probability-chart"));
    }

    [Fact]
    public async Task One_fresh_quote_shows_the_current_probability_bar_without_a_chart()
    {
        await host.SeedAsync(MlbGame(Doubleheader), [Odds(Doubleheader, "2027-07-10T23:29:00Z", 0.4)]);
        At("2027-07-10T23:30:00Z");
        Accepted(await AcceptAsync(Frame("active-complete", "opening")));
        At("2027-07-10T23:30:10Z");

        using var page = await PageAsync(Doubleheader);

        Assert.NotNull(page.QuerySelector("#mlb-situation .situation-facts"));
        var bar = page.QuerySelector("#detail-probbar .detail-probability");
        Assert.NotNull(bar);
        Assert.Equal(["ATL 60%", "PHI 40%"], bar.QuerySelectorAll(".probability-labels span").Select(s => s.TextContent.Trim()));
        Assert.DoesNotContain("Live probability unavailable", page.QuerySelector("#detail-probbar")!.TextContent);
        Assert.Null(page.QuerySelector("nb-win-probability-chart"));
    }

    [Fact]
    public async Task A_live_update_patches_board_scores_situation_and_line_score_identical_to_a_reload()
    {
        await host.SeedAsync(MlbGame(Doubleheader), [Odds(Doubleheader, "2027-07-10T23:29:00Z", 0.4)]);
        At("2027-07-10T23:30:00Z");
        Accepted(await AcceptAsync(Frame("active-complete", "opening")));
        At("2027-07-10T23:30:30Z");

        await using var stream = new SsePeer(host.Client, $"/sse/game/{Doubleheader}");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var reading = stream.ReadForAsync("detail-away-score");
        await broadcaster.ReadyAsync([$"game:{Doubleheader}"], deadline.Token);
        await broadcaster.PublishAsync(Update(Doubleheader, Sport.MLB, "Synthetic PHI", "Synthetic ATL", "PHI", "ATL", home: 9, away: 8), deadline.Token);
        using var fragments = await reading;

        Assert.Equal("3", fragments.QuerySelector("#detail-away-score")!.TextContent.Trim());
        Assert.Equal("1", fragments.QuerySelector("#detail-home-score")!.TextContent.Trim());
        var probbar = fragments.QuerySelector("#detail-probbar")!;
        Assert.Equal("Live probability unavailable", probbar.TextContent.Trim());
        Assert.DoesNotContain("50%", probbar.OuterHtml);
        var situation = fragments.QuerySelector("#mlb-situation");
        Assert.NotNull(situation);
        Assert.Equal(["Count", "Outs", "Bases", "Batting", "Pitching"], Facts(situation).Keys);
        Assert.NotNull(fragments.QuerySelector("#mlb-line-score"));
        Assert.Equal("Top 5th", Clock(fragments));

        using var page = await PageAsync(Doubleheader);
        foreach (var id in new[] { "mlb-situation", "mlb-line-score", "detail-clock" })
            Assert.Equal(page.QuerySelector("#" + id)!.OuterHtml, fragments.QuerySelector("#" + id)!.OuterHtml);
    }

    [Fact]
    public async Task An_in_progress_game_without_a_stored_scoreboard_says_the_live_scoreboard_is_unavailable()
    {
        var game = MlbGame(Doubleheader);
        game.HomeScore = 4;
        game.AwayScore = 2;
        await host.SeedAsync(game, [Odds(Doubleheader, "2027-07-10T23:20:00Z", 0.45), Odds(Doubleheader, "2027-07-10T23:29:00Z", 0.4)]);
        At("2027-07-10T23:30:00Z");

        using var page = await PageAsync(Doubleheader);

        var situation = page.QuerySelector("#mlb-situation");
        Assert.NotNull(situation);
        Assert.Null(situation.QuerySelector(".situation-facts"));
        Assert.Equal("Live scoreboard unavailable", situation.TextContent.Trim());
        Assert.Equal("Live", page.QuerySelector("#detail-status")!.TextContent.Trim());
        Assert.Null(page.QuerySelector("#mlb-line-score"));
        Assert.Equal("2", page.QuerySelector("#detail-away-score")!.TextContent.Trim());
        Assert.Equal("4", page.QuerySelector("#detail-home-score")!.TextContent.Trim());
        Assert.NotNull(page.QuerySelector("#detail-probbar .detail-probability"));
        Assert.NotNull(page.QuerySelector("nb-win-probability-chart"));
    }

    [Fact]
    public async Task A_basketball_detail_page_and_its_updates_carry_no_baseball_scoreboard()
    {
        const string id = "991300190";
        var game = MlbGame(id);
        game.Sport = "nba";
        game.HomeTeamAbbreviation = "BOS";
        game.AwayTeamAbbreviation = "NYK";
        game.HomeScore = 50;
        game.AwayScore = 48;
        await host.SeedAsync(game, [Odds(id, "2027-07-10T23:20:00Z", 0.45), Odds(id, "2027-07-10T23:29:00Z", 0.4)]);
        At("2027-07-10T23:30:00Z");

        using var page = await PageAsync(id);
        Assert.NotNull(page.QuerySelector("#detail-scoreboard"));
        AssertNoBaseball(page);

        await using var stream = new SsePeer(host.Client, $"/sse/game/{id}");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var reading = stream.ReadForAsync("detail-away-score");
        await broadcaster.ReadyAsync([$"game:{id}"], deadline.Token);
        await broadcaster.PublishAsync(Update(id, Sport.NBA, game.HomeTeam, game.AwayTeam, "BOS", "NYK", home: 52, away: 48), deadline.Token);
        using var fragments = await reading;

        Assert.Equal("52", fragments.QuerySelector("#detail-home-score")!.TextContent.Trim());
        AssertNoBaseball(fragments);
    }

    private const string Pending = "pending";
    private const string NotRecorded = "missing";
    private const string NotPlayed = "not-played";

    private static void AssertNoBaseball(IDocument document)
    {
        Assert.Null(document.QuerySelector("#mlb-situation"));
        Assert.Null(document.QuerySelector("#mlb-line-score"));
        Assert.Null(document.QuerySelector(".scoreboard-side"));
    }

    private static void AssertInDocumentOrder(IDocument page, params IElement[] elements)
    {
        var all = page.All.ToList();
        var positions = elements.Select(e => all.IndexOf(e)).ToArray();
        Assert.Equal(positions.Order(), positions);
    }

    private static IElement Situation(IDocument document)
    {
        var situation = document.QuerySelector("#mlb-situation");
        Assert.NotNull(situation);
        return situation;
    }

    private static string Clock(IDocument document) => document.QuerySelector("#detail-clock")!.TextContent.Trim();

    private static Dictionary<string, IElement> Facts(IElement situation)
    {
        var facts = situation.QuerySelectorAll("dl.situation-facts > div.situation-fact").ToArray();
        Assert.NotEmpty(facts);
        return facts.ToDictionary(f => f.QuerySelector("dt")!.TextContent.Trim(), f => f.QuerySelector("dd")!);
    }

    private static void AssertCount(IElement fact, string visible, string spoken)
    {
        Assert.Equal(visible, fact.QuerySelector("[aria-hidden='true']")!.TextContent.Trim());
        Assert.Equal(spoken, fact.QuerySelector(".visually-hidden")!.TextContent.Trim());
    }

    private static void AssertBases(IElement fact, bool[] occupied, string summary, params string[] runners)
    {
        var diamond = fact.QuerySelector(".bases-diamond");
        Assert.NotNull(diamond);
        Assert.Equal("true", diamond.GetAttribute("aria-hidden"));
        var bases = diamond.QuerySelectorAll(".base").ToArray();
        Assert.Equal(3, bases.Length);
        Assert.Equal(["base-first", "base-second", "base-third"],
            bases.Select(b => b.ClassList.Single(c => c.StartsWith("base-", StringComparison.Ordinal))));
        Assert.Equal(occupied, bases.Select(b => b.ClassList.Contains("is-occupied")));
        var text = fact.TextContent;
        var positions = new[] { summary }.Concat(runners).Select(t => text.IndexOf(t, StringComparison.Ordinal)).ToArray();
        Assert.All(positions, p => Assert.True(p >= 0, $"Bases fact lacks expected text in: {text}"));
        Assert.Equal(positions.Order(), positions);
    }

    private static IElement AssertLineScoreShell(IDocument page, int innings)
    {
        var section = page.QuerySelector("section#mlb-line-score");
        Assert.NotNull(section);
        Assert.Contains("panel", section.ClassList);
        Assert.Equal("mlb-line-score-heading", section.GetAttribute("aria-labelledby"));
        var heading = section.QuerySelector("h2#mlb-line-score-heading")!;
        Assert.Equal("Line Score", heading.TextContent.Trim());
        var scroll = section.QuerySelector("div.line-score-scroll")!;
        Assert.Equal("region", scroll.GetAttribute("role"));
        Assert.Equal("0", scroll.GetAttribute("tabindex"));
        Assert.Equal("mlb-line-score-heading", scroll.GetAttribute("aria-labelledby"));
        var table = scroll.QuerySelector("table.line-score")!;
        Assert.Equal("Runs by inning, then total runs, hits and errors", table.QuerySelector("caption")!.TextContent.Trim());
        var columns = table.QuerySelectorAll("thead th").ToArray();
        Assert.All(columns, c => Assert.Equal("col", c.GetAttribute("scope")));
        Assert.Equal(["Team", .. Enumerable.Range(1, innings).Select(i => i.ToString()), "R", "H", "E"], columns.Select(c => c.TextContent.Trim()));
        Assert.Equal(["Runs", "Hits", "Errors"], columns[^3..].Select(c => c.QuerySelector("abbr")!.GetAttribute("title")));
        Assert.All(columns[^3..], c => Assert.Contains("line-score-total", c.ClassList));
        Assert.Equal(2, table.QuerySelectorAll("tbody tr").Length);
        return table;
    }

    private static void AssertRow(IElement table, int index, string abbreviation, string name, string[] innings, string runs, string hits, string errors)
    {
        var row = table.QuerySelectorAll("tbody tr")[index];
        var header = row.Children[0];
        Assert.Equal("TH", header.TagName);
        Assert.Equal("row", header.GetAttribute("scope"));
        Assert.Equal(abbreviation, header.QuerySelector("abbr")!.TextContent.Trim());
        Assert.Equal(name, header.QuerySelector("abbr")!.GetAttribute("title"));
        var cells = row.Children.Skip(1).ToArray();
        Assert.All(cells, c => Assert.Equal("TD", c.TagName));
        string[] expected = [.. innings, runs, hits, errors];
        Assert.Equal(expected.Length, cells.Length);
        for (var i = 0; i < expected.Length; i++)
            AssertCell(cells[i], expected[i], $"{abbreviation} cell {i + 1}");
    }

    private static void AssertCell(IElement cell, string expected, string where)
    {
        switch (expected)
        {
            case Pending:
                Assert.True(cell.ClassList.Contains("line-score-pending"), where + " should be pending: " + cell.OuterHtml);
                Assert.Equal("Not yet played", cell.QuerySelector(".visually-hidden")?.TextContent.Trim());
                break;
            case NotRecorded:
                Assert.True(cell.ClassList.Contains("line-score-missing"), where + " should be missing: " + cell.OuterHtml);
                Assert.Equal("–", cell.QuerySelector("[aria-hidden='true']")?.TextContent.Trim());
                Assert.Equal("Not recorded", cell.QuerySelector(".visually-hidden")?.TextContent.Trim());
                break;
            case NotPlayed:
                Assert.True(cell.ClassList.Contains("line-score-not-played"), where + " should be not played: " + cell.OuterHtml);
                Assert.Equal("x", cell.QuerySelector("[aria-hidden='true']")?.TextContent.Trim());
                Assert.Equal("Not played", cell.QuerySelector(".visually-hidden")?.TextContent.Trim());
                break;
            default:
                Assert.True(expected == cell.TextContent.Trim(), $"{where}: expected {expected}, got {cell.OuterHtml}");
                break;
        }
    }

    private async Task<IDocument> PageAsync(string gameId)
    {
        using var response = await host.Client.GetAsync($"/game/{gameId}");
        response.EnsureSuccessStatusCode();
        return await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync());
    }

    private GameUpdate Update(string gameId, Sport sport, string homeTeam, string awayTeam, string homeAbbreviation,
        string awayAbbreviation, int home, int away) => new()
    {
        GameId = gameId,
        Sport = sport,
        HomeTeam = homeTeam,
        AwayTeam = awayTeam,
        HomeTeamAbbreviation = homeAbbreviation,
        AwayTeamAbbreviation = awayAbbreviation,
        HomeScore = home,
        AwayScore = away,
        Status = GameStatus.IN_PROGRESS,
        HomeWinProbability = null,
        AwayWinProbability = null,
        Period = 5,
        UpdatedAt = host.Clock.GetUtcNow().UtcDateTime,
    };

    private static JsonObject Load(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "MlbScoreboard", name)))!.AsObject();

    private static DateTimeOffset Instant(string instant) => DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture);

    private void At(string instant) => host.Clock.SetUtcNow(Instant(instant));

    private static Game MlbGame(string id, string status = "in_progress")
    {
        var contest = Espn["contests"]![id] ?? Espn["contests"]![Doubleheader]!;
        return new Game
        {
            Id = id,
            Sport = "mlb",
            HomeTeam = (string)contest["home"]!["displayName"]!,
            AwayTeam = (string)contest["away"]!["displayName"]!,
            HomeTeamAbbreviation = (string)contest["home"]!["abbreviation"]!,
            AwayTeamAbbreviation = (string)contest["away"]!["abbreviation"]!,
            HomeTeamId = (string)contest["home"]!["id"]!,
            AwayTeamId = (string)contest["away"]!["id"]!,
            ScheduledStart = Instant((string)contest["start"]!).UtcDateTime,
            SeasonYear = 2027,
            SeasonType = 2,
            Status = status,
        };
    }

    private static OddsSnapshot Odds(string gameId, string at, double home) => new()
    {
        GameId = gameId,
        Timestamp = Instant(at).UtcDateTime,
        HomeMoneyline = 110,
        AwayMoneyline = -130,
        HomeWinProbability = home,
        AwayWinProbability = 1 - home,
        Source = "synthetic",
    };

    private static string Score(JsonArray competitors, string side) =>
        (string)competitors.Single(c => (string)c!["homeAway"]! == side)!["score"]!;

    private static JsonNode Competition(string gameId, string observation) =>
        Espn["observations"]!.AsArray().Single(o => (string)o!["id"]! == observation)!["boards"]!.AsArray()
            .SelectMany(board => board!["body"]!["events"]!.AsArray())
            .Single(e => (string)e!["id"]! == gameId)!["competitions"]![0]!;

    private static JsonObject Report(string gameId, string observation, DateTimeOffset observedAt) =>
        Describe(new JsonObject { ["gameId"] = gameId, ["observedAt"] = observedAt }, observation);

    private static JsonObject Frame(string frameId, string observation) =>
        Describe(Rich["frames"]!.AsArray().Single(f => (string)f!["id"]! == frameId)!.DeepClone().AsObject(), observation);

    private static JsonObject Describe(JsonObject report, string observation)
    {
        var gameId = (string)report["gameId"]!;
        var contest = Espn["contests"]![gameId]!;
        var competition = Competition(gameId, observation);
        var status = competition["status"]!["type"]!;
        report["competitionId"] = (string)competition["id"]!;
        report["provider"] = "synthetic-espn";
        report["provenance"] = "live";
        report["homeTeamId"] = (string)contest["home"]!["id"]!;
        report["awayTeamId"] = (string)contest["away"]!["id"]!;
        report["state"] = States[(string)status["name"]!];
        report["detail"] = (string)status["detail"]!;
        foreach (var competitor in competition["competitors"]!.AsArray())
            report[(string)competitor!["homeAway"]!] = new JsonObject
            {
                ["runs"] = int.Parse((string)competitor["score"]!),
                ["hits"] = competitor["hits"]?.DeepClone(),
                ["errors"] = competitor["errors"]?.DeepClone(),
                ["innings"] = competitor["linescores"] is JsonArray scores
                    ? new JsonArray([.. scores.Select(s => (JsonNode?)JsonValue.Create((int)(double)s!["value"]!))])
                    : null,
            };
        return report;
    }

    private async Task<FSharpResult<MlbScoreboardFrame, MlbScoreboardRejection>> AcceptAsync(JsonObject report) =>
        await host.Services.GetRequiredService<MlbScoreboardStore>().AcceptAsync(
            JsonSerializer.Deserialize<MlbScoreboardReport>(report.ToJsonString(), JsonSerializerOptions.Web)!, CancellationToken.None);

    private static void Accepted(FSharpResult<MlbScoreboardFrame, MlbScoreboardRejection> result) =>
        Assert.True(result.IsOk, result.IsOk ? "accepted" : result.ErrorValue.ToString());

    private sealed class SsePeer : IAsyncDisposable
    {
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        private readonly Task<HttpResponseMessage> pending;
        private HttpResponseMessage? response;
        private StreamReader? reader;

        public SsePeer(HttpClient client, string path) => pending = client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, deadline.Token);

        public async Task<IDocument> ReadForAsync(string id)
        {
            response ??= await pending;
            response.EnsureSuccessStatusCode();
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
            reader ??= new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
            var elements = new List<string>();
            while (true)
            {
                var line = await reader.ReadLineAsync(deadline.Token) ?? throw new EndOfStreamException("SSE ended before " + id);
                if (line.StartsWith("data: elements ")) elements.Add(line[15..]);
                if (line.Length != 0 || elements.Count == 0) continue;
                var page = await new HtmlParser().ParseDocumentAsync(string.Join("\n", elements), deadline.Token);
                if (page.QuerySelector("#" + id) is not null) return page;
                page.Dispose();
                elements.Clear();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await deadline.CancelAsync();
            try { response ??= await pending; }
            catch (OperationCanceledException) { }
            reader?.Dispose();
            response?.Dispose();
            deadline.Dispose();
        }
    }

    private sealed class ReadyBroadcaster : ISseBroadcaster
    {
        private readonly ISseBroadcaster inner = new SseBroadcaster();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> subscriptions = new();
        public Task PublishAsync(GameUpdate update, CancellationToken ct) => inner.PublishAsync(update, ct);
        public ChannelReader<GameUpdate> Subscribe(string topic)
        {
            var reader = inner.Subscribe(topic);
            subscriptions.GetOrAdd(topic, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            return reader;
        }
        public void Unsubscribe(string topic, ChannelReader<GameUpdate> subscription) => inner.Unsubscribe(topic, subscription);
        public Task ReadyAsync(IEnumerable<string> topics, CancellationToken ct) => Task.WhenAll(topics.Select(topic =>
            subscriptions.GetOrAdd(topic, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task)).WaitAsync(ct);
    }
}
