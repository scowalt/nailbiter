namespace backend.Models;

public class OddsSnapshot
{
    public const int GameClockMaxLength = 20;

    public static string? BoundGameClock(string? clock) =>
        clock is not { Length: > GameClockMaxLength } ? clock
        : char.IsHighSurrogate(clock[GameClockMaxLength - 1]) ? clock[..(GameClockMaxLength - 1)]
        : clock[..GameClockMaxLength];

    public long Id { get; set; }
    public required string GameId { get; set; }
    public DateTime Timestamp { get; set; }

    public int HomeMoneyline { get; set; }
    public int AwayMoneyline { get; set; }
    public int? DrawMoneyline { get; set; }
    public double? Spread { get; set; }
    public double? OverUnder { get; set; }

    public double HomeWinProbability { get; set; }
    public double AwayWinProbability { get; set; }
    public double? DrawWinProbability { get; set; }

    public string? GameClock { get; set; }
    public int? Period { get; set; }
    public int? HomeScore { get; set; }
    public int? AwayScore { get; set; }

    public string? Source { get; set; }

    public bool IsHistorical { get; set; }

    public Game? Game { get; set; }
}
