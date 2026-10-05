namespace TradingFlow.Mobile.Services;

/// <summary>Only the newest read in the current visible operator/backend session may paint a page.</summary>
public sealed class LatestMarketRead(Func<long> sessionVersion)
{
    private CancellationTokenSource? cancellation;
    private long revision;
    public bool Active { get; private set; }

    public void Activate() { Cancel(); Active = true; }
    public void Deactivate() { Active = false; Cancel(); }
    public void Cancel()
    {
        revision++;
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
    }
    public Read Begin()
    {
        if (!Active) throw new InvalidOperationException("The market page is not visible.");
        Cancel();
        cancellation = new();
        return new(revision, sessionVersion(), cancellation.Token);
    }
    public bool IsCurrent(Read read) => Active && read.Revision == revision && read.Session == sessionVersion() && !read.Token.IsCancellationRequested;
    public readonly record struct Read(long Revision, long Session, CancellationToken Token);
}
