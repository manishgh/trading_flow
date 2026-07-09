using System.Text.Json;
using TradingFlow.Backtesting;
using TradingFlow.Backtesting.Optimization;
using TradingFlow.Backtesting.Research;
using TradingFlow.Backtesting.StrategyEvaluation;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Storage;

var serializerOptions = new JsonSerializerOptions
{
    WriteIndented = true
};

if (args.Length > 0 && args[0].Equals("optimize", StringComparison.OrdinalIgnoreCase))
{
    var optConfigPath = args.Length > 1
        ? args[1]
        : throw new ArgumentException("Optimization config path required.");

    var reader = new SimpleYamlReader();
    var runner = new BacktestRunner(reader);
    var optimizer = new StrategyOptimizer(reader, runner);

    var progress = new Progress<BacktestProgress>(update =>
    {
        Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} {update.Stage}: {update.Message}");
    });
    var optimizationProgress = new Progress<TradingFlow.Domain.Optimization.OptimizationProgress>(update =>
    {
        if (update.Kind.Equals("completed", StringComparison.OrdinalIgnoreCase) && update.CompletedRun is not null)
        {
            Console.WriteLine(
                $"{DateTimeOffset.Now:HH:mm:ss} optimization_result: {update.CurrentPermutation}/{update.TotalPermutations} " +
                $"{update.StrategyName} return={update.CompletedRun.TotalReturnPct:0.00}% daily={update.CompletedRun.AverageDailyReturnPct:0.0000}% net={update.CompletedRun.NetProfit:0.00} " +
                $"drawdown={update.CompletedRun.MaxDrawdownPct:0.00}%");
        }
    });

    var optResult = await optimizer.OptimizeAsync(optConfigPath, CancellationToken.None, progress, optimizationProgress);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        optResult.RunName,
        optResult.TopRuns.Count,
        TopPermutations = optResult.TopRuns.Select(r => new
        {
            r.Rank,
            r.MetricValue,
            r.TotalReturnPct,
            r.AverageDailyReturnPct,
            r.NetProfit,
            r.MaxDrawdownPct,
            Trades = r.WinningTradeCount + r.LosingTradeCount,
            WinRate = r.WinningTradeCount + r.LosingTradeCount == 0
                ? 0
                : Math.Round((decimal)r.WinningTradeCount / (r.WinningTradeCount + r.LosingTradeCount) * 100, 2),
            r.ParameterValues
        })
    }, serializerOptions));
    return;
}

if (args.Length > 0 && args[0].Equals("warm-candles", StringComparison.OrdinalIgnoreCase))
{
    var configPathForWarmup = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)
        ? args[1]
        : Path.Combine("configs", "backtest", "finviz-reddit-ross-gapgo-bullflag-8-180d-10k-api-v2-confirmed-entry.yaml");
    var reader = new SimpleYamlReader();
    var run = reader.ReadBacktestRun(configPathForWarmup);
    var lookbackDays = ParseIntOption(args, "--days") ?? ResolveWarmupDataDays(run);
    var outputRoot = ParseStringOption(args, "--output") ?? ResolveWarmOutputRoot(run.NormalizedRoot, lookbackDays);
    var end = ParseDateOption(args, "--end") ?? DateTimeOffset.UtcNow;

    var warmer = new TradingFlow.Data.Csv.RollingCandleCacheWarmer();
    var result = await warmer.WarmAsync(
        new TradingFlow.Alpaca.AlpacaMarketDataProvider(
            new HttpClient(),
            ResolveAlpacaOptions(run)),
        new TradingFlow.Data.Csv.RollingCandleWarmRequest(
            run.Tickers,
            run.Intervals,
            outputRoot,
            lookbackDays,
            end),
        CancellationToken.None);

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        result.OutputRoot,
        result.Start,
        result.End,
        result.LookbackDays,
        result.TickerCount,
        result.TimeframeCount,
        result.BarCount,
        Files = result.Files.Select(file => new
        {
            file.Ticker,
            file.Timeframe,
            file.BarCount,
            file.FirstTimestamp,
            file.LastTimestamp,
            file.Path,
            file.CalculatedPath
        })
    }, serializerOptions));
    return;
}

