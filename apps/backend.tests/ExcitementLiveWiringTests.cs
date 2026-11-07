using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using backend.Data;
using backend.GraphQL.Types;
using backend.Models;
using backend.Services;
using backend.Services.Espn;
using backend.Services.Kalshi;
using Backend.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FSharp.Core;
using Xunit;
using static backend.tests.ExcitementWiringSupport;

namespace backend.tests;

public sealed class ExcitementLiveWiringTests : IAsyncLifetime
{
    private readonly DateTimeOffset now = new(2026, 10, 10, 18, 0, 0, TimeSpan.Zero);
    private readonly ReadyBroadcaster broadcaster = new();
    private readonly HistoryReads historyReads = new();
    private readonly Provider provider = new();
    private readonly HostedGameFixture host;

    public ExcitementLiveWiringTests()
    {
        host = new HostedGameFixture(now, services =>
        {
            services.RemoveAll<ISseBroadcaster>();
            services.AddSingleton<ISseBroadcaster>(broadcaster);
            services.AddDbContext<ExcitementDbContext>(options => options.AddInterceptors(historyReads));
            services.AddTransient(s => new KalshiOddsService(new HttpClient(provider), NullLogger<KalshiOddsService>.Instance, s.GetRequiredService<TimeProvider>()));
            services.AddTransient(s => new EspnOddsService(new HttpClient(provider), NullLogger<EspnOddsService>.Instance,
                s, s.GetRequiredService<IGameUpdateService>(), s.GetRequiredService<KalshiOddsService>()));
            services.AddTransient(s => new CollegeEspnClient(new HttpClient(provider), s.GetRequiredService<TimeProvider>(),
                s.GetRequiredService<IMemoryCache>(), NullLogger<CollegeEspnClient>.Instance));
            services.AddTransient(_ => new CollegeKalshiClient(new HttpClient(provider), NullLogger<CollegeKalshiClient>.Instance));
        });
    }

    public Task InitializeAsync() => host.InitializeAsync();
    public Task DisposeAsync() => host.DisposeAsync();

    [Theory]
    [InlineData("nba")]
    [InlineData("nfl")]
    [InlineData("nhl")]
    [InlineData("mlb")]
    public async Task Professional_ingestion_delivers_full_history_excitement_over_hosted_SSE_through_completion(string sport)
    {
        var drama = Game("drama", sport, "HOM", "AWY");
        var single = Game("single", sport, "HMM", "AWA");
        await host.SeedAsync(drama, History(now, drama.Id, new double[] { .1, .9, .1, .9, .1 }
            .Concat(Enumerable.Repeat(.1, 110))).Reverse());
        await host.SeedAsync(single, []);
        await host.SeedAsync(Game("unrelated", sport, "OTH", "ELSE"), History(now, "unrelated", [.1, .9]));
        provider.SetGames(now, drama, single);

        await using var live = new SsePeer(host.Client, "/sse/live");
        await using var league = new SsePeer(host.Client, "/sse/live/" + sport);
        await using var detail = new SsePeer(host.Client, "/sse/game/drama");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await broadcaster.ReadyAsync(["live", "live:" + sport, "game:drama"], deadline.Token);

        historyReads.Commands.Clear();
        await RunProfessional(sport, deadline.Token);
        using (var fragments = await live.ReadForAsync("excitement-drama")) AssertScore(fragments, "excitement-drama", "33", "medium");
        using (var fragments = await league.ReadForAsync("excitement-drama")) AssertScore(fragments, "excitement-drama", "33", "medium");
        using (var fragments = await detail.ReadForAsync("detail-excitement"))
        {
            AssertScore(fragments, "detail-excitement", "33", "medium");
            Assert.Equal("Live", fragments.QuerySelector("#detail-status")!.TextContent);
            Assert.Equal("14", fragments.QuerySelector("#detail-home-score")!.TextContent);
        }
        using (var fragments = await live.ReadForAsync("excitement-single")) AssertUnavailable(fragments, "excitement-single", false);
        using (var fragments = await league.ReadForAsync("excitement-single")) AssertUnavailable(fragments, "excitement-single", false);
        AssertBatch("drama", "single");
        await AssertPersistedOdds("drama", .9, "Kalshi");
        await AssertHttpScore(sport, "drama", "33", "medium");

        historyReads.Commands.Clear();
        await RunProfessional(sport, deadline.Token);
        using (var fragments = await live.ReadForAsync("excitement-single")) AssertScore(fragments, "excitement-single", "0", "low");
        using (var fragments = await league.ReadForAsync("excitement-single")) AssertScore(fragments, "excitement-single", "0", "low");
        using (var fragments = await detail.ReadForAsync("detail-excitement")) AssertScore(fragments, "detail-excitement", "33", "medium");
        AssertBatch("drama", "single");
        await AssertHttpScore(sport, "single", "0", "low");

        provider.Final = true;
        historyReads.Commands.Clear();
        await RunProfessional(sport, deadline.Token);
        using (var fragments = await live.ReadForAsync("excitement-drama"))
        {
            AssertScore(fragments, "excitement-drama", "33", "medium");
            Assert.Equal("Final", fragments.QuerySelector("#status-drama")!.TextContent);
            Assert.Empty(fragments.QuerySelector("#probbar-drama")!.TextContent);
        }
        using (var fragments = await league.ReadForAsync("excitement-drama")) AssertScore(fragments, "excitement-drama", "33", "medium");
        using (var fragments = await detail.ReadForAsync("detail-excitement"))
        {
            AssertScore(fragments, "detail-excitement", "33", "medium");
            Assert.Equal("Final", fragments.QuerySelector("#detail-status")!.TextContent);
            Assert.Equal("21", fragments.QuerySelector("#detail-home-score")!.TextContent);
        }
        AssertBatch("drama", "single");
        await AssertHttpScore(sport, "drama", "33", "medium");
    }

