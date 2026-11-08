using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace backend.tests;

internal sealed class StalledBodyProvider : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stopping = new();
    private readonly Func<Uri, bool> stalls;
    private readonly TaskCompletionSource firstStall = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public StalledBodyProvider(Func<Uri, bool> stalls)
    {
        this.stalls = stalls;
        listener.Start();
        _ = AcceptAsync();
    }

    public ConcurrentQueue<Uri> StalledRequests { get; } = new();

    public int Stalled => StalledRequests.Count;

    public Task FirstStall => firstStall.Task;

    public HttpMessageHandler Handler() => new ToLoopback(((IPEndPoint)listener.LocalEndpoint).Port);

    public void Dispose()
    {
        stopping.Cancel();
        listener.Stop();
        stopping.Dispose();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true) _ = ServeAsync(await listener.AcceptTcpClientAsync(stopping.Token));
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task ServeAsync(TcpClient connection)
    {
        using var _ = connection;
        try
        {
            var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            while (await reader.ReadLineAsync(stopping.Token) is { } requestLine)
            {
                while (await reader.ReadLineAsync(stopping.Token) is { Length: > 0 }) { }
                var target = new Uri(new Uri("http://provider.invalid"), requestLine.Split(' ')[1]);
                if (!stalls(target))
                {
                    await stream.WriteAsync("HTTP/1.1 404 Not Found\r\nContent-Type: application/json\r\nContent-Length: 2\r\n\r\n{}"u8.ToArray(), stopping.Token);
                    continue;
                }
                StalledRequests.Enqueue(target);
                await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 4096\r\n\r\n{"u8.ToArray(), stopping.Token);
                await stream.FlushAsync(stopping.Token);
                firstStall.TrySetResult();
                await Task.Delay(Timeout.Infinite, stopping.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    private sealed class ToLoopback(int port) : DelegatingHandler(new SocketsHttpHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri = new UriBuilder(request.RequestUri!) { Scheme = "http", Host = "127.0.0.1", Port = port }.Uri;
            return base.SendAsync(request, cancellationToken);
        }
    }
}
