using TradingFlow.Domain.Market;

namespace TradingFlow.Engine.Abstractions;

/// <summary>Distinguishes a completely queried empty news window from a failed or truncated collection.</summary>
public sealed record CatalystQueryResult(IReadOnlyList<CatalystEvent> Events, bool Complete, string Detail,
    int PagesRead, DateTimeOffset WindowStartUtc, DateTimeOffset WindowEndUtc);

public interface ICatalystEvidenceProvider : ICatalystProvider
{
    Task<CatalystQueryResult> GetCatalystEvidenceAsync(string ticker, DateTimeOffset windowStart,
        DateTimeOffset windowEnd, CancellationToken cancellationToken);
}