if (args.Length > 0 && args[0].Equals("warm-catalysts", StringComparison.OrdinalIgnoreCase))
{
    var configPathForWarmup = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)
        ? args[1]
        : Path.Combine("configs", "backtest", "finviz-reddit-ross-gapgo-bullflag-8-180d-10k-api-v2-confirmed-entry.yaml");
    var reader = new SimpleYamlReader();
    var run = reader.ReadBacktestRun(configPathForWarmup);
    if (!run.News.Enabled)
    {
        throw new InvalidOperationException("news.enabled must be true to warm catalyst cache.");
    }

    var lookbackDays = ParseIntOption(args, "--days") ?? ResolveWarmupDataDays(run);
    var outputRoot = ParseStringOption(args, "--output") ?? ResolveWarmOutputRoot(run.NormalizedRoot, lookbackDays);
    var end = ParseDateOption(args, "--end") ?? DateTimeOffset.UtcNow;
    var start = end.AddDays(-lookbackDays);
    var tickerTimeoutSeconds = ParseIntOption(args, "--ticker-timeout-seconds") ?? run.Engine.TickerTimeoutSeconds;
    var refresh = ParseFlag(args, "--refresh");
    var rawProvider = CreateRawNewsProvider(run) ??
        throw new InvalidOperationException($"No news provider was created for {run.News.ProviderName}.");
    var provider = new TradingFlow.Data.Catalysts.CachedCatalystProvider(
        rawProvider,
        outputRoot,
        refresh ? "refresh" : run.CachePolicy);

    var warmed = new List<CatalystWarmTickerResult>();
    foreach (var ticker in run.Tickers)
    {
        using var tickerTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, tickerTimeoutSeconds)));
        try
        {
            var catalysts = await provider.GetCatalystsAsync(ticker, start, end, tickerTimeout.Token);
            Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} warm_catalysts: {ticker} cached {catalysts.Count} catalyst(s).");
            warmed.Add(new CatalystWarmTickerResult(ticker, true, catalysts.Count, null));
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} warm_catalysts: {ticker} timed out after {tickerTimeoutSeconds}s.");
            warmed.Add(new CatalystWarmTickerResult(ticker, false, 0, $"Timed out after {tickerTimeoutSeconds}s."));
        }
        catch (Exception exception)
        {
            Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} warm_catalysts: {ticker} failed: {exception.Message}");
            warmed.Add(new CatalystWarmTickerResult(ticker, false, 0, exception.Message));
        }
    }

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        OutputRoot = Path.GetFullPath(outputRoot),
        Provider = rawProvider.ProviderName,
        Start = start,
        End = end,
        LookbackDays = lookbackDays,
        TickerCount = run.Tickers.Count,
        Succeeded = warmed.Count(x => x.Succeeded),
        Failed = warmed.Count(x => !x.Succeeded),
        Tickers = warmed
    }, serializerOptions));
    return;
}

if (args.Length > 0 && args[0].Equals("catalyst-trend", StringComparison.OrdinalIgnoreCase))
{
    var ticker = (ParseStringOption(args, "--ticker") ?? "POET").Trim().ToUpperInvariant();
    var days = ParseIntOption(args, "--days") ?? 30;
    var candleTimeframe = ParseStringOption(args, "--timeframe") ?? "5m";
    var dailyTimeframe = ParseStringOption(args, "--daily-timeframe") ?? "1d";
    var end = ParseDateOption(args, "--end") ?? DateTimeOffset.UtcNow;
    var start = end.AddDays(-days);
    var outputPath = ParseStringOption(args, "--output") ??
        Path.Combine(
            "data",
            "research",
            "catalysts",
            $"{ticker.ToLowerInvariant()}-catalyst-trend-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json");

    var report = await BuildCatalystTrendReportAsync(
        ticker,
        start,
        end,
        candleTimeframe,
        dailyTimeframe,
        CancellationToken.None);

    var json = JsonSerializer.Serialize(report, serializerOptions);
    await AtomicFileArtifactWriter.Instance.WriteTextAsync(outputPath, json, CancellationToken.None);
    Console.WriteLine(json);
    Console.WriteLine($"ReportPath={Path.GetFullPath(outputPath)}");
    return;
}

