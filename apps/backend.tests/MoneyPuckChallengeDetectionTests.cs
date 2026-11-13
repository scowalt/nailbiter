using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using backend.Data;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;
using Xunit;

namespace backend.tests;

[Collection(nameof(SentryGlobalHubCollection))]
public sealed class MoneyPuckChallengeDetectionTests
{
    private const string JavaScriptDetections =
        "<script>(function(){function c(){var b=a.contentDocument;if(b){var d=b.createElement('script');"
        + "d.innerHTML=\"window.__CF$cv$params={r:'0000000000000000',t:'AAAA'};var u='/cdn-cgi/challenge-platform/scripts/jsd/main.js?ray=0000000000000000',"
        + "a=document.createElement('script');a.src=u;document.getElementsByTagName('head')[0].appendChild(a);\";"
        + "b.getElementsByTagName('head')[0].appendChild(d)}}if(document.body){var a=document.createElement('iframe');"
        + "a.style.visibility='hidden';document.body.appendChild(a);c()}})();</script>";

    private const string ManagedChallengePage =
        "<!DOCTYPE html><html lang=\"en-US\"><head><title>Just a moment...</title></head><body>"
        + "<noscript><span>Enable JavaScript and cookies to continue</span></noscript>"
        + "<script>(function(){window._cf_chl_opt={cvId:'3',cType:'managed',cRay:'0000000000000000'};"
        + "var a=document.createElement('script');a.src='/cdn-cgi/challenge-platform/h/g/orchestrate/chl_page/v1?ray=0000000000000000';"
        + "document.getElementsByTagName('head')[0].appendChild(a);}());</script></body></html>";

    private const string ChallengeOptionsOnly =
        "<!DOCTYPE html><html><head></head><body><script>window._cf_chl_opt={cType:'managed'};</script></body></html>";

    private const string OrchestrationScriptOnly =
        "<!DOCTYPE html><html><head></head><body><script>var a=document.createElement('script');"
        + "a.src='/cdn-cgi/challenge-platform/h/b/orchestrate/chl_page/v1?ray=0000000000000000';"
        + "document.getElementsByTagName('head')[0].appendChild(a);</script></body></html>";

    private const string JustAMomentTitleOnly =
        "<!DOCTYPE html><html><head><title>Just a moment...</title></head><body></body></html>";

    private const string AttentionRequiredTitleOnly =
        "<!DOCTYPE html><html><head><title>Attention Required! | Cloudflare</title></head><body></body></html>";

    private const string AccessDeniedTitleOnly =
        "<!DOCTYPE html><html><head><title>Access denied | moneypuck.com used Cloudflare to restrict access</title></head><body></body></html>";

    [Theory]
    [InlineData("A reader said the captcha on a ticket site kept failing.")]
    [InlineData("The arena app asks fans to verify you are human before they vote.")]
    [InlineData("Checking your browser history for old box scores is not required.")]
    [InlineData("Just a moment in the third period changed the series.")]
    [InlineData("Access denied to the press box was the coach's call.")]
    [InlineData("Enable JavaScript and cookies to use the interactive bracket.")]
    public async Task Challenge_phrases_in_normal_predictions_page_prose_collect_without_latching_or_reporting(string prose)
    {
        await using var app = await ReportingApp.StartAsync();
        app.Harness.Handler.Page = app.Harness.Handler.Page.Replace("</main>", $"<p>{prose}</p></main>");

        await app.AssertCollectedWithoutReportingAsync(await app.RefreshAsync());
    }

    [Fact]
    public async Task Challenge_phrase_in_an_inline_svg_title_on_a_normal_predictions_page_collects_without_latching_or_reporting()
    {
        await using var app = await ReportingApp.StartAsync();
        app.Harness.Handler.Page = app.Harness.Handler.Page
            .Replace("<html><body>", "<html><head><title>NHL Playoff Predictions | MoneyPuck</title></head><body>")
            .Replace("</main>", "<svg role=\"img\"><title>Access denied</title><path d=\"M0 0h1v1H0z\"/></svg></main>");

        await app.AssertCollectedWithoutReportingAsync(await app.RefreshAsync());
    }

    [Fact]
    public async Task Challenge_phrase_in_a_normal_simulations_csv_row_collects_without_latching_or_reporting()
    {
        await using var app = await ReportingApp.StartAsync();
        var lines = app.Harness.Handler.Csv.TrimEnd().Split('\n');
        app.Harness.Handler.Csv = string.Join('\n', lines.Select((line, i) => i switch
        {
            0 => line + ",note",
            1 => line + ",Access denied to the press box",
            _ => line + ",",
        })) + "\n";

        await app.AssertCollectedWithoutReportingAsync(await app.RefreshAsync());
    }

