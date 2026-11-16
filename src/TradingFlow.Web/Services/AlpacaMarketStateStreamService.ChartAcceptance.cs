using System.Collections.Concurrent;
using System.Text.Json;
using TradingFlow.Alpaca;
using TradingFlow.Web.Services.Charts;

namespace TradingFlow.Web.Services;

public sealed partial class AlpacaMarketStateStreamService
{
    private readonly bool chartAcceptanceEnabled;
    private OwnedLeaseState? diagnosticReadyLease;
    private ChartDiagnosticReservation? chartDiagnostic;

    // Reserve one dedicated scope on the current proven SIP connection before normal subscription replacement.
    internal async Task<bool> ReserveChartDiagnosticAsync(ChartAcceptanceSession session, CancellationToken token)
    {
        await scopeGate.WaitAsync(token);
        try
        {
            var lease = Volatile.Read(ref diagnosticReadyLease);
            if (lease is null || lease.Current.ExpiresAtUtc <= timeProvider.GetUtcNow() || marketState.RecoveryLookbackDays != 45 ||
                chartDiagnostic is not null || !session.IsCapturing || UnionSymbols().Contains(ChartAcceptanceSession.Symbol) ||
                (Volatile.Read(ref chartMembership)?.ContainsKey(ChartAcceptanceSession.Symbol) ?? true))
                return false;
            var fence = lease.Current.FencingToken;
            session.BindFence(fence);
            Volatile.Write(ref chartDiagnostic, new ChartDiagnosticReservation(session, fence));
            if (ReferenceEquals(lease, Volatile.Read(ref diagnosticReadyLease))) return true;
            Volatile.Write(ref chartDiagnostic, null);
            return false;
        }
        finally
        {
            scopeGate.Release();
        }
    }

    // A connection transition ends this scope permanently before any next connection can subscribe it.
    private void RetireChartDiagnostic(long fence)
    {
        Volatile.Write(ref diagnosticReadyLease, null);
        if (Volatile.Read(ref chartDiagnostic) is { } diagnostic && diagnostic.Fence == fence)
        {
            diagnostic.Session.Fail("The fenced SIP connection ended; this chart check cannot resume.");
            diagnostic.Removal.TrySetResult(new("connection-ended"));
        }
    }

    // A terminated diagnostic is removed from the desired union even before the cleanup endpoint queues removal.
    private bool IsRetiredChartScope(Guid scopeId) =>
        Volatile.Read(ref chartDiagnostic) is { } diagnostic &&
        diagnostic.Session.Id == scopeId && !diagnostic.Session.IsCapturing;

    // A normal owner can retain AAPL or a retired transport may need no unsubscribe.
    private void RecordDiagnosticScopeRemoval(Guid scopeId)
    {
        if (Volatile.Read(ref chartDiagnostic) is not { } diagnostic || diagnostic.Session.Id != scopeId)
            return;
        if (!diagnostic.AwaitingRemoval && UnionSymbols().Contains(ChartAcceptanceSession.Symbol))
            diagnostic.Removal.TrySetResult(new("retained-by-normal-owner"));
        else if (!diagnostic.SubscriptionStarted)
            diagnostic.Removal.TrySetResult(new("not-subscribed"));
    }

    // Wait only within the coordinator's independent cleanup budget for the actual wire result.
    internal Task<ChartAcceptanceRemoval> WaitForChartDiagnosticRemovalAsync(Guid scopeId, CancellationToken token) =>
        Volatile.Read(ref chartDiagnostic) is { } diagnostic && diagnostic.Session.Id == scopeId
            ? diagnostic.Removal.Task.WaitAsync(token)
            : Task.FromResult(new ChartAcceptanceRemoval("not-subscribed"));

    // Serialize the decision to remove against a normal owner taking priority before the command goes out.
    private async Task<bool> TryScheduleDiagnosticRemovalAsync(ChartDiagnosticReservation diagnostic, CancellationToken token)
    {
        await scopeGate.WaitAsync(token);
        try
        {
            if (UnionSymbols().Contains(ChartAcceptanceSession.Symbol)) return false;
            diagnostic.AwaitingRemoval = true;
            return true;
        }
        finally
        {
            scopeGate.Release();
        }
    }

