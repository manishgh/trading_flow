namespace TradingFlow.Engine.Execution;

public enum ManualEntryPolicy
{
    StrategyGated,
    OperatorDirect
}

public sealed record ManualEntryOptions(ManualEntryPolicy Policy)
{
    public static ManualEntryOptions Parse(string value) => new(value.Trim().ToLowerInvariant() switch
    {
        "strategy_gated" => ManualEntryPolicy.StrategyGated,
        "operator_direct" => ManualEntryPolicy.OperatorDirect,
        _ => throw new InvalidOperationException(
            "manual_entry_policy must be strategy_gated or operator_direct.")
    });
}
