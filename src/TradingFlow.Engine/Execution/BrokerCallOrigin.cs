namespace TradingFlow.Engine.Execution;

/// <summary>
/// Names the background loop that is making broker calls on the current async flow, so an archived broker-account read can
/// record which loop asked for it. It only labels reads; no decision uses it. Reads outside a named loop stay unlabelled.
/// </summary>
public static class BrokerCallOrigin
{
    public const string OrderRecovery = "order-recovery";
    public const string OrderSynchronization = "order-synchronization";

    private static readonly AsyncLocal<string?> Ambient = new();

    /// <summary>The label of the loop making calls on this async flow, or null when none was set.</summary>
    public static string? Current => Ambient.Value;

    /// <summary>Labels broker calls awaited below this point until the returned scope is disposed.</summary>
    public static IDisposable Begin(string origin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        var previous = Ambient.Value;
        Ambient.Value = origin;
        return new Scope(previous);
    }

    // Restores the label that was active before the scope began.
    private sealed class Scope(string? previous) : IDisposable
    {
        /// <inheritdoc />
        public void Dispose() => Ambient.Value = previous;
    }
}
