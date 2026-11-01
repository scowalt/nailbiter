using System.Net;
using System.Text.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using backend.Data;
using backend.Models;
using backend.Scoring;
using backend.Services;
using backend.Services.Kalshi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace backend.tests;

public sealed class MoneyPuckQualificationPageRegressionTests(ITestOutputHelper output)
{
    private sealed class OfflineProviders : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var body = request.RequestUri!.AbsoluteUri switch
            {
                "https://api.elections.kalshi.com/trade-api/v2/events/KXNHLPLAYOFF-27?with_nested_markets=true" => MoneyPuckQualificationHandler.Fixture("kalshi-quote-report-reproduction.json"),
                "https://api.elections.kalshi.com/trade-api/v2/historical/markets?event_ticker=KXNHLPLAYOFF-27&limit=1000" => "{\"markets\":[],\"cursor\":\"\"}",
                _ => throw new InvalidOperationException("Unexpected offline provider request: " + request.RequestUri)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private static Task Start(MoneyPuckQualificationHarness h, OfflineProviders providers) => h.StartAsync(configureServices: services =>
        services.ConfigureHttpClientDefaults(builder => builder.ConfigurePrimaryHttpMessageHandler(() => providers)));

    private static async Task<Game> ControlledUpcomingGame(MoneyPuckQualificationHarness h)
    {
        var game = ProHypeTests.Game();
        game.Id = "synthetic-676-upcoming-not-401892474";
        game.HomeTeam = "Synthetic Ottawa"; game.HomeTeamAbbreviation = "OTT";
        game.AwayTeam = "Synthetic Nashville"; game.AwayTeamAbbreviation = "NSH";
        game.ScheduledStart = h.Clock.Now.AddHours(4.5).UtcDateTime;
        var evidence = ProHypeTests.Evidence(game);
        evidence.ObservedAt = h.Clock.Now.UtcDateTime;
        evidence.Source = "synthetic: controlled verified schedule, not provider capture";
        game.ProEvidenceJson = JsonSerializer.Serialize(evidence);
        var odds = ProHypeTests.Odds(game); odds.FetchedAt = h.Clock.Now.UtcDateTime;
        var ranks = ProHypeTests.Ranks(game);
        foreach (var rank in ranks) { rank.FetchedAt = h.Clock.Now.UtcDateTime; rank.RankPublishedAt = h.Clock.Now.UtcDateTime; }
        using var scope = h.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        db.Games.Add(game); db.PregameOdds.Add(odds);
        foreach (var rank in ranks)
            if (!await db.TeamRatings.AnyAsync(row => row.Sport == rank.Sport && row.Source == rank.Source && row.Season == rank.Season && row.TeamAbbreviation == rank.TeamAbbreviation)) db.TeamRatings.Add(rank);
        await db.SaveChangesAsync();
        Assert.Equal(1, await h.App.Services.GetRequiredService<KalshiPlayoffRatingsService>().RefreshAsync("nhl", 2027, default));
        return game;
    }

