using System.Text.Json;
using System.Text.Json.Nodes;
using backend.Data;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FSharp.Core;
using Xunit;

namespace backend.tests;

public sealed class MlbScoreboardTests(SharedPostgreSql server) : IClassFixture<SharedPostgreSql>, IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2027, 7, 10, 23, 0, 0, TimeSpan.Zero);
    private static readonly JsonObject Espn = Load("espn-basic.json");
    private static readonly JsonObject Rich = Load("rich-frames.json");

    private static readonly Dictionary<string, string> States = new()
    {
        ["STATUS_SCHEDULED"] = "scheduled",
        ["STATUS_IN_PROGRESS"] = "in_progress",
        ["STATUS_FINAL"] = "final",
        ["STATUS_POSTPONED"] = "postponed",
        ["STATUS_CANCELED"] = "canceled",
        ["STATUS_RAIN_DELAY"] = "delayed",
        ["STATUS_SUSPENDED"] = "suspended",
    };

    private readonly HostedGameFixture host = new(Start, server: server);

    public Task InitializeAsync() => host.InitializeAsync();
    public Task DisposeAsync() => host.DisposeAsync();

    [Fact]
    public async Task An_accepted_frame_updates_the_game_row_and_reads_back_zeros_as_values_and_null_innings_as_missing()
    {
        await host.SeedAsync(MlbGame("991300104"), []);
        var report = Frame("zero-count-empty-bases", "advance");
        report["home"]!["innings"]![1] = null;
        At("2027-07-10T23:50:30Z");

        Accepted(await AcceptAsync(report));

        var row = await RowAsync("991300104");
        Assert.Equal(new Row("in_progress", 2, 3, "Bottom 6th", 6, Utc("2027-07-10T23:50:00Z"), Utc("2027-07-10T23:49:55Z"), null), row with { Json = null });
        var view = await ReadAsync("991300104");
        Assert.Equal(MlbScoreboardPhase.Live, view.Phase);
        var active = view.Active!.Value;
        Assert.Equal(new MlbCount(0, 0, 0), active.Count.Value);
        Assert.Equal(new MlbBases(MlbBase.Empty, MlbBase.Empty, MlbBase.Empty), active.Bases.Value);
        Assert.Equal(MlbHalf.Bottom, active.Half.Value);
        var board = view.Board!.Value;
        Assert.Equal(6, board.Innings);
        Assert.Equal(Cells(1, null, 0, 0, 0, 1), board.Home.Line.ToArray());
        Assert.Equal(Cells(0, 2, 0, 0, 1, 0), board.Away.Line.ToArray());
        Assert.Equal(FSharpOption<int>.Some(0), board.Away.Errors);
        Assert.Equal(FSharpOption<int>.Some(1), board.Home.Errors);
    }

    [Fact]
    public async Task Extra_innings_keep_every_inning_in_the_line_score()
    {
        await host.SeedAsync(MlbGame("991300102", "final"), []);
        At("2027-07-10T23:30:00Z");

        Accepted(await AcceptAsync(Report("991300102", "opening", Instant("2027-07-10T23:30:00Z"))));

        var board = (await ReadAsync("991300102")).Board!.Value;
        Assert.Equal(11, board.Innings);
        Assert.Equal(Cells(0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 2), board.Away.Line.ToArray());
        Assert.Equal(Cells(0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0), board.Home.Line.ToArray());
        Assert.Equal("Final/11", board.Detail);
    }

    [Fact]
    public async Task Only_a_final_with_a_leading_home_team_one_inning_short_marks_the_bottom_half_not_played()
    {
        await host.SeedAsync(MlbGame("991300101", "final"), []);
        await host.SeedAsync(MlbGame("991300104"), []);
        At("2027-07-10T23:30:00Z");
        var inProgress = Frame("retained-after-final", "opening");
        inProgress["gameId"] = "991300104";
        inProgress["competitionId"] = "991300104";
        inProgress["homeTeamId"] = "9121";
        inProgress["awayTeamId"] = "9103";
        inProgress["state"] = "in_progress";

        Accepted(await AcceptAsync(Frame("retained-after-final", "opening")));
        Accepted(await AcceptAsync(inProgress));

        var final = (await ReadAsync("991300101")).Board!.Value;
        Assert.Equal(9, final.Innings);
        Assert.Equal(Cells(1, 0, 2, 0, 0, 1, 0, 0).Append(MlbLineCell.NotPlayed), final.Home.Line);
        Assert.Equal(Cells(0, 1, 0, 0, 2, 0, 0, 0, 0), final.Away.Line.ToArray());
        Assert.Equal(Cells(1, 0, 2, 0, 0, 1, 0, 0), (await ReadAsync("991300104")).Board!.Value.Home.Line.ToArray());
    }

    [Fact]
    public async Task A_future_observation_is_rejected_and_writes_nothing()
    {
        await host.SeedAsync(MlbGame("991300104"), []);
        At("2027-07-11T00:50:00Z");
        var before = await RowAsync("991300104");

        Rejected(MlbScoreboardRejection.FutureObservation, await AcceptAsync(Frame("future", "future")));

        Assert.Equal(before, await RowAsync("991300104"));
        Assert.Equal(MlbScoreboardPhase.Unavailable, (await ReadAsync("991300104")).Phase);
    }

    [Fact]
    public async Task The_active_context_expires_exactly_at_the_two_minute_age_limit()
    {
        await host.SeedAsync(MlbGame("991300104"), []);
        At("2027-07-10T23:30:00Z");
        Accepted(await AcceptAsync(Frame("active-complete", "opening")));

        host.Clock.SetUtcNow(Instant("2027-07-10T23:30:00Z").AddMinutes(2).AddMilliseconds(-1));
        var fresh = await ReadAsync("991300104");
        host.Clock.SetUtcNow(Instant("2027-07-10T23:32:00Z"));
        var expired = await ReadAsync("991300104");

        Assert.Equal(TimeSpan.FromMinutes(2), MlbScoreboard.ActiveContextMaxAge);
        Assert.Equal(MlbScoreboardPhase.Live, fresh.Phase);
        Assert.Equal(new MlbActiveContext(
            FSharpOption<MlbHalf>.Some(MlbHalf.Top),
            FSharpOption<MlbCount>.Some(new MlbCount(2, 1, 1)),
            FSharpOption<MlbBases>.Some(new MlbBases(Occupied("Synthetic Runner A"), MlbBase.Empty, Occupied("Synthetic Runner B"))),
            FSharpOption<MlbMatchup>.Some(new MlbMatchup(FSharpOption<string>.Some("Synthetic Batter 7"), FSharpOption<string>.Some("Synthetic Pitcher 3")))),
            fresh.Active!.Value);
        Assert.Equal(MlbScoreboardPhase.Stale, expired.Phase);
        Assert.Null(expired.Active);
        Assert.Equal(3, expired.Board!.Value.Away.Runs);
    }

    [Theory]
    [InlineData("recorded", false)]
    [InlineData("live", true)]
    public async Task Recorded_and_historical_frames_never_expose_an_active_context(string provenance, bool historical)
    {
        var game = MlbGame("991300104");
        game.IsHistoricalImport = historical;
        await host.SeedAsync(game, []);
        At("2027-07-10T23:30:00Z");
        var report = Frame("active-complete", "opening");
        report["provenance"] = provenance;

        Accepted(await AcceptAsync(report));

        var view = await ReadAsync("991300104");
        Assert.Equal(MlbScoreboardPhase.Recorded, view.Phase);
        Assert.Null(view.Active);
        Assert.Equal("Top 5th", view.Board!.Value.Detail);
    }

    [Fact]
    public async Task Reports_that_contradict_the_game_or_baseball_are_rejected_without_changing_the_row()
    {
        await host.SeedAsync(MlbGame("991300103", "final"), []);
        await host.SeedAsync(MlbGame("991300104"), []);
        var basketball = MlbGame("991300190");
        basketball.Sport = "nba";
        await host.SeedAsync(basketball, []);
        var unidentified = MlbGame("991300191");
        unidentified.HomeTeamId = null;
        await host.SeedAsync(unidentified, []);
        At("2027-07-10T23:30:00Z");
        Accepted(await AcceptAsync(Frame("active-complete", "opening")));
        At("2027-07-10T23:50:00Z");
        var before = await RowAsync("991300104");

        var cases = new (string Name, Action<JsonObject> Edit, MlbScoreboardRejection Expected)[]
        {
            ("other sport", r => Identify(r, "991300190"), MlbScoreboardRejection.NotMlb),
            ("unknown game", r => Identify(r, "991300199"), MlbScoreboardRejection.WrongGame),
            ("doubleheader game id", r => r["gameId"] = "991300103", MlbScoreboardRejection.WrongCompetition),
            ("doubleheader competition", r => r["competitionId"] = "991300103", MlbScoreboardRejection.WrongCompetition),
            ("stored team missing", r => Identify(r, "991300191"), MlbScoreboardRejection.MissingTeamIdentity),
            ("reported team missing", r => r["awayTeamId"] = " ", MlbScoreboardRejection.MissingTeamIdentity),
            ("swapped sides", r => { r["homeTeamId"] = "9103"; r["awayTeamId"] = "9121"; }, MlbScoreboardRejection.NewWrongTeam(MlbSide.Home)),
            ("other away team", r => r["awayTeamId"] = "9120", MlbScoreboardRejection.NewWrongTeam(MlbSide.Away)),
            ("missing away runs", r => r["away"]!["runs"] = null, MlbScoreboardRejection.NewMissingScore(MlbSide.Away)),
            ("missing home line", r => r.Remove("home"), MlbScoreboardRejection.NewMissingScore(MlbSide.Home)),
            ("blank detail", r => r["detail"] = "", MlbScoreboardRejection.MissingDetail),
            ("missing provider", r => r.Remove("provider"), Impossible("provider")),
            ("unknown provenance", r => r["provenance"] = "replay", Impossible("provenance")),
            ("coarse state", r => r["state"] = "post", Impossible("state")),
            ("revision after observation", r => r["sourceRevisionAt"] = "2027-07-10T23:50:01Z", MlbScoreboardRejection.RevisionAfterObservation),
            ("four balls", r => r["count"]!["balls"] = 4, Impossible("count.balls")),
            ("three strikes", r => r["count"]!["strikes"] = 3, Impossible("count.strikes")),
            ("four outs", r => r["count"]!["outs"] = 4, Impossible("count.outs")),
            ("negative balls", r => r["count"]!["balls"] = -1, Impossible("count.balls")),
            ("runner on empty base", r => r["bases"]!["second"]!["runner"] = "Synthetic Runner C", Impossible("bases.second")),
            ("half without inning", r => r["inning"] = null, Impossible("half")),
            ("inning zero", r => { r["inning"] = 0; r["half"] = null; }, Impossible("inning")),
            ("unknown half", r => r["half"] = "middle", Impossible("half")),
            ("innings beyond runs", r => r["home"]!["innings"]![1] = 5, Impossible("home.innings")),
            ("negative inning", r => r["away"]!["innings"]![0] = -1, Impossible("away.innings")),
            ("negative hits", r => r["away"]!["hits"] = -1, Impossible("away.hits")),
            ("negative errors", r => r["home"]!["errors"] = -1, Impossible("home.errors")),
            ("negative runs", r => r["home"]!["runs"] = -1, Impossible("home.runs")),
            ("home ahead of away", r => { r["home"]!["innings"]!.AsArray().Add(0); r["home"]!["innings"]!.AsArray().Add(0); }, Impossible("innings")),
            ("away two ahead", r => r["away"]!["innings"]!.AsArray().Add(0), Impossible("innings")),
            ("away line beyond inning", r => r["inning"] = 4, Impossible("inning")),
        };
        foreach (var (name, edit, expected) in cases)
        {
            var report = Frame("zero-count-empty-bases", "opening");
            report["inning"] = 5;
            report["half"] = "top";
            report["detail"] = "Top 5th";
            edit(report);
            var result = await AcceptAsync(report);
            Assert.True(result.IsError && result.ErrorValue.Equals(expected), $"{name}: expected {expected}, got {Outcome(result)}");
            Assert.Equal(before, await RowAsync("991300104"));
        }
    }

    [Fact]
    public async Task Older_observations_and_revisions_below_the_high_water_mark_are_rejected()
    {
        await host.SeedAsync(MlbGame("991300104"), []);
        At("2027-07-10T23:30:00Z");
        Accepted(await AcceptAsync(Frame("active-complete", "opening")));
        At("2027-07-10T23:50:00Z");
        Accepted(await AcceptAsync(Frame("zero-count-empty-bases", "advance")));

        Rejected(MlbScoreboardRejection.RegressingObservation, await AcceptAsync(Frame("active-complete", "opening")));

        At("2027-07-11T00:00:00Z");
        Accepted(await AcceptAsync(Frame("partial-optional", "partial")));
        At("2027-07-11T00:10:00Z");
        Accepted(await AcceptAsync(Frame("rich-unavailable", "partial")));
        Assert.Equal(Utc("2027-07-10T23:49:55Z"), (await RowAsync("991300104")).Revision);
        At("2027-07-11T00:30:00Z");
        var before = await RowAsync("991300104");

        Rejected(MlbScoreboardRejection.RegressingRevision, await AcceptAsync(Frame("out-of-order", "out-of-order")));
        Assert.Equal(before, await RowAsync("991300104"));

        var sameRevision = Frame("zero-count-empty-bases", "advance");
        sameRevision["observedAt"] = "2027-07-11T00:30:00Z";
        Accepted(await AcceptAsync(sameRevision));
        var row = await RowAsync("991300104");
        Assert.Equal(Utc("2027-07-11T00:30:00Z"), row.ScoreboardUpdatedAt);
        Assert.Equal(Utc("2027-07-10T23:49:55Z"), row.Revision);
    }

    [Fact]
    public async Task A_partial_frame_replaces_the_whole_frame_instead_of_borrowing_older_fields()
    {
        await host.SeedAsync(MlbGame("991300104"), []);
        At("2027-07-10T23:30:00Z");
        Accepted(await AcceptAsync(Frame("active-complete", "opening")));
        At("2027-07-11T00:00:30Z");

        Accepted(await AcceptAsync(Frame("partial-optional", "partial")));

        var view = await ReadAsync("991300104");
        var active = view.Active!.Value;
        Assert.Null(active.Bases);
        Assert.Equal(new MlbMatchup(FSharpOption<string>.Some("Synthetic Batter 12"), null), active.Matchup.Value);
        Assert.Equal(new MlbCount(1, 0, 2), active.Count.Value);
        var board = view.Board!.Value;
        Assert.Null(board.Home.Hits);
        Assert.Empty(board.Home.Line);
        Assert.Null(board.Away.Hits);
        Assert.Equal(2, board.Home.Runs);
        Assert.Equal(6, board.Innings);
    }

    [Theory]
    [InlineData("991300101", "retained-after-final", MlbScoreboardPhase.Tags.Final, "Final", 3, 4)]
    [InlineData("991300108", "retained-while-delayed", MlbScoreboardPhase.Tags.Delayed, "Rain Delay", 1, 2)]
    [InlineData("991300109", "retained-while-suspended", MlbScoreboardPhase.Tags.Suspended, "Suspended", 3, 1)]
    [InlineData("991300105", null, MlbScoreboardPhase.Tags.Postponed, "Postponed", 0, 0)]
    [InlineData("991300107", null, MlbScoreboardPhase.Tags.Canceled, "Canceled", 0, 0)]
    public async Task Interrupted_and_ended_games_keep_scores_lines_and_detail_without_an_active_context(
        string gameId, string? frameId, int phase, string detail, int awayRuns, int homeRuns)
    {
        await host.SeedAsync(MlbGame(gameId), []);
        At("2027-07-10T23:30:00Z");

        Accepted(await AcceptAsync(frameId is null ? Report(gameId, "opening", Instant("2027-07-10T23:30:00Z")) : Frame(frameId, "opening")));

        var view = await ReadAsync(gameId);
        Assert.Equal(phase, view.Phase.Tag);
        Assert.Null(view.Active);
        var board = view.Board!.Value;
        Assert.Equal((detail, awayRuns, homeRuns), (board.Detail, board.Away.Runs, board.Home.Runs));
        var expected = Competition(gameId, "opening")["competitors"]!.AsArray()
            .Single(c => (string)c!["homeAway"]! == "away")!["linescores"]?.AsArray().Count ?? 0;
        Assert.Equal(expected, board.Away.Line.Length);
    }

    [Fact]
    public async Task A_final_scoreboard_is_completed_only_with_confirming_completion_evidence()
    {
        await host.SeedAsync(MlbGame("991300101", "final"), []);
        var confirmed = MlbGame("991300102", "final");
        var status = new EspnCompletionStatus("STATUS_FINAL", "post", true, "Final", "Final/11", "Final/11");
        confirmed.EspnCompletionEvidenceJson = JsonSerializer.Serialize(
            new MlbCompletionEvidence("991300102", "991300102", status, status, Utc("2027-07-10T23:30:00Z")));
        await host.SeedAsync(confirmed, []);
        At("2027-07-10T23:30:00Z");

        Accepted(await AcceptAsync(Frame("retained-after-final", "opening")));
        Accepted(await AcceptAsync(Report("991300102", "opening", Instant("2027-07-10T23:30:00Z"))));

        var unconfirmed = await ReadAsync("991300101");
        var completed = await ReadAsync("991300102");
        Assert.Equal((MlbScoreboardPhase.Final, false), (unconfirmed.Phase, unconfirmed.Completed));
        Assert.Equal((MlbScoreboardPhase.Final, true), (completed.Phase, completed.Completed));
    }

    [Fact]
    public async Task Scoreboard_and_odds_observations_do_not_renew_each_other()
    {
        var game = MlbGame("991300104");
        game.MarketObservedAt = Utc("2027-07-10T23:29:00Z");
        await host.SeedAsync(game, [Odds(Utc("2027-07-10T23:29:00Z"), 0.4)]);
        At("2027-07-10T23:30:00Z");

        Accepted(await AcceptAsync(Frame("active-complete", "opening")));

        Assert.Equal([(Utc("2027-07-10T23:29:00Z"), 0.4)], await OddsAsync());
        Assert.Equal(Utc("2027-07-10T23:29:00Z"), (await RowAsync("991300104")).MarketObservedAt);

        At("2027-07-10T23:32:00Z");
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            db.OddsSnapshots.Add(Odds(Utc("2027-07-10T23:32:00Z"), 0.55));
            await db.SaveChangesAsync();
            await db.Games.Where(g => g.Id == "991300104")
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.MarketObservedAt, Utc("2027-07-10T23:32:00Z")));
        }

        var view = await ReadAsync("991300104");
        Assert.Equal(MlbScoreboardPhase.Stale, view.Phase);
        Assert.Null(view.Active);
        Assert.Equal(Utc("2027-07-10T23:30:00Z"), view.Board!.Value.ObservedAt);
    }

    [Fact]
    public async Task Reading_fails_closed_for_absent_non_mlb_unobserved_and_no_longer_valid_scoreboards()
    {
        await host.SeedAsync(MlbGame("991300104"), []);
        var basketball = MlbGame("991300190");
        basketball.Sport = "nba";
        basketball.MlbScoreboardJson = "{}";
        await host.SeedAsync(basketball, []);

        using (var scope = host.Services.CreateScope())
        {
            var data = scope.ServiceProvider.GetRequiredService<GameDataService>();
            Assert.Null(await data.GetMlbScoreboardAsync("991300199", CancellationToken.None));
            Assert.Null(await data.GetMlbScoreboardAsync("991300190", CancellationToken.None));
        }
        Assert.Equal(MlbScoreboardPhase.Unavailable, (await ReadAsync("991300104")).Phase);

        At("2027-07-10T23:30:00Z");
        Accepted(await AcceptAsync(Frame("active-complete", "opening")));
        Assert.Equal(MlbScoreboardPhase.Live, (await ReadAsync("991300104")).Phase);
        await UpdateAsync(s => s.SetProperty(g => g.HomeTeamId, "9105"));
        Assert.Equal(new MlbScoreboardView(MlbScoreboardPhase.Unavailable, null, null, false), await ReadAsync("991300104"));
        await UpdateAsync(s => s.SetProperty(g => g.HomeTeamId, "9121").SetProperty(g => g.MlbScoreboardJson, "{\"gameId\":"));
        Assert.Equal(MlbScoreboardPhase.Unavailable, (await ReadAsync("991300104")).Phase);
    }

    private sealed record Row(string Status, int? HomeScore, int? AwayScore, string? Detail, int? Period, DateTime? ScoreboardUpdatedAt,
        DateTime? Revision, DateTime? MarketObservedAt)
    {
        public string? Json { get; init; }
    }

    private static JsonObject Load(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "MlbScoreboard", name)))!.AsObject();

    private static DateTimeOffset Instant(string instant) => DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture);

    private static DateTime Utc(string instant) => Instant(instant).UtcDateTime;

    private void At(string instant) => host.Clock.SetUtcNow(Instant(instant));

    private static Game MlbGame(string id, string status = "in_progress")
    {
        var contest = Espn["contests"]![id] ?? Espn["contests"]!["991300104"]!;
        return new Game
        {
            Id = id,
            Sport = "mlb",
            HomeTeam = (string)contest["home"]!["displayName"]!,
            AwayTeam = (string)contest["away"]!["displayName"]!,
            HomeTeamAbbreviation = (string)contest["home"]!["abbreviation"]!,
            AwayTeamAbbreviation = (string)contest["away"]!["abbreviation"]!,
            HomeTeamId = (string)contest["home"]!["id"]!,
            AwayTeamId = (string)contest["away"]!["id"]!,
            ScheduledStart = Utc((string)contest["start"]!),
            SeasonYear = 2027,
            SeasonType = 2,
            Status = status,
        };
    }

    private static JsonNode Competition(string gameId, string observation) =>
        Espn["observations"]!.AsArray().Single(o => (string)o!["id"]! == observation)!["boards"]!.AsArray()
            .SelectMany(board => board!["body"]!["events"]!.AsArray())
            .Single(e => (string)e!["id"]! == gameId)!["competitions"]![0]!;

    private static JsonObject Report(string gameId, string observation, DateTimeOffset observedAt) =>
        Describe(new JsonObject { ["gameId"] = gameId, ["observedAt"] = observedAt }, observation);

    private static JsonObject Frame(string frameId, string observation) =>
        Describe(Rich["frames"]!.AsArray().Single(f => (string)f!["id"]! == frameId)!.DeepClone().AsObject(), observation);

    private static JsonObject Describe(JsonObject report, string observation)
    {
        var gameId = (string)report["gameId"]!;
        var contest = Espn["contests"]![gameId]!;
        var competition = Competition(gameId, observation);
        var status = competition["status"]!["type"]!;
        report["competitionId"] = (string)competition["id"]!;
        report["provider"] = "synthetic-espn";
        report["provenance"] = "live";
        report["homeTeamId"] = (string)contest["home"]!["id"]!;
        report["awayTeamId"] = (string)contest["away"]!["id"]!;
        report["state"] = States[(string)status["name"]!];
        report["detail"] = (string)status["detail"]!;
        foreach (var competitor in competition["competitors"]!.AsArray())
            report[(string)competitor!["homeAway"]!] = new JsonObject
            {
                ["runs"] = int.Parse((string)competitor["score"]!),
                ["hits"] = competitor["hits"]?.DeepClone(),
                ["errors"] = competitor["errors"]?.DeepClone(),
                ["innings"] = competitor["linescores"] is JsonArray scores
                    ? new JsonArray([.. scores.Select(s => (JsonNode?)JsonValue.Create((int)(double)s!["value"]!))])
                    : null,
            };
        return report;
    }

    private static MlbLineCell[] Cells(params int?[] runs) =>
        [.. runs.Select(r => r is int scored ? MlbLineCell.NewRuns(scored) : MlbLineCell.Missing)];

    private static MlbBase Occupied(string runner) => MlbBase.NewOccupied(FSharpOption<string>.Some(runner));

    private static MlbScoreboardRejection Impossible(string field) => MlbScoreboardRejection.NewImpossible(field);

    private static OddsSnapshot Odds(DateTime at, double home) => new()
    {
        GameId = "991300104",
        Timestamp = at,
        HomeMoneyline = 110,
        AwayMoneyline = -130,
        HomeWinProbability = home,
        AwayWinProbability = 1 - home,
        Source = "synthetic",
    };

    private static string Outcome(FSharpResult<MlbScoreboardFrame, MlbScoreboardRejection> result) =>
        result.IsOk ? "accepted" : result.ErrorValue.ToString();

    private static void Accepted(FSharpResult<MlbScoreboardFrame, MlbScoreboardRejection> result) =>
        Assert.True(result.IsOk, Outcome(result));

    private static void Rejected(MlbScoreboardRejection expected, FSharpResult<MlbScoreboardFrame, MlbScoreboardRejection> result)
    {
        Assert.True(result.IsError, Outcome(result));
        Assert.Equal(expected, result.ErrorValue);
    }

    private static void Identify(JsonObject report, string gameId)
    {
        report["gameId"] = gameId;
        report["competitionId"] = gameId;
    }

    private async Task<FSharpResult<MlbScoreboardFrame, MlbScoreboardRejection>> AcceptAsync(JsonObject report)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        var parsed = JsonSerializer.Deserialize<MlbScoreboardReport>(report.ToJsonString(), JsonSerializerOptions.Web)!;
        return await MlbScoreboardStore.AcceptAsync(db, host.Clock, parsed, CancellationToken.None);
    }

    private async Task<MlbScoreboardView> ReadAsync(string gameId)
    {
        using var scope = host.Services.CreateScope();
        var view = await scope.ServiceProvider.GetRequiredService<GameDataService>().GetMlbScoreboardAsync(gameId, CancellationToken.None);
        Assert.NotNull(view);
        return view.Value;
    }

    private async Task<Row> RowAsync(string gameId)
    {
        using var scope = host.Services.CreateScope();
        var g = await scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Games.AsNoTracking().SingleAsync(g => g.Id == gameId);
        return new Row(g.Status, g.HomeScore, g.AwayScore, g.StatusDetail, g.Period, g.ScoreboardUpdatedAt, g.ScoreboardSourceRevisionAt,
            g.MarketObservedAt) { Json = g.MlbScoreboardJson };
    }

    private async Task<List<(DateTime, double)>> OddsAsync()
    {
        using var scope = host.Services.CreateScope();
        var odds = await scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().OddsSnapshots.AsNoTracking()
            .OrderBy(o => o.Timestamp).Select(o => new { o.Timestamp, o.HomeWinProbability }).ToListAsync();
        return [.. odds.Select(o => (o.Timestamp, o.HomeWinProbability))];
    }

    private async Task UpdateAsync(Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<Game>> setters)
    {
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Games.Where(g => g.Id == "991300104").ExecuteUpdateAsync(setters);
    }
}
