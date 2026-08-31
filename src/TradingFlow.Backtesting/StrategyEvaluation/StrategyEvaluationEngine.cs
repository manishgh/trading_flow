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

    public async Task<StrategyEvaluationResponse> EvaluateAsync(
        StrategyEvaluationRequest request,
        IMarketDataProvider provider,
        BacktestRunConfig runConfig,
        AuthorizedRuntimeStrategy runtimeStrategy,
        string strategyPath,
        CancellationToken cancellationToken)
    {
        var strategy = runtimeStrategy.Definition;
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
                IncludeExtendedHours: true,
                strategy.Session.ExchangeTimezone),
            provider,
            cancellationToken);

        var results = await Task.WhenAll(tickers
            .Select(ticker => EvaluateTickerAsync(
                ticker,
                runtimeStrategy,
                marketState,
                end.ToUniversalTime(),
                runConfig)));

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

    private async Task<StrategyTickerEvaluation> EvaluateTickerAsync(
        string ticker,
        AuthorizedRuntimeStrategy runtimeStrategy,
        CandlePipelineResult marketState,
        DateTimeOffset asOfUtc,
        BacktestRunConfig runConfig)
    {
        var strategy = runtimeStrategy.Definition;
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
        var setupAvailableAt = latest.Timestamp.Add(ParseTimeframe(strategy.Timeframe));
        var decisionTime = asOfUtc >= setupAvailableAt ? asOfUtc : setupAvailableAt;
        var expiresAt = decisionTime.Add(ParseTimeframe(strategy.Timeframe));
        var setupKey = $"preview:{strategy.StrategyId}:{latest.Timestamp:O}";
        var candidateJournal = new BacktestCandidateJournal();
        var candidateDecisions = new StrategyCandidateDecisionOrchestrator(
            new StrategyDecisionKernel(),
            candidateJournal);
        var simulationRun = BacktestCandidateJournal.CreateRun(
            new { Run = runConfig, Strategy = runtimeStrategy.Identity, Ticker = ticker },
            $"preview:{runConfig.RunName}:{runtimeStrategy.Identity.StrategyId}:{ticker}",
            setupAvailableAt);
        var discovery = StrategyDecisionRequestAssembler.CreateBacktestDiscovery(
            ticker,
            setupKey,
            setupAvailableAt,
            expiresAt);
        var universe = new StrategyEligibilityEvidence(
            true,
            "diagnostic_preview",
            $"preview-universe:{ticker}",
            decisionTime,
            "{\"diagnosticOnly\":true}");
        var regime = new StrategyEligibilityEvidence(
            true,
            "diagnostic_preview",
            $"preview-regime:{ticker}",
            decisionTime,
            "{\"diagnosticOnly\":true}");

        try
        {
            var decisionRequest = StrategyDecisionRequestAssembler.Create(
                runtimeStrategy,
                ticker,
                tickerState.BarsByTimeframe,
                tickerState.SnapshotsByTimeframe,
                [],
                decisionTime,
                discovery,
                universe,
                regime,
                StrategyCandidateState.DataWarming,
                0,
                setupKey,
                setupAvailableAt,
                expiresAt,
                runConfig.Engine.IndicatorWarmupBars,
                simulationRun.RunId);
            var persisted = await candidateDecisions.EvaluateAsync(
                simulationRun,
                decisionRequest);
            var decision = persisted.Decision
                ?? throw new InvalidOperationException(
                    "Diagnostic candidate became terminal before its current decision was evaluated.");
            var label = decision.IsTriggered
                ? "Accepted"
                : decision.State == StrategyCandidateState.Armed ? "Armed" : "Rejected";
            return StrategyTickerEvaluation.FromSnapshot(
                ticker,
                latest,
                label,
                decision.NoEntryReason,
                decision.Signal);
        }
        catch (InvalidOperationException exception)
        {
            return StrategyTickerEvaluation.FromSnapshot(
                ticker,
                latest,
                "Rejected",
                exception.Message,
                null);
        }
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
    decimal? SlotMedianVolume,
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
            snapshot.SlotMedianVolume,
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
