using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using backend.Data;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FSharp.Collections;
using Npgsql;
using NpgsqlTypes;

namespace backend.tests;

internal sealed record ExpectedGame(string Id, string Sport, string Status, (string Selector, string Text)[] Checks);

internal sealed record WorkloadDescription(string Image, string TimescaleVersion, string TelemetryLevel, string TimescaleBackgroundWorkers, long Chunks, long CompressedChunks,
    IReadOnlyDictionary<string, int> GamesByStatus, long Snapshots, long ListingSnapshots, DateTimeOffset Clock);

internal sealed class ListingWorkloadManifest(IReadOnlyList<ExpectedGame> listed)
{
    public IReadOnlyList<ExpectedGame> On(string path) =>
        path == "/" ? listed : listed.Where(game => path.StartsWith($"/{game.Sport}?", StringComparison.Ordinal)).ToList();

    public IReadOnlySet<string> ExcitementIds(string path) =>
        On(path).Where(game => game.Status != "scheduled").Select(game => game.Id).ToHashSet();

    public IReadOnlyList<string> Check(string path, string body)
    {
        using var page = new HtmlParser().ParseDocument(body);
        var expected = On(path);
        var linked = page.QuerySelectorAll("a[href^='/game/']").Select(link => link.GetAttribute("href")!["/game/".Length..]).ToHashSet();
        return linked.Except(expected.Select(game => game.Id)).Select(id => $"unexpected game link {id}")
            .Concat(expected.Where(game => !linked.Contains(game.Id)).Select(game => $"missing game link {game.Id}"))
            .Concat(expected.SelectMany(game => game.Checks)
                .Where(check => page.QuerySelector(check.Selector)?.TextContent.Contains(check.Text, StringComparison.Ordinal) != true)
                .Select(check => $"{check.Selector} lacks its expected value"))
            .ToList();
    }
}

internal static class ListingWorkload
{
    public static readonly DateTimeOffset Now = new(2026, 10, 13, 23, 30, 0, TimeSpan.Zero);
    public static readonly string[] Paths = ["/", "/nhl?date=2026-10-13", "/nba?date=2026-10-13"];
    public static readonly string[] ServerSettings = ["-c", "timescaledb.telemetry_level=off", "-c", "timescaledb.max_background_workers=0"];
    private const int HistoryDays = 28;
    private const int GameMinutes = 180;
    private const int PregameSnapshots = 36;
    private static readonly TimeZoneInfo EasternTime = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly string[] HistorySports = ["nhl", "nba", "mlb", "nfl"];
    private static readonly (string Status, double Hour)[] Slate =
        [("final", 13), ("final", 16), ("in_progress", 19), ("in_progress", 19), ("scheduled", 20.5), ("scheduled", 21), ("scheduled", 22)];
    private static readonly (string Sport, string Status, double Hour)[] Listed =
    [
        .. Slate.Select(slot => ("nhl", slot.Status, slot.Hour)),
        .. Slate.Select(slot => ("nba", slot.Status, slot.Hour)),
        .. new[] { Slate[0], Slate[2], Slate[4] }.Select(slot => ("mlb", slot.Status, slot.Hour))
    ];

    private const string Hypertable = """
        CREATE EXTENSION IF NOT EXISTS timescaledb;
        ALTER TABLE odds_snapshots DROP CONSTRAINT "PK_odds_snapshots";
        ALTER TABLE odds_snapshots ADD PRIMARY KEY (id, timestamp);
        DROP INDEX "IX_odds_snapshots_game_id_timestamp";
        DROP INDEX "IX_odds_snapshots_timestamp";
        SELECT create_hypertable('odds_snapshots', 'timestamp', chunk_time_interval => INTERVAL '1 day');
        CREATE INDEX idx_odds_game_time ON odds_snapshots(game_id, timestamp DESC);
        ALTER TABLE odds_snapshots SET (timescaledb.compress, timescaledb.compress_segmentby = 'game_id');
        """;

    private sealed record Seeded(Game Game, (DateTime At, double Probability)[] Snapshots);

