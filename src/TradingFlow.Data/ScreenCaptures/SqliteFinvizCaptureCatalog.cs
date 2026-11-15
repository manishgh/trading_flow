using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.ScreenCaptures;
using TradingFlow.Domain.Wishlists;

namespace TradingFlow.Data.ScreenCaptures;

/// <summary>Owns the observational SQLite catalog. All writes check the collector fence inside the same transaction.</summary>
public sealed class SqliteFinvizCaptureCatalog(IDbContextFactory<TradingFlowDbContext> contexts, TimeProvider clock)
{
    public const string LeaseResource = "finviz-scheduled-screen-capture";
    public const int MaximumAttempts = 3;
    private const int ScheduleId = 1;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions ObservationJson = new(JsonSerializerDefaults.Web);

    // Return the shared cursor, initializing it to the first enabled New York session date.
    public Task<FinvizCaptureSchedule> OpenScheduleAsync(
        MarketStreamLease lease, DateOnly today, CancellationToken token) =>
        OwnedWriteAsync(lease, async (db, _) =>
        {
            var schedule = await db.FinvizCaptureSchedules.FindAsync([ScheduleId], token);
            if (schedule is not null)
                return schedule;
            schedule = new FinvizCaptureSchedule { Id = ScheduleId, NextUnclosedSessionDate = today };
            db.Add(schedule);
            return schedule;
        }, token);

    // Update shared calendar status and advance only the exact oldest reconciled session date.
    public Task UpdateScheduleAsync(
        MarketStreamLease lease, DateTimeOffset? checkedAt, DateTimeOffset? next, string? error,
        DateOnly? reconciledDate, CancellationToken token) =>
        OwnedWriteAsync(lease, async (db, _) =>
        {
            var schedule = await db.FinvizCaptureSchedules.SingleAsync(token);
            schedule.CalendarCheckedAtUtc = checkedAt;
            schedule.NextSlotAtUtc = next;
            schedule.CalendarError = error;
            if (reconciledDate == schedule.NextUnclosedSessionDate)
                schedule.NextUnclosedSessionDate = schedule.NextUnclosedSessionDate.AddDays(1);
            return true;
        }, token);

    // Stage the known eligible identities without inventing a query before first claim.
    public Task StageAsync(MarketStreamLease lease, FinvizPlannedSlot plan,
        IReadOnlyList<ScreenerPreset> presets, CancellationToken token) =>
        OwnedWriteAsync(lease, async (db, now) =>
        {
            if (now < plan.PlannedAtUtc)
                return false;
            var staged = (await FindSlots(db, plan).Select(slot => slot.PresetId).ToListAsync(token)).ToHashSet();
            foreach (var preset in presets.Where(p => p.CreatedAtUtc <= plan.PlannedAtUtc && !staged.Contains(p.Id)))
            {
                db.Add(new FinvizCaptureSlot
                {
                    Id = Guid.NewGuid(), PresetId = preset.Id, SessionDate = plan.SessionDate, Kind = plan.Kind,
                    PlannedAtUtc = plan.PlannedAtUtc, State = FinvizCaptureState.Pending
                });
            }
            return true;
        }, token);

    // Recover starts that committed before a prior owner exited, including starts from a now-deleted preset.
    public async Task<IReadOnlyList<FinvizExportRequest>> GetUnfinishedAsync(FinvizPlannedSlot plan, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var starts = await (from slot in FindSlots(db, plan)
                            join attempt in db.FinvizCaptureAttempts on slot.Id equals attempt.SlotId
                            where attempt.FinishedAtUtc == null
                            select new { Slot = slot, Attempt = attempt }).ToListAsync(token);
        return starts.Select(start => ToRequest(start.Slot, start.Attempt)).ToArray();
    }

