using backend.GraphQL.Types;
using backend.Models;
using backend.Scoring;
using backend.Services;
using backend.Services.Highlights;
using Backend.Rendering;
using Falco.Markup;
using Microsoft.FSharp.Core;

namespace backend.Routes;

public static class PageRoutes
{
    public static void MapPageRoutes(this WebApplication app)
    {
        app.MapGet("/", HandleHomePage);
        app.MapGet("/nba", (HttpContext ctx, GameDataService svc, HypeScoreService hypeSvc, IProHypeLifecycle lifecycle, CancellationToken ct) => HandleLeaguePage(Sport.NBA, svc, hypeSvc, lifecycle, ctx, ct));
        app.MapGet("/nfl", (HttpContext ctx, GameDataService svc, HypeScoreService hypeSvc, IProHypeLifecycle lifecycle, CancellationToken ct) => HandleLeaguePage(Sport.NFL, svc, hypeSvc, lifecycle, ctx, ct));
        app.MapGet("/nhl", (HttpContext ctx, GameDataService svc, HypeScoreService hypeSvc, IProHypeLifecycle lifecycle, CancellationToken ct) => HandleLeaguePage(Sport.NHL, svc, hypeSvc, lifecycle, ctx, ct));
        app.MapGet("/mlb", (HttpContext ctx, GameDataService svc, HypeScoreService hypeSvc, IProHypeLifecycle lifecycle, CancellationToken ct) => HandleLeaguePage(Sport.MLB, svc, hypeSvc, lifecycle, ctx, ct));
        app.MapGet("/cfb", (HttpContext ctx, GameDataService svc, HypeScoreService hypeSvc, IProHypeLifecycle lifecycle, CancellationToken ct) => HandleLeaguePage(Sport.CFB, svc, hypeSvc, lifecycle, ctx, ct));
        app.MapGet("/world-cup", HandleWorldCupPage);
        app.MapGet("/world-cup/match/{id}", HandleWorldCupMatchPage);
        app.MapGet("/game/{id}", HandleGameDetailPage);
    }

    private static async Task<Dictionary<string, TeamRankInfo>> GetGameTeamRanksAsync(
        IEnumerable<Game> games, GameDataService svc, CancellationToken ct)
    {
        var result = new Dictionary<string, TeamRankInfo>();
        foreach (var group in games.Where(g => !g.IsHistoricalImport && (g.Sport != "cfb" || g.Status == "scheduled"))
            .GroupBy(g => new { g.Sport, Season = PlayoffRatingPolicy.gameSeason(g), g.SeasonType }))
        {
            var teams = group.SelectMany(g => new[] { g.HomeTeamAbbreviation, g.AwayTeamAbbreviation })
                .Where(a => !string.IsNullOrEmpty(a)).Distinct().ToList();
            var ranks = await svc.GetTeamRankInfosAsync(group.Key.Sport, teams.ToFSharpList(),
                group.Key.Season, group.Key.SeasonType, ct);
            foreach (var rank in ranks)
                result[$"{group.Key.Sport}:{group.Key.Season}:{group.Key.SeasonType.GetValueOrDefault()}:{rank.Key}"] = rank.Value;
        }
        return result;
    }

    private static async Task<(Dictionary<string, double?> Scores, Dictionary<string, DerivedHypeCredit> Credits)> GetGameHypeAsync(
        List<Game> games, HypeScoreService scorer, IProHypeLifecycle lifecycle, CancellationToken ct)
    {
        var scores = new Dictionary<string, double?>();
        var credits = new Dictionary<string, DerivedHypeCredit>();
        for (var index = 0; index < games.Count; index++)
        {
            var game = games[index];
            if (game.Status != "scheduled") continue;
            if (!ProHypePolicy.Supports(game.Sport))
            {
                scores[game.Id] = await scorer.CalculateHypeScoreAsync(game, ct);
                continue;
            }
            var resolution = await lifecycle.ResolveAsync(game.Id, ct);
            if (resolution.Game is null) { games.RemoveAt(index--); continue; }
            game = games[index] = resolution.Game;
            scores[game.Id] = resolution.Total;
            if (!resolution.Total.HasValue) continue;
            var components = resolution.Components;
            var teams = new List<string>();
            if (components.AwayQualificationEvidenceKind == ProQualificationEvidenceKind.MoneyPuckModel)
                teams.Add(game.AwayTeamAbbreviation);
            if (components.HomeQualificationEvidenceKind == ProQualificationEvidenceKind.MoneyPuckModel)
                teams.Add(game.HomeTeamAbbreviation);
            if (teams.Count > 0) credits[game.Id] = new DerivedHypeCredit(teams.ToFSharpList());
        }
        return (scores, credits);
    }

