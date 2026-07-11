using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Pipeline;

namespace TradingFlow.Web.Services;

// Entry preparation and market-state loading for an automation session: candle-pipeline warmup,
// timeframe resolution, signal-gate evaluation (validate vs immediate entry), and execution-bar
// signal resolution. Split into a partial file for readability; behavior is unchanged.
public sealed partial class MobileAutomationService
{
    private async Task<TickerMarketState> LoadTickerStateAsync(
        BacktestRunConfig runConfig,
        StrategyDefinition strategy,
        string ticker,
        IMarketDataProvider provider,
        CancellationToken cancellationToken)
    {
        var end = DateTimeOffset.UtcNow;
        var lookbackDays = runConfig.TimeWindow.WarmupLookbackDays > 0
            ? runConfig.TimeWindow.WarmupLookbackDays
            : Math.Max(runConfig.TimeWindow.LookbackDays, 10);
        var start = end.AddDays(-lookbackDays);
        var requiredTimeframes = ResolveRequiredTimeframes(strategy);
        var downloadTimeframes = ResolveDownloadTimeframesForAutomation(runConfig, strategy, requiredTimeframes);
        var deriveFromTimeframe = ResolveDeriveFromTimeframe(runConfig, requiredTimeframes, downloadTimeframes);

        var pipeline = new CandlePipelineEngine(candleStore);
        var state = await pipeline.RunAsync(
            new CandlePipelineRequest(
                [ticker],
                downloadTimeframes,
                requiredTimeframes,
                deriveFromTimeframe,
                start,
                end,
                runConfig.Engine.BoundedCapacity,
                runConfig.Engine.WorkerCount,
                runConfig.Execution.ExtendedHours,
                strategy.Session.ExchangeTimezone),
            provider,
            cancellationToken);

        if (!state.TickerStates.TryGetValue(ticker, out var tickerState))
        {
            var reason = state.Failures.TryGetValue(ticker, out var failure)
                ? failure
                : "missing_ticker_market_state";
            throw new InvalidOperationException(reason);
        }

        return tickerState;
    }

    private static string[] ResolveDownloadTimeframesForAutomation(
        BacktestRunConfig runConfig,
        StrategyDefinition strategy,
        IReadOnlyCollection<string> requiredTimeframes)
    {
        var source = ResolveDeriveFromTimeframe(runConfig, requiredTimeframes, runConfig.Intervals);
        var sourceDuration = TimeframeParser.Parse(source);
        var timeframes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            source
        };

        foreach (var timeframe in runConfig.Intervals.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var duration = TimeframeParser.Parse(timeframe);
            if (duration <= sourceDuration || TimeframeParser.IsDailyOrHigher(timeframe))
            {
                timeframes.Add(timeframe);
            }
        }

        if (RequiresDailyContext(strategy))
        {
            timeframes.Add("1d");
        }

        return timeframes
            .OrderBy(TimeframeParser.Parse)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ResolveDeriveFromTimeframe(
        BacktestRunConfig runConfig,
        IReadOnlyCollection<string> requiredTimeframes,
        IReadOnlyCollection<string> candidateDownloadTimeframes)
    {
        var intradayRequired = requiredTimeframes
            .Where(timeframe => !TimeframeParser.IsDailyOrHigher(timeframe))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(TimeframeParser.Parse)
            .ToArray();

        if (intradayRequired.Length == 0)
        {
            return runConfig.DerivedTimeframes.Source;
        }

        var finestRequired = intradayRequired[0];
        var finestRequiredDuration = TimeframeParser.Parse(finestRequired);
        var currentSource = string.IsNullOrWhiteSpace(runConfig.DerivedTimeframes.Source)
            ? finestRequired
            : runConfig.DerivedTimeframes.Source;

        if (TimeframeParser.Parse(currentSource) <= finestRequiredDuration)
        {
            return currentSource;
        }

        var providerCandidate = candidateDownloadTimeframes
            .Where(timeframe => !string.IsNullOrWhiteSpace(timeframe))
            .Where(timeframe => !TimeframeParser.IsDailyOrHigher(timeframe))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(TimeframeParser.Parse)
            .FirstOrDefault(timeframe => TimeframeParser.Parse(timeframe) <= finestRequiredDuration);

        return providerCandidate ?? finestRequired;
    }

    private static bool RequiresDailyContext(StrategyDefinition strategy)
    {
        return TimeframeParser.IsDailyOrHigher(strategy.Timeframe) ||
            TimeframeParser.IsDailyOrHigher(strategy.Execution.Timeframe) ||
            strategy.EntryRules.RequirePriceAboveSma50Daily ||
            strategy.EntryRules.RequirePriceAboveSma200Daily;
    }

