namespace TradingFlow.Finviz;

public sealed record FinvizScreenerRow(
    string Ticker,
    decimal? RelativeVolume);

/// <summary>
/// One archived Finviz screener response with the provider rows and immutable
/// provenance needed by operational discovery.
/// </summary>
public sealed record FinvizScreenerSnapshot(
    IReadOnlyList<FinvizScreenerRow> Rows,
    DateTimeOffset ReceivedAtUtc,
    string NormalizedQuery,
    string RawReference);
