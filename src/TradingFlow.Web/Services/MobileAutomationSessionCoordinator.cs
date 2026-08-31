namespace TradingFlow.Web.Services;

/// <summary>
/// Owns in-process automation leases so only one active session can control a ticker.
/// Persistence records history; this coordinator owns the currently running task and
/// its cancellation lifetime.
/// </summary>
internal sealed class MobileAutomationSessionCoordinator
{
    private readonly object sync = new();
    private readonly Dictionary<Guid, SessionLease> leasesBySession = new();
    private readonly Dictionary<string, Guid> sessionsByTicker = new(StringComparer.OrdinalIgnoreCase);

    public CancellationTokenSource Reserve(Guid sessionId, string ticker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);
        var normalizedTicker = ticker.Trim().ToUpperInvariant();

        lock (sync)
        {
            if (leasesBySession.ContainsKey(sessionId))
            {
                throw new InvalidOperationException($"Automation session {sessionId} is already active.");
            }

            if (sessionsByTicker.ContainsKey(normalizedTicker))
            {
                throw new InvalidOperationException(
                    $"An active mobile automation session already exists for {normalizedTicker}.");
            }

            var cancellation = new CancellationTokenSource();
            leasesBySession.Add(sessionId, new SessionLease(normalizedTicker, cancellation));
            sessionsByTicker.Add(normalizedTicker, sessionId);
            return cancellation;
        }
    }

    public bool TryCancel(Guid sessionId)
    {
        lock (sync)
        {
            if (!leasesBySession.TryGetValue(sessionId, out var lease))
            {
                return false;
            }

            // Release also holds this lock, so the source cannot be disposed between
            // lookup and cancellation.
            lease.Cancellation.Cancel();
            return true;
        }
    }

    public void Release(Guid sessionId)
    {
        CancellationTokenSource? cancellation = null;
        lock (sync)
        {
            if (leasesBySession.Remove(sessionId, out var lease))
            {
                sessionsByTicker.Remove(lease.Ticker);
                cancellation = lease.Cancellation;
            }
        }

        cancellation?.Dispose();
    }

    internal bool IsReserved(Guid sessionId)
    {
        lock (sync)
        {
            return leasesBySession.ContainsKey(sessionId);
        }
    }

    private sealed record SessionLease(string Ticker, CancellationTokenSource Cancellation);
}
