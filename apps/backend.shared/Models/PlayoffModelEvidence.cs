using System.Text.Json;

namespace backend.Models;

public sealed class PlayoffModelEvidence
{
    public int Version { get; set; } = 2;
    public string EvidenceKind { get; set; } = "model_projection";
    public string ValidationPolicy { get; set; } = "";
    public string Provider { get; set; } = "";
    public bool ProviderVerified { get; set; }
    public string Sport { get; set; } = "";
    public string Team { get; set; } = "";
    public int Season { get; set; }
    public bool SeasonVerified { get; set; }
    public bool TeamVerified { get; set; }
    public string State { get; set; } = "unavailable";
    public string ReasonCode { get; set; } = "unverified_evidence";
    public DateTime? PublishedAt { get; set; }
    public DateTime? PublicationBoundary { get; set; }
    public DateTime[] MoneyPuckRejectedPublications { get; set; } = [];
    public DateTime? FetchedAt { get; set; }
    public string RefreshId { get; set; } = "";
    public int AcceptedTeamCount { get; set; }
    public int TotalTeamCount { get; set; }
    public double? MoneyPuckExpiredProbability { get; set; }
    public MoneyPuckSeasonProof? MoneyPuckSeasonProof { get; set; }

    public static PlayoffModelEvidence? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<PlayoffModelEvidence>(json); }
        catch (JsonException) { return null; }
    }
}

public sealed class MoneyPuckSeasonProof
{
    public string PageSeasonLabel { get; set; } = "";
    public int PageSeasonStartYear { get; set; }
    public int PageSeasonEndYear { get; set; }
    public string PageReference { get; set; } = "";
    public DateTime? PageObservedAt { get; set; }
    public long FirstSimulatedId { get; set; }
    public NhlSimulatedGameSeasonEvidence[] VerifiedGames { get; set; } = [];
}

public sealed class NhlSimulatedGameSeasonEvidence
{
    public long SimulatedId { get; set; }
    public long GameId { get; set; }
    public int Season { get; set; }
    public string Reference { get; set; } = "";
    public DateTime? ObservedAt { get; set; }
}