    // Only the established connection can start the dedicated subscription and its separately cancelled warm.
    private async Task<bool> SubscribeDiagnosticAsync(ChartDiagnosticReservation diagnostic, IAlpacaMarketStateStreamClient stream,
        Task receive, ConcurrentDictionary<string, long> active, SipFormingBook preview, ConnectionWorkTracker tracker, CancellationToken token)
    {
        var symbol = ChartAcceptanceSession.Symbol;
        await scopeGate.WaitAsync(token);
        long generation;
        try
        {
            if (!diagnostic.Session.IsCapturing || diagnostic.HasNormalOwner ||
                !symbolsByScope.ContainsKey(diagnostic.Session.Id)) return false;
            diagnostic.SubscriptionStarted = true;
            generation = TrackMarketSubscription(symbol, active, preview);
            diagnostic.Session.BindGeneration(diagnostic.Fence, generation);
        }
        finally
        {
            scopeGate.Release();
        }

        // The common command/ack exchange keeps its connection token so a diagnostic timeout cannot poison a normal owner.
        var afterOrdinal = stream.CurrentArrivalOrdinal;
        await stream.SubscribeMarketStateAsync([symbol], token);
        var acknowledgement = await TryAwaitDiagnosticAcknowledgementAsync(diagnostic, stream, receive,
            active.Keys.ToArray(), afterOrdinal, token);
        diagnostic.SubscriptionConfirmed = acknowledgement is not null;
        if (acknowledgement is not { MutationChannelsConfirmed: true } || !diagnostic.Session.HasSubscriptionCoverage())
        {
            diagnostic.Session.Fail("The provider did not verify all AAPL acceptance subscription channels.");
            diagnostic.WarmCompleted.TrySetResult();
            return true;
        }
        var started = tracker.TryRun(symbol, "chart-acceptance-warm", async () =>
        {
            try { await WarmDiagnosticAsync(diagnostic, generation, active, preview, token); }
            finally { diagnostic.WarmCompleted.TrySetResult(); }
        });
        if (!started)
        {
            diagnostic.WarmCompleted.TrySetResult();
            diagnostic.Session.Fail("The exclusive AAPL warm could not start.");
        }
        return true;
    }

    // A dedicated acknowledgement failure ends the diagnostic; a dead shared reader still follows normal recovery.
    private async Task<AlpacaDiagnosticSubscriptionAcknowledgement?> TryAwaitDiagnosticAcknowledgementAsync(ChartDiagnosticReservation diagnostic,
        IAlpacaMarketStateStreamClient stream, Task receive, IReadOnlyCollection<string> expected, long afterOrdinal,
        CancellationToken token)
    {
        try
        {
            var acknowledgement = stream.WaitForDiagnosticSubscriptionAcknowledgementAsync(expected,
                ChartAcceptanceSession.Symbol, afterOrdinal, token);
            if (await Task.WhenAny(acknowledgement, receive) == receive)
            {
                await receive;
                throw new InvalidOperationException("The shared SIP reader ended before the diagnostic acknowledgement.");
            }
            return await acknowledgement;
        }
        catch (Exception exception) when (!receive.IsCompleted && !token.IsCancellationRequested &&
            exception is TimeoutException or InvalidOperationException)
        {
            diagnostic.Session.Fail("The provider did not verify the dedicated AAPL subscription change.");
            logger.LogWarning(exception, "Unverified diagnostic AAPL subscription change. ScopeId={ScopeId}", diagnostic.Session.Id);
            return null;
        }
    }

    // If a dedicated change is unverified, require exact normal membership outside its retired AAPL scope.
    private string[] ExpectedNormalAcknowledgement(ConcurrentDictionary<string, long> active)
    {
        if (Volatile.Read(ref chartDiagnostic) is { HasNormalOwner: false, SubscriptionStarted: true } diagnostic &&
            !diagnostic.Session.IsCapturing)
            return active.Keys.Where(symbol => symbol != ChartAcceptanceSession.Symbol).ToArray();
        return active.Keys.ToArray();
    }

    // A normal owner's takeover or the chart deadline cancels only this acquisition, never the transport.
    private async Task WarmDiagnosticAsync(ChartDiagnosticReservation diagnostic, long generation,
        ConcurrentDictionary<string, long> active, SipFormingBook preview, CancellationToken connectionToken)
    {
        using var warm = CancellationTokenSource.CreateLinkedTokenSource(connectionToken, diagnostic.Session.CaptureToken);
        try
        {
            warm.Token.ThrowIfCancellationRequested();
            if (!await WarmAdditionsAsync([ChartAcceptanceSession.Symbol], warm.Token) ||
                marketState.RequiresRepair(ChartAcceptanceSession.Symbol))
            {
                diagnostic.Session.Fail("The normal AAPL SIP warm did not verify continuous canonical coverage.");
                return;
            }
            if (IsActiveSubscription(ChartAcceptanceSession.Symbol, generation, active) && diagnostic.Session.IsCapturing)
            {
                var now = timeProvider.GetUtcNow();
                diagnostic.Session.MarkWarm(generation, now);
                preview.MarkWarm(ChartAcceptanceSession.Symbol, generation, now);
            }
        }
        catch (OperationCanceledException) when (warm.IsCancellationRequested)
        {
            // The normal owner may start its own warm after this exclusively owned work has exited.
        }
    }

