namespace TradingFlow.Domain.Earnings;

public enum EarningsBreakoutAssessment
{
    AwaitingRelease = 0,
    InsufficientData = 1,
    NotConfirmed = 2,
    Possible = 3
}
