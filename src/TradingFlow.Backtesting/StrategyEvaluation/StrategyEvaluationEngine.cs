using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Pipeline;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Backtesting.StrategyEvaluation;

public sealed class StrategyEvaluationEngine
{
    private readonly CandlePipelineEngine candlePipeline = new();
    private readonly SignalGenerator signalGenerator = new();
    private readonly BasicStrategyEvaluator evaluator = new();

    public async Task<StrategyEvaluationResponse> EvaluateAsync(
        StrategyEvaluationRequest request,
        IMarketDataProvider provider,
        BacktestRunConfig runConfig,
        StrategyDefinition strategy,
        string strategyPath,
        CancellationToken cancellationToken)
    {
        var tickers = ResolveTickers(request.Tickers, runConfig.Tickers);
        var lookbackDays = request.LookbackDays <= 0 ? runConfig.TimeWindow.LookbackDays : request.LookbackDays;
        var end = request.End ?? DateTimeOffset.UtcNow;
        var start = request.Start ?? end.AddDays(-lookbackDays);
        var requiredTimeframes = ResolveRequiredTimeframes(strategy);
        var downloadTimeframes = runConfig.Intervals
            .Where(x => !String.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (downloadTimeframes.Length == 0)
        {
            downloadTimeframes = requiredTimeframes;
        }

        var marketState = await candlePipeline.RunAsync(
            new CandlePipelineRequest(
                tickers,
                downloadTimeframes,
                requiredTimeframes,
                runConfig.DerivedTimeframes.Source,
                start,
                end,
                runConfig.Engine.BoundedCapacity,
                runConfig.Engine.WorkerCount,
                runConfig.Execution.ExtendedHours,
                strategy.Session.ExchangeTimezone),
            provider,
            cancellationToken);

        var results = tickers
            .Select(ticker => EvaluateTicker(ticker, strategy, marketState))
            .ToArray();

        return new StrategyEvaluationResponse(
            runConfig.RunName,
            strategy.StrategyId,
            strategy.StrategyName,
            strategyPath,
            start,
            end,
            requiredTimeframes,
            results,
            FormatProfiler(TradingFlow.Domain.Logging.ApiProfiler.GetSummary("Alpaca")));
    }

    private StrategyTickerEvaluation EvaluateTicker(
        string ticker,
        StrategyDefinition strategy,
        CandlePipelineResult marketState)
    {
        if (!marketState.TickerStates.TryGetValue(ticker, out var tickerState))
        {
            var reason = marketState.Failures.TryGetValue(ticker, out var failure)
                ? failure
                : "missing_ticker_market_state";
            return StrategyTickerEvaluation.NoData(ticker, strategy.Timeframe, reason);
        }

        if (!tickerState.SnapshotsByTimeframe.TryGetValue(strategy.Timeframe, out var strategySnapshots) ||
            strategySnapshots.Count == 0)
        {
            return StrategyTickerEvaluation.NoData(ticker, strategy.Timeframe, "missing_strategy_timeframe_bars");
        }

        var latest = strategySnapshots[^1];
        var readinessReason = GetSignalReadinessRejection(latest);
        if (readinessReason is not null)
        {
            return StrategyTickerEvaluation.FromSnapshot(ticker, latest, "NoSignal", readinessReason, null);
        }

        if (!tickerState.BarsByTimeframe.TryGetValue(strategy.Timeframe, out var strategyBars) ||
            strategyBars.Count == 0)
        {
            return StrategyTickerEvaluation.NoData(ticker, strategy.Timeframe, "missing_strategy_timeframe_bars");
        }

        var signal = signalGenerator.CreateTradeSignal(strategy, strategyBars, strategySnapshots, strategySnapshots.Count - 1);
        if (signal is null)
        {
            return StrategyTickerEvaluation.FromSnapshot(ticker, latest, "NoSignal", "no_signal_generated", null);
        }

        var signalAvailableAt = latest.Timestamp.Add(ParseTimeframe(strategy.Timeframe));
        var confluenceRejection = signalGenerator.GetConfluenceRejection(strategy, signalAvailableAt, tickerState.SnapshotsByTimeframe);
        if (confluenceRejection is not null)
        {
            return StrategyTickerEvaluation.FromSnapshot(ticker, latest, "Rejected", confluenceRejection, signal);
        }

        var entryRejection = evaluator.GetLongEntryRejection(strategy, signal, ResolveEntryRelativeVolume(strategy, latest) ?? 0m);
        if (entryRejection is not null)
        {
            return StrategyTickerEvaluation.FromSnapshot(ticker, latest, "Rejected", entryRejection, signal);
        }

        return StrategyTickerEvaluation.FromSnapshot(ticker, latest, "Accepted", null, signal);
    }

    private static decimal? ResolveEntryRelativeVolume(StrategyDefinition strategy, IndicatorSnapshot snapshot)
    {
        return strategy.EntryRules.MinVolumeSpikeSource.ToLowerInvariant() switch
        {
            "session_vs_average_day" or "session" or "finviz_style" => snapshot.SessionRelativeVolume,
            "slot_bar" or "bar_same_time" => snapshot.SlotRelativeVolume,
            _ => snapshot.RelativeVolume
        };
    }
    private static string? GetSignalReadinessRejection(IndicatorSnapshot snapshot)
    {
        var missing = new List<string>();
        if (snapshot.Rsi is null) missing.Add("rsi");
        if (snapshot.Atr is null) missing.Add("atr");
        if (snapshot.RelativeVolume is null) missing.Add("relative_volume");
        if (snapshot.Vwap is null) missing.Add("vwap");
        if (snapshot.BollingerMiddle is null) missing.Add("bollinger_middle");
        if (snapshot.MacdHistogram is null) missing.Add("macd_histogram");
        return missing.Count == 0 ? null : $"signal_missing_indicators ({String.Join(", ", missing)})";
    }

    private static string[] ResolveTickers(IReadOnlyList<string>? requestedTickers, IReadOnlyList<string> configTickers)
    {
        var source = requestedTickers is { Count: > 0 } ? requestedTickers : configTickers;
        return source
            .Select(x => x.Trim().ToUpperInvariant())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] ResolveRequiredTimeframes(StrategyDefinition strategy)
    {
        var timeframes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            strategy.Timeframe,
            strategy.Execution.Timeframe
        };
        if (strategy.Confluence.Enabled)
        {
            timeframes.Add(strategy.Confluence.Timeframe);
        }

        return timeframes.ToArray();
    }

