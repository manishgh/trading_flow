namespace TradingFlow.Research.Momentum;

/// <summary>
/// Decides whether a security has enough real, completed observations for a
/// research decision. Dataset start dates never establish issuer eligibility:
/// a later listing becomes eligible after its own observed history warms up.
/// </summary>
public static class CompletedBarHistoryEligibilityGuard
{
    public static CompletedBarHistoryEligibility Evaluate(
        int adjustedCompletedBarIndex,
        int asTradedCompletedBarIndex,
        int requiredCompletedBarIndex)
    {
        if (requiredCompletedBarIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requiredCompletedBarIndex),
                "The required completed-bar index cannot be negative.");
        }

        var requiredBars = requiredCompletedBarIndex + 1;
        var adjustedBars = Math.Max(0, adjustedCompletedBarIndex + 1);
        var asTradedBars = Math.Max(0, asTradedCompletedBarIndex + 1);

        return new CompletedBarHistoryEligibility(
            IsEligible:
                adjustedCompletedBarIndex >= requiredCompletedBarIndex &&
                asTradedCompletedBarIndex >= requiredCompletedBarIndex,
            RequiredCompletedBars: requiredBars,
            AdjustedCompletedBars: adjustedBars,
            AsTradedCompletedBars: asTradedBars);
    }
}

public sealed record CompletedBarHistoryEligibility(
    bool IsEligible,
    int RequiredCompletedBars,
    int AdjustedCompletedBars,
    int AsTradedCompletedBars);
