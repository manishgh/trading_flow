namespace TradingFlow.Domain.Execution;

public sealed record TradingLimitDefaults(int MaxNewStocksPerDay, int MaxOpenPositions);
public sealed record OperationalTradingLimits(string Mode, int MaxNewStocksPerDay, int MaxOpenPositions,
    long Revision, DateTimeOffset? SavedAtUtc);

/// <summary>One saved operational profile per mode; historical simulation never reads these limits.</summary>
public interface IOperationalTradingLimitStore
{
    Task<OperationalTradingLimits> GetAsync(string mode, CancellationToken token = default);
    Task<OperationalTradingLimits> SaveAsync(string mode, int maxNewStocksPerDay, int maxOpenPositions,
        long expectedRevision, DateTimeOffset nowUtc, CancellationToken token = default);
}

public static class OperationalTradingLimitProfile
{
    /// <summary>Resolve supported operational modes without treating historical research as paper execution.</summary>
    public static string Mode(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim().ToLowerInvariant() switch
        {
            "paper" or "paper_shadow" or "paper_experiment" => "paper",
            "live" => "live",
            _ => throw new InvalidOperationException("Trading limits require an explicit paper or live mode; historical research has no operational daily-entry cap.")
        };
    }

    /// <summary>Use the existing immutable application-event store for both profiles and their audit history.</summary>
    public static string Stream(string mode) => "operational-trading-limits:" + Mode(mode);

}

public sealed record BrokerFillActivity(string ActivityId, string BrokerOrderId, string Symbol, string Side,
    decimal Quantity, decimal CumulativeQuantity, DateTimeOffset FilledAtUtc, string? PositionIntent);

/// <summary>Complete, freshly collected account fills; unavailable or partial history cannot be treated as zero entries.</summary>
public sealed record BrokerFillHistorySnapshot(string AccountId, DateOnly NewYorkDate,
    DateTimeOffset RequestedAtUtc, DateTimeOffset ObservedAtUtc, DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc, bool Complete, IReadOnlyList<BrokerFillActivity> Activities,
    int PageCount, IReadOnlyList<string> RawArchiveIds,
    IReadOnlyList<TradingFlow.Domain.Orders.ActiveBrokerOrder>? PendingOrders = null);

public static class TradingLimitDay
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    /// <summary>Reset counts on the New York calendar day rather than the server's local or UTC date.</summary>
    public static DateOnly At(DateTimeOffset utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, NewYork).DateTime);

    /// <summary>Resolve local midnight using the exchange timezone, including 23- and 25-hour DST days.</summary>
    public static DateTimeOffset StartUtc(DateOnly date) =>
        new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), NewYork), TimeSpan.Zero);
}
