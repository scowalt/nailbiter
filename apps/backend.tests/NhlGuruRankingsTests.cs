using System.Net;
using System.Text.Json;
using backend.Data;
using backend.Models;
using backend.Scoring;
using backend.Services;
using backend.Services.PowerRankingsGuru;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.tests;

public sealed class NhlGuruRankingsTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public string Html { get; set; } = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NhlGuru", "rankings.html"));
        public List<string> Requests { get; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public DateTimeOffset? RetryAt { get; set; }
        public Exception? Failure { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request.RequestUri!.AbsoluteUri);
            if (Failure is not null) throw Failure;
            var response = new HttpResponseMessage(Status) { Content = new StringContent(Html) };
            if (RetryAt.HasValue) response.Headers.RetryAfter = new(RetryAt.Value);
            if (Status == HttpStatusCode.Found) response.Headers.Location = new Uri("https://example.invalid/do-not-follow");
            return Task.FromResult(response);
        }
    }

    private static Task Start(MoneyPuckQualificationHarness h, Handler handler) => h.StartAsync(configureServices: services =>
    {
        services.AddHttpClient<PowerRankingsGuruService>().ConfigurePrimaryHttpMessageHandler(() => handler);
        services.AddHttpClient("NhlPowerRankingsGuruService").ConfigurePrimaryHttpMessageHandler(() => handler);
    });

    private static Task<int> Refresh(MoneyPuckQualificationHarness h) => h.App.Services.GetRequiredService<PowerRankingsGuruService>().RefreshNhlRatingsAsync(null);

    private static async Task<Game> AddGame(MoneyPuckQualificationHarness h, int days = 0)
    {
        using var scope = h.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        await db.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "035_ProHypeLifecycle.sql")));
        var game = ProHypeTests.Game();
        game.SeasonType = 3;
        game.ScheduledStart = h.Clock.Now.AddDays(days).AddHours(4).UtcDateTime;
        var evidence = ProHypeTests.Evidence(game);
        evidence.ObservedAt = h.Clock.Now.UtcDateTime;
        evidence.Round = 4;
        evidence.HomeElimination = evidence.AwayElimination = true;
        game.ProEvidenceJson = JsonSerializer.Serialize(evidence);
        var odds = ProHypeTests.Odds(game);
        odds.FetchedAt = h.Clock.Now.UtcDateTime;
        db.Games.Add(game);
        db.PregameOdds.Add(odds);
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<IProHypeLifecycle>().CaptureAsync();
        return game;
    }

    private static async Task<ProHypeResolution> Resolve(MoneyPuckQualificationHarness h, Game game)
    {
        using var scope = h.App.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IProHypeLifecycle>().ResolveAsync(game.Id);
    }

    [Fact]
    public async Task Credit_follows_actual_saved_quality_on_home_league_detail_and_restart_not_new_preferred_inputs()
    {
        using var handler = new Handler();
        await using var h = new MoneyPuckQualificationHarness();
        await Start(h, handler);
        Assert.Equal(32, await Refresh(h));
        var game = await AddGame(h);
        var original = await Resolve(h, game);
        Assert.Equal(98.1886340726, original.Total!.Value, 10);
        Assert.Equal(NhlPowerRankingsGuruService.Source, original.Components.QualitySource);
        foreach (var path in new[] { "/", "/nhl?date=2026-10-11", "/game/" + game.Id })
        {
            var html = await h.Client.GetStringAsync(path);
            Assert.True(html.Contains("Quality and hype use"), path);
            Assert.Contains("href=\"https://powerrankingsguru.com/nhl/power-rankings\"", html);
            ProHypeOutputAssertions.NoDiagnostics(html);
        }
        h.Clock.Now = h.Clock.Now.AddHours(2);
        using (var scope = h.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            var preferred = ProHypeTests.Ranks(game, 14, 15);
            foreach (var row in preferred) { row.FetchedAt = h.Clock.Now.UtcDateTime; row.RankPublishedAt = row.FetchedAt; }
            db.TeamRatings.AddRange(preferred);
            await db.SaveChangesAsync();
        }
        await h.RestartApplicationAsync();
        var retained = await Resolve(h, game);
        Assert.Equal(original.Total, retained.Total);
        Assert.Equal(original.EvaluatedAt, retained.EvaluatedAt);
        Assert.Equal(JsonSerializer.Serialize(original.Components), JsonSerializer.Serialize(retained.Components));
        foreach (var path in new[] { "/", "/nhl?date=2026-10-11", "/game/" + game.Id })
        {
            var html = await h.Client.GetStringAsync(path);
            Assert.Contains("Quality and hype use", html);
            Assert.Contains("href=\"https://powerrankingsguru.com/nhl/power-rankings\"", html);
            ProHypeOutputAssertions.NoDiagnostics(html);
        }
        Assert.Single(handler.Requests);
        Assert.Empty(h.Handler.Requests);
        h.Clock.Now = new DateTimeOffset(game.ScheduledStart);
        Assert.Null((await Resolve(h, game)).Total);
        Assert.DoesNotContain("Quality and hype use", await h.Client.GetStringAsync("/game/" + game.Id));
    }

    [Theory]
    [InlineData(false, 24)]
    [InlineData(true, 24)]
    [InlineData(false, 48)]
    [InlineData(true, 48)]
    public async Task Unchanged_publication_retains_original_tuple_but_same_publication_correction_revokes_durably(bool correction, int hours)
    {
        using var handler = new Handler();
        await using var h = new MoneyPuckQualificationHarness();
        await Start(h, handler);
        Assert.Equal(32, await Refresh(h));
        var game = await AddGame(h, 6);
        var original = await Resolve(h, game);
        Assert.NotNull(original.Total);
        h.Clock.Now = h.Clock.Now.AddHours(hours);
        var valid = handler.Html;
        if (correction) handler.Html = valid.Replace("philadelphia-flyers", "SWAP").Replace("pittsburgh-penguins", "philadelphia-flyers").Replace("SWAP", "pittsburgh-penguins");
        Assert.Equal(hours == 24 ? 32 : 0, await Refresh(h));
        var rows = await h.RowsAsync("powerrankingsguru_nhl");
        Assert.All(rows, row => Assert.Equal(hours == 24, ProHypePolicy.EligibleRank(row, "nhl", 2027, h.Clock.Now.UtcDateTime)));
        var resolution = await Resolve(h, game);
        if (correction) Assert.Null(resolution.Total);
        else
        {
            Assert.Equal(original.Total, resolution.Total);
            Assert.Equal(original.EvaluatedAt, resolution.EvaluatedAt);
            Assert.Equal(JsonSerializer.Serialize(original.Components), JsonSerializer.Serialize(resolution.Components));
        }
        handler.Html = valid;
        h.Clock.Now = h.Clock.Now.AddHours(24);
        Assert.Equal(0, await Refresh(h));
        await h.RestartApplicationAsync();
        resolution = await Resolve(h, game);
        if (correction) Assert.Null(resolution.Total);
        else Assert.Equal(original.Total, resolution.Total);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_withdrawal_of_actual_ranking_support_revokes_and_restore_cannot_undo_it(bool withdrawSupport)
    {
        using var handler = new Handler();
        await using var h = new MoneyPuckQualificationHarness();
        await Start(h, handler);
        Assert.Equal(32, await Refresh(h));
        var game = await AddGame(h, 6);
        var original = await Resolve(h, game);
        Assert.NotNull(original.Total);
        h.Clock.Now = h.Clock.Now.AddHours(48);
        using (var scope = h.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            if (!withdrawSupport)
            {
                db.TeamRatings.AddRange(ProHypeTests.Ranks(game));
                await db.SaveChangesAsync();
            }
            var source = withdrawSupport ? NhlPowerRankingsGuruService.Source : MoneyPuckRankings.Source;
            var row = await db.TeamRatings.SingleAsync(r => r.Source == source && r.TeamAbbreviation == game.HomeTeamAbbreviation);
            db.TeamRatings.Remove(row);
            await db.SaveChangesAsync();
            db.TeamRatings.Add(row);
            await db.SaveChangesAsync();
        }
        await h.RestartApplicationAsync();
        var resolution = await Resolve(h, game);
        if (withdrawSupport) Assert.Null(resolution.Total);
        else
        {
            Assert.Equal(original.Total, resolution.Total);
            Assert.Equal(original.EvaluatedAt, resolution.EvaluatedAt);
        }
        Assert.Single(handler.Requests);
    }

    private static async Task<int> RefreshAfterRestart(MoneyPuckQualificationHarness h, Handler handler)
    {
        using var http = new HttpClient(handler, false);
        return await new NhlPowerRankingsGuruService(http, h.App.Services.GetRequiredService<IServiceScopeFactory>(), h.Clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NhlPowerRankingsGuruService>.Instance).RefreshAsync();
    }

    [Fact]
    public async Task Daily_admission_is_atomic_persisted_and_not_reset_by_migration_restart_or_unrelated_MoneyPuck_state()
    {
        using var handler = new Handler();
        await using var h = new MoneyPuckQualificationHarness();
        await Start(h, handler);
        using (var scope = h.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            db.MoneyPuckRefreshOperations.Add(new() { NextAttemptAtTicks = h.Clock.Now.AddDays(5).Ticks, DeniedUntilTicks = h.Clock.Now.AddDays(6).Ticks, Active = true, Denied = true });
            await db.SaveChangesAsync();
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Refresh(h)));
        Assert.Equal(32, results.Sum());
        Assert.Single(handler.Requests);
        using (var scope = h.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "037_NhlGuruRankings.sql"));
            await db.Database.ExecuteSqlRawAsync(sql);
            await db.Database.ExecuteSqlRawAsync(sql);
            Assert.Equal(h.Clock.Now.AddDays(1).Ticks, (await db.NhlGuruRefreshOperations.SingleAsync()).NextAttemptAtTicks);
            var mp = await db.MoneyPuckRefreshOperations.SingleAsync();
            Assert.True(mp.Active && mp.Denied);
            Assert.Equal(h.Clock.Now.AddDays(5).Ticks, mp.NextAttemptAtTicks);
            Assert.Equal(h.Clock.Now.AddDays(6).Ticks, mp.DeniedUntilTicks);
        }
        await h.RestartApplicationAsync();
        h.Clock.Now = h.Clock.Now.AddHours(24).AddTicks(-1);
        Assert.Equal(0, await RefreshAfterRestart(h, handler));
        Assert.Single(handler.Requests);
        h.Clock.Now = h.Clock.Now.AddTicks(1);
        Assert.Equal(32, await RefreshAfterRestart(h, handler));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Empty(h.Handler.Requests);
    }

    [Theory]
    [InlineData("http500")]
    [InlineData("http403")]
    [InlineData("http429")]
    [InlineData("redirect")]
    [InlineData("challenge")]
    [InlineData("transport")]
    [InlineData("cancelled")]
    public async Task Failure_preserves_observations_and_consumes_daily_budget_with_no_retries_or_other_requests(string failure)
    {
        using var handler = new Handler();
        await using var h = new MoneyPuckQualificationHarness();
        await Start(h, handler);
        Assert.Equal(32, await Refresh(h));
        var previous = JsonSerializer.Serialize(await h.RowsAsync(NhlPowerRankingsGuruService.Source));
        h.Clock.Now = h.Clock.Now.AddDays(1);
        handler.Status = failure switch { "http500" => HttpStatusCode.InternalServerError, "http403" => HttpStatusCode.Forbidden,
            "http429" => HttpStatusCode.TooManyRequests, "redirect" => HttpStatusCode.Found, _ => HttpStatusCode.OK };
        if (failure == "http429") handler.RetryAt = h.Clock.Now.AddDays(3);
        if (failure == "challenge") handler.Html = "<html><title>Just a moment...</title><div id='challenge-form'></div></html>";
        if (failure == "transport") handler.Failure = new HttpRequestException("Synthetic offline failure");
        if (failure == "cancelled") handler.Failure = new OperationCanceledException("Synthetic interrupted request");
        Assert.Equal(0, await Refresh(h));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(previous, JsonSerializer.Serialize(await h.RowsAsync(NhlPowerRankingsGuruService.Source)));
        await h.RestartApplicationAsync();
        h.Clock.Now = h.Clock.Now.AddDays(1).AddTicks(-1);
        Assert.Equal(0, await RefreshAfterRestart(h, handler));
        if (failure == "http429")
        {
            h.Clock.Now = handler.RetryAt!.Value.AddTicks(-1);
            Assert.Equal(0, await RefreshAfterRestart(h, handler));
        }
        Assert.Equal(2, handler.Requests.Count);
        Assert.Empty(h.Handler.Requests);
    }

    [Theory]
    [InlineData("missing_team")]
    [InlineData("duplicate_team")]
    [InlineData("unknown_team")]
    [InlineData("duplicate_rank")]
    [InlineData("rank_zero")]
    [InlineData("rank_fraction")]
    [InlineData("wrong_season")]
    [InlineData("inconsistent_season")]
    [InlineData("wrong_sport")]
    [InlineData("missing_publication")]
    [InlineData("future_publication")]
    [InlineData("wrong_timezone")]
    [InlineData("ambiguous_table")]
    [InlineData("wrong_columns")]
    public async Task Successful_invalid_batch_revokes_support_even_when_restored_before_read_and_after_restart(string fault)
    {
        using var handler = new Handler();
        await using var h = new MoneyPuckQualificationHarness();
        await Start(h, handler);
        Assert.Equal(32, await Refresh(h));
        var game = await AddGame(h, 6);
        Assert.NotNull((await Resolve(h, game)).Total);
        h.Clock.Now = h.Clock.Now.AddDays(1);
        var valid = handler.Html;
        using (var document = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(valid, default))
        {
            var rows = document.QuerySelectorAll("tbody tr");
            switch (fault)
            {
                case "missing_team": rows[31].Remove(); break;
                case "duplicate_team": rows[31].QuerySelector("a")!.SetAttribute("href", rows[0].QuerySelector("a")!.GetAttribute("href")!); break;
                case "unknown_team": rows[31].QuerySelector("a")!.SetAttribute("href", "/nhl/teams/unknown-team"); break;
                case "duplicate_rank": rows[31].QuerySelector("td")!.TextContent = "31"; break;
                case "rank_zero": rows[0].QuerySelector("td")!.TextContent = "0"; break;
                case "rank_fraction": rows[0].QuerySelector("td")!.TextContent = "1.5"; break;
                case "wrong_season": document.QuerySelector("h1")!.TextContent = "2025-26 NHL Power Rankings"; break;
                case "inconsistent_season": document.QuerySelector("h1")!.TextContent = "2026-28 NHL Power Rankings"; break;
                case "wrong_sport": document.QuerySelector("h1")!.TextContent = "2026-27 NBA Power Rankings"; break;
                case "missing_publication": document.QuerySelector("header p")!.Remove(); break;
                case "future_publication": document.QuerySelector("header p")!.TextContent = "Updated Oct 13, 2026, 4:30 AM ET"; break;
                case "wrong_timezone": document.QuerySelector("header p")!.TextContent = "Updated Oct 11, 2026, 4:30 AM UTC"; break;
                case "ambiguous_table": document.QuerySelector("table")!.ParentElement!.AppendChild(document.QuerySelector("table")!.Clone()); break;
                case "wrong_columns": document.QuerySelector("th")!.TextContent = "Seed"; break;
            }
            handler.Html = document.DocumentElement.OuterHtml;
        }
        Assert.Equal(0, await Refresh(h));
        Assert.All(await h.RowsAsync(NhlPowerRankingsGuruService.Source), row => { Assert.Null(row.PowerRank); Assert.Null(row.SourceSeason); });
        handler.Html = valid;
        h.Clock.Now = h.Clock.Now.AddDays(1);
        Assert.Equal(0, await Refresh(h));
        await h.RestartApplicationAsync();
        Assert.Null((await Resolve(h, game)).Total);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Empty(h.Handler.Requests);
    }

    [Theory]
    [InlineData("Updated Nov 1, 2026, 1:30 AM ET", 2026, 11, 2)]
    [InlineData("Updated Mar 14, 2027, 2:30 AM ET", 2027, 3, 15)]
    public async Task Ambiguous_or_nonexistent_Eastern_publication_is_not_a_timestamp(string label, int year, int month, int day)
    {
        using var handler = new Handler();
        handler.Html = handler.Html.Replace("Updated Oct 11, 2026, 4:30 AM ET", label);
        await using var h = new MoneyPuckQualificationHarness();
        h.Clock.Now = new(year, month, day, 18, 0, 0, TimeSpan.Zero);
        await Start(h, handler);
        Assert.Equal(0, await Refresh(h));
        Assert.Empty(await h.RowsAsync(NhlPowerRankingsGuruService.Source));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void Quality_uses_a_complete_coherent_same_source_pair_after_MoneyPuck_not_legacy_Guru_or_mixed_ranks()
    {
        var game = ProHypeTests.Game();
        var guru = ProHypeTests.Ranks(game, source: NhlPowerRankingsGuruService.Source);
        var mp = ProHypeTests.Ranks(game, 14, 15);
        Assert.Equal(96.84979838709677, ProHypePolicy.Quality(game, guru, ProHypeTests.Now).Value!.Value, 10);
        Assert.Equal(56.20659722222222, ProHypePolicy.Quality(game, [.. guru, .. mp], ProHypeTests.Now).Value!.Value, 10);
        mp[1].SourceSeason = 2026;
        Assert.Equal(96.84979838709677, ProHypePolicy.Quality(game, [.. guru, .. mp], ProHypeTests.Now).Value!.Value, 10);
        Assert.Null(ProHypePolicy.Quality(game, [mp[0], guru[1]], ProHypeTests.Now).Value);
        Assert.Null(ProHypePolicy.Quality(game, ProHypeTests.Ranks(game, source: "powerrankingsguru"), ProHypeTests.Now).Value);
        guru[1].RankPublishedAt = ProHypeTests.Now.AddMinutes(-1);
        Assert.Null(ProHypePolicy.Quality(game, guru, ProHypeTests.Now).Value);
        guru[1].RankPublishedAt = guru[0].RankPublishedAt;
        guru[1].FetchedAt = ProHypeTests.Now.AddMinutes(-1);
        Assert.Null(ProHypePolicy.Quality(game, guru, ProHypeTests.Now).Value);
        guru[1].FetchedAt = ProHypeTests.Now;
        guru[1].RankPublishedAt = null;
        Assert.Null(ProHypePolicy.Quality(game, guru, ProHypeTests.Now).Value);
    }

    [Fact]
    public async Task Registered_refresh_collects_one_page_for_quality_without_importing_forecast_columns()
    {
        using var handler = new Handler();
        await using var h = new MoneyPuckQualificationHarness();
        await Start(h, handler);
        Assert.Equal(32, await h.App.Services.GetRequiredService<PowerRankingsGuruService>().RefreshNhlRatingsAsync(null));
        var rows = await h.RowsAsync("powerrankingsguru_nhl");
        Assert.Equal(32, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal(2027, row.SourceSeason);
            Assert.Equal(new DateTime(2026, 10, 11, 8, 30, 0, DateTimeKind.Utc), row.RankPublishedAt);
            Assert.Equal(h.Clock.Now.UtcDateTime, row.FetchedAt);
            Assert.Null(row.PlayoffProbability);
            Assert.Null(row.ChampionshipProbability);
        });
        Assert.Equal(new[] { "https://powerrankingsguru.com/nhl/power-rankings" }, handler.Requests);
        Assert.Empty(h.Handler.Requests);
        var game = ProHypeTests.Game();
        game.ScheduledStart = h.Clock.Now.AddHours(4).UtcDateTime;
        game.SeasonType = 1;
        var evidence = ProHypeTests.Evidence(game);
        evidence.ObservedAt = h.Clock.Now.UtcDateTime;
        game.ProEvidenceJson = JsonSerializer.Serialize(evidence);
        var odds = ProHypeTests.Odds(game);
        odds.FetchedAt = h.Clock.Now.UtcDateTime;
        using var scope = h.App.Services.CreateScope();
        var score = scope.ServiceProvider.GetRequiredService<HypeScoreService>().EvaluateFromEvidence(game, rows, odds, [], h.Clock.Now.UtcDateTime);
        Assert.True(score.Components.IsComplete);
        Assert.Equal(96.84979838709677, score.Components.QualityScore, 10);
        Assert.Equal(2, score.Assessment.SupportingEvidence.Count(r => r.Kind == ProEvidenceKind.Ranking && r.Source == "powerrankingsguru_nhl"));
        game.SeasonType = 2;
        evidence.Phase = 2;
        game.ProEvidenceJson = JsonSerializer.Serialize(evidence);
        foreach (var row in rows) row.PlayoffProbability = .5;
        var regular = scope.ServiceProvider.GetRequiredService<HypeScoreService>().EvaluateFromEvidence(game, rows, odds, [], h.Clock.Now.UtcDateTime);
        Assert.True(regular.Components.QualityAvailable);
        Assert.False(regular.Components.StakesAvailable);
        Assert.Null(regular.Components.HomePlayoffOdds);
        Assert.Null(regular.Components.AwayPlayoffOdds);
    }
}