    // Reserve one request before sending it. All due first attempts sort ahead of any retry.
    public Task<FinvizExportRequest?> TryBeginAsync(MarketStreamLease lease, FinvizPlannedSlot plan, string format,
        Func<string, string> normalizeQuery, CancellationToken token) =>
        OwnedWriteAsync<FinvizExportRequest?>(lease, async (db, now) =>
        {
            if (now < plan.PlannedAtUtc || now >= plan.DeadlineUtc)
                return null;
            var ready = FindSlots(db, plan)
                .Where(slot => slot.State == FinvizCaptureState.Pending && slot.AttemptCount < MaximumAttempts)
                .Where(slot => slot.RetryNotBeforeUtcTicks == null || slot.RetryNotBeforeUtcTicks <= now.UtcTicks)
                .Where(slot => !db.FinvizCaptureAttempts.Any(attempt => attempt.SlotId == slot.Id && attempt.FinishedAtUtc == null))
                .OrderBy(slot => slot.AttemptCount).ThenBy(slot => slot.PresetId);
            while (await ready.FirstOrDefaultAsync(token) is { } slot)
            {
                var requestedAt = clock.GetUtcNow();
                if (requestedAt >= plan.DeadlineUtc || requestedAt >= lease.ExpiresAtUtc)
                    return null;
                if (slot.ClaimedAtUtc is null && !await FreezePresetAsync(db, slot, format, normalizeQuery, token))
                {
                    // Persist deletion in this transaction before selecting the next ready slot.
                    await db.SaveChangesAsync(token);
                    continue;
                }
                slot.ClaimedAtUtc ??= requestedAt;
                var attempt = new FinvizCaptureAttempt
                {
                    Id = Guid.NewGuid(), SlotId = slot.Id, Number = ++slot.AttemptCount, RequestedAtUtc = requestedAt
                };
                slot.Reason = "The collector has reserved an attempt and is waiting for its outcome.";
                db.Add(attempt);
                return ToRequest(slot, attempt);
            }
            return null;
        }, token);

