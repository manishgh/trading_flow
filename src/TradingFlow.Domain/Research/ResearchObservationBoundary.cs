namespace TradingFlow.Domain.Research;

/// <summary>Flows the verified research cutoff through nested preparation and parallel catalog reads in one execution.</summary>
public static class ResearchObservationBoundary
{
    private static readonly AsyncLocal<DateTimeOffset?> ActiveCutoff = new();

    public static DateTimeOffset? Current => ActiveCutoff.Value;
    public static DateOnly DocumentedHoldoutStart { get; } = new(2024, 1, 1);
    public static DateOnly DocumentedHoldoutEndExclusive { get; } = new(2025, 11, 1);

    /// <summary>Checks the already documented protected reversal period without opening membership or price observations.</summary>
    public static bool IncludesDocumentedHoldout(DateOnly from, DateOnly toInclusive) =>
        from < DocumentedHoldoutEndExclusive && toInclusive >= DocumentedHoldoutStart;

    /// <summary>Protects the earlier cutoff when a nested preparation uses a narrower development window.</summary>
    public static IDisposable Enter(DateTimeOffset? cutoff)
    {
        var previous = ActiveCutoff.Value;
        if (cutoff is not null && (previous is null || cutoff < previous)) ActiveCutoff.Value = cutoff;
        return new Scope(previous);
    }

    private sealed class Scope(DateTimeOffset? previous) : IDisposable
    {
        /// <summary>Restores the caller's observation boundary even when execution fails or is cancelled.</summary>
        public void Dispose() => ActiveCutoff.Value = previous;
    }
}
