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

    [Fact]
    public async Task Cloudflare_javascript_detections_on_a_normal_predictions_page_collect_without_latching_or_reporting()
    {
        await using var app = await ReportingApp.StartAsync();
        app.Harness.Handler.Page = app.Harness.Handler.Page.Replace("</body>", JavaScriptDetections + "</body>");

        var result = await app.RefreshAsync();

        Assert.Equal((32, 32), (result.Qualification, result.Rankings));
        Assert.False(await app.DeniedAsync());
        Assert.Empty(await app.SentEventsAsync());
    }

    [Theory]
    [InlineData("/cdn-cgi/challenge-platform/h/g/scripts/jsd/000000000000/main.js")]
    [InlineData("/cdn-cgi/challenge-platform/h/b/scripts/jsd/000000000000/main.js")]
    [InlineData("/cdn-cgi/challenge-platform/h/b/scripts/invisible.js")]
    public async Task Cloudflare_scripts_on_a_normal_predictions_page_collect_without_latching_or_reporting(string script)
    {
        await using var app = await ReportingApp.StartAsync();
        app.Harness.Handler.Page = app.Harness.Handler.Page.Replace("</body>", $"<script src=\"{script}\"></script></body>");

        var result = await app.RefreshAsync();

        Assert.Equal((32, 32), (result.Qualification, result.Rankings));
        Assert.Equal(32, (await app.Harness.RowsAsync()).Count);
        Assert.False(await app.DeniedAsync());
        Assert.Empty(await app.SentEventsAsync());
    }

    [Theory]
    [InlineData(200, ManagedChallengePage)]
    [InlineData(200, ChallengeOptionsOnly)]
    [InlineData(200, OrchestrationScriptOnly)]
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

        var result = await app.RefreshAsync();

        Assert.Equal((0, 0), (result.Qualification, result.Rankings));
        Assert.True(await app.DeniedAsync());
        var sent = Assert.Single(await app.SentEventsAsync());
        Assert.Equal((typeof(MoneyPuckService).FullName, "error"), (sent.Logger, sent.Level));
        Assert.Contains(sent.Exceptions, type => type == typeof(HttpRequestException).FullName);
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

        public async Task<bool> DeniedAsync()
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
