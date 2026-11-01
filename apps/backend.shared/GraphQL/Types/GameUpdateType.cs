namespace backend.GraphQL.Types;

public class GameUpdate
{
    public required string GameId { get; set; }
    public required Sport Sport { get; set; }
    public required string HomeTeam { get; set; }
    public required string AwayTeam { get; set; }
    public string? HomeTeamAbbreviation { get; set; }
    public string? AwayTeamAbbreviation { get; set; }
    public string? HomeTeamId { get; set; }
    public string? AwayTeamId { get; set; }
    public string? HomeColor { get; set; }
    public string? AwayColor { get; set; }
    public int? HomeScore { get; set; }
    public int? AwayScore { get; set; }
    public required GameStatus Status { get; set; }
    public bool IsOvertime { get; set; }
    public double? HomeWinProbability { get; set; }
    public double? AwayWinProbability { get; set; }
    public double? DrawWinProbability { get; set; }
    public string? OddsSource { get; set; }
    public double? ExcitementScore { get; set; }
    public string? GameClock { get; set; }
    public int? Period { get; set; }
    public DateTime UpdatedAt { get; set; }
}
