namespace TradingFlow.Domain.Strategies;

/// <summary>
/// Optional completed-candle continuation protection measured from the original entry risk.
/// An absent ExitRules policy disables it; constructing this record explicitly selects its defaults.
/// Confirmation consumes fresh candles after activation or the preceding confirmed adjustment.
/// </summary>
public sealed record ContinuationProtectionRules(
    decimal ActivationR = 2.4m,
    int CostBufferTicks = 1,
    int ConfirmationBars = 2,
    decimal TargetStepR = 1m)
{
    // Validate pure evaluator inputs without requiring a strategy or inventing an ExitRules instance.
    public void ValidateParameters(decimal initialTargetR)
    {
        if (ActivationR <= 0m || CostBufferTicks < 1 || ConfirmationBars < 2 || TargetStepR <= 0m)
            throw new InvalidOperationException(
                "Continuation protection requires positive activation/target R, at least one cost-buffer tick and at least two confirmation bars.");
        if (initialTargetR < 3m || ActivationR >= initialTargetR)
            throw new InvalidOperationException(
                "Continuation protection requires target_r_multiple >= 3 and activation_r below that initial target.");
    }

    // Reject unreachable activation, weaker-than-approved protection, and competing stop/target policies.
    public void Validate(ExitRules exits)
    {
        ArgumentNullException.ThrowIfNull(exits);
        ValidateParameters(exits.TargetRMultiple);
        if (!String.Equals(exits.ProfitTargetMode, "r_multiple", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Continuation protection requires profit_target_mode: r_multiple.");
        if (exits.EnableAtrTrailingStop)
            throw new InvalidOperationException("Continuation protection cannot run together with ATR trailing stops.");
    }
}
