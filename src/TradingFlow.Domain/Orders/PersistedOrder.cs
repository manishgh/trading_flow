namespace TradingFlow.Domain.Orders;

public sealed class PersistedOrder
{
    public string OrderId { get; set; } = string.Empty;
    public string Ticker { get; set; } = string.Empty;
    public string RunName { get; set; } = string.Empty;
    public string ClientOrderId { get; set; } = string.Empty;
    public string StrategyName { get; set; } = string.Empty;
    public string Broker { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal EntryPrice { get; set; }
    public decimal StopLossPrice { get; set; }
    public decimal TakeProfitPrice { get; set; }
    public int ShareQuantity { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
