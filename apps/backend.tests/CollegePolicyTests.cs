using System.Text.Json;
using backend.Models;
using backend.Services;
using Backend.Rendering;
using Xunit;

namespace backend.tests;

public sealed class CollegePolicyTests
{
    private static JsonElement Market(string ticker, string name = "") => JsonSerializer.SerializeToElement(new
    {
        ticker,
        event_ticker = ticker[..ticker.LastIndexOf('-')],
        yes_sub_title = name,
        status = "finalized",
        result = "yes"
    });

    [Theory]
    [InlineData("CCU", "CCAR")]
    [InlineData("IU", "IND")]
    [InlineData("TA&M", "TXAM")]
    [InlineData("OU", "OKLA")]
    [InlineData("BOIS", "BSU")]
    [InlineData("UTAH", "UTAH")]
    public void College_aliases_are_isolated_from_pro_aliases(string espn, string kalshi) => Assert.Equal(kalshi, CollegeFootballPolicy.KalshiCode(espn));

    [Fact]
    public void Long_outcomes_neutral_order_and_name_matching_do_not_require_short_code_splitting()
    {
        var date = new DateTime(2026, 9, 26, 18, 0, 0, DateTimeKind.Utc);
        var markets = new[] { Market("KXNCAAFGAME-26SEP26RICEFRES-FRES"), Market("KXNCAAFGAME-26SEP26RICEFRES-RICE") };
        var home = CollegeKalshiClient.Match(markets, date, "FRES", "RICE", [], []);
        var reversed = CollegeKalshiClient.Match(markets, date, "RICE", "FRES", [], []);
        Assert.NotNull(home); Assert.NotNull(reversed); Assert.Equal(home.HomeTicker, reversed.AwayTicker);
        Assert.Null(CollegeKalshiClient.Match(markets, date.AddDays(1), "FRES", "RICE", [], []));
        var named = new[] { Market("KXNCAAFGAME-26SEP26LONGCODE-UNKNOWN", "Fresno State"), Market("KXNCAAFGAME-26SEP26LONGCODE-RICE", "Rice") };
        Assert.NotNull(CollegeKalshiClient.Match(named, date, "FRES", "RICE", ["Fresno State"], ["Rice"]));
        Assert.Null(CollegeKalshiClient.Match(named, date, "FRES", "RICE", ["Fresno"], ["Rice"]));
        Assert.Null(CollegeKalshiClient.Match(markets.Concat(named), date, "FRES", "RICE", ["Fresno State"], ["Rice"]));
    }

    [Fact]
    public void A_single_exact_game_contract_is_a_listing_even_without_prices_or_other_outcome()
    {
        var match = CollegeKalshiClient.Match([Market("KXNCAAFGAME-26SEP26RICEFRES-FRES")],
            new DateTime(2026, 9, 26, 18, 0, 0, DateTimeKind.Utc), "FRES", "RICE", [], []);
        Assert.NotNull(match); Assert.EndsWith("-RICE", match.AwayTicker);
    }

    [Theory]
    [InlineData("0.40", "0.42", "5", "5", "active", true)]
    [InlineData("0", "1", "0", "0", "active", false)]
    [InlineData("0.10", "0.90", "5", "5", "active", false)]
    [InlineData("0.70", "0.60", "5", "5", "active", false)]
    [InlineData("0.40", "0.42", "0", "5", "active", false)]
    [InlineData("0.40", "0.42", "5", "5", "finalized", false)]
    public void Only_usable_current_orderbooks_produce_live_probabilities(string bid, string ask, string bidSize, string askSize, string status, bool valid)
    {
        var market = JsonSerializer.SerializeToElement(new
        {
            status,
            result = "",
            close_time = "2026-12-31T00:00:00Z",
            yes_bid_dollars = bid,
            yes_ask_dollars = ask,
            yes_bid_size_fp = bidSize,
            yes_ask_size_fp = askSize,
            last_price_dollars = "0.60"
        });
        Assert.Equal(valid, CollegeKalshiClient.Quote(market, new DateTime(2026, 9, 16)).HasValue);
        Assert.Null(CollegeKalshiClient.Quote(market, new DateTime(2027, 1, 1)));
    }

    [Theory]
    [InlineData(12, "0:00", "8OT")]
    [InlineData(5, "", "OT")]
    [InlineData(4, "2:03", "Q4 2:03")]
    public void College_clock_is_not_NFL_overtime_clock(int period, string clock, string expected) => Assert.Equal(expected, FormatHelpers.formatGameClock(period, clock, "cfb"));

    [Fact]
    public void Name_or_postseason_type_alone_never_establishes_college_stakes()
    {
        Assert.Equal("unknown", CollegeFootballPolicy.Classify(3, ""));
        Assert.Equal("unknown", CollegeFootballPolicy.Classify(3, "Rose Bowl", "42"));
        Assert.Equal("unknown", CollegeFootballPolicy.Classify(3, "Gasparilla Bowl"));
        Assert.Equal("bowl", CollegeFootballPolicy.Classify(3, "Gasparilla Bowl", "42"));
        Assert.Equal("regular", CollegeFootballPolicy.Classify(2, "CFP race implications"));
        Assert.Equal("cfp", CollegeFootballPolicy.Classify(3, "College Football Playoff Semifinal at the Orange Bowl", "42"));
    }

    [Fact]
    public void Missing_required_stakes_is_not_neutral_and_contextual_stakes_do_not_require_probabilities()
    {
        var game = new Game
        {
            Id = "test",
            Sport = "cfb",
            HomeTeam = "ORE",
            AwayTeam = "BOIS",
            HomeTeamAbbreviation = "ORE",
            AwayTeamAbbreviation = "BOIS",
            CollegeCompetition = "regular",
            SeasonYear = 2026,
            ScheduledStart = new DateTime(2026, 11, 21)
        };
        Assert.Null(CollegeFootballPolicy.Stakes(game, .5, null));
        game.CollegeCompetition = "cfp"; Assert.Equal(100, CollegeFootballPolicy.Stakes(game, null, null));
        game.CollegeCompetition = "conference_championship"; Assert.Equal(80, CollegeFootballPolicy.Stakes(game, null, null));
        game.CollegeCompetition = "bowl"; Assert.Equal(0, CollegeFootballPolicy.Stakes(game, null, null));
    }
}
