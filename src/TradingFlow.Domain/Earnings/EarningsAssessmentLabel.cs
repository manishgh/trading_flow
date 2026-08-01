namespace TradingFlow.Domain.Earnings;

/// <summary>
/// Provides one unambiguous assessment label shared by every API consumer.
/// Earnings quality and price-action confirmation remain separate domain states,
/// but operators see both parts together so "not confirmed" has clear meaning.
/// </summary>
public static class EarningsAssessmentLabel
{
    public static string Format(
        EarningsResultAssessment result,
        EarningsBreakoutAssessment breakout)
    {
        var resultLabel = result switch
        {
            EarningsResultAssessment.Positive => "Positive earnings",
            EarningsResultAssessment.Mixed => "Mixed earnings",
            EarningsResultAssessment.Negative => "Negative earnings",
            _ => "Earnings pending"
        };
        var breakoutLabel = breakout switch
        {
            EarningsBreakoutAssessment.AwaitingRelease => "awaiting release",
            EarningsBreakoutAssessment.InsufficientData => "breakout data insufficient",
            EarningsBreakoutAssessment.Possible => "possible breakout",
            _ => "breakout not confirmed"
        };

        return $"{resultLabel} · {breakoutLabel}";
    }
}
