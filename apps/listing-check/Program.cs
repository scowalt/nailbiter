namespace ListingCheck;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args is ["url", .. var rest]) return await ListingCheckCommand.RunUrlAsync(rest, Console.Out, CancellationToken.None);
            await Console.Error.WriteLineAsync(ListingCheckCommand.Usage);
            return (int)FailureKind.InvalidConfig;
        }
        catch (Exception error)
        {
            await Console.Error.WriteLineAsync(error.ToString());
            return 1;
        }
    }
}
