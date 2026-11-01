using System.Text.Json;
using backend.Data;
using backend.Models;
using backend.Scoring;
using backend.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.tests;

public sealed partial class NflPlayoffPageTests
{
    private static Dictionary<string, string?> MoneyPuckSettings() => new()
    {
        ["PlayoffRatings:MoneyPuckSelectionEnabled"] = "true",
        ["PlayoffRatings:MoneyPuckApprovedSeasons:0"] = "2027"
    };

    private static TeamRating SyntheticQualification(string team, double probability, DateTime published, DateTime fetched)
    {
        var evidence = new PlayoffModelEvidence
        {
            Version = 3, EvidenceKind = "model_projection", Provider = "moneypuck_qualification",
            ProviderVerified = true, ValidationPolicy = "moneypuck-nhl-qualification-model-v1",
            Sport = "nhl", Team = team, TeamVerified = true, Season = 2027, SeasonVerified = true,
            State = "estimate", ReasonCode = "accepted", PublishedAt = published, FetchedAt = fetched,
            RefreshId = "synthetic-675-wiring-not-importer-regression", AcceptedTeamCount = 32, TotalTeamCount = 32,
            MoneyPuckSeasonProof = new MoneyPuckSeasonProof
            {
                PageSeasonLabel = "2026–27", PageSeasonStartYear = 2026, PageSeasonEndYear = 2027,
                PageReference = "https://moneypuck.com/predictions.htm", PageObservedAt = fetched,
                FirstSimulatedId = 2099020002,
                VerifiedGames = [new NhlSimulatedGameSeasonEvidence
                {
                    SimulatedId = 2099020002, GameId = 2099020002, Season = 20262027,
                    Reference = "https://api-web.nhle.com/v1/gamecenter/2099020002/landing", ObservedAt = fetched
                }]
            }
        };
        return new TeamRating
        {
            Sport = "nhl", TeamAbbreviation = team, Season = 2027, SourceSeason = 2027,
            Source = "moneypuck_qualification", PlayoffProbability = probability,
            FetchedAt = fetched, PlayoffEvidenceJson = JsonSerializer.Serialize(evidence)
        };
    }

    private async Task<Game> SeedQualificationWiring(double home = .575, double away = .2518, bool homeKalshi = true, DateTime? publication = null)
    {
        clock.Now = new DateTimeOffset(ProHypeTests.Now);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        var game = ProHypeTests.Game();
        game.Id = "synthetic-675-wiring";
        game.HomeTeam = "Ottawa"; game.HomeTeamAbbreviation = "OTT";
        game.AwayTeam = "Nashville"; game.AwayTeamAbbreviation = "NSH";
        game.ProEvidenceJson = JsonSerializer.Serialize(ProHypeTests.Evidence(game));
        db.Games.Add(game);
        db.PregameOdds.Add(ProHypeTests.Odds(game));
        db.TeamRatings.AddRange(ProHypeTests.Ranks(game));
        db.TeamRatings.Add(SyntheticQualification("NSH", away, publication ?? ProHypeTests.Now.AddHours(-24), ProHypeTests.Now));
        db.TeamRatings.Add(SyntheticQualification("OTT", homeKalshi ? .6804 : home, publication ?? ProHypeTests.Now.AddHours(-24), ProHypeTests.Now));
        if (homeKalshi) db.TeamRatings.Add(new TeamRating
        {
            Sport = "nhl", TeamAbbreviation = "OTT", Season = 2027, SourceSeason = 2027,
            Source = "kalshi", PlayoffProbability = home, FetchedAt = ProHypeTests.Now
        });
        await db.SaveChangesAsync();
        return game;
    }

    [Fact]
    public async Task Selected_MoneyPuck_probability_is_shared_by_stored_public_reads_without_replacing_Kalshi()
    {
        var game = await SeedQualificationWiring();
        await using var enabled = CreateAppWithSettings(MoneyPuckSettings());
        using var http = enabled.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var scope = enabled.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<GameDataService>();
        var away = (await data.GetTeamRankInfoAsync("nhl", "NSH", 2027, 2, default)).Value;
        var home = (await data.GetTeamRankInfoAsync("nhl", "OTT", 2027, 2, default)).Value;
        Assert.Equal(.2518, away.PlayoffOdds!.Value);
        Assert.Equal(.575, home.PlayoffOdds!.Value);
        var components = await scope.ServiceProvider.GetRequiredService<HypeScoreService>().GetHypeComponentsAsync(game, null);
        Assert.Equal(.2518, components.AwayPlayoffOdds);
        Assert.Equal(.575, components.HomePlayoffOdds);
        Assert.True(components.IsComplete);
        Assert.Equal(.1295462478, components.StakesScore, 8);
        Assert.Equal(96.849798, components.QualityScore, 5);
        Assert.Equal(100, components.ClosenessScore);
        Assert.Equal(63.9427704867, scope.ServiceProvider.GetRequiredService<HypeScoreService>().CalculateFinalScore("nhl", components), 5);
    }

