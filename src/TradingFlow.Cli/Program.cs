using System.Text.Json;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Signals;
using TradingFlow.Domain.Strategies;
using TradingFlow.Backtesting;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Signals;
using TradingFlow.Backtesting.Optimization;
using Microsoft.Extensions.Logging;

var serializerOptions = new JsonSerializerOptions
{
    WriteIndented = true
};

if (args.Length > 0 && args[0].Equals("simulate-tradingview", StringComparison.OrdinalIgnoreCase))
{
    await SimulateTradingViewAsync(args);
    return;
}

if (args.Length > 0 && args[0].Equals("optimize", StringComparison.OrdinalIgnoreCase))
{
    var optConfigPath = args.Length > 1
        ? args[1]
        : throw new ArgumentException("Optimization config path required.");
    
    var reader = new SimpleYamlReader();
    var runner = new BacktestRunner(reader);
    var optimizer = new StrategyOptimizer(reader, runner);
    
    var optResult = await optimizer.OptimizeAsync(optConfigPath, CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(new { optResult.RunName, optResult.TopRuns.Count, TopPermutations = optResult.TopRuns.Take(3).Select(r => new { r.Rank, r.MetricValue, r.ParameterValues }) }, serializerOptions));
    return;
}

var configPath = args.Length > 0
    ? args[0]
    : Path.Combine("configs", "backtest", "semiconductors-research.yaml");

var runnerInstance = new BacktestRunner(new SimpleYamlReader());
var runConfig = new SimpleYamlReader().ReadBacktestRun(configPath);
if (runConfig.Mode.Equals("paper", StringComparison.OrdinalIgnoreCase) || runConfig.Mode.Equals("live", StringComparison.OrdinalIgnoreCase))
{
    var runner = new TradingFlow.Backtesting.LiveRunner(
        CreateProvider(runConfig), 
        CreateNewsProvider(runConfig), 
        new TradingFlow.Engine.Execution.PseudoBroker(),
        null,
        null,
        null,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<TradingFlow.Backtesting.LiveRunner>.Instance);
    
    var reader = new SimpleYamlReader();
    var strategies = runConfig.Strategies.Select(reader.ReadStrategy).ToArray();
    await runner.RunAsync(runConfig, strategies, CancellationToken.None);
    Console.WriteLine("Live Runner execution finished.");
}
else
{
    var result = await runnerInstance.RunAsync(configPath, CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(result, serializerOptions));
}

