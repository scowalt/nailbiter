using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using backend.Data;
using backend.Models;
using backend.Services;
using backend.Scoring;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Http;
using Testcontainers.PostgreSql;
using Xunit;

namespace backend.tests;

public sealed class ProHypeLifecycleTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer database = IsolatedPostgreSql.CreateContainer();
    private readonly AppFactory entry = new();
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(ProHypeTests.Now));
    private WebApplicationFactory<Program> app = null!;
    private Game game = null!;
    private readonly TransactionGate gate = new();

    private sealed class NoNetwork : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);
            builder.PrimaryHandler = new RejectHandler();
        };
        private sealed class RejectHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                throw new InvalidOperationException("Unexpected provider request in lifecycle tests: " + request.RequestUri);
        }
    }

    private sealed class TransactionGate : DbTransactionInterceptor
    {
        private readonly AsyncTestGate gate = new();
        public TaskCompletionSource Entered => gate.Entered;
        private CancellationTokenSource? cancelOnCommit;
        public void Arm() => gate.Arm();
        public void Release() => gate.Release();
        public void CancelOnCommit(CancellationTokenSource cancellation) => cancelOnCommit = cancellation;
        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken ct = default)
        {
            await gate.EnterAsync(ct);
            return result;
        }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken ct = default)
        {
            var cancellation = Interlocked.Exchange(ref cancelOnCommit, null);
            cancellation?.Cancel();
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private WebApplicationFactory<Program> CreateHost() => entry.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
    {
        services.RemoveAll<IDbContextOptionsConfiguration<ExcitementDbContext>>();
        services.RemoveAll<DbContextOptions<ExcitementDbContext>>();
        services.AddDbContext<ExcitementDbContext>(options => options.UseNpgsql(database.GetConnectionString()).AddInterceptors(gate));
        services.RemoveAll<TimeProvider>();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IHttpMessageHandlerBuilderFilter, NoNetwork>();
    }));

    public async Task InitializeAsync()
    {
        await database.StartAsync();
        app = CreateHost();
        await Change(async db =>
        {
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "035_ProHypeLifecycle.sql")));
        });
    }

    private async Task Change(Func<ExcitementDbContext, Task> action)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        await action(db);
        await db.SaveChangesAsync();
    }

    private async Task Seed(string sport = "nhl")
    {
        game = ProHypeTests.Game(sport);
        game.ScheduledStart = ProHypeTests.Now.AddDays(6);
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
    }

    private async Task<ProHypeResolution> Resolve(CancellationToken ct = default)
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IProHypeLifecycle>().ResolveAsync(game.Id, ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Conditional_NFL_capture_ignores_unrelated_outlook_withdrawal_and_retains_the_original_scenarios_during_age_gaps(bool absentAtCapture)
    {
        await Seed("nfl");
        if (absentAtCapture)
            await Change(async db =>
            {
                foreach (var row in await db.TeamRatings.Where(r => r.Source == "kalshi").ToArrayAsync())
                    row.PlayoffProbability = null;
            });
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IProHypeLifecycle>().CaptureAsync();
        var captured = await Resolve();
        Assert.Equal(54.25742943548387, captured.Total!.Value, 10);
        Assert.NotNull(captured.Components.NflPlayoffLeverage);
        Assert.Null(captured.Components.HomePlayoffOdds);
        await Change(async db =>
        {
            foreach (var row in await db.TeamRatings.Where(r => r.Source == "kalshi").ToArrayAsync())
                row.PlayoffProbability = null;
        });
        Assert.Equal(captured.Total, (await Resolve()).Total);
        clock.Advance(TimeSpan.FromHours(2));
        await Change(async db =>
        {
            var row = await db.Games.SingleAsync();
            var evidence = ProGameEvidence.Parse(row.ProEvidenceJson)!;
            evidence.ObservedAt = clock.GetUtcNow().UtcDateTime;
            row.ProEvidenceJson = JsonSerializer.Serialize(evidence);
        });
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            var current = await scope.ServiceProvider.GetRequiredService<HypeScoreService>()
                .GetHypeComponentsAsync(await db.Games.SingleAsync(), null);
            Assert.False(current.StakesAvailable);
        }
        await app.DisposeAsync();
        app = CreateHost();
        var retained = await Resolve();
        Assert.Equal(captured.Total, retained.Total);
        Assert.Equal(captured.EvaluatedAt, retained.EvaluatedAt);
        Assert.Equal(JsonSerializer.Serialize(captured.Components), JsonSerializer.Serialize(retained.Components));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejected_conditional_NFL_support_cannot_resurrect_after_an_overwritten_observation_or_restart(bool overwrite)
    {
        await Seed("nfl");
        Assert.NotNull((await Resolve()).Total);
        var original = ProGameEvidence.Parse(game.ProEvidenceJson)!.NflPlayoffLeverage;
        clock.Advance(TimeSpan.FromMinutes(1));
        await Change(async db =>
        {
            var row = await db.Games.SingleAsync();
            var evidence = ProGameEvidence.Parse(row.ProEvidenceJson)!;
            evidence.ObservedAt = clock.GetUtcNow().UtcDateTime;
            evidence.NflPlayoffLeverage!.HomeOdds = null;
            evidence.NflPlayoffLeverage.ObservedAt = clock.GetUtcNow().UtcDateTime;
            row.ProEvidenceJson = JsonSerializer.Serialize(evidence);
        });
        if (!overwrite) Assert.Null((await Resolve()).Total);
        await Change(async db =>
        {
            var row = await db.Games.SingleAsync();
            var evidence = ProGameEvidence.Parse(row.ProEvidenceJson)!;
            evidence.NflPlayoffLeverage = original;
            row.ProEvidenceJson = JsonSerializer.Serialize(evidence);
        });
        await app.DisposeAsync();
        app = CreateHost();
        Assert.Null((await Resolve()).Total);
    }

    [Fact]
    public async Task Complete_capture_survives_expiry_and_host_recreation_without_renewing_observations()
    {
        await Seed();
        var captured = await Resolve();
        Assert.Equal(64, Math.Round(captured.Total!.Value));
        clock.Advance(TimeSpan.FromDays(3));
        await app.DisposeAsync();
        app = CreateHost();
        var retained = await Resolve();
        Assert.Equal(captured.Total, retained.Total);
        Assert.Equal(captured.Components.StakesScore, retained.Components.StakesScore);
        Assert.Equal(captured.Components.QualityScore, retained.Components.QualityScore);
        Assert.Equal(captured.Components.ClosenessScore, retained.Components.ClosenessScore);
        Assert.Equal(captured.EvaluatedAt, retained.EvaluatedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Legacy_regular_season_seeding_scores_are_not_retained_under_the_qualification_only_formula(int? version)
    {
        await Seed();
        await Change(async db =>
        {
            var row = await db.Games.SingleAsync();
            var evidence = ProGameEvidence.Parse(row.ProEvidenceJson)!;
            evidence.HomeProgress.Completed = evidence.AwayProgress.Completed = 83;
            row.ProEvidenceJson = JsonSerializer.Serialize(evidence);
            foreach (var rating in await db.TeamRatings.Where(r => r.Source == "kalshi").ToArrayAsync())
                rating.PlayoffProbability = .99;
        });
        var current = await Resolve();
        Assert.Equal(1.6, current.Components.StakesScore, 10);
        await Change(async db =>
        {
            var state = await db.ProHypeSnapshots.SingleAsync();
            var legacy = JsonNode.Parse(state.SnapshotJson!)!;
            if (version.HasValue) legacy["FormulaVersion"] = version.Value;
            else legacy.AsObject().Remove("FormulaVersion");
            legacy["Components"]!["StakesScore"] = 40;
            legacy["Total"] = 77.89742943548387;
            state.SnapshotJson = legacy.ToJsonString();
        });
        clock.Advance(TimeSpan.FromHours(3));
        await app.DisposeAsync();
        app = CreateHost();
        var unavailable = await Resolve();
        Assert.Null(unavailable.Total);
        Assert.False(unavailable.Components.StakesAvailable);
        await Change(async db =>
        {
            var row = await db.Games.SingleAsync();
            var evidence = ProGameEvidence.Parse(row.ProEvidenceJson)!;
            evidence.ObservedAt = clock.GetUtcNow().UtcDateTime;
            row.ProEvidenceJson = JsonSerializer.Serialize(evidence);
            foreach (var rating in await db.TeamRatings.ToArrayAsync()) rating.FetchedAt = clock.GetUtcNow().UtcDateTime;
            (await db.PregameOdds.SingleAsync()).FetchedAt = clock.GetUtcNow().UtcDateTime;
        });
        var recovered = await Resolve();
        Assert.Equal(64.45742943548387, recovered.Total!.Value, 10);
        Assert.Equal(1.6, recovered.Components.StakesScore, 10);
        clock.Advance(TimeSpan.FromHours(2));
        await app.DisposeAsync();
        app = CreateHost();
        var retained = await Resolve();
        Assert.Equal(recovered.Total, retained.Total);
        Assert.Equal(recovered.EvaluatedAt, retained.EvaluatedAt);
        Assert.Equal(JsonSerializer.Serialize(recovered.Components), JsonSerializer.Serialize(retained.Components));
    }

    [Theory]
    [InlineData("odds")]
    [InlineData("ranking")]
    [InlineData("competition")]
    public async Task Legacy_formula_recalculation_cannot_skip_a_pending_rejected_source_observation(string source)
    {
        await Seed("nba");
        Assert.NotNull((await Resolve()).Total);
        await Change(async db =>
        {
            var state = await db.ProHypeSnapshots.SingleAsync();
            var saved = JsonNode.Parse(state.SnapshotJson!)!;
            saved.AsObject().Remove("FormulaVersion");
            var competition = saved["SupportingEvidence"]!.AsArray().Single(r => r!["Kind"]!.GetValue<string>() == "competition")!;
            competition["Revision"] = ProEvidenceReference.Fingerprint(ProGameEvidence.Parse((await db.Games.SingleAsync()).ProEvidenceJson)!);
            state.SnapshotJson = saved.ToJsonString();
        });
        await Change(async db =>
        {
            if (source == "odds") (await db.PregameOdds.SingleAsync()).SourceSeason = null;
            else if (source == "ranking") (await db.TeamRatings.FirstAsync(r => r.PowerRank != null)).SourceSeason = null;
            else
            {
                var row = await db.Games.SingleAsync();
                var context = ProGameEvidence.Parse(row.ProEvidenceJson)!;
                context.Rejection = ProCompetitionRejection.IdentityMismatch;
                row.ProEvidenceJson = JsonSerializer.Serialize(context);
            }
        });
        await Change(async db =>
        {
            if (source == "odds") (await db.PregameOdds.SingleAsync()).SourceSeason = game.SeasonYear;
            else if (source == "ranking") (await db.TeamRatings.FirstAsync(r => r.PowerRank != null)).SourceSeason = game.SeasonYear;
            else (await db.Games.SingleAsync()).ProEvidenceJson = game.ProEvidenceJson;
        });
        await app.DisposeAsync();
        app = CreateHost();
        Assert.Null((await Resolve()).Total);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_raw_competition_revocations_still_block_a_new_formula_candidate(bool wholeSignature)
    {
        await Seed("nba");
        Assert.NotNull((await Resolve()).Total);
        await Change(async db =>
        {
            var state = await db.ProHypeSnapshots.SingleAsync();
            var saved = JsonNode.Parse(state.SnapshotJson!)!;
            var support = saved["SupportingEvidence"]!.AsArray();
            var competition = support.Single(r => r!["Kind"]!.GetValue<string>() == "competition")!;
            competition["Revision"] = ProEvidenceReference.Fingerprint(ProGameEvidence.Parse((await db.Games.SingleAsync()).ProEvidenceJson)!);
            var key = "competition:" + competition["Source"]!.GetValue<string>() + ":" + competition["Team"]!.GetValue<string>() + ":" + competition["Revision"]!.GetValue<string>();
            if (wholeSignature)
            {
                var evidence = support.Select(r => new { Kind = r!["Kind"]!.GetValue<string>(), Source = r["Source"]!.GetValue<string>(),
                    Team = r["Team"]!.GetValue<string>(), Revision = r["Revision"]!.GetValue<string>() })
                    .OrderBy(r => r.Kind).ThenBy(r => r.Source).ThenBy(r => r.Team);
                key = ProEvidenceReference.Fingerprint(new { Identity = saved["Identity"], Evidence = evidence });
            }
            state.SnapshotJson = null;
            state.RevokedEvidenceJson = JsonSerializer.Serialize(new[] { key });
        });
        await app.DisposeAsync();
        app = CreateHost();
        Assert.Null((await Resolve()).Total);
    }

    [Theory]
    [InlineData("nfl")]
    [InlineData("nba")]
    [InlineData("nhl")]
    [InlineData("mlb")]
    public async Task Legacy_postseason_previews_keep_their_unchanged_formula_and_retention(string sport)
    {
        await Seed(sport);
        await Change(async db =>
        {
            var row = await db.Games.SingleAsync();
            var evidence = ProGameEvidence.Parse(row.ProEvidenceJson)!;
            row.SeasonType = evidence.Phase = 3;
            evidence.Round = 4;
            evidence.HomeElimination = evidence.AwayElimination = true;
            evidence.NflPlayoffLeverage = null;
            row.ProEvidenceJson = JsonSerializer.Serialize(evidence);
        });
        var captured = await Resolve();
        Assert.Equal(100, captured.Components.StakesScore);
        await Change(async db =>
        {
            var state = await db.ProHypeSnapshots.SingleAsync();
            var legacy = JsonNode.Parse(state.SnapshotJson!)!;
            legacy.AsObject().Remove("FormulaVersion");
            state.SnapshotJson = legacy.ToJsonString();
        });
        clock.Advance(TimeSpan.FromHours(3));
        await app.DisposeAsync();
        app = CreateHost();
        var retained = await Resolve();
        Assert.Equal(captured.Total, retained.Total);
        Assert.Equal(captured.EvaluatedAt, retained.EvaluatedAt);
        Assert.Equal(JsonSerializer.Serialize(captured.Components), JsonSerializer.Serialize(retained.Components));
    }

    [Theory]
    [InlineData("nfl")]
    [InlineData("nba")]
    [InlineData("nhl")]
    [InlineData("mlb")]
    public async Task Seeding_only_metadata_changes_cannot_revoke_a_qualification_only_preview(string sport)
    {
        await Seed(sport);
        var captured = await Resolve();
        Assert.NotNull(captured.Total);
        clock.Advance(TimeSpan.FromHours(2));
        await Change(async db =>
        {
            var row = await db.Games.SingleAsync();
            var evidence = ProGameEvidence.Parse(row.ProEvidenceJson)!;
            evidence.HomeProgress.SeedingUnresolved = evidence.AwayProgress.SeedingUnresolved = null;
            evidence.HomeProgress.Reason = evidence.AwayProgress.Reason = "Unused seeding annotation withdrawn";
            row.ProEvidenceJson = JsonSerializer.Serialize(evidence);
        });
        await app.DisposeAsync();
        app = CreateHost();
        var retained = await Resolve();
        Assert.Equal(captured.Total, retained.Total);
        Assert.Equal(captured.EvaluatedAt, retained.EvaluatedAt);
        Assert.Equal(JsonSerializer.Serialize(captured.Components), JsonSerializer.Serialize(retained.Components));
    }

    [Fact]
    public async Task Supporting_quote_rejection_cannot_be_hidden_by_recovery_before_the_next_cycle()
    {
        await Seed("nfl");
        Assert.NotNull((await Resolve()).Total);
        await Change(async db =>
        {
            var odds = await db.PregameOdds.SingleAsync();
            odds.SourceSeason = null;
        });
        await Change(async db =>
        {
            var odds = await db.PregameOdds.SingleAsync();
            odds.SourceSeason = game.SeasonYear;
        });
        clock.Advance(TimeSpan.FromHours(2));
        await app.DisposeAsync();
        app = CreateHost();
        Assert.Null((await Resolve()).Total);
    }

    [Fact]
    public async Task Rejected_support_cannot_return_when_only_an_unrelated_component_recovers()
    {
        await Seed("nfl");
        Assert.NotNull((await Resolve()).Total);
        await Change(async db => (await db.PregameOdds.SingleAsync()).SourceSeason = null);
        Assert.Null((await Resolve()).Total);
        await Change(async db =>
        {
            (await db.PregameOdds.SingleAsync()).SourceSeason = game.SeasonYear;
            (await db.TeamRatings.FirstAsync(r => r.PowerRank != null)).PowerRank = 4;
        });
        Assert.Null((await Resolve()).Total);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unrelated_later_odds_withdrawal_only_revokes_after_that_source_supports_a_complete_replacement(bool delete)
    {
        await Seed("nfl");
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IProHypeLifecycle>().CaptureAsync();
        var original = await Resolve();
        Assert.Equal(54.25742943548387, original.Total!.Value, 10);
        await Change(db =>
        {
            db.Games.Add(new Game { Id = "review-result", Sport = game.Sport, SeasonYear = game.SeasonYear, SeasonType = 2,
                HomeTeam = "Other", AwayTeam = "Other", HomeTeamAbbreviation = "A", AwayTeamAbbreviation = "B",
                ScheduledStart = ProHypeTests.Now.AddHours(-2), Status = "final" });
            return Task.CompletedTask;
        });
        clock.Advance(TimeSpan.FromMinutes(1));
        await Change(async db =>
        {
            var odds = await db.PregameOdds.SingleAsync();
            odds.Source = "kalshi";
            odds.FetchedAt = clock.GetUtcNow().UtcDateTime;
            odds.HomeWinProbability = .7;
            odds.AwayWinProbability = .3;
        });
        Assert.Equal(original.Total, (await Resolve()).Total);
        clock.Advance(TimeSpan.FromMinutes(1));
        async Task Withdraw(ExcitementDbContext db)
        {
            var odds = await db.PregameOdds.SingleAsync();
            if (delete) db.PregameOdds.Remove(odds);
            else { odds.SourceSeason = null; odds.ApplicableStart = null; odds.FetchedAt = clock.GetUtcNow().UtcDateTime; }
        }
        await Change(Withdraw);
        var retained = await Resolve();
        Assert.Equal(original.Total, retained.Total);
        Assert.Equal(JsonSerializer.Serialize(original.Components), JsonSerializer.Serialize(retained.Components));
        Assert.Equal(original.EvaluatedAt, retained.EvaluatedAt);
        await app.DisposeAsync();
        app = CreateHost();
        Assert.Equal(original.Total, (await Resolve()).Total);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Change(async db =>
        {
            var odds = await db.PregameOdds.SingleOrDefaultAsync();
            if (odds is null) { odds = ProHypeTests.Odds(game, .7); db.PregameOdds.Add(odds); }
            odds.Source = "kalshi";
            odds.SourceSeason = game.SeasonYear;
            odds.ApplicableStart = game.ScheduledStart;
            odds.FetchedAt = clock.GetUtcNow().UtcDateTime;
            var row = await db.Games.SingleAsync(g => g.Id == game.Id);
            var evidence = ProGameEvidence.Parse(row.ProEvidenceJson)!;
            evidence.ObservedAt = clock.GetUtcNow().UtcDateTime;
            evidence.CompletedGameIds = ["review-result"];
            ProHypeContinuityInputs.RefreshLeverage(row, evidence);
            row.ProEvidenceJson = JsonSerializer.Serialize(evidence);
        });
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IProHypeLifecycle>().CaptureAsync();
        var replacement = await Resolve();
        Assert.Equal(46.25742943548387, replacement.Total!.Value, 10);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Change(Withdraw);
        Assert.Null((await Resolve()).Total);
        clock.Advance(TimeSpan.FromDays(3));
        await app.DisposeAsync();
        app = CreateHost();
        Assert.Null((await Resolve()).Total);
    }

    [Fact]
    public async Task Explicit_competition_correction_revokes_even_when_the_new_observation_has_matching_identity()
    {
        await Seed("nfl");
        Assert.NotNull((await Resolve()).Total);
        await Change(async db =>
        {
            var row = await db.Games.SingleAsync();
            var evidence = ProGameEvidence.Parse(row.ProEvidenceJson)!;
            evidence.Rejection = ProCompetitionRejection.ConflictingSchedule;
            evidence.ObservedAt = clock.GetUtcNow().UtcDateTime.AddMinutes(1);
            row.ProEvidenceJson = JsonSerializer.Serialize(evidence);
        });
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null((await Resolve()).Total);
    }

    [Theory]
    [InlineData("nfl")]
    [InlineData("nba")]
    [InlineData("nhl")]
    [InlineData("mlb")]
    public async Task Completed_result_lag_keeps_the_whole_score_and_new_complete_evidence_replaces_it(string sport)
    {
        await Seed(sport);
        var captured = await Resolve();
        Assert.NotNull(captured.Total);
        await Change(db =>
        {
            db.Games.Add(new Game { Id = "unrelated-result", Sport = sport, SeasonYear = game.SeasonYear, SeasonType = 2,
                HomeTeam = "Other", AwayTeam = "Other", HomeTeamAbbreviation = "A", AwayTeamAbbreviation = "B",
                ScheduledStart = ProHypeTests.Now.AddHours(-2), Status = "final" });
            return Task.CompletedTask;
        });
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            var strict = scope.ServiceProvider.GetRequiredService<HypeScoreService>().EvaluateFromEvidence(game,
                await db.TeamRatings.ToArrayAsync(), await db.PregameOdds.SingleAsync(), ["unrelated-result"], ProHypeTests.Now);
            Assert.False(strict.Components.StakesAvailable);
            Assert.Contains(backend.Scoring.ProHypeProblem.CompletedResultLag, strict.Assessment.Problems);
        }
        var gap = await Resolve();
        Assert.Equal(captured.Total, gap.Total);
        Assert.Equal(captured.Components.StakesScore, gap.Components.StakesScore);
        Assert.Equal(captured.Components.QualityScore, gap.Components.QualityScore);
        Assert.Equal(captured.Components.ClosenessScore, gap.Components.ClosenessScore);
        clock.Advance(TimeSpan.FromMinutes(2));
        await Change(async db =>
        {
            var row = await db.Games.SingleAsync(g => g.Id == game.Id);
            var evidence = ProGameEvidence.Parse(row.ProEvidenceJson)!;
            evidence.CompletedGameIds = ["unrelated-result"];
            evidence.ObservedAt = clock.GetUtcNow().UtcDateTime;
            evidence.HomeProgress.Completed = evidence.AwayProgress.Completed = 4;
            ProHypeContinuityInputs.RefreshLeverage(row, evidence, .25);
            row.ProEvidenceJson = JsonSerializer.Serialize(evidence);
            foreach (var rating in await db.TeamRatings.ToArrayAsync())
            {
                rating.FetchedAt = clock.GetUtcNow().UtcDateTime;
                if (rating.PowerRank.HasValue) rating.PowerRank += 5;
            }
            var odds = await db.PregameOdds.SingleAsync();
            odds.FetchedAt = clock.GetUtcNow().UtcDateTime;
            odds.HomeWinProbability = .7;
            odds.AwayWinProbability = .3;
        });
        var recovered = await Resolve();
        Assert.NotNull(recovered.Total);
        Assert.NotEqual(captured.Total, recovered.Total);
        Assert.NotEqual(captured.Components.StakesScore, recovered.Components.StakesScore);
        Assert.NotEqual(captured.Components.QualityScore, recovered.Components.QualityScore);
        Assert.Equal(60, recovered.Components.ClosenessScore, 8);
        clock.Advance(TimeSpan.FromDays(3));
        Assert.Equal(recovered.Total, (await Resolve()).Total);
    }

    [Theory]
    [InlineData("home")]
    [InlineData("away")]
    [InlineData("home-id")]
    [InlineData("away-id")]
    [InlineData("season")]
    [InlineData("phase")]
    [InlineData("sport")]
    [InlineData("schedule")]
    [InlineData("canceled")]
    [InlineData("postponed")]
    [InlineData("live")]
    [InlineData("historical")]
    [InlineData("actual-start")]
    public async Task Authoritative_context_change_cannot_be_hidden_by_reverting_before_the_next_read(string change)
    {
        await Seed("nfl");
        Assert.NotNull((await Resolve()).Total);
        await Change(async db =>
        {
            var row = await db.Games.SingleAsync();
            switch (change)
            {
                case "home": row.HomeTeamAbbreviation = "OTHER"; break;
                case "away": row.AwayTeamAbbreviation = "OTHER"; break;
                case "home-id": row.HomeTeamId = "new-id"; break;
                case "away-id": row.AwayTeamId = "new-id"; break;
                case "season": row.SeasonYear++; break;
                case "phase": row.SeasonType = 3; break;
                case "sport": row.Sport = "mlb"; break;
                case "schedule": row.ScheduledStart = row.ScheduledStart.AddHours(1); break;
                case "historical": row.IsHistoricalImport = true; break;
                case "actual-start": row.ActualStart = ProHypeTests.Now; break;
                default: row.Status = change; break;
            }
        });
        await Change(async db =>
        {
            var row = await db.Games.SingleAsync();
            row.HomeTeamAbbreviation = game.HomeTeamAbbreviation;
            row.AwayTeamAbbreviation = game.AwayTeamAbbreviation;
            row.HomeTeamId = row.AwayTeamId = null;
            row.SeasonYear = game.SeasonYear;
            row.SeasonType = game.SeasonType;
            row.Sport = game.Sport;
            row.ScheduledStart = game.ScheduledStart;
            row.IsHistoricalImport = false;
            row.ActualStart = null;
            row.Status = "scheduled";
        });
        clock.Advance(TimeSpan.FromHours(2));
        Assert.Null((await Resolve()).Total);
        await app.DisposeAsync();
        app = CreateHost();
        Assert.Null((await Resolve()).Total);
    }

    [Fact]
    public async Task The_pregame_cutoff_is_exclusive_even_while_status_remains_scheduled()
    {
        await Seed("nfl");
        var captured = await Resolve();
        clock.SetUtcNow(new DateTimeOffset(game.ScheduledStart.AddTicks(-1)));
        Assert.Equal(captured.Total, (await Resolve()).Total);
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Null((await Resolve()).Total);
        await app.DisposeAsync();
        app = CreateHost();
        Assert.Null((await Resolve()).Total);
    }

    [Fact]
    public async Task Repeated_reads_do_not_renew_the_original_complete_evaluation()
    {
        await Seed("nfl");
        var captured = await Resolve();
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(captured.EvaluatedAt, (await Resolve()).EvaluatedAt);
    }

    [Theory]
    [InlineData(1, "nfl", 0)]
    [InlineData(3, "nfl", 100)]
    [InlineData(5, "nba", 75)]
    public async Task Phase_specific_stakes_are_captured_without_qualification_dependencies(int phase, string sport, double expected)
    {
        await Seed(sport);
        await Change(async db =>
        {
            var row = await db.Games.SingleAsync();
            row.SeasonType = phase;
            var evidence = ProGameEvidence.Parse(row.ProEvidenceJson)!;
            evidence.Phase = phase;
            if (phase == 3) { evidence.Round = 4; evidence.HomeElimination = evidence.AwayElimination = true; }
            if (phase == 5) evidence.PlayIn = "knockout";
            row.ProEvidenceJson = JsonSerializer.Serialize(evidence);
            db.TeamRatings.RemoveRange(await db.TeamRatings.Where(r => r.Source == "kalshi").ToArrayAsync());
        });
        var captured = await Resolve();
        Assert.NotNull(captured.Total);
        Assert.Equal(expected, captured.Components.StakesScore);
        clock.Advance(TimeSpan.FromDays(3));
        Assert.Equal(captured.Total, (await Resolve()).Total);
    }

    [Fact]
    public async Task Unrelated_source_rejection_does_not_revoke_the_selected_ranking_pair()
    {
        await Seed("nfl");
        await Change(db => { db.TeamRatings.AddRange(ProHypeTests.Ranks(game, 1, 2, "espn_fpi")); return Task.CompletedTask; });
        var captured = await Resolve();
        await Change(async db =>
        {
            foreach (var row in await db.TeamRatings.Where(r => r.Source == "powerrankingsguru").ToArrayAsync()) row.PowerRank = null;
        });
        clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(captured.Total, (await Resolve()).Total);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Trusted_resolution_veto_revokes_legacy_model_support_even_when_overwritten_before_evaluation(bool overwritten)
    {
        await Seed("nhl");
        var selection = app.Services.GetRequiredService<IOptions<PlayoffRatingsOptions>>().Value;
        Assert.Equal("off", selection.ModeFor("nhl", 2027));
        selection.NhlModelMode = "fallback";
        selection.ApprovedSeasons = [2027];
        await Change(async db =>
        {
            db.TeamRatings.RemoveRange(await db.TeamRatings.Where(r => r.Source == "kalshi").ToArrayAsync());
            foreach (var team in new[] { game.HomeTeamAbbreviation, game.AwayTeamAbbreviation })
            {
                var model = new PlayoffModelEvidence { Version = 2, Provider = "playoffstatus",
                    ValidationPolicy = "playoffstatus-nhl-qualification-model-v2", Sport = "nhl", Season = 2027, Team = team,
                    SeasonVerified = true, TeamVerified = true, PublishedAt = ProHypeTests.Now, FetchedAt = ProHypeTests.Now,
                    State = "estimate", ReasonCode = "accepted" };
                db.TeamRatings.Add(new TeamRating { Sport = "nhl", TeamAbbreviation = team, Season = 2027, SourceSeason = 2027,
                    Source = "playoffstatus", PlayoffProbability = .5, FetchedAt = ProHypeTests.Now, PlayoffEvidenceJson = JsonSerializer.Serialize(model) });
            }
        });
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IProHypeLifecycle>().CaptureAsync();
        var captured = await Resolve();
        Assert.Equal(63.95077200811926, captured.Total!.Value, 10);
        Assert.Equal(ProQualificationEvidenceKind.PlayoffStatusModel, captured.Components.HomeQualificationEvidenceKind);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Change(db =>
        {
            var at = clock.GetUtcNow().UtcDateTime;
            var veto = new PlayoffQuoteEvidence { Version = 1, Provider = "kalshi", ValidationPolicy = "kalshi-qualification-v1",
                Sport = "nhl", Season = 2027, Team = game.HomeTeamAbbreviation, EventId = "KXNHLPLAYOFF-27",
                MarketId = "KXNHLPLAYOFF-27-" + game.HomeTeamAbbreviation, ContractVerified = true,
                State = "resolution_blocked", ResolutionBlocked = true, ReasonCode = "rules_changed", ObservedAt = at };
            db.TeamRatings.Add(new TeamRating { Sport = "nhl", TeamAbbreviation = game.HomeTeamAbbreviation, Season = 2027,
                SourceSeason = 2027, Source = "kalshi", FetchedAt = at, PlayoffEvidenceJson = JsonSerializer.Serialize(veto) });
            return Task.CompletedTask;
        });
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            var raw = scope.ServiceProvider.GetRequiredService<HypeScoreService>().EvaluateFromEvidence(game,
                await db.TeamRatings.ToArrayAsync(), await db.PregameOdds.SingleAsync(), [], clock.GetUtcNow().UtcDateTime);
            Assert.False(raw.Components.StakesAvailable);
            Assert.Contains(raw.Assessment.Observations, r => r.BlocksSelection);
        }
        if (overwritten) await Change(async db => db.TeamRatings.RemoveRange(await db.TeamRatings.Where(r => r.Source == "kalshi").ToArrayAsync()));
        Assert.Null((await Resolve()).Total);
        if (!overwritten) await Change(async db => db.TeamRatings.RemoveRange(await db.TeamRatings.Where(r => r.Source == "kalshi").ToArrayAsync()));
        Assert.Null((await Resolve()).Total);
        clock.Advance(TimeSpan.FromDays(3));
        await app.DisposeAsync();
        app = CreateHost();
        var restored = app.Services.GetRequiredService<IOptions<PlayoffRatingsOptions>>().Value;
        restored.NhlModelMode = "fallback";
        restored.ApprovedSeasons = [2027];
        Assert.Null((await Resolve()).Total);
    }

    [Fact]
    public async Task A_partial_first_calculation_never_becomes_a_saved_total()
    {
        await Seed("nfl");
        await Change(async db => (await db.TeamRatings.FirstAsync(r => r.PowerRank != null)).PowerRank = null);
        var partial = await Resolve();
        Assert.Null(partial.Total);
        Assert.True(partial.Components.StakesAvailable);
        Assert.False(partial.Components.QualityAvailable);
        Assert.True(partial.Components.ClosenessAvailable);
        clock.Advance(TimeSpan.FromDays(3));
        Assert.Null((await Resolve()).Total);
    }

    [Theory]
    [InlineData("odds")]
    [InlineData("ranking")]
    [InlineData("qualification")]
    [InlineData("competition")]
    public async Task Supporting_source_rejection_is_durable_and_does_not_resurrect_after_expiry(string kind)
    {
        await Seed(kind == "qualification" ? "nba" : "nfl");
        Assert.NotNull((await Resolve()).Total);
        await Change(async db =>
        {
            switch (kind)
            {
                case "odds": (await db.PregameOdds.SingleAsync()).SourceSeason = null; break;
                case "ranking": (await db.TeamRatings.FirstAsync(r => r.PowerRank != null)).PowerRank = null; break;
                case "qualification": (await db.TeamRatings.FirstAsync(r => r.Source == "kalshi")).PlayoffProbability = null; break;
                default: (await db.Games.SingleAsync()).ProEvidenceJson = "malformed"; break;
            }
        });
        Assert.Null((await Resolve()).Total);
        clock.Advance(TimeSpan.FromDays(3));
        await app.DisposeAsync();
        app = CreateHost();
        Assert.Null((await Resolve()).Total);
    }

    [Fact]
    public async Task Additive_migration_can_be_applied_twice_without_fabricating_or_resetting_snapshots()
    {
        await Seed("nfl");
        await Change(db => db.Database.ExecuteSqlRawAsync("DROP TABLE pro_hype_snapshots, pro_hype_evidence_changes, pro_hype_revision; ALTER TABLE games DROP COLUMN series_observation_json"));
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "034_PlayoffSeriesObservation.sql"))
            + "\n" + await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "035_ProHypeLifecycle.sql"));
        await Change(async db => { await db.Database.ExecuteSqlRawAsync(sql); await db.Database.ExecuteSqlRawAsync(sql); });
        clock.Advance(TimeSpan.FromHours(2));
        Assert.Null((await Resolve()).Total);
        clock.SetUtcNow(new DateTimeOffset(ProHypeTests.Now.AddHours(2)));
        await RefreshInputs();
        var captured = await Resolve();
        Assert.NotNull(captured.Total);
        await Change(async db => { await db.Database.ExecuteSqlRawAsync(sql); await db.Database.ExecuteSqlRawAsync(sql); });
        clock.Advance(TimeSpan.FromDays(3));
        Assert.Equal(captured.Total, (await Resolve()).Total);
    }

    private async Task RefreshInputs() => await Change(async db =>
    {
        var row = await db.Games.SingleAsync(g => g.Id == game.Id);
        var evidence = ProGameEvidence.Parse(row.ProEvidenceJson)!;
        evidence.ObservedAt = clock.GetUtcNow().UtcDateTime;
        ProHypeContinuityInputs.RefreshLeverage(row, evidence);
        row.ProEvidenceJson = JsonSerializer.Serialize(evidence);
        foreach (var rating in await db.TeamRatings.ToArrayAsync()) rating.FetchedAt = clock.GetUtcNow().UtcDateTime;
        (await db.PregameOdds.SingleAsync()).FetchedAt = clock.GetUtcNow().UtcDateTime;
    });

    [Fact]
    public async Task An_older_concurrent_read_cannot_replace_a_newer_publication_across_hosts()
    {
        await Seed("nfl");
        var original = await Resolve();
        gate.Arm();
        var older = Resolve();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            clock.Advance(TimeSpan.FromMinutes(2));
            await RefreshInputs();
            await Change(async db =>
            {
                var odds = await db.PregameOdds.SingleAsync();
                odds.HomeWinProbability = .8;
                odds.AwayWinProbability = .2;
            });
            var newer = await Resolve();
            Assert.NotEqual(original.Total, newer.Total);
            gate.Release();
            var delayed = await older;
            Assert.Equal(newer.Total, delayed.Total);
            Assert.Equal(newer.EvaluatedAt, delayed.EvaluatedAt);
            clock.Advance(TimeSpan.FromDays(3));
            await app.DisposeAsync();
            app = CreateHost();
            Assert.Equal(newer.Total, (await Resolve()).Total);
        }
        finally { gate.Release(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delayed_work_cannot_resurrect_hard_invalidation_or_replace_a_whole_score_with_partial_inputs(bool hardInvalidation)
    {
        await Seed("nfl");
        var original = await Resolve();
        gate.Arm();
        var older = Resolve();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            clock.Advance(TimeSpan.FromMinutes(2));
            await Change(async db =>
            {
                if (hardInvalidation) (await db.Games.SingleAsync()).Status = "postponed";
                else db.Games.Add(new Game { Id = "unrelated-result", Sport = game.Sport, SeasonYear = game.SeasonYear, SeasonType = 2,
                    HomeTeam = "Other", AwayTeam = "Other", HomeTeamAbbreviation = "A", AwayTeamAbbreviation = "B",
                    ScheduledStart = ProHypeTests.Now.AddHours(-2), Status = "final" });
            });
            var newer = await Resolve();
            if (hardInvalidation) Assert.Null(newer.Total);
            else Assert.Equal(original.Total, newer.Total);
            gate.Release();
            Assert.Equal(newer.Total, (await older).Total);
            await app.DisposeAsync();
            app = CreateHost();
            Assert.Equal(newer.Total, (await Resolve()).Total);
        }
        finally { gate.Release(); }
    }

    [Fact]
    public async Task Cancellation_at_commit_cannot_persist_a_partial_replacement()
    {
        await Seed("nfl");
        var original = await Resolve();
        clock.Advance(TimeSpan.FromMinutes(2));
        await RefreshInputs();
        await Change(async db => { var odds = await db.PregameOdds.SingleAsync(); odds.HomeWinProbability = .8; odds.AwayWinProbability = .2; });
        using var cancellation = new CancellationTokenSource();
        gate.CancelOnCommit(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Resolve(cancellation.Token));
        clock.Advance(TimeSpan.FromHours(2));
        await app.DisposeAsync();
        app = CreateHost();
        var retained = await Resolve();
        Assert.Equal(original.Total, retained.Total);
        Assert.Equal(original.Components.ClosenessScore, retained.Components.ClosenessScore);
        Assert.Equal(original.EvaluatedAt, retained.EvaluatedAt);
    }

    [Theory]
    [InlineData("roles")]
    [InlineData("evidence-key")]
    [InlineData("signature")]
    public async Task Persisted_lowercase_roles_and_legacy_revocation_keys_remain_effective_across_recreation(string legacy)
    {
        await Seed("nba");
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IProHypeLifecycle>().CaptureAsync();
        var original = await Resolve();
        await Change(async db =>
        {
            var state = await db.ProHypeSnapshots.SingleAsync();
            var saved = System.Text.Json.Nodes.JsonNode.Parse(state.SnapshotJson!)!;
            var support = saved["SupportingEvidence"]!.AsArray();
            foreach (var reference in support)
            {
                Assert.Contains(reference!["Kind"]!.GetValue<string>(), new[] { "competition", "odds", "qualification", "ranking" });
                reference.AsObject().Remove("ExpiredProbability");
            }
            state.SnapshotJson = saved.ToJsonString();
            if (legacy == "roles") return;
            var odds = support.Single(r => r!["Kind"]!.GetValue<string>() == "odds")!;
            var key = "odds:" + odds["Source"]!.GetValue<string>() + ":" + odds["Team"]!.GetValue<string>() + ":" + odds["Revision"]!.GetValue<string>();
            if (legacy == "signature")
            {
                var evidence = support.Select(r => new { Kind = r!["Kind"]!.GetValue<string>(), Source = r["Source"]!.GetValue<string>(),
                    Team = r["Team"]!.GetValue<string>(), Revision = r["Revision"]!.GetValue<string>() })
                    .OrderBy(r => r.Kind).ThenBy(r => r.Source).ThenBy(r => r.Team);
                var originalRepresentation = JsonSerializer.Serialize(new { Identity = saved["Identity"], Evidence = evidence });
                key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(originalRepresentation)));
            }
            state.SnapshotJson = null;
            state.RevokedEvidenceJson = JsonSerializer.Serialize(new[] { key });
        });
        clock.Advance(legacy == "roles" ? TimeSpan.FromHours(2) : TimeSpan.FromMinutes(1));
        await app.DisposeAsync();
        app = CreateHost();
        var restored = await Resolve();
        if (legacy == "roles")
        {
            Assert.Equal(original.Total, restored.Total);
            Assert.Equal(original.EvaluatedAt, restored.EvaluatedAt);
            Assert.Equal(JsonSerializer.Serialize(original.Components), JsonSerializer.Serialize(restored.Components));
        }
        else
        {
            Assert.Null(restored.Total);
            clock.Advance(TimeSpan.FromDays(3));
            await app.DisposeAsync();
            app = CreateHost();
            Assert.Null((await Resolve()).Total);
        }
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("nonfinite")]
    [InlineData("partial")]
    [InlineData("inconsistent-total")]
    [InlineData("missing-provenance")]
    public async Task Corrupt_persistence_is_unavailable_rather_than_a_fabricated_complete_snapshot(string corruption)
    {
        await Seed("nfl");
        Assert.NotNull((await Resolve()).Total);
        await Change(async db =>
        {
            var state = await db.ProHypeSnapshots.SingleAsync();
            var json = System.Text.Json.Nodes.JsonNode.Parse(state.SnapshotJson!)!.AsObject();
            switch (corruption)
            {
                case "malformed": state.SnapshotJson = "not json"; return;
                case "nonfinite": json["Total"] = "NaN"; break;
                case "partial": json["Components"]!["StakesAvailable"] = false; break;
                case "inconsistent-total": json["Total"] = 99; break;
                default: json["SupportingEvidence"] = new System.Text.Json.Nodes.JsonArray(); break;
            }
            state.SnapshotJson = json.ToJsonString();
        });
        clock.Advance(TimeSpan.FromHours(2));
        Assert.Null((await Resolve()).Total);
    }

    [Fact]
    public async Task Canonical_in_progress_status_cannot_become_a_new_pregame_preview_after_feed_reversion()
    {
        await Seed("nfl");
        Assert.NotNull((await Resolve()).Total);
        await Change(async db => (await db.Games.SingleAsync()).Status = "in_progress");
        await Change(async db => (await db.Games.SingleAsync()).Status = "scheduled");
        clock.Advance(TimeSpan.FromMinutes(2));
        await RefreshInputs();
        Assert.Null((await Resolve()).Total);
        await app.DisposeAsync();
        app = CreateHost();
        Assert.Null((await Resolve()).Total);
    }

    [Fact]
    public async Task Confirmed_actual_play_cannot_be_reconstructed_as_pregame_after_a_lagging_feed_reverts()
    {
        await Seed("nfl");
        Assert.NotNull((await Resolve()).Total);
        await Change(async db => (await db.Games.SingleAsync()).ActualStart = ProHypeTests.Now);
        await Change(async db => (await db.Games.SingleAsync()).ActualStart = null);
        clock.Advance(TimeSpan.FromMinutes(2));
        await RefreshInputs();
        Assert.Null((await Resolve()).Total);
        await app.DisposeAsync();
        app = CreateHost();
        Assert.Null((await Resolve()).Total);
    }

    [Fact]
    public async Task Reusing_a_registered_scope_cannot_roll_back_a_peer_publication_during_a_gap()
    {
        await Seed("nfl");
        using var scope = app.Services.CreateScope();
        var lifecycle = scope.ServiceProvider.GetRequiredService<IProHypeLifecycle>();
        var original = await lifecycle.ResolveAsync(game.Id);
        clock.Advance(TimeSpan.FromMinutes(2));
        await RefreshInputs();
        await Change(async db => { var odds = await db.PregameOdds.SingleAsync(); odds.HomeWinProbability = .8; odds.AwayWinProbability = .2; });
        var newer = await Resolve();
        Assert.NotEqual(original.Total, newer.Total);
        await Change(db =>
        {
            db.Games.Add(new Game { Id = "unrelated-result", Sport = game.Sport, SeasonYear = game.SeasonYear, SeasonType = 2,
                HomeTeam = "Other", AwayTeam = "Other", HomeTeamAbbreviation = "A", AwayTeamAbbreviation = "B",
                ScheduledStart = ProHypeTests.Now.AddHours(-2), Status = "final" });
            return Task.CompletedTask;
        });
        Assert.Equal(newer.Total, (await lifecycle.ResolveAsync(game.Id)).Total);
        Assert.Equal(newer.Total, (await Resolve()).Total);
    }

    [Fact]
    public async Task Resolution_neither_uses_nor_commits_an_importers_uncommitted_changes()
    {
        await Seed("nfl");
        var original = await Resolve();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        (await db.Games.SingleAsync()).Status = "postponed";
        var lifecycle = scope.ServiceProvider.GetRequiredService<IProHypeLifecycle>();
        Assert.Equal(original.Total, (await lifecycle.ResolveAsync(game.Id)).Total);
        Assert.Equal(original.Total, (await Resolve()).Total);
    }

    [Fact]
    public async Task An_explicit_verified_evidence_phase_remains_applicable_when_the_scoreboard_phase_is_not_set()
    {
        await Seed("nba");
        await Change(async db => (await db.Games.SingleAsync()).SeasonType = null);
        var captured = await Resolve();
        Assert.NotNull(captured.Total);
        clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(captured.Total, (await Resolve()).Total);
    }

    public async Task DisposeAsync()
    {
        await app.DisposeAsync();
        await entry.DisposeAsync();
        await database.DisposeAsync();
    }
}
