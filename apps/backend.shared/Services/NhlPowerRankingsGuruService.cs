using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp;
using backend.Data;
using backend.Models;
using backend.Scoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace backend.Services;

public sealed class NhlPowerRankingsGuruService(HttpClient http, IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<NhlPowerRankingsGuruService> logger)
{
    public const string Source = "powerrankingsguru_nhl";
    public const string RankingsUrl = "https://powerrankingsguru.com/nhl/power-rankings";
    public const int MaxPublicationAgeHours = 48;
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly IReadOnlyDictionary<string, string> Teams = new Dictionary<string, string>
    {
        ["anaheim-ducks"] = "ANA", ["boston-bruins"] = "BOS", ["buffalo-sabres"] = "BUF", ["calgary-flames"] = "CGY",
        ["carolina-hurricanes"] = "CAR", ["chicago-blackhawks"] = "CHI", ["colorado-avalanche"] = "COL", ["columbus-blue-jackets"] = "CBJ",
        ["dallas-stars"] = "DAL", ["detroit-red-wings"] = "DET", ["edmonton-oilers"] = "EDM", ["florida-panthers"] = "FLA",
        ["los-angeles-kings"] = "LA", ["minnesota-wild"] = "MIN", ["montreal-canadiens"] = "MTL", ["nashville-predators"] = "NSH",
        ["new-jersey-devils"] = "NJ", ["new-york-islanders"] = "NYI", ["new-york-rangers"] = "NYR", ["ottawa-senators"] = "OTT",
        ["philadelphia-flyers"] = "PHI", ["pittsburgh-penguins"] = "PIT", ["san-jose-sharks"] = "SJ", ["seattle-kraken"] = "SEA",
        ["st-louis-blues"] = "STL", ["tampa-bay-lightning"] = "TB", ["toronto-maple-leafs"] = "TOR", ["utah-mammoth"] = "UTAH",
        ["vancouver-canucks"] = "VAN", ["vegas-golden-knights"] = "VGK", ["washington-capitals"] = "WSH", ["winnipeg-jets"] = "WPG"
    };
    private sealed record Snapshot(int Season, DateTime PublishedAt, Dictionary<string, int> Ranks);

    public static bool ValidPublication(DateTime publishedAt, DateTime fetchedAt, int season) => publishedAt <= fetchedAt
        && MoneyPuckRankings.SeasonAtPublication(publishedAt) == season;

