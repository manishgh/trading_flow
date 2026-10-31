using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Storage;
using TradingFlow.Domain.Market;

namespace TradingFlow.Alpaca;

public sealed partial class AlpacaMarketDataProvider
{
    private sealed class HistoricalCaptureContext(long maximumBars)
    {
        public long MaximumBars { get; } = maximumBars;
        public long RowCount { get; set; }
        public List<RawArchiveReceipt> Responses { get; } = [];
    }

    private readonly AsyncLocal<HistoricalCaptureContext?> _historicalCapture = new();

    public string HistoricalEndpointIdentity => AlpacaEndpointResolver.Resolve(_options.Profile).MarketDataRest.AbsoluteUri;
    public string HistoricalCalendarIdentity => AlpacaEndpointResolver.Resolve(_options.Profile).TradingRest.AbsoluteUri + "v2-calendar";
    public string HistoricalParserVersion => "alpaca_historical_bars_calendar_v1";

    /// <summary>Uses the ordinary archived parser with a request-local row allowance checked before every row allocation.</summary>
    public async Task<HistoricalBarsCapture> CaptureHistoricalBarsAsync(string ticker, string timeframe,
        DateTimeOffset startUtc, DateTimeOffset endUtc, long maximumBars, CancellationToken cancellationToken)
    {
        if (maximumBars <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBars));
        var prior = _historicalCapture.Value;
        var capture = new HistoricalCaptureContext(maximumBars);
        _historicalCapture.Value = capture;
        var started = DateTimeOffset.UtcNow;
        try
        {
            var bars = new List<OhlcvBar>();
            await foreach (var bar in GetBarsAsync([ticker], [timeframe], startUtc, endUtc, cancellationToken))
                bars.Add(bar);
            return new HistoricalBarsCapture(bars, new HistoricalQueryEvidence(started, DateTimeOffset.UtcNow,
                true, capture.Responses.ToArray()));
        }
        finally { _historicalCapture.Value = prior; }
    }

    /// <summary>Captures the profile-specific original calendar response before publishing explicit open and closed dates.</summary>
    public async Task<HistoricalCalendarCapture> CaptureHistoricalCalendarAsync(DateOnly startDate, DateOnly endDate,
        CancellationToken cancellationToken)
    {
        var prior = _historicalCapture.Value;
        var capture = new HistoricalCaptureContext(Int64.MaxValue);
        _historicalCapture.Value = capture;
        var started = DateTimeOffset.UtcNow;
        try
        {
            var sessions = await LoadMarketSessionSchedulesAsync(startDate, endDate, cancellationToken);
            return new HistoricalCalendarCapture(sessions, new HistoricalQueryEvidence(started, DateTimeOffset.UtcNow,
                true, capture.Responses.ToArray()));
        }
        finally { _historicalCapture.Value = prior; }
    }

    /// <summary>Stops parsing an oversized historical query before retaining the row that exceeds the book's allowance.</summary>
    private void CountHistoricalRow()
    {
        var capture = _historicalCapture.Value;
        if (capture is not null && ++capture.RowCount > capture.MaximumBars)
            throw new InvalidOperationException($"Historical source query exceeded the retained replay-bar allowance {capture.MaximumBars} while parsing provider pages.");
    }
}
