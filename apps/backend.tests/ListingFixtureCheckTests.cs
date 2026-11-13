using System.Data.Common;
using System.Text.Json;
using backend.Data;
using ListingCheck;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace backend.tests;

public sealed class ListingFixtureCheckTests(ITestOutputHelper output)
{
    private const string Settings = "--samples 2 --warmup 1 --ttfb-budget-ms 10000 --completion-budget-ms 20000 --request-timeout-ms 30000";

    [Fact]
    public async Task Fixture_mode_passes_on_the_representative_Timescale_workload_and_url_mode_passes_against_it()
    {
        var reportPath = Path.Combine(Path.GetTempPath(), $"listing-fixture-{Guid.NewGuid():N}.json");
        var fixtureText = new StringWriter();
        var urlText = new StringWriter();
        var urlExit = -1;
        try
        {
            var exit = await ListingFixtureCheck.RunAsync(Split($"{Settings} --report {reportPath}"), fixtureText, async (address, cancellationToken) =>
                urlExit = await ListingCheckCommand.RunUrlAsync(
                    Split($"--base-url {address} --path / --path /nhl?date=2026-10-13 {Settings}"), urlText, cancellationToken));
            output.WriteLine(fixtureText.ToString());
            output.WriteLine(urlText.ToString());

            Assert.Equal(0, exit);
            Assert.Equal(0, urlExit);
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
            var fixture = report.RootElement.GetProperty("fixture");
            var workload = fixture.GetProperty("workload");
            Assert.True(workload.GetProperty("compressedChunks").GetInt64() > 0);
            Assert.True(workload.GetProperty("chunks").GetInt64() >= 29);
            Assert.Equal(0, fixture.GetProperty("controls").GetProperty("providerRequestsAttempted").GetInt32());
            var work = fixture.GetProperty("queryWork").EnumerateArray().ToDictionary(path => path.GetProperty("path").GetString()!);
            Assert.Equal(ListingWorkload.Paths, work.Keys);
            Assert.All(work.Values, path =>
            {
                Assert.Equal(1, path.GetProperty("latestReads").GetInt32());
                Assert.Equal(0, path.GetProperty("duplicateRelations").GetArrayLength());
                Assert.Equal(1, path.GetProperty("excitementReads").GetInt32());
            });
            Assert.Equal([10, 4, 4], work.Values.Select(path => path.GetProperty("excitementGames").GetInt32()));
        }
        finally
        {
            File.Delete(reportPath);
        }
    }

    [Theory]
    [InlineData(SecondHistoryScan, null)]
    [InlineData(OpaqueFunction, CreateOpaqueFunction)]
    public async Task Query_work_fails_when_the_latest_odds_read_scans_history_twice_or_hides_its_plan(string replacement, string? setup)
    {
        var text = new StringWriter();
        var reportPath = Path.Combine(Path.GetTempPath(), $"listing-fixture-{Guid.NewGuid():N}.json");
        try
        {
            var exit = await ListingFixtureCheck.RunAsync(
                Split($"--samples 1 --warmup 0 --ttfb-budget-ms 10000 --completion-budget-ms 20000 --request-timeout-ms 30000 --report {reportPath}"),
                text, configureServices: services => services.AddDbContext<ExcitementDbContext>(options =>
                    options.AddInterceptors(new LatestOddsRewrite(replacement, setup))));
            output.WriteLine(text.ToString());

            Assert.Equal(6, exit);
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
            var failures = report.RootElement.GetProperty("failures").EnumerateArray().ToList();
            Assert.Equal(ListingWorkload.Paths, failures.Select(failure => failure.GetProperty("path").GetString()).Distinct());
            Assert.All(failures, failure => Assert.Equal("query-work", failure.GetProperty("kind").GetString()));
        }
        finally
        {
            File.Delete(reportPath);
        }
    }

    private const string LatestOddsRead = "SELECT DISTINCT ON (game_id) * FROM odds_snapshots WHERE game_id = ANY (";
    private const string SecondHistoryScan = "SELECT DISTINCT ON (game_id) o.* FROM odds_snapshots o "
        + "WHERE o.timestamp = (SELECT max(h.timestamp) FROM odds_snapshots h WHERE h.game_id = o.game_id) AND o.game_id = ANY (";
    private const string OpaqueFunction = "SELECT * FROM odds_snapshots_latest(";
    private const string CreateOpaqueFunction = """
        CREATE OR REPLACE FUNCTION odds_snapshots_latest(ids text[]) RETURNS SETOF odds_snapshots LANGUAGE plpgsql AS $$
        BEGIN
            RETURN QUERY SELECT DISTINCT ON (game_id) * FROM odds_snapshots WHERE game_id = ANY (ids) ORDER BY game_id, timestamp DESC, id DESC;
        END $$
        """;

    private sealed class LatestOddsRewrite(string replacement, string? setup) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains(LatestOddsRead, StringComparison.Ordinal)) return result;
            if (setup is not null)
            {
                await using var create = command.Connection!.CreateCommand();
                create.CommandText = setup;
                await create.ExecuteNonQueryAsync(cancellationToken);
            }
            command.CommandText = command.CommandText.Replace(LatestOddsRead, replacement, StringComparison.Ordinal);
            return result;
        }
    }

    [Theory]
    [InlineData("--samples 5")]
    [InlineData("--path / --ttfb-budget-ms 100 --completion-budget-ms 250")]
    [InlineData("--samples 101 --ttfb-budget-ms 100 --completion-budget-ms 250")]
    public async Task Invalid_fixture_configuration_exits_2_before_starting_a_container(string arguments)
    {
        var text = new StringWriter();

        Assert.Equal(2, await ListingFixtureCheck.RunAsync(Split(arguments), text));
        Assert.StartsWith("invalid-config: ", text.ToString());
    }

    private static string[] Split(string arguments) => arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
