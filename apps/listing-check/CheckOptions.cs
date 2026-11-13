using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace ListingCheck;

public enum FailureKind { InvalidConfig = 2, Request = 3, Deadline = 4, Content = 5, QueryWork = 6, Budget = 7 }

public enum CheckMode { Url, Fixture }

public sealed record Failure(FailureKind Kind, string? Path, string Message)
{
    public static string Name(FailureKind kind) => JsonNamingPolicy.KebabCaseLower.ConvertName(kind.ToString());
}

public sealed record CheckOptions(Uri? BaseUrl, IReadOnlyList<string> Paths, int Samples, int Warmup, TimeSpan TtfbBudget,
    TimeSpan CompletionBudget, TimeSpan RequestTimeout, string? ReportPath, bool AllowRemoteHost)
{
    private const int MaxTimeoutMs = 60_000;
    private const int MaxPaths = 6;
    private static readonly string[] SharedFlags =
        ["--samples", "--warmup", "--ttfb-budget-ms", "--completion-budget-ms", "--request-timeout-ms", "--report"];

    private static readonly Dictionary<CheckMode, (int Samples, int MaxSamples, int Warmup, int MaxWarmup, string[] Flags)> Modes = new()
    {
        [CheckMode.Url] = (3, 5, 1, 2, ["--base-url", "--path", "--allow-remote-host", .. SharedFlags]),
        [CheckMode.Fixture] = (10, 100, 2, 20, SharedFlags)
    };

    public static bool TryParse(CheckMode mode, IReadOnlyList<string> args,
        [NotNullWhen(true)] out CheckOptions? options, [NotNullWhen(false)] out Failure? failure)
    {
        var (parsed, error) = Read(mode, args);
        options = parsed;
        failure = parsed is null ? new Failure(FailureKind.InvalidConfig, null, error!) : null;
        return parsed is not null;
    }

    private static (CheckOptions?, string?) Read(CheckMode mode, IReadOnlyList<string> args)
    {
        static (CheckOptions?, string?) Fail(string message) => (null, message);
        var limits = Modes[mode];
        var values = new Dictionary<string, string>();
        var paths = new List<string>();
        var allowRemoteHost = false;
        for (var i = 0; i < args.Count; i++)
        {
            var flag = args[i];
            if (!limits.Flags.Contains(flag)) return Fail($"unknown option '{flag}' for {Name(mode)} mode");
            if (flag == "--allow-remote-host")
            {
                if (allowRemoteHost) return Fail($"{flag} given twice");
                allowRemoteHost = true;
                continue;
            }
            if (i + 1 == args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal)) return Fail($"{flag} needs a value");
            var value = args[++i];
            if (flag == "--path") paths.Add(value);
            else if (!values.TryAdd(flag, value)) return Fail($"{flag} given twice");
        }

        var numbers = new Dictionary<string, int>();
        foreach (var (flag, fallback, min, max) in new (string, int?, int, int)[]
        {
            ("--samples", limits.Samples, 1, limits.MaxSamples),
            ("--warmup", limits.Warmup, 0, limits.MaxWarmup),
            ("--ttfb-budget-ms", null, 1, MaxTimeoutMs),
            ("--completion-budget-ms", null, 1, MaxTimeoutMs),
            ("--request-timeout-ms", 10_000, 1, MaxTimeoutMs)
        })
        {
            if (!values.TryGetValue(flag, out var text))
            {
                if (fallback is null) return Fail($"{flag} is required");
                numbers[flag] = fallback.Value;
            }
            else if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= min && number <= max)
                numbers[flag] = number;
            else return Fail($"{flag} must be a whole number from {min} to {max}");
        }
        if (numbers["--ttfb-budget-ms"] > numbers["--completion-budget-ms"]) return Fail("--ttfb-budget-ms must not exceed --completion-budget-ms");
        if (numbers["--request-timeout-ms"] < numbers["--completion-budget-ms"]) return Fail("--request-timeout-ms must be at least --completion-budget-ms");

        Uri? baseUrl = null;
        if (mode == CheckMode.Url)
        {
            if (!values.TryGetValue("--base-url", out var text)) return Fail("--base-url is required");
            if (!Uri.TryCreate(text, UriKind.Absolute, out baseUrl) || baseUrl.Scheme is not ("http" or "https")
                || baseUrl.UserInfo.Length > 0 || text.Contains('?') || text.Contains('#') || baseUrl.AbsolutePath != "/")
                return Fail("--base-url must be an http or https origin without credentials, path, query or fragment");
            if (!baseUrl.IsLoopback && !allowRemoteHost) return Fail($"{baseUrl.Host} is not a loopback host; pass --allow-remote-host to check it");
            if (paths.Count == 0) paths.Add("/");
            if (paths.Count > MaxPaths) return Fail($"at most {MaxPaths} --path values are allowed");
            if (paths.FirstOrDefault(path => !ListingRoute.IsListing(path)) is { } other) return Fail($"'{other}' is not a listing path");
            if (paths.Distinct().Count() < paths.Count) return Fail("--path values must be distinct");
        }

        return (new CheckOptions(baseUrl, paths, numbers["--samples"], numbers["--warmup"],
            TimeSpan.FromMilliseconds(numbers["--ttfb-budget-ms"]), TimeSpan.FromMilliseconds(numbers["--completion-budget-ms"]),
            TimeSpan.FromMilliseconds(numbers["--request-timeout-ms"]), values.GetValueOrDefault("--report"), allowRemoteHost), null);
    }

    private static string Name(CheckMode mode) => JsonNamingPolicy.KebabCaseLower.ConvertName(mode.ToString());
}

public static class ListingRoute
{
    private enum Kind { Home, League }

    private static readonly Dictionary<string, Kind> Routes = new()
    {
        ["/"] = Kind.Home, ["/nba"] = Kind.League, ["/nfl"] = Kind.League, ["/nhl"] = Kind.League, ["/mlb"] = Kind.League, ["/cfb"] = Kind.League
    };

    private static readonly Dictionary<Kind, (string Page, string[] Listing)> Markers = new()
    {
        [Kind.Home] = ("home-schedule page", ["home-section", "empty-state-text"]),
        [Kind.League] = ("league-title", ["league-games", "empty-state-text"])
    };

    public static bool IsListing(string path)
    {
        var parts = path.Split('?', 2);
        return Routes.ContainsKey(parts[0]) && (parts.Length == 1 || parts[1].StartsWith("date=", StringComparison.Ordinal)
            && DateOnly.TryParseExact(parts[1][5..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
    }

    public static IReadOnlyList<string> ContentProblems(string path, string body)
    {
        var (page, listing) = Markers[Routes[path.Split('?', 2)[0]]];
        var problems = new List<string>();
        if (!body.TrimStart().StartsWith("<!DOCTYPE html>", StringComparison.OrdinalIgnoreCase)) problems.Add("no doctype");
        if (!body.Contains("</html>", StringComparison.OrdinalIgnoreCase)) problems.Add("truncated: no closing html tag");
        if (!body.Contains(page, StringComparison.Ordinal)) problems.Add($"no {page}");
        if (!listing.Any(marker => body.Contains(marker, StringComparison.Ordinal))) problems.Add($"no {string.Join(" or ", listing)}");
        return problems;
    }
}