if (args.Length > 0 && args[0].Equals("catalyst-event-study", StringComparison.OrdinalIgnoreCase))
{
    var eventStudyConfigPath = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)
        ? args[1]
        : throw new ArgumentException("Backtest or research config path required.");
    var reader = new SimpleYamlReader();
    var run = reader.ReadBacktestRun(eventStudyConfigPath);
    var lookbackDays = ParseIntOption(args, "--days") ?? run.TimeWindow.LookbackDays;
    var candleTimeframe = ParseStringOption(args, "--timeframe") ?? run.Intervals.FirstOrDefault() ?? "5m";
    var end = ParseDateOption(args, "--end") ?? DateTimeOffset.UtcNow;
    var start = end.AddDays(-lookbackDays);
    var tickers = ResolveCsvTickers(ParseStringOption(args, "--tickers"), run.Tickers);
    var outputPath = ParseStringOption(args, "--output") ??
        Path.Combine(
            "data",
            "research",
            "catalysts",
            $"event-study-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json");

    var report = await BuildCatalystEventStudyReportAsync(
        run,
        tickers,
        start,
        end,
        candleTimeframe,
        CancellationToken.None);

    var json = JsonSerializer.Serialize(report, serializerOptions);
    await AtomicFileArtifactWriter.Instance.WriteTextAsync(outputPath, json, CancellationToken.None);
    if (!ParseFlag(args, "--quiet"))
    {
        Console.WriteLine(json);
    }

    Console.WriteLine($"Observations={report.Observations.Count} Buckets={report.Buckets.Count}");
    Console.WriteLine($"ReportPath={Path.GetFullPath(outputPath)}");
    return;
}
if (args.Length > 0 && args[0].Equals("analyze-swing", StringComparison.OrdinalIgnoreCase))
{
    var resultPath = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)
        ? args[1]
        : throw new ArgumentException("Backtest result JSON path required.");
    var result = JsonSerializer.Deserialize<TradingFlow.Domain.Backtesting.BacktestResult>(
        File.ReadAllText(resultPath),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
        throw new InvalidOperationException($"Could not read backtest result from {resultPath}.");
    var strategyName = ParseStringOption(args, "--strategy") ?? result.Winner?.StrategyName ??
        result.StrategyResults.OrderByDescending(x => x.TotalReturnPct).FirstOrDefault()?.StrategyName ??
        throw new InvalidOperationException("No strategy was available in the backtest result.");
    var candlesRoot = ParseStringOption(args, "--candles-root") ??
        Path.Combine("data", "backtest", "normalized", "240d");
    var minimumMovePct = ParseDecimalOption(args, "--min-move-pct") ?? 10m;
    var maximumHoldingBars = ParseIntOption(args, "--max-holding-bars") ?? 30;
    var maxOpportunities = ParseIntOption(args, "--max-opportunities") ?? 5;
    var tickers = ResolveSwingAnalysisTickers(result, strategyName, ParseStringOption(args, "--tickers"));
    var dailyBars = tickers
        .Select(ticker => new
        {
            Ticker = ticker,
            Bars = LoadDailyBars(candlesRoot, ticker)
        })
        .Where(x => x.Bars.Count > 0)
        .ToDictionary(x => x.Ticker, x => (IReadOnlyList<TradingFlow.Domain.Market.OhlcvBar>)x.Bars, StringComparer.OrdinalIgnoreCase);
    if (dailyBars.Count == 0)
    {
        throw new InvalidOperationException($"No daily candle files were found under {Path.GetFullPath(candlesRoot)}.");
    }

    var report = new SwingResearchAnalyzer().Analyze(
        result,
        strategyName,
        dailyBars,
        new TradingFlow.Domain.Backtesting.SwingResearchOptions(
            minimumMovePct,
            maximumHoldingBars,
            maxOpportunities));
    var outputPath = ParseStringOption(args, "--output") ??
        Path.Combine(
            "data",
            "research",
            "swing",
            $"{SanitizeFileName(result.RunName)}-{SanitizeFileName(strategyName)}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json");
    var json = JsonSerializer.Serialize(report, serializerOptions);
    await AtomicFileArtifactWriter.Instance.WriteTextAsync(outputPath, json, CancellationToken.None);
    Console.WriteLine(json);
    Console.WriteLine($"ReportPath={Path.GetFullPath(outputPath)}");
    return;
}

if (args.Length > 0 && args[0].Equals("promotion-check", StringComparison.OrdinalIgnoreCase))
{
    var resultPath = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)
        ? args[1]
        : throw new ArgumentException("Backtest result JSON path required.");
    var result = JsonSerializer.Deserialize<TradingFlow.Domain.Backtesting.BacktestResult>(
        File.ReadAllText(resultPath),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
        throw new InvalidOperationException($"Could not read backtest result from {resultPath}.");

    Console.WriteLine($"Promotion check — {result.RunName} ({result.StrategyResults.Count} strategy result(s))");
    Console.WriteLine(new string('=', 72));
    foreach (var strategyResult in result.StrategyResults)
    {
        var assessment = TradingFlow.Domain.Backtesting.PromotionEvaluator.Evaluate(strategyResult, result.Validation);
        Console.WriteLine();
        Console.WriteLine($"[{(assessment.Eligible ? "ELIGIBLE" : "REJECTED")}] {assessment.StrategyName}");
        Console.WriteLine(
            $"  return {strategyResult.TotalReturnPct:F2}%  maxDD {strategyResult.MaxDrawdownPct:F2}%  " +
            $"accepted {strategyResult.AcceptedTradeCount}  wins {strategyResult.WinningTradeCount}  losses {strategyResult.LosingTradeCount}");
        foreach (var pass in assessment.PassedChecks)
        {
            Console.WriteLine($"    pass: {pass}");
        }

        foreach (var fail in assessment.FailedChecks)
        {
            Console.WriteLine($"    FAIL: {fail}");
        }
    }

    return;
}

