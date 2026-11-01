using Xunit;

namespace backend.tests;

internal static class ProHypeOutputAssertions
{
    public static void NoDiagnostics(string output, params string[] additional)
    {
        foreach (var forbidden in new[] { "retained", "retention", "stale", "evaluatedAt", "snapshotJson", "revokedEvidence",
            "evidenceRevision", "supportingEvidence", "stakesExplanation", "qualityExplanation", "closenessExplanation",
            "proEvidenceJson", "fixture: verified schedule", "moneypuck-nhl-qualification-model-v1" }.Concat(additional))
            Assert.DoesNotContain(forbidden, output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ProHypeTests.Now.ToString("O"), output);
    }
}
