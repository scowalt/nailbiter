using System.Diagnostics;
using System.Net;
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
    public async Task Normal_deployment_selects_imported_forecasts_and_redeployment_preserves_the_daily_limit()
    {
        var settings = await DeploymentSettingsAsync();
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync(enabled: false);
        var admitted = h.Clock.Now;
        await using (var deployed = h.App.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings))))
        {
            var options = deployed.Services.GetRequiredService<IOptions<PlayoffRatingsOptions>>().Value;
            Assert.True(options.MoneyPuckRefreshEnabledFor("nhl", 2027));
            Assert.True(options.MoneyPuckSelectionEnabledFor("nhl", 2027));
            Assert.False(options.MoneyPuckSelectionEnabledFor("nhl", 2028));
            Assert.False(options.MoneyPuckSelectionEnabledFor("nba", 2027));
            Assert.False(options.MoneyPuckRefreshEnabledFor("nhl", 2028));
            var result = await deployed.Services.GetRequiredService<MoneyPuckService>().RefreshNhlAsync(default);
            Assert.Equal(32, result.Rankings);
            Assert.Equal(32, result.Qualification);
            Assert.Equal<DateTime?>(admitted.UtcDateTime.AddHours(24), result.NextAttemptAt);
            using var scope = deployed.Services.CreateScope();
            var team = (await scope.ServiceProvider.GetRequiredService<GameDataService>().GetTeamRankInfoAsync("nhl", "NSH", 2027, 2, default)).Value;
            Assert.NotNull(team.PowerRank);
            Assert.Equal(.2518, team.PlayoffOdds.Value);
            Assert.False(team.PlayoffIsFinal);
            Assert.Equal(9, h.Handler.Requests.Count);
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

    [Fact]
    public async Task Turning_on_selection_does_not_shorten_a_persisted_denial_backoff_or_retry_the_provider()
    {
        var selectionOff = await DeploymentSettingsAsync(selectionEnabled: false);
        var selectionOn = await DeploymentSettingsAsync();
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync(enabled: false);
        h.Handler.Override = (_, _) => Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.Forbidden));
        await using (var previous = h.App.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(selectionOff))))
        {
            var denied = await previous.Services.GetRequiredService<MoneyPuckService>().RefreshNhlAsync(default);
            Assert.Equal(0, denied.Rankings);
            Assert.Equal(0, denied.Qualification);
            Assert.Single(h.Handler.Requests);
        }

        h.Handler.Override = null;
        h.Clock.Now = h.Clock.Now.AddHours(24).AddTicks(-1);
        await using var deployed = h.App.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(selectionOn)));
        Assert.True(deployed.Services.GetRequiredService<IOptions<PlayoffRatingsOptions>>().Value.MoneyPuckSelectionEnabled);
        var blocked = await deployed.Services.GetRequiredService<MoneyPuckService>().RefreshNhlAsync(default);
        Assert.Equal(0, blocked.Rankings);
        Assert.Equal(0, blocked.Qualification);
        using var scope = deployed.Services.CreateScope();
        var team = (await scope.ServiceProvider.GetRequiredService<GameDataService>().GetTeamRankInfoAsync("nhl", "NSH", 2027, 2, default)).Value;
        Assert.Null(team.PlayoffOdds);
        Assert.Single(h.Handler.Requests);
    }

    [Fact]
    public async Task Selection_uses_stored_forecasts_without_HTTP_and_preserves_the_stop_and_36_hour_expiry()
    {
        var selectionOn = await DeploymentSettingsAsync();
        var selectionOff = await DeploymentSettingsAsync(selectionEnabled: false);
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync(enabled: false);
        await using var deployed = h.App.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(selectionOn)));
        var result = await deployed.Services.GetRequiredService<MoneyPuckService>().RefreshNhlAsync(default);
        Assert.Equal(32, result.Qualification);
        var requestCount = h.Handler.Requests.Count;
        var published = DateTimeOffset.Parse(h.Handler.Marker, System.Globalization.CultureInfo.InvariantCulture);

        async Task<double?> Forecast(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app)
        {
            using var scope = app.Services.CreateScope();
            var team = (await scope.ServiceProvider.GetRequiredService<GameDataService>().GetTeamRankInfoAsync("nhl", "NSH", 2027, 2, default)).Value;
            return team.PlayoffOdds?.Value;
        }

        Assert.Equal(.2518, await Forecast(deployed));
        await using (var stopped = h.App.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(selectionOff))))
            Assert.Null(await Forecast(stopped));
        h.Clock.Now = published.AddHours(36).AddTicks(-1);
        Assert.Equal(.2518, await Forecast(deployed));
        h.Clock.Now = h.Clock.Now.AddTicks(1);
        Assert.Null(await Forecast(deployed));
        Assert.Equal(requestCount, h.Handler.Requests.Count);
    }

    private static async Task<Dictionary<string, string?>> DeploymentSettingsAsync(bool? selectionEnabled = null)
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
            if (selectionEnabled.HasValue)
                start.Environment["MONEYPUCK_SELECTION_ENABLED"] = selectionEnabled.Value.ToString().ToLowerInvariant();
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
