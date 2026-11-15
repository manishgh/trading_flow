using Microsoft.EntityFrameworkCore;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Data.Context;

internal static class ProductionRunPersistence
{
    // Keep immutable provenance identical across startup, candidates, and journal writes.
    public static async Task EnsureAsync(
        TradingFlowDbContext context,
        ProductionRun requested,
        CancellationToken cancellationToken)
    {
        var existing = await context.ProductionRuns
            .SingleOrDefaultAsync(record => record.RunId == requested.RunId, cancellationToken);
        if (existing is null)
        {
            context.ProductionRuns.Add(requested);
            return;
        }

        RequireSameProvenance(existing, requested);
    }

    // Use a common database clock for lease checks and paper discovery at the broker boundary.
    public static async Task<DateTimeOffset> ReadDatabaseTimeAsync(
        TradingFlowDbContext context, CancellationToken cancellationToken)
    {
        var ticks = await context.Database
            .SqlQueryRaw<long>("SELECT CAST((julianday('now') - 1721425.5) * 864000000000 AS INTEGER) AS \"Value\"")
            .SingleAsync(cancellationToken);
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    // Runtime state and lease changes do not alter the run’s historical provenance.
    public static void RequireSameProvenance(ProductionRun existing, ProductionRun requested, bool requireSameBuild = true)
    {
        if (existing.UniverseSnapshotId != requested.UniverseSnapshotId || existing.DecisionRunId != requested.DecisionRunId ||
            existing.SchemaVersion != requested.SchemaVersion ||
            !existing.Profile.Equals(requested.Profile, StringComparison.Ordinal) ||
            !existing.ConfigHash.Equals(requested.ConfigHash, StringComparison.Ordinal) ||
            (requireSameBuild && !existing.CodeVersion.Equals(requested.CodeVersion, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Run {requested.RunId:N} already exists with different provenance. " +
                $"Stored profile {existing.Profile}, build {existing.CodeVersion}, config {existing.ConfigHash}; " +
                $"requested profile {requested.Profile}, build {requested.CodeVersion}, config {requested.ConfigHash}.");
        }
    }
}
