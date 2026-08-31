using TradingFlow.Alpaca;
using TradingFlow.Backtesting.StrategyEvaluation;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Configuration;

namespace TradingFlow.Web.Services;

public sealed class StrategyEvaluationService
{
    private readonly SimpleYamlReader yamlReader;
    private readonly ProjectPaths paths;
    private readonly AlpacaCredentialProvider alpacaCredentials;
    private readonly StrategyArtifactCatalog? strategyArtifacts;
    private readonly StrategyEvaluationEngine evaluationEngine = new();

    public StrategyEvaluationService(
        SimpleYamlReader yamlReader,
        ProjectPaths paths,
        AlpacaCredentialProvider alpacaCredentials,
        StrategyArtifactCatalog? strategyArtifacts = null)
    {
        this.yamlReader = yamlReader;
        this.paths = paths;
        this.alpacaCredentials = alpacaCredentials;
        this.strategyArtifacts = strategyArtifacts;
    }

    public Task<StrategyEvaluationResponse> EvaluateWithAlpacaAsync(StrategyEvaluationRequest request, CancellationToken cancellationToken)
    {
        var configPath = ResolvePath(request.ConfigPath, Path.Combine("configs", "paper", "alpaca-paper.yaml"));
        var runConfig = yamlReader.ReadBacktestRun(configPath);
        var provider = new AlpacaMarketDataProvider(
            new HttpClient(),
            AlpacaOptions.Create(TradingFlow.Engine.Configuration.ProductionProfile.Paper) with
            {
                KeyId = alpacaCredentials.KeyId,
                SecretKey = alpacaCredentials.SecretKey,
                MarketDataFeed = runConfig.Providers.Alpaca.DataFeed
            });

        return EvaluateAsync(request, provider, runConfig, cancellationToken);
    }

    public Task<StrategyEvaluationResponse> EvaluateAsync(
        StrategyEvaluationRequest request,
        IMarketDataProvider provider,
        BacktestRunConfig? suppliedRunConfig,
        CancellationToken cancellationToken)
    {
        var configPath = ResolvePath(request.ConfigPath, Path.Combine("configs", "paper", "alpaca-paper.yaml"));
        var runConfig = suppliedRunConfig ?? yamlReader.ReadBacktestRun(configPath);
        var strategyPath = ResolvePath(request.StrategyPath, Path.Combine("configs", "strategies", "intraday-ema10-ema20-macd-volume.v1.yaml"));
        var runtimeStrategy = strategyArtifacts is not null
            ? new StrategyRunArtifactValidator(yamlReader, strategyArtifacts)
                .ReadAndValidateRuntimeArtifact(
                    strategyPath,
                    TradingFlow.Domain.Strategies.StrategySelectionMode.DiagnosticReplay)
            : StrategyRunArtifactValidator.CreateDiagnosticRuntimeStrategy(
                yamlReader.ReadStrategy(strategyPath));

        return evaluationEngine.EvaluateAsync(request, provider, runConfig, runtimeStrategy, strategyPath, cancellationToken);
    }

    private string ResolvePath(string? requestedPath, string fallbackRelativePath)
    {
        return paths.ResolveRepositoryPath(String.IsNullOrWhiteSpace(requestedPath) ? fallbackRelativePath : requestedPath);
    }
}

