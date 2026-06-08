using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using Microsoft.Extensions.Logging;

namespace TradingFlow.Backtesting;

public sealed class StrategyVerificationAgent
{
    private readonly SimpleYamlReader _yamlReader = new();
    private readonly ILogger _logger;

    public StrategyVerificationAgent(ILogger logger)
    {
        _logger = logger;
    }

    public void StartVerification(BacktestRunConfig liveRunConfig, string selectedStrategyPath, CancellationToken cancellationToken, IProgress<string>? progress = null)
    {
        // Run verification in the background
        _ = Task.Run(() => VerifyAllStrategiesAsync(liveRunConfig, selectedStrategyPath, cancellationToken, progress), cancellationToken);
    }

    private async Task VerifyAllStrategiesAsync(BacktestRunConfig liveRunConfig, string selectedStrategyPath, CancellationToken cancellationToken, IProgress<string>? progress = null)
    {
        try
        {
            _logger.LogInformation("Strategy Verification Agent started.");
            progress?.Report("Verification Agent: Scanning strategies directory...");

            string strategiesDir;
            if (File.Exists(selectedStrategyPath))
            {
                strategiesDir = Path.GetDirectoryName(Path.GetFullPath(selectedStrategyPath)) ?? string.Empty;
            }
            else
            {
                strategiesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "configs", "strategies");
                if (!Directory.Exists(strategiesDir))
                {
                    strategiesDir = Path.GetFullPath("configs/strategies");
                }
            }

            if (string.IsNullOrEmpty(strategiesDir) || !Directory.Exists(strategiesDir))
            {
                _logger.LogWarning("Strategies directory not found at: {Dir}", strategiesDir);
                progress?.Report($"Verification Agent: Strategies directory not found at {strategiesDir}");
                return;
            }

            var strategyFiles = Directory.GetFiles(strategiesDir, "*.yaml", SearchOption.TopDirectoryOnly);
            _logger.LogInformation("Found {Count} strategy files to verify.", strategyFiles.Length);
            progress?.Report($"Verification Agent: Found {strategyFiles.Length} strategy files to verify.");

            var reportPath = Path.Combine(liveRunConfig.ResultsRoot, "live", liveRunConfig.RunName, "strategy_verification_report.md");
            var reportDir = Path.GetDirectoryName(reportPath);
            if (!string.IsNullOrEmpty(reportDir))
            {
                Directory.CreateDirectory(reportDir);
            }

            var reportContent = new StringBuilder();
            reportContent.AppendLine("# Strategy Verification Agent Report");
            reportContent.AppendLine($"Generated on: {DateTimeOffset.UtcNow:f} (UTC)");
            reportContent.AppendLine($"Live Run Name: {liveRunConfig.RunName}");
            reportContent.AppendLine();
            reportContent.AppendLine("| Strategy File | Strategy Name | Status | Net Return | Win Rate | Max DD | Details / Error |");
            reportContent.AppendLine("|---|---|---|---|---|---|---|");

            var runner = new BacktestRunner(_yamlReader);

            foreach (var file in strategyFiles)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var fileName = Path.GetFileName(file);
                _logger.LogInformation("Verifying strategy: {FileName}", fileName);
                progress?.Report($"Verification Agent: Verifying strategy: {fileName}...");

                try
                {
                    var strategyDef = _yamlReader.ReadStrategy(file);
                    
                    // Create a backtest run config specifically for this strategy verification
                    // Using a short time window (e.g., 10 days rolling lookback) for a fast validation
                    var verificationConfig = new BacktestRunConfig(
                        $"verify-{strategyDef.StrategyId}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
                        "backtest",
                        new EngineConfig("tpl", 0, 1000, 150, false),
                        new TimeWindowConfig("rolling", 10, null, null), 
                        liveRunConfig.Tickers,
                        liveRunConfig.Provider,
                        liveRunConfig.Intervals,
                        liveRunConfig.RawRoot,
                        liveRunConfig.NormalizedRoot,
                        liveRunConfig.ResultsRoot,
                        "refresh", // Refresh cache to verify with latest data
                        liveRunConfig.DerivedTimeframes,
                        new ValidationConfig(
                            new OutOfSampleConfig(false, 0m),
                            new WalkForwardConfig(false, 0, 0),
                            new BenchmarkConfig(false, ""),
                            new DataQualityConfig(false, 0, 0, 0m),
                            new BiasRiskConfig("static_config", null, "yahoo_chart_quote")
                        ),
                        liveRunConfig.Providers,
                        liveRunConfig.Portfolio,
                        liveRunConfig.SignalSource,
                        new ExecutionConfig("simulated", "none", true, false, "bracket", "gtc", "limit"),
                        new NewsConfig(false, "none", 0, 0m),
                        liveRunConfig.Screener,
                        new[] { file }
                    );

                    var tempResultPath = Path.Combine(liveRunConfig.ResultsRoot, "live", liveRunConfig.RunName, $"verify-{strategyDef.StrategyId}.json");
                    var result = await runner.RunAsync(verificationConfig, new[] { strategyDef }, DateTimeOffset.UtcNow, tempResultPath, cancellationToken);

                    var winner = result.Winner;
                    if (winner != null)
                    {
                        var status = result.NetProfit >= 0 ? "🟢 Success" : "🟡 Underperforming";
                        var winRate = result.AcceptedTradeCount > 0 ? (result.WinningTradeCount * 100.0m / result.AcceptedTradeCount) : 0m;
                        reportContent.AppendLine($"| {fileName} | {strategyDef.StrategyName} | {status} | {result.TotalReturnPct:F2}% | {winRate:F1}% | {result.MaxDrawdownPct:F2}% | Win/Loss: {result.WinningTradeCount}/{result.LosingTradeCount}, Total Trades: {result.AcceptedTradeCount} |");
                    }
                    else
                    {
                        reportContent.AppendLine($"| {fileName} | {strategyDef.StrategyName} | ⚪ No Trades | - | - | - | Backtest completed, but generated zero trade signals |");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to verify strategy {FileName}", fileName);
                    reportContent.AppendLine($"| {fileName} | Unknown | 🔴 Failure | - | - | - | {ex.Message.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ")} |");
                }
            }

            await File.WriteAllTextAsync(reportPath, reportContent.ToString(), cancellationToken);
            _logger.LogInformation("Verification Agent report written to {ReportPath}", reportPath);
            progress?.Report($"Verification Agent: Done! Report saved to strategy_verification_report.md");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Strategy Verification Agent");
            progress?.Report($"Verification Agent: Error encountered: {ex.Message}");
        }
    }
}
