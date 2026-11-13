using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using ListingCheck;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace backend.tests;

public sealed class ListingCheckTests : IAsyncLifetime
{
    private const string BodyText = "stub-only-body-text";
    private const string Home = $"<!DOCTYPE html><html><body><div class=\"home-schedule page\"><section class=\"home-section\">{BodyText}</section></div></body></html>";
    private const string League = $"<!DOCTYPE html><html><body><h1 class=\"league-title\">NHL</h1><div class=\"league-games\">{BodyText}</div></body></html>";
    private const string EmptyLeague = "<!DOCTYPE html><html><body><h1 class=\"league-title\">NHL</h1><p class=\"empty-state-text\">No games.</p></body></html>";
    private const string Budgets = "--samples 1 --warmup 0 --ttfb-budget-ms 100 --completion-budget-ms 250 --request-timeout-ms 3000";
    private readonly ConcurrentQueue<string> requests = new();
    private readonly string reportPath = Path.Combine(Path.GetTempPath(), $"listing-check-{Guid.NewGuid():N}.json");
    private WebApplication stub = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        stub = builder.Build();
        stub.Use(async (context, next) =>
        {
            requests.Enqueue(context.Request.Method + " " + context.Request.Path + context.Request.QueryString);
            context.Response.Headers.SetCookie = "session=stub-cookie";
            await next();
        });
        stub.MapGet("/", () => Results.Content(Home, "text/html"));
        stub.MapGet("/nhl", (string? date) => Results.Content(date == "2000-01-01" ? EmptyLeague : League, "text/html"));
        stub.MapGet("/nba", async (HttpContext context) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted);
            return Results.Content(League, "text/html");
        });
        stub.MapGet("/mlb", () => Results.Content(League, "text/html", statusCode: 500));
        stub.MapGet("/nfl", () => Results.Content("<!DOCTYPE html><html><body>maintenance</body></html>", "text/html"));
        stub.MapGet("/cfb", async () =>
        {
            await Task.Delay(400);
            return Results.Content(League, "text/html");
        });
        await stub.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await stub.DisposeAsync();
        File.Delete(reportPath);
    }

    [Fact]
    public async Task Healthy_listings_pass_with_get_only_requests_and_a_report_without_headers_or_bodies()
    {
        var output = new StringWriter();
        var exit = await RunAsync(output, $"--base-url {stub.Urls.Single()} --path / --path /nhl?date=2026-10-13 --path /nhl?date=2000-01-01 "
            + $"--samples 2 --warmup 1 --ttfb-budget-ms 10000 --completion-budget-ms 20000 --request-timeout-ms 30000 --report {reportPath}");

        Assert.True(exit == 0, output.ToString());
        Assert.Equal(9, requests.Count);
        Assert.All(requests, request => Assert.StartsWith("GET ", request));
        var report = await File.ReadAllTextAsync(reportPath);
        using var json = JsonDocument.Parse(report);
        Assert.Equal("pass", json.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(3, json.RootElement.GetProperty("pages").GetArrayLength());
        Assert.All(json.RootElement.GetProperty("pages").EnumerateArray(), page => Assert.True(page.GetProperty("withinBudget").GetBoolean()));
        foreach (var forbidden in new[] { "cookie", "authorization", BodyText, "No games." })
            Assert.DoesNotContain(forbidden, report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("outcome: pass (exit 0)", output.ToString());
    }

    [Theory]
    [InlineData("/nba", "deadline", 4)]
    [InlineData("/mlb", "request", 3)]
    [InlineData("/nfl", "content", 5)]
    [InlineData("/cfb", "budget", 7)]
    public async Task Misbehaving_listing_fails_with_its_failure_class(string path, string outcome, int exitCode)
    {
        var output = new StringWriter();
        var exit = await RunAsync(output, $"--base-url {stub.Urls.Single()} --path {path} {Budgets} --report {reportPath}");

        Assert.True(exit == exitCode, output.ToString());
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
        Assert.Equal(outcome, json.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(outcome, json.RootElement.GetProperty("failures")[0].GetProperty("kind").GetString());
        Assert.Equal(new[] { "GET " + path }, requests);
    }

    [Theory]
    [InlineData("--base-url {0} --completion-budget-ms 250")]
    [InlineData("--base-url {0} --samples 6 --ttfb-budget-ms 100 --completion-budget-ms 250")]
    [InlineData("--base-url {0} --ttfb-budget-ms 300 --completion-budget-ms 250")]
    [InlineData("--base-url http://nailbiter.invalid --ttfb-budget-ms 100 --completion-budget-ms 250")]
    [InlineData("--base-url http://user:secret@127.0.0.1:9 --ttfb-budget-ms 100 --completion-budget-ms 250")]
    [InlineData("--base-url {0} --path /game/1 --ttfb-budget-ms 100 --completion-budget-ms 250")]
    [InlineData("--base-url {0} --header Cookie=1 --ttfb-budget-ms 100 --completion-budget-ms 250")]
    public async Task Invalid_configuration_exits_2_without_sending_a_request(string arguments)
    {
        var output = new StringWriter();
        var exit = await RunAsync(output, string.Format(arguments, stub.Urls.Single()) + $" --report {reportPath}");

        Assert.True(exit == 2, output.ToString());
        Assert.StartsWith("invalid-config: ", output.ToString());
        Assert.Empty(requests);
        Assert.False(File.Exists(reportPath));
    }

    private static Task<int> RunAsync(TextWriter output, string arguments) =>
        ListingCheckCommand.RunUrlAsync(arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries), output, CancellationToken.None);
}
