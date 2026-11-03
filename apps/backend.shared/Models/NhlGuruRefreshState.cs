namespace backend.Models;

public sealed class NhlGuruRefreshState
{
    public int Id { get; set; } = 1;
    public long NextAttemptAtTicks { get; set; }
}