    private static TimeSpan ParseTimeframe(string timeframe) => TimeframeParser.Parse(timeframe);

    private static ApiProfilerDto FormatProfiler(TradingFlow.Domain.Logging.ProfilerSummary profile)
    {
        return new ApiProfilerDto(
            profile.Name,
            profile.AppStartTime,
            profile.TotalRequests,
            profile.AvgDurationMs,
            profile.MinDurationMs,
            profile.MaxDurationMs,
            profile.SuccessRate,
            profile.Endpoints
                .Select(endpoint => new ApiProfilerEndpointDto(
                    endpoint.Route,
                    endpoint.Count,
                    endpoint.AvgDurationMs,
                    endpoint.MinDurationMs,
                    endpoint.MaxDurationMs,
                    endpoint.SuccessRate))
                .ToArray());
    }
}

public sealed record StrategyEvaluationRequest(
    string? ConfigPath,
    string? StrategyPath,
    IReadOnlyList<string>? Tickers,
    int LookbackDays,
    DateTimeOffset? Start,
    DateTimeOffset? End);

public sealed record StrategyEvaluationResponse(
    string RunName,
    string StrategyId,
    string StrategyName,
    string StrategyPath,
    DateTimeOffset Start,
    DateTimeOffset End,
    IReadOnlyList<string> Timeframes,
    IReadOnlyList<StrategyTickerEvaluation> Results,
    ApiProfilerDto Profiler);

public sealed record StrategyTickerEvaluation(
    string Ticker,
    string Timeframe,
    string Decision,
    string? Reason,
    DateTimeOffset? Timestamp,
    decimal? Close,
    decimal? Rsi,
    decimal? Atr,
    decimal? Volume,
    decimal? SlotAverageVolume,
    decimal? RelativeVolume,
    decimal? Vwap,
    decimal? Ema20,
    decimal? Ema50,
    decimal? MacdHistogram,
    object? Signal)
{
    public static StrategyTickerEvaluation NoData(string ticker, string timeframe, string reason)
    {
        return new StrategyTickerEvaluation(ticker, timeframe, "NoData", reason, null, null, null, null, null, null, null, null, null, null, null, null);
    }

    public static StrategyTickerEvaluation FromSnapshot(string ticker, IndicatorSnapshot snapshot, string decision, string? reason, TradeSignal? signal)
    {
        return new StrategyTickerEvaluation(
            ticker,
            snapshot.Timeframe,
            decision,
            reason,
            snapshot.Timestamp,
            snapshot.CurrentPrice,
            snapshot.Rsi,
            snapshot.Atr,
            snapshot.CurrentVolume,
            snapshot.SlotAverageVolume,
            snapshot.RelativeVolume,
            snapshot.Vwap,
            snapshot.Ema20,
            snapshot.Ema50,
            snapshot.MacdHistogram,
            signal);
    }
}

public sealed record ApiProfilerDto(
    string Name,
    DateTimeOffset AppStartTime,
    int TotalRequests,
    double AvgDurationMs,
    double MinDurationMs,
    double MaxDurationMs,
    double SuccessRate,
    IReadOnlyList<ApiProfilerEndpointDto> Endpoints);

public sealed record ApiProfilerEndpointDto(
    string Route,
    int Count,
    double AvgDurationMs,
    double MinDurationMs,
    double MaxDurationMs,
    double SuccessRate);