if (args.Length > 0 && args[0].Equals("evaluate-entry", StringComparison.OrdinalIgnoreCase))
{
    var entryConfigPath = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)
        ? args[1]
        : throw new ArgumentException("Run config path required.");
    var strategyPath = ParseStringOption(args, "--strategy")
        ?? throw new ArgumentException("--strategy path is required.");
    var tickersCsv = ParseStringOption(args, "--tickers");
    var ticker = ParseStringOption(args, "--ticker");
    var end = ParseDateOption(args, "--end") ?? DateTimeOffset.UtcNow;
    var lookbackDays = ParseIntOption(args, "--days") ?? 0;

    var reader = new SimpleYamlReader();
    var entryRunConfig = reader.ReadBacktestRun(entryConfigPath);
    var strategy = reader.ReadStrategy(strategyPath);
    var requestedTickers = !String.IsNullOrWhiteSpace(ticker)
        ? new[] { ticker.Trim().ToUpperInvariant() }
        : tickersCsv?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToUpperInvariant())
            .ToArray();

    var evaluationEngine = new StrategyEvaluationEngine();
    var response = await evaluationEngine.EvaluateAsync(
        new StrategyEvaluationRequest(
            entryConfigPath,
            strategyPath,
            requestedTickers,
            lookbackDays <= 0 ? Math.Max(entryRunConfig.TimeWindow.LookbackDays, entryRunConfig.TimeWindow.WarmupLookbackDays) : lookbackDays,
            null,
            end),
        CreateProvider(entryRunConfig),
        entryRunConfig,
        strategy,
        strategyPath,
        CancellationToken.None);

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        response.RunName,
        response.StrategyId,
        response.StrategyName,
        response.StrategyPath,
        response.Start,
        response.End,
        Results = response.Results.Select(result => new
        {
            result.Ticker,
            result.Timeframe,
            result.Decision,
            result.Reason,
            result.Timestamp,
            result.Close,
            result.Rsi,
            result.Atr,
            result.Volume,
            result.SlotAverageVolume,
            result.RelativeVolume,
            result.Vwap,
            result.Ema20,
            result.Ema50,
            result.MacdHistogram,
            result.Signal
        }),
        response.Profiler
    }, serializerOptions));
    return;
}

var configPath = ResolveRunConfigPath(args);

var readerInstance = new SimpleYamlReader();
var runConfig = readerInstance.ReadBacktestRun(configPath);
if (runConfig.Mode.Equals("paper", StringComparison.OrdinalIgnoreCase) ||
    runConfig.Mode.Equals("live", StringComparison.OrdinalIgnoreCase))
{
    var runner = new LiveRunner(
        CreateProvider(runConfig),
        CreateNewsProvider(runConfig),
        new TradingFlow.Engine.Execution.PseudoBroker(),
        null,
        null,
        null,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<LiveRunner>.Instance);

    var strategies = runConfig.Strategies.Select(readerInstance.ReadStrategy).ToArray();
    await runner.RunAsync(runConfig, strategies, CancellationToken.None);
    Console.WriteLine("Live runner execution finished.");
}
else
{
    var runner = new BacktestRunner(readerInstance);
    var result = await runner.RunAsync(configPath, CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        result.RunName,
        result.ResultPath,
        result.StartingCapital,
        result.EndingCapital,
        result.NetProfit,
        result.TotalReturnPct,
        result.AverageDailyReturnPct,
        result.TradingDayCount,
        result.MaxDrawdownPct,
        Winner = result.Winner is null
            ? null
            : new
            {
                result.Winner.StrategyName,
                result.Winner.TotalReturnPct,
                result.Winner.AverageDailyReturnPct,
                result.Winner.NetProfit,
                result.Winner.MaxDrawdownPct,
                result.Winner.AcceptedTradeCount,
                result.Winner.RejectedTradeCount
            },
        Strategies = result.StrategyResults.Select(strategy => new
        {
            strategy.StrategyName,
            strategy.TotalReturnPct,
            strategy.AverageDailyReturnPct,
            strategy.TradingDayCount,
            strategy.NetProfit,
            strategy.MaxDrawdownPct,
            strategy.CandidateTradeCount,
            strategy.AcceptedTradeCount,
            strategy.RejectedTradeCount,
            strategy.WinningTradeCount,
            strategy.LosingTradeCount
        }),
        Tickers = new
        {
            Succeeded = result.TickerResults.Count(ticker => ticker.Succeeded),
            Failed = result.TickerResults.Count(ticker => !ticker.Succeeded)
        }
    }, serializerOptions));
}

static string ResolveRunConfigPath(string[] args)
{
    if (args.Length == 0)
    {
        return Path.Combine("configs", "backtest", "finviz-reddit-ross-gapgo-bullflag-8-180d-10k-api-v2-confirmed-entry.yaml");
    }

    if (args[0].Equals("backtest", StringComparison.OrdinalIgnoreCase) ||
        args[0].Equals("run", StringComparison.OrdinalIgnoreCase))
    {
        return args.Length > 1
            ? args[1]
            : Path.Combine("configs", "backtest", "finviz-reddit-ross-gapgo-bullflag-8-180d-10k-api-v2-confirmed-entry.yaml");
    }

    return args[0];
}

static TradingFlow.Engine.Abstractions.ICatalystProvider? CreateNewsProvider(TradingFlow.Domain.Backtesting.BacktestRunConfig run)
{
    if (!run.News.Enabled)
    {
        return null;
    }

    return CreateRawNewsProvider(run);
}

