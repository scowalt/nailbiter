namespace backend.Services.Highlights;

public sealed class YouTubeHighlightsOptions
{
    public bool Enabled { get; set; }
    public string? ApiKey { get; set; }
    public int DailyUnitLimit { get; set; } = 1000;
    public int MaxUploadPages { get; set; } = 2;

    public YouTubeOutcome? ValidateConfiguration()
    {
        if (!Enabled) return YouTubeOutcome.Disabled;
        if (string.IsNullOrWhiteSpace(ApiKey)) return YouTubeOutcome.MissingApiKey;
        if (ApiKey.Length > 512 || ApiKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
            || DailyUnitLimit is < 0 or > 1000 || MaxUploadPages is < 1 or > 4) return YouTubeOutcome.InvalidConfiguration;
        return null;
    }
}

public enum YouTubeOutcome
{
    Success, Disabled, MissingApiKey, InvalidConfiguration, InvalidRequest, ChannelUnverified,
    InvalidResponse, TemporaryFailure, Throttled, QuotaExhausted, BudgetExhausted, Backoff
}

internal readonly record struct YouTubeRequestAuthorization(DateOnly PacificDay, bool Permitted);

public enum YouTubeAvailability
{
    Playable, Removed, Private, NotEmbeddable, GloballyBlocked, UnapprovedPublisher,
    NotProcessed, MadeForKids, UnknownMadeForKids
}

public sealed record YouTubeVideoEvidence(string VideoId, YouTubeAvailability Availability,
    string? ChannelId, string? Title, string? Description, DateTimeOffset? PublishedAtUtc,
    TimeSpan? Duration, bool? MadeForKids, IReadOnlyList<string> AllowedRegions,
    IReadOnlyList<string> BlockedRegions, DateTimeOffset ValidatedAtUtc);

public sealed record YouTubeHighlightsResult(YouTubeOutcome Outcome,
    IReadOnlyList<YouTubeVideoEvidence> Videos, string? NextPageToken, int UnitsConsumed,
    DateTimeOffset? RetryAtUtc)
{
    public bool ProviderConfigurationRejectedAfter(bool current) => Outcome switch
    {
        YouTubeOutcome.Success => false,
        YouTubeOutcome.InvalidConfiguration or YouTubeOutcome.ChannelUnverified => true,
        _ => current
    };
}
