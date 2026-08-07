using TradingFlow.Engine.Indicators;

namespace TradingFlow.Earnings;

public sealed record EarningsMonitorOptions(
    TimeSpan CalendarRefreshInterval,
    TimeSpan AnalysisInterval,
    TimeSpan RequestTimeout,
    TimeSpan PriorResultRequestTimeout,
    TimeSpan PriorResultRefreshInterval,
    TimeSpan AnalysisSnapshotHeartbeat,
    int RecentResultLookbackDays,
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
        RequestTimeout: TimeSpan.FromSeconds(300),
        PriorResultRequestTimeout: TimeSpan.FromMinutes(2),
        PriorResultRefreshInterval: TimeSpan.FromHours(24),
        AnalysisSnapshotHeartbeat: TimeSpan.FromMinutes(15),
        // The calendar refresh reaches back a week so after-close actuals are still picked up once
        // the provider posts them. Without this the live window advances past a report date before
        // the provider fills it in, and the historical sweep below does not reach back that far.
        RecentResultLookbackDays: 7,
        PriorResultMinimumDaysAgo: 70,
        PriorResultMaximumDaysAgo: 112,
        // 100 calendar days reliably covers the 63 prior US trading sessions used by RVOL.
        IntradayLookbackDays: 100,
        ReferenceBarCount: 78,
        MinimumSlotRelativeVolume: 1.5m,
        MinimumSlotRelativeVolumeSamples: IndicatorEngine.RelativeVolumeLookbackSessions,
        MaximumParallelAnalyses: 8);
}