static TradingFlow.Engine.Abstractions.ICatalystProvider? CreateRawNewsProvider(TradingFlow.Domain.Backtesting.BacktestRunConfig run)
{
    return run.News.ProviderName.ToLowerInvariant() switch
    {
        "alpaca" => new TradingFlow.Alpaca.AlpacaNewsProvider(
            new HttpClient(),
            ResolveAlpacaOptions(run),
            sentimentAnalyzer: CreateSentimentAnalyzer(run.News.SentimentTimeoutSeconds),
            maxArticlesPerTicker: run.News.MaxArticlesPerTicker),
        "finviz" => new TradingFlow.Finviz.FinvizNewsProvider(
            new TradingFlow.Finviz.FinvizClient(
                new HttpClient(),
                TradingFlow.Finviz.FinvizOptions.CreateDefault() with
                {
                    AuthToken = Environment.GetEnvironmentVariable("FINVIZ_API_KEY") ?? ""
                })),
        "none" => null,
        _ => throw new NotSupportedException($"Unsupported news provider: {run.News.ProviderName}")
    };
}

static TradingFlow.Engine.Abstractions.ISentimentAnalyzer CreateSentimentAnalyzer(int? timeoutSeconds = null)
{
    var endpoint = Environment.GetEnvironmentVariable("FINBERT_SENTIMENT_URL");
    if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
    {
        return new TradingFlow.Alpaca.FinbertHttpSentimentAnalyzer(
            new HttpClient(),
            uri,
            requestTimeout: TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds ?? 3)));
    }

    return new TradingFlow.Alpaca.VaderSentimentAnalyzer();
}

static async Task<CatalystEventStudyReport> BuildCatalystEventStudyReportAsync(
    TradingFlow.Domain.Backtesting.BacktestRunConfig run,
    IReadOnlyList<string> tickers,
    DateTimeOffset start,
    DateTimeOffset end,
    string candleTimeframe,
    CancellationToken cancellationToken)
{
    var marketProvider = CreateProvider(run);
    var catalystProvider = CreateRawNewsProvider(run);
    var barsByTicker = tickers.ToDictionary(
        ticker => ticker,
        _ => (IReadOnlyList<TradingFlow.Domain.Market.OhlcvBar>)Array.Empty<TradingFlow.Domain.Market.OhlcvBar>(),
        StringComparer.OrdinalIgnoreCase);
    var loadedBars = new List<TradingFlow.Domain.Market.OhlcvBar>();
    await foreach (var bar in marketProvider.GetBarsAsync(tickers, [candleTimeframe], start, end, cancellationToken))
    {
        loadedBars.Add(bar);
    }

    barsByTicker = loadedBars
        .Where(x => x.Timeframe.Equals(candleTimeframe, StringComparison.OrdinalIgnoreCase))
        .GroupBy(x => x.Ticker.ToUpperInvariant(), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(
            x => x.Key,
            x => (IReadOnlyList<TradingFlow.Domain.Market.OhlcvBar>)x.OrderBy(bar => bar.Timestamp).ToArray(),
            StringComparer.OrdinalIgnoreCase);

    var catalystsByTicker = new Dictionary<string, IReadOnlyList<TradingFlow.Domain.Market.CatalystEvent>>(StringComparer.OrdinalIgnoreCase);
    if (catalystProvider is not null)
    {
        foreach (var ticker in tickers)
        {
            try
            {
                var catalysts = await catalystProvider.GetCatalystsAsync(ticker, start, end, cancellationToken);
                catalystsByTicker[ticker] = catalysts.OrderBy(x => x.Timestamp).ToArray();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} catalyst_event_study: {ticker} catalyst load failed: {exception.Message}");
                catalystsByTicker[ticker] = Array.Empty<TradingFlow.Domain.Market.CatalystEvent>();
            }
        }
    }

    var runner = new CatalystTechnicalEventStudyRunner();
    return runner.Analyze(
        barsByTicker,
        catalystsByTicker,
        start,
        end,
        candleTimeframe,
        new CatalystEventStudyOptions());
}
static async Task<object> BuildCatalystTrendReportAsync(
    string ticker,
    DateTimeOffset start,
    DateTimeOffset end,
    string candleTimeframe,
    string dailyTimeframe,
    CancellationToken cancellationToken)
{
    var options = TradingFlow.Alpaca.AlpacaOptions.CreateDefault() with
    {
        KeyId = ResolveSecret("Alpaca", "KeyId", "ALPACA_KEY_ID"),
        SecretKey = ResolveSecret("Alpaca", "SecretKey", "ALPACA_SECRET_KEY"),
        MarketDataFeed = ParseStringOption(Environment.GetCommandLineArgs(), "--feed") ?? "sip"
    };

    var provider = new TradingFlow.Alpaca.AlpacaMarketDataProvider(new HttpClient(), options);
    var newsProvider = new TradingFlow.Alpaca.AlpacaNewsProvider(
        new HttpClient(),
        options,
        sentimentAnalyzer: CreateSentimentAnalyzer());

    var bars = new List<TradingFlow.Domain.Market.OhlcvBar>();
    await foreach (var bar in provider.GetBarsAsync([ticker], [candleTimeframe, dailyTimeframe], start, end, cancellationToken))
    {
        bars.Add(bar);
    }

    var intradayBars = bars
        .Where(x => x.Timeframe.Equals(candleTimeframe, StringComparison.OrdinalIgnoreCase))
        .OrderBy(x => x.Timestamp)
        .ToArray();
    var dailyBars = bars
        .Where(x => x.Timeframe.Equals(dailyTimeframe, StringComparison.OrdinalIgnoreCase))
        .OrderBy(x => x.Timestamp)
        .ToArray();

    var indicatorEngine = new IndicatorEngine();
    var snapshots = indicatorEngine.Compute(intradayBars);
    var news = (await newsProvider.GetCatalystsAsync(ticker, start, end, cancellationToken))
        .OrderBy(x => x.Timestamp)
        .ToArray();

    var catalystMatches = news.Select(item => BuildCatalystMatch(item, intradayBars, snapshots)).ToArray();
    var oneMonthReturn = intradayBars.Length < 2
        ? (decimal?)null
        : PercentChange(intradayBars[0].Close, intradayBars[^1].Close);
    var fiveDayReturn = ReturnFromTrailingDailyBars(dailyBars, 5);
    var twentyDayReturn = ReturnFromTrailingDailyBars(dailyBars, 20);

    return new
    {
        Ticker = ticker,
        StartUtc = start,
        EndUtc = end,
        CandleTimeframe = candleTimeframe,
        DailyTimeframe = dailyTimeframe,
        CandleCount = intradayBars.Length,
        DailyCandleCount = dailyBars.Length,
        NewsCount = news.Length,
        SentimentAnalyzer = CreateSentimentAnalyzer().AnalyzerName,
        Trend = new
        {
            FirstClose = intradayBars.FirstOrDefault()?.Close,
            LastClose = intradayBars.LastOrDefault()?.Close,
            OneMonthReturnPct = oneMonthReturn,
            FiveTradingDayReturnPct = fiveDayReturn,
            TwentyTradingDayReturnPct = twentyDayReturn,
            HighestClose = intradayBars.Length == 0 ? (decimal?)null : intradayBars.Max(x => x.Close),
            LowestClose = intradayBars.Length == 0 ? (decimal?)null : intradayBars.Min(x => x.Close),
            Label = LabelTrend(oneMonthReturn, fiveDayReturn)
        },
        CatalystMatches = catalystMatches,
        Summary = new
        {
            PositiveNews = news.Count(x => x.SentimentScore >= 0.15m),
            NeutralNews = news.Count(x => x.SentimentScore > -0.15m && x.SentimentScore < 0.15m),
            NegativeNews = news.Count(x => x.SentimentScore <= -0.15m),
            AverageSentiment = news.Length == 0 ? 0m : news.Average(x => x.SentimentScore),
            PositiveCatalystsWithPositive4hFollowThrough = catalystMatches.Count(x => x.SentimentScore >= 0.15m && x.Return4hPct > 0),
            NegativeCatalystsWithNegative4hFollowThrough = catalystMatches.Count(x => x.SentimentScore <= -0.15m && x.Return4hPct < 0)
        }
    };
}

