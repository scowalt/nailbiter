using AngleSharp.Dom;
using backend.Models;
using Xunit;

namespace backend.tests;

internal static class ExcitementWiringSupport
{
    public static IEnumerable<OddsSnapshot> History(DateTimeOffset now, string id, IEnumerable<double> probabilities) =>
        probabilities.Select((probability, index) => new OddsSnapshot
        {
            GameId = id, Timestamp = now.UtcDateTime.AddMinutes(-120).AddSeconds(index),
            HomeWinProbability = probability, AwayWinProbability = 1 - probability,
            Source = "Kalshi", HomeScore = 14, AwayScore = 10, Period = 2, GameClock = "5:00"
        });

    public static IElement AssertScore(IDocument page, string id, string value, string band)
    {
        var region = page.QuerySelector("#" + id);
        Assert.NotNull(region);
        var number = region.QuerySelector(".text-excitement-" + band);
        Assert.True(number is not null, $"Expected {value}/{band} in {id}, got {region.OuterHtml}");
        Assert.Equal(value, number.TextContent);
        return region;
    }
}
