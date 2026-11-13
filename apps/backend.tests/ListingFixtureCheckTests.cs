using System.Text.Json;
using ListingCheck;
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
