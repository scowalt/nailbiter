using System.Net;
using System.Text.Json.Nodes;
using backend.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace backend.tests;

public sealed class GameWinnerFallbackTests
{
    private static readonly DateTime Now = new(2026, 10, 3, 18, 0, 0, DateTimeKind.Utc);
    private const string Event = "KXNCAAFGAME-26OCT03MTUKU";
    private static JsonObject Market(string team, bool winner, bool resolved = false) => new()
    {
        ["ticker"] = $"{Event}-{team}", ["event_ticker"] = Event,
        ["market_type"] = "binary", ["notional_value_dollars"] = "1.0000",
        ["status"] = resolved ? "determined" : "active",
        ["result"] = resolved ? (winner ? "yes" : "no") : "",
        ["settlement_value_dollars"] = resolved ? (winner ? "1.0000" : "0.0000") : null,
        ["close_time"] = resolved ? "2026-10-03T17:59:00Z" : "2026-10-05T17:00:00Z",
        ["yes_bid_dollars"] = winner ? "0.9900" : "0.0000",
        ["yes_ask_dollars"] = winner ? "1.0000" : "0.0100",
        ["yes_bid_size_fp"] = winner ? "184275.69" : "0.00",
        ["yes_ask_size_fp"] = winner ? "0.00" : "299983.28",
        ["last_price_dollars"] = winner ? "0.9900" : "0.0100"
    };

    private sealed class Handler(JsonObject home, JsonObject away) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new JsonObject
                {
                    ["market"] = (request.RequestUri!.AbsolutePath.EndsWith("-KU") ? home : away).DeepClone()
                }.ToJsonString())
            });
    }

    private static async Task<double?> Probability(JsonObject home, JsonObject away)
    {
        using var http = new HttpClient(new Handler(home, away));
        var client = new CollegeKalshiClient(http, NullLogger<CollegeKalshiClient>.Instance);
        return await client.ProbabilityAsync(new($"{Event}-KU", $"{Event}-MTU"), Now, default);
    }

    [Theory]
    [InlineData(false, true, .995)]
    [InlineData(false, false, .005)]
    [InlineData(true, true, .995)]
    [InlineData(true, false, .005)]
    public async Task Fresh_one_sided_and_confirmed_results_supply_bounds(bool resolved, bool homeWinner, double expected)
    {
        var result = await Probability(Market("KU", homeWinner, resolved), Market("MTU", !homeWinner, resolved));
        Assert.NotNull(result);
        Assert.Equal(expected, result.Value);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(119, true)]
    [InlineData(120, false)]
    [InlineData(-1, false)]
    public void Bounds_expire_on_every_sport_display_without_removing_chart_history(int ageSeconds, bool fresh)
    {
        var snapshot = new backend.Models.OddsSnapshot
        {
            GameId = "game", Timestamp = Now.AddSeconds(-ageSeconds),
            Source = GameWinnerProbabilityPolicy.OneSidedSource
        };
        Assert.Equal(fresh, GameWinnerProbabilityPolicy.FreshForDisplay(snapshot, Now));
    }

    [Fact]
    public async Task Fully_terminal_prices_with_supporting_depth_still_supply_bounds()
    {
        var home = Market("KU", true); var away = Market("MTU", false);
        home["yes_bid_dollars"] = "1.00"; away["yes_ask_dollars"] = "0.00";
        Assert.Equal(.995, await Probability(home, away));
    }

    [Fact]
    public async Task Absent_empty_sides_do_not_hide_positive_supporting_depth()
    {
        var home = Market("KU", true); var away = Market("MTU", false);
        home.Remove("yes_ask_dollars"); away.Remove("yes_bid_dollars");
        Assert.Equal(.995, await Probability(home, away));
        home["yes_ask_size_fp"] = "10";
        Assert.Null(await Probability(home, away));
    }

    [Theory]
    [InlineData("depth")]
    [InlineData("threshold")]
    [InlineData("expired")]
    [InlineData("inactive")]
    [InlineData("crossed")]
    [InlineData("identity")]
    [InlineData("scalar")]
    public async Task Missing_or_inadequate_evidence_does_not_become_a_bound(string fault)
    {
        var home = Market("KU", true); var away = Market("MTU", false);
        switch (fault)
        {
            case "depth": home["yes_bid_size_fp"] = "0"; break;
            case "threshold": home["yes_bid_dollars"] = "0.98"; break;
            case "expired": home["close_time"] = "2026-10-03T17:59:00Z"; break;
            case "inactive": home["status"] = "closed"; break;
            case "crossed": home["yes_ask_dollars"] = "0.98"; break;
            case "identity": away["ticker"] = "KXNCAAFGAME-26OCT04MTUKU-MTU"; break;
            case "scalar": home["market_type"] = "scalar"; break;
        }
        Assert.Null(await Probability(home, away));
    }

    [Theory]
    [InlineData("fair_price")]
    [InlineData("contradictory")]
    [InlineData("unconfirmed")]
    [InlineData("void")]
    public async Task Non_winner_resolutions_do_not_supply_bounds(string fault)
    {
        var home = Market("KU", true, true); var away = Market("MTU", false, true);
        switch (fault)
        {
            case "fair_price": home["settlement_value_dollars"] = "0.65"; break;
            case "contradictory": away["result"] = "yes"; break;
            case "unconfirmed": home["status"] = "active"; break;
            case "void": home["result"] = "void"; break;
        }
        Assert.Null(await Probability(home, away));
    }
}
