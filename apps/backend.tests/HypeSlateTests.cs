using backend.Models;
using backend.Scoring;
using backend.Services;
using Backend.Rendering;
using Xunit;

namespace backend.tests;

public sealed class HypeSlateTests
{
    private sealed record Matchup(string Name, double Hype, FormatHelpers.HypeLevel Level);
    private sealed record Slate(string Name, Matchup[][] RankedGroups);

    private static readonly FormatHelpers.HypeLevel Good = FormatHelpers.HypeLevel.HypeMedium;
    private static readonly FormatHelpers.HypeLevel More = FormatHelpers.HypeLevel.HypeLow;

    private static double Closeness(double homeWinProbability) => 100 * (1 - Math.Abs(2 * homeWinProbability - 1));

    private static double College(DateTime kickoffUtc, double homeFpi, double awayFpi, double homeCfp, double awayCfp, double homeWin)
    {
        var game = new Game
        {
            Id = "slate", Sport = "cfb", HomeTeam = "HOME", AwayTeam = "AWAY", HomeTeamAbbreviation = "HOME", AwayTeamAbbreviation = "AWAY",
            CollegeCompetition = "regular", SeasonYear = 2026, ScheduledStart = kickoffUtc
        };
        return CollegeFootballPolicy.Hype(CollegeFootballPolicy.Stakes(game, homeCfp, awayCfp), CollegeFootballPolicy.Quality(homeFpi, awayFpi),
            Closeness(homeWin), CollegeFootballPolicy.CfpRelevance(game, homeCfp, awayCfp))!.Value;
    }

    private static double Pro(string sport, int completed, int homeRank, int awayRank, double homePlayoff, double awayPlayoff, double homeWin)
    {
        var game = ProHypeTests.Game(sport);
        var evidence = ProHypeTests.Evidence(game);
        evidence.HomeProgress.Completed = evidence.AwayProgress.Completed = completed;
        ProQualification Qualification(double p) => new(p, false, ProHypeTests.Now, ProHypeTests.Now.Add(PlayoffRatingPolicy.maxAge),
            "kalshi", ProQualificationEvidenceKind.Kalshi);
        return ProHypePolicy.Calculate(game, evidence, ProHypeTests.Ranks(game, homeRank, awayRank), Qualification(homePlayoff),
            Qualification(awayPlayoff), ProHypeTests.Odds(game, homeWin), [], ProHypeTests.Now).Total!.Value;
    }

    private static readonly Slate MidNovemberSaturday = new("2026-11-14 Saturday: college playoff race against early-season pro games",
    [
        [
            new("CFB primetime, #6 at #21, both in the CFP race", College(new(2026, 11, 15, 0, 30, 0, DateTimeKind.Utc), 22, 26, .3, .7, .32), Good)
        ],
        [
            new("CFB noon, two-sided CFP race with a fringe contender", College(new(2026, 11, 14, 17, 0, 0, DateTimeKind.Utc), 13, 16, .1, .45, .45), Good),
            new("CFB afternoon, coin-flip CFP team at an eliminated host", College(new(2026, 11, 14, 20, 0, 0, DateTimeKind.Utc), 19, 26, .005, .5, .35), Good)
        ],
        [
            new("NHL November, #2 against #7, both safely in", Pro("nhl", 16, 2, 7, .95, .72, .56), Good),
            new("NHL November, #9 against #5, both safely in", Pro("nhl", 16, 9, 5, .9, .8, .52), More),
            new("NHL November, #5 against #11", Pro("nhl", 16, 5, 11, .85, .75, .58), More),
            new("NBA November, #16 against #7", Pro("nba", 12, 16, 7, .4, .75, .45), More),
            new("NBA November, #9 against #14", Pro("nba", 12, 9, 14, .95, .6, .62), More)
        ]
    ]);

    private static readonly Slate[] Reviewed = [MidNovemberSaturday];

    public static TheoryData<string> Slates() => new(Reviewed.Select(s => s.Name));

    private static Slate Find(string name) => Reviewed.Single(s => s.Name == name);

    [Theory]
    [MemberData(nameof(Slates))]
    public void Every_game_lands_in_its_reviewed_home_page_section(string slate)
    {
        foreach (var game in Find(slate).RankedGroups.SelectMany(g => g))
            Assert.True(FormatHelpers.getHypeLevel(game.Hype).Equals(game.Level), $"{game.Name}: hype {game.Hype:F1} is not {game.Level}");
    }

    [Theory]
    [MemberData(nameof(Slates))]
    public void Every_game_outranks_each_game_in_a_later_reviewed_group(string slate)
    {
        var groups = Find(slate).RankedGroups;
        for (var i = 0; i < groups.Length; i++)
            foreach (var higher in groups[i])
                foreach (var lower in groups.Skip(i + 1).SelectMany(g => g))
                    Assert.True(higher.Hype > lower.Hype, $"{higher.Name} ({higher.Hype:F1}) should outrank {lower.Name} ({lower.Hype:F1})");
    }
}