static CatalystTrendMatch BuildCatalystMatch(
    TradingFlow.Domain.Market.CatalystEvent catalyst,
    IReadOnlyList<TradingFlow.Domain.Market.OhlcvBar> bars,
    IReadOnlyList<TradingFlow.Domain.Market.IndicatorSnapshot> snapshots)
{
    var anchorIndex = -1;
    for (var i = 0; i < bars.Count; i++)
    {
        if (bars[i].Timestamp <= catalyst.Timestamp)
        {
            anchorIndex = i;
            continue;
        }

        break;
    }

    if (anchorIndex < 0 && bars.Count > 0)
    {
        anchorIndex = 0;
    }

    var anchorBar = anchorIndex >= 0 ? bars[anchorIndex] : null;
    var snapshot = anchorIndex >= 0 && anchorIndex < snapshots.Count ? snapshots[anchorIndex] : null;

    decimal? return1h = anchorBar is null ? null : ReturnAtOrAfter(bars, anchorIndex, anchorBar.Timestamp.AddHours(1));
    decimal? return4h = anchorBar is null ? null : ReturnAtOrAfter(bars, anchorIndex, anchorBar.Timestamp.AddHours(4));
    decimal? return1d = anchorBar is null ? null : ReturnAtOrAfter(bars, anchorIndex, anchorBar.Timestamp.AddDays(1));
    decimal? return5d = anchorBar is null ? null : ReturnAtOrAfter(bars, anchorIndex, anchorBar.Timestamp.AddDays(5));

    return new CatalystTrendMatch(
        catalyst.Timestamp,
        TimeZoneInfo.ConvertTime(catalyst.Timestamp, ResolveCentralEuropeanTime()).ToString("yyyy-MM-dd HH:mm:ss zzz"),
        catalyst.Headline,
        catalyst.SentimentScore,
        catalyst.Source,
        catalyst.Url,
        anchorBar?.Timestamp,
        anchorBar?.Close,
        anchorBar?.Volume,
        snapshot?.Rsi,
        snapshot?.RelativeVolume,
        snapshot?.Vwap,
        snapshot?.MacdHistogram,
        return1h,
        return4h,
        return1d,
        return5d,
        LabelFollowThrough(catalyst.SentimentScore, return4h, return1d));
}

