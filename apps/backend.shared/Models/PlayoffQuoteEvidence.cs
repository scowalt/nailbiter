using System.Text.Json;

namespace backend.Models;

public sealed class PlayoffQuoteEvidence
{
    public int Version { get; set; }
    public string ValidationPolicy { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Sport { get; set; } = "";
    public string Team { get; set; } = "";
    public int Season { get; set; }
    public string EventId { get; set; } = "";
    public string MarketId { get; set; } = "";
    public string ConditionId { get; set; } = "";
    public string YesTokenId { get; set; } = "";
    public string NoTokenId { get; set; } = "";
    public string RulesHash { get; set; } = "";
    public bool ContractVerified { get; set; }
    public string State { get; set; } = "unavailable";
    public string ReasonCode { get; set; } = "unverified_evidence";
    public bool ResolutionBlocked { get; set; }
    public DateTime? ObservedAt { get; set; }
    public DateTime? EvidenceAt { get; set; }
    public DateTime? BookAt { get; set; }
    public DateTime? ResolutionAt { get; set; }
    public string BookHash { get; set; } = "";
    public string RefreshId { get; set; } = "";
    public decimal? Bid { get; set; }
    public decimal? Ask { get; set; }
    public decimal? BidSize { get; set; }
    public decimal? AskSize { get; set; }
    public bool BidExplicitlyEmpty { get; set; }
    public bool AskExplicitlyEmpty { get; set; }
    public decimal? YesPayout { get; set; }
    public decimal? NoPayout { get; set; }
    public string ResolutionReference { get; set; } = "";

    public static PlayoffQuoteEvidence? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<PlayoffQuoteEvidence>(json); }
        catch (JsonException) { return null; }
    }
}
