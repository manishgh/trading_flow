using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;

namespace TradingFlow.Backtesting;

public sealed class BacktestValidator
{
    public BacktestValidationReport Validate(
        BacktestRunConfig config,
        IReadOnlyCollection<OhlcvBar> allBars,
        IReadOnlyCollection<OhlcvBar> benchmarkBars,
        IReadOnlyCollection<StrategyBacktestResult> strategyResults)
    {
        return new BacktestValidationReport(
            BuildOutOfSample(config, strategyResults),
            BuildWalkForward(config, strategyResults),
            BuildBenchmark(config, benchmarkBars, strategyResults),
            BuildDataQuality(config, allBars),
            BuildBiasRisk(config));
    }

    private static OutOfSampleValidation BuildOutOfSample(
        BacktestRunConfig config,
        IReadOnlyCollection<StrategyBacktestResult> strategyResults)
    {
        if (!config.Validation.OutOfSample.Enabled)
        {
            return new OutOfSampleValidation(false, 0, null, Array.Empty<StrategyPartitionResult>());
        }

        var allTrades = strategyResults.SelectMany(x => x.CompletedTrades).OrderBy(x => x.EntryTimestamp).ToArray();
        if (allTrades.Length == 0)
        {
            return new OutOfSampleValidation(true, config.Validation.OutOfSample.Percent, null, Array.Empty<StrategyPartitionResult>());
        }

        var minTime = allTrades.First().EntryTimestamp;
        var maxTime = allTrades.Last().EntryTimestamp;
        var splitOffset = TimeSpan.FromTicks((long)((maxTime - minTime).Ticks * (1m - (config.Validation.OutOfSample.Percent / 100m))));
        var splitTimestamp = minTime.Add(splitOffset);

        var partitions = strategyResults.Select(strategy =>
            new StrategyPartitionResult(
                strategy.StrategyId,
                strategy.StrategyName,
                strategy.Source,
                SummarizeTrades(strategy.CompletedTrades.Where(x => x.EntryTimestamp < splitTimestamp).ToArray(), strategy.StartingCapital),
                SummarizeTrades(strategy.CompletedTrades.Where(x => x.EntryTimestamp >= splitTimestamp).ToArray(), strategy.StartingCapital)))
            .ToArray();

        return new OutOfSampleValidation(true, config.Validation.OutOfSample.Percent, splitTimestamp, partitions);
    }

    private static IReadOnlyList<WalkForwardWindowResult> BuildWalkForward(
        BacktestRunConfig config,
        IReadOnlyCollection<StrategyBacktestResult> strategyResults)
    {
        if (!config.Validation.WalkForward.Enabled)
        {
            return Array.Empty<WalkForwardWindowResult>();
        }

        var allTrades = strategyResults.SelectMany(x => x.CompletedTrades).OrderBy(x => x.EntryTimestamp).ToArray();
        if (allTrades.Length == 0)
        {
            return Array.Empty<WalkForwardWindowResult>();
        }

        var windowDays = Math.Max(1, config.Validation.WalkForward.WindowDays);
        var stepDays = Math.Max(1, config.Validation.WalkForward.StepDays);
        var currentStart = allTrades.First().EntryTimestamp;
        var finalTimestamp = allTrades.Last().EntryTimestamp;
        var windows = new List<WalkForwardWindowResult>();

        while (currentStart <= finalTimestamp)
        {
            var currentEnd = currentStart.AddDays(windowDays);
            var strategyWindows = strategyResults.Select(strategy =>
            {
                var trades = strategy.CompletedTrades
                    .Where(x => x.EntryTimestamp >= currentStart && x.EntryTimestamp < currentEnd)
                    .ToArray();
                var summary = SummarizeTrades(trades, strategy.StartingCapital);
                return new StrategyWindowResult(
                    strategy.StrategyId,
                    strategy.StrategyName,
                    strategy.Source,
                    summary.TradeCount,
                    summary.NetProfit,
                    summary.TotalReturnPct,
                    summary.MaxDrawdownPct);
            }).ToArray();

            windows.Add(new WalkForwardWindowResult(currentStart, currentEnd, strategyWindows));
            currentStart = currentStart.AddDays(stepDays);
        }

        return windows;
    }

    private static BenchmarkValidation BuildBenchmark(
        BacktestRunConfig config,
        IReadOnlyCollection<OhlcvBar> benchmarkBars,
        IReadOnlyCollection<StrategyBacktestResult> strategyResults)
    {
        if (!config.Validation.Benchmark.Enabled)
        {
            return new BenchmarkValidation(false, String.Empty, null, Array.Empty<StrategyBenchmarkComparison>());
        }

        var orderedBenchmarkBars = benchmarkBars.OrderBy(x => x.Timestamp).ToArray();
        decimal? benchmarkReturnPct = null;
        if (orderedBenchmarkBars.Length >= 2 && orderedBenchmarkBars[0].Close > 0)
        {
            benchmarkReturnPct = ((orderedBenchmarkBars[^1].Close - orderedBenchmarkBars[0].Close) / orderedBenchmarkBars[0].Close) * 100m;
            benchmarkReturnPct = Decimal.Round(benchmarkReturnPct.Value, 4);
        }

        var comparisons = strategyResults.Select(strategy =>
            new StrategyBenchmarkComparison(
                strategy.StrategyId,
                strategy.StrategyName,
                strategy.Source,
                strategy.TotalReturnPct,
                benchmarkReturnPct is null ? null : Decimal.Round(strategy.TotalReturnPct - benchmarkReturnPct.Value, 4)))
            .ToArray();

        return new BenchmarkValidation(true, config.Validation.Benchmark.Ticker, benchmarkReturnPct, comparisons);
    }

