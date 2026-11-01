namespace backend.Models;

public class TeamStanding
{
    public int Id { get; set; }
    public required string Sport { get; set; }   
    public required string TeamAbbreviation { get; set; }   
    public string? TeamName { get; set; }

    public string? Division { get; set; }         
    public string? Conference { get; set; }       
    public int? DivisionRank { get; set; }        
    public int? ConferenceRank { get; set; }      

    public int? NationalRank { get; set; }
    public string? RankingPoll { get; set; }

    public int? Wins { get; set; }
    public int? Losses { get; set; }
    public int? Ties { get; set; }               

    public int GamesPlayed => (Wins ?? 0) + (Losses ?? 0) + (Ties ?? 0);

    public int Season { get; set; }
    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;
}