    private static async Task<IResult> HandleHomePage(GameDataService svc, HypeScoreService hypeSvc, IProHypeLifecycle lifecycle, CancellationToken ct)
    {
        var todaysGames = await svc.GetTodaysGamesAsync(ct);
        var liveGames = await svc.GetLiveGamesAsync(ct);

        var allGames = todaysGames
            .Concat(liveGames)
            .Where(g => g.Sport != Sport.WORLD_CUP.ToDbValue())
            .DistinctBy(g => g.Id)
            .OrderBy(g => g.ScheduledStart)
            .ThenBy(g => g.Id, StringComparer.Ordinal)
            .ToList();

        var gameIds = allGames.Select(g => g.Id).ToList();
        var latestOdds = gameIds.Count > 0
            ? await svc.GetLatestOddsForGamesAsync(gameIds.ToFSharpList(), ct)
            : new Dictionary<string, Models.OddsSnapshot>();
        var excitementGameIds = allGames.Where(g => g.Status is "in_progress" or "final").Select(g => g.Id).ToList();
        var excitementScores = excitementGameIds.Count > 0
            ? await svc.GetExcitementScoresForGamesAsync(excitementGameIds.ToFSharpList(), ct)
            : new Dictionary<string, double?>();

        var (hypeScores, hypeCredits) = await GetGameHypeAsync(allGames, hypeSvc, lifecycle, ct);

        var teamRanks = await GetGameTeamRanksAsync(allGames, svc, ct);

        var content = HomePageRenderer.RenderWithHypeCredits(allGames, latestOdds, excitementScores, hypeScores, teamRanks, hypeCredits);
        var html = Layout.renderPage(
            "Nailbiter — Live Sports Excitement Tracker",
            "Track live sports excitement scores, win probabilities, and more",
            FSharpOption<Sport>.None,
            content);

        return Results.Content("<!DOCTYPE html>" + html, "text/html");
    }

    private static async Task<IResult> HandleWorldCupPage(GameDataService svc, CancellationToken ct)
    {
        var cards = await svc.GetWorldCupPageCardsAsync(ct);
        var content = WorldCupRenderer.RenderPage(cards);
        var html = Layout.renderPage(
            "World Cup | Nailbiter",
            "World Cup matches with home, draw, and away probabilities",
            FSharpOption<Sport>.Some(Sport.WORLD_CUP),
            content);

        return Results.Content("<!DOCTYPE html>" + html, "text/html");
    }

    private static async Task<IResult> HandleWorldCupMatchPage(string id, GameDataService svc, CancellationToken ct)
    {
        var cardOpt = await svc.GetWorldCupMatchCardAsync(id, ct);
        if (FSharpOption<WorldCupCardData>.get_IsNone(cardOpt)) return Results.NotFound();

        var card = cardOpt.Value;
        var game = card.Game;
        var snapshots = await svc.GetOddsSnapshotsAsync(id, ct);
        var content = WorldCupRenderer.RenderMatchPage(card, snapshots);
        var title = $"{game.AwayTeamAbbreviation} at {game.HomeTeamAbbreviation} | World Cup | Nailbiter";

        var html = Layout.renderPage(
            title,
            $"World Cup match page for {game.AwayTeam} at {game.HomeTeam}",
            FSharpOption<Sport>.Some(Sport.WORLD_CUP),
            content);

        return Results.Content("<!DOCTYPE html>" + html, "text/html");
    }

