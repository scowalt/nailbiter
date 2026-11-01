using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using backend.Data;
using backend.Models;
using backend.Scoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Options;

namespace backend.Services;

public sealed record ProHypeResolution(double? Total, [property: JsonIgnore] HypeComponents Components,
    [property: JsonIgnore] DateTime EvaluatedAt, [property: JsonIgnore] Game? Game = null);

public interface IProHypeLifecycle
{
    Task<ProHypeResolution> ResolveAsync(string gameId, CancellationToken ct = default);
    Task CaptureAsync(CancellationToken ct = default);
}

public sealed class ProHypeLifecycle(DbContextOptions<ExcitementDbContext> databaseOptions, HypeScoreService scorer, TimeProvider clock,
    IOptions<PlayoffRatingsOptions> options, IServiceScopeFactory scopes) : IProHypeLifecycle
{
    private readonly IModel model = Mapping(databaseOptions);

    private static IModel Mapping(DbContextOptions<ExcitementDbContext> databaseOptions)
    {
        using var context = new ExcitementDbContext(databaseOptions);
        return context.Model;
    }
    private sealed record Identity(string Id, string Sport, int? Season, int? Phase, string Home, string Away,
        string? HomeId, string? AwayId, DateTime Start)
    {
        public static Identity Of(Game game) => new(game.Id, game.Sport, game.SeasonYear,
            game.SeasonType ?? ProGameEvidence.Parse(game.ProEvidenceJson)?.Phase,
            ProHypePolicy.TeamKey(game.Sport, game.HomeTeamAbbreviation), ProHypePolicy.TeamKey(game.Sport, game.AwayTeamAbbreviation),
            game.HomeTeamId, game.AwayTeamId, game.ScheduledStart);
    }
    private sealed record Saved(Identity Identity, double Total, HypeComponents Components, DateTime EvaluatedAt,
        ProEvidenceReference[] SupportingEvidence);

    public async Task CaptureAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var horizon = now.AddDays(7);
        await using var db = new ExcitementDbContext(databaseOptions);
        var games = await db.Games.AsNoTracking().Where(g => g.Status == "scheduled" && !g.IsHistoricalImport
            && g.ScheduledStart > now && g.ScheduledStart < horizon
            && (g.Sport == "nfl" || g.Sport == "nba" || g.Sport == "nhl" || g.Sport == "mlb"))
            .OrderBy(g => g.ScheduledStart).ThenBy(g => g.Id).Select(g => g.Id).ToArrayAsync(ct);
        foreach (var gameId in games)
        {
            ct.ThrowIfCancellationRequested();
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ProHypeLifecycle>().ResolveAsync(gameId, horizon, ct);
        }
    }

    public Task<ProHypeResolution> ResolveAsync(string gameId, CancellationToken ct = default) => ResolveAsync(gameId, null, ct);

    private async Task<ProHypeResolution> ResolveAsync(string gameId, DateTime? captureHorizon, CancellationToken ct)
    {
        var evaluatedAt = clock.GetUtcNow().UtcDateTime;
        await using var db = new ExcitementDbContext(databaseOptions);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var revision = await db.ProHypeRevisions.FromSqlRaw("SELECT * FROM pro_hype_revision WHERE id = 1 FOR UPDATE").AsNoTracking().SingleAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var game = await db.Games.AsNoTracking().SingleOrDefaultAsync(g => g.Id == gameId, ct);
        if (game is null || !ProHypePolicy.Supports(game.Sport)) return Unavailable(now, game);
        var ratings = await db.TeamRatings.AsNoTracking().Where(r => r.Sport == game.Sport && r.Season == game.SeasonYear).ToArrayAsync(ct);
        var odds = await db.PregameOdds.AsNoTracking().SingleOrDefaultAsync(o => o.GameId == gameId, ct);
        var phase = Identity.Of(game).Phase;
        var completed = await db.Games.AsNoTracking().Where(g => g.Sport == game.Sport && g.SeasonYear == game.SeasonYear
            && g.SeasonType == phase && g.Status == "final").Select(g => g.Id).ToArrayAsync(ct);
        var evaluation = scorer.EvaluateFromEvidence(game, ratings, odds!, completed, now);
        var total = scorer.CalculateFinalScore(game.Sport, evaluation.Components);
        var state = await db.ProHypeSnapshots.SingleOrDefaultAsync(s => s.GameId == gameId, ct);
        if (state is null)
        {
            state = new ProHypeSnapshot { GameId = gameId };
            db.ProHypeSnapshots.Add(state);
        }
        var saved = Parse<Saved>(state.SnapshotJson);
        var revoked = Parse<HashSet<string>>(state.RevokedEvidenceJson) ?? [];
        if (saved is not null && !ValidSaved(saved, now)) saved = null;
        var gameSubject = "game:" + gameId;
        var homeSubject = $"team:{game.Sport}:{game.SeasonYear}:{ProHypePolicy.TeamKey(game.Sport, game.HomeTeamAbbreviation)}";
        var awaySubject = $"team:{game.Sport}:{game.SeasonYear}:{ProHypePolicy.TeamKey(game.Sport, game.AwayTeamAbbreviation)}";
        var hasSaved = saved is not null;
        var changes = await db.ProHypeEvidenceChanges.AsNoTracking().Where(c => c.Revision > state.EvidenceRevision
            && c.Revision <= revision.Revision && (hasSaved || c.Kind == "games")
            && (c.BeforeSubject == gameSubject || c.AfterSubject == gameSubject
                || c.BeforeSubject == homeSubject || c.AfterSubject == homeSubject
                || c.BeforeSubject == awaySubject || c.AfterSubject == awaySubject
                || (c.BeforeSubject == null && c.AfterSubject == null)))
            .OrderBy(c => c.Revision).ToArrayAsync(ct);
        state.ConfirmedStarted |= Began(game) || changes.Where(c => c.Kind == "games").Any(c =>
        {
            var before = Row<Game>(c.BeforeJson);
            var after = Row<Game>(c.AfterJson);
            return (before?.Id ?? after?.Id) == game.Id && (Began(before) || Began(after));
        });
        if (saved is not null)
        {
            var evidenceRevoked = false;
            foreach (var change in changes) evidenceRevoked |= Revokes(change, saved, game, revoked);
            evidenceRevoked |= RevokedCurrent(saved, evaluation.Assessment, game, revoked);
            if (saved.Identity != Identity.Of(game) || !Upcoming(game, now) || state.ConfirmedStarted || evidenceRevoked)
            {
                revoked.Add(Signature(saved));
                saved = null;
            }
        }
        if (Upcoming(game, now) && (!captureHorizon.HasValue || game.ScheduledStart < captureHorizon)
            && !state.ConfirmedStarted && evaluatedAt.Ticks >= state.LastEvaluationTicks && Complete(total, evaluation.Components))
        {
            var candidate = new Saved(Identity.Of(game), total, SnapshotComponents(game, evaluation), now, evaluation.Assessment.SupportingEvidence);
            if (!revoked.Contains(Signature(candidate)) && !candidate.SupportingEvidence.Any(r => revoked.Contains(EvidenceKey(r)))
                && (saved is null || Signature(candidate) != Signature(saved))) saved = candidate;
        }
        state.LastEvaluationTicks = Math.Max(state.LastEvaluationTicks, evaluatedAt.Ticks);
        state.EvidenceRevision = revision.Revision;
        state.SnapshotJson = saved is null ? null : JsonSerializer.Serialize(saved);
        state.RevokedEvidenceJson = JsonSerializer.Serialize(revoked);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return saved is not null ? new(saved.Total, saved.Components, saved.EvaluatedAt, game)
            : new(null, evaluation.Components, now, game);
    }

    private bool Revokes(ProHypeEvidenceChange change, Saved saved, Game game, HashSet<string> revoked)
    {
        if (change.Kind == "games")
        {
            var before = Row<Game>(change.BeforeJson);
            var after = Row<Game>(change.AfterJson);
            if ((before?.Id ?? after?.Id) != game.Id) return false;
            if (after is null || saved.Identity != Identity.Of(after) || !Upcoming(after, saved.EvaluatedAt)) return true;
            if (before?.ProEvidenceJson == after.ProEvidenceJson) return false;
            var observed = ProGameEvidence.Parse(after.ProEvidenceJson)?.ObservedAt ?? saved.EvaluatedAt;
            var assessment = scorer.EvaluateFromEvidence(after, [], null!, [], observed).Assessment;
            if (ProGameEvidence.Parse(after.ProEvidenceJson) is null)
            {
                foreach (var support in saved.SupportingEvidence.Where(r => r.Kind == ProEvidenceKind.Competition)) revoked.Add(EvidenceKey(support));
                return true;
            }
            return ChangedSupport(saved, assessment.Observations, ProEvidenceKind.Competition, before?.ProEvidenceJson is null, revoked);
        }
        if (change.Kind == "pregame_odds")
        {
            var before = Row<PregameOdds>(change.BeforeJson);
            var after = Row<PregameOdds>(change.AfterJson);
            if ((before?.GameId ?? after?.GameId) != game.Id) return false;
            if (after is null) return Revoke(saved.SupportingEvidence.Where(r => r.Kind == ProEvidenceKind.Odds
                && r.Source == before!.Source && before.FetchedAt >= r.ObservedAt), revoked);
            var observations = scorer.EvaluateFromEvidence(game, [], after, [], after.FetchedAt).Assessment.Observations;
            return ChangedSupport(saved, observations, ProEvidenceKind.Odds, false, revoked);
        }
        if (change.Kind == "team_ratings")
        {
            var before = Row<TeamRating>(change.BeforeJson);
            var after = Row<TeamRating>(change.AfterJson);
            var row = before ?? after;
            if (row is null || row.Sport != game.Sport || row.Season != game.SeasonYear) return false;
            var team = ProHypePolicy.TeamKey(game.Sport, row.TeamAbbreviation);
            var relevant = saved.SupportingEvidence.Where(r => r.Source == row.Source && ProHypePolicy.TeamKey(game.Sport, r.Team) == team).ToArray();
            if (after is null || after.Sport != game.Sport || after.Season != game.SeasonYear || after.Source != row.Source
                || ProHypePolicy.TeamKey(game.Sport, after.TeamAbbreviation) != team)
                return Revoke(relevant.Where(r => row.FetchedAt >= r.ObservedAt), revoked);
            var assessment = scorer.EvaluateFromEvidence(game, [after], null!, [], after.FetchedAt).Assessment;
            if (Revoke(VetoedSupport(assessment.Observations, saved), revoked)) return true;
            return ChangedSupport(saved, assessment.Observations, ProEvidenceKind.Ranking, false, revoked)
                | ChangedSupport(saved, assessment.Observations, ProEvidenceKind.Qualification, false, revoked);
        }
        return false;
    }

    private bool RevokedCurrent(Saved saved, ProHypeAssessment current, Game game, HashSet<string> revoked)
    {
        if (saved.SupportingEvidence.Any(r => r.Kind == ProEvidenceKind.Qualification && r.Source == "moneypuck_qualification")
            && !options.Value.MoneyPuckSelectionEnabledFor(game.Sport, game.SeasonYear.GetValueOrDefault()))
            return Revoke(saved.SupportingEvidence.Where(r => r.Kind == ProEvidenceKind.Qualification && r.Source == "moneypuck_qualification"), revoked);
        if (Revoke(VetoedSupport(current.Observations, saved), revoked)) return true;
        var rejected = false;
        foreach (var support in saved.SupportingEvidence)
        {
            var latest = current.Observations.FirstOrDefault(r => r.Kind == support.Kind && r.Source == support.Source
                && ProHypePolicy.TeamKey(game.Sport, r.Team) == ProHypePolicy.TeamKey(game.Sport, support.Team));
            if (latest is null && support.Kind == ProEvidenceKind.Odds) continue;
            if (latest is null || latest.State == ProEvidenceState.Rejected || RejectedPublication(support, latest) || ChangedExpiredPublication(saved, support, latest)
                || (latest.ObservedAt == support.ObservedAt && latest.Revision != support.Revision))
            {
                revoked.Add(EvidenceKey(support));
                rejected = true;
            }
        }
        return rejected;
    }

    private static bool ChangedSupport(Saved saved, ProEvidenceReference[] observations, ProEvidenceKind kind, bool missingBefore, HashSet<string> revoked)
    {
        var rejected = false;
        foreach (var support in saved.SupportingEvidence.Where(r => r.Kind == kind))
        {
            var current = observations.FirstOrDefault(r => r.Kind == kind && r.Source == support.Source
                && ProHypePolicy.TeamKey(saved.Identity.Sport, r.Team) == ProHypePolicy.TeamKey(saved.Identity.Sport, support.Team));
            if (current is null) continue;
            if ((current.ObservedAt >= support.ObservedAt && current.State == ProEvidenceState.Rejected) || RejectedPublication(support, current)
                || ChangedExpiredPublication(saved, support, current)
                || (!missingBefore && current.ObservedAt == support.ObservedAt && current.Revision != support.Revision))
            {
                revoked.Add(EvidenceKey(support));
                rejected = true;
            }
        }
        return rejected;
    }

    private static HypeComponents SnapshotComponents(Game game, ProCurrentHypeEvaluation evaluation)
    {
        var components = JsonSerializer.Deserialize<HypeComponents>(JsonSerializer.Serialize(evaluation.Components))!;
        bool Uses(string team) => evaluation.Assessment.SupportingEvidence.Any(r => r.Kind == ProEvidenceKind.Qualification
            && ProHypePolicy.TeamKey(game.Sport, r.Team) == ProHypePolicy.TeamKey(game.Sport, team));
        if (!Uses(game.HomeTeamAbbreviation))
        {
            components.HomePlayoffOdds = null;
            components.HomeQualificationSource = "";
            components.HomeQualificationEvidenceKind = ProQualificationEvidenceKind.Unavailable;
            components.HomeQualificationIsFinal = false;
        }
        if (!Uses(game.AwayTeamAbbreviation))
        {
            components.AwayPlayoffOdds = null;
            components.AwayQualificationSource = "";
            components.AwayQualificationEvidenceKind = ProQualificationEvidenceKind.Unavailable;
            components.AwayQualificationIsFinal = false;
        }
        return components;
    }
    private static bool Revoke(IEnumerable<ProEvidenceReference> supporting, HashSet<string> revoked)
    {
        var any = false;
        foreach (var reference in supporting) { revoked.Add(EvidenceKey(reference)); any = true; }
        return any;
    }
    private static bool ChangedExpiredPublication(Saved saved, ProEvidenceReference support, ProEvidenceReference current)
    {
        if (support.Kind == ProEvidenceKind.Qualification && support.Source == "moneypuck_qualification" && current.ExpiredProbability.HasValue
            && support.PublishedAt == current.PublishedAt && current.ExpiredProbability !=
                (ProHypePolicy.TeamKey(saved.Identity.Sport, support.Team) == saved.Identity.Home
                    ? saved.Components.HomePlayoffOdds : saved.Components.AwayPlayoffOdds)) return true;
        return support.Kind == ProEvidenceKind.Ranking && support.Source == MoneyPuckRankings.Source
            && current.State == ProEvidenceState.Gap && current.PowerRank.HasValue
            && support.PublishedAt.HasValue && support.PublishedAt == current.PublishedAt
            && support.Revision != ProEvidenceReference.Fingerprint(new { FetchedAt = support.ObservedAt,
                SourceSeason = saved.Identity.Season, current.PowerRank, RankPublishedAt = current.PublishedAt });
    }
    private static bool RejectedPublication(ProEvidenceReference support, ProEvidenceReference current) =>
        support.PublishedAt is DateTime published && current.RejectedPublications.Contains(published);
    private static IEnumerable<ProEvidenceReference> VetoedSupport(ProEvidenceReference[] observations, Saved saved) =>
        saved.SupportingEvidence.Where(s => s.Kind == ProEvidenceKind.Qualification && s.Source is "moneypuck_qualification" or "playoffstatus"
            && observations.Any(r => r.Kind == ProEvidenceKind.Qualification && r.Source == "kalshi" && r.BlocksSelection
                && ProHypePolicy.TeamKey(saved.Identity.Sport, s.Team) == ProHypePolicy.TeamKey(saved.Identity.Sport, r.Team)));
    private static string EvidenceKey(ProEvidenceReference reference) => $"{reference.Kind.StoredName()}:{reference.Source}:{reference.Team}:{reference.Revision}";
    private static string Signature(Saved saved) => ProEvidenceReference.Fingerprint(new { saved.Identity,
        Evidence = saved.SupportingEvidence.Select(r => new { Kind = r.Kind.StoredName(), r.Source, r.Team, r.Revision }).OrderBy(r => r.Kind).ThenBy(r => r.Source).ThenBy(r => r.Team) });
    private T? Row<T>(string? json) where T : class
    {
        if (json is null) return null;
        var source = JsonNode.Parse(json)!.AsObject();
        var entity = model.FindEntityType(typeof(T))!;
        var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
        var target = new JsonObject();
        foreach (var property in entity.GetProperties())
        {
            if (!source.TryGetPropertyValue(property.GetColumnName(table)!, out var value)) continue;
            var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
            target[property.Name] = type == typeof(DateTime) && value is not null
                ? JsonValue.Create(DateTimeOffset.Parse(value.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture).UtcDateTime)
                : value?.DeepClone();
        }
        return target.Deserialize<T>();
    }
    private static T? Parse<T>(string? json) where T : class
    {
        try { return json is null ? null : JsonSerializer.Deserialize<T>(json); }
        catch (JsonException) { return null; }
    }
    private bool ValidSaved(Saved saved, DateTime now) => saved.Identity is not null && saved.EvaluatedAt <= now
        && Complete(saved.Total, saved.Components) && saved.Total == scorer.CalculateFinalScore(saved.Identity.Sport, saved.Components)
        && saved.SupportingEvidence is not null
        && saved.SupportingEvidence.All(r => r is not null && r.State == ProEvidenceState.Accepted && r.ObservedAt <= saved.EvaluatedAt
            && !string.IsNullOrWhiteSpace(r.Source) && !string.IsNullOrWhiteSpace(r.Revision) && r.RejectedPublications is not null
            && (!r.PublishedAt.HasValue || r.PublishedAt <= r.ObservedAt))
        && saved.SupportingEvidence.Count(r => r.Kind == ProEvidenceKind.Competition) == 1
        && saved.SupportingEvidence.Count(r => r.Kind == ProEvidenceKind.Odds) == 1
        && saved.SupportingEvidence.Count(r => r.Kind == ProEvidenceKind.Ranking) == 2
        && saved.SupportingEvidence.Where(r => r.Kind == ProEvidenceKind.Ranking).Select(r => r.Source).Distinct().Count() == 1
        && saved.SupportingEvidence.Count(r => r.Kind == ProEvidenceKind.Ranking && ProHypePolicy.TeamKey(saved.Identity.Sport, r.Team) == saved.Identity.Home) == 1
        && saved.SupportingEvidence.Count(r => r.Kind == ProEvidenceKind.Ranking && ProHypePolicy.TeamKey(saved.Identity.Sport, r.Team) == saved.Identity.Away) == 1
        && saved.SupportingEvidence.Count(r => r.Kind == ProEvidenceKind.Qualification) == (saved.Identity.Phase == 2 ? 2 : 0)
        && ValidQualification(saved, saved.Identity.Home, saved.Components.HomeQualificationSource,
            saved.Components.HomeQualificationEvidenceKind, saved.Components.HomePlayoffOdds, saved.Components.HomeQualificationIsFinal)
        && ValidQualification(saved, saved.Identity.Away, saved.Components.AwayQualificationSource,
            saved.Components.AwayQualificationEvidenceKind, saved.Components.AwayPlayoffOdds, saved.Components.AwayQualificationIsFinal);
    private static bool ValidQualification(Saved saved, string team, string source, ProQualificationEvidenceKind kind,
        double? probability, bool final)
    {
        if (saved.Identity.Phase != 2)
            return string.IsNullOrEmpty(source) && kind == ProQualificationEvidenceKind.Unavailable && !probability.HasValue && !final;
        var support = saved.SupportingEvidence.Where(r => r.Kind == ProEvidenceKind.Qualification
            && ProHypePolicy.TeamKey(saved.Identity.Sport, r.Team) == team).ToArray();
        var expectedKind = source switch
        {
            "kalshi" => ProQualificationEvidenceKind.Kalshi,
            "moneypuck_qualification" => ProQualificationEvidenceKind.MoneyPuckModel,
            "playoffstatus" => ProQualificationEvidenceKind.PlayoffStatusModel,
            _ => ProQualificationEvidenceKind.Scalar
        };
        return support.Length == 1 && support[0].Source == source && kind == expectedKind
            && probability.HasValue && ProHypePolicy.Probability(probability.Value)
            && (!final || kind == ProQualificationEvidenceKind.Kalshi && probability is 0 or 1);
    }
    private static bool Began(Game? game) => game is not null && (game.ActualStart.HasValue || game.Status is "in_progress" or "live" or "final" or "suspended");
    private static bool Upcoming(Game game, DateTime now) => !game.IsHistoricalImport && game.Status == "scheduled"
        && !game.ActualStart.HasValue && game.SeasonYear.HasValue && game.ScheduledStart > now;
    private static bool Complete(double total, HypeComponents components) => components is not null && components.IsComplete
        && new[] { total, components.StakesScore, components.QualityScore, components.ClosenessScore }.All(v => double.IsFinite(v) && v >= 0 && v <= 100);
    private static ProHypeResolution Unavailable(DateTime now, Game? game) => new(null,
        new HypeComponents { StakesAvailable = false, QualityAvailable = false, ClosenessAvailable = false }, now, game);
}
