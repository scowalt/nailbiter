namespace backend.GraphQL.Types;

public enum GameStatus
{
    SCHEDULED,
    IN_PROGRESS,
    FINAL,
    CANCELED
}

public static class GameStatusExtensions
{
    public static string ToDbValue(this GameStatus status) => status switch
    {
        GameStatus.SCHEDULED => "scheduled",
        GameStatus.IN_PROGRESS => "in_progress",
        GameStatus.FINAL => "final",
        GameStatus.CANCELED => "canceled",
        _ => "scheduled"
    };

    public static GameStatus? FromDbValue(string? value) => value switch
    {
        "scheduled" => GameStatus.SCHEDULED,
        "in_progress" => GameStatus.IN_PROGRESS,
        "final" => GameStatus.FINAL,
        "canceled" => GameStatus.CANCELED,
        _ => null
    };
}
