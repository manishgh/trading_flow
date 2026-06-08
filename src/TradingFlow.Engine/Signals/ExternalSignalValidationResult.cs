using TradingFlow.Domain.Strategies;

namespace TradingFlow.Engine.Signals;

public sealed record ExternalSignalValidationResult(
    bool IsAccepted,
    string? RejectionReason,
    StrategyDefinition? Strategy)
{
    public static ExternalSignalValidationResult Accepted(StrategyDefinition strategy)
    {
        return new ExternalSignalValidationResult(true, null, strategy);
    }

    public static ExternalSignalValidationResult Rejected(string reason)
    {
        return new ExternalSignalValidationResult(false, reason, null);
    }
}
