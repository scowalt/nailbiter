namespace ListingCheck;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args is not ["url", .. var rest])
        {
            await Console.Error.WriteLineAsync(ListingCheckCommand.Usage);
            return (int)FailureKind.InvalidConfig;
        }
        return await ListingCheckCommand.RunUntilInterruptedAsync((token, onReported) =>
            ListingCheckCommand.RunUrlAsync(rest, Console.Out, token, onReported));
    }
}
