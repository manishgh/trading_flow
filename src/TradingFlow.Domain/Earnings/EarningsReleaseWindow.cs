namespace TradingFlow.Domain.Earnings;

public enum EarningsReleaseWindow
{
    Unknown = 0,
    BeforeMarketOpen = 1,
    DuringMarket = 2,
    AfterMarketClose = 3
}
