using Microsoft.EntityFrameworkCore;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Data.Context;

internal static class ProductionRunPersistence
{
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

        if (existing.SchemaVersion != requested.SchemaVersion ||
            !existing.Profile.Equals(requested.Profile, StringComparison.Ordinal) ||
            !existing.ConfigHash.Equals(requested.ConfigHash, StringComparison.Ordinal) ||
            !existing.CodeVersion.Equals(requested.CodeVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Run {requested.RunId:N} already exists with different provenance.");
        }
    }
}
