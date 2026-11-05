using System.Text.Json.Serialization;

namespace backend.Models;

public sealed record NflPlayoffLeverageEvidence
{
    public int Version { get; set; }
    public string Source { get; set; } = "";
    public string GameId { get; set; } = "";
    public int Season { get; set; }
    public string Home { get; set; } = "";
    public string Away { get; set; } = "";
    public DateTime ScheduledStart { get; set; }
    public DateTime ObservedAt { get; set; }
    public DateTime SchedulePublishedAt { get; set; }
    public string PublicationLabel { get; set; } = "";
    public bool ScheduleVerified { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NflCorroboratedKickoff? CorroboratedKickoff { get; set; }
    public string[] CompletedGameIds { get; set; } = [];
    public NflConditionalPlayoffOdds? HomeOdds { get; set; }
    public NflConditionalPlayoffOdds? AwayOdds { get; set; }
    public string Reason { get; set; } = "Conditional playoff probabilities unavailable";
}

public sealed record NflCorroboratedKickoff
{
    public int Week { get; set; }
    public DateTime SourceScheduledStart { get; set; }
    public DateTime EspnScheduledStart { get; set; }
    public string HomeGameId { get; set; } = "";
    public string AwayGameId { get; set; } = "";
    public string HomeNextGameId { get; set; } = "";
    public string AwayNextGameId { get; set; } = "";
}

public sealed record NflConditionalPlayoffOdds
{
    public string Reference { get; set; } = "";
    public DateTime PublishedAt { get; set; }
    public double? WinLower { get; set; }
    public double? WinUpper { get; set; }
    public double? LossLower { get; set; }
    public double? LossUpper { get; set; }
}
