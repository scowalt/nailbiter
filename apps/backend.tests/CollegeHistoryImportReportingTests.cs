using System.Net;
using backend.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Time.Testing;
using Sentry.AspNetCore;
using Xunit;
using static backend.tests.DatabaseFailureReportingTests;

namespace backend.tests;

[Collection(nameof(SentryGlobalHubCollection))]
public sealed class CollegeHistoryImportReportingTests
{
    [Fact]
    public async Task Own_code_fault_in_a_game_is_sent_once_at_error_and_stops_the_cli_after_keeping_earlier_games()
    {
        await using var database = await CollegeHistoryDatabase.StartAsync(4);
        var plays = 0;
        database.Provider.BeforePlays = _ => Interlocked.Increment(ref plays) == 3
            ? throw new InvalidOperationException("Synthetic fault below the plays fetch")
            : Task.CompletedTask;

        var (exitCode, sent) = await ImportAsync(database);

        Assert.Equal(1, exitCode);
        var reported = Assert.Single(sent);
        Assert.Equal(("backend.Services.CollegeFootballService", "error"), (reported.Logger, reported.Level));
        Assert.Contains(reported.Exceptions, e => e.Type == typeof(InvalidOperationException).FullName);
        Assert.Equal(2, (await database.RowsAsync("SELECT id FROM games WHERE sport = 'cfb'")).Count);
        Assert.Equal(3, plays);
    }

    [Fact]
    public async Task Kalshi_outage_for_one_game_keeps_the_game_without_history_and_a_rerun_fills_it_in()
    {
        await using var database = await CollegeHistoryDatabase.StartAsync(4);
        var failing = database.Provider.Games[2];
        database.Provider.Status = uri => uri.AbsolutePath.EndsWith("/candlesticks", StringComparison.Ordinal)
            && uri.AbsolutePath.Contains(failing.EventTicker, StringComparison.Ordinal) ? HttpStatusCode.ServiceUnavailable : null;

        var (exitCode, sent) = await ImportAsync(database);

        Assert.Equal(0, exitCode);
        var reported = Assert.Single(sent);
        Assert.Equal(("backend.Services.CollegeKalshiClient", "error"), (reported.Logger, reported.Level));
        Assert.Contains(reported.Exceptions, e => e.Type == typeof(HttpRequestException).FullName);
        Assert.Equal(4, (await database.RowsAsync("SELECT id FROM games WHERE sport = 'cfb'")).Count);
        Assert.Equal(0, await SnapshotsAsync(database, failing.Id));
        Assert.Equal(3L * database.Provider.Minutes, await database.CountAsync("odds_snapshots"));

        database.Provider.Status = null;
        var (rerunExitCode, rerunSent) = await ImportAsync(database);

        Assert.Equal(0, rerunExitCode);
        Assert.Empty(rerunSent);
        Assert.Equal(database.Provider.Minutes, await SnapshotsAsync(database, failing.Id));
        Assert.Equal(database.Provider.Snapshots, await database.CountAsync("odds_snapshots"));
    }

    [Fact]
    public async Task Game_whose_save_fails_is_sent_once_at_error()
    {
        await using var database = await CollegeHistoryDatabase.StartAsync(4);
        var plays = 0;
        IAsyncDisposable? held = null;
        database.Provider.BeforePlays = async _ =>
        {
            if (Interlocked.Increment(ref plays) == 3) held = await database.HoldAsync("LOCK TABLE odds_snapshots IN SHARE MODE");
        };

        var (exitCode, sent) = await ImportAsync(database, database.LockTimeoutConnectionString);
        await held!.DisposeAsync();

        Assert.Equal(1, exitCode);
        var reported = Assert.Single(sent);
        Assert.Equal(("backend.Services.CollegeFootballService", "error"), (reported.Logger, reported.Level));
        Assert.Contains(reported.Exceptions, e => e.Type == typeof(Microsoft.EntityFrameworkCore.DbUpdateException).FullName);
        Assert.Contains(reported.Exceptions, e => e.Value.StartsWith(Npgsql.PostgresErrorCodes.LockNotAvailable + ":", StringComparison.Ordinal));
        Assert.Equal(2, (await database.RowsAsync("SELECT id FROM games WHERE sport = 'cfb'")).Count);
    }

