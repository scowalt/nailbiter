using System.Globalization;
using System.Text.Json;
using backend.Models;

namespace backend.Services.Highlights;

public sealed class MlbHighlightsIdentityClient(IHttpClientFactory clients, TimeProvider time)
{
    public const string HttpClientName = "MlbHighlightsIdentity";
    internal sealed record Team(string Abbreviation, string EspnId, int MlbId, string Nickname);
    internal static readonly Team[] Teams =
    [
        new("BAL", "1", 110, "Orioles"), new("BOS", "2", 111, "Red Sox"), new("LAA", "3", 108, "Angels"),
        new("CHW", "4", 145, "White Sox"), new("CLE", "5", 114, "Guardians"), new("DET", "6", 116, "Tigers"),
        new("KC", "7", 118, "Royals"), new("MIL", "8", 158, "Brewers"), new("MIN", "9", 142, "Twins"),
        new("NYY", "10", 147, "Yankees"), new("ATH", "11", 133, "Athletics"), new("SEA", "12", 136, "Mariners"),
        new("TEX", "13", 140, "Rangers"), new("TOR", "14", 141, "Blue Jays"), new("ATL", "15", 144, "Braves"),
        new("CHC", "16", 112, "Cubs"), new("CIN", "17", 113, "Reds"), new("HOU", "18", 117, "Astros"),
        new("LAD", "19", 119, "Dodgers"), new("WSH", "20", 120, "Nationals"), new("NYM", "21", 121, "Mets"),
        new("PHI", "22", 143, "Phillies"), new("PIT", "23", 134, "Pirates"), new("STL", "24", 138, "Cardinals"),
        new("SD", "25", 135, "Padres"), new("SF", "26", 137, "Giants"), new("COL", "27", 115, "Rockies"),
        new("MIA", "28", 146, "Marlins"), new("ARI", "29", 109, "D-backs"), new("TB", "30", 139, "Rays")
    ];
    internal sealed record Identity(long GamePk, DateOnly OfficialDate, int Season, int HomeTeamId, int AwayTeamId,
        string DoubleHeader, int GameNumber, string? RescheduledFromDate, string? RescheduledFrom,
        string? ResumedFrom, string? ResumedFromDate, string? RescheduledToDate, string? ResumeDate,
        DateTime ScheduledStart, int SameDayContests, DateTime ObservedAt);
    internal sealed record Evidence(Identity? Identity, MlbCompletionEvidence? Completion, bool Definitive, string Reason);