    public static string Image() =>
        Regex.Match(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "docker-compose.yml")), @"timescaledb:\s*\n\s*image:\s*(\S+)")
            .Groups[1].Value is { Length: > 0 } image ? image : throw new InvalidOperationException("docker-compose.yml has no timescaledb image");

    public static async Task PrepareTemplateAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(Hypertable, connection);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<ListingWorkloadManifest> SeedAsync(IServiceProvider services)
    {
        var seeded = Games().ToList();
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        db.Games.AddRange(seeded.Select(entry => entry.Game));
        await db.SaveChangesAsync();
        await db.Database.OpenConnectionAsync();
        await using (var writer = await ((NpgsqlConnection)db.Database.GetDbConnection()).BeginBinaryImportAsync(
            "COPY odds_snapshots (game_id, timestamp, home_moneyline, away_moneyline, home_win_probability, away_win_probability, source, is_historical) FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var (game, snapshots) in seeded)
                foreach (var (at, probability) in snapshots)
                {
                    await writer.StartRowAsync();
                    await writer.WriteAsync(game.Id, NpgsqlDbType.Text);
                    await writer.WriteAsync(at, NpgsqlDbType.TimestampTz);
                    await writer.WriteAsync(-100, NpgsqlDbType.Integer);
                    await writer.WriteAsync(-100, NpgsqlDbType.Integer);
                    await writer.WriteAsync(probability, NpgsqlDbType.Double);
                    await writer.WriteAsync(1 - probability, NpgsqlDbType.Double);
                    await writer.WriteAsync("Kalshi", NpgsqlDbType.Text);
                    await writer.WriteAsync(false, NpgsqlDbType.Boolean);
                }
            await writer.CompleteAsync();
        }
        return new(seeded.Where(entry => entry.Game.Id.StartsWith("today-", StringComparison.Ordinal)).Select(Expect).ToList());
    }

    public static async Task<WorkloadDescription> CompressAndAnalyzeAsync(IServiceProvider services, string image)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        await db.Database.OpenConnectionAsync();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await using (var compress = new NpgsqlCommand(
            "SELECT count(compress_chunk(chunk)) FROM show_chunks('odds_snapshots', older_than => @cutoff) chunk; ANALYZE;", connection))
        {
            compress.Parameters.AddWithValue("cutoff", Now.UtcDateTime.AddDays(-7));
            await compress.ExecuteNonQueryAsync();
        }
        await using var describe = new NpgsqlCommand("""
            SELECT (SELECT extversion FROM pg_extension WHERE extname = 'timescaledb'), current_setting('timescaledb.telemetry_level'),
                current_setting('timescaledb.max_background_workers'),
                (SELECT count(*) FROM show_chunks('odds_snapshots')),
                (SELECT count(*) FROM timescaledb_information.chunks WHERE hypertable_name = 'odds_snapshots' AND is_compressed),
                (SELECT count(*) FROM odds_snapshots), (SELECT count(*) FROM odds_snapshots WHERE game_id LIKE 'today-%')
            """, connection);
        var statuses = await db.Games.GroupBy(game => game.Status).Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(group => group.Key, group => group.Count);
        await using var row = await describe.ExecuteReaderAsync();
        await row.ReadAsync();
        return new(image, row.GetString(0), row.GetString(1), row.GetString(2), row.GetInt64(3), row.GetInt64(4), statuses, row.GetInt64(5), row.GetInt64(6), Now);
    }

    private static IEnumerable<Seeded> Games()
    {
        var phase = 0;
        for (var day = 1; day <= HistoryDays; day++)
            foreach (var sport in HistorySports)
                for (var slot = 0; slot < 6; slot++)
                    yield return Create($"history-{sport}-{day:00}-{slot}", sport, "final", Eastern(-day, 13 + slot), phase++, null);
        var scheduled = 0;
        foreach (var (sport, status, hour) in Listed)
            yield return Create($"today-{sport}-{phase}", sport, status, Eastern(0, hour), phase++,
                status == "scheduled" ? .55 + .03 * scheduled++ : null);
    }

    private static Seeded Create(string id, string sport, string status, DateTime start, int phase, double? latestPregame)
    {
        var game = new Game
        {
            Id = id, Sport = sport, Status = status, SeasonYear = 2027, SeasonType = 2, ScheduledStart = start,
            HomeTeam = "Home", AwayTeam = "Away", HomeTeamAbbreviation = "HOM", AwayTeamAbbreviation = "AWY"
        };
        var pregameEnd = start < Now.UtcDateTime ? start : Now.UtcDateTime;
        var pregame = id.StartsWith("today-", StringComparison.Ordinal)
            ? Enumerable.Range(0, PregameSnapshots).Select(i => (pregameEnd.AddHours(-2 * (PregameSnapshots - i)),
                i == PregameSnapshots - 1 && latestPregame is { } latest ? latest : .5))
            : [];
        var minutes = status switch
        {
            "final" => GameMinutes,
            "in_progress" => (int)(Now.UtcDateTime - start).TotalMinutes,
            _ => 0
        };
        var inGame = Enumerable.Range(0, minutes).Select(i => (start.AddMinutes(i), InGame(i, phase)));
        return new(game, [.. pregame, .. inGame]);
    }

    private static double InGame(int minute, int phase)
    {
        var progress = (double)minute / (GameMinutes - 1);
        var lean = phase % 2 == 0 ? .47 : -.47;
        return Math.Clamp(.5 + lean * progress + .3 * (1 - progress) * Math.Sin(minute / 7.0 + phase), .02, .98);
    }

    private static DateTime Eastern(int day, double hour) =>
        TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, 13).AddDays(day).AddHours(hour), EasternTime);

    private static ExpectedGame Expect(Seeded entry)
    {
        var (game, snapshots) = entry;
        (string, string)[] checks = game.Status == "scheduled"
            ? [($"#probbar-{game.Id}", $"HOM {Math.Round(snapshots[^1].Probability * 100).ToString(CultureInfo.InvariantCulture)}%"),
                ($"#excitement-{game.Id}", "Hype unavailable")]
            : [($"#excitement-{game.Id}", Math.Round(ExcitementScore.Calculate(game.Sport, ListModule.OfSeq(snapshots.Select(s => s.Probability)))
                ?? throw new InvalidOperationException($"{game.Id} has no excitement score")).ToString(CultureInfo.InvariantCulture))];
        return new(game.Id, game.Sport, game.Status, checks);
    }
}
