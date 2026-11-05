namespace backend.Models;

public class Game
{
    public required string Id { get; set; }   
    public required string Sport { get; set; }   
    public required string HomeTeam { get; set; }
    public required string AwayTeam { get; set; }
    public required string HomeTeamAbbreviation { get; set; }
    public required string AwayTeamAbbreviation { get; set; }
    public DateTime ScheduledStart { get; set; }
    public DateTime? ActualStart { get; set; }
    public DateTime? EndTime { get; set; }
    public string Status { get; set; } = "scheduled";   
    public int? HomeScore { get; set; }
    public int? AwayScore { get; set; }
    public bool IsOvertime { get; set; }

    public int? SeasonYear { get; set; }
    public int? SeasonType { get; set; }   
    public int? WeekNumber { get; set; }
    public string? WeekLabel { get; set; }

    public string? HomeTeamId { get; set; }
    public string? AwayTeamId { get; set; }
    public string? HomeColor { get; set; }
    public string? AwayColor { get; set; }
    public int? Period { get; set; }
    public string? GameClock { get; set; }
    public string? StatusDetail { get; set; }
    public DateTime? ScoreboardUpdatedAt { get; set; }

    public string? HomeMarketTicker { get; set; }
    public string? AwayMarketTicker { get; set; }
    public string? CollegeCompetition { get; set; }
    public string? CompetitionEvidence { get; set; }
    public bool IsHistoricalImport { get; set; }
    public double? HomeFpi { get; set; }
    public double? AwayFpi { get; set; }
    public int? HomeApRank { get; set; }
    public int? AwayApRank { get; set; }
    public DateTime? RatingsObservedAt { get; set; }
    public DateTime? MarketObservedAt { get; set; }

    public string? SeriesObservationJson { get; set; }
    public string? ProEvidenceJson { get; set; }
    public string? EspnCompletionEvidenceJson { get; set; }
    public DateTime? EspnCompletionObservedAt { get; set; }
    public DateTime? CompletionConfirmedAt { get; set; }
    public DateTime? HighlightsDiscoveryExpiresAt { get; set; }
    public long? MlbGamePk { get; set; }
    public DateOnly? MlbOfficialDate { get; set; }
    public string? HighlightsIdentityEvidenceJson { get; set; }
    public string[]? BroadcastNetworks { get; set; }

    public List<OddsSnapshot> OddsSnapshots { get; set; } = [];
    public PregameOdds? PregameOdds { get; set; }
}
