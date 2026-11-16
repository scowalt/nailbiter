using System.Text.Json;
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

    public static Task RecordAsync(ExcitementDbContext db, Game game, double? total, HypeComponents? components, CancellationToken ct) =>
        RecordAsync(db, [(game, total, components)], ct);

    public static async Task RecordAsync(ExcitementDbContext db, IReadOnlyCollection<(Game Game, double? Total, HypeComponents? Components)> entries,
        CancellationToken ct)
    {
        var removed = entries.Where(e => e.Total is not double value || !double.IsFinite(value)).Select(e => e.Game.Id).ToArray();
        var kept = entries.Where(e => e.Total is double value && double.IsFinite(value)).Select(e =>
        {
            var credit = e.Components is null ? null : Credit(e.Game, e.Components);
            return new { game_id = e.Game.Id, total = e.Total!.Value, quality_source = credit?.QualitySource, moneypuck_teams = credit?.MoneyPuckTeams.ToArray() ?? [] };
        }).ToArray();
        if (removed.Length > 0)
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM start_hype WHERE game_id = ANY ({removed})", ct);
        if (kept.Length > 0)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO start_hype (game_id, total, quality_source, moneypuck_teams)
                SELECT entry.game_id, entry.total, entry.quality_source, ARRAY(SELECT jsonb_array_elements_text(entry.moneypuck_teams))
                FROM jsonb_to_recordset({JsonSerializer.Serialize(kept)}::jsonb)
                    AS entry(game_id text, total double precision, quality_source text, moneypuck_teams jsonb)
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
