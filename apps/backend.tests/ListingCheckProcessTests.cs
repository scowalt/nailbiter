using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using ListingCheck;
using Xunit;

namespace backend.tests;

public sealed class ListingCheckProcessTests
{
    private const int Sigterm = 15;
    private const int Sigkill = 9;
    private const string Cancelled = "cancelled: interrupted before the report was written (exit 130)";
#if DEBUG
    private const string Configuration = "Debug";
#else
    private const string Configuration = "Release";
#endif

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    [Fact]
    public async Task One_sigterm_to_the_app_run_directly_exits_130_with_one_line()
    {
        var run = await SigtermToProcessGroupAsync("dotnet", typeof(ListingCheckCommand).Assembly.Location);

        Assert.Equal([Cancelled], run.Errors);
        Assert.DoesNotContain("outcome", run.Output);
        Assert.Equal(ListingCheckCommand.Interrupted, run.Exit);
    }

    [Fact]
    public async Task One_sigterm_to_a_dotnet_run_process_group_is_one_graceful_interrupt_whose_exit_code_only_the_direct_test_checks_as_dotnet_run_exits_0_130_or_143_by_timing()
    {
        var run = await SigtermToProcessGroupAsync("dotnet", "run", "--project", ListingCheckProject(), "-c", Configuration, "--no-build", "--");

        Assert.Equal([Cancelled], run.Errors);
        Assert.DoesNotContain("outcome", run.Output);
    }

    private sealed record Terminated(int Exit, string[] Errors, string Output);

    private static async Task<Terminated> SigtermToProcessGroupAsync(params string[] command)
    {
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var port = ((IPEndPoint)silent.LocalEndpoint).Port;
        var start = new ProcessStartInfo("setsid") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in command.Concat([
            "url", "--base-url", $"http://127.0.0.1:{port}", "--samples", "1", "--warmup", "0",
            "--ttfb-budget-ms", "1000", "--completion-budget-ms", "2000", "--request-timeout-ms", "60000"]))
        {
            start.ArgumentList.Add(argument);
        }
        using var run = Process.Start(start)!;
        var stdout = run.StandardOutput.ReadToEndAsync();
        var stderr = run.StandardError.ReadToEndAsync();
        try
        {
            var connected = silent.AcceptSocketAsync();
            var first = await Task.WhenAny(connected, run.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(120));
            if (first != connected) Assert.Fail($"listing-check exited {run.ExitCode} before connecting: {await stderr}");
            using var probe = await connected;
            Assert.Equal(run.Id, ProcessGroup(run.Id));

            Assert.Equal(0, kill(-run.Id, Sigterm));
            await run.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));

            var streamsClosed = TimeSpan.FromSeconds(10);
            var errors = (await stderr.WaitAsync(streamsClosed)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new Terminated(run.ExitCode, errors, await stdout.WaitAsync(streamsClosed));
        }
        finally
        {
            kill(-run.Id, Sigkill);
        }
    }

    private static int ProcessGroup(int pid)
    {
        var stat = File.ReadAllText($"/proc/{pid}/stat");
        return int.Parse(stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[2]);
    }

    private static string ListingCheckProject()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var project = Path.Combine(directory.FullName, "apps", "listing-check", "listing-check.csproj");
            if (File.Exists(project)) return project;
        }
        throw new InvalidOperationException("apps/listing-check/listing-check.csproj is not above the test output directory");
    }
}
