using TradingFlow.Domain.Backtesting;

namespace TradingFlow.Backtesting;

/// <summary>The application binds this exact committed selection once, independently of an individual child job.</summary>
public sealed record PreparedMarketDataBinding(string RequestSha256, string SnapshotPath,
    string SnapshotSha256, PreparedMarketDataSummary Summary);

/// <summary>Hosts persist one immutable binding; repeated identical binding is allowed and replacement must fail.</summary>
public interface IPreparedMarketDataBindingStore
{
    Task<PreparedMarketDataBinding?> GetAsync(string comparisonId, CancellationToken cancellationToken);
    Task BindAsync(string comparisonId, PreparedMarketDataBinding binding, CancellationToken cancellationToken);
}

internal sealed record PreparedMarketDataSelection(int SchemaVersion, string RequestSha256,
    DateTimeOffset PreparedAtUtc, IReadOnlyList<PreparedMarketDataPart> Parts);

internal sealed record PreparedMarketDataPart(string QueryId,
    TradingFlow.Data.Csv.PreparedMarketDataVersion Version);
