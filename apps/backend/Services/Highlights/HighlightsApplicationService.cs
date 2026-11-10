using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using backend.Data;
using backend.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace backend.Services.Highlights;

public sealed class HighlightsApplicationService(ExcitementDbContext db, GameHighlightsInspectionService inspection,
    MlbHighlightsIdentityClient identities, YouTubeHighlightsClient youtube, TimeProvider time, IOptions<YouTubeHighlightsOptions> options,
    IOptions<HighlightsWorkerOptions> workerOptions, ILogger<HighlightsApplicationService> logger)
{
    private readonly HashSet<(string Evidence, string GameId)> unreadable = [];

    public async Task<GameHighlightsInspection?> InspectAsync(string gameId, CancellationToken cancellationToken = default)
    {
        var state = await inspection.InspectAsync(gameId, cancellationToken);
        return state is not null && PersistedIdentityFailure(state.Game) == "MlbNotFinal" ? state with { IsActuallyCompleted = false } : state;
    }

    public async Task<HighlightsDetailState> GetDetailAsync(string gameId, CancellationToken cancellationToken = default)
    {
        try { return await ComposeDetailAsync(gameId, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogError(HighlightsFault.Reportable(ex), "Highlights detail skipped: {Reason}", "OptionalStorageFailure");
            return new(HighlightsDetailStatus.Hidden);
        }
    }

    private async Task<HighlightsDetailState> ComposeDetailAsync(string gameId, CancellationToken cancellationToken)
    {
        var state = await InspectAsync(gameId, cancellationToken);
        if (state is null || !state.IsActuallyCompleted) return new(HighlightsDetailStatus.Hidden);
        var selection = state.Highlights;
        var now = time.GetUtcNow().UtcDateTime;
        if (VerifiedPersistedIdentity(state.Game) && selection is
            { Availability: HighlightsAvailability.Playable, SelectedVideoId: not null, Title: not null }
            && selection.ProviderDataExpiresAt > now && selection.ChannelId == YouTubeHighlightsClient.ApprovedChannelId && selection.MadeForKids == false
            && VerifiedSelectionIdentity(state.Game, selection))
            return new(HighlightsDetailStatus.Playable, selection.SelectedVideoId, selection.Title, selection.AllowedRegions, selection.BlockedRegions);
        var worker = workerOptions.Value;
        var active = worker.Enabled && worker.MaxGamesPerCycle is >= 1 and <= 100 && worker.MaxCatchupPages is >= 1 and <= 16
            && worker.MaxCatchupPages >= options.Value.MaxUploadPages
            && ConfigurationProblem() is null && state.Game.HighlightsDiscoveryExpiresAt > now
            && selection?.Suppressed != true && selection?.Ownership != HighlightsOwnership.Manual
            && selection?.Availability != HighlightsAvailability.Unavailable && !HasDecision(selection);
        if (active)
        {
            var operation = await inspection.InspectOperationsAsync(cancellationToken);
            active = !operation.ProviderConfigurationRejected;
        }
        return new(active ? HighlightsDetailStatus.Pending : HighlightsDetailStatus.Unavailable);
    }

    public async Task<HighlightsApplicationResult> ProcessCandidatesAsync(IReadOnlyCollection<string> gameIds,
        IReadOnlyCollection<YouTubeVideoEvidence> videos, bool catchup = false, CancellationToken cancellationToken = default)
    {
        if (gameIds.Count > 100 || videos.Count > 200 || videos.Select(v => v.VideoId).Distinct().Count() != videos.Count) return new(0, 0, "InvalidBatch");
        if (ConfigurationProblem() is string configurationProblem) return new(0, 0, configurationProblem);
        videos = videos.Where(Fresh).ToArray();
        var selected = 0;
        foreach (var id in gameIds.Distinct())
        {
            var game = await db.Games.AsNoTracking().SingleOrDefaultAsync(g => g.Id == id, cancellationToken);
            if (game is null) continue;
            var evidence = await identities.ResolveAsync(game, cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var current = await LockAsync(id, cancellationToken);
            if (current is null) continue;
            if (!SameContext(current, game)) { db.ChangeTracker.Clear(); continue; }
            var recorded = await RecordIdentityAsync(current, evidence, cancellationToken);
            var selection = await SelectionAsync(id, cancellationToken);
            if (recorded && (evidence.Definitive && evidence.Identity is null || evidence.Identity is not null && HasDecision(selection) && !VerifiedSelectionIdentity(current, selection)))
            {
                selection.Availability = HighlightsAvailability.Unavailable;
                selection.UnavailableReason = evidence.Identity is null ? evidence.Reason : "SelectionIdentityChanged";
            }
            else if (recorded && evidence.Identity is not null && !HasDecision(selection))
            {
                selection.Availability = HighlightsAvailability.Unknown;
                selection.UnavailableReason = "NoConfidentRecap";
            }
            if (recorded && evidence.Identity is not null && MlbCompletionEvidence.IsActuallyCompleted(current)
                && !selection.Suppressed && selection.Ownership != HighlightsOwnership.Manual
                && (selection.SelectedVideoId is null || selection.Availability == HighlightsAvailability.Unavailable)
                && (catchup
                    ? await CatchupEligibleAsync(current, cancellationToken) && (!HasDecision(selection) || current.HighlightsDiscoveryExpiresAt > time.GetUtcNow().UtcDateTime)
                    : current.HighlightsDiscoveryExpiresAt > time.GetUtcNow().UtcDateTime))
            {
                var candidate = videos.FirstOrDefault(v => Suitable(v, evidence.Identity, false));
                if (candidate is not null && await LockVideoChoiceAsync(id, candidate.VideoId, cancellationToken))
                {
                    Choose(selection, candidate, HighlightsOwnership.Automatic, evidence.Identity);
                    selected++;
                }
            }
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }
        return new(selected, 0, "Processed");
    }

    public async Task<HighlightsApplicationResult> RevalidateSelectionsAsync(IReadOnlyCollection<YouTubeVideoEvidence> videos, CancellationToken cancellationToken = default)
    {
        if (videos.Count > 200 || videos.Select(v => v.VideoId).Distinct().Count() != videos.Count) return new(0, 0, "InvalidBatch");
        videos = videos.Where(Fresh).ToArray();
        var videoIds = videos.Select(v => v.VideoId).Distinct().ToArray();
        var selections = await db.GameHighlights.AsNoTracking().Where(h => videoIds.Contains(h.SelectedVideoId!)).ToListAsync(cancellationToken);
        var count = 0;
        foreach (var original in selections)
        {
            var video = videos.SingleOrDefault(v => v.VideoId == original.SelectedVideoId);
            if (video is null) continue;
            var game = await db.Games.AsNoTracking().SingleAsync(g => g.Id == original.GameId, cancellationToken);
            var evidence = await identities.ResolveAsync(game, cancellationToken);
            if (!evidence.Definitive && video.Availability == YouTubeAvailability.Playable) continue;
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var current = await LockAsync(original.GameId, cancellationToken);
            var selection = await SelectionAsync(original.GameId, cancellationToken);
            if (current is null || selection.Version != original.Version || selection.SelectedVideoId != video.VideoId
                || selection.ValidatedAt > video.ValidatedAtUtc.UtcDateTime || !SameContext(current, game))
            { db.ChangeTracker.Clear(); continue; }
            if (!await RecordIdentityAsync(current, evidence, cancellationToken)) { db.ChangeTracker.Clear(); continue; }
            var valid = evidence.Identity is not null && MlbCompletionEvidence.IsActuallyCompleted(current)
                && Suitable(video, evidence.Identity, selection.Ownership == HighlightsOwnership.Manual);
            Refresh(selection, video, valid);
            if (evidence.Reason == "MlbNotFinal") selection.UnavailableReason = evidence.Reason;
            if (valid) selection.SelectionEvidenceJson = SelectionIdentityJson(evidence.Identity!, selection.Ownership == HighlightsOwnership.Manual);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            db.ChangeTracker.Clear();
            count++;
        }
        return new(0, count, "Revalidated");
    }

    public async Task<HighlightsOperatorResult> OperateAsync(HighlightsOperatorRequest request, CancellationToken cancellationToken = default)
    {
        var before = await InspectAsync(request.GameId, cancellationToken);
        if (before is null) return new(false, false, "GameNotFound", null);
        if (!Enum.IsDefined(request.Action)) return new(false, false, "InvalidAction", before);
        if (request.Action == HighlightsOperatorAction.Inspect) return new(true, false, "Inspected", before);
        if (before.Game.Sport != "mlb") return new(false, false, "NotMlb", before);
        var attaching = request.Action is HighlightsOperatorAction.Attach or HighlightsOperatorAction.Replace;
        MlbHighlightsIdentityClient.Evidence? identity = null;
        YouTubeVideoEvidence? video = null;
        if (attaching)
        {
            if (ConfigurationProblem() is string configurationProblem) return new(false, false, configurationProblem, before);
            if (string.IsNullOrWhiteSpace(request.VideoId)) return new(false, false, "VideoIdRequired", before);
            identity = await identities.ResolveAsync(before.Game, cancellationToken);
            if (identity.Identity is null) return new(false, false, identity.Reason, before);
            var reservation = await ReserveOperatorValidationAsync(cancellationToken);
            if (reservation.Reason is not null) return new(false, false, reservation.Reason, before);
            var result = await youtube.ValidateVideosAsync([request.VideoId], cancellationToken,
                ct => ChargeOperatorRolloverRequestAsync(reservation.Day, ct));
            await CompleteOperatorValidationAsync(result, reservation.Day, cancellationToken);
            if (result.Outcome != YouTubeOutcome.Success) return new(false, false, result.Outcome.ToString(), before);
            video = result.Videos.SingleOrDefault(v => v.VideoId == request.VideoId);
            if (video is null || !Suitable(video, identity.Identity, true)) return new(false, false, "VideoNotApprovedRecap", before);
            if (await db.GameHighlights.AsNoTracking().AnyAsync(h => h.GameId != request.GameId && h.SelectedVideoId == request.VideoId, cancellationToken))
                return new(false, false, "VideoAlreadySelected", before);
            if (request.Action == HighlightsOperatorAction.Attach && HasDecision(before.Highlights)) return new(false, false, "UseReplace", before);
            if (request.Action == HighlightsOperatorAction.Replace && !HasDecision(before.Highlights)) return new(false, false, "UseAttach", before);
        }
        if (!request.Apply) return new(true, false, "DryRun", before);
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            var current = await LockAsync(request.GameId, cancellationToken);
            if (current is null) return new(false, false, "GameNotFound", null);
            var selection = await SelectionAsync(request.GameId, cancellationToken);
            if (attaching)
            {
                if (!SameContext(current, before.Game) || current.Sport != "mlb" || current.Status != "final")
                    return new(false, false, "GameChanged", before);
                if (request.Action == HighlightsOperatorAction.Attach && HasDecision(selection)) return new(false, false, "UseReplace", before);
                if (request.Action == HighlightsOperatorAction.Replace && !HasDecision(selection)) return new(false, false, "UseAttach", before);
                var recorded = await RecordIdentityAsync(current, identity!, cancellationToken);
                if (!recorded || !MlbCompletionEvidence.IsActuallyCompleted(current)) return new(false, false, "NotActuallyCompleted", before);
                if (!await LockVideoChoiceAsync(request.GameId, video!.VideoId, cancellationToken))
                { db.ChangeTracker.Clear(); return new(false, false, "VideoAlreadySelected", before); }
                Choose(selection, video, HighlightsOwnership.Manual, identity!.Identity!);
            }
            else if (request.Action == HighlightsOperatorAction.Remove)
            {
                selection.SelectedVideoId = null;
                selection.Ownership = null;
                selection.SelectedAt = null;
                selection.Title = null;
                selection.ChannelId = null;
                selection.PublishedAt = null;
                selection.MadeForKids = null;
                selection.AllowedRegions = null;
                selection.BlockedRegions = null;
                selection.SelectionEvidenceJson = null;
                selection.ValidatedAt = null;
                selection.ProviderDataRefreshedAt = null;
                selection.ProviderDataExpiresAt = null;
                selection.Availability = HighlightsAvailability.Unavailable;
                selection.UnavailableReason = "Suppressed";
                selection.Suppressed = true;
                selection.SuppressedAt = time.GetUtcNow().UtcDateTime;
            }
            else
            {
                selection.Suppressed = false;
                selection.SuppressedAt = null;
                if (!HasDecision(selection))
                {
                    selection.Availability = HighlightsAvailability.Unknown;
                    selection.UnavailableReason = null;
                }
            }
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        db.ChangeTracker.Clear();
        return new(true, true, "Applied", await InspectAsync(request.GameId, cancellationToken));
    }

    private string? ConfigurationProblem()
    {
        var config = options.Value;
        if (config.ValidateConfiguration() is YouTubeOutcome problem) return problem.ToString();
        return config.DailyUnitLimit == 0 ? "BudgetExhausted" : null;
    }

    private async Task<(string? Reason, DateOnly Day)> ReserveOperatorValidationAsync(CancellationToken cancellationToken)
    {
        var config = options.Value;
        if (ConfigurationProblem() is string problem) return (problem, default);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var state = await db.HighlightsOperations.FromSqlRaw("SELECT *, xmin FROM highlights_operations WHERE id = 1 FOR UPDATE").SingleAsync(cancellationToken);
        var now = time.GetUtcNow();
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles")).DateTime);
        if (state.BackoffUntil > now.UtcDateTime) { db.ChangeTracker.Clear(); return ("Backoff", day); }
        if (state.QuotaDay != day) { state.QuotaDay = day; state.QuotaUnitsUsed = state.WorkerReservedUnits; }
        if (state.QuotaUnitsUsed + 3 > config.DailyUnitLimit) { db.ChangeTracker.Clear(); return ("BudgetExhausted", day); }
        state.QuotaUnitsUsed += 3;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return (null, day);
    }

    private async Task<YouTubeRequestAuthorization> ChargeOperatorRolloverRequestAsync(DateOnly reservationDay, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var state = await db.HighlightsOperations.FromSqlRaw("SELECT *, xmin FROM highlights_operations WHERE id = 1 FOR UPDATE").SingleAsync(cancellationToken);
        var now = time.GetUtcNow();
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles")).DateTime);
        if (day == reservationDay) { db.ChangeTracker.Clear(); return new(day, true); }
        if (state.QuotaDay != day) { state.QuotaDay = day; state.QuotaUnitsUsed = state.WorkerReservedUnits; }
        var permitted = state.QuotaUnitsUsed < options.Value.DailyUnitLimit;
        if (permitted) state.QuotaUnitsUsed++;
        else state.LastReason = "BudgetExhausted";
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return new(day, permitted);
    }

    private async Task CompleteOperatorValidationAsync(YouTubeHighlightsResult result, DateOnly reservationDay, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var state = await db.HighlightsOperations.FromSqlRaw("SELECT *, xmin FROM highlights_operations WHERE id = 1 FOR UPDATE").SingleAsync(cancellationToken);
        var returnedDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles")).DateTime);
        if (state.QuotaDay == reservationDay && returnedDay == reservationDay)
            state.QuotaUnitsUsed = Math.Max(0, state.QuotaUnitsUsed - Math.Max(0, 3 - result.UnitsConsumed));
        if (result.RetryAtUtc is not null && (state.BackoffUntil is null || state.BackoffUntil < result.RetryAtUtc.Value.UtcDateTime)) state.BackoffUntil = result.RetryAtUtc.Value.UtcDateTime;
        state.ProviderConfigurationRejected = result.ProviderConfigurationRejectedAfter(state.ProviderConfigurationRejected);
        state.LastReason = result.Outcome.ToString();
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
    }

    private async Task<bool> CatchupEligibleAsync(Game game, CancellationToken cancellationToken)
    {
        var operation = await db.HighlightsOperations.AsNoTracking().SingleAsync(cancellationToken);
        return operation.LaunchBoundaryAt is not null && operation.CatchupWindowStart == operation.LaunchBoundaryAt.Value.AddDays(-7)
            && operation.CatchupCompletedAt is null && operation.CatchupRemainingGameIds?.Contains(game.Id) == true
            && game.ScheduledStart >= operation.CatchupWindowStart && game.ScheduledStart <= operation.LaunchBoundaryAt;
    }

    private string? PersistedIdentityFailure(Game game)
    {
        if (game.HighlightsIdentityEvidenceJson is null) return null;
        try
        {
            using var json = JsonDocument.Parse(game.HighlightsIdentityEvidenceJson);
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("FailureReason", out var reason)
                && reason.ValueKind == JsonValueKind.String ? reason.GetString() : null;
        }
        catch (JsonException ex) { Unreadable(ex, nameof(Game.HighlightsIdentityEvidenceJson), game.Id); return null; }
    }

    private sealed record SelectionIdentity(long GamePk, DateOnly OfficialDate, int Season, bool Manual);

    private static string SelectionIdentityJson(MlbHighlightsIdentityClient.Identity identity, bool manual) =>
        JsonSerializer.Serialize(new SelectionIdentity(identity.GamePk, identity.OfficialDate, identity.Season, manual));

    private bool VerifiedSelectionIdentity(Game game, GameHighlights selection)
    {
        if (selection.SelectionEvidenceJson is null) return false;
        try
        {
            var evidence = JsonSerializer.Deserialize<SelectionIdentity>(selection.SelectionEvidenceJson);
            return evidence is not null && evidence.GamePk == game.MlbGamePk && evidence.OfficialDate == game.MlbOfficialDate
                && evidence.Season == game.SeasonYear && evidence.Manual == (selection.Ownership == HighlightsOwnership.Manual);
        }
        catch (JsonException ex) { Unreadable(ex, nameof(GameHighlights.SelectionEvidenceJson), game.Id); return false; }
    }

    private bool VerifiedPersistedIdentity(Game game)
    {
        if (game.HighlightsIdentityEvidenceJson is null) return false;
        try
        {
            var identity = JsonSerializer.Deserialize<MlbHighlightsIdentityClient.Identity>(game.HighlightsIdentityEvidenceJson);
            return identity is not null && identity.GamePk > 0 && identity.GamePk == game.MlbGamePk && identity.OfficialDate == game.MlbOfficialDate
                && identity.Season == game.SeasonYear && identity.OfficialDate.Year == identity.Season && identity.ScheduledStart == game.ScheduledStart
                && MlbHighlightsIdentityClient.Teams.Any(t => t.MlbId == identity.HomeTeamId && t.Abbreviation == game.HomeTeamAbbreviation
                    && (game.HomeTeamId is null || game.HomeTeamId == t.EspnId))
                && MlbHighlightsIdentityClient.Teams.Any(t => t.MlbId == identity.AwayTeamId && t.Abbreviation == game.AwayTeamAbbreviation
                    && (game.AwayTeamId is null || game.AwayTeamId == t.EspnId));
        }
        catch (JsonException ex) { Unreadable(ex, nameof(Game.HighlightsIdentityEvidenceJson), game.Id); return false; }
    }

    private void Unreadable(JsonException exception, string evidence, string gameId)
    {
        if (unreadable.Add((evidence, gameId)))
            logger.LogError(HighlightsFault.From(exception), "Highlights stored evidence unreadable: {Evidence} for {GameId}", evidence, gameId);
    }

    private static bool HasDecision(GameHighlights? selection) => selection?.Ownership is not null || selection?.SelectedAt is not null || selection?.SelectedVideoId is not null;
    private static bool SameContext(Game current, Game original) => current.HomeTeamAbbreviation == original.HomeTeamAbbreviation
        && current.AwayTeamAbbreviation == original.AwayTeamAbbreviation && current.HomeTeamId == original.HomeTeamId
        && current.AwayTeamId == original.AwayTeamId && current.SeasonYear == original.SeasonYear && current.ScheduledStart == original.ScheduledStart;

    private async Task<Game?> LockAsync(string id, CancellationToken cancellationToken)
    {
        await db.LockProHypeFenceAsync(ProHypeFenceAccess.WriteGames, cancellationToken);
        return await db.Games.FromSqlInterpolated($"SELECT * FROM games WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<bool> LockVideoChoiceAsync(string gameId, string videoId, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(65001, hashtext({videoId}))", cancellationToken);
        return !await db.GameHighlights.AsNoTracking().AnyAsync(h => h.GameId != gameId && h.SelectedVideoId == videoId, cancellationToken);
    }

    private async Task<GameHighlights> SelectionAsync(string id, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO game_highlights(game_id) VALUES ({id}) ON CONFLICT (game_id) DO NOTHING", cancellationToken);
        return await db.GameHighlights.SingleAsync(h => h.GameId == id, cancellationToken);
    }

    private async Task<bool> RecordIdentityAsync(Game game, MlbHighlightsIdentityClient.Evidence evidence, CancellationToken cancellationToken)
    {
        if (evidence.Identity is not null && game.EspnCompletionObservedAt > evidence.Identity.ObservedAt) return false;
        if (evidence.Completion is not null && game.EspnCompletionObservedAt > evidence.Completion.ObservedAt) return false;
        if (evidence.Identity is not null)
        {
            if (game.MlbGamePk is not null && game.MlbGamePk != evidence.Identity.GamePk) return false;
            var key = evidence.Identity.GamePk.ToString(CultureInfo.InvariantCulture);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(650, hashtext({key}))", cancellationToken);
            if (await db.Games.AnyAsync(g => g.Id != game.Id && g.MlbGamePk == evidence.Identity.GamePk, cancellationToken)) return false;
        }
        if (evidence.Completion is not null)
        {
            await evidence.Completion.RecordAsync(db, false, cancellationToken);
            await db.Entry(game).ReloadAsync(cancellationToken);
        }
        if (evidence.Identity is not null)
        {
            game.MlbGamePk = evidence.Identity.GamePk;
            game.MlbOfficialDate = evidence.Identity.OfficialDate;
            game.HighlightsIdentityEvidenceJson = JsonSerializer.Serialize(evidence.Identity);
        }
        else if (evidence.Definitive)
        {
            game.HighlightsIdentityEvidenceJson = JsonSerializer.Serialize(new { FailureReason = evidence.Reason,
                ObservedAt = evidence.Completion?.ObservedAt ?? time.GetUtcNow().UtcDateTime });
        }
        return true;
    }

    private void Choose(GameHighlights selection, YouTubeVideoEvidence video, HighlightsOwnership ownership, MlbHighlightsIdentityClient.Identity identity)
    {
        selection.SelectedVideoId = video.VideoId;
        selection.Ownership = ownership;
        selection.SelectedAt = time.GetUtcNow().UtcDateTime;
        selection.SelectionEvidenceJson = SelectionIdentityJson(identity, ownership == HighlightsOwnership.Manual);
        Refresh(selection, video, true);
    }

    private static void Refresh(GameHighlights selection, YouTubeVideoEvidence video, bool valid)
    {
        selection.Availability = valid ? HighlightsAvailability.Playable : HighlightsAvailability.Unavailable;
        selection.UnavailableReason = valid ? null : "ValidationFailed";
        selection.Title = video.Title;
        selection.ChannelId = video.ChannelId;
        selection.PublishedAt = video.PublishedAtUtc?.UtcDateTime;
        selection.MadeForKids = video.MadeForKids;
        selection.AllowedRegions = video.AllowedRegions.ToArray();
        selection.BlockedRegions = video.BlockedRegions.ToArray();
        selection.ValidatedAt = video.ValidatedAtUtc.UtcDateTime;
        selection.ProviderDataRefreshedAt = video.ValidatedAtUtc.UtcDateTime;
        selection.ProviderDataExpiresAt = video.ValidatedAtUtc.AddDays(30).UtcDateTime;
    }

    private bool Fresh(YouTubeVideoEvidence video) => video.ValidatedAtUtc <= time.GetUtcNow() && video.ValidatedAtUtc.AddDays(30) > time.GetUtcNow();

    private static bool Suitable(YouTubeVideoEvidence video, MlbHighlightsIdentityClient.Identity identity, bool manual)
    {
        if (video.Availability != YouTubeAvailability.Playable || video.ChannelId != YouTubeHighlightsClient.ApprovedChannelId
            || video.MadeForKids != false || video.Title is null) return false;
        var home = MlbHighlightsIdentityClient.Teams.Single(t => t.MlbId == identity.HomeTeamId);
        var away = MlbHighlightsIdentityClient.Teams.Single(t => t.MlbId == identity.AwayTeamId);
        var match = Regex.Match(video.Title, @"^(?<away>.+?) vs\. (?<home>.+?) (?:Game )?Highlights \((?<date>\d{1,2}/\d{1,2}/(?:\d{4}|\d{2})|\d{4}-\d{2}-\d{2})\)(?: \| MLB Highlights)?$", RegexOptions.CultureInvariant);
        if (!match.Success || match.Groups["home"].Value != home.Nickname || match.Groups["away"].Value != away.Nickname
            || !DateOnly.TryParseExact(match.Groups["date"].Value, ["M/d/yy", "M/d/yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            || date != identity.OfficialDate || date.Year != identity.Season) return false;
        var text = video.Description ?? "";
        if (video.Duration is null || video.Duration < TimeSpan.FromMinutes(2) || video.Duration > TimeSpan.FromMinutes(30)
            || Regex.IsMatch(text, @"\b(commentary|analysis only|isolated play|single play|full (game|replay)|roundup|all games|multi[- ]game|compilation)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
        if (MlbHighlightsIdentityClient.Teams.Any(t => t.MlbId != identity.HomeTeamId && t.MlbId != identity.AwayTeamId
            && Regex.IsMatch(text, @"\b" + Regex.Escape(t.Nickname) + @"\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))) return false;
        var dates = Regex.Matches(text, @"\b(?:\d{1,2}/\d{1,2}/(?:\d{4}|\d{2})|\d{4}-\d{2}-\d{2})\b");
        if (dates.Any(d => !DateOnly.TryParseExact(d.Value, ["M/d/yy", "M/d/yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var stated) || stated != date)) return false;
        if (Regex.Matches(text, @"\b20[0-9]{2}\b").Any(y => int.Parse(y.Value, CultureInfo.InvariantCulture) != identity.Season)) return false;
        if (Regex.Matches(text, @"\bGame\s+(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Any(m => !int.TryParse(m.Groups[1].Value, out var number) || number != identity.GameNumber)) return false;
        var pks = Regex.Matches(text, @"\bgamePk\s*[:=]\s*(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(m => m.Groups[1].Value).Distinct().ToArray();
        if (pks.Length > 1 || pks.Length == 1 && pks[0] != identity.GamePk.ToString(CultureInfo.InvariantCulture)) return false;
        var ordinary = identity.DoubleHeader == "N" && identity.GameNumber == 1 && identity.SameDayContests == 1
            && identity.RescheduledFromDate is null && identity.RescheduledFrom is null && identity.ResumedFrom is null
            && identity.ResumedFromDate is null && identity.RescheduledToDate is null && identity.ResumeDate is null;
        return manual || ordinary || pks.Length == 1;
    }
}
