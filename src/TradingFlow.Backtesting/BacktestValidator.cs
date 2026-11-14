using TradingFlow.Domain.Backtesting;
using TradingFlow.Engine.Indicators;
using TradingFlow.Domain.Market;

namespace TradingFlow.Backtesting;

public sealed class BacktestValidator
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    /// <summary>Summarizes the run using the evaluation window already resolved for its prepared bars.</summary>
    public BacktestValidationReport Validate(
        BacktestRunConfig config,
        IReadOnlyCollection<OhlcvBar> allBars,
        PreparedBacktestMarket market,
        IReadOnlyCollection<StrategyBacktestResult> strategyResults,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? sessions)
    {
        return new BacktestValidationReport(
            BuildOutOfSample(config, strategyResults),
            BuildWalkForward(config, strategyResults),
            PriceOnlyBenchmark.Build(config.Validation.Benchmark, market.BenchmarkBars,
                market.EvaluationStart, market.WindowEnd, sessions, strategyResults, market.SourceProvenance),
            BuildDataQuality(config, allBars),
            BuildBiasRisk(config, market.PriceBasis ?? MarketPriceBasis.FromBars(
                market.MarketState.TickerStates.Values.SelectMany(state =>
                    state.BarsByTimeframe.Values.SelectMany(bars => bars)), market.SourceProvenance)));
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

    // Report the symbols' resolved source and the actual price labels on the prepared observations.
    internal static BiasRiskValidation BuildBiasRisk(BacktestRunConfig config, MarketPriceBasis prices)
    {
        var universe = config.Universe;
        var reporting = config.Validation.BiasRisk;
        var researchScreen = reporting.ResolvedScreenManifestSha256 is not null;
        var historicalScreen = universe?.IsHistoricalScreener == true;
        var resolvedSnapshot = universe is { IsResolvedSnapshot: true } && !String.IsNullOrWhiteSpace(universe.ResolvedSource);
        var source = researchScreen ? "research_screen" : historicalScreen ? HistoricalSource(universe!) :
            resolvedSnapshot ? universe!.ResolvedSource : "static_config";
        var name = researchScreen ? reporting.ResolvedScreenName :
            resolvedSnapshot && source.Contains("wishlist", StringComparison.OrdinalIgnoreCase) ? universe!.WishlistName : null;
        var method = researchScreen ? "published_daily_membership" : universe?.DailyRanking is not null ? "daily_ranking" :
            universe?.DailyEligibility is not null ? "daily_eligibility" : null;
        var asOf = historicalScreen ? reporting.UniverseAsOfDate : resolvedSnapshot && !researchScreen && universe!.ResolvedAtUtc is { } observed
            ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(observed, NewYork).DateTime) : (DateOnly?)null;
        var staticSource = source == "static_config" || source.Contains("wishlist", StringComparison.OrdinalIgnoreCase);
        var currentScreener = source.Contains("finviz", StringComparison.OrdinalIgnoreCase);
        var missingAsOf = !researchScreen && asOf is null;

        var warnings = new List<string>();
        if (staticSource)
            warnings.Add("The configured or saved symbol list can introduce survivorship bias without historical membership evidence.");
        if (currentScreener)
            warnings.Add("Finviz supplies a current screener export. Archived point-in-time membership is required for promotion.");
        if (researchScreen)
            warnings.Add("The published screen supplies dated membership. Its manifest alone does not establish historical issuer and delisting coverage.");
        if (missingAsOf)
            warnings.Add("No observed date is recorded for the selected universe.");
        if (prices.HasRawDaily)
            warnings.Add("The inputs include raw daily bars. Splits or dividends can affect price comparisons.");
        if (prices.HasRawSubDaily)
            warnings.Add("The inputs include raw sub-daily bars. Splits or dividends can affect price comparisons.");
        if (prices.HasUnknown)
            warnings.Add("The price adjustment basis is unavailable for some or all prepared bars.");

        return new(source, asOf, prices.Policy, staticSource || currentScreener || missingAsOf || researchScreen,
            prices.HasRawDaily || prices.HasRawSubDaily || prices.HasUnknown, warnings, name,
            reporting.ResolvedScreenManifestSha256, method);
    }

    // Describe the actual candidate-pool source, including the frequency of membership changes.
    private static string HistoricalSource(UniverseConfig universe)
    {
        var source = UniverseConfig.HistoricalScreenerMode;
        if (universe.UsesFinvizScreen) source += universe.MergesCuratedAndFinviz ? "/curated+finviz" : "/finviz";
        if (universe.IsPerDayRescreen) source += "/per_day";
        return source;
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
