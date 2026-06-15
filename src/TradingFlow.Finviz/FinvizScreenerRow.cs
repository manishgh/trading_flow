namespace TradingFlow.Finviz;

public sealed record FinvizScreenerRow(
    string Ticker,
    decimal? RelativeVolume);
