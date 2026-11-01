using System.Data.Common;
using System.Text.Json;
using backend.Data;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FSharp.Collections;
using Npgsql;
using Xunit;

namespace backend.tests;

public sealed class ExcitementPolicyTests : IAsyncLifetime
{
    private readonly CommandRecorder recorder = new();
    private readonly HostedGameFixture host;
    private static readonly DateTime Start = new(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc);

    public ExcitementPolicyTests()
    {
        host = new HostedGameFixture(new DateTimeOffset(Start), services =>
            services.AddDbContext<ExcitementDbContext>(options => options.AddInterceptors(recorder)));
    }

    public Task InitializeAsync() => host.InitializeAsync();
    public Task DisposeAsync() => host.DisposeAsync();

    [Fact]
    public async Task Growing_gradual_movement_earns_suspense_weighted_credit_without_a_future_reversal()
    {
        await AddHistory("middle", Enumerable.Range(50, 11).Select(p => p / 100.0).ToArray());
        await AddHistory("one-sided", [0.9, 1.0]);

        var scores = await Scores("middle", "one-sided");

        Assert.Equal(1.626666666666667, scores["middle"]!.Value, 9);
        Assert.Equal(0.026666666666667, scores["one-sided"]!.Value, 9);
    }

    [Fact]
    public async Task Repeated_half_point_jitter_is_observed_but_earns_zero_excitement()
    {
        await AddHistory("jitter", Enumerable.Range(0, 200).Select(i => i % 2 == 0 ? 0.495 : 0.505).ToArray());

        Assert.Equal(0, (await Scores("jitter"))["jitter"]);
    }

    [Theory]
    [InlineData(new double[] { 0.5, 0.6, 0.571 }, 1.626666666666667)]
    [InlineData(new double[] { 0.5, 0.6, 0.57 }, 2.040186666666667)]
    [InlineData(new double[] { 0.5, 0.6, 0.569 }, 2.055013093333333)]
    [InlineData(new double[] { 0.5, 0.6, 0.58, 0.595, 0.61, 0.60 }, 1.751493333333333)]
    public async Task Only_threshold_retracements_count_and_an_unfinished_small_tail_is_suppressed(double[] history, double expected)
    {
        await AddHistory("game", history);

        Assert.Equal(expected, (await Scores("game"))["game"]!.Value, 9);
    }

    [Theory]
    [InlineData(new double[] { 0.5, 0.52, 0.48 }, 0.768426666666667)]
    [InlineData(new double[] { 0.5, 0.48, 0.52 }, 0.768426666666667)]
    [InlineData(new double[] { 0.4, 0.43 }, 0.41352)]
    public async Task Significant_initial_excursions_count_even_when_both_extrema_are_near_the_first_point(double[] history, double expected)
    {
        await AddHistory("game", history);

        Assert.Equal(expected, (await Scores("game"))["game"]!.Value, 9);
    }

    [Theory]
    [InlineData(new double[] { }, null)]
    [InlineData(new double[] { 0.5 }, null)]
    [InlineData(new double[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0.1, 1.1 }, null)]
    [InlineData(new double[] { 0.5, double.NaN, -0.1 }, null)]
    [InlineData(new double[] { 0.5, 0.5, 0.5 }, 0.0)]
    [InlineData(new double[] { 0.9, 0.9 }, 0.0)]
    [InlineData(new double[] { double.NaN, 0.5, 1.1, 0.6, double.PositiveInfinity }, 1.626666666666667)]
    public async Task Availability_counts_only_usable_observations_and_distinguishes_flat_zero(double[] history, double? expected)
    {
        await AddHistory("game", history);
        var actual = (await Scores("game"))["game"];

        if (expected.HasValue) Assert.Equal(expected.Value, actual!.Value, 9);
        else Assert.Null(actual);
    }

    [Theory]
    [InlineData(1, 6.613333333333333)]
    [InlineData(10, 66.13333333333333)]
    [InlineData(16, 100.0)]
    public async Task Cross_middle_swings_earn_credit_and_repeated_substantial_drama_reaches_high_and_cap(int swings, double expected)
    {
        await AddHistory("game", Enumerable.Range(0, swings + 1).Select(i => i % 2 == 0 ? 0.1 : 0.9).ToArray());
        var actual = (await Scores("game"))["game"]!.Value;

        Assert.True(double.IsFinite(actual));
        Assert.InRange(actual, 0, 100);
        Assert.Equal(expected, actual, 9);
    }

