using System.Net;
using backend.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace backend.tests;

public sealed class MoneyPuckQualificationHttpTests
{
    private sealed class UnmeasuredContent(string body) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) => await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(body));
    }

    [Theory]
    [InlineData("marker", 4096, false)]
    [InlineData("page", 1048576, false)]
    [InlineData("csv", 2097152, false)]
    [InlineData("game", 524288, false)]
    [InlineData("csv", 2097152, true)]
    public async Task Oversized_successful_bodies_revoke_snapshot_and_only_finish_publication_bracket(string target, int limit, bool unmeasured)
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        h.Clock.Now = h.Clock.Now.AddHours(24);
        var targetUrl = target switch
        {
            "marker" => MoneyPuckQualificationHandler.MarkerUrl,
            "page" => MoneyPuckQualificationHandler.PageUrl,
            "csv" => MoneyPuckQualificationHandler.CsvUrl,
            "game" => "https://api-web.nhle.com/v1/gamecenter/2099020002/landing",
            _ => throw new ArgumentException(target)
        };
        var body = new string('x', limit + 1);
        h.Handler.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsoluteUri == targetUrl
            ? new(HttpStatusCode.OK) { Content = unmeasured ? new UnmeasuredContent(body) : new StringContent(body) } : null);
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.All(await h.RowsAsync(), row =>
        {
            Assert.Null(row.PlayoffProbability);
            Assert.Equal("response_too_large", PlayoffModelEvidence.Parse(row.PlayoffEvidenceJson)!.ReasonCode);
        });
        Assert.Null((await h.SelectAsync()).Probability);
        var attempted = h.Handler.Requests.Skip(9).Where(url => url != backend.Services.MoneyPuckRankings.UpdatedUrl && url != backend.Services.MoneyPuckRankings.RankingsUrl).ToArray();
        Assert.Equal(target == "marker" ? targetUrl : MoneyPuckQualificationHandler.MarkerUrl, attempted.Last());
        Assert.Equal(targetUrl, attempted[target == "marker" ? ^1 : ^2]);
        Assert.DoesNotContain(attempted.Skip(Array.IndexOf(attempted, targetUrl) + 1), url => url != MoneyPuckQualificationHandler.MarkerUrl);
    }

    [Fact]
    public async Task Challenge_in_oversized_body_is_still_a_denial_not_source_invalidation()
    {
        await using var h = new MoneyPuckQualificationHarness();
        await h.StartAsync();
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        var original = await h.RowsAsync();
        h.Clock.Now = h.Clock.Now.AddHours(24);
        h.Handler.Override = (_, _) => Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.OK) { Content = new StringContent("Access denied CAPTCHA" + new string('x', 4096)) });
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(original.Select(r => r.PlayoffEvidenceJson), (await h.RowsAsync()).Select(r => r.PlayoffEvidenceJson));
        h.Clock.Now = h.Clock.Now.AddHours(24).AddTicks(-1);
        h.Handler.Override = null;
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(10, h.Handler.Requests.Count);
        h.Clock.Now = h.Clock.Now.AddTicks(1);
        h.Handler.Marker = h.Clock.Now.AddMinutes(-5).ToString("O");
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(19, h.Handler.Requests.Count);
    }

    [Fact]
    public async Task Per_request_timeout_stops_collection_without_renewing_prior_evidence()
    {
        await using var h = new MoneyPuckQualificationHarness();
        var time = new FakeTimeProvider(h.Clock.Now);
        await h.StartAsync(configureServices: services => { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(time); });
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        var original = await h.RowsAsync();
        time.Advance(TimeSpan.FromHours(24));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Handler.Override = async (_, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return null; };
        var refresh = h.Service.RefreshNhlRatingsAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(0, await refresh.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(10, h.Handler.Requests.Count);
        Assert.Equal(original.Select(r => r.PlayoffEvidenceJson), (await h.RowsAsync()).Select(r => r.PlayoffEvidenceJson));
    }

    [Fact]
    public async Task Every_distinct_ID_is_verified_with_a_maximum_of_39_cycle_requests_and_whole_attempt_deadline()
    {
        await using var h = new MoneyPuckQualificationHarness();
        var time = new FakeTimeProvider(h.Clock.Now);
        await h.StartAsync(configureServices: services => { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(time); });
        h.Handler.Csv = "teamCode,scenerio,madePlayoffs,firstSimulatedId\n" + string.Join("\n", MoneyPuckQualificationHarness.Teams.Select((team, i) => $"{team},ALL,0.5,{2026021000L + i}"));
        var advance = false;
        h.Handler.Override = (request, ct) =>
        {
            if (advance) time.Advance(TimeSpan.FromSeconds(10));
            ct.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            return Task.FromResult<HttpResponseMessage?>(uri.Host == "api-web.nhle.com"
                ? new(HttpStatusCode.OK) { Content = new StringContent($"{{\"id\":{uri.Segments[3].TrimEnd('/')},\"season\":20262027}}") } : null);
        };
        Assert.Equal(32, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(39, h.Handler.Requests.Count);
        var original = await h.RowsAsync();
        Assert.All(original, row => Assert.Equal(32, PlayoffModelEvidence.Parse(row.PlayoffEvidenceJson)!.MoneyPuckSeasonProof!.VerifiedGames.Length));
        time.Advance(TimeSpan.FromHours(24));
        advance = true;
        Assert.Equal(0, await h.Service.RefreshNhlRatingsAsync(default));
        Assert.Equal(48, h.Handler.Requests.Count);
        Assert.Equal(original.Select(r => r.PlayoffEvidenceJson), (await h.RowsAsync()).Select(r => r.PlayoffEvidenceJson));
    }
}