    private static bool UsesWeekDisplay(Sport sport) => sport is Sport.NFL or Sport.CFB;

    private static async Task<IResult> HandleLeaguePage(Sport sport, GameDataService svc, HypeScoreService hypeSvc, IProHypeLifecycle lifecycle, HttpContext ctx, CancellationToken ct)
    {
        var config = LeagueConfig.getLeagueConfig(sport);
        var sportDb = sport.ToDbValue();
        var query = ctx.Request.Query;

        List<Game> games;
        FSharpOption<CurrentWeekData> displayWeekInfo;
        int? seasonType;
        int seasonYear;
        string? prevWeekUrl = null, nextWeekUrl = null;

        if (UsesWeekDisplay(sport))
        {
            int? queryWeek = query.TryGetValue("week", out var weekStr) && int.TryParse(weekStr, out var wParsed) ? wParsed : null;
            int? querySeason = query.TryGetValue("season", out var seasonStr) && int.TryParse(seasonStr, out var sParsed) ? sParsed : null;
            int? queryType = query.TryGetValue("type", out var typeStr) && int.TryParse(typeStr, out var tParsed) ? tParsed : null;

            if (sport == Sport.CFB && queryWeek < 0)
                return Results.BadRequest("Week must be zero or greater.");

            var weekInfoOpt = await svc.GetCurrentWeekAsync(sportDb, ct);

            int weekNumber;
            if (queryWeek.HasValue)
            {
                weekNumber = queryWeek.Value;
                if (querySeason.HasValue && queryType.HasValue)
                {
                    seasonYear = querySeason.Value;
                    seasonType = queryType.Value;
                }
                else if (FSharpOption<CurrentWeekData>.get_IsSome(weekInfoOpt))
                {
                    var wi = weekInfoOpt.Value;
                    seasonYear = querySeason ?? wi.SeasonYear;
                    seasonType = queryType ?? (wi.SeasonType.HasValue ? wi.SeasonType.Value : null);
                }
                else
                {
                    seasonYear = querySeason ?? DateTime.UtcNow.Year;
                    seasonType = queryType;
                }
            }
            else if (FSharpOption<CurrentWeekData>.get_IsSome(weekInfoOpt))
            {
                var wi = weekInfoOpt.Value;
                seasonYear = wi.SeasonYear;
                weekNumber = wi.WeekNumber;
                seasonType = wi.SeasonType.HasValue ? wi.SeasonType.Value : null;
            }
            else
            {
                seasonYear = DateTime.UtcNow.Year;
                weekNumber = 0;
                seasonType = null;
            }

            displayWeekInfo = queryWeek.HasValue
                ? FSharpOption<CurrentWeekData>.Some(new CurrentWeekData(
                    seasonYear,
                    seasonType.HasValue ? new Nullable<int>(seasonType.Value) : new Nullable<int>(),
                    weekNumber,
                    sport == Sport.CFB ? CollegeFootballPolicy.WeekLabel(seasonType, weekNumber) : $"Week {weekNumber}"))
                : weekInfoOpt;

            var hasWeek = weekNumber > 0 || (sport == Sport.CFB && weekNumber == 0
                && (queryWeek.HasValue || FSharpOption<CurrentWeekData>.get_IsSome(weekInfoOpt)));
            games = await svc.GetGamesByFilterAsync(
                sportDb,
                new Nullable<int>(seasonYear),
                seasonType.HasValue ? new Nullable<int>(seasonType.Value) : new Nullable<int>(),
                hasWeek ? new Nullable<int>(weekNumber) : new Nullable<int>(),
                ct);

            if (hasWeek && seasonType.HasValue)
            {
                var adj = await svc.GetAdjacentWeeksAsync(sportDb, seasonYear, seasonType.Value, weekNumber, ct);
                var sportPath = config.Path;
                if (FSharpOption<int>.get_IsSome(adj.PrevWeek))
                    prevWeekUrl = $"{sportPath}?week={adj.PrevWeek.Value}&season={seasonYear}&type={seasonType.Value}";
                if (FSharpOption<int>.get_IsSome(adj.NextWeek))
                    nextWeekUrl = $"{sportPath}?week={adj.NextWeek.Value}&season={seasonYear}&type={seasonType.Value}";
            }
        }
        else
        {
            var eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            var nowEt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, eastern);
            var queryDate = query.TryGetValue("date", out var dateStr) && DateTime.TryParse(dateStr, out var dParsed) ? dParsed.Date : (DateTime?)null;
            var targetDate = queryDate ?? nowEt.Date;
            var etOffset = eastern.GetUtcOffset(DateTime.SpecifyKind(targetDate, DateTimeKind.Unspecified));
            var startUtc = new DateTimeOffset(targetDate, etOffset).UtcDateTime;
            var endUtc = startUtc.AddDays(1);

            games = await svc.GetGamesByDateRangeAsync(sportDb, startUtc, endUtc, ct);

            var weekInfoOpt = await svc.GetCurrentWeekAsync(sportDb, ct);
            if (FSharpOption<CurrentWeekData>.get_IsSome(weekInfoOpt))
            {
                seasonYear = weekInfoOpt.Value.SeasonYear;
                seasonType = weekInfoOpt.Value.SeasonType.HasValue ? weekInfoOpt.Value.SeasonType.Value : null;
            }
            else
            {
                seasonYear = DateTime.UtcNow.Year;
                seasonType = null;
            }

            var dateLabel = targetDate.ToString("MMM d, yyyy");
            displayWeekInfo = FSharpOption<CurrentWeekData>.Some(new CurrentWeekData(
                seasonYear,
                seasonType.HasValue ? new Nullable<int>(seasonType.Value) : new Nullable<int>(),
                0,
                dateLabel));

            var sportPath = config.Path;
            var prevDate = targetDate.AddDays(-1);
            var nextDate = targetDate.AddDays(1);
            prevWeekUrl = $"{sportPath}?date={prevDate:yyyy-MM-dd}";
            nextWeekUrl = $"{sportPath}?date={nextDate:yyyy-MM-dd}";
        }

