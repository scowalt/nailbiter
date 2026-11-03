namespace backend.Models;

public sealed class ProHypeSnapshot
{
    public string GameId { get; set; } = "";
    public long LastEvaluationTicks { get; set; }
    public long EvidenceRevision { get; set; }
    public bool ConfirmedStarted { get; set; }
    public string? SnapshotJson { get; set; }
    public string RevokedEvidenceJson { get; set; } = "[]";
}
