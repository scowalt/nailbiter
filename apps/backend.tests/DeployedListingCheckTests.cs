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

public sealed class DeployedListingCheckTests : IAsyncLifetime
{
    private const string Released = "1111111111111111111111111111111111111111";
    private const string Previous = "2222222222222222222222222222222222222222";
    private const string Home = "<!DOCTYPE html><html><body><div class=\"home-schedule page\"><section class=\"home-section\">games</section></div></body></html>";
    private const string League = "<!DOCTYPE html><html><body><h1 class=\"league-title\">NHL</h1><div class=\"league-games\">games</div></body></html>";
    private const string EmptyLeague = "<!DOCTYPE html><html><body><h1 class=\"league-title\">NHL</h1><p class=\"empty-state-text\">No games.</p></body></html>";
    private const string Budgets = "--samples 1 --warmup 0 --ttfb-budget-ms 100 --completion-budget-ms 250 --request-timeout-ms 3000";
    private readonly ConcurrentQueue<string> requests = new();
    private readonly string reportPath = Path.Combine(Path.GetTempPath(), $"deployed-listing-check-{Guid.NewGuid():N}.json");
    private Func<int, (int Status, string? Revision)> health = _ => (200, Released);
    private int healthPolls;
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
            context.Response.Headers["X-Nailbiter-Revision"] = Released;
            await next();
        });
        stub.MapGet("/health", (HttpContext context) =>
        {
            var (status, revision) = health(Interlocked.Increment(ref healthPolls));
            if (revision is null) context.Response.Headers.Remove("X-Nailbiter-Revision");
            else context.Response.Headers["X-Nailbiter-Revision"] = revision;
            return Results.Json(new { status = "healthy" }, statusCode: status);
        });
        stub.MapGet("/", () => Results.Content(Home, "text/html"));
        stub.MapGet("/nhl", (string? date) => Results.Content(date == "2000-01-01" ? EmptyLeague : League, "text/html"));
        stub.MapGet("/nba", (HttpContext context) =>
        {
            context.Response.Headers["X-Nailbiter-Revision"] = Previous;
            return Results.Content(League, "text/html");
        });
        stub.MapGet("/mlb", () => Results.Content(League, "text/html", statusCode: 503));
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
    public async Task Release_passes_once_health_serves_its_revision_and_every_listing_comes_from_it()
    {
        health = poll => (200, poll == 1 ? Previous : Released);
        var output = new StringWriter();

        var exit = await RunAsync(output, $"--base-url {stub.Urls.Single()} --expected-revision {Released} --ready-within-ms 30000 "
            + "--path / --path /nhl?date=2000-01-01 --samples 2 --warmup 1 --ttfb-budget-ms 10000 --completion-budget-ms 20000 "
            + $"--request-timeout-ms 30000 --report {reportPath}");

        Assert.True(exit == 0, output.ToString());
        Assert.Equal(["GET /health", "GET /health", "GET /", "GET /nhl?date=2000-01-01", "GET /", "GET /nhl?date=2000-01-01", "GET /",
            "GET /nhl?date=2000-01-01"], requests);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
        var readiness = json.RootElement.GetProperty("readiness");
        Assert.True(readiness.GetProperty("ready").GetBoolean());
        Assert.Equal(2, readiness.GetProperty("polls").GetInt32());
        Assert.Equal(Released, readiness.GetProperty("servedRevision").GetString());
        Assert.Equal(Released, json.RootElement.GetProperty("config").GetProperty("expectedRevision").GetString());
        Assert.Contains($"readiness: /health served {Released} after 2 polls", output.ToString());
        Assert.Contains("outcome: pass (exit 0)", output.ToString());
    }

    [Theory]
    [InlineData(200, Previous, $"served revision {Previous}")]
    [InlineData(503, Released, "status 503")]
    [InlineData(200, null, "served no revision")]
    [InlineData(200, "not-a-revision", "served an unrecognized revision")]
    public async Task Readiness_expires_without_requesting_a_listing_when_health_never_serves_the_release(int status, string? revision,
        string lastPoll)
    {
        health = _ => (status, revision);
        var output = new StringWriter();

        var exit = await RunAsync(output, $"--base-url {stub.Urls.Single()} --expected-revision {Released} --ready-within-ms 1000 "
            + $"--path / {Budgets} --report {reportPath}");

        Assert.True(exit == 8, output.ToString());
        Assert.Equal(["GET /health", "GET /health"], requests);
        var report = await File.ReadAllTextAsync(reportPath);
        using var json = JsonDocument.Parse(report);
        Assert.Equal("readiness", json.RootElement.GetProperty("outcome").GetString());
        Assert.False(json.RootElement.GetProperty("readiness").GetProperty("ready").GetBoolean());
        Assert.Empty(json.RootElement.GetProperty("samples").EnumerateArray());
        Assert.Contains($"readiness /health: revision {Released} not served within 1000 ms after 2 polls; last poll: {lastPoll}", output.ToString());
        Assert.DoesNotContain("not-a-revision", report);
    }

    [Theory]
    [InlineData("/nba", "revision", 9, $"served revision {Previous}, expected {Released}")]
    [InlineData("/mlb", "request", 3, "status 503")]
    [InlineData("/nfl", "content", 5, "no league-title")]
    [InlineData("/cfb", "budget", 7, "exceeds 100 ms")]
    public async Task Ready_release_with_a_misbehaving_listing_fails_with_its_failure_class(string path, string outcome, int exitCode,
        string detail)
    {
        var output = new StringWriter();

        var exit = await RunAsync(output, $"--base-url {stub.Urls.Single()} --expected-revision {Released} --ready-within-ms 1000 "
            + $"--path {path} {Budgets} --report {reportPath}");

        Assert.True(exit == exitCode, output.ToString());
        Assert.Equal(["GET /health", "GET " + path], requests);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
        Assert.Equal(outcome, json.RootElement.GetProperty("outcome").GetString());
        Assert.Contains(detail, json.RootElement.GetProperty("failures")[0].GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("/mlb")]
    [InlineData("/nfl")]
    [InlineData("/cfb")]
    public async Task A_response_from_another_revision_outranks_every_other_listing_failure(string otherPath)
    {
        var output = new StringWriter();

        var exit = await RunAsync(output, $"--base-url {stub.Urls.Single()} --expected-revision {Released} --ready-within-ms 1000 "
            + $"--path {otherPath} --path /nba {Budgets} --report {reportPath}");

        Assert.True(exit == 9, output.ToString());
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
        Assert.Equal("revision", json.RootElement.GetProperty("outcome").GetString());
        Assert.Contains(json.RootElement.GetProperty("failures").EnumerateArray(), failure => failure.GetProperty("kind").GetString() != "revision");
    }

    [Theory]
    [InlineData("--expected-revision main")]
    [InlineData("--expected-revision 1111111")]
    [InlineData("--expected-revision AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("--ready-within-ms 1000")]
    [InlineData("--expected-revision 1111111111111111111111111111111111111111 --ready-within-ms 0")]
    [InlineData("--expected-revision 1111111111111111111111111111111111111111 --ready-within-ms 600001")]
    public async Task Invalid_revision_options_exit_2_without_sending_a_request(string arguments)
    {
        var output = new StringWriter();

        var exit = await RunAsync(output, $"--base-url {stub.Urls.Single()} {arguments} {Budgets} --report {reportPath}");

        Assert.True(exit == 2, output.ToString());
        Assert.Empty(requests);
        Assert.False(File.Exists(reportPath));
    }

    private static async Task<int> RunAsync(TextWriter output, string arguments)
    {
        int? reported = null;
        var exit = await ListingCheckCommand.RunUrlAsync(arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries), output, CancellationToken.None,
            code => reported = code);
        Assert.Equal(exit == (int)FailureKind.InvalidConfig ? null : exit, reported);
        return exit;
    }
}
