namespace TradingFlow.Contracts.V1;

public sealed record TradingLimitProfileResponse(string ContractVersion, string Mode, int MaxNewStocksPerDay,
    int MaxOpenPositions, long Revision, DateTimeOffset? SavedAtUtc, string CountingRule);
public sealed record SaveTradingLimitProfileRequest(string ContractVersion, string? Mode, int MaxNewStocksPerDay,
    int MaxOpenPositions, long ExpectedRevision);
