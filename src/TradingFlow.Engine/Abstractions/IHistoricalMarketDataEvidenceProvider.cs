using TradingFlow.Domain.Market;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Engine.Abstractions;

/// <summary>Collects exact historical queries with archived source receipts and a row limit enforced while parsing.</summary>
public interface IHistoricalMarketDataEvidenceProvider
{
    string HistoricalEndpointIdentity { get; }
    string HistoricalParserVersion { get; }
    string HistoricalCalendarIdentity { get; }
    Task<HistoricalBarsCapture> CaptureHistoricalBarsAsync(string ticker, string timeframe,
        DateTimeOffset startUtc, DateTimeOffset endUtc, long maximumBars, CancellationToken cancellationToken);
    Task<HistoricalCalendarCapture> CaptureHistoricalCalendarAsync(DateOnly startDate, DateOnly endDate,
        CancellationToken cancellationToken);
}

/// <summary>Completion is asserted only after all pages were parsed; empty bars retain the same query evidence.</summary>
public sealed record HistoricalQueryEvidence(DateTimeOffset CollectionStartedAtUtc, DateTimeOffset CollectionCompletedAtUtc,
    bool PaginationCompleted, IReadOnlyList<RawArchiveReceipt> Responses);

public sealed record HistoricalBarsCapture(IReadOnlyList<OhlcvBar> Bars, HistoricalQueryEvidence Evidence);
public sealed record HistoricalCalendarCapture(IReadOnlyDictionary<DateOnly, MarketSessionSchedule> Sessions,
    HistoricalQueryEvidence Evidence);