        var gameIds = games.Select(g => g.Id).ToList();
        var latestOdds = gameIds.Count > 0
            ? await svc.GetLatestOddsForGamesAsync(gameIds.ToFSharpList(), ct)
            : new Dictionary<string, Models.OddsSnapshot>();
        var excitementGameIds = games.Where(g => g.Status is "in_progress" or "final").Select(g => g.Id).ToList();
        var excitementScores = excitementGameIds.Count > 0
            ? await svc.GetExcitementScoresForGamesAsync(excitementGameIds.ToFSharpList(), ct)
            : new Dictionary<string, double?>();

        var (hypeScores, hypeCredits) = await GetGameHypeAsync(games, hypeSvc, lifecycle, ct);

        var teamRanks = await GetGameTeamRanksAsync(games, svc, ct);

        PlayoffBracketData? bracket = null;
        if (config.DisplayOptions.ShowBracket && seasonType.HasValue && seasonType.Value == 3)
        {
            var bracketOpt = await svc.GetPlayoffBracketAsync(sportDb, seasonYear, ct);
            bracket = FSharpOption<PlayoffBracketData>.get_IsSome(bracketOpt) ? bracketOpt.Value : null;
        }
        var bracketFSharpOpt = bracket != null
            ? FSharpOption<PlayoffBracketData>.Some(bracket)
            : FSharpOption<PlayoffBracketData>.None;

        var prevWeekOpt = prevWeekUrl != null ? FSharpOption<string>.Some(prevWeekUrl) : FSharpOption<string>.None;
        var nextWeekOpt = nextWeekUrl != null ? FSharpOption<string>.Some(nextWeekUrl) : FSharpOption<string>.None;

