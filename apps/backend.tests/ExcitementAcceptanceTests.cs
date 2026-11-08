using AngleSharp.Html.Parser;
using System.Text.Json;
using backend.Data;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FSharp.Collections;
using Xunit;
using Xunit.Abstractions;

namespace backend.tests;

public sealed class ExcitementAcceptanceTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly HostedGameFixture host = new();

    public Task InitializeAsync() => host.InitializeAsync();
    public Task DisposeAsync() => host.DisposeAsync();

    [Theory]
    [InlineData("990000001", 20.83333, "21", "low")]
    [InlineData("990000002", 56.16, "56", "medium")]
    public async Task Constructed_staircase_histories_match_independent_analytic_expectations(
        string gameId, double expected, string displayed, string band)
    {
        var capture = await host.SeedSyntheticHistoryAsync(gameId);
        host.Clock.SetUtcNow(capture.Context.CaptureCutoff);
        var actual = (await Scores(gameId))[gameId];

        Assert.NotNull(actual);
        output.WriteLine($"{gameId}: {actual.Value:R}");
        Assert.Equal(expected, actual.Value, 9);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await host.Client.GetStringAsync($"/{capture.Context.Sport}"));
        AssertRating(document, gameId, displayed, band);
    }

    [Theory]
    [InlineData("990000001", new double[] { 5.833333333333333, 11.666666666666666, 17.5, 20.83333 })]
    [InlineData("990000002", new double[] { 12.48, 31.2, 43.68, 56.16 })]
    public async Task Constructed_prefixes_do_not_receive_future_steps(string gameId, double[] expected)
    {
        var capture = SyntheticExcitementHistory.Load(gameId);
        var snapshots = capture.CreateSnapshots().ToArray();
        var game = capture.CreateGame();
        int[] counts = [100, 200, 250, snapshots.Length];
        var previousCount = 0;

        for (var i = 0; i < counts.Length; i++)
        {
            var count = counts[i];
            var last = snapshots[count - 1];
            host.Clock.SetUtcNow(last.Timestamp);
            game.Status = count == snapshots.Length ? capture.Context.Status : "in_progress";
            game.HomeScore = last.HomeScore;
            game.AwayScore = last.AwayScore;
            game.Period = last.Period;
            game.GameClock = last.GameClock;
            if (i == 0) await host.SeedAsync(game, snapshots.Take(count).Reverse());
            else
            {
                using var scope = host.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
                db.Games.Update(game);
                db.OddsSnapshots.AddRange(snapshots.Skip(previousCount).Take(count - previousCount).Reverse());
                await db.SaveChangesAsync();
            }

            var actual = (await Scores(gameId))[gameId];
            Assert.NotNull(actual);
            output.WriteLine($"{gameId} prefix {count}, {last.Timestamp:O}: {actual.Value:R}");
            Assert.Equal(expected[i], actual.Value, 9);
            if (i < counts.Length - 1) Assert.True(actual < expected[^1]);
            using var json = JsonDocument.Parse(await host.Client.GetStringAsync($"/api/games/{gameId}/odds"));
            Assert.Equal(game.Status, json.RootElement.GetProperty("status").GetString());
            var deliveredLast = json.RootElement.GetProperty("oddsHistory").EnumerateArray().Last();
            Assert.Equal(last.Timestamp, deliveredLast.GetProperty("timestamp").GetDateTime());
            Assert.Equal(last.HomeScore, deliveredLast.GetProperty("homeScore").GetInt32());
            Assert.Equal(last.AwayScore, deliveredLast.GetProperty("awayScore").GetInt32());
            previousCount = count;
        }
    }

    [Theory]
    [InlineData("cfb")]
    [InlineData("nfl")]
    [InlineData("nba")]
    [InlineData("nhl")]
    [InlineData("mlb")]
    public async Task HTTP_rates_substantial_drama_identically_despite_sport_score_clock_strength_stakes_and_completion(string sport)
    {
        var start = new DateTime(2026, 9, 20, 17, 0, 0, DateTimeKind.Utc);
        host.Clock.SetUtcNow(start.AddMinutes(10));
        foreach (var completed in new[] { false, true })
        {
            var id = $"{sport}-{(completed ? "late-final" : "early-live")}";
            var game = new Game
            {
                Id = id, Sport = sport, HomeTeam = "Home", AwayTeam = "Away",
                HomeTeamAbbreviation = sport == "cfb" ? "OKST" : "CHI",
                AwayTeamAbbreviation = sport == "cfb" ? "UCF" : "MIN",
                ScheduledStart = start, SeasonYear = 2026, SeasonType = completed ? 3 : 2, WeekNumber = 1,
                Status = completed ? "final" : "in_progress", Period = completed ? 9 : 1,
                GameClock = completed ? "0:00" : "14:00", HomeScore = completed ? 99 : 0, AwayScore = completed ? 93 : 0,
                HomeFpi = completed ? 30 : -20, AwayFpi = completed ? 25 : -15,
                HomePollRank = completed ? 1 : null, AwayPollRank = completed ? 2 : null, RankPoll = completed ? CollegePoll.Ap : null,
                CollegeCompetition = completed ? "cfp" : "regular"
            };
            var evidence = new ProGameEvidence
            {
                GameId = id, Sport = sport, Season = 2026, Phase = game.SeasonType,
                Home = game.HomeTeamAbbreviation, Away = game.AwayTeamAbbreviation, ScheduledStart = start, ObservedAt = start,
                Source = "fixture", HomeElimination = completed, AwayElimination = completed, Round = completed ? 4 : null
            };
            game.ProEvidenceJson = JsonSerializer.Serialize(evidence);
            var snapshots = Enumerable.Range(0, 11).Select(i => new OddsSnapshot
            {
                GameId = id, Timestamp = start.AddMinutes(i), HomeWinProbability = i % 2 == 0 ? 0.1 : 0.9,
                AwayWinProbability = i % 2 == 0 ? 0.9 : 0.1, Source = "Kalshi",
                Period = game.Period, GameClock = game.GameClock, HomeScore = game.HomeScore, AwayScore = game.AwayScore
            });
            await host.SeedAsync(game, snapshots);
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
                await db.TeamRatings.Where(r => r.Sport == sport).ExecuteDeleteAsync();
                db.TeamRatings.AddRange(new[] { game.HomeTeamAbbreviation, game.AwayTeamAbbreviation }.Select((team, i) => new TeamRating
                {
                    Sport = sport, TeamAbbreviation = team, Season = 2026, SourceSeason = 2026,
                    Source = sport == "nhl" ? "moneypuck" : "powerrankingsguru",
                    PowerRank = completed ? i + 1 : 28 + i, PlayoffProbability = completed ? .99 : .01,
                    FetchedAt = start, RankPublishedAt = start
                }));
                await db.SaveChangesAsync();
            }
            var path = sport is "cfb" or "nfl"
                ? $"/{sport}?season=2026&type={game.SeasonType}&week=1"
                : $"/{sport}?date=2026-09-20";
            using var document = await new HtmlParser().ParseDocumentAsync(await host.Client.GetStringAsync(path));
            AssertRating(document, id, "66", "high");
            Assert.Equal(completed ? "Final" : "Live", document.QuerySelector($"#status-{id}")!.TextContent.Trim());
            Assert.Equal(completed ? "99" : "0", document.QuerySelector($"#home-score-{id}")!.TextContent.Trim());
        }
    }

    [Fact]
    public async Task HTTP_distinguishes_excluded_or_insufficient_evidence_from_observed_flat_and_noise_zero()
    {
        var capture = SyntheticExcitementHistory.Load("990000001");
        host.Clock.SetUtcNow(capture.Context.CaptureCutoff);
        foreach (var kind in new[] { "eligible", "imported", "recovered", "empty", "one", "flat", "noise" })
        {
            var game = capture.CreateGame();
            game.Id = kind;
            game.IsHistoricalImport = kind == "imported";
            var snapshots = kind switch
            {
                "empty" => [],
                "one" => capture.CreateSnapshots().Take(1).ToArray(),
                "flat" => new[] { .5, .5 }.Select((p, i) => Observation(kind, capture.Context.CaptureCutoff.AddSeconds(i - 1), p)).ToArray(),
                "noise" => Enumerable.Range(0, 50).Select(i => Observation(kind, capture.Context.CaptureCutoff.AddSeconds(i - 49), i % 2 == 0 ? .495 : .505)).ToArray(),
                _ => capture.CreateSnapshots().ToArray()
            };
            foreach (var snapshot in snapshots)
            {
                snapshot.GameId = kind;
                snapshot.IsHistorical = kind == "recovered";
            }
            if (kind == "eligible")
                snapshots = snapshots.Concat(Enumerable.Range(0, 20).Select(i =>
                {
                    var recovered = Observation(kind, capture.Samples[0].Timestamp.AddSeconds(i), i % 2 == 0 ? .1 : .9);
                    recovered.IsHistorical = true;
                    return recovered;
                })).ToArray();
            await host.SeedAsync(game, snapshots.Reverse());
        }

        foreach (var path in new[] { "/", "/cfb" })
        {
            using var document = await new HtmlParser().ParseDocumentAsync(await host.Client.GetStringAsync(path));
            AssertRating(document, "eligible", "21", "low");
            foreach (var id in new[] { "flat", "noise" }) AssertRating(document, id, "0", "low");
            foreach (var id in new[] { "imported", "recovered", "empty", "one" })
            {
                var rating = document.QuerySelector($"#excitement-{id}");
                Assert.NotNull(rating);
                Assert.Equal("Excitement unavailable", rating.TextContent.Trim());
                Assert.Empty(rating.QuerySelectorAll(".metric-value, .home-metric-value"));
            }
        }
    }

    [Fact]
    public async Task HTTP_bound_display_expires_without_erasing_accumulated_drama_or_historical_provenance()
    {
        var capture = await host.SeedSyntheticHistoryAsync("990000001");
        host.Clock.SetUtcNow(capture.Context.CaptureCutoff);
        var before = (await Scores("990000001"))["990000001"];
        foreach (var elapsed in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(119), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(121) })
        {
            host.Clock.SetUtcNow(capture.Context.CaptureCutoff + elapsed);
            using var document = await new HtmlParser().ParseDocumentAsync(await host.Client.GetStringAsync("/cfb"));
            AssertRating(document, "990000001", "21", "low");
            var probabilities = document.QuerySelector("#probbar-990000001")!;
            if (elapsed < TimeSpan.FromMinutes(2))
            {
                Assert.Contains("OKST >99%", probabilities.TextContent);
                Assert.Contains("UCF <1%", probabilities.TextContent);
                Assert.DoesNotContain("unavailable", probabilities.TextContent);
            }
            else
            {
                Assert.Equal("Live probability unavailable", probabilities.TextContent.Trim());
                Assert.DoesNotContain(">99%", probabilities.TextContent);
                Assert.DoesNotContain("<1%", probabilities.TextContent);
            }
            using var listing = JsonDocument.Parse(await host.Client.GetStringAsync("/api/games?sport=cfb"));
            var latest = Assert.Single(listing.RootElement.EnumerateArray()).GetProperty("latestOdds");
            if (elapsed < TimeSpan.FromMinutes(2))
            {
                Assert.True(latest.GetProperty("isProbabilityBound").GetBoolean());
                Assert.Equal("Kalshi (near-certain orderbook bound)", latest.GetProperty("source").GetString());
            }
            else Assert.Equal(JsonValueKind.Null, latest.ValueKind);
            Assert.Equal(before, (await Scores("990000001"))["990000001"]);
        }
        using var history = JsonDocument.Parse(await host.Client.GetStringAsync("/api/games/990000001/odds"));
        var last = history.RootElement.GetProperty("oddsHistory").EnumerateArray().Last();
        Assert.Equal(capture.Context.CaptureCutoff, last.GetProperty("timestamp").GetDateTime());
        Assert.True(last.GetProperty("isProbabilityBound").GetBoolean());
        Assert.Equal("Kalshi (near-certain orderbook bound)", last.GetProperty("source").GetString());
        Assert.Equal(.995, last.GetProperty("homeWinProbability").GetDouble());
        Assert.Equal(0.0050000000000000044, last.GetProperty("awayWinProbability").GetDouble());
    }

    private static OddsSnapshot Observation(string id, DateTime timestamp, double home) => new()
    {
        GameId = id, Timestamp = timestamp, HomeWinProbability = home, AwayWinProbability = 1 - home, Source = "Kalshi"
    };

    private static void AssertRating(AngleSharp.Dom.IDocument document, string id, string displayed, string band)
    {
        var rating = document.QuerySelector($"#excitement-{id}");
        Assert.NotNull(rating);
        Assert.Contains("Excitement", rating.TextContent);
        var value = Assert.Single(rating.QuerySelectorAll($".excitement-{band}"));
        Assert.Equal(displayed, value.TextContent.Trim());
    }

    private async Task<IDictionary<string, double?>> Scores(params string[] ids)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<GameDataService>()
            .GetExcitementScoresForGamesAsync(ListModule.OfSeq(ids), CancellationToken.None);
    }
}
