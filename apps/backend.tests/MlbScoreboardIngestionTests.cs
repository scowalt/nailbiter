using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using backend.Data;
using backend.GraphQL.Types;
using backend.Services;
using backend.Services.Espn;
using backend.Services.Kalshi;
using Backend.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace backend.tests;

public sealed class MlbScoreboardIngestionTests : IClassFixture<SharedPostgreSql>, IAsyncLifetime
{
    private static readonly JsonObject Espn = JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "MlbScoreboard", "espn-basic.json")))!.AsObject();

    private readonly SyntheticMlbProvider provider = new(Espn);
    private readonly ReadyBroadcaster broadcaster = new();
    private readonly IngestionLogs logs = new();
    private readonly HostedGameFixture host;

    public MlbScoreboardIngestionTests(SharedPostgreSql server)
    {
        host = new(Served("opening"), services =>
        {
            services.RemoveAll<ISseBroadcaster>();
            services.AddSingleton<ISseBroadcaster>(broadcaster);
            services.AddSingleton<ILoggerProvider>(logs);
            services.AddTransient(s => new KalshiOddsService(new HttpClient(provider), s.GetRequiredService<ILogger<KalshiOddsService>>(),
                s.GetRequiredService<TimeProvider>()));
            services.AddTransient(s => new EspnOddsService(new HttpClient(provider), s.GetRequiredService<ILogger<EspnOddsService>>(),
                s, s.GetRequiredService<IGameUpdateService>(), s.GetRequiredService<KalshiOddsService>()));
        }, server);
    }

    public Task InitializeAsync() => host.InitializeAsync();
    public Task DisposeAsync() => host.DisposeAsync();

    [Fact]
    public async Task The_opening_board_persists_every_contest_through_the_scoreboard_contract_without_a_market()
    {
        await Observe("opening");

        var games = await Api();
        Assert.Equal(new Listed("final", 4, 3, 9), games["991300101"]);
        Assert.Equal(new Listed("final", 2, 4, 11), games["991300102"]);
        Assert.Equal(new Listed("final", 3, 2, 9), games["991300103"]);
        Assert.Equal(new Listed("in_progress", 1, 3, 5), games["991300104"]);
        Assert.Equal(new Listed("postponed", 0, 0, null), games["991300105"]);
        Assert.Equal(new Listed("scheduled", 0, 0, null), games["991300106"]);
        Assert.Equal(new Listed("canceled", 0, 0, null), games["991300107"]);
        Assert.Equal(new Listed("in_progress", 2, 1, 6), games["991300108"]);
        Assert.Equal(new Listed("in_progress", 1, 3, 7), games["991300109"]);

        Assert.Equal(MlbScoreboardPhase.Final, (await View("991300101")).Phase);
        Assert.Equal(11, (await View("991300102")).Board.Value.Innings);
        Assert.Equal(MlbLineCell.NotPlayed, (await View("991300101")).Board.Value.Home.Line[^1]);
        Assert.Equal(MlbScoreboardPhase.Live, (await View("991300104")).Phase);
        Assert.Equal("Top 5th", (await View("991300104")).Board.Value.Detail);
        Assert.Equal(MlbScoreboardPhase.Postponed, (await View("991300105")).Phase);
        Assert.Equal(MlbScoreboardPhase.Pregame, (await View("991300106")).Phase);
        Assert.Equal(MlbScoreboardPhase.Canceled, (await View("991300107")).Phase);
        Assert.Equal(MlbScoreboardPhase.Delayed, (await View("991300108")).Phase);
        Assert.Equal(MlbScoreboardPhase.Suspended, (await View("991300109")).Phase);
        Assert.True((await View("991300102")).Completed);
        Assert.False((await View("991300105")).Completed);

        using var league = await Page("/mlb");
        Assert.Equal(["991300104", "991300108", "991300109"], league.QuerySelectorAll("a.league-game.is-live")
            .Select(a => a.GetAttribute("href")![6..]).Order().ToArray());
        using var home = await Page("/");
        Assert.Equal(["991300104", "991300108", "991300109"], home.QuerySelectorAll("#home-section-live a[href^='/game/']")
            .Select(a => a.GetAttribute("href")![6..]).Order().ToArray());

        Assert.Equal(0, await OddsRows());
        Assert.Empty(logs.AtLeast(LogLevel.Error));
    }

    [Fact]
    public async Task An_inning_only_frame_publishes_over_detail_SSE_without_an_odds_row()
    {
        await Observe("opening");
        await using var detail = new SsePeer(host.Client, "/sse/game/991300104");
        using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await broadcaster.ReadyAsync(["game:991300104"], ready.Token);

        await Observe("advance");

        Assert.Equal(("3", "2"), await Scores(detail, "2"));
        Assert.Equal(new Listed("in_progress", 2, 3, 6), (await Api())["991300104"]);
        Assert.Equal("Bottom 6th", (await View("991300104")).Board.Value.Detail);
        Assert.Equal(0, await OddsRows());
    }

    [Fact]
    public async Task An_odds_only_update_carries_the_accepted_frame_without_renewing_its_clock()
    {
        await Observe("opening");
        await Observe("advance");
        provider.ListMarket("27JUL101735ATLPHI", home: "PHI", away: "ATL", homeBid: 0.62m);
        await using var detail = new SsePeer(host.Client, "/sse/game/991300104");
        using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await broadcaster.ReadyAsync(["game:991300104"], ready.Token);

        await Observe("out-of-order");

        Assert.Equal(1, await OddsRows());
        Assert.Equal(new Listed("in_progress", 2, 3, 6), (await Api())["991300104"]);
        var view = await View("991300104");
        Assert.Equal(MlbScoreboardPhase.Stale, view.Phase);
        Assert.Equal(("Bottom 6th", Served("advance").UtcDateTime), (view.Board.Value.Detail, view.Board.Value.ObservedAt));
        Assert.Equal(("3", "2"), await Scores(detail, "2"));
    }

    [Fact]
    public async Task A_scoreboard_transport_failure_keeps_the_last_accepted_scores_lines_and_clock()
    {
        await Observe("opening");
        await Observe("advance");
        var before = await View("991300104");
        provider.Scoreboards = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        await Observe("partial");

        Assert.Equal(new Listed("in_progress", 2, 3, 6), (await Api())["991300104"]);
        var after = await View("991300104");
        Assert.Equal(MlbScoreboardPhase.Stale, after.Phase);
        Assert.Equal(before.Board.Value, after.Board.Value);
    }

    [Fact]
    public async Task A_partial_frame_replaces_the_optional_facts_of_the_previous_frame()
    {
        await Observe("opening");
        await Observe("advance");

        await Observe("partial");

        var board = (await View("991300104")).Board.Value;
        Assert.Equal((3, (int?)null, (int?)0, 6), (board.Away.Runs, Option(board.Away.Hits), Option(board.Away.Errors), board.Away.Line.Length));
        Assert.Equal((2, (int?)null, (int?)null, 0), (board.Home.Runs, Option(board.Home.Hits), Option(board.Home.Errors), board.Home.Line.Length));
    }

    public static TheoryData<string, string> Unusable => new()
    {
        { "wrong-side", "WrongTeam Home" },
        { "wrong-competition", "WrongCompetition" },
        { "malformed-line", "malformed away linescore" },
        { "missing-score", "MissingScore Home" },
        { "missing-detail", "MissingDetail" },
    };

    [Theory]
    [MemberData(nameof(Unusable))]
    public async Task An_unusable_board_is_rejected_whole_and_warned_once_per_frame(string corruption, string fault)
    {
        await Observe("opening");
        provider.Corrupt("991300104", Corruptions[corruption]);

        await Observe("advance");
        host.Clock.Advance(TimeSpan.FromSeconds(30));
        await Run();

        Assert.Equal(new Listed("in_progress", 1, 3, 5), (await Api())["991300104"]);
        Assert.Equal("Top 5th", (await View("991300104")).Board.Value.Detail);
        Assert.Equal(Served("advance").UtcDateTime.AddSeconds(30), (await View("991300108")).Board.Value.ObservedAt);
        var warning = Assert.Single(logs.From("backend.Services.MlbScoreboardIngestion"), e => e.Level >= LogLevel.Warning);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("991300104", warning.Message);
        Assert.Contains(fault, warning.Message);
        Assert.Empty(logs.AtLeast(LogLevel.Error));
    }

    [Fact]
    public async Task Out_of_order_and_future_revisions_are_rejected()
    {
        await Observe("opening");
        await Observe("advance");

        await Observe("out-of-order");
        Assert.Equal("Bottom 6th", (await View("991300104")).Board.Value.Detail);

        await Observe("future");
        Assert.Equal("Bottom 6th", (await View("991300104")).Board.Value.Detail);
        Assert.Equal(new Listed("in_progress", 2, 3, 6), (await Api())["991300104"]);
    }

    [Fact]
    public async Task A_resumed_doubleheader_game_completes_with_its_own_identity_and_extra_detail()
    {
        await Observe("opening");
        await Observe("advance");

        await Observe("resumption");

        var games = await Api();
        Assert.Equal(new Listed("final", 2, 3, 9), games["991300104"]);
        Assert.Equal(new Listed("final", 3, 2, 9), games["991300103"]);
        var view = await View("991300104");
        Assert.Equal((MlbScoreboardPhase.Final, "Final", true), (view.Phase, view.Board.Value.Detail, view.Completed));
    }

    [Fact]
    public async Task A_live_game_accepted_as_final_is_confirmed_complete_in_the_same_cycle()
    {
        await Observe("opening");
        await Observe("advance");

        await Observe("resumption");

        using var scope = host.Services.CreateScope();
        var game = await scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Games.AsNoTracking().SingleAsync(g => g.Id == "991300104");
        Assert.Equal(("final", (DateTime?)Served("resumption").UtcDateTime), (game.Status, game.CompletionConfirmedAt));
        Assert.True(MlbCompletionEvidence.IsActuallyCompleted(game));
        Assert.NotNull(game.HighlightsDiscoveryExpiresAt);
    }

    [Fact]
    public async Task A_postponed_event_returned_as_scheduled_or_in_progress_leaves_postponed()
    {
        await Observe("opening");
        Assert.Equal("postponed", (await Api())["991300105"].Status);

        provider.Corrupt("991300105", e => Restate(e, "STATUS_SCHEDULED", "pre", "7/12 - 4:10 PM EDT", "2027-07-12T20:10:00Z"));
        host.Clock.Advance(TimeSpan.FromMinutes(1));
        await Run();
        Assert.Equal("scheduled", (await Api())["991300105"].Status);
        Assert.Equal(MlbScoreboardPhase.Pregame, (await View("991300105")).Phase);

        provider.Corrupt("991300105", e => Restate(e, "STATUS_IN_PROGRESS", "in", "Top 1st", "2027-07-12T20:10:00Z", period: 1));
        host.Clock.Advance(TimeSpan.FromMinutes(1));
        await Run();
        Assert.Equal(new Listed("in_progress", 0, 0, 1), (await Api())["991300105"]);
    }

    [Fact]
    public async Task A_rejected_in_progress_board_leaves_a_final_game_final()
    {
        await Observe("opening");
        await Observe("advance");
        await Observe("resumption");
        provider.Corrupt("991300104", e =>
        {
            Restate(e, "STATUS_IN_PROGRESS", "in", "Bottom 9th", (string)e["date"]!, period: 9);
            Side(e, "home").Remove("score");
        });

        host.Clock.Advance(TimeSpan.FromSeconds(30));
        await Run();

        Assert.Equal(new Listed("final", 2, 3, 9), (await Api())["991300104"]);
        var view = await View("991300104");
        Assert.Equal((MlbScoreboardPhase.Final, "Final"), (view.Phase, view.Board.Value.Detail));
    }

    [Fact]
    public async Task A_rejected_final_board_leaves_a_live_game_in_progress()
    {
        await Observe("opening");
        await Observe("advance");
        provider.Corrupt("991300104", Corruptions["malformed-line"]);

        await Observe("resumption");

        Assert.Equal(new Listed("in_progress", 2, 3, 6), (await Api())["991300104"]);
        Assert.Equal("Bottom 6th", (await View("991300104")).Board.Value.Detail);
    }

    [Fact]
    public async Task A_game_that_becomes_postponed_publishes_no_game_update()
    {
        provider.Corrupt("991300105", e => Restate(e, "STATUS_IN_PROGRESS", "in", "Top 1st", (string)e["date"]!, period: 1));
        await Observe("opening");
        Assert.Contains(broadcaster.Published, u => u.GameId == "991300105" && u.Status == GameStatus.IN_PROGRESS);
        provider.Corrupt("991300105", _ => { });
        broadcaster.Published.Clear();

        host.Clock.Advance(TimeSpan.FromSeconds(30));
        await Run();

        Assert.Equal("postponed", (await Api())["991300105"].Status);
        Assert.Contains(broadcaster.Published, u => u.GameId == "991300104");
        Assert.DoesNotContain(broadcaster.Published, u => u.GameId == "991300105");
    }

    [Fact]
    public async Task A_scheduled_game_that_leaves_its_board_keeps_its_accepted_status_and_frame_until_the_board_returns_it()
    {
        await Observe("opening");
        provider.Withdraw("991300106");

        host.Clock.Advance(TimeSpan.FromMinutes(1));
        await Run();

        Assert.Equal(new Listed("scheduled", 0, 0, null), (await Api())["991300106"]);
        Assert.Equal(MlbScoreboardPhase.Pregame, (await View("991300106")).Phase);

        provider.Restore("991300106");
        provider.Corrupt("991300106", e => Restate(e, "STATUS_IN_PROGRESS", "in", "Top 1st", (string)e["date"]!, period: 1));
        host.Clock.Advance(TimeSpan.FromMinutes(1));
        await Run();

        Assert.Equal(new Listed("in_progress", 0, 0, 1), (await Api())["991300106"]);
        Assert.Equal(MlbScoreboardPhase.Live, (await View("991300106")).Phase);
    }

    [Fact]
    public async Task Acceptance_held_up_by_another_writer_gives_up_at_its_deadline_and_the_cycle_continues()
    {
        await Observe("opening");
        await using var holder = new NpgsqlConnection(ConnectionString());
        await holder.OpenAsync();
        await using (var hold = new NpgsqlCommand("SELECT pg_advisory_lock(683, hashtext('991300104'))", holder))
            await hold.ExecuteNonQueryAsync();

        var cycle = Task.Run(() => Observe("advance"));
        var finished = await Task.WhenAny(cycle, Task.Delay(TimeSpan.FromSeconds(60)));

        Assert.Same(cycle, finished);
        await cycle;
        var games = await Api();
        Assert.Equal(new Listed("in_progress", 1, 3, 5), games["991300104"]);
        Assert.Equal(new Listed("in_progress", 2, 1, 6), games["991300108"]);
        var error = Assert.Single(logs.AtLeast(LogLevel.Error));
        Assert.Contains("991300104", error.Message);
    }

    private static readonly Dictionary<string, Action<JsonObject>> Corruptions = new()
    {
        ["wrong-side"] = e =>
        {
            var home = Side(e, "home");
            home["id"] = "9199";
            home["team"]!["id"] = "9199";
        },
        ["wrong-competition"] = e => e["competitions"]![0]!["id"] = "991300103",
        ["malformed-line"] = e => Side(e, "away")["linescores"]![0]!["value"] = "x",
        ["missing-score"] = e => Side(e, "home").Remove("score"),
        ["missing-detail"] = e =>
        {
            foreach (var status in new[] { e["status"]!, e["competitions"]![0]!["status"]! })
            {
                status["type"]!.AsObject().Remove("detail");
                status.AsObject().Remove("detail");
            }
        },
    };

    private static IEnumerable<JsonObject> Competitors(JsonObject e) => e["competitions"]![0]!["competitors"]!.AsArray().Select(c => c!.AsObject());
    private static JsonObject Side(JsonObject e, string side) => Competitors(e).Single(c => (string)c["homeAway"]! == side);

    private static void Restate(JsonObject e, string name, string state, string detail, string date, int period = 0)
    {
        e["date"] = date;
        e["competitions"]![0]!["date"] = date;
        foreach (var status in new[] { e["status"]!, e["competitions"]![0]!["status"]! })
        {
            status["period"] = period;
            status["type"] = new JsonObject
            {
                ["name"] = name, ["state"] = state, ["completed"] = false, ["description"] = name,
                ["detail"] = detail, ["shortDetail"] = detail,
            };
        }
        if (state == "in")
            foreach (var competitor in Competitors(e))
            {
                competitor["score"] = "0";
                competitor["linescores"] = new JsonArray(new JsonObject { ["value"] = 0.0, ["displayValue"] = "0", ["period"] = 1 });
            }
    }

    private static int? Option(Microsoft.FSharp.Core.FSharpOption<int>? value) => value is null ? null : value.Value;

    private sealed record Listed(string Status, int? Home, int? Away, int? Period);

    private static DateTimeOffset Served(string observation) =>
        DateTimeOffset.Parse((string)Espn["observations"]!.AsArray().Single(o => (string)o!["id"]! == observation)!["servedAt"]!);

    private async Task Observe(string observation)
    {
        provider.Serve(observation);
        host.Clock.SetUtcNow(Served(observation));
        await Run();
    }

    private async Task Run()
    {
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<EspnOddsService>().ScrapeAndStoreOddsAsync("mlb", null);
    }

    private async Task<Dictionary<string, Listed>> Api()
    {
        using var body = JsonDocument.Parse(await host.Client.GetStringAsync("/api/games?sport=mlb"));
        return body.RootElement.EnumerateArray().ToDictionary(g => g.GetProperty("id").GetString()!, g => new Listed(
            g.GetProperty("status").GetString()!, Int(g.GetProperty("homeScore")), Int(g.GetProperty("awayScore")), Int(g.GetProperty("period"))));
        static int? Int(JsonElement value) => value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;
    }

    private async Task<MlbScoreboardView> View(string gameId) =>
        (await host.Services.GetRequiredService<MlbScoreboardStore>().ReadAsync(gameId, CancellationToken.None)).Value;

    private async Task<IDocument> Page(string path) => await new HtmlParser().ParseDocumentAsync(await host.Client.GetStringAsync(path));

    private async Task<int> OddsRows()
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().OddsSnapshots.CountAsync();
    }

    private string ConnectionString()
    {
        using var scope = host.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Database.GetConnectionString()!;
    }

    private static async Task<(string Away, string Home)> Scores(SsePeer peer, string home)
    {
        while (true)
        {
            using var batch = await peer.ReadForAsync("detail-home-score");
            var update = (batch.QuerySelector("#detail-away-score")!.TextContent.Trim(), batch.QuerySelector("#detail-home-score")!.TextContent.Trim());
            if (update.Item2 == home) return update;
        }
    }

    internal sealed record Entry(string Category, LogLevel Level, string Message);

    private sealed class IngestionLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<Entry> entries = new();
        public IEnumerable<Entry> From(string category) => entries.Where(e => e.Category == category);
        public IEnumerable<Entry> AtLeast(LogLevel level) => entries.Where(e => e.Level >= level);
        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, entries);
        public void Dispose() { }

        private sealed class Logger(string category, ConcurrentQueue<Entry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new Entry(category, logLevel, formatter(state, exception)));
        }
    }
}

