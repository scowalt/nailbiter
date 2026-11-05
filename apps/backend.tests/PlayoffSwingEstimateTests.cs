using backend.Scoring;
using Xunit;

namespace backend.tests;

public sealed class PlayoffSwingEstimateTests
{
    [Theory]
    [InlineData(.5, 1, .5699175434306182)]
    [InlineData(.01, 1, .03807448886208289)]
    [InlineData(.25, 1, .45396653240586715)]
    [InlineData(.75, 1, .45396653240586715)]
    [InlineData(.9, 1, .2507119027606955)]
    [InlineData(.5, 4, .3374692121033196)]
    [InlineData(.5, 20, .15973672883774356)]
    [InlineData(.5, 40, .11380944366928848)]
    [InlineData(.365, 84, .07429276595317309)]
    [InlineData(1e-12, 1, 1.024486067673479e-11)]
    public void Swing_is_the_normal_density_at_the_probability_over_the_remaining_margin_spread(double probability, int remaining, double expected) =>
        Assert.Equal(expected, PlayoffSwingEstimate.Swing(probability, remaining), 14);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Certain_model_probabilities_have_no_swing(double probability) =>
        Assert.Equal(0, PlayoffSwingEstimate.Swing(probability, 1));

    [Theory]
    [InlineData(double.NaN, 1)]
    [InlineData(-.01, 1)]
    [InlineData(1.01, 1)]
    [InlineData(.5, 0)]
    public void Invalid_inputs_are_rejected_rather_than_scored(double probability, int remaining) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PlayoffSwingEstimate.Swing(probability, remaining));

    [Fact]
    public void Swing_is_symmetric_and_largest_for_a_coin_flip_team()
    {
        for (var p = .01; p < 1; p += .01)
        {
            Assert.Equal(PlayoffSwingEstimate.Swing(p, 10), PlayoffSwingEstimate.Swing(1 - p, 10), 13);
            Assert.True(PlayoffSwingEstimate.Swing(p, 10) <= PlayoffSwingEstimate.Swing(.5, 10));
        }
    }

    [Theory]
    [InlineData(.25)]
    [InlineData(.5)]
    [InlineData(.75)]
    [InlineData(.9)]
    public void A_game_matters_more_with_fewer_games_left_at_the_same_point_in_the_season(double progress)
    {
        int Remaining(int length) => Math.Max(1, length - (int)Math.Round(progress * length));
        var nfl = PlayoffSwingEstimate.Swing(.5, Remaining(17));
        var nba = PlayoffSwingEstimate.Swing(.5, Remaining(82));
        var mlb = PlayoffSwingEstimate.Swing(.5, Remaining(162));
        Assert.True(nfl > nba && nba > mlb, $"{nfl} / {nba} / {mlb}");
    }

    [Fact]
    public void Inverse_normal_matches_reference_quantiles_across_all_three_regions()
    {
        Assert.Equal(0, PlayoffSwingEstimate.WichuraAs241InverseStandardNormal(.5));
        Assert.Equal(1.9599639845400532, PlayoffSwingEstimate.WichuraAs241InverseStandardNormal(.975), 14);
        Assert.Equal(-2.3263478740408416, PlayoffSwingEstimate.WichuraAs241InverseStandardNormal(.01), 14);
        Assert.Equal(-7.034483825301132, PlayoffSwingEstimate.WichuraAs241InverseStandardNormal(1e-12), 12);
        Assert.Equal(-37.47933256432158, PlayoffSwingEstimate.WichuraAs241InverseStandardNormal(1e-307), 9);
    }
}
