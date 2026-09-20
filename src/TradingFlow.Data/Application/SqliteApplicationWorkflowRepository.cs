using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Data.Application;

public sealed class SqliteApplicationWorkflowRepository(
    IDbContextFactory<TradingFlowDbContext> contextFactory) :
    IUniversePreviewRepository,
    IApplicationEventRepository,
    IOrderPreviewRepository
{
    public async Task SaveAsync(UniversePreviewRecord preview, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await context.UniversePreviews.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UniverseSnapshotId == preview.UniverseSnapshotId, cancellationToken);
        if (existing is not null)
        {
            if (!existing.ContentSha256.Equals(preview.ContentSha256, StringComparison.Ordinal) ||
                !existing.RequestSha256.Equals(preview.RequestSha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("An immutable universe preview cannot be replaced with different content.");
            }
            return;
        }

        context.UniversePreviews.Add(preview);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<UniversePreviewRecord?> GetAsync(Guid universeSnapshotId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.UniversePreviews.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UniverseSnapshotId == universeSnapshotId, cancellationToken);
    }

    public async Task<CandidateRunReservation> ReserveCandidateRunAsync(
        CandidateRunRequestRecord request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable,
            cancellationToken);
        var existing = await context.CandidateRunRequests
            .SingleOrDefaultAsync(item => item.IdempotencyKey == request.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            if (!existing.RequestSha256.Equals(request.RequestSha256, StringComparison.Ordinal))
            {
                throw new IdempotencyKeyConflictException(request.IdempotencyKey);
            }
            return new CandidateRunReservation(existing.CandidateRunId, false);
        }

        context.CandidateRunRequests.Add(request);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new CandidateRunReservation(request.CandidateRunId, true);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            await transaction.RollbackAsync(cancellationToken);
            await using var retry = await contextFactory.CreateDbContextAsync(cancellationToken);
            var winner = await retry.CandidateRunRequests.AsNoTracking()
                .SingleAsync(item => item.IdempotencyKey == request.IdempotencyKey, cancellationToken);
            if (!winner.RequestSha256.Equals(request.RequestSha256, StringComparison.Ordinal))
            {
                throw new IdempotencyKeyConflictException(request.IdempotencyKey);
            }
            return new CandidateRunReservation(winner.CandidateRunId, false);
        }
    }

    public async Task<long> AppendAsync(
        string streamName,
        string eventType,
        DateTimeOffset occurredAtUtc,
        string payloadJson,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var record = new ApplicationEventRecord
        {
            StreamName = streamName,
            EventType = eventType,
            OccurredAtUtc = occurredAtUtc.ToUniversalTime(),
            PayloadJson = payloadJson
        };
        context.ApplicationEvents.Add(record);
        await context.SaveChangesAsync(cancellationToken);
        return record.EventSequence;
    }

    public async Task<IReadOnlyList<ApplicationEventRecord>> ListAsync(
        string streamName,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.ApplicationEvents.AsNoTracking()
            .Where(item => item.StreamName == streamName && item.EventSequence > afterSequence)
            .OrderBy(item => item.EventSequence)
            .Take(Math.Clamp(limit, 1, 500))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<(long Floor, long Latest)> GetBoundsAsync(
        string streamName,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var values = await context.ApplicationEvents.AsNoTracking()
            .Where(item => item.StreamName == streamName)
            .Select(item => item.EventSequence)
            .ToArrayAsync(cancellationToken);
        return values.Length == 0 ? (0, 0) : (values.Min(), values.Max());
    }

    public async Task<OrderPreviewRecord> SaveAsync(OrderPreviewRecord preview, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await context.OrderPreviews.AsNoTracking()
            .SingleOrDefaultAsync(item => item.IdempotencyKey == preview.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            if (!existing.RequestSha256.Equals(preview.RequestSha256, StringComparison.Ordinal))
            {
                throw new IdempotencyKeyConflictException(preview.IdempotencyKey);
            }
            return existing;
        }
        context.OrderPreviews.Add(preview);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return preview;
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            await using var retry = await contextFactory.CreateDbContextAsync(cancellationToken);
            var winner = await retry.OrderPreviews.AsNoTracking()
                .SingleAsync(item => item.IdempotencyKey == preview.IdempotencyKey, cancellationToken);
            if (!winner.RequestSha256.Equals(preview.RequestSha256, StringComparison.Ordinal))
            {
                throw new IdempotencyKeyConflictException(preview.IdempotencyKey);
            }
            return winner;
        }
    }

    public async Task<OrderPreviewRecord?> GetByTokenHashAsync(
        string tokenSha256,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.OrderPreviews.AsNoTracking()
            .SingleOrDefaultAsync(item => item.TokenSha256 == tokenSha256, cancellationToken);
    }

    public async Task<OrderPreviewConfirmationClaim> ClaimConfirmationAsync(
        string tokenSha256,
        string idempotencyKey,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable,
            cancellationToken);
        var preview = await context.OrderPreviews.SingleOrDefaultAsync(
            item => item.TokenSha256 == tokenSha256,
            cancellationToken) ?? throw new InvalidOperationException("Order preview token was not found.");
        if (!preview.IdempotencyKey.Equals(idempotencyKey, StringComparison.Ordinal))
        {
            throw new IdempotencyKeyConflictException(idempotencyKey);
        }
        if (preview.Status is "confirmed" or "rejected")
        {
            return new OrderPreviewConfirmationClaim(preview, null, false);
        }
        if (preview.ExpiresAtUtc <= nowUtc)
        {
            preview.Status = "rejected";
            preview.OutcomeJson = "{\"code\":\"preview_expired\"}";
            preview.Version++;
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new OrderPreviewConfirmationClaim(preview, null, false);
        }
        if (preview.Status == "confirming" && preview.ConfirmationLeaseExpiresAtUtc > nowUtc)
        {
            return new OrderPreviewConfirmationClaim(preview, null, false);
        }

        var lease = Guid.NewGuid();
        preview.Status = "confirming";
        preview.ConfirmationLeaseToken = lease;
        preview.ConfirmationLeaseExpiresAtUtc = nowUtc + leaseDuration;
        preview.Version++;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new OrderPreviewConfirmationClaim(preview, lease, true);
    }

    public async Task<OrderPreviewRecord> CompleteAsync(
        Guid previewId,
        Guid leaseToken,
        string status,
        string outcomeJson,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (status is not ("confirmed" or "rejected"))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var preview = await context.OrderPreviews.SingleAsync(item => item.PreviewId == previewId, cancellationToken);
        if (preview.Status is "confirmed" or "rejected")
        {
            return preview;
        }
        if (preview.ConfirmationLeaseToken != leaseToken)
        {
            throw new InvalidOperationException("Order preview confirmation lease was lost.");
        }
        preview.Status = status;
        preview.OutcomeJson = outcomeJson;
        preview.ConfirmationLeaseToken = null;
        preview.ConfirmationLeaseExpiresAtUtc = null;
        preview.Version++;
        context.ApplicationEvents.Add(new ApplicationEventRecord
        {
            StreamName = $"orders/{preview.PreviewId:N}",
            EventType = $"order-preview.{status}",
            OccurredAtUtc = occurredAtUtc.ToUniversalTime(),
            PayloadJson = outcomeJson
        });
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return preview;
    }
}
