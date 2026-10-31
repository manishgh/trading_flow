using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Risk;

namespace TradingFlow.Engine.Execution;

/// <summary>
/// Frozen fill/risk inputs and the last completed-candle protection state.
/// Only the policy can create a new version; moving a stop never changes original R.
/// </summary>
public sealed record ContinuationProtectionState
{
    // Keep position facts and supplied execution costs immutable across all policy updates.
    internal ContinuationProtectionState(
        string ticker,
        PlannedOrderSide side,
        DateTimeOffset enteredAtUtc,
        decimal entryPrice,
        decimal initialStopPrice,
        decimal initialTargetR,
        decimal estimatedRoundTripCostPerShare,
        decimal tickSize,
        ContinuationProtectionRules rules)
    {
        Ticker = ticker;
        Side = side;
        EnteredAtUtc = enteredAtUtc;
        EntryPrice = entryPrice;
        InitialStopPrice = initialStopPrice;
        OriginalRiskPerShare = Math.Abs(entryPrice - initialStopPrice);
        EstimatedRoundTripCostPerShare = estimatedRoundTripCostPerShare;
        TickSize = tickSize;
        Rules = rules;
        InitialTargetR = initialTargetR;
        StopPrice = initialStopPrice;
        TargetPrice = entryPrice + (side == PlannedOrderSide.Long ? 1m : -1m) * initialTargetR * OriginalRiskPerShare;
    }

    public string Ticker { get; }
    public PlannedOrderSide Side { get; }
    public DateTimeOffset EnteredAtUtc { get; }
    public decimal EntryPrice { get; }
    public decimal InitialStopPrice { get; }
    public decimal OriginalRiskPerShare { get; }
    public decimal EstimatedRoundTripCostPerShare { get; }
    public decimal TickSize { get; }
    public ContinuationProtectionRules Rules { get; }
    public decimal InitialTargetR { get; }
    public int CompletedTargetExtensions { get; internal init; }
    public decimal StopPrice { get; internal init; }
    public decimal TargetPrice { get; internal init; }
    public bool IsActivated { get; internal init; }
    public int ConsecutiveContinuationBars { get; internal init; }
    public OhlcvBar? PreviousCompletedBar { get; internal init; }
    public DateTimeOffset? LastEvaluatedAtUtc { get; internal init; }
}

/// <summary>
/// A new stop/target becomes eligible strictly after EffectiveAfterUtc. The input
/// candle and all execution events through that instant keep their prior orders.
/// State can advance without an order change (for example, the first confirming candle).
/// </summary>
public sealed record ContinuationProtectionDecision(
    ContinuationProtectionState State,
    bool LevelsChanged,
    DateTimeOffset? EffectiveAfterUtc,
    string Reason);