    private PreparedEntryExecution PrepareEntryExecution(
        BacktestRunConfig runConfig,
        StrategyDefinition strategy,
        string ticker,
        TickerMarketState state)
    {
        if (!state.SnapshotsByTimeframe.TryGetValue(strategy.Timeframe, out var strategySnapshots) ||
            strategySnapshots.Count == 0 ||
            !state.BarsByTimeframe.TryGetValue(strategy.Timeframe, out var strategyBars) ||
            strategyBars.Count == 0)
        {
            throw new InvalidOperationException($"Strategy timeframe {strategy.Timeframe} is unavailable for {ticker}.");
        }

        var latest = strategySnapshots[^1];
        var readiness = GetSignalReadinessRejection(latest);
        if (readiness is not null)
        {
            throw new InvalidOperationException(readiness);
        }

        var signal = signalGenerator.CreateTradeSignal(strategy, strategyBars, strategySnapshots, strategySnapshots.Count - 1)
            ?? throw new InvalidOperationException($"No trade signal could be prepared for {ticker}.");

        var signalAvailableAt = latest.Timestamp.Add(TimeframeParser.Parse(strategy.Timeframe));
        var confluenceRejection = signalGenerator.GetConfluenceRejection(strategy, signalAvailableAt, state.SnapshotsByTimeframe);
        if (confluenceRejection is not null)
        {
            throw new InvalidOperationException(confluenceRejection);
        }

        var entryRejection = GetLongEntryGateRejection(strategy, latest, signal);
        if (entryRejection is not null)
        {
            throw new InvalidOperationException(entryRejection);
        }

        var executionSignal = ResolveExecutionOrderSignal(strategy, signal, state.SnapshotsByTimeframe)
            ?? throw new InvalidOperationException($"Execution timeframe {strategy.Execution.Timeframe} is unavailable for {ticker}.");

        return new PreparedEntryExecution(signal, executionSignal);
    }

    private PreparedEntryExecution PrepareAutomationEntryExecution(
        BacktestRunConfig runConfig,
        StrategyDefinition strategy,
        MutableAutomationSession session,
        TickerMarketState state,
        string entryMode)
    {
        if (entryMode.Equals("immediate_paper", StringComparison.OrdinalIgnoreCase))
        {
            return PrepareImmediateEntryExecution(strategy, session.Ticker, state);
        }

        try
        {
            return PrepareEntryExecution(runConfig, strategy, session.Ticker, state);
        }
        catch (InvalidOperationException exception) when (CanFallbackToStockPulseImmediateEntry(session, exception))
        {
            session.Report(
                "entry_fallback",
                "Stock Pulse alert had usable price/ATR state but RVOL was unavailable. Entering paper trade and letting guardian manage exits.");
            return PrepareImmediateEntryExecution(strategy, session.Ticker, state);
        }
    }

    private static bool CanFallbackToStockPulseImmediateEntry(MutableAutomationSession session, InvalidOperationException exception)
    {
        return session.Source.Equals("notification", StringComparison.OrdinalIgnoreCase) &&
            exception.Message.Contains("relative_volume", StringComparison.OrdinalIgnoreCase) &&
            !exception.Message.Contains("atr", StringComparison.OrdinalIgnoreCase) &&
            !exception.Message.Contains("vwap", StringComparison.OrdinalIgnoreCase) &&
            !exception.Message.Contains("macd", StringComparison.OrdinalIgnoreCase);
    }