    [Fact]
    public async Task College_ingestion_batches_excitement_and_delivers_unavailable_then_bound_zero_through_completion()
    {
        var drama = Game("1001", "cfb", "ORE", "BSU");
        var single = Game("1002", "cfb", "FRES", "RICE");
        await host.SeedAsync(drama, History(now, drama.Id, new double[] { .1, .9, .1, .9, .1 }
            .Concat(Enumerable.Repeat(.1, 110))).Reverse());
        await host.SeedAsync(single, []);
        var unrelated = Game("unrelated", "cfb", "OTH", "ELSE");
        unrelated.Status = "canceled";
        await host.SeedAsync(unrelated, History(now, "unrelated", [.1, .9]));
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            db.CollegeImports.Add(new CollegeImport { Season = 2026, FirstImportAt = now.UtcDateTime, WindowStart = now.UtcDateTime.AddDays(-14) });
            foreach (var game in new[] { drama, single })
                db.CollegeTeams.Add(new CollegeTeam { Season = 2026, EspnId = game.HomeTeamId!, Name = game.HomeTeam,
                    Abbreviation = game.HomeTeamAbbreviation, FetchedAt = now.UtcDateTime, FpiUpdatedAt = now.UtcDateTime });
            await db.SaveChangesAsync();
        }
        provider.SetGames(now, drama, single);
        provider.BoundSecond = true;
        await using var live = new SsePeer(host.Client, "/sse/live");
        await using var league = new SsePeer(host.Client, "/sse/live/cfb");
        await using var detail = new SsePeer(host.Client, "/sse/game/1002");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await broadcaster.ReadyAsync(["live", "live:cfb", "game:1002"], deadline.Token);

        historyReads.Commands.Clear();
        await RunCollege(deadline.Token);
        using (var fragments = await live.ReadForAsync("excitement-1001")) AssertScore(fragments, "excitement-1001", "33", "medium");
        using (var fragments = await league.ReadForAsync("excitement-1001")) AssertScore(fragments, "excitement-1001", "33", "medium");
        using (var fragments = await live.ReadForAsync("excitement-1002")) AssertUnavailable(fragments, "excitement-1002", true);
        using (var fragments = await league.ReadForAsync("excitement-1002")) AssertUnavailable(fragments, "excitement-1002", true);
        using (var fragments = await detail.ReadForAsync("detail-excitement"))
        {
            AssertUnavailable(fragments, "detail-excitement", true);
            Assert.Contains(">99%", fragments.QuerySelector("#detail-probbar")!.TextContent);
            Assert.Equal("Live", fragments.QuerySelector("#detail-status")!.TextContent);
        }
        AssertBatch("1001", "1002");
        await AssertPersistedOdds("1002", .995, "Kalshi (near-certain orderbook bound)");
        await AssertHttpScore("cfb", "1001", "33", "medium");

