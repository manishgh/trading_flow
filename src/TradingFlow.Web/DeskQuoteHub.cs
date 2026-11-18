using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using TradingFlow.Web.Services;
using TradingFlow.Web.Services.Charts;
using TradingFlow.Web.Services.Desk;

namespace TradingFlow.Web;

/// <summary>
/// Read-only Desk quote socket. It carries display quotes for one server-held Desk view, never orders or strategy
/// work, and uses the existing shared Alpaca SIP connection through <see cref="IDisplayQuoteSubscriptions"/>.
/// Frames are coalesced to at most four per second; a send that cannot complete in five seconds closes the socket.
/// </summary>
[Authorize]
public sealed class DeskQuoteHub(DeskQuoteViews views, IDisplayQuoteSubscriptions subscriptions, TimeProvider clock) : Hub
{
    public const string Path = "/api/v1/desk/quotes/hub";
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SendDeadline = TimeSpan.FromSeconds(5);

    // Admit the socket within its budget and close it if no capability arrives within the bind window.
    public override async Task OnConnectedAsync()
    {
        // Throwing closes the connection and carries this reason to the page; no stream data is read first.
        if (!views.TryConnect(Context.ConnectionId, ChartAccessPolicy.UserId(Context.User!)))
            throw new HubException("Two Desk live-quote connections are already open. Close another Desk tab.");
        _ = CloseIfUnboundAsync(Context);
        await base.OnConnectedAsync();
    }

    // Release the socket budget and detach it from its view.
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        views.Disconnect(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Consumes the one-use capability from the Desk event stream and streams changed quotes for that view until the
    /// view is revoked or expires, or five minutes pass; a fresh capability then comes from the next renewal.
    /// </summary>
    public async IAsyncEnumerable<DeskQuoteFrame> WatchQuotes(string capability,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var user = ChartAccessPolicy.UserId(Context.User!);
        if (String.IsNullOrEmpty(capability) || capability.Length > 128)
            throw new HubException("This quote view is no longer current. Waiting for the next stock list refresh.");
        if (!views.AllowWatchStart(user))
            throw new HubException("Too many live-quote reconnects. Waiting before reconnecting.");
        var viewId = views.TryBind(capability, user, Context.ConnectionId)
            ?? throw new HubException("This quote view is no longer current. Waiting for the next stock list refresh.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Context.ConnectionAborted);
        var watch = new DeskQuoteWatch(viewId);
        var endsAt = clock.GetUtcNow() + DeskQuoteViews.SocketLifetime;
        try
        {
            while (!linked.IsCancellationRequested)
            {
                var now = clock.GetUtcNow();
                var view = views.Read(viewId, Context.ConnectionId);
                if (view is null)
                {
                    yield return watch.End("This quote view closed: the stock list changed, its two-minute lease was not renewed, or you signed out.");
                    yield break;
                }
                if (now >= endsAt)
                {
                    yield return watch.End("The five-minute live-quote socket lifetime ended; reconnecting with the next stock list refresh.");
                    yield break;
                }
                if (watch.Next(view, subscriptions.ReadDisplayQuote, now) is { } frame)
                {
                    // The yield resumes only after SignalR has written the frame; a stuck browser closes the socket.
                    using var stall = clock.CreateTimer(static state => ((HubCallerContext)state!).Abort(), Context,
                        SendDeadline, Timeout.InfiniteTimeSpan);
                    yield return frame;
                }
                await Task.Delay(FrameInterval, clock, linked.Token);
            }
        }
        finally
        {
            views.Unbind(viewId, Context.ConnectionId);
            // A socket left without a watch must bind again within the window or give its budget slot back.
            _ = CloseIfUnboundAsync(Context);
        }
    }

    // A socket that presents no capability (at connect, or after its watch ends) does not keep a budget slot.
    private async Task CloseIfUnboundAsync(HubCallerContext context)
    {
        try { await Task.Delay(DeskQuoteViews.BindWindow, clock, context.ConnectionAborted); }
        catch (OperationCanceledException) { return; }
        if (!views.IsBound(context.ConnectionId)) context.Abort();
    }
}
