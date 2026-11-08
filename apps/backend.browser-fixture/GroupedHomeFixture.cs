using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using backend.Data;
using backend.Models;
using backend.GraphQL.Types;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Text.Json;

namespace BrowserFixture;

internal sealed class GroupedHomeFixture : TimeProvider
{
    public static readonly DateTimeOffset Now = new(2026, 10, 13, 14, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    private volatile bool failRefresh;
    private int rejectedSnapshots;
    private sealed record CollegeRecipe(string Competition, double HomeFpi, double AwayFpi, double? Probability);
    private static readonly CollegeRecipe UnavailableCollegeRecipe = new("bowl", 11000, 11000, null);
    private static readonly IReadOnlyDictionary<double, CollegeRecipe> CollegeRecipes = new Dictionary<double, CollegeRecipe>
    {
        [0] = new("bowl", -11000, 0, 1),
        [39.99] = new("bowl", 11000, 11000, .87525),
        [40] = new("bowl", 11000, 11000, .875),
        [69.99] = new("cfp", 0, 0, .81275),
        [70] = new("cfp", 0, 0, .8125),
        [100] = new("cfp", 11000, 11000, .5),
    };

    public void Configure(IServiceCollection services)
    {
        services.RemoveAll<TimeProvider>();
        services.AddSingleton<TimeProvider>(this);
        services.AddSingleton<IStartupFilter>(new SnapshotFailure(this));
        services.Configure<PlayoffRatingsOptions>(options =>
        {
            options.MoneyPuckSelectionEnabled = true;
            options.MoneyPuckApprovedSeasons = [2027];
        });
    }

    public void Seed(ExcitementDbContext db)
    {
        AddCollege(db, "group-null-late", 3, null);
        AddCollege(db, "group-good-6999", 6, 69.99);
        AddCollege(db, "group-null-tie-a", 2, null);
        AddCollege(db, "group-best-100", 5, 100);
        AddCollege(db, "group-low-zero", 2, 0);
        AddCollege(db, "group-good-40", 7, 40);
        AddCollege(db, "group-null-early", 1, null);
        AddCollege(db, "group-best-70", 8, 70);
        AddCollege(db, "group-low-3999", 1.5, 39.99);
        db.Games.AddRange(
            Game("group-live-today", "nba", -1, "in_progress"),
            Game("group-live-old", "nba", -24, "in_progress"),
            Game("group-final", "nba", -2, "final"),
            Game("group-suspended", "mlb", -3, "suspended"),
            Game("group-postponed", "mlb", 9, "postponed"),
            Game("group-hidden-canceled", "nba", 1, "canceled"),
            Game("group-hidden-world-cup", "world_cup", 1));
        var preseason = Game("group-hidden-preseason", "nba", 1);
        preseason.SeasonType = 1;
        db.Games.Add(preseason);
        var nba = Game("group-best-nba", "nba", 4);
        nba.SeasonType = 3;
        nba.ProEvidenceJson = JsonSerializer.Serialize(new ProGameEvidence
        {
            GameId = nba.Id, Sport = nba.Sport, Season = 2027, Home = nba.HomeTeamAbbreviation, Away = nba.AwayTeamAbbreviation,
            ScheduledStart = nba.ScheduledStart, ObservedAt = Now.UtcDateTime, Source = "synthetic fixture: verified postseason",
            VerifiedUnstarted = true, Phase = 3, Round = 1, HomeElimination = false, AwayElimination = false,
        });
        db.Games.Add(nba);
        foreach (var team in new[] { nba.HomeTeamAbbreviation, nba.AwayTeamAbbreviation })
            db.TeamRatings.Add(new TeamRating { Sport = "nba", Season = 2027, SourceSeason = 2027, TeamAbbreviation = team,
                Source = "espn_bpi", PowerRank = 1, FetchedAt = Now.UtcDateTime });
        db.TeamStandings.AddRange(
            new TeamStanding { Sport = "nba", TeamAbbreviation = "BOS", Season = 2027, ConferenceRank = 1, FetchedAt = Now.UtcDateTime },
            new TeamStanding { Sport = "nba", TeamAbbreviation = "NYK", Season = 2027, ConferenceRank = 2, FetchedAt = Now.UtcDateTime });
        db.PregameOdds.Add(Odds(nba, .5, "espn"));
        SeedPartialNhl(db);
        foreach (var id in new[] { "group-live-today", "group-live-old", "group-final" })
            db.OddsSnapshots.AddRange(new[] { .5, .8, .35, .65 }.Select((probability, index) => new OddsSnapshot
            {
                GameId = id, Timestamp = Now.AddMinutes(-60 + index * 15).UtcDateTime,
                HomeWinProbability = probability, AwayWinProbability = 1 - probability,
                HomeScore = 5, AwayScore = 2, Period = 3, GameClock = "5:00", Source = "Fixture",
            }));
        db.SaveChanges();
    }

    private sealed class SnapshotFailure(GroupedHomeFixture fixture) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                if (fixture.failRefresh && context.Request.Headers["X-Nailbiter-Live-Snapshot"] == "1")
                {
                    Interlocked.Increment(ref fixture.rejectedSnapshots);
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return;
                }
                await continuation(context);
            });
            next(app);
        };
    }

    private static void SeedPartialNhl(ExcitementDbContext db)
    {
        var game = Game("group-null-nhl", "nhl", 2.5);
        game.HomeTeam = "Fixture Ottawa"; game.HomeTeamAbbreviation = "OTT";
        game.AwayTeam = "Fixture Nashville"; game.AwayTeamAbbreviation = "NSH";
        db.Games.Add(game);
        db.PregameOdds.Add(Odds(game, .5, "espn"));
        foreach (var (team, rank) in new[] { ("OTT", 1), ("NSH", 2) })
            db.TeamRatings.Add(new TeamRating { Sport = "nhl", TeamAbbreviation = team, Season = 2027, SourceSeason = 2027,
                Source = "moneypuck", PowerRank = rank, RankPublishedAt = Now.UtcDateTime, FetchedAt = Now.UtcDateTime });
        db.TeamRatings.Add(new TeamRating { Sport = "nhl", TeamAbbreviation = "OTT", Season = 2027, SourceSeason = 2027,
            Source = "kalshi", PlayoffProbability = .57, FetchedAt = Now.UtcDateTime });
        var evidence = new PlayoffModelEvidence
        {
            Version = 4, Provider = "moneypuck_qualification", ProviderVerified = true,
            ValidationPolicy = "moneypuck-nhl-qualification-model-v1", Sport = "nhl", Team = "NSH", TeamVerified = true,
            Season = 2027, SeasonVerified = true, State = "estimate", ReasonCode = "accepted",
            PublishedAt = Now.UtcDateTime, PublicationBoundary = Now.UtcDateTime, FetchedAt = Now.UtcDateTime,
            RefreshId = "synthetic-grouped-home-controlled-evidence-not-provider-import", AcceptedTeamCount = 32, TotalTeamCount = 32,
            MoneyPuckSeasonProof = new MoneyPuckSeasonProof
            {
                PageSeasonLabel = "2026–27", PageSeasonStartYear = 2026, PageSeasonEndYear = 2027,
                PageReference = "https://moneypuck.com/predictions.htm", PageObservedAt = Now.UtcDateTime,
                FirstSimulatedId = 2099020002,
                VerifiedGames = [new NhlSimulatedGameSeasonEvidence { SimulatedId = 2099020002, GameId = 2099020002,
                    Season = 20262027, Reference = "https://api-web.nhle.com/v1/gamecenter/2099020002/landing", ObservedAt = Now.UtcDateTime }],
            },
        };
        db.TeamRatings.Add(new TeamRating { Sport = "nhl", TeamAbbreviation = "NSH", Season = 2027, SourceSeason = 2027,
            Source = "moneypuck_qualification", PlayoffProbability = .2518, FetchedAt = Now.UtcDateTime,
            PlayoffEvidenceJson = JsonSerializer.Serialize(evidence) });
    }

    private static Game Game(string id, string sport, double hours, string status = "scheduled") => new()
    {
        Id = id, Sport = sport,
        HomeTeam = sport == "nba" ? "Fixture Boston" : "Fixture Atlanta",
        AwayTeam = sport == "nba" ? "Fixture New York" : "Fixture Los Angeles",
        HomeTeamAbbreviation = sport == "nba" ? "BOS" : "ATL", AwayTeamAbbreviation = sport == "nba" ? "NYK" : "LAD",
        ScheduledStart = Now.AddHours(hours).UtcDateTime, SeasonYear = sport is "nba" or "nhl" ? 2027 : 2026,
        SeasonType = 2, Status = status, HomeScore = status == "scheduled" ? 0 : 5, AwayScore = status == "scheduled" ? 0 : 2,
        Period = status == "scheduled" ? 0 : 3, GameClock = status == "final" ? "0:00" : "5:00",
    };

    private static PregameOdds Odds(Game game, double probability, string source) => new()
    {
        GameId = game.Id, HomeWinProbability = probability, AwayWinProbability = 1 - probability,
        Source = source, SourceSeason = game.SeasonYear, ApplicableStart = game.ScheduledStart,
        HomeTeamAbbreviation = game.HomeTeamAbbreviation, AwayTeamAbbreviation = game.AwayTeamAbbreviation,
        FetchedAt = Now.UtcDateTime,
    };

    private static void AddCollege(ExcitementDbContext db, string id, double hours, double? score)
    {
        var game = Game(id, "cfb", hours);
        game.SeasonType = 3;
        game.HomeTeam = "Fixture Alabama"; game.AwayTeam = "Fixture Texas";
        game.HomeTeamAbbreviation = "ALA"; game.AwayTeamAbbreviation = "TEX";
        var recipe = ApplyCollegeContext(game, score);
        game.CompetitionEvidence = "Synthetic fixture CFP / non-CFP bowl context";
        game.RatingsObservedAt = Now.UtcDateTime;
        game.HomePollRank = 1; game.AwayPollRank = 2; game.RankPoll = CollegePoll.Ap;
        game.HomeTeamId = "333"; game.AwayTeamId = "251";
        game.HomeColor = "9e1b32"; game.AwayColor = "e87722";
        db.Games.Add(game);
        if (recipe.Probability is double probability)
        {
            db.PregameOdds.Add(Odds(game, probability, "Kalshi"));
        }
    }

    private static CollegeRecipe ApplyCollegeContext(Game game, double? score)
    {
        var recipe = score is null ? UnavailableCollegeRecipe
            : CollegeRecipes.TryGetValue(score.Value, out var supported) ? supported
            : throw new ArgumentException("Unsupported fixture boundary");
        game.CollegeCompetition = recipe.Competition;
        game.HomeFpi = recipe.HomeFpi;
        game.AwayFpi = recipe.AwayFpi;
        return recipe;
    }

    private static async Task ChangeStartAsync(ExcitementDbContext db, Game game, DateTime start)
    {
        game.ScheduledStart = start;
        var odds = await db.PregameOdds.SingleOrDefaultAsync(o => o.GameId == game.Id);
        if (odds is not null) odds.ApplicableStart = start;
        var evidence = ProGameEvidence.Parse(game.ProEvidenceJson);
        if (evidence is not null)
        {
            evidence.ScheduledStart = start;
            game.ProEvidenceJson = JsonSerializer.Serialize(evidence);
        }
        await db.SaveChangesAsync();
    }

    public async Task<object> ControlAsync(IServiceProvider services, string command)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 && parts[0] == "hype")
        {
            var game = await db.Games.SingleAsync(g => g.Id == parts[1] && g.Sport == "cfb");
            double? requested = parts[2] == "null" ? null : double.Parse(parts[2], global::System.Globalization.CultureInfo.InvariantCulture);
            var recipe = ApplyCollegeContext(game, requested);
            var odds = await db.PregameOdds.SingleOrDefaultAsync(o => o.GameId == game.Id);
            if (recipe.Probability is double probability)
            {
                if (odds is null) db.PregameOdds.Add(Odds(game, probability, "Kalshi"));
                else
                {
                    odds.HomeWinProbability = probability;
                    odds.AwayWinProbability = 1 - odds.HomeWinProbability;
                }
            }
            else if (odds is not null) db.PregameOdds.Remove(odds);
            await db.SaveChangesAsync();
        }
        else if (parts.Length == 3 && parts[0] == "start")
        {
            var game = await db.Games.SingleAsync(g => g.Id == parts[1]);
            var start = DateTimeOffset.Parse(parts[2], global::System.Globalization.CultureInfo.InvariantCulture).UtcDateTime;
            await ChangeStartAsync(db, game, start);
        }
        else if (parts.Length == 3 && parts[0] == "status")
        {
            if (parts[2] is not ("scheduled" or "in_progress" or "final" or "postponed" or "suspended" or "canceled"))
                throw new ArgumentException("Unsupported fixture status");
            var game = await db.Games.SingleAsync(g => g.Id == parts[1]);
            game.Status = parts[2];
            game.ActualStart = game.Status is "in_progress" or "final" or "suspended" ? Now.UtcDateTime : null;
            game.HomeScore = game.Status == "scheduled" ? 0 : 5;
            game.AwayScore = game.Status == "scheduled" ? 0 : 2;
            game.Period = game.Status == "scheduled" ? 0 : 3;
            game.GameClock = game.Status == "final" ? "0:00" : "5:00";
            await db.SaveChangesAsync();
        }
        else if (parts.Length == 2 && parts[0] == "remove")
        {
            db.Games.Remove(await db.Games.SingleAsync(g => g.Id == parts[1]));
            await db.SaveChangesAsync();
        }
        else if (parts.Length == 4 && parts[0] == "add")
        {
            if (!parts[1].StartsWith("group-", StringComparison.Ordinal) || await db.Games.AnyAsync(g => g.Id == parts[1]))
                throw new ArgumentException("A new grouped-home identity is required");
            double? score = parts[2] == "null" ? null : double.Parse(parts[2], global::System.Globalization.CultureInfo.InvariantCulture);
            var start = DateTimeOffset.Parse(parts[3], global::System.Globalization.CultureInfo.InvariantCulture).UtcDateTime;
            AddCollege(db, parts[1], (start - Now.UtcDateTime).TotalHours, score);
            await db.SaveChangesAsync();
        }
        else if (parts.Length == 2 && parts[0] == "scenario")
        {
            var scenario = parts[1];
            if (scenario is not ("mixed" or "no-best" or "no-good" or "no-more" or "all-unavailable" or "single" or "live-only" or "other-only" or "empty"))
                throw new ArgumentException("Unknown grouped-home scenario");
            await using var transaction = await db.Database.BeginTransactionAsync();
            db.Games.RemoveRange(await db.Games.ToListAsync());
            db.TeamRatings.RemoveRange(await db.TeamRatings.ToListAsync());
            db.TeamStandings.RemoveRange(await db.TeamStandings.ToListAsync());
            await db.SaveChangesAsync();
            Seed(db);
            var seeded = await db.Games.ToListAsync();
            string[] best = ["group-best-100", "group-best-70", "group-best-nba"];
            string[] good = ["group-good-40", "group-good-6999"];
            string[] more = ["group-low-3999", "group-low-zero", "group-null-early", "group-null-late", "group-null-tie-a", "group-null-nhl"];
            bool Keep(Game game) => scenario switch
            {
                "no-best" => !best.Contains(game.Id),
                "no-good" => !good.Contains(game.Id),
                "no-more" => !more.Contains(game.Id),
                "all-unavailable" => game.Status == "scheduled",
                "single" => game.Id == "group-best-70",
                "live-only" => game.Status == "in_progress",
                "other-only" => game.Status is "final" or "postponed" or "suspended",
                "empty" => false,
                _ => true,
            };
            db.Games.RemoveRange(seeded.Where(game => !Keep(game)));
            if (scenario == "all-unavailable") db.PregameOdds.RemoveRange(await db.PregameOdds.ToListAsync());
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        else if (parts.Length == 2 && parts[0] == "invalid-evidence")
        {
            var game = await db.Games.SingleAsync(g => g.Id == parts[1] && g.Sport == "cfb");
            game.HomeFpi = double.NaN;
            await db.SaveChangesAsync();
        }
        else if (parts.Length == 4 && parts[0] == "scores")
        {
            var game = await db.Games.SingleAsync(g => g.Id == parts[1]);
            game.HomeScore = parts[2] == "null" ? null : int.Parse(parts[2], global::System.Globalization.CultureInfo.InvariantCulture);
            game.AwayScore = parts[3] == "null" ? null : int.Parse(parts[3], global::System.Globalization.CultureInfo.InvariantCulture);
            await db.SaveChangesAsync();
        }
        else if (parts.Length == 2 && parts[0] == "publish")
        {
            var game = await db.Games.SingleAsync(g => g.Id == parts[1]);
            var odds = await db.OddsSnapshots.Where(o => o.GameId == game.Id).OrderByDescending(o => o.Timestamp).FirstOrDefaultAsync();
            var scores = await scope.ServiceProvider.GetRequiredService<GameDataService>().GetExcitementScoresForGamesAsync(
                Microsoft.FSharp.Collections.ListModule.OfSeq(new[] { game.Id }), CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<IGameUpdateService>().PublishUpdateAsync(new GameUpdate
            {
                GameId = game.Id,
                Sport = SportExtensions.FromDbValue(game.Sport)!.Value,
                Status = GameStatusExtensions.FromDbValue(game.Status)!.Value,
                HomeTeam = game.HomeTeam,
                AwayTeam = game.AwayTeam,
                HomeTeamAbbreviation = game.HomeTeamAbbreviation,
                AwayTeamAbbreviation = game.AwayTeamAbbreviation,
                HomeTeamId = game.HomeTeamId,
                AwayTeamId = game.AwayTeamId,
                HomeColor = game.HomeColor,
                AwayColor = game.AwayColor,
                HomeScore = game.HomeScore,
                AwayScore = game.AwayScore,
                HomeWinProbability = odds?.HomeWinProbability,
                AwayWinProbability = odds?.AwayWinProbability,
                ExcitementScore = scores[game.Id],
                Period = game.Period,
                GameClock = game.GameClock,
                UpdatedAt = Now.UtcDateTime,
            });
        }
        else if (command == "fail-refresh") failRefresh = true;
        else if (command == "recover") failRefresh = false;
        else if (command != "inspect") throw new ArgumentException("Unknown grouped-home operation");
        var hype = scope.ServiceProvider.GetRequiredService<HypeScoreService>();
        var games = new List<object>();
        foreach (var game in await db.Games.OrderBy(g => g.Id).ToListAsync())
        {
            var score = await hype.CalculateHypeScoreAsync(game, null);
            var components = await hype.GetHypeComponentsAsync(game, null);
            games.Add(new { id = game.Id, status = game.Status, start = game.ScheduledStart, hype = score,
                complete = components?.IsComplete ?? false,
                qualityAvailable = components?.QualityAvailable ?? false, closenessAvailable = components?.ClosenessAvailable ?? false });
        }
        return new { now = Now, games, failRefresh, rejectedSnapshots = Volatile.Read(ref rejectedSnapshots) };
    }
}