        host.Clock.Advance(TimeSpan.FromSeconds(30));
        historyReads.Commands.Clear();
        await RunCollege(deadline.Token);
        using (var fragments = await live.ReadForAsync("excitement-1002")) AssertScore(fragments, "excitement-1002", "0", "low");
        using (var fragments = await league.ReadForAsync("excitement-1002")) AssertScore(fragments, "excitement-1002", "0", "low");
        using (var fragments = await detail.ReadForAsync("detail-excitement"))
        {
            AssertScore(fragments, "detail-excitement", "0", "low");
            Assert.Contains(">99%", fragments.QuerySelector("#detail-probbar")!.TextContent);
        }
        AssertBatch("1001", "1002");
        await AssertHttpScore("cfb", "1002", "0", "low");

        provider.FailQuotes = true;
        host.Clock.Advance(TimeSpan.FromMinutes(3));
        historyReads.Commands.Clear();
        await RunCollege(deadline.Token);
        using (var fragments = await live.ReadForAsync("excitement-1001")) AssertScore(fragments, "excitement-1001", "33", "medium");
        using (var fragments = await league.ReadForAsync("excitement-1001")) AssertScore(fragments, "excitement-1001", "33", "medium");
        using (var fragments = await live.ReadForAsync("excitement-1002")) AssertScore(fragments, "excitement-1002", "0", "low");
        using (var fragments = await league.ReadForAsync("excitement-1002")) AssertScore(fragments, "excitement-1002", "0", "low");
        using (var fragments = await detail.ReadForAsync("detail-excitement"))
        {
            AssertScore(fragments, "detail-excitement", "0", "low");
            Assert.Equal("Live probability unavailable", fragments.QuerySelector("#detail-probbar")!.TextContent);
            Assert.Equal("Live", fragments.QuerySelector("#detail-status")!.TextContent);
        }
        AssertBatch("1001", "1002");
        await AssertHttpScore("cfb", "1001", "33", "medium");
        await AssertHttpScore("cfb", "1002", "0", "low");

