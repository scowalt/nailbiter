using backend.Models;
using backend.Scoring;
using backend.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.tests;

public sealed class MoneyPuckQualificationTests
{
    [Fact]
    public async Task Disabled_or_unapproved_refresh_has_no_provider_requests()
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync(enabled: false);
        Assert.Equal(0, await h.Service.RefreshNhlRatingsForSeasonAsync(2027, default));
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(0, await h.Service.RefreshNhlRatingsForSeasonAsync(2028, default));
        Assert.Empty(h.Handler.Requests);
        Assert.Empty(await h.RowsAsync());
    }

    [Fact]
    public async Task One_daily_cycle_collects_both_inputs_and_all_public_callers_share_the_budget()
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        Assert.Equal(32, await h.Service.RefreshNhlRatingsForSeasonAsync(2027, default));
        using (var scope = h.App.Services.CreateScope())
        {
            var rank = await scope.ServiceProvider.GetRequiredService<GameDataService>().GetTeamRankInfoAsync("nhl", "OTT", 2027, 2, default);
            Assert.Equal(1, rank.Value.PowerRank);
        }
        Assert.Equal(.2518, (await h.SelectAsync()).Probability.Value);
        var calls = h.Handler.Requests.Count;
        var rankings = h.App.Services.GetRequiredService<MoneyPuckService>();
        Assert.Equal(0, await rankings.RefreshNhlRatingsAsync(default));
        h.Clock.Now = h.Clock.Now.AddHours(6);
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(0, await rankings.RefreshNhlRatingsAsync(default));
        Assert.Equal(calls, h.Handler.Requests.Count);
        h.Clock.Now = h.Clock.Now.AddHours(18).AddTicks(-1);
        Assert.Equal(0, await rankings.RefreshNhlRatingsAsync(default));
        Assert.Equal(calls, h.Handler.Requests.Count);
        h.Clock.Now = h.Clock.Now.AddTicks(1);
        h.Handler.Marker = h.Clock.Now.AddMinutes(-5).ToString("O");
        Assert.Equal(32, await rankings.RefreshNhlRatingsAsync(default));
        Assert.Equal(.2518, (await h.SelectAsync()).Probability.Value);
        Assert.Equal(2, h.Handler.Requests.Count(url => url == MoneyPuckQualificationHandler.CsvUrl));
        Assert.Equal(2, h.Handler.Requests.Count(url => url == MoneyPuckRankings.RankingsUrl));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public async Task Failed_daily_attempt_survives_backend_restart_and_preserves_exact_24_hour_boundary(int tickRemainder)
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        h.Clock.Now = h.Clock.Now.AddTicks(tickRemainder);
        var attempted = h.Clock.Now;
        h.Handler.Override = (_, _) => Task.FromResult<HttpResponseMessage?>(new(System.Net.HttpStatusCode.ServiceUnavailable));
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Single(h.Handler.Requests);
        await h.RestartApplicationAsync();
        h.Handler.Override = null;
        h.Clock.Now = attempted.AddHours(6);
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Single(h.Handler.Requests);
        h.Clock.Now = attempted.AddHours(24).AddTicks(-1);
        Assert.Equal(0, await h.App.Services.GetRequiredService<MoneyPuckService>().RefreshNhlRatingsAsync(default));
        Assert.Single(h.Handler.Requests);
        h.Clock.Now = attempted.AddHours(24);
        h.Handler.Marker = h.Clock.Now.AddMinutes(-5).ToString("O");
        Assert.Equal(32, await h.App.Services.GetRequiredService<MoneyPuckService>().RefreshNhlRatingsAsync(default));
        Assert.Equal(.2518, (await h.SelectAsync()).Probability.Value);
        Assert.Single(h.Handler.Requests, url => url == MoneyPuckQualificationHandler.CsvUrl);
        Assert.Single(h.Handler.Requests, url => url == MoneyPuckRankings.RankingsUrl);
    }

    [Fact]
    public async Task Concurrent_callers_and_failed_attempts_share_daily_last_attempt_cooldown()
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Handler.Override = async (_, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); return new(System.Net.HttpStatusCode.ServiceUnavailable); };
        var first = h.Service.RefreshNhlRatingsAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, await h.Service.RefreshNhlRatingsForSeasonAsync(2027, default));
        release.SetResult();
        Assert.Equal(0, await first);
        h.Handler.Override = null;
        h.Clock.Now = h.Clock.Now.AddHours(24).AddTicks(-1);
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Single(h.Handler.Requests);
        h.Clock.Now = h.Clock.Now.AddTicks(1);
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(10, h.Handler.Requests.Count);
    }

    [Fact]
    public async Task Unchanged_publication_preserves_fetch_precision_and_expires_at_exact_boundary()
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        h.Clock.Now = h.Clock.Now.AddTicks(9);
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        var original = await h.RowsAsync();
        var evidence = PlayoffModelEvidence.Parse(original[0].PlayoffEvidenceJson)!;
        Assert.Equal(h.Clock.Now.UtcDateTime, evidence.FetchedAt);
        Assert.Equal(h.Clock.Now.Ticks / 10 * 10, original[0].FetchedAt.Ticks);
        h.Clock.Now = h.Clock.Now.AddHours(24);
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(original.Select(r => r.PlayoffEvidenceJson), (await h.RowsAsync()).Select(r => r.PlayoffEvidenceJson));
        h.Clock.Now = new DateTimeOffset(evidence.PublishedAt!.Value.AddHours(36).AddTicks(-1));
        Assert.Equal(.2518, (await h.SelectAsync()).Probability.Value);
        h.Clock.Now = h.Clock.Now.AddTicks(1);
        Assert.Null((await h.SelectAsync()).Probability);
        var calls = h.Handler.Requests.Count;
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(calls, h.Handler.Requests.Count);
        h.Clock.Now = new DateTimeOffset(evidence.FetchedAt!.Value.AddHours(48));
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.All(await h.RowsAsync(), row => Assert.Null(row.PlayoffProbability));
    }

    [Fact]
    public async Task Regression_revokes_all_teams_and_cannot_recover_from_an_older_publication()
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        h.Clock.Now = h.Clock.Now.AddHours(24);
        h.Handler.Marker = "2026-10-11T16:00:00Z";
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Null((await h.SelectAsync()).Probability);
        Assert.All(await h.RowsAsync(), row =>
        {
            Assert.Null(row.PlayoffProbability);
            var evidence = PlayoffModelEvidence.Parse(row.PlayoffEvidenceJson)!;
            Assert.Equal("publication_regression", evidence.ReasonCode);
            Assert.Equal(new DateTime(2026, 10, 11, 16, 0, 0, DateTimeKind.Utc), evidence.PublishedAt);
            Assert.True(evidence.PublicationBoundary > evidence.PublishedAt);
        });
        h.Clock.Now = h.Clock.Now.AddHours(24);
        h.Handler.Marker = "2026-10-11T16:30:00Z";
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Null((await h.SelectAsync()).Probability);
        h.Clock.Now = h.Clock.Now.AddHours(24);
        h.Handler.Marker = h.Clock.Now.AddMinutes(-10).ToString("O");
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(.2518, (await h.SelectAsync()).Probability.Value);
    }

    [Theory]
    [InlineData(1, 401, "")]
    [InlineData(2, 403, "")]
    [InlineData(3, 429, "")]
    [InlineData(4, 200, "<html><title>Just a moment...</title><div id='cf-chl-widget'>Checking your browser</div></html>")]
    [InlineData(5, 200, "<html><head><title>Please verify you are human: CAPTCHA</title></head></html>")]
    [InlineData(6, 200, "<html><head><title>Access denied</title></head></html>")]
    [InlineData(2, 200, "<html><head><title>Enable JavaScript and cookies to continue</title></head></html>")]
    public async Task Denial_or_challenge_stops_attempt_and_blocks_callers_for_24_hours_without_changing_evidence(int stopAt, int status, string body)
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        var original = await h.RowsAsync();
        h.Clock.Now = h.Clock.Now.AddHours(24);
        h.Handler.Marker = h.Clock.Now.AddMinutes(-5).ToString("O");
        var calls = 0;
        h.Handler.Override = (_, _) => Task.FromResult<HttpResponseMessage?>(++calls == stopAt
            ? new((System.Net.HttpStatusCode)status) { Content = new StringContent(body) } : null);
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(stopAt, calls);
        Assert.Equal(original.Select(r => r.PlayoffEvidenceJson), (await h.RowsAsync()).Select(r => r.PlayoffEvidenceJson));
        h.Handler.Override = null;
        h.Clock.Now = h.Clock.Now.AddHours(24).AddTicks(-1);
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(0, await h.Service.RefreshNhlRatingsForSeasonAsync(2027, default));
        Assert.Equal(9 + stopAt, h.Handler.Requests.Count);
        Assert.Null((await h.SelectAsync()).Probability);
        h.Clock.Now = h.Clock.Now.AddTicks(1);
        h.Handler.Marker = h.Clock.Now.AddMinutes(-5).ToString("O");
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(18 + stopAt, h.Handler.Requests.Count);
    }

    [Fact]
    public async Task Successful_invalid_snapshot_revokes_entire_batch_and_same_publication_cannot_resurrect_it()
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        var csv = h.Handler.Csv;
        h.Clock.Now = h.Clock.Now.AddHours(24);
        h.Handler.Marker = h.Clock.Now.AddMinutes(-10).ToString("O");
        h.Handler.Csv = csv.Replace("NSH,ALL,0.2518", "NSH,ALL,NaN");
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.All(await h.RowsAsync(), row =>
        {
            Assert.Null(row.PlayoffProbability);
            Assert.Equal("unavailable", PlayoffModelEvidence.Parse(row.PlayoffEvidenceJson)!.State);
            Assert.Equal(0, PlayoffModelEvidence.Parse(row.PlayoffEvidenceJson)!.AcceptedTeamCount);
        });
        Assert.Null((await h.SelectAsync()).Probability);
        h.Handler.Csv = csv;
        h.Clock.Now = h.Clock.Now.AddHours(24);
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Null((await h.SelectAsync()).Probability);
        h.Clock.Now = h.Clock.Now.AddHours(24);
        h.Handler.Marker = h.Clock.Now.AddMinutes(-10).ToString("O");
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(.2518, (await h.SelectAsync()).Probability.Value);
    }

    [Fact]
    public async Task Season_heading_only_in_historical_link_is_not_forecast_season_proof()
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        h.Clock.Now = h.Clock.Now.AddHours(24);
        h.Handler.Marker = h.Clock.Now.AddMinutes(-5).ToString("O");
        h.Handler.Page = "<html><body><h1><a href='archive.htm'>2026-2027 NHL Playoff Predictions</a></h1><main>Forecast unavailable</main></body></html>";
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Null((await h.SelectAsync()).Probability);
        Assert.All(await h.RowsAsync(), r => Assert.Null(r.PlayoffProbability));
    }

    [Theory]
    [InlineData(302)]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Redirects_or_transport_failures_and_publication_races_retain_all_clocks(int status)
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        var original = await h.RowsAsync();
        h.Clock.Now = h.Clock.Now.AddHours(24);
        h.Handler.Override = (_, _) => Task.FromResult<HttpResponseMessage?>(new((System.Net.HttpStatusCode)status) { Headers = { Location = new Uri("https://different.example/access") } });
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(10, h.Handler.Requests.Count);
        Assert.Equal(original.Select(r => r.PlayoffEvidenceJson), (await h.RowsAsync()).Select(r => r.PlayoffEvidenceJson));
        Assert.Equal(original.Select(r => r.FetchedAt), (await h.RowsAsync()).Select(r => r.FetchedAt));
        h.Clock.Now = h.Clock.Now.AddHours(24);
        var markerCalls = 0;
        h.Handler.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsoluteUri == MoneyPuckQualificationHandler.MarkerUrl && ++markerCalls == 2
            ? new(System.Net.HttpStatusCode.OK) { Content = new StringContent(h.Clock.Now.AddMinutes(-1).ToString("O")) } : null);
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(original.Select(r => r.PlayoffEvidenceJson), (await h.RowsAsync()).Select(r => r.PlayoffEvidenceJson));
        Assert.Equal(original.Select(r => r.FetchedAt), (await h.RowsAsync()).Select(r => r.FetchedAt));
        h.Clock.Now = h.Clock.Now.AddHours(24);
        h.Handler.Override = null;
        h.Handler.Marker = h.Clock.Now.AddMinutes(-1).ToString("O");
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(.2518, (await h.SelectAsync()).Probability.Value);
    }

    [Fact]
    public async Task Clock_regression_during_successful_retrieval_rejects_the_entire_batch()
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        h.Clock.Now = h.Clock.Now.AddHours(24);
        h.Handler.Marker = h.Clock.Now.AddMinutes(-10).ToString("O");
        var markers = 0;
        h.Handler.Override = (request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri == MoneyPuckQualificationHandler.MarkerUrl && ++markers == 2) h.Clock.Now = h.Clock.Now.AddMinutes(-1);
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.All(await h.RowsAsync(), row => Assert.Null(row.PlayoffProbability));
        Assert.Null((await h.SelectAsync()).Probability);
    }

    [Fact]
    public async Task Conflicting_unlabelled_forecast_heading_cannot_borrow_two_labels_from_another_heading()
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        h.Handler.Page = "<main><h1>NHL Playoff Predictions</h1><h2>2026-27 and 2026-2027 NHL Playoff Predictions</h2></main>";
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Null((await h.SelectAsync()).Probability);
        Assert.All(await h.RowsAsync(), row => Assert.Null(row.PlayoffProbability));
    }

    [Fact]
    public async Task Changed_probabilities_under_unchanged_marker_revoke_inconsistent_publication()
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        h.Clock.Now = h.Clock.Now.AddHours(24);
        h.Handler.Csv = h.Handler.Csv.Replace("NSH,ALL,0.2518", "NSH,ALL,0.3");
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Null((await h.SelectAsync()).Probability);
        Assert.All(await h.RowsAsync(), row => Assert.Null(row.PlayoffProbability));
    }

    [Fact]
    public async Task Refresh_imports_complete_explicitly_verified_snapshot_for_public_selection()
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        Assert.Equal(32, await h.Service.RefreshNhlRatingsForSeasonAsync(2027, default));
        var selection = await h.SelectAsync();
        Assert.Equal(.2518, selection.Probability.Value);
        Assert.Equal(MoneyPuckQualificationHarness.Source, selection.Source.Value);
        Assert.False(selection.IsFinal);
        var rows = await h.RowsAsync();
        Assert.Equal(32, rows.Count);
        Assert.Equal(MoneyPuckQualificationHarness.Teams.Order(), rows.Select(r => r.TeamAbbreviation));
        Assert.Single(rows.Select(r => PlayoffModelEvidence.Parse(r.PlayoffEvidenceJson)!.RefreshId).Distinct());
        Assert.All(rows, row =>
        {
            var proof = PlayoffModelEvidence.Parse(row.PlayoffEvidenceJson)!.MoneyPuckSeasonProof!;
            Assert.Equal(2, proof.VerifiedGames.Length);
            Assert.Contains(proof.VerifiedGames, game => game.SimulatedId == proof.FirstSimulatedId);
            Assert.All(proof.VerifiedGames, game => { Assert.Equal(game.SimulatedId, game.GameId); Assert.Equal(20262027, game.Season); });
        });
        using var scope = h.App.Services.CreateScope();
        var rank = (await scope.ServiceProvider.GetRequiredService<GameDataService>().GetTeamRankInfoAsync("nhl", "NSH", 2027, 2, default)).Value;
        Assert.Equal(.2518, rank.PlayoffOdds.Value);
        ProQualificationEvidenceKind kind = rank.PlayoffEvidenceKind;
        Assert.Equal(ProQualificationEvidenceKind.MoneyPuckModel, kind);
        string? source = rank.PlayoffSource;
        Assert.Equal(MoneyPuckQualificationHarness.Source, source);
        var game = new Game
        {
            Id = "synthetic-collector-read", Sport = "nhl", SeasonYear = 2027, SeasonType = 2,
            HomeTeam = "Synthetic Nashville", AwayTeam = "Synthetic Ottawa",
            HomeTeamAbbreviation = "NSH", AwayTeamAbbreviation = "OTT", Status = "scheduled",
            ScheduledStart = h.Clock.Now.AddHours(4).UtcDateTime
        };
        var hype = await scope.ServiceProvider.GetRequiredService<HypeScoreService>().GetHypeComponentsAsync(game, null);
        Assert.Equal(.2518, hype.HomePlayoffOdds);
        Assert.Equal(.6804, hype.AwayPlayoffOdds);
        Assert.Equal(ProQualificationEvidenceKind.MoneyPuckModel, hype.HomeQualificationEvidenceKind);
        Assert.False(hype.HomeQualificationIsFinal);
        Assert.False(hype.IsComplete);
        Assert.Equal(9, h.Handler.Requests.Count);
    }
}
