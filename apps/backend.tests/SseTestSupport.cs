using System.Collections.Concurrent;
using System.Threading.Channels;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using backend.GraphQL.Types;
using Backend.Rendering;
using Xunit;

namespace backend.tests;

internal sealed class SsePeer : IAsyncDisposable
{
    private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
    private readonly Task<HttpResponseMessage> pending;
    private HttpResponseMessage? response;
    private StreamReader? reader;

    public SsePeer(HttpClient client, string path) => pending = client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, deadline.Token);

    public async Task<IDocument> ReadForAsync(string id)
    {
        response ??= await pending;
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        reader ??= new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
        var elements = new List<string>();
        while (true)
        {
            var line = await reader.ReadLineAsync(deadline.Token) ?? throw new EndOfStreamException("SSE ended before " + id);
            if (line.StartsWith("data: elements ")) elements.Add(line[15..]);
            if (line.Length != 0 || elements.Count == 0) continue;
            var page = await new HtmlParser().ParseDocumentAsync(string.Join("\n", elements), deadline.Token);
            if (page.QuerySelector("#" + id) is not null) return page;
            page.Dispose();
            elements.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await deadline.CancelAsync();
        try { response ??= await pending; }
        catch (OperationCanceledException) { }
        reader?.Dispose();
        response?.Dispose();
        deadline.Dispose();
    }
}

internal sealed class ReadyBroadcaster : ISseBroadcaster
{
    private readonly ISseBroadcaster inner = new SseBroadcaster();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> subscriptions = new();
    public Task PublishAsync(GameUpdate update, CancellationToken ct) => inner.PublishAsync(update, ct);
    public ChannelReader<GameUpdate> Subscribe(string topic)
    {
        var reader = inner.Subscribe(topic);
        subscriptions.GetOrAdd(topic, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        return reader;
    }
    public void Unsubscribe(string topic, ChannelReader<GameUpdate> subscription) => inner.Unsubscribe(topic, subscription);
    public Task ReadyAsync(IEnumerable<string> topics, CancellationToken ct) => Task.WhenAll(topics.Select(topic =>
        subscriptions.GetOrAdd(topic, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task)).WaitAsync(ct);
}
