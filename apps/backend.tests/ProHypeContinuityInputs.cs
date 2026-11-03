using backend.Models;

namespace backend.tests;

internal static class ProHypeContinuityInputs
{
    public static ProGameEvidence Evidence(Game game)
    {
        var evidence = ProHypeTests.Evidence(game);
        RefreshLeverage(game, evidence);
        return evidence;
    }

    public static void RefreshLeverage(Game game, ProGameEvidence evidence, double swing = .01)
    {
        if (game.Sport == "nfl" && evidence.Phase == 2)
            evidence.NflPlayoffLeverage = NflPlayoffLeverageTests.SyntheticEvidence(game, evidence.ObservedAt, evidence.CompletedGameIds, swing);
    }
}
