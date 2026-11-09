using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;
using Xunit;

namespace backend.tests;

[Collection(nameof(SentryGlobalHubCollection))]
public sealed class SentryHostIsolationTests
{
    [Fact]
    public async Task Fault_in_a_task_this_host_never_awaited_is_not_sent_to_its_transport()
    {
        var transport = new EventCounter();
        await using var entry = new AppFactory();
        await using var app = entry.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            foreach (var worker in services.Where(s => s.ServiceType == typeof(IHostedService)
                && s.ImplementationType?.Namespace?.StartsWith("backend", StringComparison.Ordinal) == true).ToArray())
                services.Remove(worker);
            services.PostConfigure<SentryAspNetCoreOptions>(options =>
            {
                options.Dsn = "https://public@sentry.invalid/1";
                options.Transport = transport;
            });
        }));
        _ = app.Services;

        AbandonFaultedTask();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(transport.Exceptions);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AbandonFaultedTask()
    {
        var task = Task.Run(() => throw new ObjectDisposedException("SemaphoreSlim"));
        SpinWait.SpinUntil(() => task.IsCompleted);
    }

    private sealed class EventCounter : ITransport
    {
        public ConcurrentQueue<string> Exceptions { get; } = new();

        public async Task SendEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken = default)
        {
            using var stream = new MemoryStream();
            await envelope.SerializeAsync(stream, null, cancellationToken);
            var lines = System.Text.Encoding.UTF8.GetString(stream.ToArray()).Split('\n');
            for (var i = 1; i + 1 < lines.Length; i++)
            {
                using var header = JsonDocument.Parse(lines[i]);
                if (header.RootElement.GetProperty("type").GetString() != "event") continue;
                using var payload = JsonDocument.Parse(lines[++i]);
                foreach (var exception in payload.RootElement.GetProperty("exception").GetProperty("values").EnumerateArray())
                    Exceptions.Enqueue($"{exception.GetProperty("type").GetString()} via {exception.GetProperty("mechanism").GetProperty("type").GetString()}");
            }
        }
    }
}
