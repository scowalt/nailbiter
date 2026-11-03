namespace backend.Models;

public sealed class MoneyPuckRefreshState
{
    public int Id { get; set; } = 1;
    public long NextAttemptAtTicks { get; set; }
    public bool Active { get; set; }
    public bool Denied { get; set; }
    public long? DeniedUntilTicks { get; set; }
}
