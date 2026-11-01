using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Xunit;

namespace backend.tests;

public sealed class SyntheticExcitementHistoryTests : IAsyncLifetime
{
    private readonly HostedGameFixture host = new();

    public Task InitializeAsync() => host.InitializeAsync();
    public Task DisposeAsync() => host.DisposeAsync();

    [Fact]
    public async Task Synthetic_CFB_history_round_trips_every_constructed_chart_field_over_HTTP()
    {
        var capture = await host.SeedSyntheticHistoryAsync("990000001");
        host.Clock.SetUtcNow(capture.Context.CaptureCutoff);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await host.Client.GetStringAsync("/game/990000001"));
        var chart = Assert.Single(document.QuerySelectorAll("nb-win-probability-chart"));
        Assert.Equal("990000001", chart.GetAttribute("game-id"));
        Assert.Equal("OKST", chart.GetAttribute("home-abbr"));
        Assert.Equal("UCF", chart.GetAttribute("away-abbr"));
        Assert.Equal("false", chart.GetAttribute("data-live-final"));
        using var json = JsonDocument.Parse(chart.GetAttribute("data")!);
        var points = json.RootElement.EnumerateArray().ToArray();
        Assert.Equal(300, points.Length);
        Assert.Equal("2026-10-10T18:29:30.1234560Z", points[^1].GetProperty("timestamp").GetString());
        Assert.Equal("Q4 0:00", points[^1].GetProperty("label").GetString());
        Assert.Equal(10, points.Count(p => p.GetProperty("isProbabilityBound").GetBoolean()));
        AssertSyntheticChartFields("990000001", points);
    }

    [Fact]
    public async Task Synthetic_NFL_completed_history_round_trips_every_constructed_chart_field_over_HTTP()
    {
        var capture = await host.SeedSyntheticHistoryAsync("990000002");
        host.Clock.SetUtcNow(capture.Context.CaptureCutoff);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await host.Client.GetStringAsync("/game/990000002"));
        var chart = Assert.Single(document.QuerySelectorAll("nb-win-probability-chart"));
        Assert.Equal("990000002", chart.GetAttribute("game-id"));
        Assert.Equal("CHI", chart.GetAttribute("home-abbr"));
        Assert.Equal("MIN", chart.GetAttribute("away-abbr"));
        Assert.Equal("true", chart.GetAttribute("data-live-final"));
        using var json = JsonDocument.Parse(chart.GetAttribute("data")!);
        var points = json.RootElement.EnumerateArray().ToArray();
        Assert.Equal(320, points.Length);
        Assert.Equal("2026-09-20T17:00:00.6543210Z", points[0].GetProperty("timestamp").GetString());
        Assert.Equal("Q0 0:00", points[0].GetProperty("label").GetString());
        Assert.Equal("2026-09-20T19:39:30.6543210Z", points[^1].GetProperty("timestamp").GetString());
        Assert.Equal(21, points[^1].GetProperty("homeScore").GetInt32());
        Assert.Equal(9, points[^1].GetProperty("awayScore").GetInt32());
        Assert.All(points, point => Assert.False(point.GetProperty("isProbabilityBound").GetBoolean()));
        AssertSyntheticChartFields("990000002", points);
    }

    [Theory]
    [InlineData("990000001", "in_progress", "cfb", "Synthetic Oklahoma State", "Synthetic UCF")]
    [InlineData("990000002", "final", "nfl", "Synthetic Chicago", "Synthetic Minnesota")]
    public async Task HTTP_history_preserves_constructed_status_and_explicit_eligibility_controls(
        string gameId, string status, string sport, string home, string away)
    {
        var capture = await host.SeedSyntheticHistoryAsync(gameId);
        host.Clock.SetUtcNow(capture.Context.CaptureCutoff);
        Assert.Equal(sport, capture.Context.Sport);
        Assert.Contains("IsHistoricalImport", capture.Metadata.NotObservedFields);
        Assert.Contains("IsHistorical", capture.Metadata.NotObservedFields);
        Assert.Contains("database row IDs", capture.Metadata.NotObservedFields);
        using var response = await host.Client.GetAsync($"/api/games/{gameId}/odds");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var game = json.RootElement;
        Assert.Equal(gameId, game.GetProperty("id").GetString());
        Assert.Equal(home, game.GetProperty("homeTeam").GetString());
        Assert.Equal(away, game.GetProperty("awayTeam").GetString());
        Assert.Equal(status, game.GetProperty("status").GetString());
        Assert.False(game.GetProperty("isHistoricalImport").GetBoolean());
        var points = game.GetProperty("oddsHistory").EnumerateArray().ToArray();
        var rows = SyntheticRows(gameId).TakeLast(100).ToArray();
        Assert.Equal(100, points.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            var point = points[i];
            Assert.Equal(DateTime.Parse(row[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), point.GetProperty("timestamp").GetDateTime());
            Assert.Equal(double.Parse(row[2], CultureInfo.InvariantCulture), point.GetProperty("homeWinProbability").GetDouble());
            Assert.Equal(double.Parse(row[3], CultureInfo.InvariantCulture), point.GetProperty("awayWinProbability").GetDouble());
            Assert.Equal(int.Parse(row[4], CultureInfo.InvariantCulture), point.GetProperty("homeScore").GetInt32());
            Assert.Equal(int.Parse(row[5], CultureInfo.InvariantCulture), point.GetProperty("awayScore").GetInt32());
            Assert.Equal(row[6], $"Q{point.GetProperty("period").GetInt32()} {point.GetProperty("gameClock").GetString()}");
            Assert.Equal(row[7], point.GetProperty("source").GetString());
            Assert.Equal(bool.Parse(row[8]), point.GetProperty("isProbabilityBound").GetBoolean());
            Assert.False(point.GetProperty("isHistorical").GetBoolean());
        }
    }

    private static string[][] SyntheticRows(string gameId)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Excitement", "histories.csv");
        Assert.Equal("590d831763b985a75926d4a6c0bf2e61f2d18b4ce0ee867ea1b54d73c60273d5",
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        return File.ReadLines(path).Skip(1).Select(line => line.Split(',')).Where(row => row[0] == gameId).ToArray();
    }

    private static void AssertSyntheticChartFields(string gameId, JsonElement[] points)
    {
        var rows = SyntheticRows(gameId);
        Assert.Equal(rows.Length, points.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            var point = points[i];
            Assert.Equal(row[1], point.GetProperty("timestamp").GetString());
            Assert.Equal(double.Parse(row[2], CultureInfo.InvariantCulture), point.GetProperty("homeWinProbability").GetDouble());
            Assert.Equal(double.Parse(row[3], CultureInfo.InvariantCulture), point.GetProperty("awayWinProbability").GetDouble());
            Assert.Equal(int.Parse(row[4], CultureInfo.InvariantCulture), point.GetProperty("homeScore").GetInt32());
            Assert.Equal(int.Parse(row[5], CultureInfo.InvariantCulture), point.GetProperty("awayScore").GetInt32());
            Assert.Equal(row[6], point.GetProperty("label").GetString());
            Assert.Equal(row[7], point.GetProperty("source").GetString());
            Assert.Equal(bool.Parse(row[8]), point.GetProperty("isProbabilityBound").GetBoolean());
            if (i > 0) Assert.True(points[i - 1].GetProperty("timestamp").GetDateTime() < point.GetProperty("timestamp").GetDateTime());
        }
    }
}