    internal async Task<Evidence> ResolveAsync(Game game, CancellationToken cancellationToken)
    {
        if (game.Sport != "mlb" || game.Status != "final") return new(null, null, true, "NotCompletedMlb");
        try
        {
            var header = (await ReadAsync($"https://site.web.api.espn.com/apis/site/v2/sports/baseball/mlb/summary?event={Uri.EscapeDataString(game.Id)}", cancellationToken)).GetProperty("header");
            var competitions = header.GetProperty("competitions");
            if (header.GetProperty("id").GetString() != game.Id || competitions.GetArrayLength() != 1)
                return new(null, null, true, "ConflictingEvent");
            var competition = competitions[0];
            var status = Status(competition.GetProperty("status").GetProperty("type"));
            var observed = time.GetUtcNow().UtcDateTime;
            var completion = new MlbCompletionEvidence(game.Id, competition.GetProperty("id").GetString(),
                header.TryGetProperty("status", out var headerStatus) ? Status(headerStatus.GetProperty("type")) : status, status, observed);
            if (!completion.ConfirmsCompletion(game.Id)) return new(null, completion, true, "NotActuallyCompleted");
            var home = Teams.SingleOrDefault(t => t.Abbreviation == game.HomeTeamAbbreviation);
            var away = Teams.SingleOrDefault(t => t.Abbreviation == game.AwayTeamAbbreviation);
            if (home is null || away is null || home == away) return new(null, completion, true, "UnknownTeams");
            var competitors = competition.GetProperty("competitors").EnumerateArray().ToArray();
            if (competitors.Length != 2 || !Matches(competitors, "home", home, game.HomeTeamId) || !Matches(competitors, "away", away, game.AwayTeamId))
                return new(null, completion, true, "ConflictingTeams");
            var season = header.GetProperty("season").GetProperty("year").GetInt32();
            var start = DateTimeOffset.Parse(competition.GetProperty("date").GetString()!, CultureInfo.InvariantCulture);
            if (game.SeasonYear != season || start.UtcDateTime != game.ScheduledStart)
                return new(null, completion, true, "ConflictingSeasonOrStart");
            var from = start.UtcDateTime.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var to = start.UtcDateTime.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var schedule = await ReadAsync($"https://statsapi.mlb.com/api/v1/schedule?sportId=1&startDate={from}&endDate={to}", cancellationToken);
            var rows = schedule.GetProperty("dates").EnumerateArray().SelectMany(d => d.GetProperty("games").EnumerateArray()).ToArray();
            var pair = rows.Where(r => r.GetProperty("teams").GetProperty("home").GetProperty("team").GetProperty("id").GetInt32() == home.MlbId
                && r.GetProperty("teams").GetProperty("away").GetProperty("team").GetProperty("id").GetInt32() == away.MlbId
                && int.Parse(r.GetProperty("season").GetString()!, CultureInfo.InvariantCulture) == season).ToArray();
            var exact = pair.Where(r => DateTimeOffset.Parse(r.GetProperty("gameDate").GetString()!, CultureInfo.InvariantCulture) == start).ToArray();
            if (exact.Length != 1) return new(null, completion, true, "AmbiguousSchedule");
            var row = exact[0];
            var mlbStatus = row.GetProperty("status");
            if (mlbStatus.GetProperty("abstractGameState").GetString() != "Final" || mlbStatus.GetProperty("detailedState").GetString() != "Final"
                || mlbStatus.GetProperty("codedGameState").GetString() != "F") return new(null, completion, true, "MlbNotFinal");
            var date = DateOnly.ParseExact(row.GetProperty("officialDate").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var pk = row.GetProperty("gamePk").GetInt64();
            var doubleHeader = row.GetProperty("doubleHeader").GetString();
            var number = row.GetProperty("gameNumber").GetInt32();
            if (pk <= 0 || date.Year != season || doubleHeader is not ("N" or "Y" or "S") || number < 1)
                return new(null, completion, true, "MissingContestContext");
            if (game.MlbGamePk is not null && game.MlbGamePk != pk) return new(null, completion, true, "IdentityChanged");
            return new(new(pk, date, season, home.MlbId, away.MlbId, doubleHeader, number,
                Text(row, "rescheduledFromDate"), Text(row, "rescheduledFrom"), Text(row, "resumedFrom"),
                Text(row, "resumedFromDate"), Text(row, "rescheduledToDate"), Text(row, "resumeDate"), start.UtcDateTime,
                pair.Count(r => r.GetProperty("officialDate").GetString() == date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), observed), completion, true, "Verified");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(null, null, false, "IdentityTemporaryFailure"); }
        catch (Exception e) when (e is HttpRequestException or IOException) { return new(null, null, false, "IdentityTemporaryFailure"); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { return new(null, null, true, "MalformedIdentity"); }
    }

    private static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var field) && field.ValueKind != JsonValueKind.Null ? field.GetString() : null;
    private static EspnCompletionStatus Status(JsonElement value) => new(Text(value, "name"), Text(value, "state"),
        value.TryGetProperty("completed", out var completed) && completed.ValueKind is JsonValueKind.True or JsonValueKind.False ? completed.GetBoolean() : null,
        Text(value, "description"), Text(value, "detail"), Text(value, "shortDetail"));
    private static bool Matches(JsonElement[] competitors, string side, Team team, string? storedId)
    {
        var sideRows = competitors.Where(c => c.GetProperty("homeAway").GetString() == side).ToArray();
        if (sideRows.Length != 1 || storedId is not null && storedId != team.EspnId) return false;
        var evidence = sideRows[0].GetProperty("team");
        return evidence.GetProperty("id").GetString() == team.EspnId && evidence.GetProperty("abbreviation").GetString() == team.Abbreviation;
    }

    private async Task<JsonElement> ReadAsync(string uri, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await clients.CreateClient(HttpClientName).GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int length;
        while ((length = await stream.ReadAsync(bytes, deadline.Token)) != 0)
        {
            if (buffer.Length + length > 2 * 1024 * 1024) throw new JsonException();
            buffer.Write(bytes, 0, length);
        }
        using var json = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        return json.RootElement.Clone();
    }
}