async Task SimulateTradingViewAsync(string[] args)
{
    var configPath = args.Length > 1
        ? args[1]
        : Path.Combine("configs", "paper", "local-yahoo-paper.yaml");
    var backtestResultPath = args.Length > 2
        ? args[2]
        : Path.Combine("data", "backtest", "results", "portfolio", "semiconductors-research.json");

    var reader = new SimpleYamlReader();
    var config = reader.ReadBacktestRun(configPath);
    var strategies = config.Strategies.Select(reader.ReadStrategy).ToArray();
    var validator = new TradingViewSignalValidator(
        config.SignalSource,
        new TradingViewSignalDeduper(TimeSpan.FromSeconds(config.SignalSource.DedupeWindowSeconds)));

    using var resultDocument = JsonDocument.Parse(await File.ReadAllTextAsync(backtestResultPath));
    var simulations = new List<TradingViewSimulationRow>();
    var duplicateReplayTested = false;

    foreach (var strategyResult in resultDocument.RootElement.GetProperty("StrategyResults").EnumerateArray())
    {
        var strategyId = strategyResult.GetProperty("StrategyId").GetString() ?? String.Empty;
        var strategyName = strategyResult.GetProperty("StrategyName").GetString() ?? String.Empty;
        var strategy = strategies.FirstOrDefault(candidate =>
            candidate.StrategyId.Equals(strategyId, StringComparison.OrdinalIgnoreCase));

        if (strategy is null ||
            !strategyResult.TryGetProperty("CompletedTrades", out var trades) ||
            trades.GetArrayLength() == 0)
        {
            continue;
        }

        var trade = trades.EnumerateArray().First();
        var entryTime = trade.GetProperty("EntryTimestamp").GetDateTimeOffset();
        var exitTime = trade.GetProperty("ExitTimestamp").GetDateTimeOffset();
        var ticker = trade.GetProperty("Ticker").GetString() ?? String.Empty;
        var entryPrice = trade.GetProperty("EntryPrice").GetDecimal();
        var exitPrice = trade.GetProperty("ExitPrice").GetDecimal();
        var stopLoss = trade.GetProperty("StopLossPrice").GetDecimal();
        var takeProfit = trade.GetProperty("TakeProfitPrice").GetDecimal();
        var exitReason = trade.GetProperty("ExitReason").GetString() ?? "unknown";

        var entryRequest = CreateRequest(
            strategy,
            ticker,
            "entry",
            entryTime,
            entryPrice,
            stopLoss,
            takeProfit,
            exitReason: null);
        var entryValidation = validator.Validate(entryRequest.ToExternalSignal(), strategies, entryTime.AddSeconds(5), signatureVerified: true);
        var entryRow = CreateRow(entryRequest, entryValidation);
        simulations.Add(entryRow);

        if (entryValidation.IsAccepted && !duplicateReplayTested)
        {
            simulations.Add(CreateRow(
                entryRequest,
                validator.Validate(entryRequest.ToExternalSignal(), strategies, entryTime.AddSeconds(10), signatureVerified: true)));
            duplicateReplayTested = true;
        }

        var exitRequest = CreateRequest(
            strategy,
            ticker,
            "exit",
            exitTime,
            exitPrice,
            stopLoss,
            takeProfit,
            exitReason);
        simulations.Add(CreateRow(
            exitRequest,
            validator.Validate(exitRequest.ToExternalSignal(), strategies, exitTime.AddSeconds(5), signatureVerified: true)));
    }

    var outputPath = Path.Combine(config.ResultsRoot, "tradingview-webhook-simulation.json");
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
    var summary = new
    {
        Config = configPath,
        SourceBacktestResult = backtestResultPath,
        SignalSource = config.SignalSource,
        Accepted = simulations.Count(row => row.Accepted),
        Rejected = simulations.Count(row => !row.Accepted),
        Rows = simulations
    };

    await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(summary, serializerOptions));
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        outputPath,
        summary.Accepted,
        summary.Rejected
    }, serializerOptions));
}

ExternalSignal CreateSignal(
    StrategyDefinition strategy,
    string ticker,
    string action,
    DateTimeOffset barTimeUtc,
    decimal price,
    decimal? stopLoss,
    decimal? takeProfit,
    string? exitReason)
{
    var signalValues = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
    {
        ["risk_per_share"] = stopLoss is null ? 0m : Decimal.Round(Math.Abs(price - stopLoss.Value), 4)
    };

    if (!String.IsNullOrWhiteSpace(exitReason))
    {
        signalValues["exit_reason_code"] = exitReason switch
        {
            "take_profit" => 1m,
            "stop_loss" => 2m,
            "trailing_stop" => 3m,
            "max_hold" => 4m,
            _ => 0m
        };
    }

    return new ExternalSignal(
        "1.0",
        $"tv-sim:{strategy.StrategyId}:{ticker}:{barTimeUtc:yyyyMMddHHmmss}:{action}",
        "tradingview",
        strategy.StrategyId,
        strategy.StrategyName,
        strategy.Version,
        ticker,
        action,
        "long",
        strategy.Timeframe,
        strategy.Execution.Timeframe,
        barTimeUtc,
        Decimal.Round(price, 4),
        stopLoss is null ? null : Decimal.Round(stopLoss.Value, 4),
        takeProfit is null ? null : Decimal.Round(takeProfit.Value, 4),
        signalValues);
}

TradingViewWebhookPayload CreateRequest(
    StrategyDefinition strategy,
    string ticker,
    string action,
    DateTimeOffset barTimeUtc,
    decimal price,
    decimal? stopLoss,
    decimal? takeProfit,
    string? exitReason)
{
    return TradingViewWebhookPayload.FromExternalSignal(
        CreateSignal(strategy, ticker, action, barTimeUtc, price, stopLoss, takeProfit, exitReason),
        exitReason);
}

TradingViewSimulationRow CreateRow(
    TradingViewWebhookPayload request,
    ExternalSignalValidationResult validation)
{
    return new TradingViewSimulationRow(
        request.StrategyId,
        request.StrategyName,
        request.Ticker,
        request.Action,
        DateTimeOffset.FromUnixTimeMilliseconds(request.BarTimeEpochMs),
        validation.IsAccepted,
        validation.RejectionReason,
        request);
}

