using System.Text.Json.Serialization;

namespace TradingFlow.Domain.Backtesting;

/// <summary>Conditions proven at a completed decision close, in the simulator’s existing precedence order.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BacktestExitCondition>))]
public enum BacktestExitCondition
{
    ConfirmedVwapFailure,
    ConfirmedVwapReclaim,
    CloseBelowEma20,
    CloseBelowVwap,
    CloseAboveVwap,
    Ema10CrossBelowEma20,
    NegativeMacdHistogram,
    PositiveMacdHistogram,
    CloseAboveSma5,
    CloseBelowSma5,
    Rsi2Recovery,
    Sma10NearSma20,
    Sma10CrossBelowSma20,
    Sma10CrossAboveSma20,
    LogPriceFade,
    MaxHoldBars,
    MaxHoldTime,
    EntryFactorAtOrBelowThreshold,
    OffList
}

/// <summary>The source session or interval close when known, and the replay's separate evidence availability.</summary>
public sealed record BacktestExitEvidence(DateTimeOffset DecisionSourceStartUtc, DateTimeOffset? DecisionCloseUtc, DateTimeOffset AvailableAtUtc,
    IReadOnlyList<BacktestExitCondition> Conditions)
{
    // A separately read journal entry must compare by its ordered conditions and both clocks.
    public bool Equals(BacktestExitEvidence? other) =>
        other is not null && DecisionSourceStartUtc == other.DecisionSourceStartUtc && DecisionCloseUtc == other.DecisionCloseUtc &&
        AvailableAtUtc == other.AvailableAtUtc &&
        Conditions.SequenceEqual(other.Conditions);

    // Keep record hashing consistent with the journal's structural comparison.
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(DecisionSourceStartUtc);
        hash.Add(DecisionCloseUtc);
        hash.Add(AvailableAtUtc);
        foreach (var condition in Conditions)
            hash.Add(condition);
        return hash.ToHashCode();
    }
}
