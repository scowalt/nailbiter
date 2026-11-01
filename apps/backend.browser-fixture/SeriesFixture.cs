using System.Net;
using System.Text.Json.Nodes;
using backend.Data;
using backend.GraphQL.Types;
using backend.Models;
using backend.Services;
using backend.Services.Espn;
using backend.Services.Kalshi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BrowserFixture;

internal sealed class SeriesFixture : TimeProvider
{
    public const string Scheduled = "401907973";
    public const string LongRound = "series-nhl-long";
    public const string Live = "series-mlb-live";
    public const string Final = "series-nba-final";
    public const string Opening = "series-mlb-opening";
    public const string Baseline = "series-mlb-baseline";
    public static readonly string[] Cards = [Scheduled, LongRound, Live, Final, Opening, Baseline];
    private readonly Handler upstream = new();
    public override DateTimeOffset GetUtcNow() => new(2026, 10, 1, 19, 15, 0, TimeSpan.Zero);

    public void Configure(IServiceCollection services)
    {
        services.RemoveAll<TimeProvider>();
        services.AddSingleton<TimeProvider>(this);
        services.AddTransient(s => new KalshiOddsService(new HttpClient(upstream), NullLogger<KalshiOddsService>.Instance, this));
        services.AddTransient(s => new EspnOddsService(new HttpClient(upstream), NullLogger<EspnOddsService>.Instance,
            s, s.GetRequiredService<IGameUpdateService>(), s.GetRequiredService<KalshiOddsService>()));
        services.AddTransient(s => new ProCompetitionEvidenceService(new HttpClient(upstream), s.GetRequiredService<ExcitementDbContext>(),
            this, NullLogger<ProCompetitionEvidenceService>.Instance));
    }