    [Fact]
    public async Task Batched_rank_and_hype_reads_share_request_evaluation_time_and_selected_sources()
    {
        var game = await SeedQualificationWiring(home: .6804, homeKalshi: false);
        await using var enabled = CreateAppWithSettings(MoneyPuckSettings());
        using var http = enabled.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using (var scope = enabled.Services.CreateScope())
        {
            var ranks = await scope.ServiceProvider.GetRequiredService<GameDataService>().GetTeamRankInfosAsync("nhl", Microsoft.FSharp.Collections.ListModule.OfSeq(new[] { "NSH", "OTT" }), 2027, 2, default);
            Assert.Equal(.2518, ranks["NSH"].PlayoffOdds.Value);
            Assert.Equal(.6804, ranks["OTT"].PlayoffOdds.Value);
            Assert.All(ranks.Values, rank => Assert.Equal(ProQualificationEvidenceKind.MoneyPuckModel, rank.PlayoffEvidenceKind));
            clock.Now = clock.Now.AddHours(13);
            var hype = await scope.ServiceProvider.GetRequiredService<HypeScoreService>().GetHypeComponentsAsync(game, null);
            Assert.Equal(.2518, hype.AwayPlayoffOdds); Assert.Equal(.6804, hype.HomePlayoffOdds);
            Assert.Equal(ProQualificationEvidenceKind.MoneyPuckModel, hype.HomeQualificationEvidenceKind); Assert.Equal(ProQualificationEvidenceKind.MoneyPuckModel, hype.AwayQualificationEvidenceKind);
            Assert.True(hype.IsComplete);
        }
        using var later = enabled.Services.CreateScope();
        var expired = await later.ServiceProvider.GetRequiredService<HypeScoreService>().GetHypeComponentsAsync(game, null);
        Assert.Null(expired.AwayPlayoffOdds); Assert.Null(expired.HomePlayoffOdds);
        Assert.Equal(ProQualificationEvidenceKind.Unavailable, expired.AwayQualificationEvidenceKind); Assert.Equal(ProQualificationEvidenceKind.Unavailable, expired.HomeQualificationEvidenceKind);
    }

    [Theory]
    [InlineData("progress")]
    [InlineData("quality")]
    [InlineData("closeness")]
    public async Task Model_qualification_cannot_replace_independent_missing_hype_inputs(string missing)
    {
        var game = await SeedQualificationWiring();
        using (var seed = app.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            if (missing == "progress")
            {
                var evidence = ProHypeTests.Evidence(game); evidence.AwayProgress.Completed = null;
                game.ProEvidenceJson = JsonSerializer.Serialize(evidence);
                (await db.Games.SingleAsync(g => g.Id == game.Id)).ProEvidenceJson = game.ProEvidenceJson;
            }
            if (missing == "quality") db.TeamRatings.RemoveRange(await db.TeamRatings.Where(r => r.Source == "moneypuck").ToListAsync());
            if (missing == "closeness") db.PregameOdds.Remove(await db.PregameOdds.SingleAsync(o => o.GameId == game.Id));
            await db.SaveChangesAsync();
        }
        await using var enabled = CreateAppWithSettings(MoneyPuckSettings());
        using var http = enabled.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var scope = enabled.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<HypeScoreService>();
        var hype = await service.GetHypeComponentsAsync(game, null);
        Assert.Equal(.2518, hype.AwayPlayoffOdds); Assert.Equal(.575, hype.HomePlayoffOdds);
        Assert.Equal(ProQualificationEvidenceKind.MoneyPuckModel, hype.AwayQualificationEvidenceKind);
        Assert.False(hype.IsComplete); Assert.Null(await service.CalculateHypeScoreAsync(game, null));
        Assert.Equal(missing != "progress", hype.StakesAvailable);
        Assert.Equal(missing != "quality", hype.QualityAvailable);
        Assert.Equal(missing != "closeness", hype.ClosenessAvailable);
        foreach (var path in new[] { "/", "/nhl?date=2026-09-30", "/game/" + game.Id })
        {
            using var doc = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(await http.GetStringAsync(path), CancellationToken.None);
            var region = path.StartsWith("/game/") ? doc.Body! : doc.QuerySelector($"a[href='/game/{game.Id}']")!.ParentElement!;
            Assert.Contains("Hype unavailable", region.TextContent);
            Assert.Contains("model estimate", region.TextContent);
            Assert.NotNull(region.QuerySelector("a[href='https://moneypuck.com/predictions.htm']"));
            Assert.DoesNotContain("Hype uses", region.TextContent);
            Assert.DoesNotContain("Stakes and hype use", region.TextContent);
        }
    }

