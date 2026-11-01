using backend.Data;
using backend.Models;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

public sealed record GameHighlightsInspection(Game Game, bool IsActuallyCompleted, GameHighlights? Highlights);

public sealed class GameHighlightsInspectionService(ExcitementDbContext db)
{
    public async Task<GameHighlightsInspection?> InspectAsync(string gameId, CancellationToken cancellationToken = default)
    {
        var game = await db.Games.AsNoTracking().SingleOrDefaultAsync(g => g.Id == gameId, cancellationToken);
        if (game is null) return null;
        var highlights = await db.GameHighlights.AsNoTracking().SingleOrDefaultAsync(h => h.GameId == gameId, cancellationToken);
        return new(game, MlbCompletionEvidence.IsActuallyCompleted(game), highlights);
    }

    public Task<HighlightsOperationState> InspectOperationsAsync(CancellationToken cancellationToken = default) =>
        db.HighlightsOperations.AsNoTracking().SingleAsync(cancellationToken);
}
