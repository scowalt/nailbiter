namespace backend.Models;

public class IndyCarRaceSnapshot
{
    public long Id { get; set; }
    public required string RaceId { get; set; }
    public required string EspnAthleteId { get; set; }
    public DateTime Timestamp { get; set; }
    public int? Position { get; set; }
    public double? WinProbability { get; set; }
    public int? CompletedLaps { get; set; }
    public string? Source { get; set; }
}
