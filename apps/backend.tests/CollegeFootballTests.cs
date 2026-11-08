using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using backend.Data;
using backend.GraphQL.Types;
using backend.Models;
using backend.Services;
using backend.Services.Kalshi;
using Backend.Rendering;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace backend.tests;

public sealed class CollegeFootballTests : IAsyncLifetime
{
    private readonly IsolatedPostgreSql database = new IsolatedPostgreSql();
    private readonly AppFactory entry = new();
    private readonly Clock clock = new();
    private readonly Provider provider = new();
    private WebApplicationFactory<Program> app = null!;
    private HttpClient client = null!;

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 9, 16, 18, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Provider : HttpMessageHandler
    {
        public List<JsonObject> Events = [];
        public List<JsonObject> Markets = [];
        public List<string> Requests = [];
        public bool QuotesUsable;
        public Dictionary<string, JsonObject> QuoteOverrides = [];
        public bool PartialScoreboard;
        public bool MissingHistory;
        public bool WrongSeason;
        public bool WrongCfpSeason;
        public bool CommitteePoll;
        public int CommitteeSeason = 2026;
        public string CommitteeUpdated = "2026-09-14T03:21Z";
        public bool FailQuotes;
        public string? OmitEvent;
        public int RosterSeason = 2026;

        private static object Poll(string id, string type, int season, string updated, params (string Team, int Rank)[] ranks) => new
        {
            id, type, season = new { year = season }, lastUpdated = updated,
            ranks = ranks.Select(r => new { current = r.Rank, team = new { id = r.Team } })
        };

        public static JsonObject Event(string id, string homeId, string home, string awayId, string away, string state = "pre", string date = "2026-09-16T19:00:00Z", string note = "", int seasonType = 2, int period = 0, int week = 3) =>
            JsonSerializer.SerializeToNode(new
            {
                id,
                date,
                season = new { year = 2026, type = seasonType },
                week = new { number = week },
                status = new { period, displayClock = "0:00", type = new { state, name = state == "post" ? "STATUS_FINAL" : "STATUS_SCHEDULED", shortDetail = period == 12 ? "Final/8OT" : "" } },
                competitions = new[] { new {
                    id, date, type = new { id = "42", abbreviation = "Bowl Game" },
                    notes = new[] { new { headline = note } },
                    competitors = new[] {
                        new { homeAway = "home", score = "34", team = new { id = homeId, abbreviation = home, displayName = home, location = home, color = "123456" } },
                        new { homeAway = "away", score = "27", team = new { id = awayId, abbreviation = away, displayName = away, location = away, color = "654321" } }
                    },
                    odds = new[] { new { provider = new { name = "DraftKings" }, moneyline = new { home = new { close = new { odds = "-110" } }, away = new { close = new { odds = "-110" } } } } }
                } }
            })!.AsObject();

        public void List(string date, string home, string away, string status = "active")
        {
            var eventTicker = $"KXNCAAFGAME-{date}{away}{home}";
            foreach (var team in new[] { home, away }) Markets.Add(new JsonObject
            {
                ["ticker"] = $"{eventTicker}-{team}",
                ["event_ticker"] = eventTicker,
                ["status"] = status,
                ["result"] = status == "finalized" ? "yes" : "",
                ["yes_sub_title"] = team
            });
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri.PathAndQuery);
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
            object response;
            if (uri.AbsolutePath.EndsWith("/powerindex")) response = new
            {
                requestedSeason = new { year = WrongSeason ? RosterSeason - 1 : RosterSeason },
                currentSeason = new { year = RosterSeason },
                currentValues = new { season = RosterSeason, conference = "80" },
                pagination = new { page = 1, pages = 1, count = 4 },
                lastUpdated = "2026-09-16T08:00Z",
                categories = new[] { new { name = "fpi", names = new[] { "fpirank", "fpi", "probmakeplayoffs" } } },
                teams = new[] { ("2483", "ORE", 25.0), ("68", "BOIS", 14.0), ("278", "FRES", -2.0), ("242", "RICE", -12.0) }
                    .Select(t => new { team = new { id = t.Item1, abbreviation = t.Item2, displayName = t.Item2 }, categories = new[] { new { name = "fpi", values = new[] { 2.0, t.Item3, 99.0 } } } })
            };
            else if (uri.AbsolutePath.EndsWith("/rankings")) response = new
            {
                rankings = new[] { Poll("1", "ap", 2026, "2026-09-13T21:19Z", ("2483", 5)) }
                    .Concat(CommitteePoll ? [Poll("21", "cfp", CommitteeSeason, CommitteeUpdated, ("2483", 11), ("68", 23))] : [])
            };
            else if (uri.AbsolutePath.EndsWith("/scoreboard")) response = new
            {
                leagues = new[] { new { season = new { year = 2026 }, calendar = new[] { new { value = "2", startDate = "2026-08-22T07:00Z" } } } },
                events = PartialScoreboard ? [] : ForDate(query["dates"].ToString()).Where(e => e["id"]!.ToString() != OmitEvent).ToArray()
            };
            else if (uri.Host == "sports.core.api.espn.com" && uri.AbsolutePath.EndsWith("/events"))
            {
                var events = ForDate(query["dates"].ToString()).Where(e => e["id"]!.ToString() != OmitEvent).ToArray();
                response = new
                {
                    count = events.Length,
                    pageCount = events.Length == 0 ? 0 : 1,
                    pageIndex = events.Length == 0 ? 0 : 1,
                    pageSize = events.Length == 0 ? 25 : 100,
                    items = events.Select(e => new Dictionary<string, string> { ["$ref"] = $"https://sports.core.api.espn.com/v2/sports/football/leagues/college-football/events/{e["id"]}?lang=en" })
                };
            }
            else if (uri.AbsolutePath.EndsWith("/summary"))
            {
                var e = Events.Single(e => e["id"]!.ToString() == query["event"].ToString()).DeepClone().AsObject();
                e["competitions"]![0]!["status"] = e["status"]!.DeepClone(); e.Remove("status"); e.Remove("date");
                e["week"] = e["week"]?["number"]?.DeepClone();
                response = new { header = e };
            }
            else if (uri.AbsolutePath.EndsWith("/historical/cutoff")) response = new { market_settled_ts = "2026-07-18T00:00:00Z" };
            else if (uri.AbsolutePath.EndsWith("/candlesticks"))
            {
                var start = long.Parse(query["start_ts"]!);
                response = new
                {
                    candlesticks = MissingHistory ? [] : new[] {
                    new { end_period_ts = start + 60, price = new { close_dollars = "0.4000" }, volume_fp = "5" },
                    new { end_period_ts = start + 120, price = new { close_dollars = "0.6000" }, volume_fp = "5" },
                    new { end_period_ts = start + 180, price = new { close_dollars = "1.0000" }, volume_fp = "5" }
                }
                };
            }
            else if (uri.AbsolutePath.EndsWith("/plays")) response = new
            {
                pageIndex = 1,
                pageCount = 1,
                items = new[] {
                    new { period = new { number = 1 }, wallclock = "2026-09-05T19:38:47Z" },
                    new { period = new { number = 4 }, wallclock = "2026-09-05T23:07:50Z" }
                }
            };
            else if (uri.AbsolutePath.EndsWith("/odds")) response = new
            {
                items = MissingHistory ? [] : new[] { new {
                provider = new { name = "DraftKings" },
                homeTeamOdds = new { moneyLine = -9999, close = new { moneyLine = new { american = "-2800" }, pointSpread = new { american = "-24.5" } } },
                awayTeamOdds = new { moneyLine = 9999, close = new { moneyLine = new { american = "+1300" } } },
                close = new { total = new { american = "53.5" } }
            } }
            };
            else if (uri.AbsolutePath.Contains("/events/KXNCAAFPLAYOFF-")) response = new
            {
                @event = new
                {
                    event_ticker = "KXNCAAFPLAYOFF-26",
                    series_ticker = "KXNCAAFPLAYOFF",
                    sub_title = WrongCfpSeason ? "2025-26" : "2026-27",
                    markets = new[] { new { ticker = "KXNCAAFPLAYOFF-26-ORE", event_ticker = "KXNCAAFPLAYOFF-26", status = "active", result = "",
                    yes_bid_dollars = "0.62", yes_ask_dollars = "0.64", yes_bid_size_fp = "10", yes_ask_size_fp = "10", close_time = "2026-12-15T00:00:00Z" } }
                }
            };
            else if (uri.AbsolutePath.EndsWith("/historical/markets")) response = new { markets = Array.Empty<object>(), cursor = "" };
            else if (uri.AbsolutePath.EndsWith("/markets")) response = new { markets = Markets, cursor = "" };
            else if (uri.AbsolutePath.Contains("/markets/"))
            {
                if (FailQuotes) throw new HttpRequestException("fixture quote outage");
                var ticker = Uri.UnescapeDataString(uri.Segments.Last());
                if (QuoteOverrides.TryGetValue(ticker, out var quote))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(new JsonObject { ["market"] = quote.DeepClone() }.ToJsonString())
                    });
                response = new
                {
                    market = new
                    {
                        ticker,
                        event_ticker = ticker[..ticker.LastIndexOf('-')],
                        market_type = "binary", notional_value_dollars = "1.0000",
                        status = "active",
                        result = "",
                        close_time = "2026-09-29T00:00:00Z",
                        yes_bid_dollars = QuotesUsable ? "0.40" : "0.00",
                        yes_ask_dollars = QuotesUsable ? "0.42" : "1.00",
                        yes_bid_size_fp = QuotesUsable ? "5" : "0",
                        yes_ask_size_fp = QuotesUsable ? "5" : "0"
                    }
                };
            }
            else throw new InvalidOperationException("Unexpected fixture request: " + uri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response)) });
        }
        private IEnumerable<JsonObject> ForDate(string date) => Events.Where(e => DateTime.Parse(e["date"]!.ToString(), CultureInfo.InvariantCulture).ToString("yyyyMMdd") == date);
    }

    public async Task InitializeAsync()
    {
        await database.StartAsync();
        app = entry.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDbContextOptionsConfiguration<ExcitementDbContext>>();
            services.RemoveAll<DbContextOptions<ExcitementDbContext>>();
            services.AddDbContext<ExcitementDbContext>(o => o.UseNpgsql(database.GetConnectionString()));
            services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock);
            services.AddTransient(s => new CollegeEspnClient(new HttpClient(provider), clock, s.GetRequiredService<IMemoryCache>(), NullLogger<CollegeEspnClient>.Instance));
            services.AddTransient(_ => new CollegeKalshiClient(new HttpClient(provider), NullLogger<CollegeKalshiClient>.Instance));
            services.AddTransient(s => new KalshiPlayoffRatingsService(new HttpClient(provider), NullLogger<KalshiPlayoffRatingsService>.Instance, s, clock));
        }));
        client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Database.EnsureCreatedAsync();
    }

    private async Task<int> Run(bool history = false, bool apply = true)
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CollegeFootballService>().RunAsync(history, apply, CancellationToken.None);
    }
    private async Task<List<Game>> Games()
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Games.AsNoTracking().ToListAsync();
    }
    private async Task<string> Text(string path)
    {
        using var document = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(await client.GetStringAsync(path), CancellationToken.None);
        return document.Body!.TextContent;
    }

    private void SetupPregameKalshi(bool bound = true, bool homeWinner = true)
    {
        var e = Provider.Event("1", "2483", "ORE", "68", "BOIS", note: "SEC Championship");
        var moneyline = e["competitions"]![0]!["odds"]![0]!["moneyline"]!;
        moneyline["home"]!["close"]!["odds"] = "OFF";
        moneyline["away"]!["close"]!["odds"] = "OFF";
        provider.Events.Add(e); provider.List("26SEP16", "ORE", "BSU");
        const string eventTicker = "KXNCAAFGAME-26SEP16BSUORE";
        foreach (var team in new[] { "ORE", "BSU" })
        {
            var winner = (team == "ORE") == homeWinner;
            provider.QuoteOverrides[$"{eventTicker}-{team}"] = new JsonObject
            {
                ["ticker"] = $"{eventTicker}-{team}", ["event_ticker"] = eventTicker,
                ["status"] = "active", ["result"] = "", ["market_type"] = "binary",
                ["notional_value_dollars"] = "1.0000", ["close_time"] = "2026-09-18T19:00:00Z",
                ["yes_bid_dollars"] = bound ? (winner ? "0.99" : "0.00") : (winner ? "0.70" : "0.28"),
                ["yes_ask_dollars"] = bound ? (winner ? "1.00" : "0.01") : (winner ? "0.72" : "0.30"),
                ["yes_bid_size_fp"] = bound && !winner ? "0" : "10",
                ["yes_ask_size_fp"] = bound && winner ? "0" : "10"
            };
        }
    }

    [Fact]
    public async Task Eligibility_is_FBS_by_season_ID_AND_listing_not_price_or_scoreboard_group()
    {
        provider.Events.Add(Provider.Event("1", "2483", "ORE", "999", "FCS"));
        provider.Events.Add(Provider.Event("2", "998", "FCS1", "999", "FCS2"));
        provider.Events.Add(Provider.Event("3", "68", "BOIS", "2483", "ORE"));
        provider.List("26SEP16", "ORE", "FCS"); provider.List("26SEP16", "FCS1", "FCS2");
        var broadcaster = app.Services.GetRequiredService<ISseBroadcaster>();
        var pregameUpdates = broadcaster.Subscribe("game:1");
        Assert.Equal(1, await Run());
        Assert.False(pregameUpdates.TryRead(out _));  
        broadcaster.Unsubscribe("game:1", pregameUpdates);
        var game = Assert.Single(await Games());
        Assert.Equal("2483", game.HomeTeamId); Assert.NotNull(game.HomeMarketTicker);
        Assert.Equal(25, game.HomeFpi); Assert.Null(game.AwayFpi); Assert.Equal(5, game.HomePollRank); Assert.Equal(CollegePoll.Ap, game.RankPoll);
        foreach (var path in new[] { "/", "/cfb?week=3&season=2026&type=2", "/game/1" })
        {
            using var document = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(await client.GetStringAsync(path), CancellationToken.None);
            var text = document.Body!.TextContent;
            var rank = Assert.Single(document.QuerySelectorAll("sup"));
            Assert.Equal("5", rank.TextContent); Assert.Equal("AP rank 5", rank.GetAttribute("aria-label")); Assert.Equal("AP rank 5", rank.GetAttribute("title"));
            Assert.StartsWith("ORE", rank.ParentElement!.TextContent);
            Assert.Contains("Hype unavailable", text); Assert.Contains("CFP", text);
            Assert.DoesNotContain("Regular season", text); Assert.DoesNotContain("Live probability unavailable", text);
            Assert.DoesNotContain("AP #5", text);
            if (path == "/game/1") Assert.Contains("FPI", text);
            else
            {
                Assert.DoesNotContain("FPI", text);  
                Assert.NotNull(document.QuerySelector("#home-score-1"));
                Assert.NotNull(document.QuerySelector("#away-score-1"));
            }
        }
        Assert.Contains("2026 Week 3", await Text("/cfb"));
        Assert.DoesNotContain("Previous Day", await Text("/cfb"));
    }

    private async Task<List<(string Team, string Rank, string Label)>> RankBadges(string path)
    {
        using var document = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(await client.GetStringAsync(path), CancellationToken.None);
        return document.QuerySelectorAll("sup[aria-label]")
            .Select(s => (s.ParentElement!.TextContent.Trim()[..3], s.TextContent, s.GetAttribute("aria-label")!)).OrderBy(b => b.Item1).ToList();
    }

    [Fact]
    public async Task Committee_ranks_replace_AP_ranks_once_published_and_label_the_poll()
    {
        provider.CommitteePoll = true;
        provider.Events.Add(Provider.Event("1", "2483", "ORE", "68", "BOIS")); provider.List("26SEP16", "ORE", "BSU");
        await Run();
        var game = Assert.Single(await Games());
        Assert.Equal(CollegePoll.Cfp, game.RankPoll); Assert.Equal(11, game.HomePollRank); Assert.Equal(23, game.AwayPollRank);
        foreach (var path in new[] { "/", "/cfb?week=3&season=2026&type=2", "/game/1" })
            Assert.Equal([("BOI", "23", "CFP rank 23"), ("ORE", "11", "CFP rank 11")], await RankBadges(path));
    }

    [Theory]
    [InlineData(2025, "2026-09-14T03:21Z")]
    [InlineData(2026, "2026-09-17T03:21Z")]
    public async Task Committee_ranks_for_another_season_or_a_future_date_fall_back_to_AP(int season, string updated)
    {
        provider.CommitteePoll = true; provider.CommitteeSeason = season; provider.CommitteeUpdated = updated;
        provider.Events.Add(Provider.Event("1", "2483", "ORE", "68", "BOIS")); provider.List("26SEP16", "ORE", "BSU");
        await Run();
        var game = Assert.Single(await Games());
        Assert.Equal(CollegePoll.Ap, game.RankPoll); Assert.Equal(5, game.HomePollRank); Assert.Null(game.AwayPollRank);
        foreach (var path in new[] { "/", "/cfb?week=3&season=2026&type=2", "/game/1" })
            Assert.Equal([("ORE", "5", "AP rank 5")], await RankBadges(path));
    }

    [Theory]
    [InlineData("""{"rankings":[{"id":"21","type":"cfp","season":{"year":2026},"lastUpdated":"2026-11-04T03:21Z","ranks":[]},{"id":"1","type":"ap","season":{"year":2026},"lastUpdated":"2026-11-01T22:08Z","ranks":[{"current":4,"team":{"id":"87"}}]}]}""", "Ap")]
    [InlineData("""{"rankings":[{"id":"21","type":"cfp","season":{"year":2026},"date":"2026-11-03T08:00Z","ranks":[{"current":26,"team":{"id":"87"}},{"current":3,"team":{"id":"87"}}]}]}""", "Cfp")]
    [InlineData("""{"rankings":[{"id":"1","type":"usa","season":{"year":2026},"lastUpdated":"2026-11-01T22:08Z","ranks":[{"current":4,"team":{"id":"87"}}]}]}""", null)]
    public void Poll_selection_requires_a_ranked_committee_poll_and_ignores_other_polls(string json, string? expected)
    {
        using var document = JsonDocument.Parse(json);
        var selected = CollegeEspnClient.SelectPoll(document.RootElement, 2026, new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(expected, selected?.Poll.ToString());
        if (expected is not null) Assert.Equal(expected == "Cfp" ? 3 : 4, Assert.Single(selected!.Value.Ranks).Value);
    }

    [Theory]
    [InlineData("pre", false)]
    [InlineData("in", true)]
    [InlineData("post", false)]
    public async Task Missing_live_probability_is_only_a_live_game_warning(string state, bool showWarning)
    {
        provider.Events.Add(Provider.Event("1", "2483", "ORE", "68", "BOIS", state));
        provider.List("26SEP16", "ORE", "BSU");
        await Run();
        foreach (var path in new[] { "/", "/cfb?week=3&season=2026&type=2", "/game/1" })
        {
            var text = await Text(path);
            Assert.Equal(showWarning, text.Contains("Live probability unavailable"));
            Assert.DoesNotContain("Regular season", text);
        }
    }

    [Fact]
    public async Task College_page_groups_the_current_week_across_days_and_links_to_adjacent_weeks()
    {
        foreach (var (id, day, week) in new[] { ("301", 15, 3), ("302", 16, 3), ("303", 19, 3), ("304", 23, 4) })
        {
            provider.Events.Add(Provider.Event(id, "2483", "ORE", "68", "BOIS", date: $"2026-09-{day}T19:00:00Z", week: week));
            provider.List($"26SEP{day}", "ORE", "BSU");
        }
        await Run();

        foreach (var path in new[] { "/cfb", "/cfb?week=3&season=2026&type=2" })
        {
            using var document = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(await client.GetStringAsync(path), CancellationToken.None);
            Assert.Contains("2026 Week 3", document.Body!.TextContent);
            foreach (var id in new[] { "301", "302", "303" }) Assert.NotNull(document.QuerySelector($"a[href='/game/{id}']"));
            Assert.Null(document.QuerySelector("a[href='/game/304']"));
            Assert.NotNull(document.QuerySelector("a[href='/cfb?week=4&season=2026&type=2']"));
            Assert.Contains("Next Week", document.Body.TextContent);
            Assert.DoesNotContain("Next Day", document.Body.TextContent);
        }
        using var next = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(await client.GetStringAsync("/cfb?week=4&season=2026&type=2"), CancellationToken.None);
        Assert.Contains("2026 Week 4", next.Body!.TextContent);
        Assert.NotNull(next.QuerySelector("a[href='/game/304']"));
        Assert.Null(next.QuerySelector("a[href='/game/303']"));
        Assert.NotNull(next.QuerySelector("a[href='/cfb?week=3&season=2026&type=2']"));
        Assert.Contains("Previous Week", next.Body.TextContent);
    }

    [Fact]
    public async Task Negative_college_week_is_rejected_instead_of_showing_the_whole_season()
    {
        var response = await client.GetAsync("/cfb?week=-1&season=2026&type=2");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task College_week_does_not_truncate_games_at_the_pro_league_limit()
    {
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            db.Games.AddRange(Enumerable.Range(1, 101).Select(i => new Game
            {
                Id = $"full-week-{i}", Sport = "cfb", HomeTeam = "Oregon", AwayTeam = "Boise State",
                HomeTeamAbbreviation = "ORE", AwayTeamAbbreviation = "BOIS", HomeTeamId = "2483", AwayTeamId = "68",
                SeasonYear = 2026, SeasonType = 2, WeekNumber = 1, Status = "scheduled",
                ScheduledStart = clock.Now.UtcDateTime.AddMinutes(i)
            }));
            await db.SaveChangesAsync();
        }
        using var document = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(
            await client.GetStringAsync("/cfb?week=1&season=2026&type=2"), CancellationToken.None);
        Assert.Equal(101, document.QuerySelectorAll("a[href^='/game/full-week-']").Length);
    }

    [Theory]
    [InlineData(0, "Week 0")]
    [InlineData(1, "Week 1 (includes Week 0)")]
    public async Task Opening_week_is_a_real_filter_with_an_honest_college_label(int week, string label)
    {
        clock.Now = new DateTimeOffset(2026, 8, 23, 18, 0, 0, TimeSpan.Zero);
        provider.Events.Add(Provider.Event("306", "2483", "ORE", "68", "BOIS", date: "2026-08-23T19:00:00Z", week: week));
        provider.Events.Add(Provider.Event("307", "2483", "ORE", "68", "BOIS", date: "2026-08-29T19:00:00Z", week: 2));
        provider.List("26AUG23", "ORE", "BSU"); provider.List("26AUG29", "ORE", "BSU");
        await Run();

        using var document = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(
            await client.GetStringAsync($"/cfb?week={week}&season=2026&type=2"), CancellationToken.None);
        Assert.Contains($"2026 {label}", document.Body!.TextContent);
        Assert.NotNull(document.QuerySelector("a[href='/game/306']"));
        Assert.Null(document.QuerySelector("a[href='/game/307']"));
        Assert.NotNull(document.QuerySelector("a[href='/cfb?week=2&season=2026&type=2']"));
        using var next = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(
            await client.GetStringAsync("/cfb?week=2&season=2026&type=2"), CancellationToken.None);
        Assert.NotNull(next.QuerySelector($"a[href='/cfb?week={week}&season=2026&type=2']"));
    }

    [Fact]
    public async Task Existing_completed_games_receive_missing_week_metadata_from_summary_headers()
    {
        clock.Now = new DateTimeOffset(2026, 9, 5, 23, 0, 0, TimeSpan.Zero);
        provider.Events.Add(Provider.Event("305", "2483", "ORE", "68", "BOIS", "post", "2026-09-05T19:00:00Z", week: 1));
        provider.List("26SEP05", "ORE", "BSU", "finalized");
        await Run();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            var game = await db.Games.SingleAsync();
            game.WeekNumber = null; game.WeekLabel = null;
            await db.SaveChangesAsync();
        }
        clock.Now = new DateTimeOffset(2026, 9, 16, 18, 0, 0, TimeSpan.Zero);
        await Run();

        using var document = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(
            await client.GetStringAsync("/cfb?week=1&season=2026&type=2"), CancellationToken.None);
        Assert.NotNull(document.QuerySelector("a[href='/game/305']"));
        Assert.Contains("2026 Week 1 (includes Week 0)", document.Body!.TextContent);
    }

    [Fact]
    public async Task Empty_schedule_days_do_not_block_collection_of_listed_games_later_in_the_week()
    {
        clock.Now = new DateTimeOffset(2026, 9, 17, 18, 0, 0, TimeSpan.Zero);
        provider.Events.Add(Provider.Event("308", "2483", "ORE", "68", "BOIS", date: "2026-09-19T19:00:00Z"));
        provider.List("26SEP19", "ORE", "BSU");

        Assert.Equal(1, await Run());
        using var document = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(
            await client.GetStringAsync("/cfb?week=3&season=2026&type=2"), CancellationToken.None);
        Assert.NotNull(document.QuerySelector("a[href='/game/308']"));
        Assert.Contains("2026 Week 3", document.Body!.TextContent);
        Assert.Equal(1, await Run());  
    }

    [Fact]
    public async Task Core_traversal_recovers_a_partial_scoreboard_and_absence_does_not_cancel()
    {
        provider.Events.Add(Provider.Event("1", "278", "FRES", "242", "RICE"));
        provider.List("26SEP16", "FRES", "RICE"); provider.PartialScoreboard = true;
        Assert.Equal(1, await Run());
        Assert.Contains(provider.Requests, r => r.Contains("summary?event=1"));
        provider.OmitEvent = "1"; clock.Now = clock.Now.AddMinutes(20);
        Assert.Equal(1, await Run());
        Assert.Equal("scheduled", Assert.Single(await Games()).Status);
    }

    [Fact]
    public async Task Postponement_preserves_market_identity_and_8OT_scores_update_without_quotes()
    {
        provider.Events.Add(Provider.Event("1", "278", "FRES", "242", "RICE")); provider.List("26SEP16", "FRES", "RICE");
        await Run();
        var ticker = (await Games()).Single().HomeMarketTicker;
        provider.Events[0] = Provider.Event("1", "278", "FRES", "242", "RICE", "in", "2026-09-17T19:00:00Z", period: 12);
        provider.Markets.Clear(); provider.FailQuotes = true;
        clock.Now = clock.Now.AddDays(1);
        await Run();
        var game = Assert.Single(await Games());
        Assert.Equal(ticker, game.HomeMarketTicker); Assert.Equal(34, game.HomeScore); Assert.Equal(12, game.Period);
        var text = await Text("/game/1"); Assert.Contains("8OT", text); Assert.Contains("Live probability unavailable", text);
        var update = new GameUpdate { GameId = game.Id, Sport = Sport.CFB, HomeTeam = "FRES", AwayTeam = "RICE", Status = GameStatus.IN_PROGRESS, Period = 12, GameClock = "0:00", HomeScore = 34, AwayScore = 27 };
        var fragment = SseFragmentRenderer.renderDetailFragments(update);
        Assert.Contains("8OT", fragment); Assert.Contains("probability unavailable", fragment); Assert.DoesNotContain("50%", fragment);
        provider.Events[0] = Provider.Event("1", "242", "RICE", "278", "FRES", "in", "2026-09-17T19:00:00Z", period: 12);
        clock.Now = clock.Now.AddMinutes(1); await Run();
        game = Assert.Single(await Games()); Assert.Equal("278", game.HomeTeamId); Assert.Equal(ticker, game.HomeMarketTicker); Assert.Equal(27, game.HomeScore);
    }

    [Fact]
    public async Task Close_game_between_teams_out_of_CFP_contention_gets_almost_no_hype_from_closeness()
    {
        provider.Events.Add(Provider.Event("1", "2483", "ORE", "68", "BOIS")); provider.List("26SEP16", "ORE", "BSU"); await Run();
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        foreach (var team in new[] { "ORE", "BOIS" }) db.TeamRatings.Add(new TeamRating
        {
            Sport = "cfb",
            TeamAbbreviation = team,
            Source = "kalshi",
            Season = 2026,
            SourceSeason = 2026,
            PlayoffProbability = .005,
            FetchedAt = clock.Now.UtcDateTime
        });
        await db.SaveChangesAsync(); var game = Assert.Single(await Games());
        var hype = scope.ServiceProvider.GetRequiredService<HypeScoreService>();
        var components = await hype.GetHypeComponentsAsync(game, null);
        Assert.Equal(100, components.ClosenessScore);
        var score = (await hype.CalculateHypeScoreAsync(game, null)).GetValueOrDefault(double.NaN);
        Assert.InRange(score - (components.StakesScore * .45 + components.QualityScore * .35), 0, .5);
        var text = await Text("/game/1");
        Assert.Contains("Closeness100", text); Assert.Contains("Hype counts 1% of Closeness", text);
    }

    [Fact]
    public async Task Invalid_roster_season_is_not_evidence_of_current_membership()
    {
        provider.WrongSeason = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => Run());
        Assert.Empty(await Games());
    }

    [Fact]
    public async Task Qualification_uses_compact_estimates_across_pages_and_preserves_source_and_hype()
    {
        provider.Events.Add(Provider.Event("1", "2483", "ORE", "68", "BOIS")); provider.List("26SEP16", "ORE", "BSU");
        await Run();
        using var scope = app.Services.CreateScope();
        var importer = scope.ServiceProvider.GetRequiredService<KalshiPlayoffRatingsService>();
        Assert.Equal(1, await importer.RefreshAsync("cfb", 2026, CancellationToken.None));
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        var rows = await db.TeamRatings.ToListAsync();
        Assert.Equal(.63, rows.Single(r => r.TeamAbbreviation == "ORE").PlayoffProbability);
        var assumed = rows.Single(r => r.TeamAbbreviation == "BOIS");
        Assert.Equal(.005, assumed.PlayoffProbability); Assert.Equal("cfp_no_market", assumed.Source);
        Assert.False(assumed.PlayoffProbabilityIsFinal);
        Assert.Contains(provider.Requests, r => r.Contains("events/KXNCAAFPLAYOFF-26?"));
        foreach (var path in new[] { "/", "/cfb?week=3&season=2026&type=2", "/game/1" })
        {
            var text = await Text(path);
            Assert.Matches(@"(?<!\d)63\s*%", text); Assert.Contains("<1%", text);
            Assert.DoesNotContain("assumed", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("no CFP market", text, StringComparison.OrdinalIgnoreCase);
            if (path != "/game/1") Assert.Contains("CFP <1%", text);
            Assert.DoesNotContain("Hype unavailable", text); Assert.DoesNotMatch(@"(?<!\d)0[.,]5\s*%", text);
            Assert.DoesNotMatch(@"(?<!\d)99\s*%", text); Assert.DoesNotContain("Missed playoffs", text);
        }
        Assert.Contains("Nailbiter estimate", await Text("/game/1"));
        var game = Assert.Single(await Games());
        var hype = scope.ServiceProvider.GetRequiredService<HypeScoreService>();
        Assert.NotNull(await hype.CalculateHypeScoreAsync(game, null));
        var components = hype.CalculateFromData(game, rows.Single(r => r.TeamAbbreviation == "ORE"), assumed,
            await db.PregameOdds.SingleAsync(), null!, null!);
        Assert.True(components.StakesAvailable); Assert.Equal(.005, components.AwayPlayoffOdds);
        provider.WrongCfpSeason = true;
        Assert.Equal(0, await importer.RefreshAsync("cfb", 2026, CancellationToken.None));
        clock.Now = clock.Now.AddHours(1);
        var expired = await Text("/game/1"); Assert.Contains("Unavailable", expired); Assert.DoesNotMatch(@"(?<!\d)63\s*%", expired);
        Assert.DoesNotContain("<1%", expired);
    }

    [Theory]
    [InlineData("College Football Playoff Quarterfinal at the Rose Bowl", 3, "cfp", 100)]
    [InlineData("SEC Championship", 2, "conference_championship", 80)]
    [InlineData("Union Home Mortgage Gasparilla Bowl", 3, "bowl", 0)]
    [InlineData("Rose Bowl", 3, "unknown", -1)]
    public async Task Competition_context_can_establish_stakes_without_unrelated_CFP_odds(string note, int seasonType, string category, double stakes)
    {
        provider.Events.Add(Provider.Event("1", "2483", "ORE", "68", "BOIS", note: note, seasonType: seasonType)); provider.List("26SEP16", "ORE", "BSU");
        await Run();
        var game = Assert.Single(await Games()); Assert.Equal(category, game.CollegeCompetition);
        using var scope = app.Services.CreateScope();
        var hype = scope.ServiceProvider.GetRequiredService<HypeScoreService>();
        var components = await hype.GetHypeComponentsAsync(game, null);
        Assert.True(components.QualityAvailable); Assert.True(components.ClosenessAvailable);
        Assert.Equal(stakes >= 0, components.StakesAvailable);
        Assert.Equal(stakes >= 0, (await hype.CalculateHypeScoreAsync(game, null)).HasValue);
        if (stakes >= 0) Assert.Equal(stakes, components.StakesScore);
    }

    [Fact]
    public async Task Initial_history_is_fixed_bounded_idempotent_and_never_scored()
    {
        provider.Events.Add(Provider.Event("401858433", "2483", "ORE", "68", "BOIS", "post", "2026-09-05T19:30:00Z"));
        provider.Events.Add(Provider.Event("old", "2483", "ORE", "68", "BOIS", "post", "2026-09-01T19:30:00Z"));
        provider.List("26SEP05", "ORE", "BSU", "finalized"); provider.List("26SEP01", "ORE", "BSU", "finalized");
        Assert.Equal(1, await Run(history: true, apply: false)); Assert.Empty(await Games());
        Assert.Equal(1, await Run(history: true));
        clock.Now = clock.Now.AddDays(1);
        Assert.Equal(1, await Run(history: true));
        var game = Assert.Single(await Games()); Assert.True(game.IsHistoricalImport); Assert.Null(game.HomeFpi); Assert.Null(game.HomePollRank); Assert.Null(game.RankPoll);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        var samples = await db.OddsSnapshots.ToListAsync(); Assert.Equal(2, samples.Count); Assert.All(samples, s => Assert.True(s.IsHistorical));
        var closing = await db.PregameOdds.SingleAsync(); Assert.Equal(-2800, closing.HomeMoneyline); Assert.Equal(-24.5, closing.Spread); Assert.Equal(53.5, closing.OverUnder);
        var checkpoint = await db.CollegeImports.SingleAsync(); Assert.Equal(new DateTime(2026, 9, 2, 18, 0, 0, DateTimeKind.Utc), checkpoint.WindowStart);
        var scores = await scope.ServiceProvider.GetRequiredService<GameDataService>().GetExcitementScoresForGamesAsync(Microsoft.FSharp.Collections.ListModule.OfSeq(new[] { game.Id }), CancellationToken.None);
        Assert.Null(scores[game.Id]);
        var text = await Text("/game/401858433"); Assert.Contains("Historical market chart", text); Assert.Contains("Historical closing odds", text); Assert.Contains("excitement unavailable", text);
        Assert.DoesNotContain("AP #", text); Assert.DoesNotContain("FPI +", text);
    }

    [Fact]
    public async Task Missing_closing_lines_and_candles_do_not_hide_historical_results()
    {
        provider.Events.Add(Provider.Event("1", "2483", "ORE", "68", "BOIS", "post", "2026-09-05T19:30:00Z")); provider.List("26SEP05", "ORE", "BSU", "finalized");
        provider.MissingHistory = true;
        await Run(history: true);
        var game = Assert.Single(await Games()); Assert.Equal(34, game.HomeScore);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        Assert.Empty(await db.OddsSnapshots.ToListAsync()); Assert.Empty(await db.PregameOdds.ToListAsync());
    }

    [Fact]
    public async Task Live_samples_produce_excitement_but_a_later_quote_failure_clears_current_probability()
    {
        provider.Events.Add(Provider.Event("1", "2483", "ORE", "68", "BOIS", "in")); provider.List("26SEP16", "ORE", "BSU"); provider.QuotesUsable = true;
        await Run(); clock.Now = clock.Now.AddSeconds(30); await Run();
        using var scope = app.Services.CreateScope(); var svc = scope.ServiceProvider.GetRequiredService<GameDataService>();
        var ids = Microsoft.FSharp.Collections.ListModule.OfSeq(new[] { "1" });
        Assert.Equal(0, (await svc.GetExcitementScoresForGamesAsync(ids, CancellationToken.None))["1"]);
        Assert.Single(await svc.GetLatestOddsForGamesAsync(ids, CancellationToken.None));
        provider.FailQuotes = true; clock.Now = clock.Now.AddSeconds(30); await Run();
        Assert.Empty(await svc.GetLatestOddsForGamesAsync(ids, CancellationToken.None));
        Assert.Equal(34, Assert.Single(await Games()).HomeScore);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Near_certain_bounds_flow_through_collection_API_SSR_SSE_and_scoring(bool resolved)
    {
        provider.Events.Add(Provider.Event("1", "2483", "ORE", "68", "BOIS", "in"));
        provider.List("26SEP16", "ORE", "BSU"); provider.QuotesUsable = true;
        await Run();
        const string eventTicker = "KXNCAAFGAME-26SEP16BSUORE";
        foreach (var team in new[] { "ORE", "BSU" })
        {
            var winner = team == "ORE";
            provider.QuoteOverrides[$"{eventTicker}-{team}"] = new JsonObject
            {
                ["ticker"] = $"{eventTicker}-{team}", ["event_ticker"] = eventTicker,
                ["status"] = resolved ? "determined" : "active", ["result"] = resolved ? (winner ? "yes" : "no") : "",
                ["market_type"] = "binary", ["notional_value_dollars"] = "1.0000",
                ["settlement_value_dollars"] = resolved ? (winner ? "1.0000" : "0.0000") : null,
                ["close_time"] = resolved ? "2026-09-16T17:59:00Z" : "2026-09-29T00:00:00Z",
                ["yes_bid_dollars"] = winner ? "0.99" : "0.00", ["yes_ask_dollars"] = winner ? "1.00" : "0.01",
                ["yes_bid_size_fp"] = winner ? "10" : "0", ["yes_ask_size_fp"] = winner ? "0" : "10"
            };
        }
        clock.Now = clock.Now.AddSeconds(30); await Run();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        var samples = await db.OddsSnapshots.OrderBy(o => o.Timestamp).ToListAsync();
        Assert.Equal(2, samples.Count);
        Assert.Equal(.995, samples[1].HomeWinProbability);
        Assert.Equal(resolved ? GameWinnerProbabilityPolicy.ResolvedSource : GameWinnerProbabilityPolicy.OneSidedSource, samples[1].Source);
        var game = Assert.Single(await Games()); Assert.Equal("in_progress", game.Status);
        foreach (var path in new[] { "/", "/cfb", "/game/1" })
        {
            var text = await Text(path);
            Assert.True(text.Contains("ORE >99%"), $"{path}: {text}"); Assert.Contains("BOIS <1%", text);
            Assert.DoesNotContain("Live probability unavailable", text);
        }
        using var api = JsonDocument.Parse(await client.GetStringAsync("/api/games/1/odds"));
        Assert.True(api.RootElement.GetProperty("oddsHistory")[1].GetProperty("isProbabilityBound").GetBoolean());
        using var listing = JsonDocument.Parse(await client.GetStringAsync("/api/games?sport=cfb"));
        Assert.True(listing.RootElement[0].GetProperty("latestOdds").GetProperty("isProbabilityBound").GetBoolean());
        var html = await client.GetStringAsync("/game/1"); Assert.Contains("isProbabilityBound", html);
        var fragment = SseFragmentRenderer.renderDetailFragments(new GameUpdate
        {
            GameId = "1", Sport = Sport.CFB, HomeTeam = "ORE", AwayTeam = "BOIS",
            HomeTeamAbbreviation = "ORE", AwayTeamAbbreviation = "BOIS",
            Status = GameStatus.IN_PROGRESS, HomeWinProbability = .995, AwayWinProbability = .005
        });
        Assert.Contains("ORE &gt;99%", fragment); Assert.Contains("BOIS &lt;1%", fragment);
        var svc = scope.ServiceProvider.GetRequiredService<GameDataService>();
        var ids = Microsoft.FSharp.Collections.ListModule.OfSeq(new[] { "1" });
        Assert.True((await svc.GetExcitementScoresForGamesAsync(ids, CancellationToken.None))["1"] > 0);
        provider.FailQuotes = true; clock.Now = clock.Now.AddSeconds(30); await Run();
        Assert.Empty(await svc.GetLatestOddsForGamesAsync(ids, CancellationToken.None));
    }

    [Fact]
    public async Task Initial_window_is_clamped_to_season_start_not_a_calendar_year_or_full_season()
    {
        clock.Now = new DateTimeOffset(2026, 8, 25, 18, 0, 0, TimeSpan.Zero);
        provider.Events.Add(Provider.Event("111", "2483", "ORE", "68", "BOIS", "post", "2026-08-23T19:30:00Z"));
        provider.Events.Add(Provider.Event("110", "2483", "ORE", "68", "BOIS", "post", "2026-08-21T19:30:00Z"));
        provider.List("26AUG23", "ORE", "BSU", "finalized"); provider.List("26AUG21", "ORE", "BSU", "finalized");
        provider.MissingHistory = true;
        Assert.Equal(1, await Run(history: true));
        Assert.Equal("111", Assert.Single(await Games()).Id);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        Assert.Equal(new DateTime(2026, 8, 22, 7, 0, 0, DateTimeKind.Utc), (await db.CollegeImports.SingleAsync()).WindowStart);
        Assert.DoesNotContain(provider.Requests, r => r.Contains("dates=20260821"));
    }

    [Fact]
    public async Task January_playoff_game_retains_the_prior_start_year_and_postseason_group_without_bracket()
    {
        clock.Now = new DateTimeOffset(2027, 1, 1, 18, 0, 0, TimeSpan.Zero);
        provider.Events.Add(Provider.Event("112", "2483", "ORE", "68", "BOIS", "pre", "2027-01-01T19:30:00Z", "College Football Playoff Quarterfinal at the Rose Bowl", 3, week: 1));
        provider.List("27JAN01", "ORE", "BSU");
        await Run(); var game = Assert.Single(await Games());
        Assert.Equal(2026, game.SeasonYear); Assert.Equal("cfp", game.CollegeCompetition);
        foreach (var path in new[] { "/cfb", "/cfb?week=1&season=2026&type=3" })
        {
            var text = await Text(path);
            Assert.Contains("2026 Postseason (Bowls & CFP)", text); Assert.Contains("CFP elimination", text);
            Assert.DoesNotContain("2026 Week 1", text); Assert.DoesNotContain("Previous Day", text);
            Assert.DoesNotContain("seed", text, StringComparison.OrdinalIgnoreCase);
        }
        using var regular = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(
            await client.GetStringAsync("/cfb?week=1&season=2026&type=2"), CancellationToken.None);
        Assert.Null(regular.QuerySelector("a[href='/game/112']"));
        using var wrongSeason = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(
            await client.GetStringAsync("/cfb?week=1&season=2027&type=3"), CancellationToken.None);
        Assert.Null(wrongSeason.QuerySelector("a[href='/game/112']"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Depth_backed_pregame_bound_supplies_closeness_when_moneylines_are_off(bool homeWinner)
    {
        SetupPregameKalshi(homeWinner: homeWinner);
        await Run();
        var game = Assert.Single(await Games());
        using var scope = app.Services.CreateScope();
        var hype = scope.ServiceProvider.GetRequiredService<HypeScoreService>();
        var components = await hype.GetHypeComponentsAsync(game, null);
        Assert.True(components.ClosenessAvailable);
        Assert.Equal(1, components.ClosenessScore, 8);
        Assert.True(components.QualityAvailable); Assert.True(components.StakesAvailable);
        Assert.NotNull(await hype.CalculateHypeScoreAsync(game, null));
        var odds = await scope.ServiceProvider.GetRequiredService<GameDataService>().GetPregameOddsAsync("1", CancellationToken.None);
        Assert.NotNull(odds);
        Assert.Equal(GameWinnerProbabilityPolicy.OneSidedSource, odds.Value.Source);
        Assert.Null(odds.Value.HomeMoneyline); Assert.Null(odds.Value.AwayMoneyline);
        var text = await Text("/game/1");
        Assert.Contains(homeWinner ? "ORE >99%" : "ORE <1%", text);
        Assert.Contains(homeWinner ? "BOIS <1%" : "BOIS >99%", text);
        Assert.Contains(GameWinnerProbabilityPolicy.OneSidedSource, text);
        Assert.Contains("Closeness uses a near-certain market bound", text);
        Assert.DoesNotContain("Moneyline", text);
        Assert.DoesNotMatch(@"(?<!\d)99[.,]5\s*%", text);
        Assert.DoesNotMatch(@"(?<!\d)0[.,]5\s*%", text);
    }

    [Fact]
    public async Task Pregame_Kalshi_fallback_expires_after_two_minutes_without_a_refresh()
    {
        var e = Provider.Event("1", "2483", "ORE", "68", "BOIS", note: "SEC Championship");
        e["competitions"]![0]!["odds"] = new JsonArray();
        provider.Events.Add(e); provider.List("26SEP16", "ORE", "BSU"); provider.QuotesUsable = true;
        await Run();
        var game = Assert.Single(await Games());
        using var scope = app.Services.CreateScope();
        var hype = scope.ServiceProvider.GetRequiredService<HypeScoreService>();
        clock.Now = clock.Now.AddSeconds(119);
        Assert.True((await hype.GetHypeComponentsAsync(game, null)).ClosenessAvailable);
        clock.Now = clock.Now.AddSeconds(1);
        var components = await hype.GetHypeComponentsAsync(game, null);
        Assert.False(components.ClosenessAvailable);
        Assert.True(components.QualityAvailable); Assert.True(components.StakesAvailable);
        Assert.Null(await hype.CalculateHypeScoreAsync(game, null));
    }

    [Fact]
    public async Task Pregame_Kalshi_cannot_relabel_an_old_market_for_a_changed_game_date()
    {
        SetupPregameKalshi();
        await Run();
        provider.Events[0]["date"] = "2026-09-19T19:00:00Z";
        provider.Events[0]["competitions"]![0]!["date"] = "2026-09-19T19:00:00Z";
        clock.Now = clock.Now.AddSeconds(30);
        await Run();
        var game = Assert.Single(await Games());
        using var scope = app.Services.CreateScope();
        var hype = scope.ServiceProvider.GetRequiredService<HypeScoreService>();
        Assert.False((await hype.GetHypeComponentsAsync(game, null)).ClosenessAvailable);
        var text = await Text("/game/1");
        Assert.Contains("Hype unavailable", text);
        Assert.DoesNotContain("ORE >99%", text);
    }

    [Fact]
    public async Task Two_sided_pregame_Kalshi_quotes_supply_probabilities_without_fabricated_moneylines()
    {
        SetupPregameKalshi(bound: false);
        await Run();
        var game = Assert.Single(await Games());
        using var scope = app.Services.CreateScope();
        var hype = scope.ServiceProvider.GetRequiredService<HypeScoreService>();
        var components = await hype.GetHypeComponentsAsync(game, null);
        Assert.True(components.ClosenessAvailable); Assert.Equal(58, components.ClosenessScore, 8);
        Assert.NotNull(await hype.CalculateHypeScoreAsync(game, null));
        var data = scope.ServiceProvider.GetRequiredService<GameDataService>();
        var odds = (await data.GetPregameOddsAsync("1", CancellationToken.None)).Value;
        Assert.Equal("Kalshi", odds.Source); Assert.Equal(.71, odds.HomeWinProbability, 8);
        Assert.Null(odds.HomeMoneyline); Assert.Null(odds.AwayMoneyline);
        Assert.Empty(await data.GetOddsSnapshotsAsync("1", CancellationToken.None));
        var text = await Text("/game/1");
        Assert.Contains("ORE 71%", text); Assert.Contains("BOIS 29%", text); Assert.Contains("Kalshi", text);
        Assert.DoesNotContain("Moneyline", text); Assert.DoesNotContain("near-certain", text);
    }

    [Fact]
    public async Task ESPN_moneylines_take_precedence_and_keep_their_24_hour_expiry()
    {
        SetupPregameKalshi();
        var moneyline = provider.Events[0]["competitions"]![0]!["odds"]![0]!["moneyline"]!;
        moneyline["home"]!["close"]!["odds"] = "-110";
        moneyline["away"]!["close"]!["odds"] = "-110";
        await Run();
        var game = Assert.Single(await Games());
        using var scope = app.Services.CreateScope();
        var hype = scope.ServiceProvider.GetRequiredService<HypeScoreService>();
        var odds = (await scope.ServiceProvider.GetRequiredService<GameDataService>().GetPregameOddsAsync("1", CancellationToken.None)).Value;
        Assert.Equal("ESPN DraftKings", odds.Source); Assert.Equal(-110, odds.HomeMoneyline);
        clock.Now = clock.Now.AddHours(24).AddSeconds(-1);
        var components = await hype.GetHypeComponentsAsync(game, null);
        Assert.True(components.ClosenessAvailable); Assert.Equal(100, components.ClosenessScore);
        clock.Now = clock.Now.AddSeconds(1);
        Assert.False((await hype.GetHypeComponentsAsync(game, null)).ClosenessAvailable);
    }

    [Theory]
    [InlineData("empty winner depth")]
    [InlineData("empty loser depth")]
    [InlineData("not extreme")]
    [InlineData("crossed")]
    [InlineData("expired")]
    [InlineData("resolved")]
    [InlineData("fair price")]
    [InlineData("wrong event")]
    [InlineData("wrong ticker")]
    [InlineData("nonbinary")]
    [InlineData("stale last trade")]
    public async Task Unsupported_pregame_Kalshi_quotes_do_not_invent_closeness(string condition)
    {
        SetupPregameKalshi();
        var home = provider.QuoteOverrides["KXNCAAFGAME-26SEP16BSUORE-ORE"];
        var away = provider.QuoteOverrides["KXNCAAFGAME-26SEP16BSUORE-BSU"];
        switch (condition)
        {
            case "empty winner depth": home["yes_bid_size_fp"] = "0"; break;
            case "empty loser depth": away["yes_ask_size_fp"] = "0"; break;
            case "not extreme": home["yes_bid_dollars"] = "0.98"; break;
            case "crossed": home["yes_ask_dollars"] = "0.98"; home["yes_ask_size_fp"] = "10"; break;
            case "expired": home["close_time"] = clock.Now.ToString("O"); break;
            case "resolved":
            case "fair price":
                home["status"] = "determined"; away["status"] = "determined";
                home["result"] = "yes"; away["result"] = "no";
                home["settlement_value_dollars"] = condition == "resolved" ? "1.0000" : "0.5000";
                away["settlement_value_dollars"] = condition == "resolved" ? "0.0000" : "0.5000";
                break;
            case "wrong event": away["event_ticker"] = "KXNCAAFGAME-26SEP16OTHER"; break;
            case "wrong ticker": away["ticker"] = "KXNCAAFGAME-26SEP16OTHER-BSU"; break;
            case "nonbinary": home["market_type"] = "scalar"; break;
            case "stale last trade":
                home["yes_bid_size_fp"] = "0"; away["yes_ask_size_fp"] = "0";
                home["last_price_dollars"] = "0.99"; away["last_price_dollars"] = "0.01";
                break;
        }
        await Run();
        var game = Assert.Single(await Games());
        using var scope = app.Services.CreateScope();
        var hype = scope.ServiceProvider.GetRequiredService<HypeScoreService>();
        var components = await hype.GetHypeComponentsAsync(game, null);
        Assert.False(components.ClosenessAvailable);
        Assert.True(components.QualityAvailable); Assert.True(components.StakesAvailable);
        Assert.Null(await hype.CalculateHypeScoreAsync(game, null));
        var text = await Text("/game/1");
        Assert.Contains("Hype unavailable", text); Assert.Contains("Stakes80", text);
        Assert.DoesNotContain("Win Probability", text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_pregame_Kalshi_refresh_does_not_extend_quotes_or_show_expired_probabilities(bool bound)
    {
        SetupPregameKalshi(bound);
        await Run();
        provider.FailQuotes = true;
        clock.Now = clock.Now.AddSeconds(119);
        await Run();
        var game = Assert.Single(await Games());
        using var scope = app.Services.CreateScope();
        var hype = scope.ServiceProvider.GetRequiredService<HypeScoreService>();
        Assert.True((await hype.GetHypeComponentsAsync(game, null)).ClosenessAvailable);
        Assert.DoesNotContain("Hype unavailable", await Text("/game/1"));
        clock.Now = clock.Now.AddSeconds(1);
        await Run();
        var components = await hype.GetHypeComponentsAsync(game, null);
        Assert.False(components.ClosenessAvailable); Assert.True(components.QualityAvailable); Assert.True(components.StakesAvailable);
        Assert.Null(await hype.CalculateHypeScoreAsync(game, null));
        foreach (var path in new[] { "/", "/cfb", "/game/1" }) Assert.Contains("Hype unavailable", await Text(path));
        var text = await Text("/game/1");
        Assert.Contains("ClosenessUnavailable", text); Assert.DoesNotContain("Win Probability", text);
        Assert.DoesNotContain("near-certain", text); Assert.DoesNotContain("Moneyline", text);
        provider.FailQuotes = false;
        clock.Now = clock.Now.AddSeconds(30); await Run();
        Assert.DoesNotContain("Hype unavailable", await Text("/game/1"));
    }

    [Theory]
    [InlineData("wrong game")]
    [InlineData("wrong season")]
    [InlineData("wrong team")]
    [InlineData("changed kickoff")]
    [InlineData("future observation")]
    [InlineData("incomplete probability pair")]
    [InlineData("nonfinite probability")]
    [InlineData("already started")]
    public async Task Inapplicable_pregame_Kalshi_evidence_cannot_supply_hype(string condition)
    {
        SetupPregameKalshi();
        await Run();
        var game = Assert.Single(await Games());
        using var scope = app.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<GameDataService>();
        var odds = (await data.GetPregameOddsAsync("1", CancellationToken.None)).Value;
        switch (condition)
        {
            case "wrong game": odds.GameId = "other"; break;
            case "wrong season": odds.SourceSeason = 2025; break;
            case "wrong team": odds.HomeTeamAbbreviation = "OTHER"; break;
            case "changed kickoff": game.ScheduledStart = game.ScheduledStart.AddHours(1); break;
            case "future observation": odds.FetchedAt = clock.Now.AddSeconds(1).UtcDateTime; break;
            case "incomplete probability pair": odds.AwayWinProbability = .1; break;
            case "nonfinite probability": odds.HomeWinProbability = double.NaN; break;
            case "already started": game.ActualStart = clock.Now.UtcDateTime; break;
        }
        var hype = scope.ServiceProvider.GetRequiredService<HypeScoreService>();
        var components = hype.CalculateFromData(game, null!, null!, odds, null!, null!);
        Assert.False(components.ClosenessAvailable);
        Assert.True(double.IsNaN(hype.CalculateFinalScore("cfb", components)));
    }

    [Fact]
    public async Task College_pregame_probability_migration_is_repeatable_and_preserves_existing_moneylines()
    {
        SetupPregameKalshi();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE pregame_odds ALTER COLUMN home_moneyline SET NOT NULL; ALTER TABLE pregame_odds ALTER COLUMN away_moneyline SET NOT NULL");
        await Assert.ThrowsAsync<DbUpdateException>(() => Run());
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "032_CollegePregameProbabilities.sql"));
        await db.Database.ExecuteSqlRawAsync(sql); await db.Database.ExecuteSqlRawAsync(sql);
        await Run();
        var data = scope.ServiceProvider.GetRequiredService<GameDataService>();
        var odds = (await data.GetPregameOddsAsync("1", CancellationToken.None)).Value;
        Assert.Null(odds.HomeMoneyline); Assert.Null(odds.AwayMoneyline);
        var moneyline = provider.Events[0]["competitions"]![0]!["odds"]![0]!["moneyline"]!;
        moneyline["home"]!["close"]!["odds"] = "-110";
        moneyline["away"]!["close"]!["odds"] = "-110";
        await Run();
        db.ChangeTracker.Clear();
        await db.Database.ExecuteSqlRawAsync(sql);
        odds = (await data.GetPregameOddsAsync("1", CancellationToken.None)).Value;
        Assert.Equal(-110, odds.HomeMoneyline); Assert.Equal(-110, odds.AwayMoneyline);
        Assert.Equal("ESPN DraftKings", odds.Source);
    }

    [Theory]
    [InlineData("market_type", "scalar")]
    [InlineData("notional_value_dollars", "10.0000")]
    [InlineData("event_ticker", "KXNCAAFGAME-26SEP16OTHER")]
    public async Task Two_sided_pregame_prices_still_require_matching_binary_game_winner_contracts(string field, string value)
    {
        SetupPregameKalshi(bound: false);
        provider.QuoteOverrides["KXNCAAFGAME-26SEP16BSUORE-BSU"][field] = value;
        await Run();
        var game = Assert.Single(await Games());
        using var scope = app.Services.CreateScope();
        var hype = scope.ServiceProvider.GetRequiredService<HypeScoreService>();
        Assert.False((await hype.GetHypeComponentsAsync(game, null)).ClosenessAvailable);
        Assert.Contains("Hype unavailable", await Text("/game/1"));
    }

    [Fact]
    public async Task Missing_closeness_keeps_valid_quality_and_contextual_stakes_visible()
    {
        var e = Provider.Event("1", "2483", "ORE", "68", "BOIS", note: "SEC Championship");
        e["competitions"]![0]!["odds"] = new JsonArray(); provider.Events.Add(e); provider.List("26SEP16", "ORE", "BSU");
        await Run(); var game = Assert.Single(await Games());
        using var scope = app.Services.CreateScope(); var hype = scope.ServiceProvider.GetRequiredService<HypeScoreService>();
        var components = await hype.GetHypeComponentsAsync(game, null);
        Assert.True(components.QualityAvailable); Assert.True(components.StakesAvailable); Assert.False(components.ClosenessAvailable);
        Assert.Null(await hype.CalculateHypeScoreAsync(game, null));
        var text = await Text("/game/1"); Assert.Contains("Hype unavailable", text); Assert.Contains("Stakes80", text); Assert.Contains("ClosenessUnavailable", text);
    }

    [Fact]
    public async Task Complete_regular_season_evidence_produces_hype_without_a_poll_rank_for_both_teams()
    {
        provider.Events.Add(Provider.Event("1", "2483", "ORE", "68", "BOIS")); provider.List("26SEP16", "ORE", "BSU"); await Run();
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        foreach (var team in new[] { "ORE", "BOIS" }) db.TeamRatings.Add(new TeamRating
        {
            Sport = "cfb",
            TeamAbbreviation = team,
            Source = "kalshi",
            Season = 2026,
            SourceSeason = 2026,
            PlayoffProbability = .6,
            FetchedAt = clock.Now.UtcDateTime
        });
        await db.SaveChangesAsync(); var game = Assert.Single(await Games()); Assert.Null(game.AwayPollRank); Assert.NotNull(game.AwayFpi);
        Assert.NotNull(await scope.ServiceProvider.GetRequiredService<HypeScoreService>().CalculateHypeScoreAsync(game, null));
    }

    [Fact]
    public async Task Migration_can_be_applied_twice()
    {
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE pregame_odds ALTER COLUMN source TYPE varchar(50)");
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "027_CollegeFootball.sql"));
        await db.Database.ExecuteSqlRawAsync(sql); await db.Database.ExecuteSqlRawAsync(sql);
        var dataType = await db.Database.SqlQueryRaw<string>("SELECT data_type AS \"Value\" FROM information_schema.columns WHERE table_name = 'pregame_odds' AND column_name = 'source'").SingleAsync();
        Assert.Equal("text", dataType);
    }

    [Fact]
    public async Task Poll_migration_keeps_legacy_AP_ranks_labeled_AP_leaves_the_old_columns_empty_and_can_be_applied_twice()
    {
        provider.Events.Add(Provider.Event("1", "2483", "ORE", "68", "BOIS")); provider.List("26SEP16", "ORE", "BSU");
        await Run();
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE college_teams RENAME COLUMN poll_rank TO ap_rank;
            ALTER TABLE college_teams RENAME COLUMN poll_updated_at TO ap_updated_at;
            ALTER TABLE college_teams DROP COLUMN poll;
            ALTER TABLE games RENAME COLUMN home_poll_rank TO home_ap_rank;
            ALTER TABLE games RENAME COLUMN away_poll_rank TO away_ap_rank;
            ALTER TABLE games DROP COLUMN rank_poll;
            """);
        var unchanged = "SELECT count(*)::int AS \"Value\" FROM games WHERE home_ap_rank IS NOT NULL OR away_ap_rank IS NOT NULL";
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>(unchanged).SingleAsync());
        var legacy = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "027_CollegeFootball.sql"));
        var polls = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "040_CollegePolls.sql"));
        await db.Database.ExecuteSqlRawAsync(polls); await db.Database.ExecuteSqlRawAsync(legacy); await db.Database.ExecuteSqlRawAsync(polls);
        var game = Assert.Single(await Games());
        Assert.Equal(CollegePoll.Ap, game.RankPoll); Assert.Equal(5, game.HomePollRank); Assert.Null(game.AwayPollRank);
        var teams = await db.CollegeTeams.AsNoTracking().ToListAsync();
        Assert.All(teams, t => { Assert.Equal(CollegePoll.Ap, t.Poll); Assert.NotNull(t.PollUpdatedAt); });
        Assert.Equal(5, teams.Single(t => t.EspnId == "2483").PollRank); Assert.Null(teams.Single(t => t.EspnId == "68").PollRank);
        Assert.Equal(4, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE column_name IN ('ap_rank', 'ap_updated_at', 'home_ap_rank', 'away_ap_rank')").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>(unchanged).SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM college_teams WHERE ap_rank IS NOT NULL OR ap_updated_at IS NOT NULL").SingleAsync());
        Assert.Equal(2, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_constraint WHERE conname IN ('ck_college_teams_poll', 'ck_games_rank_poll')").SingleAsync());
    }

    [Theory]
    [InlineData(2027, 1, 2026)]
    [InlineData(2026, 9, 2026)]
    public void College_seasons_use_start_year(int year, int month, int expected) => Assert.Equal(expected, CollegeFootballPolicy.SeasonAt(new DateTime(year, month, 10)));

    [Fact]
    public void College_strength_does_not_saturate_at_NFL_bounds_and_poll_ranks_are_not_an_input()
    {
        Assert.True(CollegeFootballPolicy.Quality(35, 35) > CollegeFootballPolicy.Quality(25, 25));
        Assert.Null(CollegeFootballPolicy.Quality(25, null));
    }

    public async Task DisposeAsync()
    {
        client?.Dispose(); if (app is not null) await app.DisposeAsync(); await entry.DisposeAsync(); await database.DisposeAsync();
    }
}
