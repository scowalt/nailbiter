namespace backend.Models;

public class CollegeTeam
{
    public int Season { get; set; }
    public required string EspnId { get; set; }
    public required string Abbreviation { get; set; }
    public required string Name { get; set; }
    public double? Fpi { get; set; }
    public DateTime FpiUpdatedAt { get; set; }
    public int? ApRank { get; set; }
    public DateTime? ApUpdatedAt { get; set; }
    public DateTime FetchedAt { get; set; }
}

public class CollegeImport
{
    public string Id { get; set; } = "initial";
    public int Season { get; set; }
    public DateTime FirstImportAt { get; set; }
    public DateTime WindowStart { get; set; }
}
