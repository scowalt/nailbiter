using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ListingCheck;

public sealed record CheckStart(DateTimeOffset AtUtc, long Timestamp, double? LoadAverage1m)
{
    public static CheckStart Now() => new(DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), HostInfo.LoadAverage1m());
}

public sealed record HostInfo(double? LoadAverage1mBefore, double? LoadAverage1mAfter, int ProcessorCount)
{
    public static double? LoadAverage1m() =>
        File.Exists("/proc/loadavg") && double.TryParse(File.ReadAllText("/proc/loadavg").Split(' ')[0], NumberStyles.Float,
            CultureInfo.InvariantCulture, out var load) ? load : null;
}

public sealed record ReportConfig(int Samples, int Warmup, double TtfbBudgetMs, double CompletionBudgetMs, double RequestTimeoutMs,
    IReadOnlyList<string> Paths);

public sealed record CheckReport(string Schema, CheckMode Mode, string Outcome, int ExitCode, DateTimeOffset StartedAtUtc, double DurationMs,
    string Target, ReportConfig Config, HostInfo Host, IReadOnlyList<PageSummary> Pages, IReadOnlyList<Sample> Samples,
    IReadOnlyList<Failure> Failures, object? Fixture)
{
    private const int PrintedFailures = 20;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) }
    };

    public static CheckReport Create(CheckStart start, CheckMode mode, CheckOptions options, string target, ProbeResult probe,
        IEnumerable<Failure> fixtureFailures, object? fixture)
    {
        var failures = probe.Failures.Concat(fixtureFailures).ToList();
        FailureKind? top = failures.Count == 0 ? null : failures.Min(failure => failure.Kind);
        return new("nailbiter.listing-check/1", mode, top is { } kind ? Failure.Name(kind) : "pass", top is { } code ? (int)code : 0,
            start.AtUtc, Math.Round(Stopwatch.GetElapsedTime(start.Timestamp).TotalMilliseconds, 1), target,
            new(options.Samples, options.Warmup, options.TtfbBudget.TotalMilliseconds, options.CompletionBudget.TotalMilliseconds,
                options.RequestTimeout.TotalMilliseconds, options.Paths),
            new(start.LoadAverage1m, HostInfo.LoadAverage1m(), Environment.ProcessorCount),
            probe.Pages, probe.Samples, failures, fixture);
    }

    public async Task WriteAsync(TextWriter output, string? reportPath)
    {
        var width = Math.Max(24, Pages.Max(page => page.Path.Length) + 2);
        await output.WriteLineAsync($"{"path".PadRight(width)}{"samples",-9}{"ttfb med/max ms",-20}{"completion med/max ms",-24}{"budget",-8}failures");
        foreach (var page in Pages)
        {
            var count = Samples.Count(sample => sample.Path == page.Path);
            var failed = Failures.Count(failure => failure.Path == page.Path);
            var budget = page.WithinBudget ? "ok"
                : Failures.Any(failure => failure.Path == page.Path && failure.Kind == FailureKind.Budget) ? "over" : "-";
            await output.WriteLineAsync(
                $"{page.Path.PadRight(width)}{$"{page.Measured}+{count - page.Measured}w",-9}{Format(page.Ttfb),-20}{Format(page.Completion),-24}{budget,-8}{failed}");
        }
        foreach (var failure in Failures.Take(PrintedFailures))
            await output.WriteLineAsync($"{Failure.Name(failure.Kind)} {failure.Path}: {failure.Message}");
        if (Failures.Count > PrintedFailures) await output.WriteLineAsync($"... {Failures.Count - PrintedFailures} more failures in the report");
        await output.WriteLineAsync($"outcome: {Outcome} (exit {ExitCode}) in {DurationMs:0} ms, load {Host.LoadAverage1mBefore?.ToString(CultureInfo.InvariantCulture) ?? "?"} -> {Host.LoadAverage1mAfter?.ToString(CultureInfo.InvariantCulture) ?? "?"}");
        if (reportPath is not null) await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(this, Json));
    }

    private static string Format(Stats? stats) =>
        stats is null ? "-" : string.Create(CultureInfo.InvariantCulture, $"{stats.Median:0.0} / {stats.Max:0.0}");
}

public static class ListingCheckCommand
{
    public const string Usage = "usage: listing-check url --base-url <origin> [--path <listing path>]... --ttfb-budget-ms <ms> "
        + "--completion-budget-ms <ms> [--samples 1-5] [--warmup 0-2] [--request-timeout-ms <ms>] [--report <file>] [--allow-remote-host]";

    public static async Task<int> RunUrlAsync(IReadOnlyList<string> args, TextWriter output, CancellationToken cancellationToken)
    {
        if (!CheckOptions.TryParse(CheckMode.Url, args, out var options, out var failure)) return await RejectAsync(output, failure);
        var start = CheckStart.Now();
        using var client = ListingProbe.CreateClient();
        var probe = await ListingProbe.RunAsync(client, options.BaseUrl!, options, null, cancellationToken);
        var report = CheckReport.Create(start, CheckMode.Url, options, options.BaseUrl!.GetLeftPart(UriPartial.Authority), probe, [], null);
        await report.WriteAsync(output, options.ReportPath);
        return report.ExitCode;
    }

    public static async Task<int> RunAsync(Func<CancellationToken, Task<int>> run, TextWriter error, CancellationTokenSource cancellation)
    {
        return await run(cancellation.Token);
    }

    public static async Task<int> RejectAsync(TextWriter output, Failure failure)
    {
        await output.WriteLineAsync($"{Failure.Name(failure.Kind)}: {failure.Message}");
        return (int)failure.Kind;
    }
}
