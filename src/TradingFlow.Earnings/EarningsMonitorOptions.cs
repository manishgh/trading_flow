using TradingFlow.Engine.Indicators;

namespace TradingFlow.Earnings;

public sealed record EarningsMonitorOptions(
    TimeSpan CalendarRefreshInterval,
    TimeSpan AnalysisInterval,
    TimeSpan RequestTimeout,
    TimeSpan PriorResultRequestTimeout,
    TimeSpan PriorResultRefreshInterval,
    TimeSpan AnalysisSnapshotHeartbeat,
    int PriorResultMinimumDaysAgo,
    int PriorResultMaximumDaysAgo,
    int IntradayLookbackDays,
    int ReferenceBarCount,
    decimal MinimumSlotRelativeVolume,
    int MinimumSlotRelativeVolumeSamples,
    int MaximumParallelAnalyses)
{
    public static EarningsMonitorOptions Default { get; } = new(
        CalendarRefreshInterval: TimeSpan.FromMinutes(15),
        AnalysisInterval: TimeSpan.FromMinutes(1),
        RequestTimeout: TimeSpan.FromSeconds(45),
        PriorResultRequestTimeout: TimeSpan.FromMinutes(2),
        PriorResultRefreshInterval: TimeSpan.FromHours(24),
        AnalysisSnapshotHeartbeat: TimeSpan.FromMinutes(15),
        PriorResultMinimumDaysAgo: 70,
        PriorResultMaximumDaysAgo: 112,
        // 100 calendar days reliably covers the 63 prior US trading sessions used by RVOL.
        IntradayLookbackDays: 100,
        ReferenceBarCount: 78,
        MinimumSlotRelativeVolume: 1.5m,
        MinimumSlotRelativeVolumeSamples: IndicatorEngine.RelativeVolumeLookbackSessions,
        MaximumParallelAnalyses: 8);
}