    private async Task AssertPages(MoneyPuckQualificationHarness h, Game game, bool selected, bool complete, bool allPages = true)
    {
        var paths = allPages ? new[] { "/", $"/nhl?date={h.Clock.Now:yyyy-MM-dd}", "/game/" + game.Id } : new[] { "/game/" + game.Id };
        foreach (var path in paths)
        {
            var html = await h.Client.GetStringAsync(path);
            using var doc = await new HtmlParser().ParseDocumentAsync(html, CancellationToken.None);
            var detail = path.StartsWith("/game/");
            var comparison = detail ? doc.QuerySelector("#detail-comparison")! : doc.QuerySelector($"a[href='/game/{game.Id}']")!;
            Assert.NotNull(comparison);
            var text = comparison.TextContent.Replace(" ", "");
            Assert.Contains("57%", text); Assert.DoesNotContain("68%", text);
            Assert.Equal(selected, text.Contains("25%", StringComparison.Ordinal));
            Assert.Equal(selected, text.Contains("modelestimate", StringComparison.Ordinal));
            var region = detail ? doc.Body! : comparison.ParentElement!;
            var score = detail ? doc.QuerySelector("#detail-hype")! : comparison.QuerySelector($"#excitement-{game.Id}")!;
            Assert.Equal(!complete, score.TextContent.Contains("Hype unavailable", StringComparison.Ordinal));
            if (complete) Assert.Equal("64", detail ? score.QuerySelector(".text-3xl")!.TextContent : score.TextContent.Replace(" ", "").Replace("Hype", ""));
            var credits = region.QuerySelectorAll("a[href='https://moneypuck.com/predictions.htm']");
            Assert.Equal(selected, credits.Length > 0);
            foreach (var link in credits)
            {
                Assert.Equal("MoneyPuck", link.TextContent);
                Assert.Contains("NSH", link.ParentElement!.TextContent);
                Assert.DoesNotContain("OTT", link.ParentElement.TextContent);
                Assert.Null(link.ParentElement.Closest("a"));
            }
            if (!detail)
            {
                Assert.Empty(comparison.QuerySelectorAll("a"));
                if (selected)
                {
                    var credit = region.QuerySelector($"#qualification-credit-{game.Id}")!;
                    Assert.Equal(comparison.ParentElement, credit.ParentElement);
                    Assert.True(html.IndexOf("</a>", html.IndexOf($"href=\"/game/{game.Id}\"", StringComparison.Ordinal), StringComparison.Ordinal)
                        < html.IndexOf($"id=\"qualification-credit-{game.Id}\"", StringComparison.Ordinal));
                }
            }
            else
            {
                var metrics = score.TextContent.Replace(" ", "");
                Assert.Contains("Quality97", metrics); Assert.Contains("Closeness100", metrics);
                Assert.Equal(selected && complete, score.QuerySelector("a[href='https://moneypuck.com/predictions.htm']") is not null);
            }
            Assert.DoesNotContain("moneypuck-nhl-qualification-model-v1", html);
            Assert.DoesNotContain("firstSimulatedId", html);
            output.WriteLine($"{path}: NSH={(selected ? "25% model estimate" : "Unavailable")}; OTT=57% Kalshi; Hype={(complete ? "64" : "unavailable")}; selected NSH-only credit={selected}; valid sibling/detail links");
        }
    }

    [Fact]
    public async Task Rejected_distant_future_publication_cannot_poison_current_import_recovery()
    {
        await using var h = new MoneyPuckQualificationHarness();
        using var providers = new OfflineProviders();
        await Start(h, providers);
        Assert.Equal(32, await h.Service.RefreshNhlRatingsForSeasonAsync(2027, default));
        var prior = PlayoffModelEvidence.Parse((await h.RowsAsync())[0].PlayoffEvidenceJson)!.PublishedAt!.Value;
        h.Clock.Now = h.Clock.Now.AddHours(24);
        var future = h.Clock.Now.AddYears(5);
        h.Handler.Marker = future.ToString("O");
        Assert.Equal(0, await h.Service.RefreshNhlRatingsForSeasonAsync(2027, default));
        foreach (var team in MoneyPuckQualificationHarness.Teams) Assert.Null((await h.SelectAsync(team)).Probability);
        Assert.All(await h.RowsAsync(), row =>
        {
            var evidence = PlayoffModelEvidence.Parse(row.PlayoffEvidenceJson)!;
            Assert.Equal("unavailable", evidence.State);
            Assert.Equal("future_publication", evidence.ReasonCode);
            Assert.Equal(future.UtcDateTime, evidence.PublishedAt);
            Assert.Equal(prior, evidence.PublicationBoundary);
        });
        h.Clock.Now = h.Clock.Now.AddHours(24);
        var current = h.Clock.Now.AddMinutes(-5);
        Assert.True(current.UtcDateTime > prior && current < future);
        h.Handler.Marker = current.ToString("O");
        Assert.Equal(32, await h.Service.RefreshNhlRatingsForSeasonAsync(2027, default));
        Assert.Equal(.2518, (await h.SelectAsync()).Probability.Value);
        var game = await ControlledUpcomingGame(h);
        using (var scope = h.App.Services.CreateScope())
        {
            var hype = await scope.ServiceProvider.GetRequiredService<HypeScoreService>().GetHypeComponentsAsync(game, null);
            Assert.Equal(.2518, hype.AwayPlayoffOdds);
            Assert.False(hype.AwayQualificationIsFinal);
            Assert.True(hype.IsComplete);
        }
        var beforePages = (h.Handler.Requests.Count, providers.Requests);
        await AssertPages(h, game, selected: true, complete: true);
        Assert.Equal(beforePages, (h.Handler.Requests.Count, providers.Requests));
    }

