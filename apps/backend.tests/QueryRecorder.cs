using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace backend.tests;

internal sealed record CapturedCommand(string Sql, (string Name, object Value)[] Parameters, bool CanBeCanceled);

internal sealed class CommandRecorder : DbCommandInterceptor
{
    public List<CapturedCommand> Commands { get; } = [];

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Commands.Add(new(command.CommandText, command.Parameters.Cast<DbParameter>()
            .Select(p => (p.ParameterName, p.Value!)).ToArray(), cancellationToken.CanBeCanceled));
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}

internal static class QueryPlans
{
    public static IEnumerable<JsonElement> Descendants(JsonElement node)
    {
        yield return node;
        if (node.TryGetProperty("Plans", out var children))
            foreach (var child in children.EnumerateArray())
                foreach (var descendant in Descendants(child))
                    yield return descendant;
    }
}
