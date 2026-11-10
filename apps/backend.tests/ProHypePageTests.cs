using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using System.Text.Json;
using System.Threading.Channels;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using backend.Data;
using backend.GraphQL.Types;
using backend.Models;
using Backend.Rendering;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace backend.tests;

public sealed class ProHypePageTests
{
    private static async Task Change(HostedGameFixture host, Func<ExcitementDbContext, Task> change)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        await change(db);
        await db.SaveChangesAsync();
    }

    private static async Task<Game> Seed(HostedGameFixture host, string sport)
    {
        var game = ProHypeTests.Game(sport);
        game.WeekNumber = 4;
        game.ProEvidenceJson = JsonSerializer.Serialize(ProHypeContinuityInputs.Evidence(game));
        await Change(host, async db =>
        {
            await db.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "035_ProHypeLifecycle.sql")));
            db.Games.Add(game);
            db.TeamRatings.AddRange(ProHypeTests.Ranks(game));
            db.PregameOdds.Add(ProHypeTests.Odds(game));
            foreach (var team in new[] { game.HomeTeamAbbreviation, game.AwayTeamAbbreviation })
                db.TeamRatings.Add(new TeamRating { Sport = sport, TeamAbbreviation = team, Season = game.SeasonYear!.Value,
                    SourceSeason = game.SeasonYear, Source = "kalshi", PlayoffProbability = .5, FetchedAt = ProHypeTests.Now });
        });
        return game;
    }

    private sealed record Preview(double? Total, HypeComponents Components);

    private static Task<Preview> Capture(HostedGameFixture host, Game game) =>
        Capture(host.Services, game, host.Clock.GetUtcNow().UtcDateTime);

    private static async Task<Preview> Capture(IServiceProvider services, Game game, DateTime now)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        var scorer = scope.ServiceProvider.GetRequiredService<HypeScoreService>();
        var authoritative = await db.Games.AsNoTracking().SingleAsync(g => g.Id == game.Id);
        var completed = await db.Games.Where(g => g.Sport == authoritative.Sport && g.SeasonYear == authoritative.SeasonYear
            && g.SeasonType == authoritative.SeasonType && g.Status == "final").Select(g => g.Id).ToArrayAsync();
        var components = scorer.CalculateFromEvidence(authoritative, await db.TeamRatings.ToArrayAsync(),
            await db.PregameOdds.SingleAsync(o => o.GameId == game.Id), completed, now);
        Assert.True(components.IsComplete);
        var total = scorer.CalculateFinalScore(game.Sport, components);
        Assert.True(double.IsFinite(total));
        await scope.ServiceProvider.GetRequiredService<IProHypeLifecycle>().CaptureAsync();
        return new Preview(total, components);
    }

    private static string LeaguePath(Game game) => game.Sport == "nfl"
        ? $"/nfl?week=4&season={game.SeasonYear}&type=2"
        : $"/{game.Sport}?date={game.ScheduledStart:yyyy-MM-dd}";

    private static async Task<IDocument> Page(HttpClient client, string path, bool liveSnapshot = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (liveSnapshot) request.Headers.Add("X-Nailbiter-Live-Snapshot", "1");
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        NoLifecycleDiagnostics(html);
        return await new HtmlParser().ParseDocumentAsync(html);
    }

    private static void NoLifecycleDiagnostics(string output) => ProHypeOutputAssertions.NoDiagnostics(output, "firstSimulatedId");

    private static void Score(IDocument document, Game game, double total)
    {
        var score = document.QuerySelector($"#excitement-{game.Id}")!;
        Assert.NotNull(score);
        Assert.Equal("Hype" + Math.Round(total), score.TextContent);
    }

    private static void Breakdown(IDocument document, Preview expected)
    {
        var hype = document.QuerySelector("#detail-hype")!;
        Assert.NotNull(hype);
        Assert.StartsWith("Hype Score" + Math.Round(expected.Total!.Value) + "Stakes" + Math.Round(expected.Components.StakesScore)
            + "Quality" + Math.Round(expected.Components.QualityScore) + "Closeness" + Math.Round(expected.Components.ClosenessScore), hype.TextContent);
    }

    private sealed class ReadyBroadcaster : ISseBroadcaster
    {
        private readonly ISseBroadcaster inner = new SseBroadcaster();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> ready = new();
        public Task PublishAsync(GameUpdate update, CancellationToken ct) => inner.PublishAsync(update, ct);
        public ChannelReader<GameUpdate> Subscribe(string topic)
        {
            var reader = inner.Subscribe(topic);
            ready.GetOrAdd(topic, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            return reader;
        }
        public void Unsubscribe(string topic, ChannelReader<GameUpdate> reader) => inner.Unsubscribe(topic, reader);
        public Task Ready(string topic, CancellationToken ct) => ready.GetOrAdd(topic, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(ct);
    }

    private static async Task<IDocument> Fragments(StreamReader reader, CancellationToken ct)
    {
        var elements = new List<string>();
        while (true)
        {
            var line = await reader.ReadLineAsync(ct) ?? throw new EndOfStreamException("SSE ended before a fragment");
            if (line.StartsWith("data: elements ")) elements.Add(line[15..]);
            if (line.Length == 0 && elements.Count > 0)
            {
                var html = string.Join("\n", elements);
                NoLifecycleDiagnostics(html);
                return await new HtmlParser().ParseDocumentAsync(html, ct);
            }
        }
    }

    [Theory]
    [InlineData("nfl")]
    [InlineData("nba")]
    [InlineData("nhl")]
    [InlineData("mlb")]
    public async Task Existing_scheduled_card_fragments_do_not_erase_hype_and_live_fragments_still_update_excitement(string sport)
    {
        var broadcaster = new ReadyBroadcaster();
        var host = new HostedGameFixture(new DateTimeOffset(ProHypeTests.Now), services =>
        {
            services.RemoveAll<ISseBroadcaster>();
            services.AddSingleton<ISseBroadcaster>(broadcaster);
        });
        await host.InitializeAsync();
        try
        {
            var game = await Seed(host, sport);
            var captured = await Capture(host, game);
            host.Clock.Advance(TimeSpan.FromHours(2));
            Score(await Page(host.Client, "/"), game, captured.Total!.Value);
            Score(await Page(host.Client, LeaguePath(game)), game, captured.Total.Value);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var live = host.Client.GetAsync("/sse/live", HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            var league = host.Client.GetAsync("/sse/live/" + sport, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            await Task.WhenAll(broadcaster.Ready("live", deadline.Token), broadcaster.Ready("live:" + sport, deadline.Token));
            var update = new GameUpdate { GameId = game.Id, Sport = SportExtensions.FromDbValue(sport)!.Value,
                HomeTeam = game.HomeTeam, AwayTeam = game.AwayTeam, HomeTeamAbbreviation = game.HomeTeamAbbreviation,
                AwayTeamAbbreviation = game.AwayTeamAbbreviation, Status = GameStatus.SCHEDULED,
                HomeScore = 7, AwayScore = 3, HomeWinProbability = .65, AwayWinProbability = .35 };
            await host.Services.GetRequiredService<IGameUpdateService>().PublishUpdateAsync(update, deadline.Token);
            using var liveResponse = await live;
            using var leagueResponse = await league;
            using var liveReader = new StreamReader(await liveResponse.Content.ReadAsStreamAsync(deadline.Token));
            using var leagueReader = new StreamReader(await leagueResponse.Content.ReadAsStreamAsync(deadline.Token));
            foreach (var reader in new[] { liveReader, leagueReader })
            {
                var fragments = await Fragments(reader, deadline.Token);
                Assert.Null(fragments.QuerySelector($"#excitement-{game.Id}"));
                Assert.Equal("Scheduled", fragments.QuerySelector($"#status-{game.Id}")!.TextContent);
                Assert.Null(fragments.QuerySelector($"#scores-{game.Id}"));
                Assert.Equal("", fragments.QuerySelector($"#home-score-{game.Id}")!.TextContent);
                Assert.Null(fragments.QuerySelector($"#probbar-{game.Id}"));
            }
            Score(await Page(host.Client, "/", true), game, captured.Total.Value);
            update.Status = GameStatus.CANCELED;
            await host.Services.GetRequiredService<IGameUpdateService>().PublishUpdateAsync(update, deadline.Token);
            foreach (var reader in new[] { liveReader, leagueReader })
            {
                var fragments = await Fragments(reader, deadline.Token);
                Assert.Null(fragments.QuerySelector($"#excitement-{game.Id}"));
                Assert.Equal("Canceled", fragments.QuerySelector($"#status-{game.Id}")!.TextContent);
            }
            update.Status = GameStatus.IN_PROGRESS;
            update.ExcitementScore = 33;
            await host.Services.GetRequiredService<IGameUpdateService>().PublishUpdateAsync(update, deadline.Token);
            foreach (var reader in new[] { liveReader, leagueReader })
            {
                var fragments = await Fragments(reader, deadline.Token);
                Assert.Equal("Excitement33", fragments.QuerySelector($"#excitement-{game.Id}")!.TextContent);
                Assert.Equal("Live", fragments.QuerySelector($"#status-{game.Id}")!.TextContent);
            }
            await deadline.CancelAsync();
        }
        finally { await host.DisposeAsync(); }
    }

    private sealed class PercentCultureFilter(int pattern) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (_, continuation) =>
            {
                var original = CultureInfo.CurrentCulture;
                var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
                culture.NumberFormat.PercentPositivePattern = pattern;
                CultureInfo.CurrentCulture = culture;
                try { await continuation(); }
                finally { CultureInfo.CurrentCulture = original; }
            });
            next(app);
        };
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Retained_model_hype_credit_is_independent_of_new_current_team_selection(int percentPattern)
    {
        await using var harness = new MoneyPuckQualificationHarness();
        await harness.StartAsync(configureServices: services =>
        {
            services.ConfigureHttpClientDefaults(builder => builder.ConfigurePrimaryHttpMessageHandler(() => harness.Handler));
            services.AddSingleton<IStartupFilter>(new PercentCultureFilter(percentPattern));
        });
        using (var scope = harness.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            await db.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "035_ProHypeLifecycle.sql")));
        }
        Assert.Equal(32, await harness.Service.RefreshNhlRatingsForSeasonAsync(2027, default));
        var game = ProHypeTests.Game();
        game.HomeTeamAbbreviation = "OTT";
        game.AwayTeamAbbreviation = "NSH";
        game.ScheduledStart = harness.Clock.Now.AddHours(4).UtcDateTime;
        var evidence = ProHypeTests.Evidence(game);
        evidence.ObservedAt = harness.Clock.Now.UtcDateTime;
        game.ProEvidenceJson = JsonSerializer.Serialize(evidence);
        var odds = ProHypeTests.Odds(game);
        odds.FetchedAt = harness.Clock.Now.UtcDateTime;
        using (var scope = harness.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            db.Games.Add(game);
            db.PregameOdds.Add(odds);
            await db.SaveChangesAsync();
        }
        var captured = await Capture(harness.App.Services, game, harness.Clock.Now.UtcDateTime);
        Assert.NotNull(captured.Total);
        var requests = harness.Handler.Requests.Count;
        harness.Clock.Now = harness.Clock.Now.AddMinutes(1);
        using (var scope = harness.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            db.Games.Add(new Game { Id = "uncovered-result", Sport = "nhl", SeasonYear = 2027, SeasonType = 2,
                HomeTeam = "Other", AwayTeam = "Other", HomeTeamAbbreviation = "A", AwayTeamAbbreviation = "B",
                ScheduledStart = harness.Clock.Now.AddHours(-2).UtcDateTime, Status = "final" });
            await db.SaveChangesAsync();
        }
        foreach (var path in new[] { "/", LeaguePath(game) })
        {
            var document = await Page(harness.Client, path);
            Score(document, game, captured.Total.Value);
            var current = document.QuerySelector($"#qualification-credit-{game.Id}")!;
            Assert.DoesNotContain("Hype uses", current.TextContent);
            Assert.NotNull(document.QuerySelector($"#hype-credit-{game.Id}"));
        }
        using (var scope = harness.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            db.TeamRatings.Add(new TeamRating { Sport = "nhl", TeamAbbreviation = "NSH", Season = 2027,
                SourceSeason = 2027, Source = "kalshi", PlayoffProbability = .5, FetchedAt = harness.Clock.Now.UtcDateTime });
            await db.SaveChangesAsync();
        }
        foreach (var snapshot in new[] { false, true })
        {
            foreach (var path in new[] { "/", LeaguePath(game) })
            {
                var document = await Page(harness.Client, path, snapshot);
                Score(document, game, captured.Total.Value);
                var current = document.QuerySelector($"#qualification-credit-{game.Id}")!;
                Assert.NotNull(current);
                Assert.Contains("OTT", current.TextContent);
                Assert.DoesNotContain("NSH", current.TextContent);
                Assert.DoesNotContain("Hype uses", current.TextContent);
                var derived = document.QuerySelector($"#hype-credit-{game.Id}")!;
                Assert.NotNull(derived);
                Assert.Equal("Stakes and hype use model estimates for NSH / OTT from MoneyPuck.", derived.TextContent);
                Assert.Null(derived.Closest("a"));
                Assert.Equal(document.QuerySelector($"a[href='/game/{game.Id}']")!.ParentElement, derived.ParentElement);
                Assert.Empty(document.QuerySelector($"a[href='/game/{game.Id}']")!.QuerySelectorAll("a"));
            }
            var detail = await Page(harness.Client, "/game/" + game.Id, snapshot);
            Breakdown(detail, captured);
            Assert.Contains("Stakes and hype use model estimates for NSH / OTT from MoneyPuck.", detail.QuerySelector("#detail-hype")!.TextContent);
            Assert.Contains("OTT", detail.QuerySelector("#detail-qualification-credit")!.TextContent);
            Assert.DoesNotContain("NSH", detail.QuerySelector("#detail-qualification-credit")!.TextContent);
            var outlook = detail.QuerySelector("#detail-comparison")!.Children.Single(row =>
                row.Children.Length == 3 && row.Children[1].TextContent == "Playoff outlook");
            Assert.Matches(@"^50\s*%$", outlook.Children[0].TextContent);
        }
        Assert.Equal(requests, harness.Handler.Requests.Count);
    }

    [Fact]
    public async Task Retained_closeness_does_not_authorize_expired_ordinary_odds_and_current_quotes_update_independently()
    {
        var host = new HostedGameFixture(new DateTimeOffset(ProHypeTests.Now));
        await host.InitializeAsync();
        try
        {
            var game = await Seed(host, "nhl");
            await Change(host, async db =>
            {
                var odds = await db.PregameOdds.SingleAsync();
                odds.HomeMoneyline = -110;
                odds.AwayMoneyline = 110;
            });
            var captured = await Capture(host, game);
            Assert.Contains("Pregame Odds", (await Page(host.Client, "/game/" + game.Id)).Body!.TextContent);
            host.Clock.Advance(TimeSpan.FromHours(2));
            foreach (var snapshot in new[] { false, true })
            {
                var detail = await Page(host.Client, "/game/" + game.Id, snapshot);
                Breakdown(detail, captured);
                Assert.DoesNotContain("Pregame Odds", detail.Body!.TextContent);
                Assert.DoesNotMatch(@"(?<!\d)50\s*%", detail.Body.TextContent);
            }
            await Change(host, async db =>
            {
                var odds = await db.PregameOdds.SingleAsync();
                odds.HomeWinProbability = .65;
                odds.AwayWinProbability = .35;
                odds.HomeMoneyline = -150;
                odds.AwayMoneyline = 150;
                odds.FetchedAt = host.Clock.GetUtcNow().UtcDateTime;
            });
            var refreshed = await Page(host.Client, "/game/" + game.Id, true);
            Breakdown(refreshed, captured);
            Assert.Contains("65%", refreshed.Body!.TextContent);
            Assert.Contains("-150 / +150", refreshed.Body.TextContent);
            await Change(host, async db => (await db.PregameOdds.SingleAsync()).SourceSeason = null);
            var rejected = await Page(host.Client, "/game/" + game.Id);
            Assert.Contains("Hype unavailable", rejected.QuerySelector("#detail-hype")!.TextContent);
            Assert.DoesNotContain("Pregame Odds", rejected.Body!.TextContent);
            Assert.DoesNotContain("65%", rejected.Body.TextContent);
        }
        finally { await host.DisposeAsync(); }
    }

    [Theory]
    [InlineData("nfl")]
    [InlineData("nba")]
    [InlineData("nhl")]
    [InlineData("mlb")]
    public async Task Completed_result_gap_keeps_one_whole_preview_across_pages_and_existing_snapshot_reads(string sport)
    {
        var host = new HostedGameFixture(new DateTimeOffset(ProHypeTests.Now));
        await host.InitializeAsync();
        try
        {
            var game = await Seed(host, sport);
            var captured = await Capture(host, game);
            Assert.NotNull(captured.Total);
            Assert.Equal(sport switch { "nfl" => 54, "nba" => 70, "nhl" => 58, _ => 64 }, Math.Round(captured.Total.Value));
            await Change(host, db =>
            {
                db.Games.Add(new Game { Id = "uncovered-result", Sport = sport, SeasonYear = game.SeasonYear, SeasonType = 2,
                    HomeTeam = "Other", AwayTeam = "Other", HomeTeamAbbreviation = "A", AwayTeamAbbreviation = "B",
                    ScheduledStart = ProHypeTests.Now.AddHours(-2), Status = "final" });
                return Task.CompletedTask;
            });
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
                var raw = scope.ServiceProvider.GetRequiredService<HypeScoreService>().CalculateFromEvidence(game,
                    await db.TeamRatings.ToArrayAsync(), await db.PregameOdds.SingleAsync(), ["uncovered-result"], ProHypeTests.Now);
                Assert.False(raw.StakesAvailable);
                Assert.True(double.IsNaN(scope.ServiceProvider.GetRequiredService<HypeScoreService>().CalculateFinalScore(sport, raw)));
            }
            host.Clock.Advance(TimeSpan.FromMinutes(1));
            await Change(host, async db =>
            {
                foreach (var rank in await db.TeamRatings.Where(r => r.PowerRank != null).ToListAsync())
                {
                    rank.PowerRank = 12;
                    rank.FetchedAt = host.Clock.GetUtcNow().UtcDateTime;
                    if (sport == "nhl") rank.RankPublishedAt = rank.FetchedAt;
                }
                var odds = await db.PregameOdds.SingleAsync();
                odds.HomeWinProbability = .7;
                odds.AwayWinProbability = .3;
                odds.FetchedAt = host.Clock.GetUtcNow().UtcDateTime;
            });
            foreach (var snapshot in new[] { false, true })
            {
                Score(await Page(host.Client, "/", snapshot), game, captured.Total.Value);
                Score(await Page(host.Client, LeaguePath(game), snapshot), game, captured.Total.Value);
                Breakdown(await Page(host.Client, "/game/" + game.Id, snapshot), captured);
            }
            var detail = await Page(host.Client, "/game/" + game.Id);
            Assert.Contains("70%", detail.Body!.TextContent);
            await Change(host, async db =>
            {
                var row = await db.Games.SingleAsync(g => g.Id == game.Id);
                var evidence = ProGameEvidence.Parse(row.ProEvidenceJson)!;
                evidence.CompletedGameIds = ["uncovered-result"];
                evidence.ObservedAt = host.Clock.GetUtcNow().UtcDateTime;
                ProHypeContinuityInputs.RefreshLeverage(row, evidence);
                row.ProEvidenceJson = JsonSerializer.Serialize(evidence);
            });
            var recovered = await Capture(host, game);
            Assert.NotNull(recovered.Total);
            Assert.NotEqual(captured.Total, recovered.Total);
            Score(await Page(host.Client, "/"), game, recovered.Total.Value);
            Score(await Page(host.Client, LeaguePath(game)), game, recovered.Total.Value);
            Breakdown(await Page(host.Client, "/game/" + game.Id), recovered);
            foreach (var path in new[] { "/api/games", $"/api/games/{game.Id}/odds" })
            {
                var json = await host.Client.GetStringAsync(path);
                NoLifecycleDiagnostics(json);
                Assert.DoesNotContain("hype", json, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally { await host.DisposeAsync(); }
    }
}
