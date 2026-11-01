using System.Text.Json;

namespace backend.Models;

public sealed class ProGameEvidence
{
    public string GameId { get; set; } = "";
    public string Sport { get; set; } = "";
    public int Season { get; set; }
    public string Home { get; set; } = "";
    public string Away { get; set; } = "";
    public DateTime ScheduledStart { get; set; }
    public DateTime ObservedAt { get; set; }
    public string Source { get; set; } = "";
    public bool VerifiedUnstarted { get; set; }
    public int? Phase { get; set; }
    public int? Round { get; set; }
    public bool? HomeElimination { get; set; }
    public bool? AwayElimination { get; set; }
    public string? PlayIn { get; set; }
    public ProTeamProgress HomeProgress { get; set; } = new();
    public ProTeamProgress AwayProgress { get; set; } = new();
    public string[] CompletedGameIds { get; set; } = [];
    public string Reason { get; set; } = "Competition context unavailable";

    public static ProGameEvidence? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<ProGameEvidence>(json); }
        catch (JsonException) { return null; }
    }
}

public sealed class ProTeamProgress
{
    public int? Completed { get; set; }
    public int? Scheduled { get; set; }
    public bool ScheduleVerified { get; set; }
    public bool? SeedingUnresolved { get; set; }
    public string Reason { get; set; } = "Verified seeding consequences (including home advantage) unavailable";
}
