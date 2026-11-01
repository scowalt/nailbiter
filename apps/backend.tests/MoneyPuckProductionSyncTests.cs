using System.Diagnostics;
using System.Text.Json;
using backend.Models;
using backend.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace backend.tests;

public sealed class MoneyPuckProductionSyncTests
{
    [Fact]
    public async Task Normal_deployment_syncs_both_inputs_and_redeployment_preserves_the_daily_limit()
    {
        var settings = await DeploymentSettingsAsync();
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync(enabled: false);
        var admitted = h.Clock.Now;
        await using (var deployed = h.App.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings))))
        {
            var options = deployed.Services.GetRequiredService<IOptions<PlayoffRatingsOptions>>().Value;
            Assert.True(options.MoneyPuckRefreshEnabledFor("nhl", 2027));
            Assert.False(options.MoneyPuckSelectionEnabled);
            Assert.False(options.MoneyPuckRefreshEnabledFor("nhl", 2028));
            var result = await deployed.Services.GetRequiredService<MoneyPuckService>().RefreshNhlAsync(default);
            Assert.Equal(32, result.Rankings);
            Assert.Equal(32, result.Qualification);
            Assert.Equal(admitted.UtcDateTime.AddHours(24), result.NextAttemptAt.Value);
            using var scope = deployed.Services.CreateScope();
            var team = (await scope.ServiceProvider.GetRequiredService<GameDataService>().GetTeamRankInfoAsync("nhl", "NSH", 2027, 2, default)).Value;
            Assert.NotNull(team.PowerRank);
            Assert.Null(team.PlayoffOdds);
        }
        await using var redeployed = h.App.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings)));
        var service = redeployed.Services.GetRequiredService<MoneyPuckService>();
        h.Clock.Now = admitted.AddHours(6);
        var early = await service.RefreshNhlAsync(default);
        Assert.Equal(0, early.Rankings);
        Assert.Equal(0, early.Qualification);
        h.Clock.Now = admitted.AddHours(24).AddTicks(-1);
        var beforeBoundary = await service.RefreshNhlAsync(default);
        Assert.Equal(0, beforeBoundary.Rankings);
        Assert.Equal(0, beforeBoundary.Qualification);
        Assert.Equal(9, h.Handler.Requests.Count);
        h.Clock.Now = admitted.AddHours(24);
        h.Handler.Marker = h.Clock.Now.AddMinutes(-5).ToString("O");
        var next = await service.RefreshNhlAsync(default);
        Assert.Equal(32, next.Rankings);
        Assert.Equal(32, next.Qualification);
        Assert.Equal(18, h.Handler.Requests.Count);
    }

    private static async Task<Dictionary<string, string?>> DeploymentSettingsAsync()
    {
        var envFile = Path.GetTempFileName();
        try
        {
            var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "compose", "--env-file", envFile, "--file", Path.Combine(AppContext.BaseDirectory, "docker-compose.yml"), "config", "--format", "json" })
                start.ArgumentList.Add(argument);
            start.Environment.Clear();
            start.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH");
            start.Environment["HOME"] = Environment.GetEnvironmentVariable("HOME");
            start.Environment["POSTGRES_PASSWORD"] = "postgres";
            start.Environment["LOKI_URL"] = "https://logs.example.invalid";
            start.Environment["LOKI_USERNAME"] = "fixture";
            start.Environment["GRAFANA_CLOUD_API_KEY"] = "fixture";
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw;
            }
            Assert.True(process.ExitCode == 0, await error);
            using var configuration = JsonDocument.Parse(await output);
            return configuration.RootElement.GetProperty("services").GetProperty("backend").GetProperty("environment")
                .EnumerateObject().Where(property => property.Name.StartsWith("PlayoffRatings__MoneyPuck", StringComparison.Ordinal))
                .ToDictionary(property => property.Name.Replace("__", ":", StringComparison.Ordinal), property => property.Value.GetString());
        }
        finally { File.Delete(envFile); }
    }
}