    private PreparedEntryExecution PrepareImmediateEntryExecution(
        StrategyDefinition strategy,
        string ticker,
        TickerMarketState state)
    {
        if (!state.SnapshotsByTimeframe.TryGetValue(strategy.Execution.Timeframe, out var executionSnapshots) ||
            executionSnapshots.Count == 0)
        {
            throw new InvalidOperationException($"Execution timeframe {strategy.Execution.Timeframe} is unavailable for {ticker}.");
        }

        var latest = executionSnapshots[^1];
        if (latest.Atr is null or <= 0m)
        {
            throw new InvalidOperationException("signal_missing_indicators (atr)");
        }

        var signal = new TradeSignal(
            Ticker: ticker,
            Timestamp: latest.Timestamp,
            Timeframe: latest.Timeframe,
            CurrentPrice: latest.CurrentPrice,
            CurrentVolume: latest.CurrentVolume,
            CurrentRsi: latest.Rsi ?? 50m,
            CurrentAtr: latest.Atr.Value,
            IsAboveVwap: latest.Vwap is not null && latest.CurrentPrice >= latest.Vwap.Value,
            IsVwapPullback: false,
            IsVwapReclaim: false,
            IsVwapRejection: false,
            IsEma20Pullback: false,
            IsOpeningRangeBreakout: false,
            IsOpeningRangeBreakdown: false,
            IsRecentHighBreakout: false,
            IsRecentLowBreakdown: false,
            IsVolatilityContraction: false,
            IsPriceAboveEma20: latest.Ema20 is not null && latest.CurrentPrice >= latest.Ema20.Value,
            IsPriceAboveEma50: latest.Ema50 is not null && latest.CurrentPrice >= latest.Ema50.Value,
            IsEma20AboveEma50: latest.Ema20 is not null && latest.Ema50 is not null && latest.Ema20.Value >= latest.Ema50.Value,
            VwapExtensionAtr: latest.Vwap is not null && latest.Atr is > 0m
                ? (latest.CurrentPrice - latest.Vwap.Value) / latest.Atr.Value
                : null,
            IsAboveBollingerMiddle: latest.BollingerMiddle is not null && latest.CurrentPrice >= latest.BollingerMiddle.Value,
            IsMacdHistogramPositive: latest.MacdHistogram is > 0m,
            IsMacdNotBearish: latest.MacdHistogram is null or >= 0m,
            IsPriceAboveEma10: latest.Ema10 is not null && latest.CurrentPrice >= latest.Ema10.Value,
            IsEma10AboveEma20: latest.Ema10 is not null && latest.Ema20 is not null && latest.Ema10.Value >= latest.Ema20.Value,
            SessionRelativeVolume: latest.SessionRelativeVolume,
            SlotRelativeVolume: latest.SlotRelativeVolume,
            Catalyst: latest.Catalyst);

        return new PreparedEntryExecution(signal, signal);
    }

    private static string NormalizeEntryMode(string? entryMode)
    {
        if (String.IsNullOrWhiteSpace(entryMode))
        {
            return "validate_strategy";
        }

        return entryMode.Trim().Equals("immediate_paper", StringComparison.OrdinalIgnoreCase)
            ? "immediate_paper"
            : "validate_strategy";
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
        return missing.Count == 0 ? null : $"signal_missing_indicators ({string.Join(", ", missing)})";
    }

    internal string? GetLongEntryGateRejection(
        StrategyDefinition strategy,
        IndicatorSnapshot snapshot,
        TradeSignal signal)
    {
        var relativeVolume = TradingFlow.Engine.Strategies.StrategyDecisionBrain.ResolveEntryRelativeVolume(strategy, snapshot);
        if (relativeVolume is null)
        {
            return $"entry_relative_volume_unavailable (Source: {strategy.EntryRules.MinVolumeSpikeSource})";
        }

        return strategyEvaluator.GetLongEntryRejection(strategy, signal, relativeVolume.Value);
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

    private static TradeSignal? ResolveExecutionOrderSignal(
        StrategyDefinition strategy,
        TradeSignal signal,
        IReadOnlyDictionary<string, IReadOnlyList<IndicatorSnapshot>> snapshotsByTimeframe)
    {
        if (strategy.Execution.Timeframe.Equals(strategy.Timeframe, StringComparison.OrdinalIgnoreCase))
        {
            return signal;
        }

        if (!snapshotsByTimeframe.TryGetValue(strategy.Execution.Timeframe, out var executionSnapshots) ||
            executionSnapshots.Count == 0)
        {
            return null;
        }

        var orderedSnapshots = executionSnapshots.OrderBy(snapshot => snapshot.Timestamp).ToArray();
        var signalCloseTimestamp = signal.Timestamp.Add(TimeframeParser.Parse(strategy.Timeframe));
        var executionSnapshot = orderedSnapshots.FirstOrDefault(snapshot => snapshot.Timestamp >= signalCloseTimestamp)
            ?? orderedSnapshots.LastOrDefault(snapshot => snapshot.Timestamp >= signal.Timestamp);

        if (executionSnapshot is null)
        {
            return null;
        }

        return signal with
        {
            Timestamp = executionSnapshot.Timestamp,
            Timeframe = executionSnapshot.Timeframe,
            CurrentPrice = executionSnapshot.CurrentPrice,
            CurrentVolume = executionSnapshot.CurrentVolume,
            CurrentRsi = executionSnapshot.Rsi ?? signal.CurrentRsi,
            CurrentAtr = signal.CurrentAtr > 0m
                ? signal.CurrentAtr
                : executionSnapshot.Atr ?? signal.CurrentAtr
        };
    }

    private static int FindFirstBarIndexAtOrAfter(IReadOnlyList<OhlcvBar> bars, DateTimeOffset timestamp)
    {
        for (var index = 0; index < bars.Count; index++)
        {
            if (bars[index].Timestamp >= timestamp)
            {
                return index;
            }
        }

        return bars.Count;
    }

}