internal sealed class SyntheticMlbProvider(JsonObject fixture) : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, Action<JsonObject>> corruptions = new();
    private readonly ConcurrentDictionary<string, JsonObject> markets = new();
    private readonly ConcurrentDictionary<string, bool> withdrawn = new();
    private volatile JsonObject? observation;

    public ConcurrentQueue<string> Requests { get; } = new();
    public Func<HttpResponseMessage>? Scoreboards { get; set; }

    public void Serve(string id) => observation = fixture["observations"]!.AsArray().Single(o => (string)o!["id"]! == id)!.AsObject();

    public void Corrupt(string gameId, Action<JsonObject> corruption) => corruptions[gameId] = corruption;

    public void Withdraw(string gameId) => withdrawn[gameId] = true;

    public void Restore(string gameId) => withdrawn.TryRemove(gameId, out _);

    public void ListMarket(string eventCode, string home, string away, decimal homeBid)
    {
        JsonObject Market(string team, decimal bid) => new()
        {
            ["ticker"] = $"KXMLBGAME-{eventCode}-{team}", ["event_ticker"] = $"KXMLBGAME-{eventCode}", ["status"] = "active",
            ["result"] = "", ["market_type"] = "binary", ["notional_value_dollars"] = "1.0000",
            ["yes_bid_dollars"] = bid.ToString("0.0000"), ["yes_ask_dollars"] = (bid + 0.01m).ToString("0.0000"),
            ["yes_bid_size_fp"] = "10.00", ["yes_ask_size_fp"] = "10.00", ["yes_sub_title"] = "Synthetic " + team,
        };
        markets[eventCode + home] = Market(home, homeBid);
        markets[eventCode + away] = Market(away, 0.99m - homeBid);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        Requests.Enqueue(uri.ToString());
        if (uri.Host == "site.web.api.espn.com")
        {
            if (Scoreboards is { } respond) return Task.FromResult(respond());
            var served = observation!;
            var board = served["boards"]!.AsArray().FirstOrDefault(b => (string)b!["url"]! == uri.ToString());
            var body = board?["body"]!.DeepClone().AsObject() ?? new JsonObject { ["events"] = new JsonArray() };
            var events = body["events"]!.AsArray();
            foreach (var item in events.Where(e => withdrawn.ContainsKey((string)e!["id"]!)).ToList())
                events.Remove(item);
            foreach (var item in body["events"]!.AsArray())
                if (corruptions.TryGetValue((string)item!["id"]!, out var corruption))
                    corruption(item.AsObject());
            var response = Json(body.ToJsonString());
            response.Headers.Date = DateTimeOffset.Parse((string)served["responseDate"]!);
            return Task.FromResult(response);
        }
        if (uri.AbsolutePath.EndsWith("/markets", StringComparison.Ordinal))
            return Task.FromResult(Json(new JsonObject { ["markets"] = new JsonArray([.. markets.Values.Select(m => m.DeepClone())]), ["cursor"] = "" }.ToJsonString()));
        if (uri.AbsolutePath.EndsWith("/milestones", StringComparison.Ordinal))
            return Task.FromResult(Json("{\"milestones\":[],\"cursor\":\"\"}"));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
