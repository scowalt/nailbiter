namespace backend.Models;

public class F1ChampionshipStanding
{
    public int Id { get; set; }
    public int Season { get; set; }
    public required string Type { get; set; }   
    public required string EntityName { get; set; }   
    public required string Abbreviation { get; set; }
    public int Position { get; set; }
    public double Points { get; set; }
    public int Wins { get; set; }
    public int AfterRound { get; set; }   
}
