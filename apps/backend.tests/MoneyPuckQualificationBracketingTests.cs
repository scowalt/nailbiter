using System.Net;
using backend.Models;
using backend.Services;
using Xunit;

namespace backend.tests;

public sealed class MoneyPuckQualificationBracketingTests
{
    [Theory]
    [InlineData("page", true, true)]
    [InlineData("csv", true, true)]
    [InlineData("game", true, true)]
    [InlineData("page", true, false)]
    [InlineData("csv", true, false)]
    [InlineData("game", true, false)]
    [InlineData("csv", false, true)]
    [InlineData("game", false, true)]
    [InlineData("csv", false, false)]
    [InlineData("game", false, false)]
    public async Task Invalid_success_finishes_only_publication_bracket_and_race_wins_over_revocation(string target, bool invalidEncoding, bool race)
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        var original = await h.RowsAsync();
        h.Clock.Now = h.Clock.Now.AddHours(24);
        var targetUrl = target switch
        {
            "page" => MoneyPuckQualificationHandler.PageUrl,
            "csv" => MoneyPuckQualificationHandler.CsvUrl,
            _ => "https://api-web.nhle.com/v1/gamecenter/2099020002/landing"
        };
        var markers = 0;
        h.Handler.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsoluteUri switch
        {
            var url when url == targetUrl => new(HttpStatusCode.OK)
            {
                Content = invalidEncoding ? new ByteArrayContent([0xc3, 0x28]) : new StringContent(new string('x', target == "csv" ? 2097153 : 524289))
            },
            MoneyPuckQualificationHandler.MarkerUrl when ++markers == 2 && race => new(HttpStatusCode.OK) { Content = new StringContent(h.Clock.Now.AddMinutes(-1).ToString("O")) },
            _ => null
        });
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        var selection = await h.SelectAsync();
        var latest = await h.RowsAsync();
        if (race)
        {
            Assert.NotNull(selection.Probability);
            Assert.Equal(.2518, selection.Probability.Value);
            Assert.Equal(original.Select(r => r.PlayoffEvidenceJson), latest.Select(r => r.PlayoffEvidenceJson));
            Assert.Equal(original.Select(r => r.FetchedAt), latest.Select(r => r.FetchedAt));
            Assert.Equal(original.Select(r => r.PlayoffProbability), latest.Select(r => r.PlayoffProbability));
        }
        else
        {
            Assert.Null(selection.Probability);
            Assert.All(latest, row =>
            {
                Assert.Null(row.PlayoffProbability);
                Assert.Equal(invalidEncoding ? "invalid_encoding" : "response_too_large", PlayoffModelEvidence.Parse(row.PlayoffEvidenceJson)!.ReasonCode);
            });
        }
        Assert.Equal(2, markers);
        var attempted = h.Handler.Requests.Skip(9).Where(url => url != MoneyPuckRankings.UpdatedUrl && url != MoneyPuckRankings.RankingsUrl).ToArray();
        Assert.Equal(targetUrl, attempted[^2]);
        Assert.Equal(MoneyPuckQualificationHandler.MarkerUrl, attempted[^1]);
        Assert.DoesNotContain(attempted.Skip(Array.IndexOf(attempted, targetUrl) + 1), url => url != MoneyPuckQualificationHandler.MarkerUrl);
    }

    [Theory]
    [InlineData(503)]
    [InlineData(403)]
    public async Task Failed_after_marker_cannot_turn_invalid_intermediate_body_into_revocation(int status)
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        var original = await h.RowsAsync();
        h.Clock.Now = h.Clock.Now.AddHours(24);
        var markers = 0;
        h.Handler.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsoluteUri switch
        {
            MoneyPuckQualificationHandler.PageUrl => new(HttpStatusCode.OK) { Content = new ByteArrayContent([0xc3, 0x28]) },
            MoneyPuckQualificationHandler.MarkerUrl when ++markers == 2 => new((HttpStatusCode)status),
            _ => null
        });
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        var selection = await h.SelectAsync();
        Assert.NotNull(selection.Probability); Assert.Equal(.2518, selection.Probability.Value);
        var retained = await h.RowsAsync();
        Assert.Equal(original.Select(r => r.PlayoffEvidenceJson), retained.Select(r => r.PlayoffEvidenceJson));
        Assert.Equal(original.Select(r => r.FetchedAt), retained.Select(r => r.FetchedAt));
        Assert.Equal(12, h.Handler.Requests.Count);
        if (status == 403)
        {
            h.Handler.Override = null;
            h.Clock.Now = h.Clock.Now.AddHours(24);
            Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
            Assert.Equal(12, h.Handler.Requests.Count);
        }
    }

    [Fact]
    public async Task Publication_race_around_oversized_success_retains_selected_probability_and_all_clocks()
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        var original = await h.RowsAsync();
        h.Clock.Now = h.Clock.Now.AddHours(24);
        var markers = 0;
        h.Handler.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsoluteUri switch
        {
            MoneyPuckQualificationHandler.PageUrl => new(HttpStatusCode.OK) { Content = new StringContent(new string('x', 1048577)) },
            MoneyPuckQualificationHandler.MarkerUrl when ++markers == 2 => new(HttpStatusCode.OK) { Content = new StringContent(h.Clock.Now.AddMinutes(-1).ToString("O")) },
            _ => null
        });
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        var selected = await h.SelectAsync();
        Assert.NotNull(selected.Probability);
        Assert.Equal(.2518, selected.Probability.Value);
        var retained = await h.RowsAsync();
        Assert.Equal(original.Select(r => r.PlayoffEvidenceJson), retained.Select(r => r.PlayoffEvidenceJson));
        Assert.Equal(original.Select(r => r.FetchedAt), retained.Select(r => r.FetchedAt));
        Assert.Equal(original.Select(r => r.PlayoffProbability), retained.Select(r => r.PlayoffProbability));
        Assert.Equal(2, markers);
        Assert.Equal(new[] { MoneyPuckQualificationHandler.MarkerUrl, MoneyPuckQualificationHandler.PageUrl, MoneyPuckQualificationHandler.MarkerUrl }, h.Handler.Requests.Skip(9).Where(url => url != MoneyPuckRankings.UpdatedUrl && url != MoneyPuckRankings.RankingsUrl));
    }
}
