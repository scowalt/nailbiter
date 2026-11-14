using System.Text.Json;
using backend.Data;
using backend.GraphQL.Types;
using backend.Models;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

internal static class CollegeFailure
{
    public static LogLevel Level(Exception ex, Func<Exception, bool> outage) =>
        ex is HttpRequestException && !outage(ex) || ex is JsonException or InvalidDataException ? LogLevel.Warning : LogLevel.Error;
}

public sealed class CollegeFootballService(
    ExcitementDbContext db, CollegeEspnClient espn, CollegeKalshiClient kalshi,
    IGameUpdateService updates, TimeProvider time, ILogger<CollegeFootballService> logger)
{
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public async Task<int> RunAsync(bool initialHistory, bool apply, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await db.Database.SqlQueryRaw<bool>("SELECT pg_try_advisory_xact_lock(674213608) AS \"Value\"").SingleAsync(ct))
            throw new InvalidOperationException("Another college collector/import is running");
        var checkpoint = await db.CollegeImports.SingleOrDefaultAsync(ct);
        var season = initialHistory && checkpoint is not null ? checkpoint.Season : CollegeFootballPolicy.SeasonAt(now);
        var today = TimeZoneInfo.ConvertTimeFromUtc(now, Eastern).Date;
        var todaySchedule = await espn.ScheduleAsync(today, season, true, ct);
        if (checkpoint is null)
        {
            var seasonStart = todaySchedule.SeasonStart ?? throw new InvalidDataException("Cannot establish college season start for initial import");
            checkpoint = new CollegeImport { Season = season, FirstImportAt = now, WindowStart = new[] { now.AddDays(-14), seasonStart }.Max() };
            if (checkpoint.WindowStart > now) checkpoint.WindowStart = now;
            db.CollegeImports.Add(checkpoint);
        }
        var teams = await db.CollegeTeams.Where(t => t.Season == season).ToListAsync(ct);
        if (teams.Count == 0 || teams.Min(t => t.FetchedAt) < now.AddHours(-6))
        {
            try
            {
                var roster = await espn.RosterAsync(season, ct);
                var existing = teams.ToDictionary(t => t.EspnId);
                foreach (var team in roster)
                {
                    if (existing.TryGetValue(team.EspnId, out var stored))
                    {
                        stored.Abbreviation = team.Abbreviation; stored.Name = team.Name;
                        stored.Fpi = team.Fpi; stored.FpiUpdatedAt = team.FpiUpdatedAt; stored.FetchedAt = now;
                    }
                    else { db.CollegeTeams.Add(team); teams.Add(team); }
                }
                var rosterIds = roster.Select(t => t.EspnId).ToHashSet();
                foreach (var removed in teams.Where(t => !rosterIds.Contains(t.EspnId)).ToList())
                { db.CollegeTeams.Remove(removed); teams.Remove(removed); }
                try
                {
                    var rankings = await espn.RankingsAsync(season, ct);
                    if (rankings is { } poll)
                        foreach (var team in teams)
                        {
                            team.PollRank = poll.Ranks.TryGetValue(team.EspnId, out var rank) ? rank : null;
                            team.Poll = poll.Poll;
                            team.PollUpdatedAt = poll.Date;
                        }
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException)
                { logger.Log(ProviderFailure.Is(ex) ? LogLevel.Error : LogLevel.Warning, ex, "College rankings unavailable; existing evidence will expire normally"); }
            }
            catch (Exception ex) when (teams.Count > 0 && !ct.IsCancellationRequested)
            {
                logger.Log(CollegeFailure.Level(ex, ProviderFailure.Is), ex, "College roster refresh unavailable; retaining verified season membership and expiring old metric evidence");
            }
        }
        var membership = teams.ToDictionary(t => t.EspnId);
        var since = initialHistory ? checkpoint.WindowStart : now.AddDays(-2);
        List<JsonElement> listings;
        try { listings = await kalshi.ListingsAsync(since, initialHistory, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (initialHistory) throw;
            logger.Log(CollegeFailure.Level(ex, KalshiFailure.Is), ex, "College listings unavailable; refreshing known games only");
            listings = [];
        }
        var events = new Dictionary<string, JsonElement>();
        var firstDay = initialHistory ? TimeZoneInfo.ConvertTimeFromUtc(checkpoint.WindowStart, Eastern).Date : today.AddDays(-1);
        var lastDay = initialHistory ? TimeZoneInfo.ConvertTimeFromUtc(checkpoint.FirstImportAt, Eastern).Date : today.AddDays(7);
        for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
        {
            var schedule = day == today ? todaySchedule : await espn.ScheduleAsync(day, season, true, ct);
            foreach (var e in schedule.Events) events[e.At("id").Text()] = e;
        }
        if (!initialHistory)
        {
            var known = await db.Games.Where(g => g.Sport == "cfb" && g.SeasonYear == season
                && g.Status != "canceled" && (g.Status != "final" || g.WeekNumber == null)).ToListAsync(ct);
            foreach (var game in known.Where(g => !events.ContainsKey(g.Id)))
            {
                try { events[game.Id] = await espn.EventAsync(game.Id, ct); }
                catch (Exception ex) when (!ct.IsCancellationRequested) { logger.Log(CollegeFailure.Level(ex, ProviderFailure.Is), ex, "College event {Event} missing; preserving stored state", game.Id); }
            }
        }
        var changed = new List<Game>();
        foreach (var e in events.Values)
        {
            var game = await StoreAsync(e, season, membership, listings, checkpoint, initialHistory, ct);
            if (game is not null) changed.Add(game);
        }
        if (!apply)
        {
            logger.LogInformation("DRY RUN: {Count} eligible college games; fixed initial window {Start:o} to {End:o}. No writes.", changed.Count, checkpoint.WindowStart, checkpoint.FirstImportAt);
            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return changed.Count;
        }
        await db.LockProHypeFenceAsync(ProHypeFenceAccess.WriteGames, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        var publishable = changed.Where(g => !initialHistory && g.Status != "scheduled").ToList();
        if (publishable.Count == 0) return changed.Count;
        var gameIds = publishable.Select(g => g.Id).ToList();
        var scores = await new GameDataService(db, time).GetExcitementScoresForGamesAsync(
            Microsoft.FSharp.Collections.ListModule.OfSeq(gameIds), ct);
        var latestByGame = await db.OddsSnapshots.Where(o => gameIds.Contains(o.GameId) && !o.IsHistorical)
            .GroupBy(o => o.GameId)
            .Select(g => g.OrderByDescending(o => o.Timestamp).ThenByDescending(o => o.Id).First())
            .ToDictionaryAsync(o => o.GameId, ct);
        foreach (var game in publishable)
        {
            latestByGame.TryGetValue(game.Id, out var latest);
            var usable = latest is not null && CollegeFootballPolicy.UsableLiveOdds(game, latest, now);
            await updates.PublishUpdateAsync(new GameUpdate
            {
                GameId = game.Id,
                Sport = Sport.CFB,
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
                Status = GameStatusExtensions.FromDbValue(game.Status) ?? GameStatus.SCHEDULED,
                IsOvertime = game.IsOvertime,
                Period = game.Period,
                GameClock = game.GameClock,
                HomeWinProbability = usable ? latest!.HomeWinProbability : null,
                AwayWinProbability = usable ? latest!.AwayWinProbability : null,
                OddsSource = usable ? latest!.Source : null,
                ExcitementScore = scores[game.Id],
                UpdatedAt = now
            }, ct);
        }
        return changed.Count;
    }

    private async Task<Game?> StoreAsync(JsonElement e, int season, Dictionary<string, CollegeTeam> membership,
        List<JsonElement> listings, CollegeImport checkpoint, bool history, CancellationToken ct)
    {
        var id = e.At("id").Text();
        if (id == "") return null;
        var game = await db.Games.FindAsync([id], ct);
        var competition = e.At("competitions").Items().FirstOrDefault();
        var home = competition.At("competitors").Items().FirstOrDefault(c => game?.HomeTeamId is not null
            ? c.At("team", "id").Text() == game.HomeTeamId : c.At("homeAway").Text() == "home");
        var away = competition.At("competitors").Items().FirstOrDefault(c => game?.AwayTeamId is not null
            ? c.At("team", "id").Text() == game.AwayTeamId : c.At("homeAway").Text() == "away");
        var reversed = home.At("homeAway").Text() == "away";
        var h = home.At("team"); var a = away.At("team");
        var homeId = h.At("id").Text(); var awayId = a.At("id").Text();
        var start = e.At("date").Date() ?? competition.At("date").Date();
        var eventSeason = e.At("season", "year").Int();
        if (id == "" || start is null || eventSeason != season || homeId == "" || awayId == "" || homeId == awayId) return null;
        if (!membership.ContainsKey(homeId) && !membership.ContainsKey(awayId)) return null;
        if (history && (start < checkpoint.WindowStart || start >= checkpoint.FirstImportAt)) return null;
        var now = time.GetUtcNow().UtcDateTime;
        var status = e.At("status");
        if (status.ValueKind == JsonValueKind.Undefined) status = competition.At("status");
        var state = status.At("type", "state").Text();
        if (state is not ("pre" or "in" or "post")) return null;
        if (history && state != "post") return null;
        if (game is not null && (game.Sport != "cfb" || game.HomeTeamId != homeId || game.AwayTeamId != awayId))
            throw new InvalidDataException($"College identity conflict for event {id}");
        var pair = game?.HomeMarketTicker is not null && game.AwayMarketTicker is not null
            ? new CollegeMarketPair(game.HomeMarketTicker, game.AwayMarketTicker)
            : CollegeKalshiClient.Match(listings, start.Value, h.At("abbreviation").Text(), a.At("abbreviation").Text(),
                Names(h), Names(a));
        if (pair is null) return null;
        if (game is null)
        {
            game = new Game
            {
                Id = id,
                Sport = "cfb",
                HomeTeam = h.At("displayName").Text(),
                AwayTeam = a.At("displayName").Text(),
                HomeTeamId = homeId,
                AwayTeamId = awayId,
                HomeTeamAbbreviation = h.At("abbreviation").Text(),
                AwayTeamAbbreviation = a.At("abbreviation").Text(),
                IsHistoricalImport = history || state == "post"
            };
            db.Games.Add(game);
        }
        game.HomeMarketTicker = pair.HomeTicker; game.AwayMarketTicker = pair.AwayTicker;
        game.ScheduledStart = start.Value; game.SeasonYear = season;
        game.SeasonType = e.At("season", "type").Int() ?? e.At("season", "type", "type").Int();
        var week = e.At("week", "number").Int() ?? e.At("week").Int();
        if (week is >= 0)
        {
            game.WeekNumber = week;
            game.WeekLabel = CollegeFootballPolicy.WeekLabel(game.SeasonType, week.Value);
        }
        game.Status = status.At("type", "name").Text() == "STATUS_CANCELED" ? "canceled" : state switch { "in" => "in_progress", "post" => "final", _ => "scheduled" };
        game.HomeScore = state == "pre" ? null : home.At("score").Int() ?? home.At("score", "displayValue").Int();
        game.AwayScore = state == "pre" ? null : away.At("score").Int() ?? away.At("score", "displayValue").Int();
        game.Period = status.At("period").Int(); game.GameClock = status.At("displayClock").Text();
        game.StatusDetail = status.At("type", "shortDetail").Text(); game.IsOvertime = game.Period > 4;
        game.ScoreboardUpdatedAt = now; game.HomeColor = CollegeFootballPolicy.Color(h.At("color").Text()); game.AwayColor = CollegeFootballPolicy.Color(a.At("color").Text());
        game.CompetitionEvidence = string.Join(" | ", competition.At("notes").Items().Select(n => n.At("headline").Text()));
        game.CollegeCompetition = CollegeFootballPolicy.Classify(game.SeasonType ?? 0, game.CompetitionEvidence, competition.At("type", "id").Text());
        var networks = backend.Services.Espn.EspnBroadcasts.networks(competition.At("broadcasts"), competition.At("geoBroadcasts"));
        if (!networks.IsEmpty) game.BroadcastNetworks = networks.ToArray();
        if (!game.IsHistoricalImport && (state == "pre" || state == "in"))
        {
            membership.TryGetValue(homeId, out var ht); membership.TryGetValue(awayId, out var at);
            game.HomeFpi = CollegeFootballPolicy.Fresh(ht?.FpiUpdatedAt, now, TimeSpan.FromDays(8)) ? ht?.Fpi : null;
            game.AwayFpi = CollegeFootballPolicy.Fresh(at?.FpiUpdatedAt, now, TimeSpan.FromDays(8)) ? at?.Fpi : null;
            CollegePoll? FreshPoll(CollegeTeam? team) => CollegeFootballPolicy.Fresh(team?.PollUpdatedAt, now, TimeSpan.FromDays(8)) ? team?.Poll : null;
            var homePoll = FreshPoll(ht); var awayPoll = FreshPoll(at);
            game.RankPoll = homePoll ?? awayPoll;
            game.HomePollRank = homePoll is not null && homePoll == game.RankPoll ? ht?.PollRank : null;
            game.AwayPollRank = awayPoll is not null && awayPoll == game.RankPoll ? at?.PollRank : null;
            game.RatingsObservedAt = now;
            if (state == "pre")
            {
                var pregame = CollegeEspnClient.Pregame(competition, id, now);
                if (pregame is not null) await SavePregame(pregame, reversed, ct);
                else
                {
                    var probability = await kalshi.ObservePregameProbabilityAsync(pair, game.ScheduledStart, now, ct);
                    if (probability is { } observation)
                        await SavePregame(new PregameOdds
                        {
                            GameId = id,
                            HomeWinProbability = observation.HomeProbability,
                            AwayWinProbability = 1 - observation.HomeProbability,
                            SourceSeason = season,
                            ApplicableStart = game.ScheduledStart,
                            HomeTeamAbbreviation = game.HomeTeamAbbreviation,
                            AwayTeamAbbreviation = game.AwayTeamAbbreviation,
                            FetchedAt = now,
                            Source = observation.Source
                        }, false, ct);
                }
            }
        }
        if (!history && state == "in")
        {
            game.ActualStart ??= now;
            var probability = await kalshi.ObserveProbabilityAsync(pair, now, ct);
            game.MarketObservedAt = now;
            if (probability is { } observation)
                db.OddsSnapshots.Add(new OddsSnapshot
                {
                    GameId = id,
                    Timestamp = now,
                    HomeWinProbability = observation.HomeProbability,
                    AwayWinProbability = 1 - observation.HomeProbability,
                    Source = observation.Source,
                    Period = game.Period,
                    GameClock = game.GameClock,
                    HomeScore = game.HomeScore,
                    AwayScore = game.AwayScore
                });
        }
        if (history && game.IsHistoricalImport)
        {
            var closing = await espn.ClosingOddsAsync(id, ct);
            if (closing is not null) await SavePregame(closing, reversed, ct);
            var window = await espn.PlayWindowAsync(id, ct);
            if (window is not null && window.Value.Start >= game.ScheduledStart.AddHours(-1)
                && window.Value.Start <= game.ScheduledStart.AddDays(1) && window.Value.End > window.Value.Start
                && window.Value.End - window.Value.Start < TimeSpan.FromDays(1))
            {
                game.ActualStart = window.Value.Start; game.EndTime = window.Value.End;
                var candles = await kalshi.HistoryAsync(pair, window.Value.Start, window.Value.End, ct);
                var existing = await db.OddsSnapshots.Where(o => o.GameId == id && o.IsHistorical).Select(o => o.Timestamp).ToListAsync(ct);
                var timestamps = existing.ToHashSet();
                foreach (var candle in candles.Where(c => timestamps.Add(c.Timestamp)))
                    db.OddsSnapshots.Add(new OddsSnapshot
                    {
                        GameId = id,
                        Timestamp = candle.Timestamp,
                        HomeWinProbability = candle.HomeProbability,
                        AwayWinProbability = 1 - candle.HomeProbability,
                        IsHistorical = true,
                        Source = CollegeFootballPolicy.HistoricalSource
                    });
            }
        }
        return game;
    }

    private static string[] Names(JsonElement team) => [team.At("displayName").Text(), team.At("shortDisplayName").Text(), team.At("location").Text()];
    private async Task SavePregame(PregameOdds odds, bool reversed, CancellationToken ct)
    {
        if (reversed)
        {
            (odds.HomeMoneyline, odds.AwayMoneyline) = (odds.AwayMoneyline, odds.HomeMoneyline);
            (odds.HomeWinProbability, odds.AwayWinProbability) = (odds.AwayWinProbability, odds.HomeWinProbability);
            odds.Spread = -odds.Spread;
        }
        var existing = await db.PregameOdds.FindAsync([odds.GameId], ct);
        if (existing is null) db.PregameOdds.Add(odds);
        else db.Entry(existing).CurrentValues.SetValues(odds);
    }
}

public sealed class CollegeFootballBackgroundService(IServiceScopeFactory scopes, ILogger<CollegeFootballBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<CollegeFootballService>().RunAsync(false, true, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "College refresh failed; stored games retained"); }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
