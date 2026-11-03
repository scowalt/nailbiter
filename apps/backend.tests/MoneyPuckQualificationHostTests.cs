using System.Net;
using System.Text;
using System.Text.Json;
using backend.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Xunit;

namespace backend.tests;

public sealed class MoneyPuckQualificationHostTests
{
    private sealed class OfflineUnusedProviders : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task Default_host_controls_are_off_without_provider_requests()
    {
        await using var entry = new AppFactory();
        var requests = new List<string>();
        await using var app = entry.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            ConfigureWire(services, HttpStatusCode.Forbidden, requests)));
        var service = app.Services.GetRequiredService<MoneyPuckQualificationService>();
        Assert.Equal(0, await service.RefreshNhlRatingsForSeasonAsync(2027, default));
        var settings = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<backend.Models.PlayoffRatingsOptions>>().Value;
        Assert.False(settings.MoneyPuckRefreshEnabled);
        Assert.False(settings.MoneyPuckSelectionEnabled);
        Assert.Empty(settings.MoneyPuckApprovedSeasons);
        Assert.Empty(requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Configured_transport_stops_without_redirects_or_same_day_retries_then_permits_next_day(HttpStatusCode status)
    {
        await using var h = new MoneyPuckQualificationHarness();
        var requests = new List<string>();
        await h.StartAsync(configureServices: services => ConfigureWire(services, status, requests), configuredTransport: true);
        var service = h.Service;
        Assert.Equal(0, await service.RefreshNhlRatingsForSeasonAsync(2027, default));
        Assert.Single(requests);
        Assert.Contains("GET /moneypuck/simulations/update_date.txt HTTP/1.1", requests[0]);
        Assert.Contains("Host: moneypuck.com", requests[0]);
        Assert.Equal(0, await service.RefreshNhlRatingsForSeasonAsync(2027, default));
        Assert.Single(requests);
        h.Clock.Now = h.Clock.Now.AddHours(24).AddTicks(-1);
        Assert.Equal(0, await service.RefreshNhlRatingsForSeasonAsync(2027, default));
        Assert.Single(requests);
        h.Clock.Now = h.Clock.Now.AddTicks(1);
        Assert.Equal(0, await service.RefreshNhlRatingsForSeasonAsync(2027, default));
        Assert.Equal(2, requests.Count);
        Assert.All(requests, request => Assert.DoesNotContain("offline-redirect", request));
    }

    private static void ConfigureWire(IServiceCollection services, HttpStatusCode status, List<string> requests)
    {
        services.AddHttpClient<MoneyPuckService>()
            .ConfigurePrimaryHttpMessageHandler((handler, _) =>
            {
                var transport = (SocketsHttpHandler)handler;
                transport.UseProxy = false;
                transport.ConnectCallback = (_, _) => ValueTask.FromResult<Stream>(new OfflineResponseStream(status, requests));
            })
            .AddHttpMessageHandler(() => new OfflinePlaintextRequestHandler());
    }

    private sealed class OfflinePlaintextRequestHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.RequestUri = new UriBuilder(request.RequestUri!) { Scheme = "http", Port = 80 }.Uri;
            return base.SendAsync(request, ct);
        }
    }

    private sealed class OfflineResponseStream(HttpStatusCode status, List<string> requests) : Stream
    {
        private readonly MemoryStream input = new(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {(int)status} Offline fixture\r\nLocation: http://offline-redirect.invalid/blocked\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        private readonly MemoryStream written = new();
        private bool recorded;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => input.ReadAsync(buffer, ct);
        public override void Write(byte[] buffer, int offset, int count) => Record(buffer.AsSpan(offset, count));
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Record(buffer.Span);
            return ValueTask.CompletedTask;
        }
        private void Record(ReadOnlySpan<byte> bytes)
        {
            written.Write(bytes);
            var request = Encoding.ASCII.GetString(written.ToArray());
            if (!recorded && request.Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                lock (requests) requests.Add(request);
                recorded = true;
            }
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { input.Dispose(); written.Dispose(); }
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task Actual_background_and_manual_HTTP_share_gate_and_cooldown_while_pages_never_collect()
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync(configureServices: services => services.ConfigureAll<HttpClientFactoryOptions>(options => options.HttpMessageHandlerBuilderActions.Add(builder =>
        {
            if (!builder.Name!.EndsWith(nameof(MoneyPuckService), StringComparison.Ordinal)) builder.PrimaryHandler = new OfflineUnusedProviders();
        })));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = true;
        h.Handler.Override = async (_, ct) =>
        {
            if (first) { first = false; entered.SetResult(); await release.Task.WaitAsync(ct); }
            return null;
        };
        using var worker = ActivatorUtilities.CreateInstance<MoneyPuckRefreshBackgroundService>(h.App.Services);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var manual = await h.Client.PostAsync("/api/ratings/refresh", null);
            Assert.True(manual.IsSuccessStatusCode, await manual.Content.ReadAsStringAsync());
            using var result = JsonDocument.Parse(await manual.Content.ReadAsStringAsync());
            Assert.Equal(0, result.RootElement.GetProperty("nhl_moneypuckQualification").GetInt32());
            Assert.Equal(0, result.RootElement.GetProperty("nhl_powerRankings").GetInt32());
            Assert.Single(h.Handler.Requests);
            release.SetResult();
            for (var i = 0; i < 100 && (await h.RowsAsync("moneypuck")).Count != 32; i++) await Task.Delay(50);
            Assert.Equal(.2518, (await h.SelectAsync()).Probability.Value);
        }
        finally
        {
            release.TrySetResult();
            await worker.StopAsync(CancellationToken.None);
        }
        h.Clock.Now = h.Clock.Now.AddHours(24).AddTicks(-1);
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(9, h.Handler.Requests.Count);
        h.Clock.Now = h.Clock.Now.AddTicks(1);
        var response = await h.Client.PostAsync("/api/ratings/refresh", null);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(32, body.RootElement.GetProperty("nhl_moneypuckQualification").GetInt32());
        Assert.Equal(32, body.RootElement.GetProperty("nhl_powerRankings").GetInt32());
        Assert.Equal(18, h.Handler.Requests.Count);
        foreach (var path in new[] { "/", "/nhl?date=2026-10-11", "/game/missing-synthetic" })
            await h.Client.GetAsync(path);
        Assert.Equal(18, h.Handler.Requests.Count);
    }
}
