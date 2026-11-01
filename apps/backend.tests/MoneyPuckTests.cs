using backend.Models;
using backend.Scoring;
using backend.Services;
using Xunit;

namespace backend.tests;

public sealed class MoneyPuckTests
{
    internal static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "MoneyPuck", name));
    internal static readonly DateTime Published = new(2026, 9, 27, 16, 0, 0, DateTimeKind.Utc);
    private static MoneyPuckSnapshot? Parse(string? csv = null, string? updated = null, DateTime? now = null) =>
        MoneyPuckRankings.Parse(csv ?? Fixture("rankings_current.csv"), updated ?? Fixture("update_time.txt"), now ?? ProHypeTests.Now);

    [Fact]
    public void Synthetic_public_table_uses_row_order_and_normalizes_all_NHL_aliases()
    {
        var snapshot = Parse()!;
        Assert.Equal(2027, snapshot.Season);
        Assert.Equal(Published, snapshot.PublishedAt);  
        Assert.Equal(32, snapshot.Teams.Count);
        foreach (var (team, rank) in new[] { ("TB", 5), ("NYR", 23), ("UTAH", 13), ("NJ", 16), ("LA", 20), ("SJ", 27), ("CHI", 29), ("SEA", 30) })
            Assert.Equal(rank, snapshot.Teams.Single(t => t.Team == team).Rank);
    }

    [Theory]
    [InlineData("Thursday January 1, 2026 at 12:49 PM Eastern Time", "2026-01-01T17:49:00Z", 2026)]
    [InlineData("Tuesday June 30, 2026 at 11:59 PM Eastern Time", "2026-07-01T03:59:00Z", 2026)]
    [InlineData("Wednesday July 1, 2026 at 12:01 AM Eastern Time", "2026-07-01T04:01:00Z", 2027)]
    [InlineData("Thursday December 31, 2026 at 11:59 PM Eastern Time", "2027-01-01T04:59:00Z", 2027)]
    public void Publication_season_and_timezone_handle_preseason_and_year_boundaries(string updated, string utc, int season)
    {
        var expected = DateTime.Parse(utc, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        var snapshot = Parse(updated: updated, now: expected)!;
        Assert.Equal(expected, snapshot.PublishedAt);
        Assert.Equal(season, snapshot.Season);
    }

    [Theory]
    [InlineData("")]
    [InlineData("2026-09-27")]
    [InlineData("Sunday September 27, 2026 at 12:49 PM")]
    [InlineData("Sunday March 8, 2026 at 2:30 AM Eastern Time")]
    [InlineData("Sunday November 1, 2026 at 1:30 AM Eastern Time")]
    public void Unknown_or_ambiguous_publication_is_not_replaced_with_fetch_time(string updated) => Assert.Null(Parse(updated: updated));

    [Fact]
    public void Publication_age_is_exclusive_and_future_updates_are_rejected()
    {
        Assert.NotNull(Parse(now: Published.AddDays(7).AddTicks(-1)));
        Assert.Null(Parse(now: Published.AddDays(7)));
        Assert.Null(Parse(now: Published.AddTicks(-1)));
    }

    [Fact]
    public void Partial_duplicate_unknown_or_malformed_rows_invalidate_the_snapshot()
    {
        var csv = Fixture("rankings_current.csv");
        Assert.Null(Parse(csv: string.Join('\n', csv.TrimEnd().Split('\n').SkipLast(1))));
        foreach (var broken in new[] {
            csv.Replace("COL,", "CAR,"), csv.Replace("UTA,", "ARI,"), csv.Replace("teamCode", "team"),
            csv.Replace("0.75000", "NaN"), csv.Replace("0.75000", "1.1"), csv.Replace("CAR,", "CAR,extra,"),
            csv.Replace("FixtureGoalie,FixtureBackup", "\"unterminated,FixtureBackup"), "<html>Access denied</html>"
        }) Assert.Null(Parse(csv: broken));
        Assert.NotNull(Parse(csv: csv.Replace("FixtureGoalie", "\"FixtureGoalie, P\"")));  
        Assert.NotNull(Parse(csv: csv.Replace("\n", "\r\n")));  
    }

    [Fact]
    public void NHL_quality_uses_only_MoneyPuck_and_preserves_independent_components()
    {
        var game = ProHypeTests.Game(); game.HomeTeamAbbreviation = "NYR"; game.AwayTeamAbbreviation = "TB";
        var oldSource = ProHypeTests.Ranks(game, 1, 2, "powerrankingsguru");
        Assert.Null(ProHypePolicy.Quality(game, oldSource, ProHypeTests.Now).Value);
        var ranks = ProHypeTests.Ranks(game, 23, 5);
        Assert.Equal(ProHypePolicy.Quality(game, ranks, ProHypeTests.Now).Value,
            ProHypePolicy.Quality(game, [.. ranks, .. oldSource], ProHypeTests.Now).Value);
        Assert.Equal(44, Math.Round(ProHypePolicy.Quality(game, ranks, ProHypeTests.Now).Value!.Value));
        Assert.Null(ProHypePolicy.Quality(game, [ranks[0], oldSource[1]], ProHypeTests.Now).Value);
        var e = ProHypeTests.Evidence(game); e.HomeProgress.SeedingUnresolved = null;
        e.HomeProgress.Completed = null;  
        var q = new ProQualification(.5, false, ProHypeTests.Now, ProHypeTests.Now.Add(PlayoffRatingPolicy.maxAge), "kalshi", ProQualificationEvidenceKind.Kalshi);
        var result = ProHypePolicy.Calculate(game, e, ranks, q, q, ProHypeTests.Odds(game), [], ProHypeTests.Now);
        Assert.NotNull(result.Quality.Value); Assert.Null(result.Stakes.Value); Assert.Null(result.Total);
    }

    [Fact]
    public void Fresh_fetch_cannot_renew_old_publication_or_relabel_previous_season()
    {
        var game = ProHypeTests.Game();
        var ranks = ProHypeTests.Ranks(game);
        foreach (var published in new DateTime?[] { null, ProHypeTests.Now.AddDays(-7), ProHypeTests.Now.AddTicks(1), new(2026, 6, 30, 12, 0, 0, DateTimeKind.Utc) })
        {
            ranks[0].RankPublishedAt = published;
            Assert.Null(ProHypePolicy.Quality(game, ranks, ProHypeTests.Now).Value);
        }
        ranks[0].RankPublishedAt = ProHypeTests.Now.AddDays(-7).AddTicks(1);
        Assert.NotNull(ProHypePolicy.Quality(game, ranks, ProHypeTests.Now).Value);
        ranks[0].FetchedAt = ProHypeTests.Now.AddHours(-48);
        Assert.Null(ProHypePolicy.Quality(game, ranks, ProHypeTests.Now).Value);
        ranks[0].FetchedAt = ProHypeTests.Now;
        ranks[0].SourceSeason = 2026;
        Assert.Null(ProHypePolicy.Quality(game, ranks, ProHypeTests.Now).Value);
        var valid = ProHypeTests.Ranks(game); var invalid = ProHypeTests.Ranks(game)[0];
        invalid.FetchedAt = ProHypeTests.Now.AddMinutes(1); invalid.RankPublishedAt = null;
        Assert.Null(ProHypePolicy.Quality(game, [.. valid, invalid], invalid.FetchedAt).Value);
    }
}
