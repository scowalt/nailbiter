using System.Text.Json;

namespace backend.Services.Highlights;

public sealed class HighlightsOperatorCommand(HighlightsApplicationService service)
{
    public async Task<int> RunAsync(IReadOnlyList<string> arguments, TextWriter output, CancellationToken cancellationToken = default)
    {
        if (arguments.Count < 2 || !Enum.TryParse<HighlightsOperatorAction>(arguments[0], true, out var action) || !Enum.IsDefined(action))
            return await InvalidAsync(output);
        var applying = arguments[^1] == "--apply";
        var positional = arguments.Count - (applying ? 1 : 0);
        var needsVideo = action is HighlightsOperatorAction.Attach or HighlightsOperatorAction.Replace;
        if (positional != (needsVideo ? 3 : 2) || arguments.Take(positional).Any(a => string.IsNullOrWhiteSpace(a) || a.StartsWith("--", StringComparison.Ordinal)))
            return await InvalidAsync(output);
        var result = await service.OperateAsync(new(action, arguments[1], needsVideo ? arguments[2] : null, applying), cancellationToken);
        await output.WriteLineAsync(JsonSerializer.Serialize(result));
        return result.Accepted ? 0 : 2;
    }

    private static async Task<int> InvalidAsync(TextWriter output)
    {
        await output.WriteLineAsync("Usage: --highlights inspect|attach|replace|remove|resume GAME_ID [VIDEO_ID] [--apply]. Default: dry run.");
        return 2;
    }
}
