using System.Diagnostics;
using System.Net;
using System.Text;

namespace ListingCheck;

public sealed record Sample(string Path, string Phase, int Round, int? Status, double? TtfbMs, double? CompletionMs, long? Bytes,
    FailureKind? Failure, string? Detail);

public sealed record Stats(double Min, double Median, double Max)
{
    public static Stats? Of(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) return null;
        var middle = sorted.Length / 2;
        var median = sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
        return new(sorted[0], Math.Round(median, 1), sorted[^1]);
    }
}

public sealed record PageSummary(string Path, int Measured, Stats? Ttfb, Stats? Completion, bool WithinBudget);

public sealed record ProbeResult(IReadOnlyList<Sample> Samples, IReadOnlyList<PageSummary> Pages, IReadOnlyList<Failure> Failures);

public static class ListingProbe
{
    public static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    public static async Task<ProbeResult> RunAsync(HttpClient client, Uri baseUrl, CheckOptions options,
        Func<string, string, IReadOnlyList<string>>? extraCheck, CancellationToken cancellationToken)
    {
        var samples = new List<Sample>();
        for (var round = 0; round < options.Warmup + options.Samples; round++)
            foreach (var path in options.Paths)
                samples.Add(await SendAsync(client, baseUrl, path, round < options.Warmup ? ("warmup", round) : ("measured", round - options.Warmup),
                    options.RequestTimeout, extraCheck, cancellationToken));
        var failures = samples.Where(s => s.Failure is not null)
            .Select(s => new Failure(s.Failure!.Value, s.Path, $"{s.Phase} round {s.Round}: {s.Detail}")).ToList();
        var pages = options.Paths.Select(path => Summarize(path, samples.Where(s => s.Path == path && s.Phase == "measured").ToList(), options, failures)).ToList();
        return new(samples, pages, failures);
    }

    private static PageSummary Summarize(string path, List<Sample> measured, CheckOptions options, List<Failure> failures)
    {
        var succeeded = measured.Where(s => s.Failure is null).ToList();
        var ttfb = Stats.Of(succeeded.Select(s => s.TtfbMs!.Value));
        var completion = Stats.Of(succeeded.Select(s => s.CompletionMs!.Value));
        if (ttfb is null || completion is null || succeeded.Count < measured.Count) return new(path, measured.Count, ttfb, completion, false);
        var over = new[] { ("TTFB", ttfb.Median, options.TtfbBudget), ("completion", completion.Median, options.CompletionBudget) }
            .Where(check => check.Item2 > check.Item3.TotalMilliseconds).ToArray();
        failures.AddRange(over.Select(check =>
            new Failure(FailureKind.Budget, path, $"median {check.Item1} {check.Item2:0.#} ms exceeds {check.Item3.TotalMilliseconds:0} ms")));
        return new(path, measured.Count, ttfb, completion, over.Length == 0);
    }

    private static async Task<Sample> SendAsync(HttpClient client, Uri baseUrl, string path, (string Phase, int Round) at, TimeSpan timeout,
        Func<string, string, IReadOnlyList<string>>? extraCheck, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var started = Stopwatch.GetTimestamp();
        double? ttfb = null;
        int? status = null;
        double Elapsed() => Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1);
        Sample Result(FailureKind? failure, string? detail, double? completion = null, long? bytes = null) =>
            new(path, at.Phase, at.Round, status, ttfb, completion, bytes, failure, detail);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUrl, path));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            ttfb = Elapsed();
            status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
                return Result(FailureKind.Request, status is >= 300 and < 400 ? $"redirect {status} not followed" : $"status {status}");
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType != "text/html") return Result(FailureKind.Request, $"content type {mediaType ?? "missing"}");
            var bytes = await response.Content.ReadAsByteArrayAsync(deadline.Token);
            var completion = Elapsed();
            var body = Encoding.UTF8.GetString(bytes);
            var problems = ListingRoute.ContentProblems(path, body).Concat(extraCheck?.Invoke(path, body) ?? []).ToArray();
            return Result(problems.Length == 0 ? null : FailureKind.Content, problems.Length == 0 ? null : string.Join("; ", problems),
                completion, bytes.LongLength);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result(FailureKind.Deadline, $"no complete response within {timeout.TotalMilliseconds:0} ms");
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            return Result(FailureKind.Request, error.Message);
        }
    }
}