    public async Task<int> RefreshAsync(CancellationToken ct = default)
    {
        var observed = clock.GetUtcNow().UtcDateTime;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        var claimed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO nhl_guru_refresh_state (id, next_attempt_at_ticks) VALUES (1, {observed.AddDays(1).Ticks})
            ON CONFLICT (id) DO UPDATE SET next_attempt_at_ticks = EXCLUDED.next_attempt_at_ticks
            WHERE nhl_guru_refresh_state.next_attempt_at_ticks <= {observed.Ticks}
            """, ct);
        if (claimed != 1) return 0;
        try
        {
            using var response = await http.GetAsync(RankingsUrl, ct);
            var retryAt = response.Headers.RetryAfter?.Date?.UtcDateTime
                ?? (response.Headers.RetryAfter?.Delta is TimeSpan delay ? clock.GetUtcNow().UtcDateTime.Add(delay) : observed);
            if (retryAt > observed.AddDays(1))
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE nhl_guru_refresh_state SET next_attempt_at_ticks = GREATEST(next_attempt_at_ticks, {retryAt.Ticks}) WHERE id = 1", ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("NHL Power Rankings Guru returned {Status}; collection stopped", response.StatusCode);
                return 0;
            }
            var html = await response.Content.ReadAsStringAsync(ct);
            if (Regex.IsMatch(html, @"cf-chl-|challenge-platform|<title>\s*Just a moment|id=[""']challenge-form", RegexOptions.IgnoreCase))
            {
                logger.LogWarning("NHL Power Rankings Guru access challenge; collection stopped");
                return 0;
            }
            var snapshot = await ParseAsync(html, observed, ct);
            var previous = await db.TeamRatings.Where(r => r.Sport == "nhl" && r.Source == Source).ToListAsync(ct);
            if (previous.Any(r => r.FetchedAt > observed)) return 0;
            if (snapshot is not null && previous.Any(r => r.Season == snapshot.Season && r.RankPublishedAt > snapshot.PublishedAt)) snapshot = null;
            if (snapshot is null)
            {
                foreach (var row in previous)
                {
                    row.PowerRank = null;
                    row.SourceSeason = null;
                    row.RankPublishedAt = null;
                    row.FetchedAt = observed;
                }
                await db.SaveChangesAsync(ct);
                logger.LogWarning("Rejected NHL Power Rankings Guru ranking batch");
                return 0;
            }
            foreach (var (team, rank) in snapshot.Ranks)
            {
                var row = previous.SingleOrDefault(r => r.Season == snapshot.Season && r.TeamAbbreviation == team);
                if (row is null)
                {
                    row = new TeamRating { Sport = "nhl", Source = Source, Season = snapshot.Season, TeamAbbreviation = team };
                    db.TeamRatings.Add(row);
                }
                row.PowerRank = rank;
                row.SourceSeason = snapshot.Season;
                row.RankPublishedAt = snapshot.PublishedAt;
                row.FetchedAt = observed;
            }
            await db.SaveChangesAsync(ct);
            var eligible = ProHypePolicy.Fresh(snapshot.PublishedAt, observed, MaxPublicationAgeHours);
            logger.LogInformation("Stored NHL Power Rankings Guru rankings for {Count} teams, season {Season}, publication {Publication}; eligible {Eligible}",
                snapshot.Ranks.Count, snapshot.Season, snapshot.PublishedAt, eligible);
            return eligible ? snapshot.Ranks.Count : 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            logger.LogWarning(ex, "NHL Power Rankings Guru collection interrupted; original observations preserved");
            return 0;
        }
    }

    private static async Task<Snapshot?> ParseAsync(string html, DateTime observed, CancellationToken ct)
    {
        using var context = BrowsingContext.New(Configuration.Default);
        using var document = await context.OpenAsync(request => request.Content(html), ct);
        var sheets = document.QuerySelectorAll("main [data-component='PageSheet']");
        if (sheets.Length != 1) return null;
        var headings = sheets[0].QuerySelectorAll("header h1");
        if (headings.Length != 1) return null;
        var title = Regex.Match(headings[0].TextContent.Trim(), @"^(\d{4})-(\d{2}) NHL Power Rankings$");
        if (!title.Success) return null;
        var start = int.Parse(title.Groups[1].Value, CultureInfo.InvariantCulture);
        var season = start + 1;
        if (int.Parse(title.Groups[2].Value, CultureInfo.InvariantCulture) != season % 100 || ProHypePolicy.TeamCount("nhl", season) != Teams.Count) return null;
        var updated = sheets[0].QuerySelectorAll("header p").Where(p => p.TextContent.Trim().StartsWith("Updated ", StringComparison.Ordinal)).ToArray();
        if (updated.Length != 1 || !DateTime.TryParseExact(updated[0].TextContent.Trim(), "'Updated' MMM d, yyyy, h:mm tt 'ET'",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var local) || Eastern.IsInvalidTime(local) || Eastern.IsAmbiguousTime(local)) return null;
        var published = TimeZoneInfo.ConvertTimeToUtc(local, Eastern);
        if (!ValidPublication(published, observed, season)) return null;
        var tables = sheets[0].QuerySelectorAll("[data-component='PowerRankingsTable'] table");
        if (tables.Length != 1) return null;
        var headers = tables[0].QuerySelectorAll("thead tr:last-child th");
        if (headers.Length < 2 || headers[0].TextContent.Trim() != "Rank" || headers[1].TextContent.Trim() != "Team") return null;
        var rows = tables[0].QuerySelectorAll("tbody tr");
        if (rows.Length != Teams.Count) return null;
        var ranks = new Dictionary<string, int>();
        foreach (var row in rows)
        {
            var cells = row.QuerySelectorAll("td");
            if (cells.Length < 2 || !int.TryParse(cells[0].TextContent.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var rank)
                || rank != ranks.Count + 1) return null;
            var links = cells[1].QuerySelectorAll("a[href]");
            if (links.Length != 1) return null;
            var href = links[0].GetAttribute("href")!;
            const string prefix = "/nhl/teams/";
            if (!href.StartsWith(prefix, StringComparison.Ordinal) || !Teams.TryGetValue(href[prefix.Length..], out var team) || !ranks.TryAdd(team, rank)) return null;
        }
        return new(season, published, ranks);
    }
}
