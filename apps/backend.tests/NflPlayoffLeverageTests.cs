using System.Net;
using System.Text.Json;
using backend.Models;
using backend.Scoring;
using backend.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace backend.tests;

public sealed class NflPlayoffLeverageTests
{
    internal static readonly DateTime Now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
    internal static readonly DateTime Published = new(2026, 10, 16, 10, 5, 0, DateTimeKind.Utc);
    internal static string Fixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "NflPlayoffLeverage", name);
        return File.ReadAllText(path);
    }
    internal static Game Game() => new()
    {
        Id = "controlled-nfl-2026-gb-det", Sport = "nfl", HomeTeam = "Detroit Lions", AwayTeam = "Green Bay Packers",
        HomeTeamAbbreviation = "DET", AwayTeamAbbreviation = "GB", SeasonYear = 2026, SeasonType = 2, WeekNumber = 7,
        ScheduledStart = new DateTime(2026, 10, 18, 17, 0, 0, DateTimeKind.Utc)
    };
    internal static ProCompetitionEvidenceService.Schedule[] Schedules(Func<string, object>? formatScore = null)
    {
        var rows = Fixture("synthetic-espn-controls.csv").Trim().Split('\n').Skip(1).Select(line => line.Split(',')).ToArray();
        return rows.SelectMany(r => new[] { r[1], r[2] }).Distinct().Select(team =>
        {
            var own = rows.Where(r => r[1] == team || r[2] == team).ToArray();
            var final = own.Where(r => r[4] != "").Select(Id).ToArray();
            var events = own.Select(r => JsonSerializer.SerializeToElement(new
            {
                id = Id(r), date = r[3], season = new { year = 2026 }, seasonType = new { type = 2 }, week = new { number = int.Parse(r[0]) },
                competitions = new[] { new
                {
                    status = new { type = new { name = r[4] == "" ? "STATUS_SCHEDULED" : "STATUS_FINAL", state = r[4] == "" ? "pre" : "post", completed = r[4] != "" }, period = r[4] == "" ? 0 : 4 },
                    competitors = new[]
                    {
                        new { id = Array.IndexOf(rows.SelectMany(x => new[] { x[1], x[2] }).Distinct().ToArray(), r[1]).ToString(), homeAway = "home", team = new { abbreviation = r[1] }, score = new { value = formatScore?.Invoke(r[4] == "" ? "0" : r[4]) ?? (r[4] == "" ? "0" : r[4]) } },
                        new { id = Array.IndexOf(rows.SelectMany(x => new[] { x[1], x[2] }).Distinct().ToArray(), r[2]).ToString(), homeAway = "away", team = new { abbreviation = r[2] }, score = new { value = formatScore?.Invoke(r[5] == "" ? "0" : r[5]) ?? (r[5] == "" ? "0" : r[5]) } }
                    }
                } }
            })).ToArray();
            return new ProCompetitionEvidenceService.Schedule(team, "controlled-division", 2026, 2, 17, final.Length, 0, final, events);
        }).ToArray();
        static string Id(string[] row) => $"control-{row[0]}-{row[1]}-{row[2]}";
    }
    internal static ProGameEvidence Context(Game game) => new()
    {
        GameId = game.Id, Sport = game.Sport, Season = game.SeasonYear!.Value, Phase = game.SeasonType,
        Home = game.HomeTeamAbbreviation, Away = game.AwayTeamAbbreviation, ScheduledStart = game.ScheduledStart,
        ObservedAt = Now, Source = "synthetic ESPN controls", VerifiedUnstarted = true,
        CompletedGameIds = Schedules().SelectMany(s => s.CompletedIds).Distinct().ToArray(),
        HomeProgress = new() { Completed = 6, Scheduled = 17, ScheduleVerified = true },
        AwayProgress = new() { Completed = 6, Scheduled = 17, ScheduleVerified = true }
    };
    internal static TeamRating[] Ranks(Game game)
    {
        var rows = ProHypeTests.Ranks(game);
        foreach (var row in rows) row.FetchedAt = Now;
        return rows;
    }
    internal static PregameOdds Odds(Game game)
    {
        var odds = ProHypeTests.Odds(game, .6); odds.FetchedAt = Now; return odds;
    }
    private static ProQualification Q(double? p = null, bool final = false) => new(p, final, final ? Now : null,
        final ? null : Now.AddHours(1), "kalshi", ProQualificationEvidenceKind.Kalshi);
    internal static NflPlayoffLeverageEvidence SyntheticEvidence(Game game, DateTime now, IEnumerable<string>? completed = null, double swing = .5) => new()
    {
        Version = 1, Source = NflPlayoffLeveragePolicy.Source, GameId = game.Id, Season = game.SeasonYear!.Value,
        Home = game.HomeTeamAbbreviation, Away = game.AwayTeamAbbreviation, ScheduledStart = game.ScheduledStart,
        ObservedAt = now, SchedulePublishedAt = now, PublicationLabel = "synthetic source publication", ScheduleVerified = true,
        CompletedGameIds = (completed ?? []).ToArray(),
        HomeOdds = new() { Reference = "https://www.playoffstatus.com/nfl/lionswhatif.html", PublishedAt = now, WinLower = swing, WinUpper = swing, LossLower = 0, LossUpper = 0 },
        AwayOdds = new() { Reference = "https://www.playoffstatus.com/nfl/packerswhatif.html", PublishedAt = now, WinLower = swing, WinUpper = swing, LossLower = 0, LossUpper = 0 }
    };
    private static ProHypeResult Score(Game game, ProGameEvidence context, ProQualification? home = null, ProQualification? away = null,
        DateTime? now = null, string[]? completed = null) =>
        ProHypePolicy.Calculate(game, context, Ranks(game), home ?? Q(), away ?? Q(), Odds(game), completed ?? [], now ?? Now);

    internal sealed class Handler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public Func<string, string, string> Transform { get; set; } = (_, body) => body;
        public Func<string, string> FixtureName { get; set; } = name => name;
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public DateTime? Publication { get; set; } = Published;
        public bool MissingDate { get; set; }
        public DateTime ServerDate { get; set; } = Now;
        public bool Throw { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath; Requests.Add(path);
            if (Throw) throw new HttpRequestException("offline transport control");
            var name = FixtureName(Path.GetFileName(path));
            Assert.Contains(name, new[] { "nflschedule.html", "lionswhatif.html", "packerswhatif.html" });
            var response = new HttpResponseMessage(Status) { Content = new StringContent(Transform(path, Fixture(name))) };
            response.Content.Headers.LastModified = Publication;
            if (!MissingDate) response.Headers.Date = ServerDate;
            return Task.FromResult(response);
        }
    }
    internal static NflPlayoffLeverageCollector Collector(Handler handler, TimeProvider? time = null) => new(new HttpClient(handler),
        time ?? new FakeTimeProvider(Now), NullLogger<NflPlayoffLeverageCollector>.Instance);
    private static async Task<ProGameEvidence> Captured(Game game, Handler? handler = null)
    {
        var context = Context(game);
        context.NflPlayoffLeverage = (await Collector(handler ?? new()).CollectAsync([game], Schedules(), default))[game.Id];
        return context;
    }

    [Theory]
    [InlineData("nflschedule.html", "e64c66689b238d79b74a26591ba67fdfb11b017a018fd08df2734dfe9aabd8f1")]
    [InlineData("packerswhatif.html", "8e24dae9ec70175b4bcfdd21ae59cfd947130ad6cb3072240e2a10fb370bd78b")]
    [InlineData("lionswhatif.html", "49c2477af66f545091d5415df5018f126a8c013932f99ae331836290c770835c")]
    [InlineData("synthetic-espn-controls.csv", "88c68873e47b2a27a903290eef73ab6632467b207381f3cd1a351852bbfe1a40")]
    public void Independently_generated_fixtures_match_the_reviewed_byte_inventory(string file, string expected)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Fixture(file)));
        Assert.Equal(expected, Convert.ToHexStringLower(hash));
    }

    [Fact]
    public async Task Synthetic_next_game_forecasts_replace_NFL_bubble_urgency_without_requiring_Kalshi_estimates()
    {
        var game = Game(); var handler = new Handler(); var context = await Captured(game, handler);
        var e = context.NflPlayoffLeverage!;
        Assert.True(e.ScheduleVerified, e.Reason);
        Assert.NotNull(e.HomeOdds); Assert.NotNull(e.AwayOdds);
        Assert.Equal(.74, e.HomeOdds.WinLower!.Value, 8); Assert.Equal(.54, e.HomeOdds.LossLower!.Value, 8);
        Assert.Equal(.82, e.AwayOdds.WinLower!.Value, 8); Assert.Equal(.57, e.AwayOdds.LossLower!.Value, 8);
        Assert.Equal(3, handler.Requests.Count);
        var result = Score(game, context);
        Assert.Equal(20, result.Stakes.Value!.Value, 8);
        Assert.Equal(58, Math.Round(result.Total!.Value));
        Assert.Contains("PlayoffStatus model", result.Stakes.Explanation);
        Assert.Null(context.HomeProgress.SeedingUnresolved); Assert.Null(context.AwayProgress.SeedingUnresolved);
        Assert.Equal(result.Stakes.Value, Score(game, context, Q(.01), Q(.99)).Stakes.Value);
        (e.HomeOdds, e.AwayOdds) = (e.AwayOdds, e.HomeOdds);
        Assert.Equal(result.Stakes.Value, Score(game, context).Stakes.Value);
        using var provider = new ServiceCollection().BuildServiceProvider();
        var service = new HypeScoreService(provider, NullLogger<HypeScoreService>.Instance, new FakeTimeProvider(Now));
        game.ProEvidenceJson = JsonSerializer.Serialize(context);
        var components = service.CalculateFromEvidence(game, Ranks(game), Odds(game), [], Now);
        Assert.True(components.IsComplete, components.StakesExplanation);
        Assert.False(components.HomePlayoffOdds.HasValue); Assert.False(components.AwayPlayoffOdds.HasValue);
        Assert.Equal(result.Total, service.CalculateFinalScore("nfl", components));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Regular_season_NFL_stakes_use_only_qualification_even_at_the_last_game(bool? seeding)
    {
        var game = Game(); var context = await Captured(game);
        context.HomeProgress.Completed = context.AwayProgress.Completed = 16;
        context.HomeProgress.SeedingUnresolved = context.AwayProgress.SeedingUnresolved = seeding;
        var result = Score(game, context);
        Assert.NotNull(result.Stakes.Value);
        Assert.Equal(20, result.Stakes.Value.Value, 8);
        Assert.Equal(58.3933971774, result.Total!.Value, 10);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Qualification_intervals_still_require_an_exact_game_maximum_without_a_seeding_term(bool dominated)
    {
        var game = Game(); var context = await Captured(game);
        context.HomeProgress.Completed = context.AwayProgress.Completed = 16;
        context.HomeProgress.SeedingUnresolved = context.AwayProgress.SeedingUnresolved = true;
        var evidence = context.NflPlayoffLeverage!;
        evidence.HomeOdds = evidence.HomeOdds! with { WinLower = .5, WinUpper = .625, LossLower = .375, LossUpper = .375 };
        var awayWin = dominated ? .375 : .125;
        evidence.AwayOdds = evidence.AwayOdds! with { WinLower = awayWin, WinUpper = awayWin, LossLower = 0, LossUpper = 0 };
        var result = Score(game, context);
        if (dominated)
        {
            Assert.Equal(30, result.Stakes.Value);
            Assert.NotNull(result.Total);
        }
        else
        {
            Assert.Null(result.Stakes.Value);
            Assert.Null(result.Total);
            Assert.NotNull(result.Quality.Value);
            Assert.NotNull(result.Closeness.Value);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Integral_decimal_schedule_scores_preserve_verified_NFL_forecasts_and_hype(bool numeric)
    {
        var game = Game();
        var schedules = Schedules(score => numeric
            ? (object)(decimal.Parse(score, System.Globalization.CultureInfo.InvariantCulture) + 0.00m) : score + ".0");
        var evidence = (await Collector(new Handler()).CollectAsync([game], schedules, default))[game.Id]!;
        Assert.True(evidence.ScheduleVerified, evidence.Reason);
        Assert.NotNull(evidence.HomeOdds); Assert.NotNull(evidence.AwayOdds);
        var context = Context(game); context.NflPlayoffLeverage = evidence;
        var result = Score(game, context);
        Assert.Equal(20, result.Stakes.Value!.Value, 8);
        Assert.Equal(58, Math.Round(result.Total!.Value));
    }

    [Theory]
    [InlineData("fractional")]
    [InlineData("tiny-fraction")]
    [InlineData("negative")]
    [InlineData("overflow")]
    [InlineData("nonfinite")]
    [InlineData("empty")]
    [InlineData("comma")]
    public async Task Invalid_schedule_scores_cannot_be_truncated_or_replaced_with_zero(string problem)
    {
        var game = Game();
        var schedules = Schedules(score => problem switch
        {
            "fractional" => score + ".5",
            "tiny-fraction" => score + ".00000000000000000000000000001",
            "negative" => "-3.0",
            "overflow" => "2147483648.0",
            "nonfinite" => "NaN",
            "empty" => "",
            _ => "1,000.0"
        });
        var evidence = (await Collector(new Handler()).CollectAsync([game], schedules, default))[game.Id]!;
        Assert.False(evidence.ScheduleVerified);
        Assert.Null(evidence.HomeOdds); Assert.Null(evidence.AwayOdds);
        var context = Context(game); context.NflPlayoffLeverage = evidence;
        Assert.Null(Score(game, context).Total);
    }

    [Fact]
    public async Task NFL_leverage_is_not_multiplied_by_urgency_and_still_requires_verified_progress()
    {
        var game = Game(); var context = await Captured(game);
        context.HomeProgress.SeedingUnresolved = context.AwayProgress.SeedingUnresolved = false;
        foreach (var played in new[] { 0, 5, 10, 16 })
        {
            context.HomeProgress.Completed = context.AwayProgress.Completed = played;
            Assert.Equal(20, Score(game, context).Stakes.Value!.Value, 8);
        }
        foreach (var invalid in new int?[] { null, -1, 17, 18 })
        {
            context.HomeProgress.Completed = invalid;
            Assert.Null(Score(game, context).Stakes.Value);
        }
        context.HomeProgress.Completed = context.AwayProgress.Completed = 16;
        context.HomeProgress.SeedingUnresolved = context.AwayProgress.SeedingUnresolved = true;
        Assert.Equal(20, Score(game, context).Stakes.Value!.Value, 8);
        context.HomeProgress.SeedingUnresolved = null;
        Assert.Equal(20, Score(game, context).Stakes.Value!.Value, 8);
        context.AwayProgress.SeedingUnresolved = null;
        Assert.Equal(20, Score(game, context).Stakes.Value!.Value, 8);
    }

    [Theory]
    [InlineData("season")]
    [InlineData("opponent")]
    [InlineData("record")]
    [InlineData("duplicate-row")]
    [InlineData("bad-probability")]
    [InlineData("reversed-probability")]
    [InlineData("publication-label")]
    [InlineData("truncated")]
    [InlineData("schedule-duplicate")]
    public async Task Successful_but_unusable_observations_do_not_reuse_a_previous_pair(string problem)
    {
        var game = Game(); var handler = new Handler { Transform = (path, body) => problem switch
        {
            "season" when path.EndsWith("nflschedule.html") => body.Replace("2026 NFL Schedule", "2025 NFL Schedule"),
            "opponent" when path.EndsWith("packerswhatif.html") => body.Replace("Next Game - Lions", "Next Game - Ravens"),
            "record" when path.EndsWith("packerswhatif.html") => body.Replace("<td>Win Next Game</td>", "<td>Win Next Game</td><td>999</td>"),
            "duplicate-row" when path.EndsWith("packerswhatif.html") => body.Replace("<td>Current Standings</td>", "<td>Win Next Game</td>"),
            "bad-probability" when path.EndsWith("packerswhatif.html") => body.Replace(">18%</td>", ">NaN%</td>"),
            "reversed-probability" when path.EndsWith("packerswhatif.html") => body.Replace(">18%</td>", ">90%</td>"),
            "publication-label" when path.EndsWith("packerswhatif.html") => body.Replace("Synthetic publication batch A", "Synthetic publication batch B"),
            "truncated" => body[..^10],
            "schedule-duplicate" when path.EndsWith("nflschedule.html") => body.Replace("#packers", "#lions"),
            _ => body
        } };
        var context = await Captured(game, handler);
        var result = Score(game, context);
        Assert.Null(result.Stakes.Value); Assert.Null(result.Total);
        Assert.NotNull(result.Quality.Value); Assert.NotNull(result.Closeness.Value);
    }

    [Fact]
    public async Task Source_thresholds_are_bounds_not_invented_zero_one_or_midpoint_probabilities()
    {
        var game = Game();
        var context = await Captured(game, new Handler { Transform = (path, body) => path.EndsWith("packerswhatif.html") ? body.Replace(">18%</td>", ">&lt;1%</td>") : body });
        var odds = context.NflPlayoffLeverage!.AwayOdds!;
        Assert.Equal(.99, odds.WinLower); Assert.Equal(1, odds.WinUpper);
        Assert.Null(Score(game, context).Stakes.Value);
        context.NflPlayoffLeverage.HomeOdds = context.NflPlayoffLeverage.HomeOdds! with { WinLower = 1, WinUpper = 1, LossLower = 0, LossUpper = 0 };
        Assert.Equal(80, Score(game, context).Stakes.Value);
    }

    [Theory]
    [InlineData("missing-last-modified")]
    [InlineData("missing-date")]
    [InlineData("future-publication")]
    [InlineData("week-old-publication")]
    public async Task Publication_uses_actual_UTC_HTTP_metadata_not_fetch_time_or_guessed_page_timezone(string problem)
    {
        var handler = new Handler
        {
            Publication = problem switch { "missing-last-modified" => null, "future-publication" => Now.AddTicks(1), "week-old-publication" => Now.AddDays(-7), _ => Published },
            MissingDate = problem == "missing-date"
        };
        var game = Game(); var context = await Captured(game, handler);
        Assert.Null(Score(game, context).Stakes.Value);
        Assert.False(context.NflPlayoffLeverage!.ScheduleVerified);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("roles")]
    [InlineData("later-game")]
    [InlineData("incomplete-league")]
    public async Task Conditional_scenarios_must_match_both_next_games_and_every_known_result(string problem)
    {
        var game = Game(); var schedules = Schedules();
        if (problem == "start") game.ScheduledStart = game.ScheduledStart.AddHours(1);
        if (problem == "roles") (game.HomeTeamAbbreviation, game.AwayTeamAbbreviation) = (game.AwayTeamAbbreviation, game.HomeTeamAbbreviation);
        if (problem == "later-game")
        {
            var later = Schedules().Single(s => s.Team == "GB").Events.Single(e => e.GetProperty("week").GetProperty("number").GetInt32() == 8);
            var competitors = later.GetProperty("competitions")[0].GetProperty("competitors").EnumerateArray().ToArray();
            game.ScheduledStart = DateTimeOffset.Parse(later.GetProperty("date").GetString()!).UtcDateTime;
            game.WeekNumber = 8;
            game.HomeTeamAbbreviation = competitors.Single(t => t.GetProperty("homeAway").GetString() == "home").GetProperty("team").GetProperty("abbreviation").GetString()!;
            game.AwayTeamAbbreviation = competitors.Single(t => t.GetProperty("homeAway").GetString() == "away").GetProperty("team").GetProperty("abbreviation").GetString()!;
        }
        if (problem == "incomplete-league") schedules = schedules.Skip(1).ToArray();
        var handler = new Handler();
        var result = (await Collector(handler).CollectAsync([game], schedules, default))[game.Id]!;
        Assert.False(result.ScheduleVerified, result.Reason);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_final_the_source_has_not_yet_published_keeps_previous_evidence_instead_of_rejecting_it()
    {
        var game = Game(); var second = Game(); second.Id += "-second";
        var schedules = Schedules();
        var newlyFinal = schedules.SelectMany(s => s.Events).First(e => e.GetProperty("week").GetProperty("number").GetInt32() == 7
            && e.GetProperty("id").GetString() != "control-7-DET-GB").GetProperty("id").GetString()!;
        schedules = schedules.Select(s => s.Events.Any(e => e.GetProperty("id").GetString() == newlyFinal)
            ? s with { Completed = s.Completed + 1, CompletedIds = [.. s.CompletedIds, newlyFinal] } : s).ToArray();
        var handler = new Handler();
        var result = await Collector(handler).CollectAsync([game, second], schedules, default);
        Assert.Null(result[game.Id]); Assert.Null(result[second.Id]);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task Transport_denial_or_failure_does_not_manufacture_an_unavailable_success_or_renew_evidence(int status)
    {
        var game = Game(); var second = Game(); second.Id += "-second";
        var handler = new Handler { Status = (HttpStatusCode)status };
        var result = await Collector(handler).CollectAsync([game, second], Schedules(), default);
        Assert.Null(result[game.Id]); Assert.Null(result[second.Id]); Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1440)]
    public async Task One_future_kickoff_disagreement_does_not_poison_unrelated_verified_next_games(int minutes)
    {
        var game = Game(); var schedules = Schedules(); var handler = new Handler();
        var changed = schedules.SelectMany(s => s.Events).First(e => e.GetProperty("week").GetProperty("number").GetInt32() == 7
            && e.GetProperty("competitions")[0].GetProperty("competitors").EnumerateArray()
                .All(t => t.GetProperty("team").GetProperty("abbreviation").GetString() is not ("DET" or "GB")));
        var changedId = changed.GetProperty("id").GetString();
        var changedStart = DateTimeOffset.Parse(changed.GetProperty("date").GetString()!).UtcDateTime.AddMinutes(minutes);
        var competitors = changed.GetProperty("competitions")[0].GetProperty("competitors").EnumerateArray().ToArray();
        var affected = Game(); affected.Id = changedId!; affected.ScheduledStart = changedStart;
        affected.HomeTeamAbbreviation = competitors.Single(t => t.GetProperty("homeAway").GetString() == "home").GetProperty("team").GetProperty("abbreviation").GetString()!;
        affected.AwayTeamAbbreviation = competitors.Single(t => t.GetProperty("homeAway").GetString() == "away").GetProperty("team").GetProperty("abbreviation").GetString()!;
        schedules = schedules.Select(s => s with { Events = s.Events.Select(e =>
        {
            if (e.GetProperty("id").GetString() != changedId) return e;
            var copy = System.Text.Json.Nodes.JsonNode.Parse(e.GetRawText())!; copy["date"] = changedStart;
            return JsonSerializer.SerializeToElement(copy);
        }).ToArray() }).ToArray();
        var results = await Collector(handler).CollectAsync([game, affected], schedules, default);
        var supported = results[game.Id]!;
        Assert.True(supported.ScheduleVerified, supported.Reason);
        Assert.NotNull(supported.HomeOdds); Assert.NotNull(supported.AwayOdds);
        var context = Context(game); context.NflPlayoffLeverage = supported;
        Assert.Equal(20, Score(game, context).Stakes.Value!.Value, 8);
        Assert.Equal(58, Math.Round(Score(game, context).Total!.Value));
        var blocked = results[affected.Id]!;
        Assert.False(blocked.ScheduleVerified);
        Assert.Null(blocked.HomeOdds); Assert.Null(blocked.AwayOdds);
        Assert.Contains("verified next game", blocked.Reason);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData("week")]
    [InlineData("roles")]
    [InlineData("missing-week-and-date")]
    [InlineData("inconsistent-copies")]
    [InlineData("completed-start")]
    [InlineData("completed-score")]
    public async Task Isolation_preserves_league_identity_copy_consistency_and_exact_completed_results(string problem)
    {
        var game = Game(); var schedules = Schedules();
        var past = problem.StartsWith("completed-", StringComparison.Ordinal);
        var changedId = schedules.SelectMany(s => s.Events).First(e => e.GetProperty("week").GetProperty("number").GetInt32() == (past ? 1 : 7)
            && e.GetProperty("competitions")[0].GetProperty("competitors").EnumerateArray()
                .All(t => t.GetProperty("team").GetProperty("abbreviation").GetString() is not ("DET" or "GB"))).GetProperty("id").GetString();
        var modified = false;
        schedules = schedules.Select(s => s with { Events = s.Events.Select(e =>
        {
            if (e.GetProperty("id").GetString() != changedId || problem == "inconsistent-copies" && modified) return e;
            modified = true;
            var copy = System.Text.Json.Nodes.JsonNode.Parse(e.GetRawText())!;
            switch (problem)
            {
                case "week": copy["week"]!["number"] = 8; break;
                case "roles":
                    copy["competitions"]![0]!["competitors"]![0]!["homeAway"] = "away";
                    copy["competitions"]![0]!["competitors"]![1]!["homeAway"] = "home"; break;
                case "missing-week-and-date":
                    copy.AsObject().Remove("week"); copy["date"] = DateTimeOffset.Parse(e.GetProperty("date").GetString()!).AddDays(1); break;
                case "completed-score": copy["competitions"]![0]!["competitors"]![0]!["score"]!["value"] = "999"; break;
                default: copy["date"] = DateTimeOffset.Parse(e.GetProperty("date").GetString()!).AddMinutes(37); break;
            }
            return JsonSerializer.SerializeToElement(copy);
        }).ToArray() }).ToArray();
        var handler = new Handler();
        var result = (await Collector(handler).CollectAsync([game], schedules, default))[game.Id]!;
        Assert.False(result.ScheduleVerified);
        Assert.Null(result.HomeOdds); Assert.Null(result.AwayOdds);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task One_collection_shares_requests_and_never_contacts_other_leagues()
    {
        var game = Game(); var second = Game(); second.Id += "-second";
        var other = ProHypeTests.Game("nhl");
        var handler = new Handler(); var collector = Collector(handler);
        var result = await collector.CollectAsync([game, second, other], Schedules(), default);
        Assert.Equal(3, handler.Requests.Count); Assert.Equal(3, handler.Requests.Distinct().Count());
        Assert.True(result[second.Id]!.ScheduleVerified); Assert.False(result.ContainsKey(other.Id));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collector.CollectAsync([game], Schedules(), cancel.Token));
    }

    [Fact]
    public async Task Every_read_enforces_expiry_identity_and_new_result_coverage()
    {
        var game = Game(); var context = await Captured(game);
        var original = JsonSerializer.Serialize(context);
        foreach (var problem in new[] { "version", "source", "season", "team", "game", "start", "unverified", "future", "old", "coverage", "bad-number", "bad-url" })
        {
            context = ProGameEvidence.Parse(original)!; var e = context.NflPlayoffLeverage!;
            switch (problem)
            {
                case "version": e.Version = 0; break;
                case "source": e.Source = "kalshi"; break;
                case "season": e.Season--; break;
                case "team": e.Home = "KC"; break;
                case "game": e.GameId = "other"; break;
                case "start": e.ScheduledStart = e.ScheduledStart.AddMinutes(1); break;
                case "unverified": e.ScheduleVerified = false; break;
                case "future": e.ObservedAt = Now.AddTicks(1); break;
                case "old": e.ObservedAt = Now.AddHours(-1); break;
                case "coverage": e.CompletedGameIds = []; break;
                case "bad-number": e.HomeOdds!.WinLower = double.NaN; break;
                case "bad-url": e.HomeOdds!.Reference = "https://example.com/nfl/lionswhatif.html"; break;
            }
            Assert.Null(Score(game, context).Stakes.Value);
        }
        context = ProGameEvidence.Parse(original)!;
        Assert.NotNull(NflPlayoffLeveragePolicy.Select(game, context, Now.AddHours(1).AddTicks(-1)));
        Assert.Null(NflPlayoffLeveragePolicy.Select(game, context, Now.AddHours(1)));
        Assert.Null(Score(game, context, completed: ["newly-finished"]).Stakes.Value);
        context.CompletedGameIds = [.. context.CompletedGameIds, "newly-finished"];
        Assert.Null(NflPlayoffLeveragePolicy.Select(game, context, Now));
        context = ProGameEvidence.Parse(original)!;
        context.NflPlayoffLeverage!.HomeOdds!.PublishedAt = Now.AddDays(-7);
        Assert.Null(Score(game, context).Stakes.Value);
        context = ProGameEvidence.Parse(original)!;
        context.NflPlayoffLeverage = null;
        Assert.Null(Score(game, context, Q(.5), Q(.5)).Stakes.Value);
    }

    [Fact]
    public async Task Trusted_finals_contribute_zero_and_extreme_forecasts_never_become_final_outcomes()
    {
        var game = Game(); var context = await Captured(game);
        context.NflPlayoffLeverage!.HomeOdds = null;
        Assert.Equal(20, Score(game, context, Q(0, true)).Stakes.Value!.Value, 8);
        context.NflPlayoffLeverage = null;
        Assert.Equal(0, Score(game, context, Q(0, true), Q(0, true)).Stakes.Value);
        context.HomeProgress.SeedingUnresolved = true;
        Assert.NotNull(Score(game, context, Q(1, true), Q(0, true)).Stakes.Value);
        context.HomeProgress.SeedingUnresolved = false;
        context.HomeProgress.Completed = null;
        Assert.Equal(0, Score(game, context, Q(1, true), Q(0, true)).Stakes.Value);
        context = await Captured(game);
        context.NflPlayoffLeverage!.HomeOdds = context.NflPlayoffLeverage.HomeOdds! with { WinLower = 1, WinUpper = 1, LossLower = 1, LossUpper = 1 };
        context.NflPlayoffLeverage.AwayOdds = context.NflPlayoffLeverage.AwayOdds! with { WinLower = 0, WinUpper = 0, LossLower = 0, LossUpper = 0 };
        Assert.Equal(0, Score(game, context).Stakes.Value);
        context.HomeProgress.SeedingUnresolved = context.AwayProgress.SeedingUnresolved = false;
        Assert.Equal(0, Score(game, context).Stakes.Value);
        game.SeasonType = context.Phase = 1;
        Assert.Equal(0, Score(game, context).Stakes.Value);
        game.SeasonType = context.Phase = 3; context.Round = 4; context.HomeElimination = context.AwayElimination = true;
        Assert.Equal(100, Score(game, context).Stakes.Value);
        game.Status = "in_progress";
        Assert.Null(Score(game, context).Total);
    }
}
