using TradingFlow.Domain.Market;
using TradingFlow.Engine.Market;

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
            .OrderBy(catalyst => catalyst.DecisionAvailableAt ?? catalyst.Timestamp)
            .ToArray();
        var catalystIndex = 0;
        CatalystEvent? latestCatalyst = null;

        for (var snapshotIndex = 0; snapshotIndex < snapshots.Count; snapshotIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = snapshots[snapshotIndex];
            var snapshotEndTime = snapshot.Timestamp.Add(TimeframeParser.Parse(snapshot.Timeframe));

            while (catalystIndex < orderedCatalysts.Length &&
                   (orderedCatalysts[catalystIndex].DecisionAvailableAt ?? orderedCatalysts[catalystIndex].Timestamp) <= snapshotEndTime)
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
