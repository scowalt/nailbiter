using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using backend.Data;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace backend.tests;

internal sealed record CollegeHistoryGame(string Id, DateTime Kickoff, string HomeId, string Home, string AwayId, string Away)
{
    public DateTime PlayStart => Kickoff.AddMinutes(5);

    public string EventTicker => $"KXNCAAFGAME-{Kickoff.ToString("yyMMMdd", CultureInfo.InvariantCulture).ToUpperInvariant()}{Away}{Home}";
}

internal sealed class CollegeHistoryProvider : HttpMessageHandler
{
    public static readonly DateTimeOffset Now = new(2026, 9, 16, 18, 0, 0, TimeSpan.Zero);
    public const string ProbeGame = "nfl-fence-probe";
    private const int Days = 13;

    public CollegeHistoryProvider(int games, int minutes = 210)
    {
        Minutes = minutes;
        Games = Enumerable.Range(0, games).Select(i => new CollegeHistoryGame(
            (402000000 + i).ToString(CultureInfo.InvariantCulture),
            Now.UtcDateTime.Date.AddDays(1 + i % Days - 14).AddHours(16 + i / Days % 7),
            (10000 + i).ToString(CultureInfo.InvariantCulture), $"H{i:D3}",
            (20000 + i).ToString(CultureInfo.InvariantCulture), $"A{i:D3}")).ToList();
    }

    public int Minutes { get; }

    public IReadOnlyList<CollegeHistoryGame> Games { get; }

    public int Snapshots => Games.Count * Minutes;

    public Func<string, Task>? BeforePlays { get; set; }

    public Func<string, string, string?>? Override { get; set; }

    public string? FenceProbe { get; set; }

    public ConcurrentQueue<string> Requests { get; } = new();

    public ConcurrentQueue<string> FencedRequests { get; } = new();

    public ConcurrentQueue<string> Unexpected { get; } = new();

    public IEnumerable<CollegeTeam> Teams() => Games.SelectMany(game => new[] { (game.HomeId, game.Home), (game.AwayId, game.Away) })
        .Select(team => new CollegeTeam { Season = 2026, EspnId = team.Item1, Abbreviation = team.Item2, Name = team.Item2, FetchedAt = Now.UtcDateTime });

    public static Game ProbeTarget() => new()
    {
        Id = ProbeGame, Sport = "nfl", Status = "scheduled", SeasonYear = 2026, SeasonType = 2,
        HomeTeam = "Kansas City Chiefs", AwayTeam = "Buffalo Bills", HomeTeamAbbreviation = "KC", AwayTeamAbbreviation = "BUF",
        ScheduledStart = Now.UtcDateTime.AddDays(3)
    };

    public CollegeFootballService Service(ExcitementDbContext db, ILoggerFactory? logs = null)
    {
        logs ??= NullLoggerFactory.Instance;
        var clock = new FixedClock();
        var espn = new CollegeEspnClient(new HttpClient(this, false), clock, new MemoryCache(new MemoryCacheOptions()), logs.CreateLogger<CollegeEspnClient>());
        var kalshi = new CollegeKalshiClient(new HttpClient(this, false), logs.CreateLogger<CollegeKalshiClient>());
        return new CollegeFootballService(db, espn, kalshi, new NoUpdates(), clock, logs.CreateLogger<CollegeFootballService>());
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var path = uri.AbsolutePath;
        Requests.Enqueue(path);
        if (FenceProbe is not null && await FenceHeldAsync(FenceProbe, cancellationToken)) FencedRequests.Enqueue(path);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        if (path.EndsWith("/plays", StringComparison.Ordinal) && BeforePlays is not null) await BeforePlays(EventOf(path));
        var body = (path.EndsWith("/plays", StringComparison.Ordinal) || path.EndsWith("/odds", StringComparison.Ordinal)) && Override?.Invoke(EventOf(path), path) is { } replaced
            ? replaced
            : path switch
            {
                _ when path.EndsWith("/scoreboard", StringComparison.Ordinal) => Scoreboard(query["dates"]!),
                _ when uri.Host == "sports.core.api.espn.com" && path.EndsWith("/events", StringComparison.Ordinal) => Index(query["dates"]!),
                _ when path.EndsWith("/plays", StringComparison.Ordinal) => Plays(Game(EventOf(path))),
                _ when path.EndsWith("/odds", StringComparison.Ordinal) => Odds,
                _ when path.EndsWith("/historical/cutoff", StringComparison.Ordinal) => """{"market_settled_ts":"2026-07-18T00:00:00Z"}""",
                "/trade-api/v2/markets" => Markets(),
                _ when path.EndsWith("/candlesticks", StringComparison.Ordinal) =>
                    Candles(long.Parse(query["start_ts"]!, CultureInfo.InvariantCulture), long.Parse(query["end_ts"]!, CultureInfo.InvariantCulture)),
                _ => null
            };
        if (body is null) Unexpected.Enqueue(uri.ToString());
        return new HttpResponseMessage(body is null ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = new StringContent(body ?? "{}") };
    }

