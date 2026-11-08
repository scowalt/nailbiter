using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using backend.Data;
using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace backend.tests;

public sealed class GroupedHomePageTests : IAsyncLifetime
{
    private readonly IsolatedPostgreSql database = new IsolatedPostgreSql();
    private readonly AppFactory entryPoint = new();
    private readonly Clock clock = new();
    private WebApplicationFactory<Program> app = null!;
    private HttpClient client = null!;

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; } = new(2026, 10, 13, 16, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public async Task InitializeAsync()
    {
        await database.StartAsync();
        app = entryPoint.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDbContextOptionsConfiguration<ExcitementDbContext>>();
            services.RemoveAll<DbContextOptions<ExcitementDbContext>>();
            services.AddDbContext<ExcitementDbContext>(options => options.UseNpgsql(database.GetConnectionString()));
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        }));
        client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Database.EnsureCreatedAsync();
    }

    private Game Game(string id, int hour = 18, string status = "scheduled", string sport = "nba") => new()
    {
        Id = id, Sport = sport, Status = status, SeasonYear = 2027, SeasonType = 2,
        HomeTeam = "Boston", HomeTeamAbbreviation = "BOS", AwayTeam = "New York", AwayTeamAbbreviation = "NY",
        ScheduledStart = clock.Now.UtcDateTime.Date.AddHours(hour), HomeScore = 0, AwayScore = 0
    };

    private async Task Store(params Game[] games)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        db.Games.AddRange(games);
        await db.SaveChangesAsync();
    }

    private async Task<IDocument> Page(string path = "/") => await new HtmlParser().ParseDocumentAsync(await client.GetStringAsync(path));
    private static string[] Headings(IDocument page) => page.QuerySelectorAll("main h2").Select(h => h.TextContent).ToArray();
    private static string[] Cards(IParentNode region) => region.QuerySelectorAll("a[href^='/game/']").Select(a => a.GetAttribute("href")![6..]).ToArray();
    private static IElement Section(IDocument page, string name) => page.QuerySelectorAll("main section").Single(s => s.QuerySelector("h2")?.TextContent == name);

    [Fact]
    public async Task Unavailable_schedule_is_chronological_with_stable_ties_live_first_and_other_statuses_last()
    {
        var oldLive = Game("old-live", status: "in_progress");
        oldLive.ScheduledStart = oldLive.ScheduledStart.AddDays(-1);
        var hidden = Game("preseason"); hidden.SeasonType = 1;
        await Store(Game("late", 22), Game("tie-z"), Game("tie-A"), Game("early", 17),
            Game("final", 19, "final"), Game("live", 21, "in_progress"), Game("postponed", 20, "postponed"),
            oldLive, hidden, Game("canceled", status: "canceled"), Game("world-cup", sport: "world_cup"), Game("before-eastern-day", 3));

        using var page = await Page();
        Assert.Equal(["Live now", "More games", "Other games"], Headings(page));
        Assert.Equal(["old-live", "live"], Cards(Section(page, "Live now")));
        Assert.Equal(["early", "tie-A", "tie-z", "late"], Cards(Section(page, "More games")));
        Assert.Equal(["final", "postponed"], Cards(Section(page, "Other games")));
        Assert.Equal(8, Cards(page).Length);
        Assert.Equal(8, Cards(page).Distinct().Count());
        using var reload = await Page("/?variant=A&sort=hype-asc");
        Assert.Equal(Cards(page), Cards(reload));
        Assert.Equal(Headings(page), Headings(reload));
    }

    private async Task StoreCollege(string id, int hour, string competition, double homeFpi, double awayFpi, double probability)
    {
        var game = Game(id, hour, sport: "cfb");
        game.SeasonYear = 2026; game.HomeTeamAbbreviation = "OSU"; game.AwayTeamAbbreviation = "MICH";
        game.HomeTeamId = "194"; game.AwayTeamId = "130";
        game.HomePollRank = 2; game.AwayPollRank = 7; game.RankPoll = CollegePoll.Ap;
        game.HomeFpi = homeFpi; game.AwayFpi = awayFpi;
        game.RatingsObservedAt = clock.Now.UtcDateTime;
        game.CollegeCompetition = competition;
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        db.Games.Add(game);
        db.PregameOdds.Add(new PregameOdds
        {
            GameId = id, Source = "Kalshi", SourceSeason = 2026, ApplicableStart = game.ScheduledStart,
            HomeTeamAbbreviation = "OSU", AwayTeamAbbreviation = "MICH", FetchedAt = clock.Now.UtcDateTime,
            HomeWinProbability = probability, AwayWinProbability = 1 - probability
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Raw_hype_bands_are_not_rounded_and_more_games_mix_unavailable_and_low_by_start()
    {
        await StoreCollege("max", 23, "cfp", 12000, 12000, .5);
        await StoreCollege("seventy", 20, "cfp", 0, 0, .8125);
        await StoreCollege("almost-seventy", 22, "cfp", 0, 0, .81275);
        await StoreCollege("forty", 18, "bowl", 12000, 12000, .875);
        await StoreCollege("almost-forty", 19, "bowl", 12000, 12000, .87525);
        await StoreCollege("zero", 21, "bowl", -12000, 0, 1);
        await Store(Game("missing-early", 17), Game("missing-tie", 19), Game("missing-late", 23),
            Game("live", 16, "in_progress"), Game("final", 15, "final"));

        using var page = await Page();
        Assert.Equal(["Live now", "Best games", "Good games", "More games", "Other games"], Headings(page));
        Assert.Equal(["live"], Cards(Section(page, "Live now")));
        Assert.Equal(["final"], Cards(Section(page, "Other games")));
        Assert.Equal(11, Cards(page).Distinct().Count());
        Assert.Equal(["seventy", "max"], Cards(Section(page, "Best games")));
        Assert.Equal(["forty", "almost-seventy"], Cards(Section(page, "Good games")));
        Assert.Equal(["missing-early", "almost-forty", "missing-tie", "zero", "missing-late"], Cards(Section(page, "More games")));
        Assert.Equal("Hype70", page.QuerySelector("#excitement-almost-seventy")!.TextContent.Replace(" ", ""));
        Assert.Equal("Hype40", page.QuerySelector("#excitement-almost-forty")!.TextContent.Replace(" ", ""));
        Assert.Equal("Hype0", page.QuerySelector("#excitement-zero")!.TextContent.Replace(" ", ""));
    }

    [Fact]
    public async Task Pregame_cards_show_hype_and_context_without_scorelines_while_live_and_final_keep_results()
    {
        await StoreCollege("college", 19, "cfp", 0, 0, .8125);
        var live = Game("live", status: "in_progress"); live.HomeScore = 81; live.AwayScore = 76;
        var final = Game("final", status: "final"); final.HomeScore = 110; final.AwayScore = 105;
        await Store(Game("missing"), live, final);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            var college = await db.Games.SingleAsync(g => g.Id == "college");
            college.HomeScore = 13; college.AwayScore = 9;
            foreach (var id in new[] { "live", "final" })
                db.OddsSnapshots.AddRange(new[] { .5, .6, .4 }.Select((p, i) => new OddsSnapshot
                {
                    GameId = id, Timestamp = clock.Now.UtcDateTime.AddSeconds(i - 3),
                    HomeWinProbability = p, AwayWinProbability = 1 - p, Period = 3, GameClock = "5:30"
                }));
            await db.SaveChangesAsync();
        }

        using var page = await Page();
        var missing = page.QuerySelector("#excitement-missing")!;
        Assert.Contains("Hype", missing.TextContent);
        Assert.Contains("unavailable", missing.TextContent);
        Assert.Contains("—", missing.TextContent);
        Assert.DoesNotContain("0", missing.TextContent);
        foreach (var id in new[] { "missing", "college" })
        {
            var card = page.QuerySelector($"a[href='/game/{id}']")!;
            Assert.Equal("", card.QuerySelector($"#away-score-{id}")?.TextContent ?? "");
            Assert.Equal("", card.QuerySelector($"#home-score-{id}")?.TextContent ?? "");
            Assert.Empty(card.QuerySelectorAll("a"));
        }
        var collegeCard = page.QuerySelector("a[href='/game/college']")!;
        Assert.Contains("CFB", collegeCard.TextContent);
        Assert.Contains("MICH", collegeCard.TextContent); Assert.Contains("OSU", collegeCard.TextContent);
        Assert.Contains("CFP elimination game", collegeCard.TextContent);
        Assert.Equal(2, collegeCard.QuerySelectorAll("sup[aria-label^='AP rank']").Length);
        Assert.Equal(2, collegeCard.QuerySelectorAll("img[src*='/ncaa/']").Length);
        Assert.Equal("2026-10-13T19:00:00.0000000Z", collegeCard.QuerySelector("time")!.GetAttribute("datetime"));
        Assert.Equal("81", page.QuerySelector("#home-score-live")!.TextContent);
        Assert.Equal("76", page.QuerySelector("#away-score-live")!.TextContent);
        Assert.Equal("110", page.QuerySelector("#home-score-final")!.TextContent);
        Assert.Equal("105", page.QuerySelector("#away-score-final")!.TextContent);
        foreach (var id in new[] { "live", "final" })
        {
            Assert.Contains("Excitement", page.QuerySelector($"#excitement-{id}")!.TextContent);
            Assert.DoesNotContain("Hype", page.QuerySelector($"#excitement-{id}")!.TextContent);
            Assert.NotNull(page.QuerySelector($"nb-sparkline[game-id='{id}']"));
        }
        Assert.Equal("Live", page.QuerySelector("#status-live")!.TextContent);
        Assert.Equal("Final", page.QuerySelector("#status-final")!.TextContent);
        Assert.Contains("Q3 5:30", page.QuerySelector("#clock-live")!.TextContent);
    }

    [Fact]
    public async Task Live_games_keep_the_hype_captured_before_kickoff_and_lead_with_the_most_hyped()
    {
        await StoreCollege("seventy", 20, "cfp", 0, 0, .8125);
        await StoreCollege("forty", 18, "bowl", 12000, 12000, .875);
        await Store(Game("unrated", 17, "in_progress"));
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            await StartHypes.CaptureCollegeAsync(db, scope.ServiceProvider.GetRequiredService<HypeScoreService>(), clock.Now.UtcDateTime, default);
            foreach (var game in await db.Games.Where(g => g.Sport == "cfb").ToListAsync())
                game.Status = "in_progress";
            await db.SaveChangesAsync();
            await StartHypes.CaptureCollegeAsync(db, scope.ServiceProvider.GetRequiredService<HypeScoreService>(), clock.Now.UtcDateTime, default);
        }

        using var page = await Page();
        Assert.Equal(["Live now"], Headings(page));
        Assert.Equal(["seventy", "forty", "unrated"], Cards(Section(page, "Live now")));
        Assert.Equal("Hype70", page.QuerySelector("#hype-seventy")!.TextContent.Replace(" ", ""));
        Assert.Equal("Hype40", page.QuerySelector("#hype-forty")!.TextContent.Replace(" ", ""));
        Assert.Null(page.QuerySelector("#hype-unrated"));
    }

    [Fact]
    public async Task Home_has_concise_accessible_sections_and_preserves_nonpregame_statuses_without_extra_chrome()
    {
        await StoreCollege("best-only", 20, "cfp", 12000, 12000, .5);
        await Store(Game("postponed", 17, "postponed"), Game("suspended", 18, "suspended"));
        using var page = await Page();
        Assert.Equal("Today's Games", Assert.Single(page.QuerySelectorAll("main h1")).TextContent);
        Assert.Equal(["Best games", "Other games"], Headings(page));
        Assert.Equal("Postponed", page.QuerySelector("#status-postponed")!.TextContent);
        Assert.Equal("Suspended", page.QuerySelector("#status-suspended")!.TextContent);
        foreach (var section in page.QuerySelectorAll("main section"))
            Assert.Equal(section.QuerySelector("h2")!.Id, section.GetAttribute("aria-labelledby"));
        var text = page.QuerySelector("main")!.TextContent;
        foreach (var rejected in new[] { "Live scores, win probabilities, and excitement scores", "Start time", "High hype", "70+", "40–69", "snapshot", "Prototype" })
            Assert.DoesNotContain(rejected, text);
        Assert.Empty(page.QuerySelectorAll("main button, main select, main details"));
        Assert.NotNull(page.QuerySelector("#main-content"));
        Assert.NotNull(page.QuerySelector("#live-reconnect-warning"));
        Assert.NotNull(page.QuerySelector("script[src*='live-sync.js']"));
    }

    [Theory]
    [InlineData("live", "Live now")]
    [InlineData("other", "Other games")]
    [InlineData("missing", "More games")]
    [InlineData("best", "Best games")]
    [InlineData("good", "Good games")]
    [InlineData("low", "More games")]
    public async Task Single_section_schedules_omit_every_empty_heading(string scenario, string heading)
    {
        if (scenario == "best") await StoreCollege("only", 20, "cfp", 0, 0, .8125);
        else if (scenario == "good") await StoreCollege("only", 20, "bowl", 12000, 12000, .875);
        else if (scenario == "low") await StoreCollege("only", 20, "bowl", -12000, 0, 1);
        else await Store(Game("only", 20, scenario == "live" ? "in_progress" : scenario == "other" ? "final" : "scheduled"));
        using var page = await Page();
        Assert.Equal([heading], Headings(page));
        Assert.Equal(["only"], Cards(page));
    }

    [Fact]
    public async Task No_eligible_games_retains_the_existing_empty_message_without_sections()
    {
        var hidden = Game("preseason"); hidden.SeasonType = 1;
        await Store(hidden, Game("canceled", status: "canceled"), Game("world", sport: "world_cup"));
        using var page = await Page();
        Assert.Empty(Headings(page)); Assert.Empty(Cards(page));
        Assert.Contains("No games scheduled for today.", page.QuerySelector("main")!.TextContent);
    }

    public async Task DisposeAsync()
    {
        client.Dispose();
        await app.DisposeAsync();
        await entryPoint.DisposeAsync();
        await database.DisposeAsync();
    }
}