    [Theory]
    [InlineData("default-off")]
    [InlineData("rejected")]
    [InlineData("Kalshi-selected")]
    [InlineData("other-league")]
    public async Task Pages_never_contact_providers_or_credit_unselected_model_evidence(string state)
    {
        var game = await SeedQualificationWiring();
        using (var seed = app.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            if (state == "Kalshi-selected") db.TeamRatings.Add(new TeamRating
            {
                Sport = "nhl", TeamAbbreviation = "NSH", Season = 2027, SourceSeason = 2027,
                Source = "kalshi", PlayoffProbability = .42, FetchedAt = ProHypeTests.Now
            });
            if (state == "rejected")
            {
                var row = await db.TeamRatings.SingleAsync(r => r.Source == "moneypuck_qualification" && r.TeamAbbreviation == "NSH");
                var evidence = JsonSerializer.Deserialize<PlayoffModelEvidence>(row.PlayoffEvidenceJson!)!;
                evidence.State = "unavailable"; evidence.ReasonCode = "synthetic-rejected-snapshot";
                row.PlayoffEvidenceJson = JsonSerializer.Serialize(evidence);
            }
            if (state == "other-league")
            {
                var stored = await db.Games.SingleAsync(g => g.Id == game.Id);
                stored.Sport = game.Sport = "nba";
                stored.ProEvidenceJson = game.ProEvidenceJson = JsonSerializer.Serialize(ProHypeTests.Evidence(game));
                foreach (var row in await db.TeamRatings.Where(r => r.Sport == "nhl").ToListAsync()) row.Sport = "nba";
                db.TeamRatings.AddRange(ProHypeTests.Ranks(game));
            }
            await db.SaveChangesAsync();
        }
        var requests = 0;
        await using var configured = CreateAppWithSettings(state == "default-off" ? new() : MoneyPuckSettings(), configureServices: services =>
            services.ConfigureHttpClientDefaults(builder => builder.ConfigurePrimaryHttpMessageHandler(() => new ProFixtureHandler(_ =>
            {
                Interlocked.Increment(ref requests);
                throw new InvalidOperationException("Pages must read stored evidence, not call providers.");
            }))));
        using var http = configured.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var scope = configured.Services.CreateScope();
        var rank = (await scope.ServiceProvider.GetRequiredService<GameDataService>().GetTeamRankInfoAsync(game.Sport, "NSH", 2027, 2, default)).Value;
        var hype = await scope.ServiceProvider.GetRequiredService<HypeScoreService>().GetHypeComponentsAsync(game, null);
        var market = state == "Kalshi-selected";
        Assert.Equal(market ? "kalshi" : "", hype.AwayQualificationSource);
        Assert.Equal(market ? ProQualificationEvidenceKind.Kalshi : ProQualificationEvidenceKind.Unavailable, hype.AwayQualificationEvidenceKind);
        Assert.Equal(market, rank.PlayoffSource is not null);
        Assert.Equal(market, hype.StakesAvailable);
        Assert.True(hype.QualityAvailable); Assert.True(hype.ClosenessAvailable);
        foreach (var path in new[] { "/", $"/{game.Sport}?date=2026-09-30", "/game/" + game.Id })
        {
            var html = await http.GetStringAsync(path);
            Assert.DoesNotContain("https://moneypuck.com/predictions.htm", html);
            Assert.DoesNotContain("model estimate", html);
            using var doc = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(html, CancellationToken.None);
            var region = path.StartsWith("/game/") ? doc.Body! : doc.QuerySelector($"a[href='/game/{game.Id}']")!;
            Assert.Equal(!market, region.TextContent.Contains("Hype unavailable", StringComparison.Ordinal));
            if (market) AssertContainsPercent(42, region.TextContent);
        }
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task Stored_model_expires_at_publication_plus_36_hours_without_hiding_quality_or_closeness()
    {
        var game = await SeedQualificationWiring(publication: ProHypeTests.Now.AddHours(-36).AddSeconds(1));
        await using var enabled = CreateAppWithSettings(MoneyPuckSettings());
        using var http = enabled.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        foreach (var offset in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1).Add(TimeSpan.FromTicks(10)) })
        {
            clock.Now = new DateTimeOffset(ProHypeTests.Now.Add(offset));
            using var scope = enabled.Services.CreateScope();
            var rank = (await scope.ServiceProvider.GetRequiredService<GameDataService>().GetTeamRankInfoAsync("nhl", "NSH", 2027, 2, default)).Value;
            var hype = await scope.ServiceProvider.GetRequiredService<HypeScoreService>().GetHypeComponentsAsync(game, null);
            Assert.True(hype.QualityAvailable); Assert.True(hype.ClosenessAvailable);
            Assert.Equal(96.849798, hype.QualityScore, 5); Assert.Equal(100, hype.ClosenessScore);
            var eligible = offset == TimeSpan.Zero;
            Assert.Equal(eligible, hype.StakesAvailable); Assert.Equal(eligible, rank.PlayoffOdds is not null);
            Assert.Equal(eligible ? ProQualificationEvidenceKind.MoneyPuckModel : ProQualificationEvidenceKind.Unavailable, hype.AwayQualificationEvidenceKind);
            Assert.Equal(eligible ? "moneypuck_qualification" : "", hype.AwayQualificationSource);
            Assert.Equal(eligible, rank.PlayoffSource is not null);
            foreach (var path in new[] { "/", "/nhl?date=2026-09-30", "/game/" + game.Id })
            {
                using var doc = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(await http.GetStringAsync(path), CancellationToken.None);
                var region = path.StartsWith("/game/") ? doc.Body! : doc.QuerySelector($"a[href='/game/{game.Id}']")!.ParentElement!;
                Assert.Equal(eligible, region.QuerySelector("a[href='https://moneypuck.com/predictions.htm']") is not null);
                Assert.Equal(!eligible, region.TextContent.Contains("Hype unavailable", StringComparison.Ordinal));
                if (!eligible) Assert.DoesNotContain("25%", region.TextContent);
            }
        }
    }

    [Fact]
    public async Task Real_pages_credit_only_the_selected_team_model_and_derived_hype_with_sibling_card_links()
    {
        var game = await SeedQualificationWiring();
        var requests = 0;
        await using var enabled = CreateAppWithSettings(MoneyPuckSettings(), configureServices: services =>
            services.ConfigureHttpClientDefaults(builder => builder.ConfigurePrimaryHttpMessageHandler(() => new ProFixtureHandler(_ =>
            {
                Interlocked.Increment(ref requests);
                throw new InvalidOperationException("Selected model pages must not contact providers.");
            }))));
        using var http = enabled.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        foreach (var path in new[] { "/", "/nhl?date=2026-09-30", "/game/" + game.Id })
        {
            var html = await http.GetStringAsync(path);
            AssertNoScoringDiagnostics(html);
            using var doc = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(html, CancellationToken.None);
            var region = path.StartsWith("/game/") ? doc.QuerySelector("#detail-comparison")! : doc.QuerySelector($"a[href='/game/{game.Id}']")!;
            AssertContainsPercent(25, region.TextContent);
            AssertContainsPercent(57, region.TextContent);
            Assert.DoesNotContain("68%", region.TextContent);
            Assert.Contains("model estimate", region.TextContent);
            var credit = path.StartsWith("/game/") ? region.QuerySelector("#detail-qualification-credit")! : region.ParentElement!.QuerySelector($"#qualification-credit-{game.Id}")!;
            Assert.NotNull(credit);
            Assert.Contains("NSH", credit.TextContent);
            Assert.DoesNotContain("OTT", credit.TextContent);
            Assert.Equal("MoneyPuck", credit.QuerySelector("a[href='https://moneypuck.com/predictions.htm']")!.TextContent);
            if (path.StartsWith("/game/"))
            {
                Assert.NotNull(doc.QuerySelector("#detail-hype a[href='https://moneypuck.com/predictions.htm']"));
                Assert.Contains("model estimate", doc.QuerySelector("#detail-hype")!.TextContent);
                Assert.Equal("64", doc.QuerySelector("#detail-hype .text-3xl")!.TextContent);
                Assert.DoesNotContain("Hype unavailable", doc.Body!.TextContent);
            }
            else
            {
                Assert.Contains("Hype uses", credit.TextContent);
                Assert.Equal("Hype64", region.QuerySelector($"#excitement-{game.Id}")!.TextContent.Replace(" ", ""));
                Assert.Empty(region.QuerySelectorAll("a"));
                Assert.Equal(region.ParentElement, credit.ParentElement);
                Assert.True(html.IndexOf("</a>", html.IndexOf($"href=\"/game/{game.Id}\"", StringComparison.Ordinal), StringComparison.Ordinal)
                    < html.IndexOf($"id=\"qualification-credit-{game.Id}\"", StringComparison.Ordinal));
            }
        }
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task Both_selected_team_models_receive_probability_and_hype_credit_on_real_pages()
    {
        var game = await SeedQualificationWiring(home: .6804, homeKalshi: false);
        await using var enabled = CreateAppWithSettings(MoneyPuckSettings());
        using var http = enabled.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        foreach (var path in new[] { "/", "/nhl?date=2026-09-30", "/game/" + game.Id })
        {
            using var doc = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(await http.GetStringAsync(path), CancellationToken.None);
            var region = path.StartsWith("/game/") ? doc.QuerySelector("#detail-comparison")! : doc.QuerySelector($"a[href='/game/{game.Id}']")!.ParentElement!;
            AssertContainsPercent(25, region.TextContent); AssertContainsPercent(68, region.TextContent);
            var credit = region.QuerySelector(path.StartsWith("/game/") ? "#detail-qualification-credit" : $"#qualification-credit-{game.Id}")!;
            Assert.Contains("NSH / OTT playoff probabilities", credit.TextContent);
            Assert.Contains("model estimates", credit.TextContent);
            Assert.NotNull(credit.QuerySelector("a[href='https://moneypuck.com/predictions.htm']"));
            if (!path.StartsWith("/game/")) Assert.Contains("Hype uses these estimates", credit.TextContent);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Selected_model_endpoints_expose_MoneyPuck_provenance_but_never_finality(double probability)
    {
        var game = await SeedQualificationWiring(away: probability);
        await using var enabled = CreateAppWithSettings(MoneyPuckSettings());
        using var http = enabled.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var scope = enabled.Services.CreateScope();
        var rank = (await scope.ServiceProvider.GetRequiredService<GameDataService>().GetTeamRankInfoAsync("nhl", "NSH", 2027, 2, default)).Value;
        Assert.Equal("moneypuck_qualification", rank.PlayoffSource);
        Assert.Equal(ProQualificationEvidenceKind.MoneyPuckModel, rank.PlayoffEvidenceKind);
        Assert.False(rank.PlayoffIsFinal);
        var hype = await scope.ServiceProvider.GetRequiredService<HypeScoreService>().GetHypeComponentsAsync(game, null);
        Assert.Equal("moneypuck_qualification", hype.AwayQualificationSource);
        Assert.Equal(ProQualificationEvidenceKind.MoneyPuckModel, hype.AwayQualificationEvidenceKind);
        Assert.False(hype.AwayQualificationIsFinal);
        Assert.Equal("kalshi", hype.HomeQualificationSource);
        Assert.Equal(ProQualificationEvidenceKind.Kalshi, hype.HomeQualificationEvidenceKind);
        Assert.False(hype.HomeQualificationIsFinal);
        Assert.Equal(probability, hype.AwayPlayoffOdds);
        Assert.True(hype.IsComplete);
        foreach (var path in new[] { "/", "/nhl?date=2026-09-30", "/game/" + game.Id })
        {
            using var doc = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(await http.GetStringAsync(path), CancellationToken.None);
            var region = path.StartsWith("/game/") ? doc.Body! : doc.QuerySelector($"a[href='/game/{game.Id}']")!.ParentElement!;
            Assert.Contains(probability == 0 ? "<1%" : ">99%", region.TextContent.Replace(" ", ""));
            Assert.Contains("model estimate", region.TextContent);
            Assert.DoesNotContain("Clinched playoffs", region.TextContent);
            Assert.DoesNotContain("Missed playoffs", region.TextContent);
            Assert.NotNull(region.QuerySelector("a[href='https://moneypuck.com/predictions.htm']"));
        }
    }
}
