namespace backend.Models;

public sealed class PlayoffStatusCollectionState
{
    public string League { get; set; } = "";
    public long NextAttemptAtTicks { get; set; }
}
