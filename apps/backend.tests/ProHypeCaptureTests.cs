using System.Data.Common;
using System.Text.Json;
using backend.Data;
using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace backend.tests;

public sealed class ProHypeCaptureTests : IAsyncLifetime
{
    private static readonly TimeSpan BackgroundDeadline = TimeSpan.FromSeconds(60);
    private readonly PostgreSqlContainer database = IsolatedPostgreSql.CreateContainer();
    private readonly AppFactory entry = new();
    private readonly Clock clock = new();
    private readonly NoNetwork network = new();
    private readonly TransactionGate gate = new();
    private readonly FenceGate fence = new();
    private WebApplicationFactory<Program>? app;
    private DbContextOptions<ExcitementDbContext> options = null!;

    private sealed class Clock : TimeProvider
    {
        private readonly FakeTimeProvider provider = new(new DateTimeOffset(ProHypeTests.Now));
        public TaskCompletionSource CycleFinished { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override DateTimeOffset GetUtcNow() => provider.GetUtcNow();
        public void Advance(TimeSpan elapsed)
        {
            CycleFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
            provider.Advance(elapsed);
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = provider.CreateTimer(callback, state, dueTime, period);
            if (dueTime == TimeSpan.FromSeconds(30)) CycleFinished.TrySetResult();
            return timer;
        }
    }

