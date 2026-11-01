using backend.Data;
using backend.GraphQL.Types;
using Backend.Rendering;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

public class GameUpdateService : IGameUpdateService
{
    private readonly ISseBroadcaster _sseBroadcaster;
    private readonly IServiceScopeFactory _scopes;

    public GameUpdateService(ISseBroadcaster sseBroadcaster, IServiceScopeFactory scopes)
    {
        _sseBroadcaster = sseBroadcaster;
        _scopes = scopes;
    }

    public async Task PublishUpdateAsync(GameUpdate update, CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        var game = await db.Games.Where(g => g.Id == update.GameId)
            .Select(g => new { g.Sport, g.SeasonType }).FirstOrDefaultAsync(ct);
        if (game is not null && GameVisibilityPolicy.IsExcluded(game.Sport, game.SeasonType)) return;

        await _sseBroadcaster.PublishAsync(update, ct);
    }
}
