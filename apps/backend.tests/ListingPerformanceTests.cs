using System.Data.Common;
using System.Text.Json;
using AngleSharp.Html.Parser;
using backend.Data;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FSharp.Collections;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace backend.tests;

public sealed class ListingPerformanceTests : IClassFixture<SharedPostgreSql>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 18, 0, 0, TimeSpan.Zero);
    private readonly CommandRecorder recorder = new();
    private readonly HostedGameFixture host;
    private readonly ITestOutputHelper output;

    public ListingPerformanceTests(SharedPostgreSql server, ITestOutputHelper output)
    {
        this.output = output;
        host = new HostedGameFixture(Now, services =>
            services.AddDbContext<ExcitementDbContext>(options => options.AddInterceptors(recorder)), server);
    }

    public Task InitializeAsync() => host.InitializeAsync();
    public Task DisposeAsync() => host.DisposeAsync();

    [Fact]
    public async Task Latest_odds_lookup_scans_requested_histories_once_and_excludes_other_games()
    {
        await host.SeedAsync(Game("target", "in_progress"), History("target", 105).Reverse());
        await host.SeedAsync(Game("second", "in_progress"), History("second", 2).Reverse());
        await host.SeedAsync(Game("empty", "in_progress"), []);
        await host.SeedAsync(Game("other", "in_progress"), History("other", 105));
        recorder.Commands.Clear();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<GameDataService>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var odds = await service.GetLatestOddsForGamesAsync(ListModule.OfSeq(new[] { "target", "second", "empty", "target" }), deadline.Token);
        Assert.Equal(Now.UtcDateTime.AddMinutes(104), odds["target"].Timestamp);
        Assert.Equal(Now.UtcDateTime.AddMinutes(1), odds["second"].Timestamp);
        Assert.Equal(2, odds.Count);
        Assert.DoesNotContain("empty", odds.Keys);
        Assert.DoesNotContain("other", odds.Keys);

        var captured = Assert.Single(recorder.Commands, c => c.Sql.Contains("odds_snapshots"));
        output.WriteLine(captured.Sql);
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "EXPLAIN (FORMAT JSON) " + captured.Sql;
        foreach (var (name, value) in captured.Parameters)
            command.Parameters.Add(new NpgsqlParameter(name, value));
        using var plan = JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!);
        var scans = Descendants(plan.RootElement[0].GetProperty("Plan"))
            .Where(n => n.TryGetProperty("Relation Name", out var relation) && relation.GetString() == "odds_snapshots")
            .ToArray();
        Assert.True(scans.Length == 1, $"Expected one odds history scan, got {scans.Length}. Plan: {plan.RootElement}");
        var predicate = string.Join(" ", new[] { "Filter", "Index Cond", "Recheck Cond" }
            .Where(name => scans[0].TryGetProperty(name, out _)).Select(name => scans[0].GetProperty(name).GetString()));
        Assert.Contains("game_id", predicate);
        Assert.Contains("target", predicate);
        Assert.Contains("second", predicate);
        Assert.DoesNotContain("other", predicate);
        Assert.True(captured.CanBeCanceled);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/nhl?date=2026-10-13")]
    public async Task Scheduled_listing_keeps_latest_probabilities_without_reading_excitement_history(string path)
    {
        await host.SeedAsync(Game("scheduled", "scheduled"), History("scheduled", 2));
        recorder.Commands.Clear();
        using var page = await new HtmlParser().ParseDocumentAsync(await host.Client.GetStringAsync(path));
        Assert.NotNull(page.QuerySelector("a[href='/game/scheduled']"));
        Assert.Equal("Scheduled", page.QuerySelector("#status-scheduled")!.TextContent.Trim());
        Assert.Equal(path == "/" ? "Hype unavailable—" : "Hype unavailable", page.QuerySelector("#excitement-scheduled")!.TextContent.Trim());
        Assert.Contains("50%", page.QuerySelector("#probbar-scheduled")!.TextContent);
        var historyReads = recorder.Commands.Where(c => c.Sql.Contains("odds_snapshots") && c.Sql.Contains("is_historical_import"));
        Assert.Empty(historyReads);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/nhl?date=2026-10-13")]
    public async Task Mixed_listing_reads_full_excitement_history_only_for_live_and_final_games(string path)
    {
        var probabilities = new[] { .1, .9, .1, .9, .1 }.Concat(Enumerable.Repeat(.1, 110)).ToArray();
        foreach (var (id, status) in new[] { ("scheduled", "scheduled"), ("live", "in_progress"), ("final", "final") })
        {
            var snapshots = History(id, probabilities.Length).ToArray();
            for (var i = 0; i < snapshots.Length; i++)
            {
                snapshots[i].HomeWinProbability = probabilities[i];
                snapshots[i].AwayWinProbability = 1 - probabilities[i];
            }
            await host.SeedAsync(Game(id, status), snapshots.Reverse());
        }
        recorder.Commands.Clear();
        using var page = await new HtmlParser().ParseDocumentAsync(await host.Client.GetStringAsync(path));
        var score = ExcitementScore.Calculate("nhl", ListModule.OfSeq(probabilities));
        Assert.True(score.HasValue);
        foreach (var id in new[] { "live", "final" })
            Assert.Contains(Math.Round(score.GetValueOrDefault()).ToString(System.Globalization.CultureInfo.InvariantCulture),
                page.QuerySelector("#excitement-" + id)!.TextContent);
        Assert.Equal(path == "/" ? "Hype unavailable—" : "Hype unavailable", page.QuerySelector("#excitement-scheduled")!.TextContent.Trim());
        var history = Assert.Single(recorder.Commands, c => c.Sql.Contains("odds_snapshots") && c.Sql.Contains("is_historical_import"));
        var parameters = history.Parameters.Select(p => p.Value).OfType<IEnumerable<string>>().SelectMany(p => p)
            .Concat(history.Parameters.Select(p => p.Value).OfType<string>()).ToArray();
        foreach (var id in new[] { "live", "final" })
            Assert.True(parameters.Contains(id) || history.Sql.Contains("'" + id + "'"));
        Assert.DoesNotContain("scheduled", parameters);
        Assert.DoesNotContain("'scheduled'", history.Sql);
    }

    [Fact]
    public async Task Latest_odds_with_no_requested_games_does_not_query_the_database()
    {
        recorder.Commands.Clear();
        using var scope = host.Services.CreateScope();
        var odds = await scope.ServiceProvider.GetRequiredService<GameDataService>()
            .GetLatestOddsForGamesAsync(FSharpList<string>.Empty, CancellationToken.None);
        Assert.Empty(odds);
        Assert.Empty(recorder.Commands);
    }

    [Fact]
    public async Task Latest_odds_break_timestamp_ties_by_snapshot_id()
    {
        var snapshots = History("target", 2).ToArray();
        snapshots[1].Timestamp = snapshots[0].Timestamp;
        snapshots[1].HomeWinProbability = .7;
        snapshots[1].AwayWinProbability = .3;
        await host.SeedAsync(Game("target", "in_progress"), snapshots);
        using var scope = host.Services.CreateScope();
        var odds = await scope.ServiceProvider.GetRequiredService<GameDataService>()
            .GetLatestOddsForGamesAsync(ListModule.OfSeq(new[] { "target" }), CancellationToken.None);
        Assert.Equal(.7, odds["target"].HomeWinProbability);
    }

    [Fact]
    public async Task Latest_odds_parameterizes_game_ids()
    {
        const string id = "target' OR true --";
        await host.SeedAsync(Game(id, "in_progress"), History(id, 1));
        await host.SeedAsync(Game("other", "in_progress"), History("other", 1));
        recorder.Commands.Clear();
        using var scope = host.Services.CreateScope();
        var odds = await scope.ServiceProvider.GetRequiredService<GameDataService>()
            .GetLatestOddsForGamesAsync(ListModule.OfSeq(new[] { id }), CancellationToken.None);
        Assert.Equal(id, Assert.Single(odds).Key);
        var captured = Assert.Single(recorder.Commands, c => c.Sql.Contains("odds_snapshots"));
        Assert.DoesNotContain(id, captured.Sql);
        Assert.Contains(captured.Parameters, p => p.Value is string[] ids && ids.Contains(id));
    }

    [Fact]
    public async Task Latest_odds_still_reject_expired_or_future_bounds_without_falling_back_to_older_quotes()
    {
        foreach (var (id, age) in new[] { ("expired", -3.0), ("fresh", -.5), ("future", 1.0) })
        {
            var snapshots = History(id, 2).ToArray();
            snapshots[0].Timestamp = Now.UtcDateTime.AddMinutes(-4);
            snapshots[1].Timestamp = Now.UtcDateTime.AddMinutes(age);
            snapshots[1].Source = GameWinnerProbabilityPolicy.OneSidedSource;
            snapshots[1].HomeWinProbability = .995;
            snapshots[1].AwayWinProbability = .005;
            await host.SeedAsync(Game(id, "in_progress"), snapshots);
        }
        using var scope = host.Services.CreateScope();
        var odds = await scope.ServiceProvider.GetRequiredService<GameDataService>()
            .GetLatestOddsForGamesAsync(ListModule.OfSeq(new[] { "expired", "fresh", "future" }), CancellationToken.None);
        Assert.Equal("fresh", Assert.Single(odds).Key);
        Assert.Equal(.995, odds["fresh"].HomeWinProbability);
    }

    private static Game Game(string id, string status) => new()
    {
        Id = id, Sport = "nhl", Status = status, SeasonYear = 2027, SeasonType = 2,
        HomeTeam = "Home", AwayTeam = "Away", HomeTeamAbbreviation = "HOM", AwayTeamAbbreviation = "AWY",
        ScheduledStart = Now.UtcDateTime.AddHours(3)
    };

    private static IEnumerable<OddsSnapshot> History(string id, int count) => Enumerable.Range(0, count).Select(i => new OddsSnapshot
    {
        GameId = id, Timestamp = Now.UtcDateTime.AddMinutes(i), HomeWinProbability = .5, AwayWinProbability = .5,
        Source = "Kalshi", HomeMoneyline = -100, AwayMoneyline = -100
    });

    private static IEnumerable<JsonElement> Descendants(JsonElement node)
    {
        yield return node;
        if (node.TryGetProperty("Plans", out var children))
            foreach (var child in children.EnumerateArray())
                foreach (var descendant in Descendants(child))
                    yield return descendant;
    }

    private sealed record CapturedCommand(string Sql, (string Name, object Value)[] Parameters, bool CanBeCanceled);

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<CapturedCommand> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(new(command.CommandText, command.Parameters.Cast<DbParameter>()
                .Select(p => (p.ParameterName, p.Value!)).ToArray(), cancellationToken.CanBeCanceled));
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
