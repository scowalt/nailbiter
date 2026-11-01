namespace backend.Models;

public sealed class HighlightsOperationState
{
    public int Id { get; set; } = 1;
    public DateTime? LaunchBoundaryAt { get; set; }
    public DateTime? CatchupWindowStart { get; set; }
    public string? CatchupCursorGameId { get; set; }
    public string[]? CatchupRemainingGameIds { get; set; }
    public int CatchupPagesScanned { get; set; }
    public DateTime? CatchupCompletedAt { get; set; }
    public string? UploadsPlaylistId { get; set; }
    public string? UploadsPageToken { get; set; }
    public DateTime? ProviderDataRefreshedAt { get; set; }
    public DateTime? ProviderDataExpiresAt { get; set; }
    public DateTime? LastPollAttemptAt { get; set; }
    public DateTime? NextPollAt { get; set; }
    public DateTime? NextValidationAttemptAt { get; set; }
    public DateTime? BackoffUntil { get; set; }
    public DateOnly? QuotaDay { get; set; }
    public int QuotaUnitsUsed { get; set; }
    public int WorkerReservedUnits { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string? LastReason { get; set; }
    public bool ProviderConfigurationRejected { get; set; }
    public uint Version { get; set; }
}
