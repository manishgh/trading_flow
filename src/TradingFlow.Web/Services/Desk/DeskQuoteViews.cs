using System.Security.Cryptography;
using System.Text;

namespace TradingFlow.Web.Services.Desk;

/// <summary>
/// What one Desk stock-list renewal returns: a fresh single-use capability when the view needs one, any refusal, and
/// <see cref="Closed"/> when the view was ended by sign-out and its event stream must stop.
/// </summary>
public sealed record DeskQuoteOffer(Guid ViewId, string? Capability, string? Reason, IReadOnlyList<string> Refused, bool Closed = false);

/// <summary>The server-held stock set of one Desk view at one revision, read by its bound quote socket.</summary>
public sealed record DeskQuoteViewState(Guid ViewId, long Revision, IReadOnlyList<string> Symbols, IReadOnlyList<string> Refused);

/// <summary>
/// Display-only quote views for the Desk. The Desk's normal event stream resolves the visible stocks and renews the
/// view; the server keeps the authoritative symbol set. A browser socket may watch a view only by presenting the
/// one-use capability issued for it, as the same signed-in user. A view expires 120 seconds after its last renewal,
/// so a stalled resolver ends quotes with a stated reason instead of silently extending old list membership.
/// Every count is bounded: two views and two sockets per user, 32 of each in the process, 50 stocks per view,
/// one pending capability per view, ten watch starts per user per minute.
/// </summary>
public sealed class DeskQuoteViews(IDisplayQuoteSubscriptions subscriptions, TimeProvider clock, ILogger<DeskQuoteViews> logger)
{
    public const int MaximumSymbols = DeskSources.DisplayLimit;
    public const int MaximumViewsPerUser = 2;
    public const int MaximumViews = 32;
    public const int MaximumDisplayUnion = 500;
    public const int MaximumWatchStartsPerMinute = 10;
    public const int MaximumSymbolLength = 16;
    public static readonly TimeSpan Lease = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan CapabilityLifetime = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan BindWindow = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan SocketLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SignedOutMemory = TimeSpan.FromMinutes(10);

