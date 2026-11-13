using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Backtesting;

// Decision schedules (for example month_end) for daily rules: entries and the entry-factor exit are judged only on sessions
// the exchange calendar puts on the schedule. The calendar is published in advance, so no later price bar is read.
public sealed partial class BacktestRunner
{
    /// <summary>
    /// Scheduled entries and the entry-factor exit are implemented only on the ordinary daily candle path; reject the other
    /// execution paths before any candidate work so they can never ignore the schedule or never close a position.
    /// </summary>
    private static void RequireScheduledRulesSupported(BacktestRunConfig run, StrategyDefinition strategy, bool decisionHour)
    {
        var scheduled = strategy.EntryRules.DecisionSchedule is not null ||
            strategy.ExitRules.ExitOnEntryFactorAtOrBelow is not null || strategy.ExitRules.EntryFactorExitSchedule is not null;
        if (scheduled && (decisionHour || run.Engine.ExecutionReplayPolicyId is not null))
            throw new NotSupportedException(
                $"{strategy.StrategyId} uses a decision schedule or the entry-factor exit, which only the default daily candle replay supports.");
    }

    /// <summary>
    /// True when a scheduled strategy's setup bar is not on its entry schedule, so the daily loop skips it before building a
    /// decision. The kernel would reject the same bar with the same rule; skipping first avoids writing a full decision record
    /// (which embeds the stock's drawdown history) for each of the roughly 20 off-schedule sessions of every month.
    /// </summary>
    private bool IsOffDecisionSchedule(StrategyDefinition strategy, IndicatorSnapshot setupSnapshot) =>
        strategy.EntryRules.DecisionSchedule is { } schedule &&
        !DecisionSchedules.IsDecisionSession(schedule, ExchangeSessionCalendar.SessionDate(setupSnapshot.Timestamp),
            NextExchangeSession(setupSnapshot.Timestamp));

    // Attach the calendar's next trading session after the request's setup bar when the strategy schedules its entries.
    private StrategyDecisionRequest WithNextExchangeSession(StrategyDecisionRequest request, StrategyDefinition strategy)
    {
        if (strategy.EntryRules.DecisionSchedule is null) return request;
        var setupBar = request.BarsByTimeframe[strategy.Timeframe][^1];
        return request with { NextExchangeSessionDate = NextExchangeSession(setupBar.Timestamp) };
    }

    // Whether the entry-factor exit is checked at this completed bar's close under the strategy's exit schedule.
    private bool IsEntryFactorExitSession(StrategyDefinition strategy, OhlcvBar bar) =>
        strategy.ExitRules.EntryFactorExitSchedule is not { } schedule ||
        DecisionSchedules.IsDecisionSession(schedule, ExchangeSessionCalendar.SessionDate(bar.Timestamp),
            NextExchangeSession(bar.Timestamp));

    /// <summary>
    /// The session whose list membership governs a decision: the New York date of the decision time, moved to the calendar's
    /// next trading session when that date is a weekend or holiday. A daily bar's decision time is the following midnight, so
    /// a Friday close is judged on Monday's list (the session it fills in), not on a Saturday the list never contains. Without
    /// an exchange calendar, or for a date the calendar does not cover, the date itself is used.
    /// </summary>
    private DateOnly EligibilitySession(DateTimeOffset decisionTime)
    {
        var day = ExchangeSessionCalendar.SessionDate(decisionTime);
        return candidateSessionSchedules is { } calendar && calendar.TryGetValue(day, out var schedule) && !schedule.IsTradingDay
            ? ExchangeSessionCalendar.NextTradingSession(calendar, day)
            : day;
    }

    /// <summary>
    /// The exchange calendar's next trading session after the session of the daily bar starting at this instant. The bar's
    /// New York date must itself be a trading day: a bar stamped at another hour (for example 00:00 UTC, which is the previous
    /// New York evening) would otherwise shift every month end by one session without any error.
    /// </summary>
    private DateOnly NextExchangeSession(DateTimeOffset barStart)
    {
        var calendar = candidateSessionSchedules
            ?? throw new InvalidDataException("A decision schedule requires the authoritative exchange calendar.");
        var session = ExchangeSessionCalendar.SessionDate(barStart);
        if (!calendar.TryGetValue(session, out var schedule) || !schedule.IsTradingDay)
            throw new InvalidDataException(
                $"The daily bar starting {barStart:O} falls on {session:yyyy-MM-dd}, which the exchange calendar does not list as a trading session.");
        return ExchangeSessionCalendar.NextTradingSession(calendar, session);
    }

    // The entry-factor exit reason for completed bar index, or null when it does not fire or is off schedule.
    private string? EntryFactorExitReason(StrategyDefinition strategy, IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots, int index) =>
        strategy.ExitRules.ExitOnEntryFactorAtOrBelow is null
            ? null
            : TechnicalExecutionEngine.GetEntryFactorExitReason(strategy, bars, snapshots, index,
                IsEntryFactorExitSession(strategy, bars[index]));
}
