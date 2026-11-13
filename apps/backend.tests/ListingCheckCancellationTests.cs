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

        var exit = await ListingCheckCommand.RunAsync(token =>
        {
            cancellation.Cancel();
            throw new ObjectDisposedException("IServiceProvider");
        }, error, cancellation);

        Assert.Equal(130, exit);
        Assert.Equal(["cancelled: interrupted before the report was written (exit 130)"], error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task An_uninterrupted_failure_exits_1_with_the_error()
    {
        using var cancellation = new CancellationTokenSource();
        var error = new StringWriter();

        var exit = await ListingCheckCommand.RunAsync(_ => throw new InvalidOperationException("boom"), error, cancellation);

        Assert.Equal(1, exit);
        Assert.Contains("InvalidOperationException: boom", error.ToString());
    }

    [Fact]
    public async Task A_completed_run_keeps_its_exit_code()
    {
        using var cancellation = new CancellationTokenSource();

        Assert.Equal(7, await ListingCheckCommand.RunAsync(_ => Task.FromResult(7), new StringWriter(), cancellation));
    }
}
