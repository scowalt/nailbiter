namespace backend.tests;

internal sealed class AsyncTestGate
{
    private int armed;
    public TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Arm()
    {
        Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        armed = 1;
    }

    public void Release() => release.TrySetResult();

    public async Task EnterAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref armed, 0) != 1) return;
        Entered.TrySetResult();
        await release.Task.WaitAsync(ct);
    }
}