static decimal? ReturnAtOrAfter(
    IReadOnlyList<TradingFlow.Domain.Market.OhlcvBar> bars,
    int anchorIndex,
    DateTimeOffset targetTime)
{
    var anchorClose = bars[anchorIndex].Close;
    var target = bars.Skip(anchorIndex + 1).FirstOrDefault(x => x.Timestamp >= targetTime);
    return target is null ? null : PercentChange(anchorClose, target.Close);
}

static decimal PercentChange(decimal start, decimal end)
{
    return start == 0m ? 0m : ((end / start) - 1m) * 100m;
}

static decimal? ReturnFromTrailingDailyBars(IReadOnlyList<TradingFlow.Domain.Market.OhlcvBar> dailyBars, int sessions)
{
    if (dailyBars.Count <= sessions)
    {
        return null;
    }

    return PercentChange(dailyBars[^sessions].Close, dailyBars[^1].Close);
}

static string LabelTrend(decimal? oneMonthReturn, decimal? fiveDayReturn)
{
    return (oneMonthReturn, fiveDayReturn) switch
    {
        ({ } month, { } five) when month > 10m && five > 0m => "uptrend_with_recent_strength",
        ({ } month, { } five) when month > 10m && five <= 0m => "uptrend_pullback",
        ({ } month, _) when month < -10m => "downtrend",
        _ => "mixed"
    };
}

static string LabelFollowThrough(decimal sentiment, decimal? return4h, decimal? return1d)
{
    if (return4h is null && return1d is null)
    {
        return "insufficient_future_candles";
    }

    var followThrough = return4h ?? return1d!.Value;
    return sentiment switch
    {
        >= 0.15m when followThrough > 0m => "positive_news_positive_follow_through",
        >= 0.15m when followThrough <= 0m => "positive_news_failed_follow_through",
        <= -0.15m when followThrough < 0m => "negative_news_negative_follow_through",
        <= -0.15m when followThrough >= 0m => "negative_news_recovered",
        _ when followThrough > 0m => "neutral_news_positive_move",
        _ when followThrough < 0m => "neutral_news_negative_move",
        _ => "flat"
    };
}

static TimeZoneInfo ResolveCentralEuropeanTime()
{
    try
    {
        return TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    }
    catch (TimeZoneNotFoundException)
    {
        return TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
    }
}

static TradingFlow.Engine.Abstractions.IMarketDataProvider CreateProvider(TradingFlow.Domain.Backtesting.BacktestRunConfig run)
{
    return run.Provider.ToLowerInvariant() switch
    {
        "csv" => new TradingFlow.Data.Csv.CsvMarketDataProvider(run.NormalizedRoot),
        "alpaca" => new TradingFlow.Alpaca.AlpacaMarketDataProvider(
            new HttpClient(),
            ResolveAlpacaOptions(run)),
        "finviz" => new TradingFlow.Finviz.FinvizMarketDataProvider(
            new TradingFlow.Finviz.FinvizClient(
                new HttpClient(),
                TradingFlow.Finviz.FinvizOptions.CreateDefault() with
                {
                    AuthToken = Environment.GetEnvironmentVariable("FINVIZ_API_KEY") ?? ""
                })),
        _ => throw new NotSupportedException($"Unsupported market data provider: {run.Provider}")
    };
}

static int? ParseIntOption(string[] args, string name)
{
    var value = ParseStringOption(args, name);
    return value is null ? null : Int32.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}

static decimal? ParseDecimalOption(string[] args, string name)
{
    var value = ParseStringOption(args, name);
    return value is null ? null : Decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}

static DateTimeOffset? ParseDateOption(string[] args, string name)
{
    var value = ParseStringOption(args, name);
    return value is null ? null : DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}

static string? ParseStringOption(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }

    return null;
}

static bool ParseFlag(string[] args, string name)
{
    return args.Any(arg => arg.Equals(name, StringComparison.OrdinalIgnoreCase));
}

