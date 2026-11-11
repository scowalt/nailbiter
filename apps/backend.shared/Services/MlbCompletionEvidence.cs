using System.Text.Json;
using backend.Models;
using backend.Data;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

public sealed record EspnCompletionStatus(string? Name, string? State, bool? Completed, string? Description, string? Detail, string? ShortDetail);

public sealed record MlbCompletionEvidence(
    string EventId,
    string? CompetitionId,
    EspnCompletionStatus EventStatus,
    EspnCompletionStatus CompetitionStatus,
    DateTime ObservedAt)
{
    private static bool Confirmed(EspnCompletionStatus? status) =>
        status is not null && status.Name == "STATUS_FINAL" && status.State == "post" && status.Completed == true
        && !ContradictsCompletion(status.Description) && !ContradictsCompletion(status.Detail) && !ContradictsCompletion(status.ShortDetail);

    private static bool ContradictsCompletion(string? description) => description is not null
        && new[] { "postpon", "suspend", "cancel", "delay", "abandon" }
            .Any(word => description.Contains(word, StringComparison.OrdinalIgnoreCase));

    public bool ConfirmsCompletion(string gameId) => EventId == gameId && CompetitionId == gameId
        && Confirmed(EventStatus) && Confirmed(CompetitionStatus);

    public static bool IsActuallyCompleted(Game game) => IsActuallyCompleted(game, out _);

    public static bool IsActuallyCompleted(Game game, out JsonException? unreadable)
    {
        unreadable = null;
        if (game.Sport != "mlb" || game.Status != "final" || game.EspnCompletionEvidenceJson is null)
            return false;
        try
        {
            return JsonSerializer.Deserialize<MlbCompletionEvidence>(game.EspnCompletionEvidenceJson)?.ConfirmsCompletion(game.Id) == true;
        }
        catch (JsonException exception)
        {
            unreadable = exception;
            return false;
        }
    }

    public Task<int> RecordAsync(ExcitementDbContext db, bool allowOrdinaryDiscovery, CancellationToken cancellationToken = default)
    {
        var confirmed = ConfirmsCompletion(EventId);
        var evidence = JsonSerializer.Serialize(this);
        var deadline = ObservedAt.AddHours(72);
        return db.Games.Where(g => g.Id == EventId && g.Sport == "mlb"
                && (g.EspnCompletionObservedAt == null || g.EspnCompletionObservedAt <= ObservedAt))
            .ExecuteUpdateAsync(s => s
                .SetProperty(g => g.EspnCompletionEvidenceJson, evidence)
                .SetProperty(g => g.EspnCompletionObservedAt, ObservedAt)
                .SetProperty(g => g.CompletionConfirmedAt, g => confirmed && g.Status == "final"
                    ? g.CompletionConfirmedAt ?? ObservedAt : g.CompletionConfirmedAt)
                .SetProperty(g => g.HighlightsDiscoveryExpiresAt, g => confirmed && g.Status == "final"
                    && g.CompletionConfirmedAt == null && allowOrdinaryDiscovery
                    ? deadline : g.HighlightsDiscoveryExpiresAt), cancellationToken);
    }
}