    // Do not restart the diagnostic with a silently repaired gap, while normal ownership keeps its usual path.
    private bool StopDiagnosticRepair(string symbol)
    {
        if (symbol != ChartAcceptanceSession.Symbol || Volatile.Read(ref chartDiagnostic) is not
            { SubscriptionStarted: true, HasNormalOwner: false } diagnostic) return false;
        if (diagnostic.WarmTask.IsCompleted)
            diagnostic.Session.Fail("A gap required canonical repair, so the covered observation ended.");
        return true;
    }

    // Observation stays restricted to the same diagnostic and leased transport.
    private ChartAcceptanceSession? FindDiagnosticSession(long fence, string? symbol) =>
        symbol == ChartAcceptanceSession.Symbol && Volatile.Read(ref chartDiagnostic) is { } diagnostic &&
        diagnostic.Fence == fence && diagnostic.Session.IsCapturing ? diagnostic.Session : null;

    private sealed class DiagnosticReceiptObserver(AlpacaMarketStateStreamService owner, long fence) : IAlpacaMarketDataReceiptObserver
    {
        private ChartDiagnosticReservation? Current => Volatile.Read(ref owner.chartDiagnostic) is { } diagnostic &&
            diagnostic.Fence == fence ? diagnostic : null;

        public bool IsCapturing => Current is { } diagnostic && diagnostic.Session.IsCapturing;

        // Process ephemeral receipt spans in memory only and keep post-capture acknowledgements out of the payload archive.
        public void Observe(JsonElement element, ReadOnlySpan<byte> originalUtf8, DateTimeOffset receivedAtUtc, long ordinal)
        {
            if (Current is not { } diagnostic) return;
            try
            {
                diagnostic.Session.ObserveProvider(fence, element, originalUtf8, receivedAtUtc, ordinal);
            }
            catch (Exception exception)
            {
                diagnostic.Session.Fail("Original AAPL receipts could not be recorded.");
                owner.logger.LogWarning(exception, "Chart acceptance receipt observation failed.");
            }
        }
    }

    private sealed class ChartDiagnosticReservation(ChartAcceptanceSession session, long fence)
    {
        private int handoffProcessed;
        private volatile bool normalOwner;
        private volatile bool subscriptionStarted;
        private volatile bool awaitingRemoval;
        public ChartAcceptanceSession Session { get; } = session;
        public long Fence { get; } = fence;
        public bool HasNormalOwner => normalOwner;
        public bool SubscriptionStarted { get => subscriptionStarted; set => subscriptionStarted = value; }
        public bool SubscriptionConfirmed { get; set; }
        public bool AwaitingRemoval { get => awaitingRemoval; set => awaitingRemoval = value; }
        public TaskCompletionSource<ChartAcceptanceRemoval> Removal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WarmCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task WarmTask => WarmCompleted.Task;

        // End acceptance while preserving the normal owner's newly requested membership.
        public void YieldToNormalOwner()
        {
            normalOwner = true;
            Session.Fail("A normal market-data owner requested AAPL and took priority.");
            if (!awaitingRemoval)
                Removal.TrySetResult(new("retained-by-normal-owner"));
        }

        // The existing subscription may be promoted to normal ownership only once.
        public bool TryFinishHandoff() => Interlocked.Exchange(ref handoffProcessed, 1) == 0;

        // Require both normal membership and mutation-channel removal in the same post-command acknowledgement.
        public void ConfirmRemoval(AlpacaDiagnosticSubscriptionAcknowledgement? acknowledgement)
        {
            Removal.TrySetResult(acknowledgement is { MutationChannelsConfirmed: true } removed
                ? new("acknowledged", removed.ReceivedAtUtc, removed.ArrivalOrdinal)
                : new("provider-removal-unverified"));
            awaitingRemoval = false;
        }
    }
}