    private static DataQualityValidation BuildDataQuality(BacktestRunConfig config, IReadOnlyCollection<OhlcvBar> allBars)
    {
        if (!config.Validation.DataQuality.Enabled)
        {
            return new DataQualityValidation(allBars.Count, 0, 0, 0, 0, Array.Empty<string>());
        }

        var duplicateBars = allBars
            .GroupBy(x => (x.Ticker, x.Timeframe, x.Timestamp))
            .Where(x => x.Count() > 1)
            .Sum(x => x.Count() - 1);
        var invalidOhlc = allBars.Count(x =>
            x.Open <= 0 ||
            x.High <= 0 ||
            x.Low <= 0 ||
            x.Close <= 0 ||
            x.High < x.Low ||
            x.Open > x.High ||
            x.Open < x.Low ||
            x.Close > x.High ||
            x.Close < x.Low);
        var negativeVolume = allBars.Count(x => x.Volume < 0);
        var zeroVolume = allBars.Count(x => x.Volume == 0);
        var zeroVolumePct = allBars.Count == 0 ? 0 : (zeroVolume / (decimal)allBars.Count) * 100m;
        var warnings = new List<string>();

        if (duplicateBars > config.Validation.DataQuality.MaxDuplicateBars)
        {
            warnings.Add($"Duplicate bar count {duplicateBars} exceeds limit {config.Validation.DataQuality.MaxDuplicateBars}.");
        }

        if (invalidOhlc > config.Validation.DataQuality.MaxInvalidOhlcBars)
        {
            warnings.Add($"Invalid OHLC bar count {invalidOhlc} exceeds limit {config.Validation.DataQuality.MaxInvalidOhlcBars}.");
        }

        if (zeroVolumePct > config.Validation.DataQuality.MaxZeroVolumePct)
        {
            warnings.Add($"Zero-volume bar percentage {Decimal.Round(zeroVolumePct, 4)} exceeds limit {config.Validation.DataQuality.MaxZeroVolumePct}.");
        }

        return new DataQualityValidation(
            allBars.Count,
            duplicateBars,
            invalidOhlc,
            negativeVolume,
            zeroVolume,
            warnings);
    }

    private static BiasRiskValidation BuildBiasRisk(BacktestRunConfig config)
    {
        var warnings = new List<string>();
        var staticUniverse = config.Validation.BiasRisk.UniverseSource.Equals("static_config", StringComparison.OrdinalIgnoreCase);
        var missingAsOfDate = config.Validation.BiasRisk.UniverseAsOfDate is null;
        var rawIntradayPolicy = config.Validation.BiasRisk.PriceAdjustmentPolicy.Contains("raw", StringComparison.OrdinalIgnoreCase);

        if (staticUniverse)
        {
            warnings.Add("Universe source is static_config; this can introduce survivorship bias unless the ticker list is reconstructed as-of the backtest date.");
        }

        if (missingAsOfDate)
        {
            warnings.Add("Universe as-of date is not configured.");
        }

        if (rawIntradayPolicy)
        {
            warnings.Add("Price adjustment policy uses raw intraday bars; validate split/dividend handling before comparing long historical windows.");
        }

        return new BiasRiskValidation(
            config.Validation.BiasRisk.UniverseSource,
            config.Validation.BiasRisk.UniverseAsOfDate,
            config.Validation.BiasRisk.PriceAdjustmentPolicy,
            staticUniverse || missingAsOfDate,
            rawIntradayPolicy,
            warnings);
    }

    private static TradePartitionResult SummarizeTrades(IReadOnlyCollection<BacktestTrade> trades, decimal startingCapital)
    {
        var netProfit = trades.Sum(x => x.NetProfit);
        var returnPct = startingCapital == 0 ? 0 : (netProfit / startingCapital) * 100m;
        var winRate = trades.Count == 0 ? 0 : (trades.Count(x => x.NetProfit > 0) / (decimal)trades.Count) * 100m;
        return new TradePartitionResult(
            trades.Count,
            Decimal.Round(netProfit, 4),
            Decimal.Round(returnPct, 4),
            Decimal.Round(winRate, 4),
            Decimal.Round(CalculateMaxDrawdown(startingCapital, trades), 4));
    }

    private static decimal CalculateMaxDrawdown(decimal startingCapital, IReadOnlyCollection<BacktestTrade> completedTrades)
    {
        var equity = startingCapital;
        var highWaterMark = startingCapital;
        var maxDrawdown = 0m;

        foreach (var trade in completedTrades.OrderBy(x => x.ExitTimestamp))
        {
            equity += trade.NetProfit;
            highWaterMark = Math.Max(highWaterMark, equity);
            if (highWaterMark > 0)
            {
                maxDrawdown = Math.Max(maxDrawdown, ((highWaterMark - equity) / highWaterMark) * 100m);
            }
        }

        return maxDrawdown;
    }
}