    private static string EventOf(string path) => path.Split('/')[^4];

    private CollegeHistoryGame Game(string id) => Games.Single(game => game.Id == id);

    private IEnumerable<CollegeHistoryGame> On(string date) => Games.Where(game => game.Kickoff.ToString("yyyyMMdd", CultureInfo.InvariantCulture) == date);

    private string Scoreboard(string date) =>
        $$"""{"leagues":[{"season":{"year":2026},"calendar":[{"value":"2","startDate":"2026-08-22T07:00Z"}]}],"events":[{{string.Join(",", On(date).Select(Event))}}]}""";

    private string Index(string date)
    {
        var ids = On(date).Select(game => game.Id).ToList();
        if (ids.Count == 0) return """{"count":0,"pageCount":0,"pageIndex":0,"pageSize":25,"items":[]}""";
        var items = string.Join(",", ids.Select(id => $$"""{"$ref":"https://sports.core.api.espn.com/v2/sports/football/leagues/college-football/events/{{id}}?lang=en"}"""));
        return $$"""{"count":{{ids.Count}},"pageCount":1,"pageIndex":1,"pageSize":100,"items":[{{items}}]}""";
    }

    private static string Event(CollegeHistoryGame game)
    {
        var at = game.Kickoff.ToString("yyyy-MM-ddTHH:mmZ", CultureInfo.InvariantCulture);
        return $$$"""
            {"id":"{{{game.Id}}}","date":"{{{at}}}","season":{"year":2026,"type":2},"week":{"number":3},
             "status":{"period":4,"displayClock":"0:00","type":{"state":"post","name":"STATUS_FINAL","shortDetail":"Final"}},
             "competitions":[{"id":"{{{game.Id}}}","date":"{{{at}}}","competitors":[
               {"homeAway":"home","score":"31","team":{"id":"{{{game.HomeId}}}","abbreviation":"{{{game.Home}}}","displayName":"{{{game.Home}}}","location":"{{{game.Home}}}"}},
               {"homeAway":"away","score":"24","team":{"id":"{{{game.AwayId}}}","abbreviation":"{{{game.Away}}}","displayName":"{{{game.Away}}}","location":"{{{game.Away}}}"}}]}]}
            """;
    }

    private string Plays(CollegeHistoryGame game) => $$"""
        {"pageIndex":1,"pageCount":1,"items":[
          {"period":{"number":1},"wallclock":"{{Stamp(game.PlayStart)}}"},
          {"period":{"number":4},"wallclock":"{{Stamp(game.PlayStart.AddMinutes(Minutes + 1))}}"}]}
        """;

    private static string Stamp(DateTime at) => at.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private const string Odds = """
        {"items":[{"provider":{"name":"DraftKings"},
          "homeTeamOdds":{"close":{"moneyLine":{"american":"-150"},"pointSpread":{"american":"-3.5"}}},
          "awayTeamOdds":{"close":{"moneyLine":{"american":"+130"}}},"close":{"total":{"american":"51.5"}}}]}
        """;

    private string Markets() => $$"""{"markets":[{{string.Join(",", Games.SelectMany(game => new[] { game.Home, game.Away }.Select(team =>
        $$$"""{"ticker":"{{{game.EventTicker}}}-{{{team}}}","event_ticker":"{{{game.EventTicker}}}","status":"finalized","result":"{{{(team == game.Home ? "yes" : "no")}}}","yes_sub_title":"{{{team}}}"}""")))}}],"cursor":""}""";

    private static string Candles(long from, long to)
    {
        var candles = new StringBuilder("""{"candlesticks":[""");
        for (var minute = 1L; from + minute * 60 <= to; minute++)
        {
            if (minute > 1) candles.Append(',');
            var price = (0.30m + minute % 40 / 100m).ToString("0.0000", CultureInfo.InvariantCulture);
            candles.Append(CultureInfo.InvariantCulture, $$"""{"end_period_ts":{{from + minute * 60}},"price":{"close_dollars":"{{price}}"},"volume_fp":"5"}""");
        }
        return candles.Append("]}").ToString();
    }

