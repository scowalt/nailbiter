using System.Text.Json;
using backend.Data;
using backend.Models;
using backend.Scoring;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace backend.tests;

public sealed class ProHypeLifecycleMoneyPuckTests : IAsyncLifetime
{
    private readonly MoneyPuckQualificationHarness harness = new();
    private Game game = null!;

    public async Task InitializeAsync()
    {
        await harness.StartAsync();
        using var scope = harness.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        await db.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "035_ProHypeLifecycle.sql")));
        Assert.Equal(32, await harness.Service.RefreshNhlRatingsForSeasonAsync(2027, default));
        game = ProHypeTests.Game();
        game.HomeTeamAbbreviation = "OTT";
        game.AwayTeamAbbreviation = "NSH";
        game.ScheduledStart = harness.Clock.Now.AddDays(6).UtcDateTime;
        var evidence = ProHypeTests.Evidence(game);
        evidence.ObservedAt = harness.Clock.Now.UtcDateTime;
        game.ProEvidenceJson = JsonSerializer.Serialize(evidence);
        var odds = ProHypeTests.Odds(game);
        odds.FetchedAt = harness.Clock.Now.UtcDateTime;
        var ranks = ProHypeTests.Ranks(game);
        foreach (var rank in ranks) { rank.FetchedAt = harness.Clock.Now.UtcDateTime; rank.RankPublishedAt = harness.Clock.Now.UtcDateTime; }
        db.Games.Add(game);
        db.PregameOdds.Add(odds);
        foreach (var rank in ranks)
            if (!await db.TeamRatings.AnyAsync(r => r.Sport == rank.Sport && r.Season == rank.Season
                && r.Source == rank.Source && r.TeamAbbreviation == rank.TeamAbbreviation)) db.TeamRatings.Add(rank);
        await db.SaveChangesAsync();
    }

    private async Task<ProHypeResolution> Resolve()
    {
        using var scope = harness.App.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IProHypeLifecycle>().ResolveAsync(game.Id);
    }

    [Fact]
    public async Task Original_model_credit_survives_expiry_and_new_current_selection_without_any_provider_access()
    {
        var captured = await Resolve();
        Assert.NotNull(captured.Total);
        Assert.Equal(ProQualificationEvidenceKind.MoneyPuckModel, captured.Components.AwayQualificationEvidenceKind);
        var requests = harness.Handler.Requests.Count;
        harness.Clock.Now = harness.Clock.Now.AddDays(3);
        await harness.RestartApplicationAsync();
        using (var scope = harness.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            var raw = scope.ServiceProvider.GetRequiredService<HypeScoreService>().CalculateFromEvidence(game,
                await db.TeamRatings.ToArrayAsync(), await db.PregameOdds.SingleAsync(), [], harness.Clock.Now.UtcDateTime);
            Assert.False(raw.StakesAvailable);
            db.TeamRatings.Add(new TeamRating { Sport = "nhl", TeamAbbreviation = "NSH", Season = 2027,
                SourceSeason = 2027, Source = "kalshi", FetchedAt = harness.Clock.Now.UtcDateTime, PlayoffProbability = .5 });
            await db.SaveChangesAsync();
        }
        var retained = await Resolve();
        Assert.Equal(captured.Total, retained.Total);
        Assert.Equal(captured.Components.AwayPlayoffOdds, retained.Components.AwayPlayoffOdds);
        Assert.Equal(ProQualificationEvidenceKind.MoneyPuckModel, retained.Components.AwayQualificationEvidenceKind);
        Assert.Equal(captured.EvaluatedAt, retained.EvaluatedAt);
        Assert.Equal(requests, harness.Handler.Requests.Count);
    }

    [Fact]
    public async Task Successful_age_only_collection_keeps_original_complete_hype_and_credit_across_recreation()
    {
        using (var scope = harness.App.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IProHypeLifecycle>().CaptureAsync();
        var captured = await Resolve();
        Assert.Equal(46.690811288126092, captured.Total!.Value, 10);
        harness.Clock.Now = harness.Clock.Now.AddHours(48);
        Assert.Equal(captured.Total, (await Resolve()).Total);
        Assert.Equal(0, await harness.Service.RefreshNhlRatingsForSeasonAsync(2027, default));
        Assert.Contains(MoneyPuckQualificationHandler.CsvUrl, harness.Handler.Requests);
        var selection = await harness.SelectAsync();
        Assert.Null(selection.Probability);
        using (var scope = harness.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            var raw = scope.ServiceProvider.GetRequiredService<HypeScoreService>().CalculateFromEvidence(game,
                await db.TeamRatings.ToArrayAsync(), await db.PregameOdds.SingleAsync(), [], harness.Clock.Now.UtcDateTime);
            Assert.False(raw.StakesAvailable);
        }
        var retained = await Resolve();
        Assert.Equal(captured.Total, retained.Total);
        Assert.Equal(JsonSerializer.Serialize(captured.Components), JsonSerializer.Serialize(retained.Components));
        Assert.Equal(captured.EvaluatedAt, retained.EvaluatedAt);
        await harness.RestartApplicationAsync();
        retained = await Resolve();
        Assert.Equal(captured.Total, retained.Total);
        Assert.Equal(JsonSerializer.Serialize(captured.Components), JsonSerializer.Serialize(retained.Components));
        Assert.Equal(captured.EvaluatedAt, retained.EvaluatedAt);
        var html = await harness.Client.GetStringAsync("/game/" + game.Id);
        Assert.Contains("Hype Score", html);
        Assert.Contains("MoneyPuck", html);
        harness.Clock.Now = harness.Clock.Now.AddHours(24);
        Assert.Equal(0, await harness.Service.RefreshNhlRatingsForSeasonAsync(2027, default));
        retained = await Resolve();
        Assert.Equal(captured.Total, retained.Total);
        Assert.Equal(captured.EvaluatedAt, retained.EvaluatedAt);
        Assert.Null((await harness.SelectAsync()).Probability);
    }

    [Theory]
    [InlineData("nonfinite")]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("correction")]
    public async Task Hard_invalid_collection_of_an_expired_publication_still_revokes_support_durably(string rejection)
    {
        using (var scope = harness.App.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IProHypeLifecycle>().CaptureAsync();
        Assert.NotNull((await Resolve()).Total);
        harness.Clock.Now = harness.Clock.Now.AddHours(48);
        if (rejection == "correction")
        {
            Assert.Equal(0, await harness.Service.RefreshNhlRatingsForSeasonAsync(2027, default));
            Assert.NotNull((await Resolve()).Total);
            harness.Clock.Now = harness.Clock.Now.AddHours(24);
        }
        harness.Handler.Csv = rejection switch
        {
            "nonfinite" => harness.Handler.Csv.Replace("NSH,ALL,0.2518", "NSH,ALL,NaN"),
            "missing" => harness.Handler.Csv.Replace("NSH,ALL,0.2518", "NSH,ALL,"),
            "correction" => harness.Handler.Csv.Replace("NSH,ALL,0.2518", "NSH,ALL,0.4"),
            _ => "not a forecast CSV"
        };
        Assert.Equal(0, await harness.Service.RefreshNhlRatingsForSeasonAsync(2027, default));
        Assert.Null((await Resolve()).Total);
        harness.Handler.Csv = MoneyPuckQualificationHandler.Fixture("simulations.csv");
        harness.Clock.Now = harness.Clock.Now.AddHours(24);
        Assert.Equal(0, await harness.Service.RefreshNhlRatingsForSeasonAsync(2027, default));
        await harness.RestartApplicationAsync();
        Assert.Null((await Resolve()).Total);
        Assert.Null((await harness.SelectAsync()).Probability);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("allowlist")]
    public async Task Revoked_model_selection_is_durable_even_if_configuration_is_restored(string revocation)
    {
        Assert.NotNull((await Resolve()).Total);
        var options = harness.App.Services.GetRequiredService<IOptions<PlayoffRatingsOptions>>().Value;
        if (revocation == "disabled") options.MoneyPuckSelectionEnabled = false;
        else options.MoneyPuckApprovedSeasons = [];
        Assert.Null((await Resolve()).Total);
        options.MoneyPuckSelectionEnabled = true;
        options.MoneyPuckApprovedSeasons = [2027];
        Assert.Null((await Resolve()).Total);
        harness.Clock.Now = harness.Clock.Now.AddDays(3);
        await harness.RestartApplicationAsync();
        Assert.Null((await Resolve()).Total);
    }

    [Fact]
    public async Task A_successfully_rejected_model_publication_revokes_the_original_estimate()
    {
        Assert.NotNull((await Resolve()).Total);
        harness.Clock.Now = harness.Clock.Now.AddHours(24);
        harness.Handler.Csv = harness.Handler.Csv.Replace("NSH,ALL,0.2518", "NSH,ALL,NaN");
        Assert.Equal(0, await harness.Service.RefreshNhlRatingsForSeasonAsync(2027, default));
        Assert.Null((await Resolve()).Total);
        harness.Clock.Now = harness.Clock.Now.AddDays(2);
        await harness.RestartApplicationAsync();
        Assert.Null((await Resolve()).Total);
    }

    [Fact]
    public async Task Current_evaluation_classifies_missing_model_probability_as_rejection_even_after_expiry()
    {
        using var scope = harness.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        var rows = await db.TeamRatings.ToArrayAsync();
        rows.Single(r => r.Source == MoneyPuckQualificationHarness.Source && r.TeamAbbreviation == "NSH").PlayoffProbability = null;
        var evaluation = scope.ServiceProvider.GetRequiredService<HypeScoreService>().EvaluateFromEvidence(game,
            rows, await db.PregameOdds.SingleAsync(), [], harness.Clock.Now.AddDays(3).UtcDateTime);
        var model = evaluation.Assessment.Observations.Single(r => r.Kind == ProEvidenceKind.Qualification
            && r.Source == MoneyPuckQualificationHarness.Source && r.Team == "NSH");
        Assert.Equal(ProEvidenceState.Rejected, model.State);
        Assert.Equal("missing_probability", model.Reason);
        Assert.False(evaluation.Components.StakesAvailable);
    }

    public async Task DisposeAsync() => await harness.DisposeAsync();
}
