using System.Net;
using backend.Models;
using Xunit;

namespace backend.tests;

public sealed class MoneyPuckQualificationValidationTests
{
    [Theory]
    [InlineData("csv", "empty")]
    [InlineData("csv", "truncated")]
    [InlineData("csv", "missing_team")]
    [InlineData("csv", "duplicate_alias")]
    [InlineData("csv", "unknown_team")]
    [InlineData("csv", "championship_only")]
    [InlineData("csv", "wrong_scenario_header")]
    [InlineData("csv", "duplicate_header")]
    [InlineData("csv", "conditional_only")]
    [InlineData("csv", "negative")]
    [InlineData("csv", "too_large")]
    [InlineData("csv", "infinity")]
    [InlineData("csv", "nan")]
    [InlineData("csv", "empty_probability")]
    [InlineData("csv", "bad_id")]
    [InlineData("csv", "short_id")]
    [InlineData("csv", "missing_id")]
    [InlineData("page", "stale")]
    [InlineData("page", "ambiguous")]
    [InlineData("page", "navigation_only")]
    [InlineData("page", "scripts_only")]
    [InlineData("page", "non_consecutive")]
    [InlineData("page", "title_only")]
    [InlineData("marker", "no_offset")]
    [InlineData("marker", "time_only")]
    [InlineData("marker", "empty")]
    [InlineData("marker", "future")]
    [InlineData("game", "wrong_season")]
    [InlineData("game", "wrong_id")]
    [InlineData("game", "no_season")]
    [InlineData("game", "string_season")]
    [InlineData("game", "malformed")]
    [InlineData("game", "duplicate_season")]
    public async Task Successful_invalid_source_revokes_all_imported_eligibility(string target, string failure)
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        h.Clock.Now = h.Clock.Now.AddHours(24);
        h.Handler.Marker = h.Clock.Now.AddMinutes(-5).ToString("O");
        if (target == "csv") h.Handler.Csv = InvalidCsv(h.Handler.Csv, failure);
        if (target == "page") h.Handler.Page = failure switch
        {
            "stale" => h.Handler.Page.Replace("2026–2027", "2025–26"),
            "ambiguous" => h.Handler.Page.Replace("</main>", "<h2>2025-2026 NHL Playoff Predictions</h2></main>"),
            "navigation_only" => "<nav><h1>2026-2027 NHL Playoff Predictions</h1></nav>",
            "scripts_only" => "<script>2026-2027 NHL Playoff Predictions</script>",
            "non_consecutive" => h.Handler.Page.Replace("2026–2027", "2026–2026"),
            "title_only" => "<title>2026-2027 NHL Playoff Predictions</title><body>No forecast</body>",
            _ => throw new ArgumentException(failure)
        };
        if (target == "marker") h.Handler.Marker = failure switch
        {
            "no_offset" => "2026-10-11 17:07:34.627868",
            "time_only" => "17:07Z",
            "empty" => "",
            "future" => h.Clock.Now.AddMinutes(1).ToString("O"),
            _ => throw new ArgumentException(failure)
        };
        if (target == "game") h.Handler.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsoluteUri.EndsWith("/2099020003/landing")
            ? new(HttpStatusCode.OK) { Content = new StringContent(failure switch
            {
                "wrong_season" => "{\"id\":2099020003,\"season\":20252026}",
                "wrong_id" => "{\"id\":2026020004,\"season\":20262027}",
                "no_season" => "{\"id\":2099020003}",
                "string_season" => "{\"id\":2099020003,\"season\":\"20262027\"}",
                "malformed" => "{",
                "duplicate_season" => "{\"id\":2099020003,\"season\":20252026,\"season\":20262027}",
                _ => throw new ArgumentException(failure)
            }) } : null);
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        foreach (var team in MoneyPuckQualificationHarness.Teams) Assert.Null((await h.SelectAsync(team)).Probability);
        var rows = await h.RowsAsync();
        Assert.Equal(32, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Null(row.PlayoffProbability);
            var evidence = PlayoffModelEvidence.Parse(row.PlayoffEvidenceJson)!;
            Assert.Equal("unavailable", evidence.State);
            Assert.NotEqual("accepted", evidence.ReasonCode);
            Assert.Equal(0, evidence.AcceptedTeamCount);
        });
        Assert.Single(rows.Select(r => PlayoffModelEvidence.Parse(r.PlayoffEvidenceJson)!.RefreshId).Distinct());
        Assert.True(h.Handler.Requests.Count <= 18);
    }

    [Fact]
    public async Task Initial_future_marker_remains_diagnostic_only_and_allows_current_first_estimate()
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        h.Handler.Marker = h.Clock.Now.AddYears(5).ToString("O");
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.All(await h.RowsAsync(), row => Assert.Null(PlayoffModelEvidence.Parse(row.PlayoffEvidenceJson)!.PublicationBoundary));
        Assert.Null((await h.SelectAsync()).Probability);
        h.Clock.Now = h.Clock.Now.AddHours(24);
        h.Handler.Marker = h.Clock.Now.AddMinutes(-5).ToString("O");
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(.2518, (await h.SelectAsync()).Probability.Value);
    }

    [Fact]
    public async Task Rejected_future_publication_cannot_resurrect_when_time_catches_up_to_that_same_marker()
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        h.Handler.Marker = h.Clock.Now.AddHours(24).ToString("O");
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        h.Clock.Now = h.Clock.Now.AddHours(24);
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Null((await h.SelectAsync()).Probability);
        h.Clock.Now = h.Clock.Now.AddHours(24);
        h.Handler.Marker = h.Clock.Now.AddMinutes(-5).ToString("O");
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(.2518, (await h.SelectAsync()).Probability.Value);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("no-offset")]
    [InlineData("replacement-future")]
    public async Task Rejected_future_identity_survives_unverified_marker_until_same_publication_is_current(string replacement)
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        var prior = PlayoffModelEvidence.Parse((await h.RowsAsync())[0].PlayoffEvidenceJson)!.PublishedAt!.Value;
        h.Clock.Now = h.Clock.Now.AddHours(24);
        var future = h.Clock.Now.AddHours(72);
        h.Handler.Marker = future.ToString("O");
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        h.Clock.Now = h.Clock.Now.AddHours(24);
        h.Handler.Marker = replacement switch
        {
            "missing" => "",
            "no-offset" => "2026-10-12 05:00:00",
            "replacement-future" => future.AddHours(24).ToString("O"),
            _ => throw new ArgumentException(replacement)
        };
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.All(await h.RowsAsync(), row =>
        {
            var e = PlayoffModelEvidence.Parse(row.PlayoffEvidenceJson)!;
            Assert.Equal(replacement == "replacement-future" ? future.AddHours(24).UtcDateTime : (DateTime?)null, e.PublishedAt);
            Assert.Equal(prior, e.PublicationBoundary);
            Assert.Equal(replacement == "replacement-future" ? "future_publication" : "invalid_publication", e.ReasonCode);
            Assert.Contains(future.UtcDateTime, e.MoneyPuckRejectedPublications);
        });
        h.Clock.Now = future.AddMinutes(5);
        h.Handler.Marker = future.ToString("O");
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        foreach (var team in MoneyPuckQualificationHarness.Teams) Assert.Null((await h.SelectAsync(team)).Probability);
        h.Clock.Now = h.Clock.Now.AddHours(24).AddMinutes(1);
        h.Handler.Marker = h.Clock.Now.AddMinutes(-5).ToString("O");
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(.2518, (await h.SelectAsync()).Probability.Value);
    }

    [Fact]
    public async Task Distinct_rejected_future_identities_survive_current_recovery_and_are_pruned_only_when_boundary_excludes_them()
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        var first = h.Clock.Now.AddHours(144);
        var second = h.Clock.Now.AddHours(120);
        foreach (var rejected in new[] { first, second })
        {
            h.Clock.Now = h.Clock.Now.AddHours(24);
            h.Handler.Marker = rejected.ToString("O");
            Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        }
        h.Clock.Now = h.Clock.Now.AddHours(24);
        h.Handler.Marker = "";
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        h.Clock.Now = h.Clock.Now.AddHours(24);
        var current = h.Clock.Now.AddMinutes(-5);
        Assert.True(current < second && current < first);
        h.Handler.Marker = current.ToString("O");
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(.2518, (await h.SelectAsync()).Probability.Value);
        Assert.All(await h.RowsAsync(), row =>
        {
            var e = PlayoffModelEvidence.Parse(row.PlayoffEvidenceJson)!;
            Assert.Equal(current.UtcDateTime, e.PublicationBoundary);
            Assert.Equal(2, e.MoneyPuckRejectedPublications.Length);
        });
        foreach (var rejected in new[] { second, first })
        {
            h.Clock.Now = rejected.AddMinutes(5);
            h.Handler.Marker = rejected.ToString("O");
            Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
            foreach (var team in MoneyPuckQualificationHarness.Teams) Assert.Null((await h.SelectAsync(team)).Probability);
            Assert.All(await h.RowsAsync(), row => Assert.Equal(current.UtcDateTime, PlayoffModelEvidence.Parse(row.PlayoffEvidenceJson)!.PublicationBoundary));
        }
        h.Clock.Now = h.Clock.Now.AddHours(24);
        h.Handler.Marker = h.Clock.Now.AddMinutes(-5).ToString("O");
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(.2518, (await h.SelectAsync()).Probability.Value);
        Assert.All(await h.RowsAsync(), row => Assert.Empty(PlayoffModelEvidence.Parse(row.PlayoffEvidenceJson)!.MoneyPuckRejectedPublications));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("2026-10-12 05:00:00", false)]
    [InlineData("", true)]
    [InlineData("2026-10-12 05:00:00", true)]
    public async Task Unverified_clock_preserves_valid_boundary_without_resurrecting_a_rejected_publication(string marker, bool afterRejected)
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        var boundary = PlayoffModelEvidence.Parse((await h.RowsAsync())[0].PlayoffEvidenceJson)!.PublishedAt!.Value;
        if (afterRejected)
        {
            h.Clock.Now = h.Clock.Now.AddHours(24);
            boundary = h.Clock.Now.AddMinutes(-5).UtcDateTime;
            h.Handler.Marker = boundary.ToString("O");
            h.Handler.Csv = InvalidCsv(h.Handler.Csv, "nan");
            Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
            h.Handler.Csv = MoneyPuckQualificationHandler.Fixture("simulations.csv");
        }
        h.Clock.Now = h.Clock.Now.AddHours(24);
        h.Handler.Marker = marker;
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.All(await h.RowsAsync(), row =>
        {
            var e = PlayoffModelEvidence.Parse(row.PlayoffEvidenceJson)!;
            Assert.Equal("invalid_publication", e.ReasonCode);
            Assert.Null(e.PublishedAt);
            Assert.Equal(boundary, e.PublicationBoundary);
        });
        foreach (var team in MoneyPuckQualificationHarness.Teams) Assert.Null((await h.SelectAsync(team)).Probability);
        h.Clock.Now = h.Clock.Now.AddHours(24);
        h.Handler.Marker = boundary.ToString("O");
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Null((await h.SelectAsync()).Probability);
        h.Clock.Now = h.Clock.Now.AddHours(24);
        h.Handler.Marker = h.Clock.Now.AddMinutes(-5).ToString("O");
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(.2518, (await h.SelectAsync()).Probability.Value);
    }

    private static string InvalidCsv(string csv, string failure) => failure switch
    {
        "empty" => "",
        "truncated" => csv[..csv.IndexOf("WPG,ALL")] + "WPG,ALL,0.5,",
        "missing_team" => csv.Replace("NSH,ALL,0.2518,2099020003,0.01\n", ""),
        "duplicate_alias" => csv + "UTAH,ALL,0.5,2099020002,0.01\n",
        "unknown_team" => csv.Replace("NSH,ALL", "XXX,ALL"),
        "championship_only" => csv.Replace("madePlayoffs", "championshipProbability"),
        "wrong_scenario_header" => csv.Replace("scenerio", "scenario"),
        "duplicate_header" => csv.Replace("wonCup", "madePlayoffs"),
        "conditional_only" => csv.Replace(",ALL,", ",WIN,"),
        "negative" => csv.Replace("NSH,ALL,0.2518", "NSH,ALL,-0.1"),
        "too_large" => csv.Replace("NSH,ALL,0.2518", "NSH,ALL,1.01"),
        "infinity" => csv.Replace("NSH,ALL,0.2518", "NSH,ALL,Infinity"),
        "nan" => csv.Replace("NSH,ALL,0.2518", "NSH,ALL,NaN"),
        "empty_probability" => csv.Replace("NSH,ALL,0.2518", "NSH,ALL,"),
        "bad_id" => csv.Replace("NSH,ALL,0.2518,2099020003", "NSH,ALL,0.2518,2026x20003"),
        "short_id" => csv.Replace("NSH,ALL,0.2518,2099020003", "NSH,ALL,0.2518,2026"),
        "missing_id" => csv.Replace("firstSimulatedId", "simulatedId"),
        _ => throw new ArgumentException(failure)
    };

    [Fact]
    public async Task All_persisted_tick_remainders_keep_model_precision_and_zero_one_never_become_final()
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        h.Handler.Csv = h.Handler.Csv.Replace("UTA,ALL", "UTAH,ALL").Replace("ANA,ALL,0.5", "ANA,ALL,0").Replace("BOS,ALL,0.5", "BOS,ALL,1");
        for (var remainder = 0; remainder < 10; remainder++)
        {
            h.Clock.Now = new DateTimeOffset(h.Clock.Now.UtcDateTime.AddHours(24).AddTicks(-(h.Clock.Now.Ticks % 10) + remainder));
            h.Handler.Marker = h.Clock.Now.AddMinutes(-5).ToString("O");
            Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
            var row = (await h.RowsAsync()).Single(r => r.TeamAbbreviation == "NSH");
            Assert.Equal(h.Clock.Now.UtcDateTime.Ticks / 10 * 10, row.FetchedAt.Ticks);
            Assert.Equal(h.Clock.Now.UtcDateTime, PlayoffModelEvidence.Parse(row.PlayoffEvidenceJson)!.FetchedAt);
            Assert.Equal(.2518, (await h.SelectAsync()).Probability.Value);
            Assert.Equal(0, (await h.SelectAsync("ANA")).Probability.Value);
            Assert.Equal(1, (await h.SelectAsync("BOS")).Probability.Value);
            Assert.False((await h.SelectAsync("ANA")).IsFinal);
            Assert.False((await h.SelectAsync("BOS")).IsFinal);
        }
    }
}