    // Freeze the exact revision that exists in the first-claim transaction, including a conservative planned-revision flag.
    private static async Task<bool> FreezePresetAsync(TradingFlowDbContext db, FinvizCaptureSlot slot, string format,
        Func<string, string> normalizeQuery, CancellationToken token)
    {
        var preset = await db.ScreenerPresets.AsNoTracking().SingleOrDefaultAsync(preset => preset.Id == slot.PresetId, token);
        if (preset is null)
        {
            slot.State = FinvizCaptureState.Missed;
            slot.Reason = "The saved screen was deleted before first claim. Its planned query is unknown.";
            return false;
        }
        slot.PresetName = preset.Name;
        slot.PresetUpdatedAtUtc = preset.UpdatedAtUtc;
        slot.PlannedRevisionKnown = preset.UpdatedAtUtc <= slot.PlannedAtUtc;
        slot.Query = normalizeQuery(preset.FilterQuery);
        slot.QuerySha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(slot.Query)));
        slot.ExportFormat = format;
        return true;
    }

    // Conclude a previously reserved attempt and publish at most one successfully verified receipt for its slot.
    public Task FinishAsync(MarketStreamLease lease, FinvizExportRequest request,
        FinvizExportObservation observation, CancellationToken token) =>
        OwnedWriteAsync(lease, async (db, now) =>
        {
            var attempt = await db.FinvizCaptureAttempts.FindAsync([request.CorrelationId], token)
                ?? throw new InvalidOperationException("The scheduled Finviz attempt no longer exists.");
            if (attempt.FinishedAtUtc is not null)
                return false;
            var slot = await db.FinvizCaptureSlots.SingleAsync(slot => slot.Id == attempt.SlotId, token);
            if (request != ToRequest(slot, attempt) || attempt.Number != slot.AttemptCount || slot.State != FinvizCaptureState.Pending)
                throw new InvalidOperationException("The Finviz attempt outcome conflicts with its frozen slot.");

            attempt.FinishedAtUtc = now;
            attempt.ObservationJson = JsonSerializer.Serialize(observation, ObservationJson);
            if (observation.State is FinvizCaptureState.Captured or FinvizCaptureState.VerifiedEmpty)
            {
                VerifyPublishableObservation(observation, request);
                slot.State = observation.State;
                slot.PublishedAttemptId = slot.EvidenceAttemptId = attempt.Id;
                slot.PublishedAtUtc = now;
                slot.Reason = observation.Reason;
                slot.RetryNotBeforeUtcTicks = null;
            }
            else
            {
                await ApplyUnsuccessfulAttemptAsync(db, slot, attempt, observation, request.DeadlineUtc, now, token);
            }
            return true;
        }, token);

    // A successful slot requires a verified in-window payload and an exact raw archive pointer.
    private static void VerifyPublishableObservation(FinvizExportObservation observation, FinvizExportRequest request)
    {
        if (!observation.CompletePayload || observation.HttpStatus != 200
            || observation.ReceivedAtUtc is not { } received || received < request.RequestedAtUtc || received >= request.DeadlineUtc
            || String.IsNullOrWhiteSpace(observation.ArchiveId) || observation.Sha256?.Length != 64 || observation.ArchivedBytes is not > 0
            || observation.Headers is not { Length: > 0 } || observation.RowCount is not >= 0
            || observation.ColumnCoverage is not (FinvizColumnCoverage.Baseline or FinvizColumnCoverage.Unexpected)
            || observation.State == FinvizCaptureState.VerifiedEmpty && (!observation.MembershipVerified || observation.RowCount != 0))
            throw new InvalidOperationException("The scheduled Finviz outcome lacks the raw receipt or CSV evidence required for publication.");
    }

    // Preserve useful partial evidence if a subsequent retry failed and enforce the original retry budget.
    private static async Task ApplyUnsuccessfulAttemptAsync(TradingFlowDbContext db, FinvizCaptureSlot slot, FinvizCaptureAttempt attempt,
        FinvizExportObservation observation, DateTimeOffset deadline, DateTimeOffset now, CancellationToken token)
    {
        var previous = await ReadEvidenceAsync(db, slot.EvidenceAttemptId, token);
        if (observation.State == FinvizCaptureState.Partial || previous?.State != FinvizCaptureState.Partial)
            slot.EvidenceAttemptId = attempt.Id;

        var retryAt = now + RetryDelay;
        if (observation.RetryAfterUtc is { } providerRetry && providerRetry > retryAt)
            retryAt = providerRetry;
        var lastState = observation.State == FinvizCaptureState.Partial || previous?.State == FinvizCaptureState.Partial
            ? FinvizCaptureState.Partial : FinvizCaptureState.Failed;
        slot.Reason = observation.Reason;
        if (slot.AttemptCount >= MaximumAttempts)
            slot.Reason += " The three-attempt budget was exhausted.";
        else if (observation.RetryAfterUtc >= deadline)
            slot.Reason += " The provider's retry delay extends beyond the capture window.";
        else if (retryAt >= deadline)
            slot.Reason += " The capture window leaves no time for another attempt.";
        else
        {
            slot.RetryNotBeforeUtcTicks = retryAt.UtcTicks;
            return;
        }
        slot.State = lastState;
        slot.RetryNotBeforeUtcTicks = null;
    }

    // Close every expired reservation after receipt recovery. A slot without a durable claim has no planned-query evidence.
    public Task CloseExpiredAsync(MarketStreamLease lease, FinvizPlannedSlot plan, CancellationToken token) =>
        OwnedWriteAsync(lease, async (db, now) =>
        {
            if (now < plan.DeadlineUtc)
                return false;
            var pending = await FindSlots(db, plan).Where(slot => slot.State == FinvizCaptureState.Pending)
                .Where(slot => !db.FinvizCaptureAttempts.Any(attempt => attempt.SlotId == slot.Id && attempt.FinishedAtUtc == null))
                .ToListAsync(token);
            foreach (var slot in pending)
            {
                var previous = await ReadEvidenceAsync(db, slot.EvidenceAttemptId, token);
                slot.State = slot.AttemptCount == 0 ? FinvizCaptureState.Missed
                    : previous?.State == FinvizCaptureState.Partial ? FinvizCaptureState.Partial : FinvizCaptureState.Failed;
                slot.Reason = slot.AttemptCount == 0 ? "No attempt was claimed during the capture window. The planned query is unknown."
                    : $"{slot.Reason} The capture window elapsed.";
                slot.RetryNotBeforeUtcTicks = null;
            }
            return true;
        }, token);

    // Read the shared calendar status without contacting the provider or updating collector state.
    public async Task<FinvizCaptureSchedule?> ReadScheduleAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        return await db.FinvizCaptureSchedules.AsNoTracking().SingleOrDefaultAsync(token);
    }

    // Read the most recent slot and most recent published capture using the per-preset index.
    public async Task<FinvizPresetCaptureHistory> GetHistoryAsync(Guid presetId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var history = db.FinvizCaptureSlots.AsNoTracking().Where(slot => slot.PresetId == presetId)
            .OrderByDescending(slot => slot.SessionDate).ThenByDescending(slot => slot.Kind);
        var latest = await history.FirstOrDefaultAsync(token);
        var published = latest?.PublishedAttemptId is not null ? latest
            : await history.Where(slot => slot.PublishedAttemptId != null).FirstOrDefaultAsync(token);
        return new FinvizPresetCaptureHistory(latest,
            await ReadEvidenceAsync(db, latest?.EvidenceAttemptId, token), published,
            await ReadEvidenceAsync(db, published?.PublishedAttemptId, token));
    }

    // Rebuild the exact persisted request for sending or receipt recovery.
    private static FinvizExportRequest ToRequest(FinvizCaptureSlot slot, FinvizCaptureAttempt attempt) => new(
        slot.PresetId, attempt.Id, slot.Query!, slot.QuerySha256!, slot.ExportFormat!, attempt.RequestedAtUtc,
        slot.PlannedAtUtc + FinvizPlannedSlot.Window);

    // Select only the bounded logical slot currently being scheduled or reconciled.
    private static IQueryable<FinvizCaptureSlot> FindSlots(TradingFlowDbContext db, FinvizPlannedSlot plan) =>
        db.FinvizCaptureSlots.Where(slot => slot.SessionDate == plan.SessionDate && slot.Kind == plan.Kind);

    // Interpret durable attempt evidence without consulting or mutating the raw archive.
    private static async Task<FinvizExportObservation?> ReadEvidenceAsync(
        TradingFlowDbContext db, Guid? attemptId, CancellationToken token)
    {
        if (attemptId is not { } id)
            return null;
        var json = await db.FinvizCaptureAttempts.AsNoTracking().Where(attempt => attempt.Id == id)
            .Select(attempt => attempt.ObservationJson).SingleOrDefaultAsync(token);
        return json is null ? null : JsonSerializer.Deserialize<FinvizExportObservation>(json, ObservationJson);
    }

    // Acquire SQLite's write lock before checking the shared owner, preventing a superseded process from committing.
    private async Task<T> OwnedWriteAsync<T>(MarketStreamLease lease,
        Func<TradingFlowDbContext, DateTimeOffset, Task<T>> write, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await db.Database.OpenConnectionAsync(token);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await using var transaction = connection.BeginTransaction(deferred: false);
        await db.Database.UseTransactionAsync(transaction, token);
        var now = clock.GetUtcNow();
        if (lease.ResourceKey != LeaseResource || !await IsOwnerAsync(connection, transaction, lease, now, token))
            throw new FinvizCaptureOwnershipLostException();
        var result = await write(db, now);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return result;
    }

    // Compare against the existing lease table's UTC Unix-millisecond representation inside the writer transaction.
    private static async Task<bool> IsOwnerAsync(SqliteConnection connection, SqliteTransaction transaction,
        MarketStreamLease lease, DateTimeOffset now, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1 FROM market_stream_leases
            WHERE resource_key = $key AND owner_id = $owner AND fencing_token = $fence AND expires_at_utc > $now;
            """;
        command.Parameters.AddWithValue("$key", lease.ResourceKey);
        command.Parameters.AddWithValue("$owner", lease.OwnerId);
        command.Parameters.AddWithValue("$fence", lease.FencingToken);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        return await command.ExecuteScalarAsync(token) is not null;
    }
}

public sealed record FinvizPresetCaptureHistory(
    FinvizCaptureSlot? Latest, FinvizExportObservation? LatestEvidence,
    FinvizCaptureSlot? LastPublished, FinvizExportObservation? LastPublishedEvidence);

public sealed class FinvizCaptureOwnershipLostException() : InvalidOperationException("The scheduled Finviz collector lease has expired or changed owner.");