    private readonly object sync = new();
    private readonly Dictionary<Guid, View> views = [];
    private readonly Dictionary<string, string> sockets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset Start, int Count)> watchStarts = new(StringComparer.Ordinal);
    // Views ended by sign-out, remembered past any stream's lifetime so a still-running stream cannot re-create them.
    private readonly Dictionary<Guid, DateTimeOffset> signedOut = [];

    // Mutable server state of one view; only touched under the lock. Each instance owns its own subscription scope,
    // so a delayed release of an expired instance can never remove a re-created view's subscription.
    private sealed class View(Guid id, string user)
    {
        public Guid Id { get; } = id;
        public Guid Scope { get; } = Guid.NewGuid();
        public string User { get; } = user;
        public IReadOnlySet<string> Symbols { get; set; } = new HashSet<string>(StringComparer.Ordinal);
        public string[] Refused { get; set; } = [];
        public long Revision { get; set; }
        public DateTimeOffset ExpiresAtUtc { get; set; }
        public byte[]? CapabilityHash { get; set; }
        public DateTimeOffset CapabilityExpiresAtUtc { get; set; }
        public string? BoundConnection { get; set; }
    }

    /// <summary>
    /// Called by the Desk event stream after it resolves the visible stocks, before the slow full snapshot. Creates the
    /// view on first use (within the per-user and process budgets), replaces its stocks, extends its lease, and issues a
    /// new capability only when no socket is bound and no unexpired capability is pending.
    /// </summary>
    public async Task<DeskQuoteOffer> RenewAsync(Guid viewId, string user, IEnumerable<string> symbols, CancellationToken cancellationToken)
    {
        var requested = symbols.Select(symbol => symbol.Trim().ToUpperInvariant())
            .Where(symbol => symbol.Length is > 0 and <= MaximumSymbolLength)
            .Distinct(StringComparer.Ordinal).Take(MaximumSymbols).ToArray();
        View? view;
        lock (sync)
        {
            ReleaseExpired();
            if (signedOut.ContainsKey(viewId))
                return new(viewId, null, "You signed out; sign in again to stream quotes.", [], Closed: true);
            if (!views.TryGetValue(viewId, out view))
            {
                if (AdmissionRefusal(user) is { } refusal) return new(viewId, null, refusal, []);
                view = new View(viewId, user) { ExpiresAtUtc = clock.GetUtcNow() + Lease };
                views[viewId] = view;
            }
        }
        var admitted = await subscriptions.ReplaceBoundedDisplayQuotesAsync(view.Scope, requested, MaximumDisplayUnion, cancellationToken);
        lock (sync)
        {
            if (!views.TryGetValue(viewId, out var current) || !ReferenceEquals(current, view))
            {
                // Revoked or expired while subscribing: release exactly what this instance just requested.
                Release([view.Scope]);
                return new(viewId, null, "This quote view was closed.", [], Closed: signedOut.ContainsKey(viewId));
            }
            return Refresh(view, requested, admitted);
        }
    }

    // Apply the admitted set, extend the lease and issue a capability if the view has neither a socket nor a pending one.
    private DeskQuoteOffer Refresh(View view, IReadOnlyList<string> requested, IReadOnlySet<string> admitted)
    {
        var now = clock.GetUtcNow();
        var refused = requested.Where(symbol => !admitted.Contains(symbol)).ToArray();
        if (!view.Symbols.SetEquals(admitted) || !view.Refused.SequenceEqual(refused))
        {
            view.Symbols = admitted.ToHashSet(StringComparer.Ordinal);
            view.Refused = refused;
            view.Revision++;
        }
        view.ExpiresAtUtc = now + Lease;
        string? capability = null;
        if (view.BoundConnection is null && (view.CapabilityHash is null || view.CapabilityExpiresAtUtc <= now))
        {
            capability = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            view.CapabilityHash = Hash(capability);
            view.CapabilityExpiresAtUtc = now + CapabilityLifetime;
        }
        var reason = refused.Length == 0 ? null :
            $"{refused.Length} stock(s) have no streamed quote because the shared display quote limit of {MaximumDisplayUnion} symbols is reached. Their snapshot quotes are still shown.";
        return new(view.Id, capability, reason, refused);
    }

    // Views waiting for a socket count against the same budgets as watched ones.
    private string? AdmissionRefusal(string user)
    {
        if (views.Values.Count(view => view.User == user) >= MaximumViewsPerUser)
            return $"Live quotes are already streaming to {MaximumViewsPerUser} Desk views for this user. Close another Desk tab; snapshot quotes continue here.";
        if (views.Count >= MaximumViews)
            return "The server's Desk live-quote capacity is full. Snapshot quotes continue here.";
        return null;
    }

    /// <summary>Ends the view: its socket stops at its next check and its symbols leave the display subscription after the linger window.</summary>
    public Task RevokeAsync(Guid viewId)
    {
        View? view;
        lock (sync)
            if (!views.Remove(viewId, out view)) return Task.CompletedTask;
        return subscriptions.RemoveDisplayQuotesAsync(view.Scope, CancellationToken.None);
    }

    /// <summary>
    /// On sign-out, ends every Desk quote view of this user and keeps their ids closed, so a stream still running in
    /// another tab stops instead of re-creating them. Streams on other signed-in devices reconnect with new views.
    /// </summary>
    public Task RevokeUserAsync(string user)
    {
        View[] removed;
        lock (sync)
        {
            removed = views.Values.Where(view => view.User == user).ToArray();
            var until = clock.GetUtcNow() + SignedOutMemory;
            foreach (var view in removed)
            {
                views.Remove(view.Id);
                signedOut[view.Id] = until;
            }
        }
        return Task.WhenAll(removed.Select(view => subscriptions.RemoveDisplayQuotesAsync(view.Scope, CancellationToken.None)));
    }

    /// <summary>Admits a socket within two per user and 32 in the process.</summary>
    public bool TryConnect(string connection, string user)
    {
        lock (sync)
        {
            if (sockets.Count >= MaximumViews || sockets.Values.Count(owner => owner == user) >= MaximumViewsPerUser) return false;
            sockets[connection] = user;
            return true;
        }
    }

    /// <summary>Releases the socket and detaches it from its view, so the next renewal offers a fresh capability.</summary>
    public void Disconnect(string connection)
    {
        lock (sync)
        {
            sockets.Remove(connection);
            foreach (var view in views.Values.Where(view => view.BoundConnection == connection)) view.BoundConnection = null;
        }
    }

    /// <summary>True while the socket watches a view; an unbound socket is closed after <see cref="BindWindow"/>.</summary>
    public bool IsBound(string connection)
    {
        lock (sync) return views.Values.Any(view => view.BoundConnection == connection);
    }

    /// <summary>Counts a watch start against the user's ten-per-minute budget.</summary>
    public bool AllowWatchStart(string user)
    {
        lock (sync)
        {
            var now = clock.GetUtcNow();
            foreach (var stale in watchStarts.Where(item => now - item.Value.Start >= TimeSpan.FromMinutes(1)).Select(item => item.Key).ToArray())
                watchStarts.Remove(stale);
            if (!watchStarts.TryGetValue(user, out var current))
            {
                if (watchStarts.Count >= 512) return false;
                watchStarts[user] = (now, 1);
                return true;
            }
            if (current.Count >= MaximumWatchStartsPerMinute) return false;
            watchStarts[user] = (current.Start, current.Count + 1);
            return true;
        }
    }

    /// <summary>
    /// Consumes a pending capability for this user's admitted socket and binds the view to it. The capability can
    /// never be used again; an unknown, expired, consumed or other user's capability gets the same refusal.
    /// </summary>
    public Guid? TryBind(string capability, string user, string connection)
    {
        var presented = Hash(capability);
        lock (sync)
        {
            ReleaseExpired();
            var now = clock.GetUtcNow();
            if (!sockets.TryGetValue(connection, out var owner) || owner != user) return null;
            var view = views.Values.FirstOrDefault(item => item.CapabilityHash is { } hash &&
                CryptographicOperations.FixedTimeEquals(hash, presented));
            if (view is null) return null;
            view.CapabilityHash = null;
            if (view.User != user || view.CapabilityExpiresAtUtc <= now || view.BoundConnection is not null) return null;
            foreach (var other in views.Values.Where(item => item.BoundConnection == connection)) other.BoundConnection = null;
            view.BoundConnection = connection;
            return view.Id;
        }
    }

    /// <summary>The view's current stocks for its bound socket, or null once it is revoked, expired or bound elsewhere.</summary>
    public DeskQuoteViewState? Read(Guid viewId, string connection)
    {
        lock (sync)
        {
            ReleaseExpired();
            return views.TryGetValue(viewId, out var view) && view.BoundConnection == connection
                ? new(view.Id, view.Revision, view.Symbols.Order(StringComparer.Ordinal).ToArray(), view.Refused)
                : null;
        }
    }

    /// <summary>Detaches a finished watch; the view itself stays until its stream ends or its lease expires.</summary>
    public void Unbind(Guid viewId, string connection)
    {
        lock (sync)
            if (views.TryGetValue(viewId, out var view) && view.BoundConnection == connection) view.BoundConnection = null;
    }

    private static byte[] Hash(string capability) => SHA256.HashData(Encoding.UTF8.GetBytes(capability));

    // Remove views past their lease under the lock and release their subscriptions without blocking the caller.
    private void ReleaseExpired()
    {
        var now = clock.GetUtcNow();
        var expired = views.Values.Where(view => view.ExpiresAtUtc <= now).ToArray();
        foreach (var view in expired) views.Remove(view.Id);
        if (expired.Length > 0) Release(expired.Select(view => view.Scope).ToArray());
        foreach (var id in signedOut.Where(item => item.Value <= now).Select(item => item.Key).ToArray()) signedOut.Remove(id);
    }

    // Subscription release is asynchronous; failures are logged and the two-minute linger still applies.
    private void Release(IReadOnlyCollection<Guid> scopes) => _ = Task.Run(async () =>
    {
        foreach (var scope in scopes)
        {
            try { await subscriptions.RemoveDisplayQuotesAsync(scope, CancellationToken.None); }
            catch (Exception exception) { logger.LogWarning(exception, "Releasing a Desk quote view's subscription failed. Scope={Scope}", scope); }
        }
    });
}
