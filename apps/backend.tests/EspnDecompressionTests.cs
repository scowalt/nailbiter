using System.IO.Compression;
using System.Text;
using backend.Services.Espn;
using backend.Services.EspnNascar;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Xunit;

namespace backend.tests;

public sealed class EspnDecompressionTests
{
    private const string Scoreboard = "{\"leagues\":[],\"events\":[]}";

    [Theory]
    [InlineData("gzip")]
    [InlineData("deflate")]
    [InlineData("br")]
    [InlineData("identity")]
    public async Task Scoreboard_scrapers_accept_compressed_and_plain_responses(string encoding)
    {
        using var entryPoint = new AppFactory();
        using var app = CreateApp(entryPoint, encoding);
        using var scope = app.Services.CreateScope();
        var mlb = await scope.ServiceProvider.GetRequiredService<EspnOddsService>()
            .FetchScoreboardAsync("mlb", null, null);
        Assert.NotNull(mlb);
        Assert.Empty(mlb.Events);
        var nascar = await scope.ServiceProvider.GetRequiredService<EspnNascarService>()
            .FetchScoreboardAsync(null, null);
        Assert.NotNull(nascar);
        Assert.Empty(nascar.Events);
    }

    [Theory]
    [InlineData("EspnOddsService")]
    [InlineData("EspnPowerIndexService")]
    [InlineData("EspnStandingsService")]
    [InlineData("NflBackfillService")]
    [InlineData("EspnF1Service")]
    [InlineData("EspnNascarService")]
    [InlineData("EspnIndyCarService")]
    public async Task Every_registered_ESPN_client_decodes_gzip(string clientName)
    {
        using var entryPoint = new AppFactory();
        using var app = CreateApp(entryPoint, "gzip");
        using var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);
        Assert.Equal(Scoreboard, await client.GetStringAsync("https://site.web.api.espn.com/scoreboard"));
    }

    private static WebApplicationFactory<Program> CreateApp(AppFactory entryPoint, string encoding) =>
        entryPoint.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.ConfigureAll<HttpClientFactoryOptions>(options =>
                options.HttpMessageHandlerBuilderActions.Add(http =>
                {
                    var transport = Assert.IsType<SocketsHttpHandler>(http.PrimaryHandler);
                    transport.UseProxy = false;
                    transport.ConnectCallback = (_, _) => ValueTask.FromResult<Stream>(ResponseStream(encoding));
                    http.AdditionalHandlers.Add(new PlaintextRequestHandler());
                }))));

    private static Stream ResponseStream(string encoding)
    {
        using var body = new MemoryStream();
        if (encoding == "identity")
            body.Write(Encoding.UTF8.GetBytes(Scoreboard));
        else
        {
            using Stream compressor = encoding switch
            {
                "gzip" => new GZipStream(body, CompressionMode.Compress, leaveOpen: true),
                "deflate" => new ZLibStream(body, CompressionMode.Compress, leaveOpen: true),
                "br" => new BrotliStream(body, CompressionMode.Compress, leaveOpen: true),
                _ => throw new ArgumentOutOfRangeException(nameof(encoding))
            };
            compressor.Write(Encoding.UTF8.GetBytes(Scoreboard));
        }
        var contentEncoding = encoding == "identity" ? "" : $"Content-Encoding: {encoding}\r\n";
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n{contentEncoding}Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        return new HttpResponseStream([.. headers, .. body.ToArray()]);
    }

    private sealed class PlaintextRequestHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.RequestUri = new UriBuilder(request.RequestUri!) { Scheme = "http", Port = 80 }.Uri;
            return base.SendAsync(request, ct);
        }
    }

    private sealed class HttpResponseStream(byte[] response) : Stream
    {
        private readonly MemoryStream input = new(response);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => input.ReadAsync(buffer, ct);
        public override void Write(byte[] buffer, int offset, int count) { }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => ValueTask.CompletedTask;
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) input.Dispose();
            base.Dispose(disposing);
        }
    }
}