TradingFlow.Engine.Abstractions.ICatalystProvider? CreateNewsProvider(TradingFlow.Domain.Backtesting.BacktestRunConfig run)
{
    if (!run.News.Enabled) return null;
    
    return run.News.ProviderName.ToLowerInvariant() switch
    {
        "alpaca" => new TradingFlow.Alpaca.AlpacaNewsProvider(
            new HttpClient(), 
            TradingFlow.Alpaca.AlpacaOptions.CreateDefault() with
            {
                KeyId = Environment.GetEnvironmentVariable("ALPACA_KEY_ID") ?? "",
                SecretKey = Environment.GetEnvironmentVariable("ALPACA_SECRET_KEY") ?? ""
            }),
        "finviz" => new TradingFlow.Finviz.FinvizNewsProvider(
            new TradingFlow.Finviz.FinvizClient(
                new HttpClient(),
                TradingFlow.Finviz.FinvizOptions.CreateDefault() with { AuthToken = Environment.GetEnvironmentVariable("FINVIZ_API_KEY") ?? "" }
            )),
        "none" => null,
        _ => throw new NotSupportedException($"Unsupported news provider: {run.News.ProviderName}")
    };
}

TradingFlow.Engine.Abstractions.IMarketDataProvider CreateProvider(TradingFlow.Domain.Backtesting.BacktestRunConfig run)
{
    var etoroOptions = TradingFlow.Etoro.Configuration.EtoroOptions.CreateDefault(TradingFlow.Etoro.Configuration.EtoroEnvironment.Demo) with
    {
        Demo = new TradingFlow.Etoro.Configuration.EtoroCredentialProfile(
            "ETORO_DEMO_API_KEY",
            "ETORO_DEMO_USER_KEY",
            false)
    };
    var etoroCreds = new TradingFlow.Etoro.Authentication.EtoroCredentialsProvider(etoroOptions);
    var etoroRateLimiter = new TradingFlow.Etoro.Http.EtoroRateLimiter(etoroOptions.RateLimits);

    return run.Provider.ToLowerInvariant() switch
    {
        "csv" => new TradingFlow.Data.Csv.CsvMarketDataProvider(run.NormalizedRoot),
        "yahoo" => new TradingFlow.Data.Yahoo.YahooFinanceProvider(
            TradingFlow.Data.Yahoo.YahooFinanceProvider.CreateBrowserLikeClient(run.Providers.Yahoo),
            run.Providers.Yahoo,
            run.RawRoot,
            run.NormalizedRoot,
            run.CachePolicy),
        "alpaca" => new TradingFlow.Alpaca.AlpacaMarketDataProvider(
            new HttpClient(),
            TradingFlow.Alpaca.AlpacaOptions.CreateDefault() with
            {
                KeyId = Environment.GetEnvironmentVariable("ALPACA_KEY_ID") ?? "",
                SecretKey = Environment.GetEnvironmentVariable("ALPACA_SECRET_KEY") ?? ""
            }),
        "finviz" => new TradingFlow.Finviz.FinvizMarketDataProvider(
            new TradingFlow.Finviz.FinvizClient(
                new HttpClient(),
                TradingFlow.Finviz.FinvizOptions.CreateDefault() with { AuthToken = Environment.GetEnvironmentVariable("FINVIZ_API_KEY") ?? "" }
            )),
        "etoro" => new TradingFlow.Etoro.MarketData.EtoroMarketDataProvider(
            new TradingFlow.Etoro.Http.EtoroApiClient(
                new HttpClient(),
                etoroOptions,
                etoroCreds,
                etoroRateLimiter),
            new TradingFlow.Etoro.MarketData.EtoroInstrumentResolver(
                new TradingFlow.Etoro.Http.EtoroApiClient(
                    new HttpClient(),
                    etoroOptions,
                    etoroCreds,
                    etoroRateLimiter)
            )
        ),
        _ => throw new NotSupportedException($"Unsupported market data provider: {run.Provider}")
    };
}

internal sealed record TradingViewSimulationRow(
    string StrategyId,
    string StrategyName,
    string Ticker,
    string Action,
    DateTimeOffset BarTimeUtc,
    bool Accepted,
    string? RejectionReason,
    TradingViewWebhookPayload Request);
