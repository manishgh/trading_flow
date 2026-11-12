using System.Collections.Concurrent;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Backtesting;

/// <summary>
/// One run's decision-hour evidence per stock-session (decision, provisional snapshot, skip reason, unresolved window),
/// computed once and shared by every strategy of the run, so several decision-hour strategies do not repeat the full
/// indicator computation. Thread-safe; each entry is computed exactly once.
/// </summary>
internal sealed class DecisionHourEvidenceCache
{
    private readonly ConcurrentDictionary<(string Ticker, DateOnly Session), Lazy<SessionEvidence>> entries = new();
    private int computed;

    /// <summary>A stock-session's evidence: the decision and its snapshot, or the skip reason; plus the unresolved window.</summary>
    public sealed record SessionEvidence(DecisionHourDailyBars.SessionDecision? Decision, IndicatorSnapshot? Snapshot,
        string? SkipReason, DecisionHourDailyBars.UnresolvedWindow? Window);

    /// <summary>How many stock-sessions were computed (each at most once).</summary>
    public int ComputedCount => Volatile.Read(ref computed);

    /// <summary>The stock-session's evidence, computed by <paramref name="compute"/> on first request only.</summary>
    public SessionEvidence Get(string ticker, DateOnly session, Func<SessionEvidence> compute) =>
        entries.GetOrAdd((ticker.ToUpperInvariant(), session), _ => new(() =>
        {
            Interlocked.Increment(ref computed);
            return compute();
        })).Value;

    /// <summary>
    /// Computes one session's evidence in full: DecisionHourDailyBars.Build on the session's completed daily bar (its open only)
    /// and regular-session hourly bars, the provisional snapshot over every prior completed daily bar plus the provisional bar,
    /// and the unresolved window from the completed daily bar for resting orders.
    /// </summary>
    public static SessionEvidence Compute(IndicatorEngine engine, IReadOnlyList<OhlcvBar> dailyBars, int index,
        IReadOnlyList<OhlcvBar> sessionHourlyBars, MarketSessionSchedule schedule, TimeZoneInfo zone)
    {
        var (decision, skip) = DecisionHourDailyBars.Build(dailyBars[index], sessionHourlyBars, schedule, zone);
        var snapshot = decision is null ? null
            : DecisionHourDailyBars.ProvisionalSnapshot(engine, dailyBars.Take(index).ToArray(), decision.ProvisionalDailyBar);
        return new(decision, snapshot, skip, DecisionHourDailyBars.OpeningWindow(dailyBars[index], sessionHourlyBars, schedule, zone));
    }
}
