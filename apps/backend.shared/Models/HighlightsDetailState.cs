namespace backend.Models;

public enum HighlightsDetailStatus { Hidden, Pending, Unavailable, Playable }

public sealed record HighlightsDetailState(HighlightsDetailStatus Status, string? VideoId = null,
    string? Title = null, IReadOnlyList<string>? AllowedRegions = null, IReadOnlyList<string>? BlockedRegions = null);