    public async Task InitializeAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var collector = scope.ServiceProvider.GetRequiredService<EspnOddsService>();
        await collector.ScrapeAndStoreOddsAsync("mlb", null);
        await collector.ScrapeAndStoreOddsAsync("nhl", null);
        await collector.ScrapeAndStoreOddsAsync("nba", null);
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        db.TeamRatings.AddRange(new[] { ("ATL", 4), ("PHI", 8) }.Select(t => new TeamRating
        {
            Sport = "mlb", TeamAbbreviation = t.Item1, Source = "powerrankingsguru", PowerRank = t.Item2,
            Season = 2026, SourceSeason = 2026, FetchedAt = GetUtcNow().UtcDateTime
        }));
        db.TeamStandings.AddRange(new[] { ("mlb", "ATL", 2), ("mlb", "PHI", 5), ("nhl", "FLA", 1), ("nhl", "EDM", 3) }.Select(t => new TeamStanding
        {
            Sport = t.Item1, TeamAbbreviation = t.Item2, Season = 2026, ConferenceRank = t.Item3
        }));
        foreach (var id in new[] { Live, Final })
            db.OddsSnapshots.AddRange(new[] { .4, .6, .55 }.Select((p, i) => new OddsSnapshot
            {
                GameId = id, Timestamp = GetUtcNow().AddSeconds(i - 3).UtcDateTime,
                HomeWinProbability = p, AwayWinProbability = 1 - p, Period = 5, GameClock = "0:00", Source = "Fixture"
            }));
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<ProCompetitionEvidenceService>().RefreshAsync(default);
        await scope.ServiceProvider.GetRequiredService<PregameOddsService>().RefreshPregameOddsForSportAsync("mlb", null);
    }

    public async Task<object> ControlAsync(IServiceProvider services, string command)
    {
        if (command == "publish")
        {
            await services.GetRequiredService<IGameUpdateService>().PublishUpdateAsync(new GameUpdate
            {
                GameId = Live, Sport = Sport.MLB, Status = GameStatus.IN_PROGRESS,
                HomeTeam = "Atlanta Braves", AwayTeam = "Philadelphia Phillies", HomeTeamAbbreviation = "ATL", AwayTeamAbbreviation = "PHI",
                HomeScore = 9, AwayScore = 6, HomeWinProbability = .72, AwayWinProbability = .28,
                ExcitementScore = 83, Period = 7, GameClock = "0:00", UpdatedAt = GetUtcNow().UtcDateTime
            });
        }
        else if (command != "inspect") throw new ArgumentException("Unknown series fixture command.");
        using var scope = services.CreateScope();
        var games = await scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Games.AsNoTracking()
            .Where(g => Cards.Contains(g.Id)).ToListAsync();
        return new { upstreamCalls = upstream.Requests, usable = games.Where(g => PlayoffSeriesObservation.TryReadFor(g) is not null).Select(g => g.Id).Order().ToArray() };
    }

    private static JsonNode Capture() => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
        "Fixtures", "ProHype", "mlb-pregame-401907973.json")))!;

    private static JsonNode Event(string id, int homeWins, int awayWins, int bestOf, string note, string state = "pre")
    {
        var item = Capture()["board"]!["body"]!["events"]![0]!.DeepClone();
        var competition = item["competitions"]![0]!;
        item["id"] = id;
        competition["id"] = id;
        competition["notes"]![0]!["headline"] = note;
        competition["series"]!["totalCompetitions"] = bestOf;
        competition["series"]!["competitors"]![0]!["wins"] = homeWins;
        competition["series"]!["competitors"]![1]!["wins"] = awayWins;
        competition["series"]!["completed"] = state == "post" && Math.Max(homeWins, awayWins) == bestOf / 2 + 1;
        foreach (var status in new[] { item["status"]!, competition["status"]! })
        {
            status["type"]!["state"] = state;
            status["type"]!["name"] = state switch { "in" => "STATUS_IN_PROGRESS", "post" => "STATUS_FINAL", _ => "STATUS_SCHEDULED" };
            status["type"]!["completed"] = state == "post";
        }
        if (state != "pre")
        {
            competition["competitors"]![0]!["score"] = state == "post" ? "8" : "7";
            competition["competitors"]![1]!["score"] = state == "post" ? "3" : "4";
        }
        if (state == "post")
        {
            competition["competitors"]![0]!["winner"] = true;
            competition["competitors"]![1]!["winner"] = false;
        }
        return item;
    }

    private static JsonNode Board(string sport)
    {
        var board = Capture()["board"]!["body"]!.DeepClone();
        if (sport == "nhl")
        {
            var item = Event(LongRound, 3, 3, 7, "Stanley Cup Final - Game 7");
            var competition = item["competitions"]![0]!;
            foreach (var participant in new[] { (0, "26", "FLA", "Florida Panthers"), (1, "6", "EDM", "Edmonton Oilers") })
            {
                var member = competition["competitors"]![participant.Item1]!;
                member["id"] = participant.Item2;
                member["team"]!["id"] = participant.Item2;
                member["team"]!["abbreviation"] = participant.Item3;
                member["team"]!["displayName"] = participant.Item4;
                competition["series"]!["competitors"]![participant.Item1]!["id"] = participant.Item2;
            }
            competition.AsObject().Remove("odds");
            board["events"] = new JsonArray(item);
        }
        else if (sport == "nba")
        {
            var item = Event(Final, 4, 1, 7, "NBA Finals - Game 5", "post");
            var competition = item["competitions"]![0]!;
            foreach (var participant in new[] { (0, "1", "Atlanta Hawks", "118"), (1, "20", "Philadelphia 76ers", "103") })
            {
                competition["competitors"]![participant.Item1]!["id"] = participant.Item2;
                competition["competitors"]![participant.Item1]!["team"]!["id"] = participant.Item2;
                competition["competitors"]![participant.Item1]!["team"]!["displayName"] = participant.Item3;
                competition["competitors"]![participant.Item1]!["score"] = participant.Item4;
                competition["series"]!["competitors"]![participant.Item1]!["id"] = participant.Item2;
            }
            board["events"] = new JsonArray(item);
        }
        else
        {
            var baseline = Event(Baseline, 1, 1, 3, "");
            baseline["season"]!["type"] = 2;
            baseline["season"]!["slug"] = "regular-season";
            baseline["competitions"]![0]!.AsObject().Remove("series");
            board["events"]!.AsArray().Add(Event(Live, 2, 1, 5, "NLDS - Game 4", "in"));
            board["events"]!.AsArray().Add(Event(Opening, 0, 0, 3, "NLWC - Game 1"));
            board["events"]!.AsArray().Add(baseline);
        }
        return board;
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            var uri = request.RequestUri!;
            string body;
            if (uri.AbsolutePath.EndsWith("/scoreboard"))
                body = uri.Query.Contains("dates=20261001") ? Board(uri.AbsolutePath.Contains("/nhl/") ? "nhl" : uri.AbsolutePath.Contains("/nba/") ? "nba" : "mlb").ToJsonString() : "{\"events\":[]}";
            else if (uri.AbsolutePath.EndsWith("/schedule"))
                body = Capture()["schedules"]!.AsArray().FirstOrDefault(s => new Uri(s!["url"]!.GetValue<string>()).PathAndQuery == uri.PathAndQuery)?["body"]!.ToJsonString() ?? "{\"events\":[]}";
            else if (uri.AbsolutePath.EndsWith("/teams"))
                body = "{\"sports\":[{\"leagues\":[{\"season\":{\"year\":2026},\"teams\":[{\"team\":{\"id\":\"15\",\"abbreviation\":\"ATL\"}},{\"team\":{\"id\":\"22\",\"abbreviation\":\"PHI\"}}]}]}]}";
            else if (uri.Host.Contains("kalshi")) body = "{\"markets\":[],\"events\":[],\"cursor\":\"\"}";
            else throw new InvalidOperationException("Unexpected series fixture outbound HTTP.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
