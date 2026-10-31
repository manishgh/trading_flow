using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Execution;

namespace TradingFlow.Application.Jobs;

/// <summary>Expose one saved mode profile to API, Web and Android without starting a trading run.</summary>
public sealed class TradingLimitsService(IOperationalTradingLimitStore store, TimeProvider clock)
{
    /// <summary>Return server-resolved values and their precise account-wide counting rule.</summary>
    public async Task<TradingLimitProfileResponse> GetAsync(string mode, CancellationToken token = default) =>
        Project(await store.GetAsync(mode, token));

    /// <summary>Save choices independently of run creation, preserving optimistic concurrency for multiple editors.</summary>
    public async Task<TradingLimitProfileResponse> SaveAsync(SaveTradingLimitProfileRequest request, CancellationToken token = default)
    {
        if (request.ContractVersion != ContractVersions.VersionOne || String.IsNullOrWhiteSpace(request.Mode))
            throw new InvalidOperationException("A supported trading-limits contract and an explicit paper or live mode are required.");
        return Project(await store.SaveAsync(request.Mode, request.MaxNewStocksPerDay, request.MaxOpenPositions,
            request.ExpectedRevision, clock.GetUtcNow(), token));
    }

    /// <summary>Use plain labels while explaining pending capacity and that historical studies use separate limits.</summary>
    private static TradingLimitProfileResponse Project(OperationalTradingLimits value) => new(
        ContractVersions.VersionOne, value.Mode, value.MaxNewStocksPerDay, value.MaxOpenPositions, value.Revision, value.SavedAtUtc,
        "Across this account's runs: count each stock once on its first entry fill in a New York day; partial fills and same-day reentry do not add another stock. Pending entry orders reserve capacity until filled or cancelled. Holdings count until fully closed. Historical backtests do not use these operational limits.");
}
