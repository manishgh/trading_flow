using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Data.Orders;

/// <summary>
/// Persists the exact strategy candidate that is subsequently evaluated by the
/// entry gate chain. Revalidation updates evidence without changing its identity.
/// </summary>
public sealed class SqliteCandidateRepository(
    IDbContextFactory<TradingFlowDbContext> contextFactory) : ICandidateRepository
{
    public async Task<CandidateRecord> UpsertValidatedAsync(
        ProductionRun run,
        CandidateRecord candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(candidate);
        Validate(candidate);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await ProductionRunPersistence.EnsureAsync(context, run, cancellationToken);
        var existing = await context.Candidates.SingleOrDefaultAsync(
            item => item.CandidateId == candidate.CandidateId,
            cancellationToken);
        if (existing is null)
        {
            context.Candidates.Add(candidate);
        }
        else
        {
            CopyRevalidation(candidate, existing);
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return candidate;
    }

    public async Task<CandidateRecord?> GetAsync(
        Guid candidateId,
        CancellationToken cancellationToken = default)
    {
        if (candidateId == Guid.Empty)
        {
            return null;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Candidates
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.CandidateId == candidateId, cancellationToken);
    }

    private static void Validate(CandidateRecord candidate)
    {
        if (candidate.CandidateId == Guid.Empty || candidate.RunId == Guid.Empty ||
            String.IsNullOrWhiteSpace(candidate.Symbol) ||
            String.IsNullOrWhiteSpace(candidate.SelectedStrategy) ||
            !candidate.State.Equals("SETUP_VALID", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A validated candidate requires identity, run provenance, symbol, strategy, and SETUP_VALID state.");
        }

        if (candidate.RevalidatedAtUtc == default || candidate.DiscoveredAtUtc == default)
        {
            throw new InvalidOperationException("Candidate discovery and revalidation timestamps are required.");
        }
    }

    private static void CopyRevalidation(CandidateRecord source, CandidateRecord target)
    {
        target.RevalidatedAtUtc = source.RevalidatedAtUtc;
        target.LastPrice = source.LastPrice;
        target.SpreadBps = source.SpreadBps;
        target.QuoteAgeMs = source.QuoteAgeMs;
        target.SameTimeRvol = source.SameTimeRvol;
        target.SetupScoresJson = source.SetupScoresJson;
        target.SelectedStrategy = source.SelectedStrategy;
        target.State = source.State;
        target.RejectReasonsJson = source.RejectReasonsJson;
        target.ConfigHash = source.ConfigHash;
        target.CodeVersion = source.CodeVersion;
        target.SchemaVersion = source.SchemaVersion;
    }
}
