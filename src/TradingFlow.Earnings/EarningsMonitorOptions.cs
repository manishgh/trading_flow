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
    int SubDailyLookbackDays,
    int ReferenceBarCount,
    int MaximumParallelAnalyses,
    int MaximumParallelBarFileReads)
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
        // Five calendar days hold at least two full sessions even across a weekend or a one-day holiday: enough
        // for the 78-bar pre-release window, the 35-bar minimum, EMA 10/20 and MACD. It was 100 days for the
        // same-time volume ratio, which needs 20 earlier sessions; that ratio was shown for context only and
        // never decided anything, so it is now always empty and each watched stock reads 5 days, not about 70.
        SubDailyLookbackDays: 5,
        ReferenceBarCount: 78,
        MaximumParallelAnalyses: 8,
        // After a restart each tracked stock's stored bars are read back from about 70 day files. On
        // 4 Nov 2026, files not yet in the Windows file cache took 1.2 s per stock read one at a time and
        // 141 ms per stock read eight at a time, so 900 stocks take about 2 minutes instead of 18.
        MaximumParallelBarFileReads: 8);
}
