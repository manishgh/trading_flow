using TradingFlow.Domain.Market;

namespace TradingFlow.Web.Services;

/// <summary>Observation about a provider attempt; a saved article never makes this attempt successful.</summary>
public sealed record NewsSourceReading(string Source, string State, DateTimeOffset? AttemptedAtUtc,
    int ReceivedCount, string Detail, DateTimeOffset? WindowStartUtc = null, DateTimeOffset? WindowEndUtc = null);

public sealed record NewsRefreshStatus(bool Running, string State, DateTimeOffset? CompletedAtUtc,
    IReadOnlyList<NewsSourceReading> Sources, string Detail);

public sealed record NewsWindowStatus(string Heading, string Detail, NewsRefreshStatus Refresh);

internal sealed record NewsCollection(IReadOnlyList<CatalystEvent> Events, NewsSourceReading Reading);
