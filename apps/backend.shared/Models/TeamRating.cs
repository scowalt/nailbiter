namespace backend.Models;

public class TeamRating
{
    public int Id { get; set; }
    public required string Sport { get; set; }   
    public required string TeamAbbreviation { get; set; }   
    public string? TeamName { get; set; }

    public double? EloRating { get; set; }       
    public double? BpiRating { get; set; }       
    public double? FpiRating { get; set; }       
    public int? PowerRank { get; set; }          

    public double? PlayoffProbability { get; set; }
    public bool PlayoffProbabilityIsFinal { get; set; }
    public string? PlayoffEvidenceJson { get; set; }
    public double? ChampionshipProbability { get; set; }

    public double? GameQuality { get; set; }      
    public double? GameImportance { get; set; }   

    public required string Source { get; set; }   
    public int Season { get; set; }
    public int? SourceSeason { get; set; }
    public DateTime? RankPublishedAt { get; set; }
    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;
}
