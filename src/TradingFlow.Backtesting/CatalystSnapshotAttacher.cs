using TradingFlow.Domain.Market;

namespace TradingFlow.Backtesting;

public static class CatalystSnapshotAttacher
{
    public static void AttachToSnapshots(
        IList<IndicatorSnapshot> snapshots,
        IReadOnlyList<CatalystEvent> catalysts,
        CancellationToken cancellationToken)
    {
        if (snapshots.Count == 0 || catalysts.Count == 0)
        {
            return;
        }

        var orderedCatalysts = catalysts
            .OrderBy(catalyst => catalyst.Timestamp)
            .ToArray();
        var catalystIndex = 0;
        CatalystEvent? latestCatalyst = null;

        for (var snapshotIndex = 0; snapshotIndex < snapshots.Count; snapshotIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = snapshots[snapshotIndex];
            while (catalystIndex < orderedCatalysts.Length &&
                   orderedCatalysts[catalystIndex].Timestamp <= snapshot.Timestamp)
            {
                latestCatalyst = orderedCatalysts[catalystIndex];
                catalystIndex++;
            }

            if (latestCatalyst is not null &&
                latestCatalyst.Timestamp >= snapshot.Timestamp.AddDays(-3))
            {
                snapshots[snapshotIndex] = snapshot with { Catalyst = latestCatalyst };
            }
        }
    }
}
