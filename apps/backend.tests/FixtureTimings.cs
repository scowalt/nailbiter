using System.Diagnostics;
using System.Globalization;

namespace backend.tests;

internal static class FixtureTimings
{
    public const string PathVariable = "NAILBITER_FIXTURE_TIMINGS";

    private static readonly string? Path = Environment.GetEnvironmentVariable(PathVariable);
    private static readonly Lock Gate = new();

    public static async Task<T> MeasureAsync<T>(string owner, string phase, Func<Task<T>> work)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await work();
        }
        finally
        {
            Record(owner, phase, Stopwatch.GetElapsedTime(started));
        }
    }

    public static Task MeasureAsync(string owner, string phase, Func<Task> work) =>
        MeasureAsync(owner, phase, async () => { await work(); return true; });

    public static void Record(string owner, string phase, TimeSpan elapsed)
    {
        if (Path is null) return;
        var line = string.Create(CultureInfo.InvariantCulture, $"{owner}\t{phase}\t{elapsed.TotalMilliseconds:F1}\n");
        lock (Gate) File.AppendAllText(Path, line);
    }
}
