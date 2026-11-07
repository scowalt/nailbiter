using backend.Data;
using backend.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace backend.Services.Highlights;

public sealed class HighlightsWorkerOptions
{
    public bool Enabled { get; set; }
    public bool AuthorizeLaunchCatchup { get; set; }
    public int MaxGamesPerCycle { get; set; } = 100;
    public int MaxCatchupPages { get; set; } = 16;
}

public sealed record HighlightsLifecycleResult(int Selected, int Revalidated, int Expired, string Reason);

public sealed class HighlightsLifecycleService(ExcitementDbContext db, TimeProvider time, IOptions<HighlightsWorkerOptions> options,
    IOptions<YouTubeHighlightsOptions> providerOptions, YouTubeHighlightsClient youtube, HighlightsApplicationService application,
    ILogger<HighlightsLifecycleService> logger)
{
    private static readonly TimeZoneInfo QuotaZone = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");

    public async Task<HighlightsLifecycleResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var expired = 0;
        var locked = false;
        try
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
            await using (var command = db.Database.GetDbConnection().CreateCommand())
            {
                command.CommandText = "SELECT pg_try_advisory_lock(651, 647)";
                locked = (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
            }
            if (!locked) return new(0, 0, 0, "Busy");
            await RecoverReservationAsync(cancellationToken);
            expired = await MaintainAsync(cancellationToken);
            if (ConfigurationProblem() is string problem) return new(0, 0, expired, problem);
            await InitializeCatchupAsync(cancellationToken);
            var state = await ReadStateAsync(cancellationToken);
            var now = time.GetUtcNow().UtcDateTime;
            if (state.BackoffUntil > now) return new(0, 0, expired, "Backoff");
            var revalidated = 0;
            var validationCutoff = now.AddDays(-1);
            var ids = await db.GameHighlights.AsNoTracking().Where(h => h.SelectedVideoId != null && (h.ValidatedAt == null || h.ValidatedAt <= validationCutoff))
                .OrderBy(h => h.ValidatedAt).ThenBy(h => h.GameId).Select(h => h.SelectedVideoId!).Take(200).ToArrayAsync(cancellationToken);
            if (ids.Length > 0 && !(state.NextValidationAttemptAt > now))
            {
                await db.HighlightsOperations.ExecuteUpdateAsync(s => s.SetProperty(h => h.NextValidationAttemptAt, now.AddMinutes(15)), cancellationToken);
                var validation = await ProviderAsync(ids.Distinct().ToArray(), null, cancellationToken);
                if (validation is null) return new(0, 0, expired, "BudgetExhausted");
                if (validation.Outcome != YouTubeOutcome.Success) return new(0, 0, expired, validation.Outcome.ToString());
                revalidated = (await application.RevalidateSelectionsAsync(validation.Videos, cancellationToken)).Revalidated;
            }
            if (state.NextPollAt > now) return new(0, revalidated, expired, "NotDue");
            await db.HighlightsOperations.ExecuteUpdateAsync(s => s.SetProperty(h => h.LastPollAttemptAt, now)
                .SetProperty(h => h.NextPollAt, now.AddMinutes(15)), cancellationToken);
            var result = await ProviderAsync(null, null, cancellationToken);
            if (result is null) return new(0, revalidated, expired, "BudgetExhausted");
            if (result.Outcome != YouTubeOutcome.Success) return new(0, revalidated, expired, result.Outcome.ToString());
            var games = await db.Games.AsNoTracking().Where(g => g.Sport == "mlb" && g.Status == "final" && g.HighlightsDiscoveryExpiresAt > now
                && !db.GameHighlights.Any(h => h.GameId == g.Id && (h.Suppressed || h.Ownership == HighlightsOwnership.Manual || h.Availability == HighlightsAvailability.Playable)))
                .OrderBy(g => g.HighlightsDiscoveryExpiresAt).ThenBy(g => g.Id).Select(g => g.Id).Take(options.Value.MaxGamesPerCycle).ToArrayAsync(cancellationToken);
            var selected = await application.ProcessCandidatesAsync(games, result.Videos, cancellationToken: cancellationToken);
            var catchup = await CatchupAsync(state, result, cancellationToken);
            return new(selected.Selected + catchup, revalidated, expired, "Processed");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            if (DatabaseFailure.Is(ex)) logger.LogError(ex, "Highlights lifecycle skipped: {Reason}", "OperationFailure");
            else logger.LogWarning("Highlights lifecycle skipped: {Reason}", "OperationFailure");
            return new(0, 0, expired, "OperationFailure");
        }
        finally
        {
            if (locked)
            {
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = "SELECT pg_advisory_unlock(651, 647)";
                try { await command.ExecuteScalarAsync(CancellationToken.None); }
                catch (Exception) { db.Database.GetDbConnection().Close(); }
            }
            await db.Database.CloseConnectionAsync();
        }
    }

    private string? ConfigurationProblem()
    {
        var worker = options.Value;
        var provider = providerOptions.Value;
        if (!worker.Enabled) return "WorkerDisabled";
        if (worker.MaxGamesPerCycle is < 1 or > 100 || worker.MaxCatchupPages is < 1 or > 16
            || worker.MaxCatchupPages < provider.MaxUploadPages) return "InvalidWorkerConfiguration";
        if (provider.ValidateConfiguration() is YouTubeOutcome problem) return problem.ToString();
        return provider.DailyUnitLimit == 0 ? "BudgetExhausted" : null;
    }

    private async Task InitializeCatchupAsync(CancellationToken ct)
    {
        if (!options.Value.AuthorizeLaunchCatchup) return;
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var state = await db.HighlightsOperations.FromSqlRaw("SELECT *, xmin FROM highlights_operations WHERE id = 1 FOR UPDATE").SingleAsync(ct);
        if (state.LaunchBoundaryAt is not null)
        {
            if (state.CatchupRemainingGameIds is null && state.CatchupCompletedAt is null)
            {
                state.CatchupCompletedAt = time.GetUtcNow().UtcDateTime;
                state.LastReason = "LegacyCohortUnknown";
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            db.ChangeTracker.Clear();
            return;
        }
        var boundary = time.GetUtcNow().UtcDateTime;
        var start = boundary.AddDays(-7);
        var cohort = await db.Games.AsNoTracking().Where(g => g.Sport == "mlb" && g.Status == "final" && g.ScheduledStart >= start && g.ScheduledStart <= boundary)
            .OrderBy(g => g.Id).Select(g => g.Id).Take(10001).ToArrayAsync(ct);
        if (cohort.Length > 10000) throw new InvalidOperationException("CatchupCohortTooLarge");
        state.LaunchBoundaryAt = boundary;
        state.CatchupWindowStart = start;
        state.CatchupRemainingGameIds = cohort;
        state.CatchupCompletedAt = cohort.Length == 0 ? boundary : null;
        state.CatchupPagesScanned = 0;
        state.UploadsPageToken = null;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        db.ChangeTracker.Clear();
    }

    private async Task<int> CatchupAsync(HighlightsOperationState state, YouTubeHighlightsResult root, CancellationToken ct)
    {
        if (state.CatchupCompletedAt is not null || state.CatchupRemainingGameIds is not { Length: > 0 }) return 0;
        var batch = state.CatchupRemainingGameIds.Take(options.Value.MaxGamesPerCycle).ToArray();
        if (state.CatchupPagesScanned + providerOptions.Value.MaxUploadPages > options.Value.MaxCatchupPages)
        {
            await CheckpointCatchupAsync(batch, null, ct);
            return 0;
        }
        var result = state.UploadsPageToken is null ? root : await ProviderAsync(null, state.UploadsPageToken, ct);
        if (result is null || result.Outcome != YouTubeOutcome.Success) return 0;
        var processed = await application.ProcessCandidatesAsync(batch, result.Videos, catchup: true, cancellationToken: ct);
        await CheckpointCatchupAsync(batch, result, ct);
        return processed.Selected;
    }

    private async Task CheckpointCatchupAsync(string[] batch, YouTubeHighlightsResult? result, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var current = await db.HighlightsOperations.FromSqlRaw("SELECT *, xmin FROM highlights_operations WHERE id = 1 FOR UPDATE").SingleAsync(ct);
        if (result is not null)
        {
            current.CatchupPagesScanned += providerOptions.Value.MaxUploadPages;
            current.UploadsPlaylistId = null;
            current.UploadsPageToken = result.NextPageToken;
            current.ProviderDataRefreshedAt = time.GetUtcNow().UtcDateTime;
            current.ProviderDataExpiresAt = time.GetUtcNow().AddDays(30).UtcDateTime;
        }
        if (result?.NextPageToken is null || current.CatchupPagesScanned + providerOptions.Value.MaxUploadPages > options.Value.MaxCatchupPages)
        {
            current.CatchupCursorGameId = batch[^1];
            current.CatchupRemainingGameIds = current.CatchupRemainingGameIds!.Skip(batch.Length).ToArray();
            current.CatchupPagesScanned = 0;
            current.UploadsPageToken = null;
            if (current.CatchupRemainingGameIds.Length == 0) current.CatchupCompletedAt = time.GetUtcNow().UtcDateTime;
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        db.ChangeTracker.Clear();
    }

    private Task<HighlightsOperationState> ReadStateAsync(CancellationToken ct) => db.HighlightsOperations.AsNoTracking().SingleAsync(ct);

    private async Task RecoverReservationAsync(CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var state = await db.HighlightsOperations.FromSqlRaw("SELECT *, xmin FROM highlights_operations WHERE id = 1 FOR UPDATE").SingleAsync(ct);
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), QuotaZone).DateTime);
        if (state.WorkerReservedUnits > 0)
        {
            if (state.QuotaDay != day) { state.QuotaDay = day; state.QuotaUnitsUsed = state.WorkerReservedUnits; }
            state.WorkerReservedUnits = 0;
            state.LastReason = "InterruptedReservationRetained";
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        db.ChangeTracker.Clear();
    }

    private async Task<YouTubeHighlightsResult?> ProviderAsync(string[]? ids, string? token, CancellationToken ct)
    {
        var maximum = ids is null ? 2 + 2 * providerOptions.Value.MaxUploadPages : 2 + (ids.Length + 49) / 50;
        DateOnly day;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            var state = await db.HighlightsOperations.FromSqlRaw("SELECT *, xmin FROM highlights_operations WHERE id = 1 FOR UPDATE").SingleAsync(ct);
            var now = time.GetUtcNow();
            day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, QuotaZone).DateTime);
            if (state.BackoffUntil > now.UtcDateTime) { db.ChangeTracker.Clear(); return null; }
            if (state.QuotaDay != day) { state.QuotaDay = day; state.QuotaUnitsUsed = 0; }
            if (state.QuotaUnitsUsed + maximum > providerOptions.Value.DailyUnitLimit)
            {
                state.LastReason = "BudgetExhausted";
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                db.ChangeTracker.Clear();
                return null;
            }
            state.QuotaUnitsUsed += maximum;
            state.WorkerReservedUnits = maximum;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            db.ChangeTracker.Clear();
        }
        var result = ids is null ? await youtube.DiscoverUploadsAsync(token, ct) : await youtube.ValidateVideosAsync(ids, ct);
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            var state = await db.HighlightsOperations.FromSqlRaw("SELECT *, xmin FROM highlights_operations WHERE id = 1 FOR UPDATE").SingleAsync(ct);
            var returnedDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), QuotaZone).DateTime);
            if (state.QuotaDay == day && returnedDay == day)
                state.QuotaUnitsUsed = Math.Max(0, state.QuotaUnitsUsed - Math.Max(0, maximum - result.UnitsConsumed));
            if (returnedDay != day)
            {
                if (state.QuotaDay != returnedDay) { state.QuotaDay = returnedDay; state.QuotaUnitsUsed = maximum; }
            }
            if (result.Outcome == YouTubeOutcome.Success) state.ConsecutiveFailures = 0;
            else
            {
                state.ConsecutiveFailures = Math.Min(10, state.ConsecutiveFailures + 1);
                var until = time.GetUtcNow().AddMinutes(Math.Min(360, 15 * Math.Pow(2, state.ConsecutiveFailures - 1))).UtcDateTime;
                if (result.RetryAtUtc?.UtcDateTime > until) until = result.RetryAtUtc.Value.UtcDateTime;
                if (state.BackoffUntil is null || state.BackoffUntil < until) state.BackoffUntil = until;
            }
            state.WorkerReservedUnits = 0;
            state.ProviderConfigurationRejected = result.ProviderConfigurationRejectedAfter(state.ProviderConfigurationRejected);
            state.LastReason = result.Outcome.ToString();
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            db.ChangeTracker.Clear();
        }
        return result;
    }

    private async Task<int> MaintainAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var cutoff = now.AddDays(-30);
        var expired = await db.GameHighlights.Where(h => (h.SelectedVideoId != null || h.Title != null || h.ChannelId != null || h.SelectionEvidenceJson != null
            || h.PublishedAt != null || h.MadeForKids != null || h.AllowedRegions != null || h.BlockedRegions != null || h.ValidatedAt != null
            || h.ProviderDataRefreshedAt != null || h.ProviderDataExpiresAt != null)
            && (h.ProviderDataExpiresAt == null || h.ProviderDataExpiresAt <= now || h.ProviderDataRefreshedAt == null || h.ProviderDataRefreshedAt <= cutoff))
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.SelectedVideoId, (string?)null).SetProperty(h => h.Title, (string?)null)
                .SetProperty(h => h.ChannelId, (string?)null).SetProperty(h => h.SelectionEvidenceJson, (string?)null)
                .SetProperty(h => h.PublishedAt, (DateTime?)null).SetProperty(h => h.MadeForKids, (bool?)null)
                .SetProperty(h => h.AllowedRegions, (string[]?)null).SetProperty(h => h.BlockedRegions, (string[]?)null)
                .SetProperty(h => h.ValidatedAt, (DateTime?)null).SetProperty(h => h.ProviderDataRefreshedAt, (DateTime?)null)
                .SetProperty(h => h.ProviderDataExpiresAt, (DateTime?)null).SetProperty(h => h.Availability, HighlightsAvailability.Unavailable)
                .SetProperty(h => h.UnavailableReason, "ProviderDataExpired"), cancellationToken);
        await db.HighlightsOperations.Where(h => h.ProviderDataExpiresAt == null || h.ProviderDataExpiresAt <= now || h.ProviderDataRefreshedAt == null || h.ProviderDataRefreshedAt <= cutoff)
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.UploadsPlaylistId, (string?)null).SetProperty(h => h.UploadsPageToken, (string?)null)
                .SetProperty(h => h.ProviderDataRefreshedAt, (DateTime?)null).SetProperty(h => h.ProviderDataExpiresAt, (DateTime?)null), cancellationToken);
        return expired;
    }
}
