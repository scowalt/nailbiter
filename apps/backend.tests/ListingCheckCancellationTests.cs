using System.Runtime.InteropServices;
using ListingCheck;
using Xunit;

namespace backend.tests;

public sealed class ListingCheckCancellationTests
{
    [Fact]
    public async Task An_interrupted_run_exits_130_with_one_line_whatever_the_run_throws()
    {
        using var cancellation = new CancellationTokenSource();
        var error = new StringWriter();

        var exit = await ListingCheckCommand.RunAsync((_, _) =>
        {
            cancellation.Cancel();
            throw new ObjectDisposedException("IServiceProvider");
        }, error, cancellation);

        Assert.Equal(130, exit);
        Assert.Equal(["cancelled: interrupted before the report was written (exit 130)"], error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task An_interrupt_after_the_report_was_written_keeps_the_reported_outcome()
    {
        using var cancellation = new CancellationTokenSource();
        var error = new StringWriter();

        var exit = await ListingCheckCommand.RunAsync((_, onReported) =>
        {
            onReported(7);
            cancellation.Cancel();
            throw new ObjectDisposedException("IServiceProvider");
        }, error, cancellation);

        Assert.Equal(7, exit);
        Assert.Equal(["cancelled: interrupted after the report was written (exit 7)"], error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Sigterm_copies_stay_graceful_and_only_a_second_sigint_keeps_the_default_action()
    {
        using var interruption = new Interruption();
        var signals = new[] { PosixSignal.SIGTERM, PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGTERM, PosixSignal.SIGINT }
            .Select(signal => new PosixSignalContext(signal)).ToList();

        foreach (var context in signals) interruption.Handle(context);

        Assert.True(interruption.Cancellation.IsCancellationRequested);
        Assert.Equal([true, true, true, true, false], signals.Select(context => context.Cancel));
    }

    [Fact]
    public void Of_two_simultaneous_sigints_exactly_one_stays_graceful()
    {
        const int Rounds = 200_000;
        var interruptions = Enumerable.Range(0, Rounds).Select(_ => new Interruption()).ToArray();
        var graceful = new int[Rounds];
        var round = -1;
        var handlers = Enumerable.Range(0, 2).Select(_ => new Thread(() =>
        {
            for (var mine = 0; mine < Rounds; mine++)
            {
                while (Volatile.Read(ref round) < mine) Thread.SpinWait(1);
                var context = new PosixSignalContext(PosixSignal.SIGINT);
                interruptions[mine].Handle(context);
                if (context.Cancel) Interlocked.Increment(ref graceful[mine]);
            }
        })).ToList();
        handlers.ForEach(handler => handler.Start());
        for (var next = 0; next < Rounds; next++)
        {
            Volatile.Write(ref round, next);
            while (Volatile.Read(ref graceful[next]) == 0) Thread.SpinWait(1);
        }
        handlers.ForEach(handler => handler.Join());
        foreach (var interruption in interruptions) interruption.Dispose();

        Assert.Equal([], graceful.Select((count, index) => (count, index)).Where(result => result.count != 1).Take(5));
    }

    [Fact]
    public async Task An_uninterrupted_failure_exits_1_with_the_error()
    {
        using var cancellation = new CancellationTokenSource();
        var error = new StringWriter();

        var exit = await ListingCheckCommand.RunAsync((_, _) => throw new InvalidOperationException("boom"), error, cancellation);

        Assert.Equal(1, exit);
        Assert.Contains("InvalidOperationException: boom", error.ToString());
    }

    [Fact]
    public async Task A_completed_run_keeps_its_exit_code()
    {
        using var cancellation = new CancellationTokenSource();

        Assert.Equal(7, await ListingCheckCommand.RunAsync((_, _) => Task.FromResult(7), new StringWriter(), cancellation));
    }
}
