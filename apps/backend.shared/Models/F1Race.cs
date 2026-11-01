namespace backend.Models;

public class F1Race
{
    public required string Id { get; set; }   
    public required string EspnEventId { get; set; }   
    public required string MeetingName { get; set; }   
    public required string CircuitName { get; set; }   
    public required string CircuitShortName { get; set; }   
    public required string Country { get; set; }
    public int Year { get; set; }
    public int RoundNumber { get; set; }
    public DateTime ScheduledStart { get; set; }
    public DateTime? ActualStart { get; set; }
    public DateTime? EndTime { get; set; }
    public string Status { get; set; } = "scheduled";   
    public int TotalLaps { get; set; }
    public int? CompletedLaps { get; set; }
    public double? ExcitementScore { get; set; }
}
