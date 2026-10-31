using TradingFlow.Contracts.V1;

namespace TradingFlow.Contracts.Transport;

public sealed partial class TradingWorkflowClient
{
    /// <summary>Loads the same real wishlist and saved Finviz choices shown by Web.</summary>
    public Task<IReadOnlyList<BacktestStockSource>> BacktestSourcesAsync(CancellationToken token = default) =>
        GetAsync<IReadOnlyList<BacktestStockSource>>("backtests/sources", token);
    /// <summary>Shows group membership and complete workload before saving or starting.</summary>
    public Task<BacktestPlanResponse> PreviewBacktestPlanAsync(SaveBacktestPlanRequest request, CancellationToken token = default) =>
        PostAsync<SaveBacktestPlanRequest, BacktestPlanResponse>("backtests/plans/preview", request, token);

    /// <summary>Saves choices without creating executable jobs.</summary>
    public Task<BacktestPlanResponse> SaveBacktestPlanAsync(SaveBacktestPlanRequest request, CancellationToken token = default) =>
        PostAsync<SaveBacktestPlanRequest, BacktestPlanResponse>("backtests/plans", request, token);

    /// <summary>Lists saved automatic and manual cohort plans.</summary>
    public Task<BacktestPlanResponse[]> BacktestPlansAsync(CancellationToken token = default) =>
        GetAsync<BacktestPlanResponse[]>("backtests/plans", token);

    /// <summary>Starts the exact saved plan using a stable retry key.</summary>
    public Task<BacktestPlanStartResponse> StartBacktestPlanAsync(long id, StartBacktestPlanRequest request, CancellationToken token = default) =>
        PostAsync<StartBacktestPlanRequest, BacktestPlanStartResponse>($"backtests/plans/{id}/start", request, token);
}
