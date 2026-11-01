namespace backend.Models;

public class IndyCarDriver
{
    public int Id { get; set; }
    public required string EspnAthleteId { get; set; }
    public required string FullName { get; set; }
    public required string DisplayName { get; set; }
    public required string ShortName { get; set; }
    public string? CountryFlagHref { get; set; }
    public string? CarNumber { get; set; }
    public string? TeamName { get; set; }
    public int Season { get; set; }
}
