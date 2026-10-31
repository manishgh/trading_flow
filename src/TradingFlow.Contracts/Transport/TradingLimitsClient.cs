using TradingFlow.Contracts.V1;

namespace TradingFlow.Contracts.Transport;

public sealed partial class TradingWorkflowClient
{
    /// <summary>Loads central mode limits rather than independent Web or Android defaults.</summary>
    public Task<TradingLimitProfileResponse> TradingLimitsAsync(string mode, CancellationToken token = default) =>
        GetAsync<TradingLimitProfileResponse>("trading-limits/" + Uri.EscapeDataString(mode), token);

    /// <summary>Saves limits without starting a run, requiring the exact revision shown to the operator.</summary>
    public Task<TradingLimitProfileResponse> SaveTradingLimitsAsync(SaveTradingLimitProfileRequest request, CancellationToken token = default) =>
        PostAsync<SaveTradingLimitProfileRequest, TradingLimitProfileResponse>("trading-limits", request, token);
}
