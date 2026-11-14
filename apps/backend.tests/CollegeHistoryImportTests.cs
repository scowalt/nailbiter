using System.Data.Common;
using backend.Services;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace backend.tests;

public sealed class CollegeHistoryImportTests
{
    private const string Stopped = "College history import stopped at event {Event} after {Committed} committed games; rerun to resume";
    private static readonly TimeSpan ProWaitCeiling = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task Pro_writer_waits_at_most_one_game_on_the_fence_during_a_150_game_import()
    {
        await using var database = await CollegeHistoryDatabase.StartAsync(150);
        await using var probe = ProWriterProbe.Start(database.ConnectionString);

        var import = await database.ImportAsync();
        await probe.StopAsync();

        Assert.True(probe.MaxWait < ProWaitCeiling, $"a pro writer waited {probe.MaxWait.TotalMilliseconds:F0} ms on the import's fence");
        Assert.Equal((150, true), (import.Games, import.Complete));
        Assert.Equal(database.Provider.Snapshots, await database.CountAsync("odds_snapshots"));
        Assert.Contains(probe.Samples, sample => sample.Snapshots > 0 && sample.Snapshots < database.Provider.Snapshots);
        Assert.Empty(database.Provider.Unexpected);
    }

    [Fact]
    public async Task Import_holds_the_fence_for_every_write_and_for_no_provider_request()
    {
        await using var database = await CollegeHistoryDatabase.StartAsync(4);
        database.Provider.FenceProbe = database.ConnectionString;
        var writes = new FenceAtSave(database.ConnectionString);

        await database.ImportAsync(writes);

        Assert.Equal(5, writes.Saves.Count);
        Assert.All(writes.Saves, held => Assert.True(held, "the import wrote without holding the pro-hype fence"));
        Assert.NotEmpty(database.Provider.Requests);
        Assert.Empty(database.Provider.FencedRequests);
        Assert.Empty(database.Provider.Unexpected);
    }

    [Fact]
    public async Task Live_collector_is_refused_between_import_units_and_runs_after_the_import()
    {
        await using var database = await CollegeHistoryDatabase.StartAsync(4);
        var plays = 0;
        long committedBeforeLive = -1;
        Exception? live = null;
        database.Provider.BeforePlays = async _ =>
        {
            if (Interlocked.Increment(ref plays) != 3) return;
            committedBeforeLive = await database.CountAsync("games") - 1;
            live = await Record.ExceptionAsync(database.CollectLiveAsync);
        };

        var import = await database.ImportAsync();

        Assert.Equal(2, committedBeforeLive);
        Assert.Equal("Another college collector/import is running", Assert.IsType<InvalidOperationException>(live).Message);
        Assert.Equal((4, true), (import.Games, import.Complete));
        await database.CollectLiveAsync();
    }

    [Fact]
    public async Task Import_stopped_by_a_failed_save_keeps_earlier_games_and_a_rerun_matches_a_clean_import()
    {
        await using var clean = await CollegeHistoryDatabase.StartAsync(6);
        await clean.ImportAsync();
        await using var database = await CollegeHistoryDatabase.StartAsync(6);
        var logs = new Recorder();
        var plays = 0;
        IAsyncDisposable? fence = null;
        database.Provider.BeforePlays = async _ =>
        {
            if (Interlocked.Increment(ref plays) == 4) fence = await database.HoldFenceAsync();
        };

        CollegeHistoryImport stopped;
        await using (var db = CollegeHistoryDatabase.Context(database.LockTimeoutConnectionString))
            stopped = await database.Provider.Service(db, logs).ImportHistoryAsync(true, CancellationToken.None);
        await fence!.DisposeAsync();

        Assert.Equal((3, false), (stopped.Games, stopped.Complete));
        Assert.Equal(3, (await database.RowsAsync("SELECT DISTINCT game_id FROM odds_snapshots")).Count);
        Assert.Equal(3, await database.CountAsync("games") - 1);
        var failure = Assert.Single(logs.Entries, entry => entry.Level >= LogLevel.Warning);
        Assert.Equal((LogLevel.Error, Stopped, (int?)3), (failure.Level, failure.Template, failure.Committed));
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, Assert.IsType<PostgresException>(failure.Exception).SqlState);

