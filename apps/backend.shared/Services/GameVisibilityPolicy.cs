using System.Linq.Expressions;
using backend.GraphQL.Types;
using backend.Models;

namespace backend.Services;

public static class GameVisibilityPolicy
{
    public static Expression<Func<Game, bool>> Visible { get; } = game =>
        game.SeasonType != (int)SeasonType.PRESEASON && !game.Sport.StartsWith("nba-summer-");

    public const string PublicSqlPredicate = "(season_type IS NULL OR season_type <> 1) AND sport NOT LIKE 'nba-summer-%'";

    public static bool IsExcluded(string? sport, int? seasonType) =>
        seasonType == (int)SeasonType.PRESEASON || IsSummerLeague(sport);

    public static int? ClassifySeasonType(int? providerType, string? seasonSlug, string? leagueSlug)
    {
        if (IsSummerLeague(leagueSlug)
            || string.Equals(seasonSlug, "preseason", StringComparison.OrdinalIgnoreCase)
            || string.Equals(seasonSlug, "spring-training", StringComparison.OrdinalIgnoreCase))
            return (int)SeasonType.PRESEASON;
        return providerType is > 0 ? providerType : null;
    }

    private static bool IsSummerLeague(string? slug) => slug?.StartsWith("nba-summer-", StringComparison.Ordinal) == true;
}
