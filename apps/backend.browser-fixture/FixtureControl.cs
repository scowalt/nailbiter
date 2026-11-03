using System.Net.Sockets;
using System.Text.Json;

namespace BrowserFixture;

internal static class FixtureControl
{
    public static async Task RunAsync(string path, FixtureHost host, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Owned Unix IPC is required.");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        listener.Listen(1);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var socket = await listener.AcceptAsync(ct);
                using var stream = new NetworkStream(socket);
                using var reader = new StreamReader(stream);
                using var writer = new StreamWriter(stream) { AutoFlush = true };
                var command = await reader.ReadLineAsync(ct);
                try
                {
                    var result = host.GroupedHome is not null
                        ? await host.GroupedHome.ControlAsync(host.Services, command ?? "")
                        : host.Series is not null
                            ? await host.Series.ControlAsync(host.Services, command ?? "")
                        : await host.Highlights!.ControlAsync(host.Services, command ?? "");
                    await writer.WriteLineAsync(JsonSerializer.Serialize(result));
                }
                catch (Exception error)
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new { error = error.GetType().Name }));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { File.Delete(path); }
    }
}
