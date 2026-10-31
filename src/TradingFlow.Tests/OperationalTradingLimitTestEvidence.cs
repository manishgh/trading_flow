using TradingFlow.Domain.Execution;

namespace TradingFlow.Tests;

internal static class OperationalTradingLimitTestEvidence
{
    // Supply explicit complete fake broker history without reading or modifying an operational account.
    public static BrokerFillHistorySnapshot Empty(string accountId, DateTimeOffset nowUtc) =>
        new(accountId, TradingLimitDay.At(nowUtc), nowUtc, nowUtc,
            TradingLimitDay.StartUtc(TradingLimitDay.At(nowUtc)), nowUtc, true, [], 1, ["isolated-test-fixture"], []);
}
