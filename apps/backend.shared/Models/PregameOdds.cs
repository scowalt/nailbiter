namespace backend.Models;

public class PregameOdds
{
    public required string GameId { get; set; }

    public int? HomeMoneyline { get; set; }
    public int? AwayMoneyline { get; set; }
    public int? DrawMoneyline { get; set; }
    public double? Spread { get; set; }
    public double? OverUnder { get; set; }

    public double HomeWinProbability { get; set; }
    public double AwayWinProbability { get; set; }
    public double? DrawWinProbability { get; set; }

    public int? SourceSeason { get; set; }
    public DateTime? ApplicableStart { get; set; }
    public string? HomeTeamAbbreviation { get; set; }
    public string? AwayTeamAbbreviation { get; set; }

    public DateTime FetchedAt { get; set; }

    public string Source { get; set; } = "yahoo";

    public Game? Game { get; set; }
}
