using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Orders;

namespace TradingFlow.Backtesting.Artifacts;

public static class BacktestArtifactProjector
{
    public const string SummaryRetentionMode = "summary";
    public const string FullRetentionMode = "full";

    // Apply configured detail retention while preserving the independently reconcilable portfolio accounting evidence.
    public static BacktestResult Project(BacktestResult result, ArtifactRetentionConfig artifacts)
    {
        var retentionMode = NormalizeRetentionMode(artifacts.RetentionMode);
        return retentionMode switch
        {
            FullRetentionMode => result,
            SummaryRetentionMode => ProjectSummary(result),
            _ => throw new InvalidOperationException(
                $"Unsupported artifacts.retention_mode '{artifacts.RetentionMode}'. Supported values are '{SummaryRetentionMode}' and '{FullRetentionMode}'.")
        };
    }

    // Trim only the old trade/order presentation detail; retain state fills/sessions, eligibility and terminal exposure.
    private static BacktestResult ProjectSummary(BacktestResult result)
    {
        return result with
        {
            CompletedTrades = Array.Empty<BacktestTrade>(),
            AcceptedOrders = Array.Empty<FinalizedOrder>(),
            StrategyResults = result.StrategyResults
                .Select(strategy => strategy with
                {
                    CompletedTrades = Array.Empty<BacktestTrade>(),
                    ExecutionFailures = Array.Empty<BacktestExecutionFailure>()
                })
                .ToArray(),
            UnifiedPortfolio = result.UnifiedPortfolio is null
                ? null
                : result.UnifiedPortfolio with { CompletedTrades = Array.Empty<BacktestTrade>() }
        };
    }

    // Keep the existing full default and deterministic spelling for explicitly selected retention modes.
    private static string NormalizeRetentionMode(string? retentionMode)
    {
        return String.IsNullOrWhiteSpace(retentionMode)
            ? FullRetentionMode
            : retentionMode.Trim().ToLowerInvariant();
    }
}