    [Fact]
    public async Task Import_whose_session_is_terminated_is_sent_once_at_error_and_exits_nonzero()
    {
        await using var database = await CollegeHistoryDatabase.StartAsync(4);
        var plays = 0;
        database.Provider.BeforePlays = async _ =>
        {
            if (Interlocked.Increment(ref plays) == 3)
                await database.RowsAsync("SELECT pg_terminate_backend(pid)::text FROM pg_locks WHERE locktype = 'advisory' AND granted");
        };

        var (exitCode, sent) = await ImportAsync(database);

        Assert.Equal(database.Provider.Games.Take(2).Select(game => game.Id).Order(),
            (await database.RowsAsync("SELECT id FROM games WHERE sport = 'cfb'")).Order());
        Assert.Equal(1, exitCode);
        var reported = Assert.Single(sent);
        Assert.Equal(("backend.Services.CollegeFootballService", "error"), (reported.Logger, reported.Level));
        Assert.Contains(reported.Exceptions, e => e.Value.StartsWith(Npgsql.PostgresErrorCodes.AdminShutdown + ":", StringComparison.Ordinal));
        Assert.Equal(0, await database.AdvisoryLocksAsync());
    }

    [Fact]
    public async Task Game_conflicting_with_a_stored_game_is_sent_once_at_error_and_stops_the_cli()
    {
        await using var database = await CollegeHistoryDatabase.StartAsync(4);
        var conflicting = database.Provider.Games[2];
        await using (var seed = database.Context())
        {
            seed.Games.Add(new Game
            {
                Id = conflicting.Id, Sport = "nfl", Status = "final", SeasonYear = 2026, HomeTeam = conflicting.Home, AwayTeam = conflicting.Away,
                HomeTeamAbbreviation = conflicting.Home, AwayTeamAbbreviation = conflicting.Away,
                HomeTeamId = conflicting.HomeId, AwayTeamId = conflicting.AwayId, ScheduledStart = conflicting.Kickoff
            });
            await seed.SaveChangesAsync();
        }

        var (exitCode, sent) = await ImportAsync(database);

        Assert.Equal(1, exitCode);
        var reported = Assert.Single(sent);
        Assert.Equal(("backend.Services.CollegeFootballService", "error"), (reported.Logger, reported.Level));
        Assert.Contains(reported.Exceptions, e => e.Value == $"College identity conflict for event {conflicting.Id}");
        Assert.Equal(2, (await database.RowsAsync("SELECT id FROM games WHERE sport = 'cfb'")).Count);
    }

    [Fact]
    public async Task Clean_import_exits_zero_and_sends_nothing()
    {
        await using var database = await CollegeHistoryDatabase.StartAsync(4);

        var (exitCode, sent) = await ImportAsync(database);

        Assert.Equal(0, exitCode);
        Assert.Empty(sent);
        Assert.Equal(4, (await database.RowsAsync("SELECT id FROM games WHERE sport = 'cfb'")).Count);
    }

    private static async Task<long> SnapshotsAsync(CollegeHistoryDatabase database, string gameId) =>
        (await database.RowsAsync($"SELECT game_id FROM odds_snapshots WHERE game_id = '{gameId}'")).Count;

    private static Task<(int ExitCode, SentEvent[] Sent)> ImportAsync(CollegeHistoryDatabase database) => ImportAsync(database, database.ConnectionString);

    private static async Task<(int ExitCode, SentEvent[] Sent)> ImportAsync(CollegeHistoryDatabase database, string connectionString)
    {
        var transport = new SentTransport();
        var exitCode = await ProgramEntryPoint.RunAsync(
            [
                "--environment=Production",
                $"--ConnectionStrings:DefaultConnection={connectionString}",
                "--Sentry:Dsn=https://public@sentry.invalid/1",
                "--import-college-history", "--apply"
            ],
            services =>
            {
                services.PostConfigure<SentryAspNetCoreOptions>(options => options.Transport = transport);
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FakeTimeProvider(CollegeHistoryProvider.Now));
                services.ConfigureAll<HttpClientFactoryOptions>(options =>
                    options.HttpMessageHandlerBuilderActions.Add(handlers => handlers.PrimaryHandler = new Forwarding(database.Provider)));
                services.ConfigureAll<HttpStandardResilienceOptions>(options => options.Retry.Delay = TimeSpan.Zero);
            });
        return (exitCode, transport.Events.ToArray());
    }

    private sealed class Forwarding(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing) { }
    }
}
