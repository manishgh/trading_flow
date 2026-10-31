using TradingFlow.Contracts.V1;

namespace TradingFlow.Contracts.Transport;

public sealed partial class TradingWorkflowClient
{
    /// <summary>Queues provider preparation and the shared strategy engine without creating orders.</summary>
    public Task<StockStrategyAssessmentJob> StartStockStrategyAssessmentAsync(StartStockStrategyAssessmentRequest request,
        CancellationToken token = default) => PostAsync<StartStockStrategyAssessmentRequest, StockStrategyAssessmentJob>(
        "desk/strategy-assessments", request, token);

    /// <summary>Reads a durable observation and its original checks.</summary>
    public Task<StockStrategyAssessmentJob> StockStrategyAssessmentJobAsync(Guid job, CancellationToken token = default) =>
        GetAsync<StockStrategyAssessmentJob>($"desk/strategy-assessments/{job}", token);

    /// <summary>Requests cancellation through the existing durable queue.</summary>
    public Task<StockStrategyAssessmentJob> CancelStockStrategyAssessmentAsync(Guid job, CancellationToken token = default) =>
        PostAsync<object, StockStrategyAssessmentJob>($"desk/strategy-assessments/{job}/cancel", new {}, token);
    /// <summary>Lists exact strategies and recorded runs without starting preparation or execution.</summary>
    public Task<StockStrategyChoice[]> StockStrategiesAsync(CancellationToken token = default) =>
        GetAsync<StockStrategyChoice[]>("desk/strategy-choices", token);

    /// <summary>Reads recorded checks for the requested stocks; absent evidence remains explicit.</summary>
    public Task<StockStrategyAssessmentResponse> StockStrategyAssessmentsAsync(
        Guid? runId, StrategyReference strategy, IReadOnlyList<string> symbols, CancellationToken token = default)
    {
        var query = "desk/strategy-assessments?strategyId=" + Uri.EscapeDataString(strategy.StrategyId) +
            "&semanticVersion=" + Uri.EscapeDataString(strategy.SemanticVersion) +
            "&contentSha256=" + Uri.EscapeDataString(strategy.ContentSha256) +
            "&symbols=" + Uri.EscapeDataString(String.Join(',', symbols));
        if (runId is { } run) query += "&runId=" + run;
        return GetAsync<StockStrategyAssessmentResponse>(query, token);
    }
}