static string ResolveWarmOutputRoot(string normalizedRoot, int lookbackDays)
{
    var fullRoot = Path.GetFullPath(normalizedRoot);
    var leaf = Path.GetFileName(fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    var segment = $"{lookbackDays}d";
    if (leaf.Equals(segment, StringComparison.OrdinalIgnoreCase))
    {
        return fullRoot;
    }

    if (leaf.EndsWith("d", StringComparison.OrdinalIgnoreCase) &&
        Int32.TryParse(leaf[..^1], out _))
    {
        return Path.Combine(Path.GetDirectoryName(fullRoot)!, segment);
    }

    return Path.Combine(fullRoot, segment);
}

static IReadOnlyList<string> ResolveCsvTickers(string? tickersCsv, IReadOnlyList<string> fallbackTickers)
{
    var source = String.IsNullOrWhiteSpace(tickersCsv)
        ? fallbackTickers
        : tickersCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    return source
        .Select(x => x.Trim().ToUpperInvariant())
        .Where(x => !String.IsNullOrWhiteSpace(x))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(x => x)
        .ToArray();
}
static IReadOnlyList<string> ResolveSwingAnalysisTickers(
    TradingFlow.Domain.Backtesting.BacktestResult result,
    string strategyName,
    string? tickersCsv)
{
    if (!String.IsNullOrWhiteSpace(tickersCsv))
    {
        return tickersCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToArray();
    }

    var strategy = result.StrategyResults.FirstOrDefault(x =>
        x.StrategyName.Equals(strategyName, StringComparison.OrdinalIgnoreCase));
    return (strategy?.CompletedTrades ?? result.CompletedTrades)
        .Select(x => x.Ticker.ToUpperInvariant())
        .Concat(result.TickerResults.Where(x => x.Succeeded).Select(x => x.Ticker.ToUpperInvariant()))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(x => x)
        .ToArray();
}

static IReadOnlyList<TradingFlow.Domain.Market.OhlcvBar> LoadDailyBars(string candlesRoot, string ticker)
{
    var path = Path.Combine(candlesRoot, ticker.ToUpperInvariant(), "bars_1d.csv");
    if (!File.Exists(path))
    {
        return Array.Empty<TradingFlow.Domain.Market.OhlcvBar>();
    }

    return File.ReadLines(path)
        .Skip(1)
        .Where(line => !String.IsNullOrWhiteSpace(line))
        .Select(line => ParseOhlcvCsvLine(ticker, line))
        .OrderBy(x => x.Timestamp)
        .ToArray();
}

static TradingFlow.Domain.Market.OhlcvBar ParseOhlcvCsvLine(string fallbackTicker, string line)
{
    var parts = line.Split(',');
    if (parts.Length < 7)
    {
        throw new FormatException($"Invalid OHLCV CSV line: {line}");
    }

    var culture = System.Globalization.CultureInfo.InvariantCulture;
    return new TradingFlow.Domain.Market.OhlcvBar(
        String.IsNullOrWhiteSpace(parts[0]) ? fallbackTicker.ToUpperInvariant() : parts[0].Trim().ToUpperInvariant(),
        DateTimeOffset.Parse(parts[1], culture),
        "1d",
        Decimal.Parse(parts[2], culture),
        Decimal.Parse(parts[3], culture),
        Decimal.Parse(parts[4], culture),
        Decimal.Parse(parts[5], culture),
        Decimal.Parse(parts[6], culture));
}

static string SanitizeFileName(string value)
{
    var invalid = Path.GetInvalidFileNameChars().ToHashSet();
    var cleaned = new String(value.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray());
    return String.IsNullOrWhiteSpace(cleaned) ? "report" : cleaned.Replace(' ', '-').ToLowerInvariant();
}

static int ResolveWarmupDataDays(TradingFlow.Domain.Backtesting.BacktestRunConfig run)
{
    var warmupDays = Math.Max(run.TimeWindow.WarmupLookbackDays, 0);
    if (run.Mode.Equals("backtest", StringComparison.OrdinalIgnoreCase))
    {
        return Math.Max(1, run.TimeWindow.LookbackDays + warmupDays);
    }

    return Math.Max(1, warmupDays > 0 ? warmupDays : run.TimeWindow.LookbackDays);
}

static TradingFlow.Alpaca.AlpacaOptions ResolveAlpacaOptions(TradingFlow.Domain.Backtesting.BacktestRunConfig run)
{
    return TradingFlow.Alpaca.AlpacaOptions.CreateDefault() with
    {
        KeyId = ResolveSecret("Alpaca", "KeyId", "ALPACA_KEY_ID"),
        SecretKey = ResolveSecret("Alpaca", "SecretKey", "ALPACA_SECRET_KEY"),
        MarketDataFeed = run.Providers.Alpaca.DataFeed
    };
}

static string ResolveSecret(string section, string key, string environmentVariable)
{
    var settingsPath = FindRepositoryFile(Path.Combine("src", "TradingFlow.Web", "appsettings.local.json"));
    if (settingsPath is not null)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
        if (document.RootElement.TryGetProperty(section, out var sectionElement) &&
            sectionElement.TryGetProperty(key, out var keyElement))
        {
            var localValue = keyElement.GetString();
            if (!String.IsNullOrWhiteSpace(localValue))
            {
                return localValue;
            }
        }
    }

    return Environment.GetEnvironmentVariable(environmentVariable) ?? String.Empty;
}

static string? FindRepositoryFile(string relativePath)
{
    foreach (var startPath in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
    {
        var directory = new DirectoryInfo(startPath);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }
    }

    return null;
}

public sealed record CatalystTrendMatch(
    DateTimeOffset Timestamp,
    string LocalTime,
    string Headline,
    decimal SentimentScore,
    string? Source,
    string? Url,
    DateTimeOffset? AnchorCandleTime,
    decimal? AnchorClose,
    decimal? AnchorVolume,
    decimal? Rsi,
    decimal? RelativeVolume,
    decimal? Vwap,
    decimal? MacdHistogram,
    decimal? Return1hPct,
    decimal? Return4hPct,
    decimal? Return1dPct,
    decimal? Return5dPct,
    string FollowThroughLabel);

public sealed record CatalystWarmTickerResult(
    string Ticker,
    bool Succeeded,
    int CatalystCount,
    string? Error);
