using DotNet.Testcontainers.Containers;
using Xunit;
using Xunit.Abstractions;

namespace backend.tests;

public sealed class RyukHostPortStressProbe(ITestOutputHelper output)
{
    internal const string RoundsVariable = "NAILBITER_RYUK_STRESS_ROUNDS";

    [RyukStressFact]
    public async Task Ryuk_starts_every_round_under_host_port_pressure()
    {
        var rounds = int.Parse(Environment.GetEnvironmentVariable(RoundsVariable)!);
        var ephemeralUpperBound = RyukHostPortTests.EphemeralUpperBound();
        ResourceReaper? reaper = null;
        ResourceReaper.StateChanged += (_, change) =>
        {
            if (change.State == ResourceReaperState.MaintainingConnection) reaper = change.Instance;
        };
        var ports = new List<ushort>();
        var failures = new List<string>();
        var started = 0;
        try
        {
            for (; started < rounds; started++)
            {
                try
                {
                    await using var container = IsolatedPostgreSql.CreateContainer();
                    await container.StartAsync();
                    ports.Add(await RyukHostPortTests.PublishedRyukHostPort(ResourceReaper.DefaultSessionId));
                }
                catch (Exception error)
                {
                    failures.Add($"round {started}: {error.GetType().Name}: {error.Message.ReplaceLineEndings(" ")}");
                }
                finally
                {
                    if (reaper is not null) await reaper.DisposeAsync();
                    reaper = null;
                    await WaitUntilRemoved(ResourceReaper.DefaultSessionId);
                }
            }
        }
        finally
        {
            var portConflicts = failures.Count(failure => failure.Contains("testcontainers-ryuk", StringComparison.Ordinal)
                && (failure.Contains("address already in use", StringComparison.Ordinal) || failure.Contains("port is already allocated", StringComparison.Ordinal)));
            output.WriteLine($"RYUK-STRESS starts={started} failures={failures.Count} ryuk_port_conflicts={portConflicts} other_failures={failures.Count - portConflicts}");
            output.WriteLine($"RYUK-STRESS ports_in_ephemeral_range={ports.Count(port => port <= ephemeralUpperBound)} ports_above={ports.Count(port => port > ephemeralUpperBound)} min={(ports.Count > 0 ? ports.Min() : 0)} max={(ports.Count > 0 ? ports.Max() : 0)}");
            foreach (var failure in failures) output.WriteLine($"RYUK-STRESS {failure}");
        }
        Assert.Empty(failures);
    }

    private static async Task WaitUntilRemoved(Guid sessionId)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        while ((await RyukHostPortTests.RyukContainers(sessionId)).Count > 0)
            await Task.Delay(TimeSpan.FromMilliseconds(200), deadline.Token);
    }
}

public sealed class RyukStressFactAttribute : FactAttribute
{
    public RyukStressFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(RyukHostPortStressProbe.RoundsVariable) is null)
            Skip = "Stress probe for issue #102; run it alone through scripts/ryuk-port-stress/run.sh because it restarts this process's Ryuk";
    }
}
