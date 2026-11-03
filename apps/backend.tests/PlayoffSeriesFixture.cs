using System.Net;
using System.Text.Json.Nodes;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using backend.Services;
using backend.Services.Espn;
using backend.Services.Kalshi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace backend.tests;

internal sealed class PlayoffSeriesFixture : IAsyncLifetime
{
    public HostedGameFixture Host { get; }
    public SeriesProvider Provider { get; } = new();

    public PlayoffSeriesFixture()
    {
        Host = new HostedGameFixture(new DateTimeOffset(2026, 10, 1, 19, 15, 0, TimeSpan.Zero), services =>
        {
            services.AddTransient(s => new KalshiOddsService(new HttpClient(Provider), NullLogger<KalshiOddsService>.Instance, s.GetRequiredService<TimeProvider>()));
            services.AddTransient(s => new EspnOddsService(new HttpClient(Provider), NullLogger<EspnOddsService>.Instance,
                s, s.GetRequiredService<IGameUpdateService>(), s.GetRequiredService<KalshiOddsService>()));
            services.AddTransient(s => new ProCompetitionEvidenceService(new HttpClient(Provider), s.GetRequiredService<backend.Data.ExcitementDbContext>(),
                s.GetRequiredService<TimeProvider>(), NullLogger<ProCompetitionEvidenceService>.Instance));
        });
    }

    public async Task InitializeAsync()
    {
        await Host.InitializeAsync();
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<backend.Data.ExcitementDbContext>();
        foreach (var migration in new[] { "034_PlayoffSeriesObservation.sql", "035_ProHypeLifecycle.sql" })
            await db.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", migration)));
    }
    public Task DisposeAsync() => Host.DisposeAsync();

    public async Task CollectAsync(string sport = "mlb")
    {
        using var scope = Host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<EspnOddsService>().ScrapeAndStoreOddsAsync(sport, null);
    }

    public async Task<IDocument> HomeAsync() => await new HtmlParser().ParseDocumentAsync(await Host.Client.GetStringAsync("/"));

    public static JsonNode SyntheticBoard() => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
        "Fixtures", "ProHype", "mlb-pregame-401907973.json")))!["board"]!["body"]!.DeepClone();

    internal sealed class SeriesProvider : HttpMessageHandler
    {
        public JsonNode Board { get; set; } = SyntheticBoard();
        public string Date { get; set; } = "20261001";
        public bool TransportFailure { get; set; }
        private int requests;
        private PendingResponse? pending;
        private PendingResponse? pendingMarket;
        public int Requests => requests;

        public PendingResponse HoldNextBoard()
        {
            var held = new PendingResponse();
            if (Interlocked.CompareExchange(ref pending, held, null) is not null) throw new InvalidOperationException("A response is already held");
            return held;
        }

        public PendingResponse HoldNextMarket()
        {
            var held = new PendingResponse();
            if (Interlocked.CompareExchange(ref pendingMarket, held, null) is not null) throw new InvalidOperationException("A market response is already held");
            return held;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requests);
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("/scoreboard"))
            {
                var matches = url.Contains("dates=" + Date);
                var response = new HttpResponseMessage(TransportFailure ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
                {
                    Content = new StringContent(matches ? Board.ToJsonString() : "{\"events\":[]}")
                };
                var held = matches ? Interlocked.Exchange(ref pending, null) : null;
                if (held is not null)
                {
                    held.Requested.TrySetResult();
                    await held.Released.Task.WaitAsync(cancellationToken);
                }
                return response;
            }
            if (url.Contains("/schedule"))
            {
                var capture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
                    "Fixtures", "ProHype", "mlb-pregame-401907973.json")))!;
                var schedule = capture["schedules"]!.AsArray().Single(s => new Uri(s!["url"]!.GetValue<string>()).PathAndQuery == request.RequestUri.PathAndQuery)!;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(schedule["body"]!.ToJsonString()) };
            }
            if (url.Contains("/teams?"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"sports\":[{\"leagues\":[{\"season\":{\"year\":2026},\"teams\":[{\"team\":{\"id\":\"15\",\"abbreviation\":\"ATL\"}},{\"team\":{\"id\":\"22\",\"abbreviation\":\"PHI\"}}]}]}]}") };
            if (url.Contains("kalshi"))
            {
                var held = url.Contains("/markets?") ? Interlocked.Exchange(ref pendingMarket, null) : null;
                if (held is not null)
                {
                    held.Requested.TrySetResult();
                    await held.Released.Task.WaitAsync(cancellationToken);
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"markets\":[],\"events\":[],\"cursor\":\"\"}") };
            }
            throw new InvalidOperationException("Unexpected provider request: " + url);
        }

        public sealed class PendingResponse
        {
            internal TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task WaitUntilRequestedAsync() => Requested.Task.WaitAsync(TimeSpan.FromSeconds(20));
            public void Release() => Released.TrySetResult();
        }
    }
}