    [Fact]
    public async Task Team_swap_duplicates_and_directional_interpolation_do_not_change_the_rating()
    {
        double[] history = [0.5, 0.52, 0.48, 0.9, 0.1, 0.995, 0.98];
        await AddHistory("original", history);
        await AddHistory("swapped", history.Select(p => 1 - p).ToArray());
        await AddHistory("duplicates", history.SelectMany(p => new[] { p, p, p }).ToArray());
        var interpolated = history.Zip(history.Skip(1))
            .SelectMany(pair => Enumerable.Range(0, 10).Select(i => pair.First + (pair.Second - pair.First) * i / 10))
            .Append(history[^1]).ToArray();
        await AddHistory("interpolated", interpolated);

        var scores = await Scores("original", "swapped", "duplicates", "interpolated");

        Assert.True(scores["original"] > 0);
        foreach (var id in new[] { "swapped", "duplicates", "interpolated" })
            Assert.Equal(scores["original"]!.Value, scores[id]!.Value, 9);
    }

    [Fact]
    public async Task Appended_ordinary_evidence_never_lowers_a_prefix_rating_and_stays_finite_and_bounded()
    {
        double[] history = [0.5, 0.52, 0.48, 0.1, 0.13, 0.159, 0.16, 0.2, 0.9, 0.88, 0.86, 1, 0.98, 0.97, 0, 0.01, 0.03];
        var random = new Random(667);
        history = history.Concat(Enumerable.Range(0, 100).Select(_ => random.NextDouble())).ToArray();
        await AddHistory("game", [history[0]]);
        Assert.Null((await Scores("game"))["game"]);
        double previous = 0;

        for (var i = 1; i < history.Length; i++)
        {
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            db.OddsSnapshots.Add(Snapshot("game", history[i], i));
            await db.SaveChangesAsync();
            var score = (await Scores("game"))["game"]!.Value;

            Assert.True(double.IsFinite(score));
            Assert.InRange(score, previous - 1e-9, 100);
            previous = score;
        }
        Assert.Equal(100, previous);
    }

    [Fact]
    public async Task Persisted_histories_are_ordered_by_timestamp_then_row_identity_not_insertion_order()
    {
        await AddHistory("chronological", []);
        await AddHistory("tied", []);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            double[] probabilities = [0.5, 0.55, 0.6];
            foreach (var i in new[] { 2, 0, 1 })
            {
                var chronological = Snapshot("chronological", probabilities[i], i);
                chronological.Id = 100 + i;
                var tied = Snapshot("tied", probabilities[i], 0);
                tied.Id = 200 + i;
                db.OddsSnapshots.AddRange(chronological, tied);
                await db.SaveChangesAsync();
            }
        }

        var scores = await Scores("chronological", "tied");

