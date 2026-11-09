using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;

namespace TradingFlow.Application.Jobs;

/// <summary>Turns a plan's requested per-day eligibility into the shared daily screen rules the backtest engine applies.</summary>
public static class BacktestEligibility
{
    /// <summary>
    /// Maps and checks the requested rules; null means the plan trades its fixed stock list every day. The execution replay
    /// policy decides whether entries fill at the session open, which forbids comparing that same open.
    /// </summary>
    public static DailyScreenRules? ToRules(BacktestEligibilityRules? requested, string? executionReplayPolicyId)
    {
        if (requested is null) return null;
        try
        {
            var rules = new DailyScreenRules(requested.MinPrice, DailyScreenRules.ParsePriceBasis(requested.PriceBasis),
                requested.AverageSessions, requested.MinAverageVolume, requested.MinAverageDollarVolume, requested.AtrPeriod,
                requested.MinAtr, requested.MinHistorySessions);
            rules.Validate();
            rules.RequireKnownBeforeEntry(!ExecutionReplayPolicy.IsIntradayCandle(executionReplayPolicyId));
            return rules;
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("Daily eligibility rules are invalid: " + exception.Message, exception);
        }
    }
}