    [Theory]
    [InlineData("off")]
    [InlineData("expiry")]
    [InlineData("invalid-success")]
    public async Task Imported_model_loss_preserves_independent_quality_closeness_and_Ottawa_Kalshi(string state)
    {
        await using var h = new MoneyPuckQualificationHarness();
        using var providers = new OfflineProviders();
        await Start(h, providers);
        Assert.Equal(32, await h.Service.RefreshNhlRatingsForSeasonAsync(2027, default));
        var imported = await h.RowsAsync();
        if (state == "expiry") h.Clock.Now = new DateTimeOffset(PlayoffModelEvidence.Parse(imported[0].PlayoffEvidenceJson)!.PublishedAt!.Value.AddHours(36));
        if (state == "invalid-success")
        {
            h.Clock.Now = h.Clock.Now.AddHours(24);
            h.Handler.Marker = h.Clock.Now.AddMinutes(-10).ToString("O");
            h.Handler.Csv = h.Handler.Csv.Replace("NSH,ALL,0.2518", "NSH,ALL,NaN");
            Assert.Equal(0, await h.Service.RefreshNhlRatingsForSeasonAsync(2027, default));
        }
        if (state == "off") h.App.Services.GetRequiredService<IOptions<PlayoffRatingsOptions>>().Value.MoneyPuckSelectionEnabled = false;
        var game = await ControlledUpcomingGame(h);
        var beforePages = (h.Handler.Requests.Count, providers.Requests);
        using (var scope = h.App.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<HypeScoreService>();
            var hype = await service.GetHypeComponentsAsync(game, null);
            Assert.Null(hype.AwayPlayoffOdds); Assert.Equal(.575, hype.HomePlayoffOdds);
            Assert.Equal(ProQualificationEvidenceKind.Unavailable, hype.AwayQualificationEvidenceKind); Assert.Equal("", hype.AwayQualificationSource);
            Assert.Equal(ProQualificationEvidenceKind.Kalshi, hype.HomeQualificationEvidenceKind);
            Assert.False(hype.StakesAvailable); Assert.False(hype.IsComplete);
            Assert.Equal(96.849798, hype.QualityScore, 5); Assert.Equal(100, hype.ClosenessScore);
            Assert.True(hype.QualityAvailable); Assert.True(hype.ClosenessAvailable);
            Assert.Null(await service.CalculateHypeScoreAsync(game, null));
        }
        await AssertPages(h, game, selected: false, complete: false, allPages: false);
        Assert.Equal(beforePages, (h.Handler.Requests.Count, providers.Requests));
        if (state != "invalid-success") Assert.Equal(imported.Select(r => r.PlayoffEvidenceJson), (await h.RowsAsync()).Select(r => r.PlayoffEvidenceJson));
    }

    [Fact]
    public async Task Imported_qualification_survives_missing_progress_but_cannot_supply_total_hype()
    {
        await using var h = new MoneyPuckQualificationHarness();
        using var providers = new OfflineProviders();
        await Start(h, providers);
        var game = await ControlledUpcomingGame(h);
        Assert.Equal(32, await h.Service.RefreshNhlRatingsForSeasonAsync(2027, default));
        using (var scope = h.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            var evidence = ProGameEvidence.Parse(game.ProEvidenceJson)!;
            evidence.AwayProgress.Completed = null;
            game.ProEvidenceJson = JsonSerializer.Serialize(evidence);
            (await db.Games.SingleAsync(g => g.Id == game.Id)).ProEvidenceJson = game.ProEvidenceJson;
            await db.SaveChangesAsync();
        }
        var beforePages = (h.Handler.Requests.Count, providers.Requests);
        using (var scope = h.App.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<HypeScoreService>();
            var hype = await service.GetHypeComponentsAsync(game, null);
            Assert.Equal(.2518, hype.AwayPlayoffOdds); Assert.Equal(.575, hype.HomePlayoffOdds);
            Assert.Equal(ProQualificationEvidenceKind.MoneyPuckModel, hype.AwayQualificationEvidenceKind); Assert.False(hype.AwayQualificationIsFinal);
            Assert.False(hype.StakesAvailable); Assert.False(hype.IsComplete);
            Assert.Equal(96.849798, hype.QualityScore, 5); Assert.Equal(100, hype.ClosenessScore);
            Assert.Null(await service.CalculateHypeScoreAsync(game, null));
        }
        await AssertPages(h, game, selected: true, complete: false, allPages: false);
        Assert.Equal(beforePages, (h.Handler.Requests.Count, providers.Requests));
    }

