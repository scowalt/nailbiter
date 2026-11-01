namespace backend.Models;

public class NascarRace
{
    public required string Id { get; set; }   
    public required string RaceName { get; set; }
    public int Year { get; set; }
    public int RaceNumber { get; set; }
    public DateTime ScheduledStart { get; set; }
    public DateTime? ActualStart { get; set; }
    public DateTime? EndTime { get; set; }
    public string Status { get; set; } = "scheduled";   
    public int TotalLaps { get; set; }
    public int? CompletedLaps { get; set; }
    public double? ExcitementScore { get; set; }
    public int? FlagState { get; set; }   
}