    public static async Task<bool> FenceHeldAsync(string connectionString, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var probe = new NpgsqlCommand("SELECT count(*) FROM (SELECT id FROM pro_hype_revision WHERE id = 1 FOR UPDATE SKIP LOCKED) free", connection);
        return (long)(await probe.ExecuteScalarAsync(ct))! == 0;
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class NoUpdates : IGameUpdateService
    {
        public Task PublishUpdateAsync(backend.GraphQL.Types.GameUpdate update, CancellationToken ct = default) => Task.CompletedTask;
    }
}

internal sealed class ProWriterProbe : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    private readonly ConcurrentQueue<(TimeSpan Wait, long Snapshots)> samples = new();

    private ProWriterProbe(string connectionString) => loop = Task.Run(() => RunAsync(connectionString));

    public static ProWriterProbe Start(string connectionString) => new(connectionString);

    public IReadOnlyCollection<(TimeSpan Wait, long Snapshots)> Samples => samples;

    public TimeSpan MaxWait => samples.Select(sample => sample.Wait).DefaultIfEmpty().Max();

    public async Task StopAsync()
    {
        await stop.CancelAsync();
        await loop;
    }

    private async Task RunAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var write = new NpgsqlCommand($"UPDATE games SET game_clock = game_clock WHERE id = '{CollegeHistoryProvider.ProbeGame}'", connection) { CommandTimeout = 120 };
        await using var count = new NpgsqlCommand("SELECT count(*) FROM odds_snapshots", connection);
        while (!stop.IsCancellationRequested)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            await write.ExecuteNonQueryAsync();
            var wait = timer.Elapsed;
            samples.Enqueue((wait, (long)(await count.ExecuteScalarAsync())!));
            try { await Task.Delay(10, stop.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!loop.IsCompleted) await StopAsync();
        stop.Dispose();
    }
}

internal sealed class CollegeHistoryDatabase : IAsyncDisposable
{
    private readonly Testcontainers.PostgreSql.PostgreSqlContainer container;

    private CollegeHistoryDatabase(Testcontainers.PostgreSql.PostgreSqlContainer container, CollegeHistoryProvider provider)
    {
        this.container = container;
        Provider = provider;
    }

    public CollegeHistoryProvider Provider { get; }

    public string ConnectionString => container.GetConnectionString();

    public static async Task<CollegeHistoryDatabase> StartAsync(int games, int minutes = 210)
    {
        var container = IsolatedPostgreSql.CreateContainer();
        await container.StartAsync();
        var database = new CollegeHistoryDatabase(container, new CollegeHistoryProvider(games, minutes));
        await using var db = database.Context();
        await TestSchema.CreateAsync(db.Database);
        await db.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "035_ProHypeLifecycle.sql")));
        db.CollegeTeams.AddRange(database.Provider.Teams());
        db.Games.Add(CollegeHistoryProvider.ProbeTarget());
        await db.SaveChangesAsync();
        return database;
    }

    public string LockTimeoutConnectionString => ConnectionString + ";Options=-c lock_timeout=500";

    public ExcitementDbContext Context(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) => Context(ConnectionString, interceptors);

    public static ExcitementDbContext Context(string connectionString, params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<ExcitementDbContext>().UseNpgsql(connectionString).AddInterceptors(interceptors).Options);

    public async Task<CollegeHistoryImport> ImportAsync(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors)
    {
        await using var db = Context(interceptors);
        return await Provider.Service(db).ImportHistoryAsync(true, CancellationToken.None);
    }

    public async Task<int> CollectLiveAsync()
    {
        await using var db = Context();
        return await Provider.Service(db).RunAsync(CancellationToken.None);
    }

    public async Task<List<string>> RowsAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        return rows;
    }

    public async Task<IAsyncDisposable> HoldFenceAsync()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();
        await using var fence = new NpgsqlCommand("SELECT id FROM pro_hype_revision WHERE id = 1 FOR UPDATE", connection, transaction);
        await fence.ExecuteNonQueryAsync();
        return new HeldFence(connection, transaction);
    }

    private sealed class HeldFence(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await transaction.RollbackAsync();
            await connection.DisposeAsync();
        }
    }

    public async Task<long> CountAsync(string table)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public async ValueTask DisposeAsync()
    {
        Provider.Dispose();
        await container.DisposeAsync();
    }
}
