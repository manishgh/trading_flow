namespace TradingFlow.Domain.Market;

/// <summary>One accepted source bar with its independent chart availability clock.</summary>
public sealed record ChartSourceBar(OhlcvBar Bar, string SourceKind, DateTimeOffset? RetrievedAtUtc, DateTimeOffset AvailableAtUtc);

/// <summary>A completed, calendar-classified observation in one continuous price basis.</summary>
public sealed record ChartCompletedBar(
    OhlcvBar Bar, DateTimeOffset EndUtc, DateOnly SessionDate, string Segment, int Continuity,
    string SourceKind, DateTimeOffset? RetrievedAtUtc, DateTimeOffset AvailableAtUtc);

/// <summary>Bounded non-owning projection of an existing live 1m pipeline.</summary>
public sealed record ChartCanonicalSession(
    long OwnershipFence, long SubscriptionGeneration,
    IReadOnlyList<OhlcvBar> Bars,
    IReadOnlySet<DateTimeOffset> NoTradeMinutes,
    bool NeedsRepair,
    DateTimeOffset? NoTradeProofAtUtc);
