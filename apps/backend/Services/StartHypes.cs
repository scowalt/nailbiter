using backend.Data;
using backend.Models;
using backend.Scoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.FSharp.Collections;

namespace backend.Services;

public static class StartHypes
{
    public static DerivedHypeCredit? Credit(Game game, HypeComponents components)
    {
        var teams = new List<string>();
        if (components.AwayQualificationEvidenceKind == ProQualificationEvidenceKind.MoneyPuckModel)
            teams.Add(game.AwayTeamAbbreviation);
        if (components.HomeQualificationEvidenceKind == ProQualificationEvidenceKind.MoneyPuckModel)
            teams.Add(game.HomeTeamAbbreviation);
        return teams.Count > 0 || components.QualitySource == NhlPowerRankingsGuruService.Source
            ? new DerivedHypeCredit(ListModule.OfSeq(teams), components.QualitySource)
            : null;
    }

    public static Task RecordAsync(ExcitementDbContext db, Game game, double? total, HypeComponents? components, CancellationToken ct)
    {
        if (total is not double value || !double.IsFinite(value))
            return db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM start_hype WHERE game_id = {game.Id}", ct);
        var credit = components is null ? null : Credit(game, components);
        var source = credit?.QualitySource;
        var teams = credit?.MoneyPuckTeams.ToArray() ?? [];
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO start_hype (game_id, total, quality_source, moneypuck_teams) VALUES ({game.Id}, {value}, {source}, {teams})
            ON CONFLICT (game_id) DO UPDATE SET total = EXCLUDED.total, quality_source = EXCLUDED.quality_source, moneypuck_teams = EXCLUDED.moneypuck_teams
            """, ct);
    }

    public static async Task CaptureCollegeAsync(ExcitementDbContext db, HypeScoreService scorer, DateTime now, CancellationToken ct)
    {
        var horizon = now.AddDays(1);
        var games = await db.Games.AsNoTracking().Where(g => g.Sport == "cfb" && g.Status == "scheduled" && !g.IsHistoricalImport
            && g.ActualStart == null && g.ScheduledStart > now && g.ScheduledStart < horizon).ToListAsync(ct);
        foreach (var game in games)
            await RecordAsync(db, game, await scorer.CalculateHypeScoreAsync(game, ct), null, ct);
    }

    public static async Task<Dictionary<string, StartHype>> LoadAsync(ExcitementDbContext db, IEnumerable<string> gameIds, CancellationToken ct)
    {
        var ids = gameIds.ToArray();
        return ids.Length == 0 ? [] : await db.StartHypes.AsNoTracking().Where(h => ids.Contains(h.GameId)).ToDictionaryAsync(h => h.GameId, ct);
    }
}
