using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using backend.Data;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace backend.tests;

public sealed class CollegeFenceOrderTests : IAsyncLifetime
{
    private const string Live = "401900010";
    private const string Upcoming = "401900011";
    private static readonly DateTimeOffset Start = new(2026, 9, 16, 18, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlContainer database = IsolatedPostgreSql.CreateContainer();
    private readonly Clock clock = new();
    private readonly Provider provider = new();
    private DbContextOptions<ExcitementDbContext> options = null!;

    public async Task InitializeAsync()
    {
        await database.StartAsync();
        options = new DbContextOptionsBuilder<ExcitementDbContext>().UseNpgsql(database.GetConnectionString()).Options;
        await using var db = new ExcitementDbContext(options);
        await TestSchema.CreateAsync(db.Database);
        await db.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "035_ProHypeLifecycle.sql")));
        db.CollegeTeams.AddRange(
            Team("2483", "ORE"), Team("242", "RICE"), Team("278", "FRES"), Team("68", "BOIS"));
        db.Games.AddRange(
            Known(Live, "2483", "ORE", "242", "RICE", Start.UtcDateTime.AddHours(-1)),
            Known(Upcoming, "278", "FRES", "68", "BOIS", Start.UtcDateTime.AddHours(2)));
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => database.DisposeAsync().AsTask();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task College_save_waits_on_the_fence_before_newest_odds_and_both_saves_commit(bool gameRowsUnchanged)
    {
        await RunAsync();
        Assert.Equal(["pregame 401900011 -110"], await TextAsync("SELECT concat_ws(' ', 'pregame', game_id, home_moneyline) FROM pregame_odds"));
        Assert.Empty(await TextAsync("SELECT game_id FROM odds_snapshots"));
        if (!gameRowsUnchanged) clock.Now = clock.Now.AddSeconds(30);
        provider.QuotesUsable = true;
        provider.UpcomingHomeMoneyline = "-150";

        await using var live = new NpgsqlConnection(database.GetConnectionString());
        await live.OpenAsync();
        await using var liveSave = await live.BeginTransactionAsync();
        await using (var fence = new NpgsqlCommand("SELECT id FROM pro_hype_revision WHERE id = 1 FOR UPDATE", live, liveSave))
            await fence.ExecuteNonQueryAsync();
        var college = OutcomeAsync(RunAsync());
        var blockedOn = await BlockedQueryAsync(live.ProcessID, college);
        var liveOutcome = await OutcomeAsync(Task.Run(async () =>
        {
            await using var insert = new NpgsqlCommand($"""
                INSERT INTO odds_snapshots (game_id, timestamp, home_moneyline, away_moneyline, home_win_probability, away_win_probability, source)
                VALUES ('{Live}', '2026-09-16 17:59:00+00', -120, 100, 0.55, 0.45, 'espn')
                """, live, liveSave) { CommandTimeout = 60 };
            await insert.ExecuteNonQueryAsync();
            await liveSave.CommitAsync();
        }));
        var collegeOutcome = await college.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(blockedOn is not null, "the college save never waited for the live save's fence");
        Assert.Equal(("SELECT id FROM pro_hype_revision WHERE id = 1 FOR UPDATE", "committed", "committed"), (blockedOn, liveOutcome, collegeOutcome));
        Assert.Equal(["pregame 401900011 -150"], await TextAsync("SELECT concat_ws(' ', 'pregame', game_id, home_moneyline) FROM pregame_odds"));
        Assert.Equal([$"odds {Live} Kalshi", $"odds {Live} espn"], await TextAsync("SELECT concat_ws(' ', 'odds', game_id, source) FROM odds_snapshots ORDER BY source"));
    }

    [Fact]
    public async Task College_collector_holds_no_fence_while_a_provider_request_is_in_flight()
    {
        provider.QuotesUsable = true;
        provider.FenceProbe = database.GetConnectionString();

        await RunAsync();

        Assert.NotEmpty(provider.Requests);
        Assert.Empty(provider.FencedRequests);
        Assert.Empty(provider.Unexpected);
        Assert.Equal([$"odds {Live} Kalshi"], await TextAsync("SELECT concat_ws(' ', 'odds', game_id, source) FROM odds_snapshots"));
        Assert.Equal(["pregame 401900011 -110"], await TextAsync("SELECT concat_ws(' ', 'pregame', game_id, home_moneyline) FROM pregame_odds"));
    }

    private async Task RunAsync()
    {
        await using var db = new ExcitementDbContext(options);
        var espn = new CollegeEspnClient(new HttpClient(provider), clock, new MemoryCache(new MemoryCacheOptions()), NullLogger<CollegeEspnClient>.Instance);
        var kalshi = new CollegeKalshiClient(new HttpClient(provider), NullLogger<CollegeKalshiClient>.Instance);
        await new CollegeFootballService(db, espn, kalshi, new NoUpdates(), clock, NullLogger<CollegeFootballService>.Instance)
            .RunAsync(false, true, CancellationToken.None);
    }

    private static async Task<string> OutcomeAsync(Task save)
    {
        try
        {
            await save;
            return "committed";
        }
        catch (Exception e)
        {
            var root = e;
            while (root.InnerException is not null) root = root.InnerException;
            return root is PostgresException p ? $"{p.SqlState} {p.MessageText}" : $"{root.GetType().Name} {root.Message}";
        }
    }

    private async Task<string?> BlockedQueryAsync(int livePid, Task college)
    {
        var timer = Stopwatch.StartNew();
        while (!college.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(60))
        {
            var waiting = await TextAsync($"""
                SELECT left(regexp_replace(query, '\s+', ' ', 'g'), 120) FROM pg_stat_activity
                WHERE wait_event_type = 'Lock' AND backend_type = 'client backend' AND pid <> {livePid}
                """);
            if (waiting.Count > 0) return waiting[0];
            await Task.Delay(20);
        }
        return null;
    }

    private async Task<List<string>> TextAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        return rows;
    }

    private static CollegeTeam Team(string id, string abbreviation) => new()
    {
        Season = 2026, EspnId = id, Abbreviation = abbreviation, Name = abbreviation, FetchedAt = Start.UtcDateTime
    };

    private static Game Known(string id, string homeId, string home, string awayId, string away, DateTime start) => new()
    {
        Id = id, Sport = "cfb", Status = "scheduled", SeasonYear = 2026, SeasonType = 2,
        HomeTeam = home, AwayTeam = away, HomeTeamAbbreviation = home, AwayTeamAbbreviation = away,
        HomeTeamId = homeId, AwayTeamId = awayId, ScheduledStart = start,
        HomeMarketTicker = $"KXNCAAFGAME-26SEP16{away}{home}-{home}", AwayMarketTicker = $"KXNCAAFGAME-26SEP16{away}{home}-{away}"
    };

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = Start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class NoUpdates : IGameUpdateService
    {
        public Task PublishUpdateAsync(backend.GraphQL.Types.GameUpdate update, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Provider : HttpMessageHandler
    {
        public bool QuotesUsable;
        public string UpcomingHomeMoneyline = "-110";
        public string? FenceProbe;
        public ConcurrentQueue<string> Requests { get; } = new();
        public ConcurrentQueue<string> FencedRequests { get; } = new();
        public ConcurrentQueue<string> Unexpected { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Enqueue(uri.AbsolutePath);
            if (FenceProbe is not null && await FenceHeldAsync(FenceProbe, cancellationToken)) FencedRequests.Enqueue(uri.AbsolutePath);
            var body = uri switch
            {
                { AbsolutePath: var path } when path.EndsWith("/scoreboard", StringComparison.Ordinal) =>
                    """{"leagues":[{"season":{"year":2026},"calendar":[{"value":"2","startDate":"2026-08-22T07:00Z"}]}],"events":[]}""",
                { Host: "sports.core.api.espn.com", AbsolutePath: var path } when path.EndsWith("/events", StringComparison.Ordinal) =>
                    """{"count":0,"pageCount":0,"pageIndex":0,"pageSize":25,"items":[]}""",
                { AbsolutePath: var path } when path.EndsWith("/summary", StringComparison.Ordinal) =>
                    Summary(System.Web.HttpUtility.ParseQueryString(uri.Query)["event"]),
                { AbsolutePath: "/trade-api/v2/markets" } => """{"markets":[],"cursor":""}""",
                { AbsolutePath: var path } when path.StartsWith("/trade-api/v2/markets/", StringComparison.Ordinal) =>
                    Market(Uri.UnescapeDataString(uri.Segments[^1])),
                _ => null
            };
            if (body is null) Unexpected.Enqueue(uri.ToString());
            return new HttpResponseMessage(body is null ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = new StringContent(body ?? "{}") };
        }

        private static async Task<bool> FenceHeldAsync(string connectionString, CancellationToken ct)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(ct);
            await using var probe = new NpgsqlCommand("SELECT count(*) FROM (SELECT id FROM pro_hype_revision WHERE id = 1 FOR UPDATE SKIP LOCKED) free", connection);
            return (long)(await probe.ExecuteScalarAsync(ct))! == 0;
        }

        private string? Summary(string? id)
        {
            var (state, home, homeId, away, awayId, date) = id switch
            {
                Live => ("in", "ORE", "2483", "RICE", "242", "2026-09-16T17:00Z"),
                Upcoming => ("pre", "FRES", "278", "BOIS", "68", "2026-09-16T20:00Z"),
                _ => default
            };
            if (state is null) return null;
            var competition = new JsonObject
            {
                ["id"] = id,
                ["date"] = date,
                ["status"] = new JsonObject
                {
                    ["period"] = state == "in" ? 2 : 0,
                    ["displayClock"] = state == "in" ? "7:00" : "0:00",
                    ["type"] = new JsonObject { ["state"] = state, ["name"] = state == "in" ? "STATUS_IN_PROGRESS" : "STATUS_SCHEDULED", ["shortDetail"] = "" }
                },
                ["competitors"] = new JsonArray(
                    new JsonObject { ["homeAway"] = "home", ["score"] = "14", ["team"] = new JsonObject { ["id"] = homeId, ["abbreviation"] = home, ["displayName"] = home } },
                    new JsonObject { ["homeAway"] = "away", ["score"] = "7", ["team"] = new JsonObject { ["id"] = awayId, ["abbreviation"] = away, ["displayName"] = away } })
            };
            if (state == "pre")
                competition["odds"] = new JsonArray(new JsonObject
                {
                    ["provider"] = new JsonObject { ["name"] = "DraftKings" },
                    ["moneyline"] = new JsonObject
                    {
                        ["home"] = new JsonObject { ["close"] = new JsonObject { ["odds"] = UpcomingHomeMoneyline } },
                        ["away"] = new JsonObject { ["close"] = new JsonObject { ["odds"] = "+130" } }
                    }
                });
            return new JsonObject
            {
                ["header"] = new JsonObject
                {
                    ["id"] = id,
                    ["date"] = date,
                    ["season"] = new JsonObject { ["year"] = 2026, ["type"] = 2 },
                    ["week"] = new JsonObject { ["number"] = 3 },
                    ["competitions"] = new JsonArray(competition)
                }
            }.ToJsonString();
        }

        private string Market(string ticker) => new JsonObject
        {
            ["market"] = new JsonObject
            {
                ["ticker"] = ticker,
                ["event_ticker"] = ticker[..ticker.LastIndexOf('-')],
                ["market_type"] = "binary",
                ["notional_value_dollars"] = "1.0000",
                ["status"] = "active",
                ["result"] = "",
                ["close_time"] = "2026-09-29T00:00:00Z",
                ["yes_bid_dollars"] = QuotesUsable ? "0.40" : "0.00",
                ["yes_ask_dollars"] = QuotesUsable ? "0.42" : "1.00",
                ["yes_bid_size_fp"] = QuotesUsable ? "5" : "0",
                ["yes_ask_size_fp"] = QuotesUsable ? "5" : "0"
            }
        }.ToJsonString();
    }
}