        var content = LeaguePageRenderer.RenderWithHypeCredits(sport, config, games, latestOdds, excitementScores, hypeScores, displayWeekInfo, teamRanks, bracketFSharpOpt, prevWeekOpt, nextWeekOpt, hypeCredits);
        var html = Layout.renderPage(
            $"{config.Name} | Nailbiter",
            config.Meta.Description,
            FSharpOption<Sport>.Some(sport),
            content);

        return Results.Content("<!DOCTYPE html>" + html, "text/html");
    }

    private static async Task<IResult> HandleGameDetailPage(string id, GameDataService svc, HypeScoreService hypeSvc, IProHypeLifecycle lifecycle, HighlightsApplicationService highlightsSvc, TimeProvider time, CancellationToken ct)
    {
        var gameOpt = await svc.GetGameAsync(id, ct);
        if (FSharpOption<Game>.get_IsNone(gameOpt)) return Results.NotFound();

        var game = gameOpt.Value;
        var sportEnum = SportExtensions.FromDbValue(game.Sport);
        if (sportEnum.HasValue && sportEnum.Value == Sport.WORLD_CUP)
        {
            return Results.Redirect($"/world-cup/match/{Uri.EscapeDataString(id)}");
        }

        var snapshots = await svc.GetOddsSnapshotsAsync(id, ct);
        var pregameOddsOpt = await svc.GetPregameOddsAsync(id, ct);

        HypeComponents? hypeComponents = null;
        System.Nullable<double> hypeScore = new System.Nullable<double>();
        var homeTeamRank = FSharpOption<TeamRankInfo>.None;
        var awayTeamRank = FSharpOption<TeamRankInfo>.None;

        if (game.Status == "scheduled" && !game.IsHistoricalImport)
        {
            if (ProHypePolicy.Supports(game.Sport))
            {
                var resolution = await lifecycle.ResolveAsync(game.Id, ct);
                if (resolution.Game is null) return Results.NotFound();
                game = resolution.Game;
                sportEnum = SportExtensions.FromDbValue(game.Sport);
                hypeComponents = resolution.Components;
                hypeScore = resolution.Total;
            }
            else
            {
                hypeComponents = await hypeSvc.GetHypeComponentsAsync(game, ct);
                if (hypeComponents is not null && hypeComponents.IsComplete)
                    hypeScore = hypeSvc.CalculateFinalScore(game.Sport, hypeComponents);
            }

            var seasonType = game.SeasonType;
            var homeRankInfoOpt = await svc.GetTeamRankInfoAsync(game.Sport, game.HomeTeamAbbreviation, PlayoffRatingPolicy.gameSeason(game), seasonType, ct);
            var awayRankInfoOpt = await svc.GetTeamRankInfoAsync(game.Sport, game.AwayTeamAbbreviation, PlayoffRatingPolicy.gameSeason(game), seasonType, ct);

            homeTeamRank = homeRankInfoOpt;
            awayTeamRank = awayRankInfoOpt;
        }

        var highlights = await highlightsSvc.GetDetailAsync(id, ct);
        var content = GameDetailRenderer.RenderWithHighlightsAtTime(time.GetUtcNow().UtcDateTime, game, snapshots, pregameOddsOpt, hypeComponents, hypeScore, homeTeamRank, awayTeamRank, highlights);
        var title = $"{game.AwayTeamAbbreviation} @ {game.HomeTeamAbbreviation} | Nailbiter";

        var html = Layout.renderPage(
            title,
            $"Live win probability for {game.AwayTeam} at {game.HomeTeam}",
            sportEnum.HasValue ? FSharpOption<Sport>.Some(sportEnum.Value) : FSharpOption<Sport>.None,
            content);

        return Results.Content("<!DOCTYPE html>" + html, "text/html");
    }
}

internal static class FSharpListExtensions
{
    public static Microsoft.FSharp.Collections.FSharpList<T> ToFSharpList<T>(this List<T> list)
    {
        var result = Microsoft.FSharp.Collections.FSharpList<T>.Empty;
        for (int i = list.Count - 1; i >= 0; i--)
            result = Microsoft.FSharp.Collections.FSharpList<T>.Cons(list[i], result);
        return result;
    }
}