    private sealed class NoNetwork : IHttpMessageHandlerBuilderFilter
    {
        public int Requests;
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);
            builder.PrimaryHandler = new RejectHandler(this);
        };
        private sealed class RejectHandler(NoNetwork owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Interlocked.Increment(ref owner.Requests);
                throw new InvalidOperationException("Unexpected provider request in capture tests: " + request.RequestUri);
            }
        }
    }

    private sealed class TransactionGate : DbTransactionInterceptor
    {
        private readonly AsyncTestGate gate = new();
        private CancellationTokenSource? cancelOnCommit;
        public void CancelOnCommit(CancellationTokenSource cancellation) => cancelOnCommit = cancellation;
        public TaskCompletionSource Entered => gate.Entered;
        public void Arm() => gate.Arm();
        public void Release() => gate.Release();
        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken ct = default)
        {
            await gate.EnterAsync(ct);
            return result;
        }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken ct = default)
        {
            Interlocked.Exchange(ref cancelOnCommit, null)?.Cancel();
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FenceGate : DbCommandInterceptor
    {
        private readonly AsyncTestGate gate = new();
        public TaskCompletionSource Entered => gate.Entered;
        public void Arm() => gate.Arm();
        public void Release() => gate.Release();
        public override async ValueTask<InterceptionResult> DataReaderClosingAsync(DbCommand command,
            DataReaderClosingEventData eventData, InterceptionResult result)
        {
            if (command.CommandText.Contains("pro_hype_revision", StringComparison.Ordinal)) await gate.EnterAsync(default);
            return result;
        }
    }

    public async Task InitializeAsync()
    {
        await database.StartAsync();
        options = new DbContextOptionsBuilder<ExcitementDbContext>().UseNpgsql(database.GetConnectionString()).Options;
        await Change(async db =>
        {
            await TestSchema.CreateAsync(db.Database);
            await db.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "035_ProHypeLifecycle.sql")));
        });
    }

    private async Task Change(Func<ExcitementDbContext, Task> action)
    {
        await using var db = new ExcitementDbContext(options);
        await action(db);
        await db.SaveChangesAsync();
    }

    private async Task<Game> Seed(string sport = "nhl", string id = "capture-game", DateTime? start = null)
    {
        var game = ProHypeTests.Game(sport);
        game.Id = id;
        game.WeekNumber = 1;
        game.ScheduledStart = start ?? ProHypeTests.Now.AddDays(6);
        game.ProEvidenceJson = JsonSerializer.Serialize(ProHypeContinuityInputs.Evidence(game));
        await Change(db =>
        {
            db.Games.Add(game);
            db.PregameOdds.Add(ProHypeTests.Odds(game));
            db.TeamRatings.AddRange(ProHypeTests.Ranks(game));
            foreach (var team in new[] { game.HomeTeamAbbreviation, game.AwayTeamAbbreviation })
                db.TeamRatings.Add(new TeamRating { Sport = sport, TeamAbbreviation = team, Season = game.SeasonYear!.Value,
                    SourceSeason = game.SeasonYear, Source = "kalshi", PlayoffProbability = .5, FetchedAt = ProHypeTests.Now });
            return Task.CompletedTask;
        });
        return game;
    }

    private void Start(bool captureEnabled = true)
    {
        app = entry.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("EnableProHypeCapture", captureEnabled.ToString());
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EnableProHypeCapture"] = captureEnabled.ToString()
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDbContextOptionsConfiguration<ExcitementDbContext>>();
                services.RemoveAll<DbContextOptions<ExcitementDbContext>>();
                services.AddDbContext<ExcitementDbContext>(o => o.UseNpgsql(database.GetConnectionString()).AddInterceptors(gate, fence));
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton<IHttpMessageHandlerBuilderFilter>(network);
            });
        });
        _ = app.Services;
    }

    private async Task<ProHypeResolution> Resolve(string id)
    {
        using var scope = app!.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IProHypeLifecycle>().ResolveAsync(id);
    }

    private async Task Capture(CancellationToken ct = default)
    {
        using var scope = app!.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IProHypeLifecycle>().CaptureAsync(ct);
    }

    [Theory]
    [InlineData("nfl", 54, .8)]
    [InlineData("nba", 70, 6.384034163378589)]
    [InlineData("nhl", 58, 6.3081393271862405)]
    [InlineData("mlb", 64, 4.5504108020051035)]
    public async Task Registered_background_capture_precedes_any_viewer_and_survives_expiry_and_restart(string sport, int expected, double stakes)
    {
        var game = await Seed(sport);
        Start();
        await clock.CycleFinished.Task.WaitAsync(BackgroundDeadline);
        await app!.DisposeAsync();
        clock.Advance(TimeSpan.FromDays(3));
        Start(captureEnabled: false);
        var firstRead = await Resolve(game.Id);
        Assert.Equal(expected, Math.Round(firstRead.Total!.Value));
        Assert.Equal(stakes, firstRead.Components.StakesScore, 10);
        Assert.InRange(firstRead.Components.QualityScore, 96, 97);
        Assert.Equal(100, firstRead.Components.ClosenessScore);
        Assert.Equal(ProHypeTests.Now, firstRead.EvaluatedAt);
        Assert.Equal(0, network.Requests);
    }

    [Theory]
    [InlineData("nfl")]
    [InlineData("nba")]
    [InlineData("nhl")]
    [InlineData("mlb")]
    public async Task Ordinary_background_cycles_keep_a_whole_score_during_result_lag_and_replace_every_component_on_recovery(string sport)
    {
        var game = await Seed(sport);
        Start();
        await clock.CycleFinished.Task.WaitAsync(BackgroundDeadline);
        await Change(db =>
        {
            db.Games.Add(new Game { Id = "unrelated-result", Sport = sport, SeasonYear = game.SeasonYear, SeasonType = 2,
                HomeTeam = "Other", AwayTeam = "Other", HomeTeamAbbreviation = "A", AwayTeamAbbreviation = "B",
                Status = "final", ScheduledStart = ProHypeTests.Now.AddHours(-2) });
            return Task.CompletedTask;
        });
        using (var scope = app!.Services.CreateScope())
        {
            var current = await scope.ServiceProvider.GetRequiredService<HypeScoreService>().GetHypeComponentsAsync(game, null);
            Assert.False(current.StakesAvailable);
            Assert.True(current.QualityAvailable);
            Assert.True(current.ClosenessAvailable);
        }
        var captured = await Resolve(game.Id);
        Assert.NotNull(captured.Total);
        Assert.Equal(ProHypeTests.Now, captured.EvaluatedAt);
        clock.Advance(TimeSpan.FromMinutes(2));
        await clock.CycleFinished.Task.WaitAsync(BackgroundDeadline);
        var partialCycle = await Resolve(game.Id);
        Assert.Equal(captured.Total, partialCycle.Total);
        Assert.Equal(captured.Components.StakesScore, partialCycle.Components.StakesScore);
        Assert.Equal(captured.Components.QualityScore, partialCycle.Components.QualityScore);
        Assert.Equal(captured.Components.ClosenessScore, partialCycle.Components.ClosenessScore);
        Assert.Equal(captured.EvaluatedAt, partialCycle.EvaluatedAt);
        var recoveredAt = clock.GetUtcNow().UtcDateTime.AddMinutes(2);
        await RefreshInputs(game.Id, recoveredAt, ["unrelated-result"]);
        clock.Advance(TimeSpan.FromMinutes(2));
        await clock.CycleFinished.Task.WaitAsync(BackgroundDeadline);
        await app!.DisposeAsync();
        clock.Advance(TimeSpan.FromDays(3));
        Start(captureEnabled: false);
        var recovered = await Resolve(game.Id);
        Assert.NotNull(recovered.Total);
        Assert.NotEqual(captured.Total, recovered.Total);
        Assert.NotEqual(captured.Components.StakesScore, recovered.Components.StakesScore);
        Assert.NotEqual(captured.Components.QualityScore, recovered.Components.QualityScore);
        Assert.Equal(60, recovered.Components.ClosenessScore, 8);
        Assert.Equal(recoveredAt, recovered.EvaluatedAt);
        Assert.Equal(0, network.Requests);
    }

    [Fact]
    public async Task A_reschedule_during_capture_cannot_expand_the_seven_day_horizon()
    {
        var game = await Seed("nfl");
        Start(captureEnabled: false);
        gate.Arm();
        var capture = Capture();
        await gate.Entered.Task.WaitAsync(BackgroundDeadline);
        try
        {
            await Change(async db =>
            {
                var row = await db.Games.SingleAsync();
                row.ScheduledStart = ProHypeTests.Now.AddDays(8);
                row.ProEvidenceJson = JsonSerializer.Serialize(ProHypeContinuityInputs.Evidence(row));
                (await db.PregameOdds.SingleAsync()).ApplicableStart = row.ScheduledStart;
            });
            gate.Release();
            await capture;
            await app!.DisposeAsync();
            clock.Advance(TimeSpan.FromHours(2));
            Start(captureEnabled: false);
            Assert.Null((await Resolve(game.Id)).Total);
            Assert.Equal(0, network.Requests);
        }
        finally { gate.Release(); }
    }

    [Theory]
    [InlineData(-10, true)]
    [InlineData(0, false)]
    [InlineData(10, false)]
    public async Task Background_capture_uses_the_existing_exclusive_seven_day_horizon(long offsetTicks, bool captured)
    {
        var game = await Seed("nfl", start: ProHypeTests.Now.AddDays(7).AddTicks(offsetTicks));
        Start();
        await clock.CycleFinished.Task.WaitAsync(BackgroundDeadline);
        await app!.DisposeAsync();
        clock.Advance(TimeSpan.FromHours(2));
        Start(captureEnabled: false);
        var firstRead = await Resolve(game.Id);
        if (captured) Assert.Equal(54, Math.Round(firstRead.Total!.Value));
        else Assert.Null(firstRead.Total);
        Assert.Equal(0, network.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Capture_rechecks_kickoff_and_confirmed_early_play_after_enumeration(bool earlyPlay)
    {
        var game = await Seed("nfl");
        Start(captureEnabled: false);
        gate.Arm();
        var capture = Capture();
        await gate.Entered.Task.WaitAsync(BackgroundDeadline);
        try
        {
            if (earlyPlay) await Change(async db => (await db.Games.SingleAsync()).ActualStart = ProHypeTests.Now);
            else clock.Advance(TimeSpan.FromDays(6));
            gate.Release();
            await capture;
            await app!.DisposeAsync();
            Start(captureEnabled: false);
            Assert.Null((await Resolve(game.Id)).Total);
            Assert.Equal(0, network.Requests);
        }
        finally { gate.Release(); }
    }

    [Theory]
    [InlineData("nfl")]
    [InlineData("nhl")]
    public async Task The_last_pregame_score_stays_on_the_live_home_card_after_the_cutoff_clears_pregame_hype(string sport)
    {
        var game = await Seed(sport, start: ProHypeTests.Now.AddHours(1));
        Start(captureEnabled: false);
        await Capture();
        var pregame = (await Resolve(game.Id)).Total;
        Assert.NotNull(pregame);
        clock.Advance(TimeSpan.FromHours(2));
        Assert.Null((await Resolve(game.Id)).Total);
        await Change(async db =>
        {
            var live = await db.Games.SingleAsync(g => g.Id == game.Id);
            live.Status = "in_progress";
            live.ActualStart = ProHypeTests.Now.AddHours(1);
        });
        await Capture();
        Assert.Null((await Resolve(game.Id)).Total);

        var html = await app!.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") }).GetStringAsync("/");
        using var page = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
        Assert.NotNull(page.QuerySelector($"#home-section-live a[href='/game/{game.Id}']"));
        Assert.Equal($"Hype{Math.Round(pregame.Value)}", page.QuerySelector($"#hype-{game.Id}")!.TextContent.Replace(" ", ""));
        Assert.Equal(0, network.Requests);
    }

    [Fact]
    public async Task An_incomplete_first_background_cycle_preserves_independent_metrics_but_never_saves_a_total()
    {
        var game = await Seed("nfl");
        await Change(async db => (await db.TeamRatings.FirstAsync(r => r.PowerRank != null)).PowerRank = null);
        Start();
        await clock.CycleFinished.Task.WaitAsync(BackgroundDeadline);
        var firstRead = await Resolve(game.Id);
        Assert.Null(firstRead.Total);
        Assert.True(firstRead.Components.StakesAvailable);
        Assert.False(firstRead.Components.QualityAvailable);
        Assert.True(firstRead.Components.ClosenessAvailable);
        await app!.DisposeAsync();
        clock.Advance(TimeSpan.FromDays(3));
        Start(captureEnabled: false);
        Assert.Null((await Resolve(game.Id)).Total);
        Assert.Equal(0, network.Requests);
    }

    [Fact]
    public async Task Background_capture_processes_supporting_rejection_even_when_source_values_recover_between_cycles()
    {
        var game = await Seed("nfl");
        Start();
        await clock.CycleFinished.Task.WaitAsync(BackgroundDeadline);
        await Change(async db => (await db.PregameOdds.SingleAsync()).SourceSeason = null);
        await Change(async db => (await db.PregameOdds.SingleAsync()).SourceSeason = game.SeasonYear);
        clock.Advance(TimeSpan.FromHours(2));
        await clock.CycleFinished.Task.WaitAsync(BackgroundDeadline);
        await app!.DisposeAsync();
        Start(captureEnabled: false);
        Assert.Null((await Resolve(game.Id)).Total);
        Assert.Equal(0, network.Requests);
    }

    [Fact]
    public async Task A_reused_capture_scope_sees_new_inputs_and_does_not_freeze_qualification_before_kickoff()
    {
        var game = await Seed("nfl");
        Start(captureEnabled: false);
        using var scope = app!.Services.CreateScope();
        var lifecycle = scope.ServiceProvider.GetRequiredService<IProHypeLifecycle>();
        var data = scope.ServiceProvider.GetRequiredService<GameDataService>();
        await data.GetTeamRankInfoAsync(game.Sport, game.HomeTeamAbbreviation, game.SeasonYear!.Value, 2, CancellationToken.None);
        await lifecycle.CaptureAsync();
        var original = await Resolve(game.Id);
        clock.Advance(TimeSpan.FromMinutes(2));
        await RefreshInputs(game.Id, clock.GetUtcNow().UtcDateTime);
        await lifecycle.CaptureAsync();
        var recovered = await Resolve(game.Id);
        Assert.NotEqual(original.Total, recovered.Total);
        Assert.Equal(60, recovered.Components.ClosenessScore, 8);
        clock.Advance(TimeSpan.FromDays(6));
        await lifecycle.CaptureAsync();
        Assert.Null((await Resolve(game.Id)).Total);
        Assert.Equal(0, network.Requests);
    }

    [Fact]
    public async Task A_canceled_capture_cannot_publish_any_part_of_a_replacement()
    {
        var game = await Seed("nfl");
        Start(captureEnabled: false);
        await Capture();
        clock.Advance(TimeSpan.FromMinutes(2));
        await RefreshInputs(game.Id, clock.GetUtcNow().UtcDateTime);
        using var cancellation = new CancellationTokenSource();
        gate.CancelOnCommit(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Capture(cancellation.Token));
        await app!.DisposeAsync();
        clock.Advance(TimeSpan.FromHours(2));
        Start(captureEnabled: false);
        var retained = await Resolve(game.Id);
        Assert.Equal(54, Math.Round(retained.Total!.Value));
        Assert.Equal(96.849798, retained.Components.QualityScore, 5);
        Assert.Equal(100, retained.Components.ClosenessScore);
        Assert.Equal(ProHypeTests.Now, retained.EvaluatedAt);
        Assert.Equal(0, network.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Capture_and_a_migration_replay_that_alters_then_writes_games_both_commit(bool migrationFirst)
    {
        var game = await Seed();
        Start(captureEnabled: false);
        var replay = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "022_WorldCupOvertime.sql"));
        var write = replay.IndexOf("UPDATE games", StringComparison.Ordinal);
        await using var migration = new NpgsqlConnection(database.GetConnectionString());
        await migration.OpenAsync();
        Task capture;
        Task migrate;
        if (migrationFirst)
        {
            gate.Arm();
            capture = Capture();
            await gate.Entered.Task.WaitAsync(BackgroundDeadline);
            await new NpgsqlCommand(replay[..write], migration).ExecuteNonQueryAsync();
            gate.Release();
            await BlockedOnLock();
            migrate = new NpgsqlCommand(replay[write..], migration).ExecuteNonQueryAsync();
        }
        else
        {
            fence.Arm();
            capture = Capture();
            await fence.Entered.Task.WaitAsync(BackgroundDeadline);
            migrate = new NpgsqlCommand(replay, migration).ExecuteNonQueryAsync();
            await BlockedOnLock();
            fence.Release();
        }
        await Task.WhenAll(capture, migrate).WaitAsync(TimeSpan.FromSeconds(30));
        await using var db = new ExcitementDbContext(options);
        Assert.NotNull((await db.ProHypeSnapshots.SingleAsync(s => s.GameId == game.Id)).SnapshotJson);
    }

    private async Task BlockedOnLock()
    {
        await using var observer = new NpgsqlConnection(database.GetConnectionString());
        await observer.OpenAsync();
        await using var waiting = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_locks WHERE NOT granted)", observer);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!(bool)(await waiting.ExecuteScalarAsync())!)
        {
            Assert.True(DateTime.UtcNow < deadline, "no session blocked on a lock");
            await Task.Delay(10);
        }
    }

    private async Task RefreshInputs(string id, DateTime observedAt, string[]? completed = null) => await Change(async db =>
    {
        var row = await db.Games.SingleAsync(g => g.Id == id);
        var evidence = ProGameEvidence.Parse(row.ProEvidenceJson)!;
        evidence.ObservedAt = observedAt;
        evidence.CompletedGameIds = completed ?? [];
        evidence.HomeProgress.Completed = evidence.AwayProgress.Completed = 4;
        ProHypeContinuityInputs.RefreshLeverage(row, evidence, .25);
        row.ProEvidenceJson = JsonSerializer.Serialize(evidence);
        foreach (var rating in await db.TeamRatings.ToArrayAsync())
        {
            rating.FetchedAt = observedAt;
            if (rating.PowerRank.HasValue) rating.PowerRank += 5;
            if (rating.RankPublishedAt.HasValue) rating.RankPublishedAt = observedAt;
        }
        var odds = await db.PregameOdds.SingleAsync(o => o.GameId == id);
        odds.FetchedAt = observedAt;
        odds.HomeWinProbability = .7;
        odds.AwayWinProbability = .3;
    });

    public async Task DisposeAsync()
    {
        if (app is not null) await app.DisposeAsync();
        await entry.DisposeAsync();
        await database.DisposeAsync();
    }
}