    [Theory]
    [InlineData(200, "challenge")]
    [InlineData(503, "challenge")]
    [InlineData(200, "block, challenge")]
    public async Task Cloudflare_challenge_header_latches_and_reports_one_error(int status, string mitigated)
    {
        await using var app = await ReportingApp.StartAsync();
        var page = app.Harness.Handler.Page;
        app.Harness.Handler.Override = (request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri != MoneyPuckQualificationHandler.PageUrl) return Task.FromResult<HttpResponseMessage?>(null);
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(page) };
            response.Headers.Add("cf-mitigated", mitigated);
            return Task.FromResult<HttpResponseMessage?>(response);
        };

        await app.AssertLatchedWithOneErrorAsync(await app.RefreshAsync());
    }

    [Fact]
    public async Task Cloudflare_javascript_detections_on_a_normal_predictions_page_collect_without_latching_or_reporting()
    {
        await using var app = await ReportingApp.StartAsync();
        app.Harness.Handler.Page = app.Harness.Handler.Page.Replace("</body>", JavaScriptDetections + "</body>");

        await app.AssertCollectedWithoutReportingAsync(await app.RefreshAsync());
    }

    [Theory]
    [InlineData("/cdn-cgi/challenge-platform/h/g/scripts/jsd/000000000000/main.js")]
    [InlineData("/cdn-cgi/challenge-platform/h/b/scripts/jsd/000000000000/main.js")]
    [InlineData("/cdn-cgi/challenge-platform/h/b/scripts/invisible.js")]
    public async Task Cloudflare_scripts_on_a_normal_predictions_page_collect_without_latching_or_reporting(string script)
    {
        await using var app = await ReportingApp.StartAsync();
        app.Harness.Handler.Page = app.Harness.Handler.Page.Replace("</body>", $"<script src=\"{script}\"></script></body>");

        await app.AssertCollectedWithoutReportingAsync(await app.RefreshAsync());
    }

    [Theory]
    [InlineData(200, ManagedChallengePage)]
    [InlineData(200, ChallengeOptionsOnly)]
    [InlineData(200, OrchestrationScriptOnly)]
    [InlineData(200, JustAMomentTitleOnly)]
    [InlineData(200, AttentionRequiredTitleOnly)]
    [InlineData(200, AccessDeniedTitleOnly)]
    [InlineData(401, "")]
    [InlineData(403, ManagedChallengePage)]
    [InlineData(429, "")]
    public async Task Challenge_or_refusal_on_the_predictions_page_latches_and_reports_one_error(int status, string body)
    {
        await using var app = await ReportingApp.StartAsync();
        app.Harness.Handler.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(
            request.RequestUri!.AbsoluteUri == MoneyPuckQualificationHandler.PageUrl
                ? new((HttpStatusCode)status) { Content = new StringContent(body) }
                : null);

        await app.AssertLatchedWithOneErrorAsync(await app.RefreshAsync());
    }

    private sealed record SentEvent(string? Logger, string? Level, string?[] Exceptions);

    private sealed class ReportingApp : IAsyncDisposable
    {
        private readonly SentTransport transport = new();

        public MoneyPuckQualificationHarness Harness { get; } = new();

        public static async Task<ReportingApp> StartAsync()
        {
            var app = new ReportingApp();
            await app.Harness.StartAsync(configureServices: services => services.PostConfigure<SentryAspNetCoreOptions>(options =>
            {
                options.Dsn = "https://public@sentry.invalid/1";
                options.Transport = app.transport;
            }));
            return app;
        }

        public Task<MoneyPuckRefreshResult> RefreshAsync() =>
            Harness.App.Services.GetRequiredService<MoneyPuckService>().RefreshNhlAsync(CancellationToken.None);

        public async Task AssertCollectedWithoutReportingAsync(MoneyPuckRefreshResult result)
        {
            Assert.Equal((32, 32), (result.Qualification, result.Rankings));
            Assert.Equal(32, (await Harness.RowsAsync()).Count);
            Assert.False(await DeniedAsync());
            Assert.Empty(await SentEventsAsync());
        }

        public async Task AssertLatchedWithOneErrorAsync(MoneyPuckRefreshResult result)
        {
            Assert.Equal((0, 0), (result.Qualification, result.Rankings));
            Assert.True(await DeniedAsync());
            var sent = Assert.Single(await SentEventsAsync());
            Assert.Equal((typeof(MoneyPuckService).FullName, "error"), (sent.Logger, sent.Level));
            Assert.Contains(sent.Exceptions, type => type == typeof(HttpRequestException).FullName);
        }

        private async Task<bool> DeniedAsync()
        {
            using var scope = Harness.App.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Database
                .SqlQueryRaw<bool>("SELECT denied AS \"Value\" FROM moneypuck_refresh_state WHERE id = 1").SingleAsync();
        }

        public async Task<IReadOnlyCollection<SentEvent>> SentEventsAsync()
        {
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));
            return transport.Events.ToArray();
        }

        public ValueTask DisposeAsync() => Harness.DisposeAsync();
    }

    private sealed class SentTransport : ITransport
    {
        public ConcurrentQueue<SentEvent> Events { get; } = new();

        public async Task SendEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken = default)
        {
            using var stream = new MemoryStream();
            await envelope.SerializeAsync(stream, null, cancellationToken);
            var lines = System.Text.Encoding.UTF8.GetString(stream.ToArray()).Split('\n');
            for (var i = 1; i + 1 < lines.Length; i++)
            {
                using var header = JsonDocument.Parse(lines[i]);
                if (header.RootElement.GetProperty("type").GetString() != "event") continue;
                using var payload = JsonDocument.Parse(lines[++i]);
                var root = payload.RootElement;
                var exceptions = root.TryGetProperty("exception", out var exception)
                    ? exception.GetProperty("values").EnumerateArray().Select(e => Text(e, "type")).ToArray()
                    : [];
                Events.Enqueue(new SentEvent(Text(root, "logger"), Text(root, "level"), exceptions));
            }
        }

        private static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) ? value.GetString() : null;
    }
}