        provider.Final = true;
        historyReads.Commands.Clear();
        await RunCollege(deadline.Token);
        using (var fragments = await live.ReadForAsync("excitement-1001")) AssertScore(fragments, "excitement-1001", "33", "medium");
        using (var fragments = await league.ReadForAsync("excitement-1001")) AssertScore(fragments, "excitement-1001", "33", "medium");
        using (var fragments = await live.ReadForAsync("excitement-1002"))
        {
            AssertScore(fragments, "excitement-1002", "0", "low");
            Assert.Equal("Final", fragments.QuerySelector("#status-1002")!.TextContent);
            Assert.Equal("21", fragments.QuerySelector("#home-score-1002")!.TextContent);
        }
        using (var fragments = await league.ReadForAsync("excitement-1002")) AssertScore(fragments, "excitement-1002", "0", "low");
        using (var fragments = await detail.ReadForAsync("detail-excitement"))
        {
            AssertScore(fragments, "detail-excitement", "0", "low");
            Assert.Empty(fragments.QuerySelector("#detail-probbar")!.TextContent);
            Assert.Equal("Final", fragments.QuerySelector("#detail-status")!.TextContent);
            Assert.Equal("21", fragments.QuerySelector("#detail-home-score")!.TextContent);
        }
        AssertBatch("1001", "1002");
        await AssertHttpScore("cfb", "1001", "33", "medium");
        await AssertHttpScore("cfb", "1002", "0", "low");
    }

    private async Task RunCollege(CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CollegeFootballService>().RunAsync(false, true, ct);
    }

    private async Task RunProfessional(string sport, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<EspnOddsService>()
            .ScrapeAndStoreOddsAsync(sport, FSharpOption<CancellationToken>.Some(ct));
    }

    private Game Game(string id, string sport, string home, string away) => new()
    {
        Id = id, Sport = sport, Status = "in_progress", HomeTeam = home, AwayTeam = away,
        HomeTeamAbbreviation = home, AwayTeamAbbreviation = away,
        HomeTeamId = "home-" + id, AwayTeamId = "away-" + id,
        ScheduledStart = now.UtcDateTime.AddHours(-1), SeasonYear = 2026, SeasonType = 2, WeekNumber = 5,
        HomeScore = 14, AwayScore = 10, Period = 2, GameClock = "5:00",
        HomeMarketTicker = CollegeTicker(now.UtcDateTime.AddHours(-1), home, away) + "-" + home,
        AwayMarketTicker = CollegeTicker(now.UtcDateTime.AddHours(-1), home, away) + "-" + away
    };

    private static string CollegeTicker(DateTime start, string home, string away) =>
        "KXNCAAFGAME-" + TimeZoneInfo.ConvertTimeFromUtc(start, TimeZoneInfo.FindSystemTimeZoneById("America/New_York"))
            .ToString("yyMMMdd", CultureInfo.InvariantCulture).ToUpperInvariant() + away + home;

    private async Task AssertHttpScore(string sport, string id, string value, string band)
    {
        var easternDate = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).ToString("yyyy-MM-dd");
        foreach (var path in new[] { "/", sport is "nfl" or "cfb" ? $"/{sport}?week=5&season=2026&type=2" : $"/{sport}?date={easternDate}", $"/game/{id}" })
        {
            using var page = await new HtmlParser().ParseDocumentAsync(await host.Client.GetStringAsync(path));
            AssertScore(page, path.StartsWith("/game/") ? "detail-excitement" : "excitement-" + id, value, band);
        }
    }

    private async Task AssertPersistedOdds(string id, double probability, string source)
    {
        using var response = await host.Client.GetAsync($"/api/games/{id}/odds");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var latest = json.RootElement.GetProperty("oddsHistory").EnumerateArray().Last();
        Assert.Equal(probability, latest.GetProperty("homeWinProbability").GetDouble(), 8);
        Assert.Equal(source, latest.GetProperty("source").GetString());
        Assert.Equal(host.Clock.GetUtcNow().UtcDateTime, latest.GetProperty("timestamp").GetDateTime());
    }

    private void AssertBatch(params string[] ids)
    {
        var read = Assert.Single(historyReads.Commands);
        Assert.True(read.CanBeCanceled);
        var parameters = read.Parameters.OfType<IEnumerable<string>>().SelectMany(p => p)
            .Concat(read.Parameters.OfType<string>()).ToArray();
        foreach (var id in ids)
            Assert.True(parameters.Contains(id) || read.Sql.Contains("'" + id + "'"), "History read must restrict requested IDs: " + read.Sql);
        Assert.DoesNotContain("unrelated", parameters);
        Assert.DoesNotContain("unrelated", read.Sql);
    }

    private static void AssertUnavailable(IDocument page, string id, bool college)
    {
        var region = page.QuerySelector("#" + id);
        Assert.NotNull(region);
        Assert.Null(region.QuerySelector(".excitement-high, .excitement-medium, .excitement-low"));
        Assert.Equal(college ? "Excitement unavailable" : "", region.TextContent.Trim());
    }

    private sealed class Provider : HttpMessageHandler
    {
        private Game[] games = [];
        private DateTimeOffset observed;
        public bool Final;
        public bool BoundSecond;
        public bool FailQuotes;

        public void SetGames(DateTimeOffset time, params Game[] values) { observed = time; games = values; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
            object body;
            if (uri.AbsolutePath.EndsWith("/scoreboard"))
            {
                var eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
                body = new {
                    leagues = new[] { new { season = new { year = 2026 }, calendar = new[] { new { value = "2", startDate = "2026-08-22T07:00Z" } } } },
                    events = games.Where(g => TimeZoneInfo.ConvertTimeFromUtc(g.ScheduledStart, eastern).ToString("yyyyMMdd") == query["dates"].ToString()).Select(Event)
                };
            }
            else if (uri.AbsolutePath.EndsWith("/markets"))
            {
                body = new { markets = games[0].Sport == "cfb" ? [] : games.SelectMany(g => new[] { Market(g, true), Market(g, false) }), cursor = "" };
            }
            else if (uri.Host == "sports.core.api.espn.com" && uri.AbsolutePath.EndsWith("/events"))
            {
                var eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
                var listed = games.Where(g => TimeZoneInfo.ConvertTimeFromUtc(g.ScheduledStart, eastern).ToString("yyyyMMdd") == query["dates"].ToString()).ToArray();
                body = new { count = listed.Length, pageCount = listed.Length == 0 ? 0 : 1, pageIndex = listed.Length == 0 ? 0 : 1,
                    items = listed.Select(g => new Dictionary<string, string> { ["$ref"] = $"https://sports.core.api.espn.com/v2/sports/football/leagues/college-football/events/{g.Id}?lang=en" }) };
            }
            else if (uri.AbsolutePath.Contains("/markets/"))
            {
                if (FailQuotes) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                var ticker = Uri.UnescapeDataString(uri.Segments.Last());
                var game = games.Single(g => g.HomeMarketTicker == ticker || g.AwayMarketTicker == ticker);
                body = new { market = Market(game, game.HomeMarketTicker == ticker) };
            }
            else throw new InvalidOperationException("Unexpected controlled provider request: " + uri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body)) });
        }

        private object Event(Game game) => new
        {
            id = game.Id, date = game.ScheduledStart, shortName = game.AwayTeam + " @ " + game.HomeTeam,
            season = new { year = 2026, type = 2 }, week = new { number = 5 },
            status = new { period = Final ? 4 : 2, displayClock = Final ? "0:00" : "5:00", type = new { state = Final ? "post" : "in", name = Final ? "STATUS_FINAL" : "STATUS_IN_PROGRESS" } },
            competitions = new[] { new { id = game.Id, competitors = new[] {
                new { homeAway = "home", score = Final ? "21" : "14", team = new { id = game.HomeTeamId, displayName = game.HomeTeam, abbreviation = game.HomeTeamAbbreviation } },
                new { homeAway = "away", score = "10", team = new { id = game.AwayTeamId, displayName = game.AwayTeam, abbreviation = game.AwayTeamAbbreviation } }
            } } }
        };

        private object Market(Game game, bool home)
        {
            var date = TimeZoneInfo.ConvertTimeFromUtc(game.ScheduledStart, TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
            var series = game.Sport == "cfb" ? "KXNCAAFGAME" : "KX" + game.Sport.ToUpperInvariant() + "GAME";
            var ticker = series + "-" + date.ToString("yyMMMdd", CultureInfo.InvariantCulture).ToUpperInvariant() + game.AwayTeamAbbreviation + game.HomeTeamAbbreviation;
            var probability = game.Id is "drama" or "1001" ? (home ? .9 : .1) : .5;
            var bound = BoundSecond && game.Id == "1002";
            return new
            {
                ticker = ticker + "-" + (home ? game.HomeTeamAbbreviation : game.AwayTeamAbbreviation), event_ticker = ticker,
                status = "active", result = "", market_type = "binary", notional_value_dollars = "1.0000",
                close_time = observed.AddDays(1),
                yes_bid_dollars = bound ? (home ? "0.99" : "0.00") : (probability - .01).ToString("F2", CultureInfo.InvariantCulture),
                yes_ask_dollars = bound ? (home ? "1.00" : "0.01") : (probability + .01).ToString("F2", CultureInfo.InvariantCulture),
                yes_bid_size_fp = bound && !home ? "0" : "10", yes_ask_size_fp = bound && home ? "0" : "10"
            };
        }
    }

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

    private sealed record HistoryRead(string Sql, object[] Parameters, bool CanBeCanceled);

    private sealed class HistoryReads : DbCommandInterceptor
    {
        public ConcurrentQueue<HistoryRead> Commands { get; } = new();
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("odds_snapshots") && command.CommandText.Contains("is_historical_import"))
                Commands.Enqueue(new(command.CommandText, command.Parameters.Cast<DbParameter>().Select(p => p.Value!).ToArray(), cancellationToken.CanBeCanceled));
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
