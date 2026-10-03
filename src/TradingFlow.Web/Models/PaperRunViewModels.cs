namespace TradingFlow.Web.Models;
public class ChartDataPoint
{
    public DateTimeOffset Timestamp { get; set; }
    public string Ticker { get; set; } = String.Empty;
    public string StrategyName { get; set; } = String.Empty;
    public string Timeframe { get; set; } = String.Empty;
    public decimal Close { get; set; }
    public decimal Atr { get; set; }
    public decimal? RelativeVolume { get; set; }
    public decimal? SlotRelativeVolume { get; set; }
    public string RelativeVolumeSource { get; set; } = String.Empty;
    public decimal? SlotMedianVolume { get; set; }
    public decimal? CumulativeSameTimeMedianVolume { get; set; }
    public int RelativeVolumeSampleCount { get; set; }
    public int SlotRelativeVolumeSampleCount { get; set; }
    public string MarketEvidenceProfileVersion { get; set; } = String.Empty;
    public string RelativeVolumeCohort { get; set; } = String.Empty;
    public string DataFeed { get; set; } = String.Empty;
    public string AdjustmentPolicy { get; set; } = String.Empty;
    public string MarketEvidenceReliability { get; set; } = String.Empty;
    public decimal Volume { get; set; }
    public decimal? Rsi { get; set; }
    public decimal? Vwap { get; set; }
}

public class OrderDisplayViewModel
{
    public OrderDisplayViewModel(TradingFlow.Domain.Orders.ActiveBrokerOrder order, decimal? currentPrice)
    {
        Order = order;
        CurrentPrice = currentPrice;
    }

    public TradingFlow.Domain.Orders.ActiveBrokerOrder Order { get; }
    public decimal? CurrentPrice { get; }

    public decimal? DistancePct
    {
        get
        {
            if (Order.LimitPrice == null || CurrentPrice == null || CurrentPrice == 0) return null;
            return (CurrentPrice - Order.LimitPrice) / CurrentPrice * 100m;
        }
    }

}
