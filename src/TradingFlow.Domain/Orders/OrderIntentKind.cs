namespace TradingFlow.Domain.Orders;

/// <summary>
/// Describes why an immutable broker intent exists and whether strategy-candidate
/// authorization is required.
/// </summary>
public enum OrderIntentKind
{
    StrategyEntry = 1,
    OperatorEntry = 2,
    ProtectiveStop = 3
}
