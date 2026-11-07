namespace backend.Models;

public sealed class StartHype
{
    public string GameId { get; set; } = "";
    public double Total { get; set; }
    public string? QualitySource { get; set; }
    public string[] MoneyPuckTeams { get; set; } = [];
}
