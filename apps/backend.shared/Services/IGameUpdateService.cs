using backend.GraphQL.Types;

namespace backend.Services;

public interface IGameUpdateService
{
    Task PublishUpdateAsync(GameUpdate update, CancellationToken ct = default);
}
