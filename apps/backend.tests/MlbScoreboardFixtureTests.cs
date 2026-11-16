using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using backend.Services.Espn;
using Microsoft.Extensions.Logging;
using Microsoft.FSharp.Core;
using Xunit;

namespace backend.tests;

public sealed class MlbScoreboardFixtureTests
{
    private static readonly string[] Sides = ["home", "away"];

    private static JsonObject Fixture() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "MlbScoreboard", "espn-basic.json")))!.AsObject();

    private static JsonObject Contests(JsonObject fixture) => fixture["contests"]!.AsObject();

    private static IEnumerable<JsonObject> Observations(JsonObject fixture) => fixture["observations"]!.AsArray().Select(o => o!.AsObject());

    private static IEnumerable<JsonObject> Boards(JsonObject observation) => observation["boards"]!.AsArray().Select(b => b!.AsObject());

    private static JsonObject Board(JsonObject fixture, string observation, string date) =>
        Boards(Observations(fixture).Single(o => (string)o["id"]! == observation)).Single(b => (string)b["date"]! == date);

    private sealed class SyntheticEspn(IReadOnlyDictionary<string, string> routes) : HttpMessageHandler
    {
        public List<Uri> Served { get; } = [];
        public List<Uri> Denied { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (request.Method != HttpMethod.Get || !routes.TryGetValue(uri.AbsoluteUri, out var body))
            {
                Denied.Add(uri);
                throw new InvalidOperationException($"Unexpected outbound request {request.Method} {uri}");
            }
            Served.Add(uri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class RecordingLogger : ILogger<EspnOddsService>
    {
        public List<string> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) Entries.Add($"{logLevel}: {formatter(state, exception)} {exception?.Message}");
        }
    }

    private sealed record Fetched(EspnScoreboardResponse? Scoreboard, SyntheticEspn Provider, RecordingLogger Log);

    private static async Task<Fetched> FetchAsync(JsonObject board, Action<JsonNode>? corrupt = null, string? date = null)
    {
        var body = board["body"]!.DeepClone();
        corrupt?.Invoke(body);
        var provider = new SyntheticEspn(new Dictionary<string, string> { [(string)board["url"]!] = body.ToJsonString() });
        var log = new RecordingLogger();
        using var http = new HttpClient(provider);
        var service = new EspnOddsService(http, log, null!, null!, null!);
        var scoreboard = await service.FetchScoreboardAsync("mlb", FSharpOption<string>.Some(date ?? (string)board["date"]!), FSharpOption<CancellationToken>.None);
        return new Fetched(scoreboard, provider, log);
    }

    private static JsonElement? Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;

    private static IReadOnlyList<string> Mismatches(EspnScoreboardResponse? scoreboard, JsonObject contests, JsonObject expected)
    {
        if (scoreboard is null) return ["scoreboard was not deserialized"];
        var problems = new List<string>();
        var ids = scoreboard.Events.Select(e => e.Id).ToList();
        if (!ids.Order().SequenceEqual(expected.Select(e => e.Key).Order())) problems.Add($"events [{string.Join(",", ids)}] differ from the expected contests");
        var rawEvents = Property(scoreboard.RawJson, "events")?.EnumerateArray().ToList() ?? [];
        foreach (var (id, node) in expected)
        {
            var want = node!.AsObject();
            var contest = contests[id]!.AsObject();
            var matches = scoreboard.Events.Where(e => e.Id == id).ToList();
            if (matches.Count != 1) { problems.Add($"{id}: {matches.Count} events"); continue; }
            var evt = matches[0];
            if (evt.Competitions is not [var competition]) { problems.Add($"{id}: competition nesting"); continue; }
            if (competition.Id != (string)contest["competitionId"]!) problems.Add($"{id}: competition id {competition.Id}");
            var raw = rawEvents.Single(e => Property(e, "id")?.GetString() == id);
            var rawCompetition = Property(raw, "competitions")!.Value.EnumerateArray().First();
            foreach (var (label, status, rawOwner) in new[] { ("event", evt.Status, raw), ("competition", competition.Status, rawCompetition) })
            {
                if (status?.Type is null) { problems.Add($"{id}: {label} status nesting"); continue; }
                if (status.Type.Name != (string)want["name"]! || status.Type.State != (string)want["state"]! || status.Type.Completed != (bool)want["completed"]!)
                    problems.Add($"{id}: {label} status {status.Type.Name}/{status.Type.State}/{status.Type.Completed}");
                if (status.Period != (int)want["period"]!) problems.Add($"{id}: {label} period {status.Period}");
                var detail = Property(rawOwner, "status") is { } rawStatus && Property(rawStatus, "type") is { } rawType ? Property(rawType, "detail")?.GetString() : null;
                if (detail != (string)want["detail"]!) problems.Add($"{id}: {label} detail {detail}");
            }
            var rawCompetitors = Property(rawCompetition, "competitors")?.EnumerateArray().ToList() ?? [];
            foreach (var side in Sides)
            {
                var team = contest[side]!.AsObject();
                var line = want[side]!.AsObject();
                var typed = competition.Competitors?.Where(c => c.HomeAway == side).ToList() ?? [];
                if (typed is not [var competitor]) { problems.Add($"{id}: {typed.Count} {side} competitors"); continue; }
                if (competitor.Id != (string)team["id"]! || competitor.Team?.Id != (string)team["id"]! || competitor.Team.Abbreviation != (string)team["abbreviation"]!)
                    problems.Add($"{id}: {side} team {competitor.Id}/{competitor.Team?.Id}/{competitor.Team?.Abbreviation}");
                if (competitor.Score != line["runs"]!.ToJsonString()) problems.Add($"{id}: {side} runs {competitor.Score}");
                var rawSide = rawCompetitors.Single(c => Property(c, "homeAway")?.GetString() == side);
                foreach (var count in new[] { "hits", "errors" })
                {
                    var actual = Property(rawSide, count)?.GetRawText();
                    if (actual != line[count]?.ToJsonString()) problems.Add($"{id}: {side} {count} {actual ?? "absent"}");
                }
                var innings = Property(rawSide, "linescores")?.EnumerateArray()
                    .Select(inning => $"{Property(inning, "period")?.GetRawText()}:{Property(inning, "displayValue")?.GetString()}:{Property(inning, "value")?.GetDouble()}").ToList();
                var wanted = line["innings"]?.AsArray().Select((runs, i) => $"{i + 1}:{runs}:{(int)runs!}").ToList();
                if (innings is null != wanted is null || innings is not null && !innings.SequenceEqual(wanted!))
                    problems.Add($"{id}: {side} innings {(innings is null ? "absent" : string.Join(",", innings))}");
            }
        }
        return problems;
    }

    [Fact]
    public async Task Every_observation_deserializes_through_the_scoreboard_fetch_with_its_authored_identities_sides_statuses_and_lines()
    {
        var fixture = Fixture();
        var served = 0;
        foreach (var observation in Observations(fixture))
        foreach (var board in Boards(observation))
        {
            var fetched = await FetchAsync(board);
            Assert.Empty(fetched.Provider.Denied);
            Assert.Equal([(string)board["url"]!], fetched.Provider.Served.Select(uri => uri.AbsoluteUri));
            Assert.Empty(fetched.Log.Entries);
            Assert.Empty(Mismatches(fetched.Scoreboard, Contests(fixture), board["expected"]!.AsObject()));
            served++;
        }
        Assert.Equal(12, served);
    }

    [Fact]
    public async Task A_request_outside_the_authored_routes_is_denied_and_yields_no_scoreboard()
    {
        var fixture = Fixture();
        var fetched = await FetchAsync(Board(fixture, "opening", "20270710"), date: "20270712");
        Assert.Null(fetched.Scoreboard);
        Assert.Equal(["https://site.web.api.espn.com/apis/site/v2/sports/baseball/mlb/scoreboard?dates=20270712"], fetched.Provider.Denied.Select(uri => uri.AbsoluteUri));
        Assert.Empty(fetched.Provider.Served);
    }

    [Fact]
    public async Task Fetched_boards_keep_doubleheader_makeup_and_resumption_identities()
    {
        var fixture = Fixture();
        async Task<EspnEvent[]> Events(string observation, string date) => [.. (await FetchAsync(Board(fixture, observation, date))).Scoreboard!.Events];
        static (string, string, string) Facts(EspnEvent evt) => (evt.Competitions.Single().Id, evt.Competitions[0].Status.Type.Name, Teams(evt));
        static string Teams(EspnEvent evt) => string.Join(",", evt.Competitions[0].Competitors.OrderBy(c => c.HomeAway).Select(c => $"{c.HomeAway}:{c.Team.Id}"));
        var opening = await Events("opening", "20270710");
        var resumedDay = await Events("resumption", "20270711");
        var resumedPrior = await Events("resumption", "20270710");
        EspnEvent Find(EspnEvent[] events, string id) => events.Single(e => e.Id == id);

        var (first, second) = (Find(opening, "991300103"), Find(opening, "991300104"));
        Assert.Equal(Teams(first), Teams(second));
        Assert.NotEqual(first.Competitions[0].Id, second.Competitions[0].Id);

        Assert.Equal(("991300105", "STATUS_POSTPONED", Teams(Find(resumedPrior, "991300105"))), Facts(Find(resumedPrior, "991300105")));
        Assert.Equal(("991300106", "STATUS_FINAL", Teams(Find(resumedPrior, "991300105"))), Facts(Find(resumedDay, "991300106")));

        var suspended = Find(opening, "991300109");
        Assert.Equal(("991300109", "STATUS_SUSPENDED", Teams(suspended)), Facts(suspended));
        Assert.Equal(("991300109", "STATUS_FINAL", Teams(suspended)), Facts(Find(resumedDay, "991300109")));
        Assert.DoesNotContain(resumedPrior, e => e.Id == "991300109");
    }

    private static JsonObject Event(JsonNode body, string id) =>
        body["events"]!.AsArray().Single(e => (string)e!["id"]! == id)!.AsObject();

    private static JsonObject Competition(JsonNode body, string id) => Event(body, id)["competitions"]![0]!.AsObject();

    private static JsonObject Competitor(JsonNode body, string id, string side) =>
        Competition(body, id)["competitors"]!.AsArray().Single(c => (string)c!["homeAway"]! == side)!.AsObject();

    public static TheoryData<string, string, string, string> Corruptions => new()
    {
        { "unexpected-event", "opening", "20270710", "events [991300101,991300102,991300103,991300104,991300105,991300107,991300108,991300109,991300198] differ from the expected contests" },
        { "event-identity", "opening", "20270710", "991300104: 0 events" },
        { "doubleheader-collapsed", "opening", "20270710", "991300103: 2 events" },
        { "competition-identity", "opening", "20270710", "991300103: competition id 991300104" },
        { "team-identity", "opening", "20270710", "991300104: home team 9121/9121/ATL" },
        { "competitor-identity", "opening", "20270710", "991300104: home team 9103/9121/PHI" },
        { "team-id", "opening", "20270710", "991300104: away team 9103/9121/ATL" },
        { "swapped-sides", "opening", "20270710", "991300102: home team 9117/9117/MIL" },
        { "missing-side", "opening", "20270710", "991300108: 0 away competitors" },
        { "flattened-competitors", "opening", "20270710", "991300101: 0 home competitors" },
        { "missing-competition", "opening", "20270710", "991300107: competition nesting" },
        { "flattened-status", "opening", "20270710", "991300105: competition status nesting" },
        { "postponed-named-final", "opening", "20270710", "991300105: competition status STATUS_FINAL/post/False" },
        { "postponed-completed", "opening", "20270710", "991300105: competition status STATUS_POSTPONED/post/True" },
        { "delay-ended", "opening", "20270710", "991300108: competition status STATUS_RAIN_DELAY/post/False" },
        { "canceled-as-scheduled", "opening", "20270710", "991300107: competition status STATUS_SCHEDULED/pre/False" },
        { "scheduled-in-progress", "opening", "20270711", "991300106: competition status STATUS_SCHEDULED/in/False" },
        { "final-not-completed", "opening", "20270710", "991300101: competition status STATUS_FINAL/post/False" },
        { "suspended-as-delayed", "opening", "20270710", "991300109: event status STATUS_RAIN_DELAY/in/False" },
        { "extra-innings-as-nine", "opening", "20270710", "991300102: event period 9" },
        { "detail", "opening", "20270710", "991300102: competition detail Final" },
        { "event-detail", "opening", "20270710", "991300109: event detail Final" },
        { "runs", "opening", "20270710", "991300103: home runs 2" },
        { "zero-inning-removed", "opening", "20270710", "991300101: away innings 1:0:0,2:1:1,3:0:0,4:0:0,5:2:2,6:0:0,7:0:0,8:0:0" },
        { "unplayed-inning-as-zero", "opening", "20270710", "991300101: home innings 1:1:1,2:0:0,3:2:2,4:0:0,5:0:0,6:1:1,7:0:0,8:0:0,9:0:0" },
        { "absent-lines-as-empty", "partial", "20270710", "991300104: home innings " },
        { "absent-hits-as-zero", "partial", "20270710", "991300104: away hits 0" },
        { "zero-errors-absent", "opening", "20270710", "991300101: home errors absent" },
        { "makeup-merged", "resumption", "20270711", "991300106: 0 events" },
        { "resumption-reidentified", "resumption", "20270711", "991300109: 0 events" },
    };

    private static readonly Dictionary<string, Action<JsonNode>> Corrupt = new()
    {
        ["unexpected-event"] = body =>
        {
            var extra = Event(body, "991300103").DeepClone().AsObject();
            extra["id"] = "991300198";
            body["events"]!.AsArray().Add(extra);
        },
        ["event-identity"] = body => Event(body, "991300104")["id"] = "991300199",
        ["doubleheader-collapsed"] = body => Event(body, "991300104")["id"] = "991300103",
        ["competition-identity"] = body => Competition(body, "991300103")["id"] = "991300104",
        ["team-identity"] = body => Competitor(body, "991300104", "home")["team"]!["abbreviation"] = "ATL",
        ["competitor-identity"] = body => Competitor(body, "991300104", "home")["id"] = "9103",
        ["team-id"] = body => Competitor(body, "991300104", "away")["team"]!["id"] = "9121",
        ["swapped-sides"] = body =>
        {
            var (home, away) = (Competitor(body, "991300102", "home"), Competitor(body, "991300102", "away"));
            (home["homeAway"], away["homeAway"]) = ("away", "home");
        },
        ["missing-side"] = body => Competitor(body, "991300108", "away").Remove("homeAway"),
        ["flattened-competitors"] = body =>
        {
            var competition = Competition(body, "991300101");
            var competitors = competition["competitors"]!;
            competition.Remove("competitors");
            Event(body, "991300101")["competitors"] = competitors;
        },
        ["missing-competition"] = body => Event(body, "991300107")["competitions"] = new JsonArray(),
        ["flattened-status"] = body =>
        {
            var status = Competition(body, "991300105")["status"]!.AsObject();
            var type = status["type"]!.AsObject();
            status.Remove("type");
            foreach (var (key, value) in type.ToList()) { type.Remove(key); status[key] = value; }
        },
        ["postponed-named-final"] = body => Competition(body, "991300105")["status"]!["type"]!["name"] = "STATUS_FINAL",
        ["postponed-completed"] = body => Competition(body, "991300105")["status"]!["type"]!["completed"] = true,
        ["delay-ended"] = body => Competition(body, "991300108")["status"]!["type"]!["state"] = "post",
        ["canceled-as-scheduled"] = body =>
        {
            var type = Competition(body, "991300107")["status"]!["type"]!;
            (type["name"], type["state"]) = ("STATUS_SCHEDULED", "pre");
        },
        ["scheduled-in-progress"] = body => Competition(body, "991300106")["status"]!["type"]!["state"] = "in",
        ["final-not-completed"] = body => Competition(body, "991300101")["status"]!["type"]!["completed"] = false,
        ["suspended-as-delayed"] = body =>
        {
            var type = Event(body, "991300109")["status"]!["type"]!;
            (type["name"], type["state"]) = ("STATUS_RAIN_DELAY", "in");
        },
        ["extra-innings-as-nine"] = body => Event(body, "991300102")["status"]!["period"] = 9,
        ["detail"] = body => Competition(body, "991300102")["status"]!["type"]!["detail"] = "Final",
        ["event-detail"] = body => Event(body, "991300109")["status"]!["type"]!["detail"] = "Final",
        ["runs"] = body => Competitor(body, "991300103", "home")["score"] = "2",
        ["zero-inning-removed"] = body => Competitor(body, "991300101", "away")["linescores"]!.AsArray().RemoveAt(8),
        ["unplayed-inning-as-zero"] = body =>
            Competitor(body, "991300101", "home")["linescores"]!.AsArray().Add(JsonNode.Parse("""{"value":0.0,"displayValue":"0","period":9}""")),
        ["absent-lines-as-empty"] = body => Competitor(body, "991300104", "home")["linescores"] = new JsonArray(),
        ["absent-hits-as-zero"] = body => Competitor(body, "991300104", "away")["hits"] = 0,
        ["zero-errors-absent"] = body => Competitor(body, "991300101", "home").Remove("errors"),
        ["makeup-merged"] = body => Event(body, "991300106")["id"] = "991300105",
        ["resumption-reidentified"] = body => Event(body, "991300109")["id"] = "991300199",
    };

    [Theory]
    [MemberData(nameof(Corruptions))]
    public async Task A_corrupted_identity_side_nesting_status_or_line_fact_is_reported(string corruption, string observation, string date, string expected)
    {
        var fixture = Fixture();
        var board = Board(fixture, observation, date);
        var fetched = await FetchAsync(board, Corrupt[corruption]);
        Assert.Empty(fetched.Provider.Denied);
        Assert.Contains(expected, Mismatches(fetched.Scoreboard, Contests(fixture), board["expected"]!.AsObject()));
    }
}