        database.Provider.BeforePlays = null;
        var rerun = await database.ImportAsync();

        Assert.Equal((6, true), (rerun.Games, rerun.Complete));
        foreach (var table in Tables)
            Assert.Equal(await clean.RowsAsync(table), await database.RowsAsync(table));
    }

    [Fact]
    public async Task Caller_cancellation_mid_import_is_rethrown_without_a_log_and_keeps_committed_games()
    {
        await using var database = await CollegeHistoryDatabase.StartAsync(4);
        var logs = new Recorder();
        using var cancel = new CancellationTokenSource();
        var plays = 0;
        database.Provider.BeforePlays = async _ =>
        {
            if (Interlocked.Increment(ref plays) == 3) await cancel.CancelAsync();
        };

        await using (var db = database.Context())
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => database.Provider.Service(db, logs).ImportHistoryAsync(true, cancel.Token));

        Assert.DoesNotContain(logs.Entries, entry => entry.Level >= LogLevel.Warning);
        Assert.Equal(2, await database.CountAsync("games") - 1);
        await database.CollectLiveAsync();
    }

    [Fact]
    public async Task Event_conflicting_with_a_stored_game_stops_the_import_with_a_warning_after_the_games_before_it()
    {
        await using var database = await CollegeHistoryDatabase.StartAsync(4);
        var logs = new Recorder();
        var conflicting = database.Provider.Games[2];
        await using (var seed = database.Context())
        {
            seed.Games.Add(new backend.Models.Game
            {
                Id = conflicting.Id, Sport = "nfl", Status = "final", SeasonYear = 2026, HomeTeam = conflicting.Home, AwayTeam = conflicting.Away,
                HomeTeamAbbreviation = conflicting.Home, AwayTeamAbbreviation = conflicting.Away,
                HomeTeamId = conflicting.HomeId, AwayTeamId = conflicting.AwayId, ScheduledStart = conflicting.Kickoff
            });
            await seed.SaveChangesAsync();
        }

        CollegeHistoryImport import;
        await using (var db = database.Context())
            import = await database.Provider.Service(db, logs).ImportHistoryAsync(true, CancellationToken.None);

        Assert.Equal((2, false), (import.Games, import.Complete));
        Assert.Equal(2, (await database.RowsAsync("SELECT DISTINCT game_id FROM odds_snapshots")).Count);
        var failure = Assert.Single(logs.Entries, entry => entry.Level >= LogLevel.Warning);
        Assert.Equal((LogLevel.Warning, Stopped, (int?)2), (failure.Level, failure.Template, failure.Committed));
        Assert.IsType<InvalidDataException>(failure.Exception);
    }

    private static readonly string[] Tables =
    [
        "SELECT g::text FROM games g ORDER BY id",
        "SELECT concat_ws(' ', game_id, timestamp, home_win_probability, away_win_probability, is_historical, source) FROM odds_snapshots ORDER BY game_id, timestamp",
        "SELECT p::text FROM pregame_odds p ORDER BY game_id",
        "SELECT c::text FROM college_imports c",
        "SELECT t::text FROM college_teams t ORDER BY espn_id"
    ];

    internal sealed record Logged(LogLevel Level, string? Template, int? Committed, Exception? Exception);

    internal sealed class Recorder : ILoggerFactory, ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentQueue<Logged> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(Entries);

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }

        private sealed class Logger(System.Collections.Concurrent.ConcurrentQueue<Logged> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var values = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
                entries.Enqueue(new Logged(logLevel,
                    values.FirstOrDefault(v => v.Key == "{OriginalFormat}").Value as string,
                    values.FirstOrDefault(v => v.Key == "Committed").Value as int?,
                    exception));
            }
        }
    }

    private sealed class FenceAtSave(string connectionString) : SaveChangesInterceptor
    {
        public List<bool> Saves { get; } = [];

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Saves.Add(await CollegeHistoryProvider.FenceHeldAsync(connectionString, cancellationToken));
            return result;
        }
    }
}
