using TradingFlow.Domain.Backtesting;

namespace TradingFlow.Backtesting;

public sealed partial class BacktestRunner
{
    // Bounds trade-record requests per stock and run; gaps beyond it stay unresolved (missing evidence), never assumed quiet.
    private const int MaximumQuietIntervalGapsPerStock = 2_000;

    private IIntervalTradePrintSource? intervalTradePrintSource;
    private bool intervalTradePrintSourceResolved;

    /// <summary>
    /// The trade-record source used to confirm five-minute intervals without a candle, created once per runner. A caller
    /// factory wins (tests and research replays); otherwise only Alpaca runs on live provider data get the SIP source,
    /// archived under the durable raw root so repeat runs re-read it. Other providers get none, so gaps stay gaps.
    /// </summary>
    private IIntervalTradePrintSource? CreateIntervalTradePrintSource(BacktestRunConfig run)
    {
        if (intervalTradePrintSourceResolved) return intervalTradePrintSource;
        intervalTradePrintSourceResolved = true;
        if (intervalTradePrintSourceFactory is not null) return intervalTradePrintSource = intervalTradePrintSourceFactory(run);
        // The trade records are consolidated SIP prints, so they can only explain gaps in SIP candles.
        if (marketDataProviderFactory is not null || !run.Provider.Equals("alpaca", StringComparison.OrdinalIgnoreCase) ||
            !run.Providers.Alpaca.DataFeed.Equals("sip", StringComparison.OrdinalIgnoreCase)) return null;
        var rawRoot = logicalInputRoots?.RawRoot ?? run.RawRoot;
        return intervalTradePrintSource = new AlpacaSipIntervalTradePrintSource(_dataProviders.CreateAlpacaMarketDataClient(run),
            Path.Combine(rawRoot, "sip-interval-trades"));
    }

    /// <summary>
    /// Publishes each stock's confirmed quiet intervals, unresolved gaps and trade-archive references beside the results,
    /// and returns the quiet intervals per stock for the fill replay. Nothing is written when no stock had a gap.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlySet<DateTimeOffset>>?> PublishQuietIntervalsAsync(BacktestRunConfig run,
        IReadOnlyList<StrategyTickerBacktestBatch> batches, CancellationToken token)
    {
        var resolved = batches.Where(batch => batch.IntradayQuietIntervals is { } item &&
            (item.QuietIntervals.Count > 0 || item.UnresolvedGaps.Count > 0)).ToArray();
        if (resolved.Length == 0) return null;
        var payload = new
        {
            runName = run.RunName,
            conditionTable = SipNonPriceSettingConditions.Version,
            stocks = resolved.ToDictionary(batch => batch.Ticker, batch => new
            {
                confirmedQuietIntervalsUtc = batch.IntradayQuietIntervals!.QuietIntervals.Order().ToArray(),
                unresolvedGapsUtc = batch.IntradayQuietIntervals.UnresolvedGaps.Select(gap => new
                {
                    startUtc = gap,
                    reason = batch.IntradayQuietIntervals.UnresolvedReasons.GetValueOrDefault(gap, "unresolved")
                }).ToArray(),
                tradeArchives = batch.IntradayQuietIntervals.EvidenceIds
            }, StringComparer.OrdinalIgnoreCase)
        };
        await _artifactWriter.WriteTextAsync(Path.Combine(run.ResultsRoot, "evidence", $"{run.RunName}-quiet-intervals.json"),
            System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), token);
        return resolved.ToDictionary(batch => batch.Ticker.ToUpperInvariant(),
            batch => batch.IntradayQuietIntervals!.QuietIntervals, StringComparer.Ordinal);
    }
}