    [Fact]
    public async Task Actual_importers_fill_synthetic_wide_quote_gap_without_replacing_eligible_market()
    {
        await using var h = new MoneyPuckQualificationHarness();
        using var providers = new OfflineProviders();
        await Start(h, providers);
        var game = await ControlledUpcomingGame(h);
        Assert.True(game.ScheduledStart > h.Clock.Now.UtcDateTime);
        Assert.False(game.IsHistoricalImport);
        var rejected = PlayoffQuoteEvidence.Parse((await h.RowsAsync("kalshi")).Single(row => row.TeamAbbreviation == "NSH").PlayoffEvidenceJson)!;
        Assert.Equal(.25m, rejected.Bid); Assert.Equal(.75m, rejected.Ask);
        Assert.Equal("wide_spread", rejected.ReasonCode);
        var ranksBefore = (await h.RowsAsync("moneypuck")).Select(r => (r.TeamAbbreviation, r.PowerRank)).ToArray();
        using (var scope = h.App.Services.CreateScope())
        {
            var hype = await scope.ServiceProvider.GetRequiredService<HypeScoreService>().GetHypeComponentsAsync(game, null);
            Assert.Null(hype.AwayPlayoffOdds); Assert.Equal(.575, hype.HomePlayoffOdds);
            Assert.False(hype.StakesAvailable); Assert.False(hype.IsComplete);
            Assert.Equal(96.849798, hype.QualityScore, 5); Assert.Equal(100, hype.ClosenessScore);
        }
        var beforeUnavailablePages = (h.Handler.Requests.Count, providers.Requests);
        await AssertPages(h, game, selected: false, complete: false);
        Assert.Equal(beforeUnavailablePages, (h.Handler.Requests.Count, providers.Requests));
        Assert.Equal(32, await h.Service.RefreshNhlRatingsForSeasonAsync(2027, default));
        Assert.Equal(ranksBefore, (await h.RowsAsync("moneypuck")).Where(r => r.TeamAbbreviation is "OTT" or "NSH").Select(r => (r.TeamAbbreviation, r.PowerRank)).ToArray());
        Assert.Equal(32, (await h.RowsAsync("moneypuck")).Count);
        using (var scope = h.App.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<HypeScoreService>();
            var hype = await service.GetHypeComponentsAsync(game, null);
            Assert.Equal(.2518, hype.AwayPlayoffOdds); Assert.Equal(.575, hype.HomePlayoffOdds);
            Assert.Equal("moneypuck_qualification", hype.AwayQualificationSource); Assert.Equal(ProQualificationEvidenceKind.MoneyPuckModel, hype.AwayQualificationEvidenceKind);
            Assert.False(hype.AwayQualificationIsFinal);
            Assert.Equal("kalshi", hype.HomeQualificationSource); Assert.Equal(ProQualificationEvidenceKind.Kalshi, hype.HomeQualificationEvidenceKind);
            Assert.False(hype.HomeQualificationIsFinal);
            Assert.True(hype.IsComplete, hype.StakesExplanation);
            Assert.Equal(.1295462478, hype.StakesScore, 8);
            Assert.Equal(96.849798, hype.QualityScore, 5); Assert.Equal(100, hype.ClosenessScore);
            Assert.Equal(63.9427704867, (await service.CalculateHypeScoreAsync(game, null))!.Value, 5);
        }
        var beforePages = (h.Handler.Requests.Count, providers.Requests);
        await AssertPages(h, game, selected: true, complete: true);
        Assert.Equal(beforePages, (h.Handler.Requests.Count, providers.Requests));
        output.WriteLine("After both actual refreshes: .2518 MoneyPuckModel/nonfinal, .575 Kalshi/nonfinal (not .6804); all page requests add zero provider requests.");
    }
}
