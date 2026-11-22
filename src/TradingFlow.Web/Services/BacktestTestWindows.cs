using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Regime;

namespace TradingFlow.Web.Services;

/// <summary>One registered held-back (never-tuned-on) test period of a strategy, with the registry's own use state.</summary>
/// <param name="EndUtc">Exclusive end.</param>
public sealed record HeldBackTestPeriod(string StrategyId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, ResearchHoldoutState State)
{
    /// <summary>Plain wording for the registry's use state.</summary>
    public string StateLabel => State switch
    {
        ResearchHoldoutState.Unopened => "not opened by a registered trial",
        ResearchHoldoutState.Consumed => "already used, result not published",
        _ => "already used"
    };
}

/// <summary>Works out the Strategy Lab's test window choices: a profile's own window and the registered held-back periods.</summary>
public static class BacktestTestWindows
{
    /// <summary>
    /// A profile's window: its own start and end when both are set, otherwise its rolling lookback as New York calendar
    /// days ending at the start of today. Null when the profile configures neither.
    /// </summary>
    public static (DateTimeOffset StartUtc, DateTimeOffset EndUtc)? ProfileWindow(TimeWindowConfig window, DateTimeOffset now)
    {
        if (window is { Start: { } start, End: { } end }) return (start.ToUniversalTime(), end.ToUniversalTime());
        if (window.LookbackDays <= 0) return null;
        var today = TimeZoneInfo.ConvertTime(now, VixRegime.NewYorkTimeZone).Date;
        return (NewYorkMidnight(today.AddDays(-window.LookbackDays)), NewYorkMidnight(today));
    }

    /// <summary>
    /// Every held-back period registered in the ledger, once per strategy and dates, with the most-used state any trial
    /// recorded for it (a period opened by one trial counts as opened). Signal and portfolio trials are
    /// registered under the strategy they test; catalog studies use a study name, so theirs match no strategy.
    /// </summary>
    public static IReadOnlyList<HeldBackTestPeriod> HeldBackPeriods(ResearchLedger ledger) =>
        ledger.Trials
            .Where(entry => entry.HoldoutState is not null)
            .SelectMany(entry => entry.Trial.Partitions
                .Where(partition => partition.Name.Equals("holdout", StringComparison.OrdinalIgnoreCase))
                .Select(partition => new HeldBackTestPeriod(entry.Trial.FamilyId, partition.StartUtc, partition.EndUtc, entry.HoldoutState!.Value)))
            .GroupBy(period => (period.StrategyId, period.StartUtc, period.EndUtc))
            .Select(group => group.MaxBy(period => period.State)!)
            .OrderBy(period => period.StrategyId, StringComparer.Ordinal)
            .ThenBy(period => period.StartUtc)
            .ToArray();

    // Midnight of a New York calendar date in UTC (midnight always exists there; clock changes happen at 02:00).
    private static DateTimeOffset NewYorkMidnight(DateTime date) =>
        new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), VixRegime.NewYorkTimeZone), TimeSpan.Zero);
}
