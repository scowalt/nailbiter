namespace backend.Models;

public class F1Driver
{
    public int Id { get; set; }
    public required string DriverNumber { get; set; }   
    public required string FullName { get; set; }
    public required string Abbreviation { get; set; }   
    public required string TeamName { get; set; }   
    public required string TeamColor { get; set; }   
    public string? HeadshotUrl { get; set; }
    public string? CountryCode { get; set; }
    public int Season { get; set; }
    public int? QualifyingPosition { get; set; }
}
