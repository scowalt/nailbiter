using Backend.Rendering;
using StarFederation.Datastar.DependencyInjection;
using backend.GraphQL.Types;
using backend.Services;
using Microsoft.FSharp.Core;

namespace backend.Routes;

public static class SseRoutes
{
    public static void MapSseRoutes(this WebApplication app)
    {
        app.MapGet("/sse/live", HandleLiveStream);
        app.MapGet("/sse/live/{sport}", HandleSportStream);
        app.MapGet("/sse/game/{id}", HandleGameStream);
        app.MapGet("/sse/page-sync", HandlePageSync);
    }

    private static async Task HandlePageSync(HttpContext context, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-cache, no-store";
        context.Response.Headers.Append("X-Accel-Buffering", "no");
        context.Response.ContentType = "text/event-stream";

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await context.Response.WriteAsync($"event: sync\ndata: {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}\n\n", ct);
                await context.Response.Body.FlushAsync(ct);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task HandleLiveStream(
        ISseBroadcaster broadcaster,
        IDatastarService datastar,
        CancellationToken ct)
    {
        await StreamTopicWithFragments(broadcaster, "live", isDetailPage: false, datastar, ct);
    }

    private static async Task HandleSportStream(
        string sport,
        ISseBroadcaster broadcaster,
        IDatastarService datastar,
        CancellationToken ct)
    {
        var sportEnum = SportExtensions.FromDbValue(sport);
        var normalizedSport = sportEnum.HasValue ? sportEnum.Value.ToDbValue() : sport.ToLowerInvariant();
        await StreamTopicWithFragments(broadcaster, $"live:{normalizedSport}", isDetailPage: false, datastar, ct);
    }

    private static async Task HandleGameStream(
        string id,
        HttpContext context,
        GameDataService games,
        ISseBroadcaster broadcaster,
        IDatastarService datastar,
        CancellationToken ct)
    {
        if (FSharpOption<backend.Models.Game>.get_IsNone(await games.GetGameAsync(id, ct)))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        await StreamTopicWithFragments(broadcaster, $"game:{id}", isDetailPage: true, datastar, ct);
    }

    private static async Task StreamTopicWithFragments(
        ISseBroadcaster broadcaster,
        string topic,
        bool isDetailPage,
        IDatastarService datastar,
        CancellationToken ct)
    {
        await datastar.StartServerEventStreamAsync(cancellationToken: ct);

        var reader = broadcaster.Subscribe(topic);

        try
        {
            await foreach (var update in reader.ReadAllAsync(ct))
            {
                var html = RenderFragments(update, isDetailPage);
                if (!string.IsNullOrEmpty(html))
                    await datastar.PatchElementsAsync(html, cancellationToken: ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            broadcaster.Unsubscribe(topic, reader);
        }
    }

    private static string RenderFragments(GameUpdate update, bool isDetailPage)
    {
        var sport = update.Sport;

        if (sport == Sport.WORLD_CUP)
        {
            if (isDetailPage)
            {
                return WorldCupRenderer.RenderMatchFragments(update)
                    + SseFragmentRenderer.renderDetailFragments(update);
            }

            return WorldCupRenderer.RenderFragments(update);
        }

        if (isDetailPage)
        {
            return SseFragmentRenderer.renderDetailFragments(update);
        }
        else
        {
            return SseFragmentRenderer.renderGameCardFragments(update);
        }
    }
}
