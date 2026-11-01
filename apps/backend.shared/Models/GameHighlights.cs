namespace backend.Models;

public enum HighlightsOwnership { Automatic, Manual }
public enum HighlightsAvailability { Unknown, Playable, Unavailable }

public sealed class GameHighlights
{
    public required string GameId { get; set; }
    public string? SelectedVideoId { get; set; }
    public HighlightsOwnership? Ownership { get; set; }
    public bool Suppressed { get; set; }
    public DateTime? SuppressedAt { get; set; }
    public DateTime? SelectedAt { get; set; }
    public HighlightsAvailability Availability { get; set; }
    public string? UnavailableReason { get; set; }
    public string? Title { get; set; }
    public string? ChannelId { get; set; }
    public DateTime? PublishedAt { get; set; }
    public bool? MadeForKids { get; set; }
    public string[]? AllowedRegions { get; set; }
    public string[]? BlockedRegions { get; set; }
    public string? SelectionEvidenceJson { get; set; }
    public DateTime? ValidatedAt { get; set; }
    public DateTime? ProviderDataRefreshedAt { get; set; }
    public DateTime? ProviderDataExpiresAt { get; set; }
    public uint Version { get; set; }
}