        Assert.Equal(1.626666666666667, scores["chronological"]!.Value, 9);
        Assert.Equal(1.626666666666667, scores["tied"]!.Value, 9);
    }

    [Fact]
    public async Task In_scope_sports_share_the_policy_and_World_Cup_and_motorsports_keep_raw_movement()
    {
        string[] revised = ["cfb", "nfl", "nba", "nhl", "mlb"];
        string[] legacy = ["world_cup", "f1", "nascar", "indycar"];
        foreach (var sport in revised.Concat(legacy))
            await AddHistory(sport, [0.5, 0.6, 0.58], sport);

        var scores = await Scores(revised.Concat(legacy).ToArray());

        foreach (var sport in revised) Assert.Equal(1.626666666666667, scores[sport]!.Value, 9);
        foreach (var sport in legacy) Assert.Equal(2.4, scores[sport]!.Value, 9);
    }

    [Fact]
    public async Task Historical_imports_and_recovered_samples_cannot_supply_or_inflate_eligible_excitement()
    {
        await AddHistory("eligible", [0.5, 0.6]);
        await AddHistory("imported", [0.5, 0.6], historicalImport: true);
        await AddHistory("recovered", []);
        await AddHistory("one-eligible", [0.5]);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            foreach (var id in new[] { "eligible", "recovered", "one-eligible" })
                foreach (var i in Enumerable.Range(0, 20))
                {
                    var recovered = Snapshot(id, i % 2 == 0 ? 0.1 : 0.9, i);
                    recovered.IsHistorical = true;
                    db.OddsSnapshots.Add(recovered);
                }
            await db.SaveChangesAsync();
        }

        var scores = await Scores("eligible", "imported", "recovered", "one-eligible", "missing");

        Assert.Equal(1.626666666666667, scores["eligible"]!.Value, 9);
        foreach (var id in new[] { "imported", "recovered", "one-eligible", "missing" }) Assert.Null(scores[id]);
    }

    [Fact]
    public async Task Requested_games_use_the_full_history_in_one_cancellable_database_filtered_batch()
    {
        await AddHistory("full", new[] { 0.1 }.Concat(Enumerable.Repeat(0.9, 150)).ToArray());
        await AddHistory("requested", [0.5, 0.6]);
        await AddHistory("unrelated", Enumerable.Range(0, 200).Select(i => i % 2 == 0 ? 0.1 : 0.9).ToArray());
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            (await db.Games.SingleAsync(g => g.Id == "full")).Status = "final";
            await db.SaveChangesAsync();
        }
        recorder.Commands.Clear();
        using var cancellation = new CancellationTokenSource();

        var scores = await Scores(cancellation.Token, "full", "requested", "missing", "full");

        Assert.Equal(3, scores.Count);
        Assert.Equal(6.613333333333333, scores["full"]!.Value, 9);
        Assert.Equal(1.626666666666667, scores["requested"]!.Value, 9);
        Assert.Null(scores["missing"]);
        Assert.DoesNotContain("unrelated", scores.Keys);
        var historyRead = Assert.Single(recorder.Commands, c => c.Sql.Contains("odds_snapshots"));
        Assert.True(historyRead.CanBeCanceled);

        using var explainScope = host.Services.CreateScope();
        var explainDb = explainScope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        await explainDb.Database.OpenConnectionAsync();
        await using var explain = new NpgsqlCommand("EXPLAIN (FORMAT JSON) " + historyRead.Sql,
            (NpgsqlConnection)explainDb.Database.GetDbConnection());
        foreach (var (name, value) in historyRead.Parameters) explain.Parameters.AddWithValue(name, value);
        using var plan = JsonDocument.Parse((string)(await explain.ExecuteScalarAsync())!);
        var scans = Descendants(plan.RootElement[0].GetProperty("Plan"))
            .Where(n => n.TryGetProperty("Relation Name", out var relation) && relation.GetString() == "odds_snapshots").ToArray();
        Assert.NotEmpty(scans);
        foreach (var scan in scans)
        {
            var predicate = string.Join(" ", new[] { "Filter", "Index Cond", "Recheck Cond" }
                .Where(name => scan.TryGetProperty(name, out _)).Select(name => scan.GetProperty(name).GetString()));
            Assert.True(predicate.Contains("game_id") && predicate.Contains("full") && predicate.Contains("requested"),
                "History scan must be restricted to requested IDs: " + scan);
        }
    }

    [Fact]
    public async Task A_cancelled_batch_read_propagates_cancellation_instead_of_returning_scores()
    {
        await AddHistory("game", [0.5, 0.6]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Scores(cancellation.Token, "game"));
    }

    private static IEnumerable<JsonElement> Descendants(JsonElement node)
    {
        yield return node;
        if (node.TryGetProperty("Plans", out var children))
            foreach (var child in children.EnumerateArray())
                foreach (var descendant in Descendants(child)) yield return descendant;
    }

    private async Task AddHistory(string id, double[] probabilities, string sport = "mlb", bool historicalImport = false)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        db.Games.Add(new Game
        {
            Id = id, Sport = sport, HomeTeam = "Home", AwayTeam = "Away",
            HomeTeamAbbreviation = "H", AwayTeamAbbreviation = "A",
            ScheduledStart = Start, Status = "in_progress", IsHistoricalImport = historicalImport
        });
        db.OddsSnapshots.AddRange(probabilities.Select((p, i) => Snapshot(id, p, i)));
        await db.SaveChangesAsync();
    }

    private static OddsSnapshot Snapshot(string id, double probability, int minute) => new()
    {
        GameId = id, Timestamp = Start.AddMinutes(minute),
        HomeWinProbability = probability, AwayWinProbability = 1 - probability, Source = "kalshi"
    };

    private Task<IDictionary<string, double?>> Scores(params string[] ids) => Scores(CancellationToken.None, ids);

    private async Task<IDictionary<string, double?>> Scores(CancellationToken ct, params string[] ids)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<GameDataService>()
            .GetExcitementScoresForGamesAsync(ListModule.OfSeq(ids), ct);
    }

    private sealed record CapturedCommand(string Sql, (string Name, object Value)[] Parameters, bool CanBeCanceled);

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<CapturedCommand> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(new(command.CommandText, command.Parameters.Cast<DbParameter>()
                .Select(p => (p.ParameterName, p.Value!)).ToArray(), cancellationToken.CanBeCanceled));
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
