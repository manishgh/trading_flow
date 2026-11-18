using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TradingFlow.Alpaca;
using TradingFlow.Engine.Configuration;
using TradingFlow.Web.Services;
using TradingFlow.Web.Services.Desk;
using TestClock = TradingFlow.Tests.ChartAcceptanceSessionTests.TestClock;

namespace TradingFlow.Tests;

/// <summary>
/// The Desk live-quote path: provider reply attribution on the shared socket, the quote book's generation and clock
/// rules, the server-held view registry (capabilities, leases, budgets) and the socket frame builder.
/// </summary>
public sealed class DeskLiveQuoteTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-17T15:00:00Z");

    private static JsonElement Reply(string bars, string quotes) => JsonSerializer.Deserialize<JsonElement>(
        $$"""{"T":"subscription","bars":[{{bars}}],"updatedBars":[{{bars}}],"trades":[{{bars}}],"statuses":[{{bars}}],"quotes":[{{quotes}}]}""");

    // A quote command answered after an execution command was sent still shows the older execution set; it must not
    // fail the execution wait (before this fix it threw and the shared socket was replaced) nor satisfy it.
    [Fact]
    public async Task QuoteReplyCarryingTheOlderExecutionSetNeitherFailsNorSatisfiesTheExecutionWait()
    {
        using var client = new AlpacaStreamClient(AlpacaOptions.Create(ProductionProfile.Paper));
        client.QueueSubscriptionAcknowledgement(Reply("\"MSFT\"", ""), 1, Start);
        await client.WaitForSubscriptionAcknowledgementAsync(["MSFT"], default);

        client.SetArrivalOrdinal(1);
        client.MarkMarketCommand();
        client.QueueSubscriptionAcknowledgement(Reply("\"MSFT\"", "\"NVDA\""), 2, Start);
        client.QueueSubscriptionAcknowledgement(Reply("\"AAPL\",\"MSFT\"", "\"NVDA\""), 3, Start);
        await client.WaitForSubscriptionAcknowledgementAsync(["AAPL", "MSFT"], default);
        Assert.Equal(0, client.PendingExecutionAcknowledgements);
        Assert.True(await client.WaitForQuoteSubscriptionAsync(1, ["NVDA"], [], TimeSpan.FromSeconds(1), default));
    }

    // When Alpaca answers the execution command first, the quote reply that follows is left queued; the next
    // execution wait skips it because it was read before that command was sent.
    [Fact]
    public async Task LeftoverQuoteReplyIsSkippedByTheNextExecutionWait()
    {
        using var client = new AlpacaStreamClient(AlpacaOptions.Create(ProductionProfile.Paper));
        client.MarkMarketCommand();
        client.QueueSubscriptionAcknowledgement(Reply("\"MSFT\"", ""), 1, Start);
        client.QueueSubscriptionAcknowledgement(Reply("\"MSFT\"", "\"NVDA\""), 2, Start);
        await client.WaitForSubscriptionAcknowledgementAsync(["MSFT"], default);
        Assert.Equal(1, client.PendingExecutionAcknowledgements);

        client.SetArrivalOrdinal(2);
        client.MarkMarketCommand();
        client.QueueSubscriptionAcknowledgement(Reply("\"AAPL\",\"MSFT\"", "\"NVDA\""), 3, Start);
        await client.WaitForSubscriptionAcknowledgementAsync(["AAPL", "MSFT"], default);
        Assert.Equal(0, client.PendingExecutionAcknowledgements);
    }

    // Quote confirmation needs a reply read after the command boundary that lists every added and no removed symbol.
    [Fact]
    public async Task QuoteConfirmationIgnoresEarlierRepliesAndTimesOutHonestly()
    {
        using var client = new AlpacaStreamClient(AlpacaOptions.Create(ProductionProfile.Paper));
        client.QueueSubscriptionAcknowledgement(Reply("", "\"NVDA\",\"AMD\""), 4, Start);
        Assert.False(await client.WaitForQuoteSubscriptionAsync(4, ["NVDA"], [], TimeSpan.FromMilliseconds(50), default));
        Assert.False(await client.WaitForQuoteSubscriptionAsync(3, ["NVDA"], ["AMD"], TimeSpan.FromMilliseconds(50), default));

        var waiting = client.WaitForQuoteSubscriptionAsync(4, ["NVDA"], ["AMD"], TimeSpan.FromSeconds(5), default);
        client.QueueSubscriptionAcknowledgement(Reply("", "\"NVDA\""), 5, Start);
        Assert.True(await waiting);
    }

    // A quote command that never gets a reply leaves nothing behind: a later execution command whose reply leaves the
    // execution channels unchanged (a redundant re-subscribe) still completes its wait.
    [Fact]
    public async Task LostQuoteReplyDoesNotBlockAnUnchangedExecutionReply()
    {
        using var client = new AlpacaStreamClient(AlpacaOptions.Create(ProductionProfile.Paper));
        client.QueueSubscriptionAcknowledgement(Reply("\"MSFT\"", ""), 1, Start);
        await client.WaitForSubscriptionAcknowledgementAsync(["MSFT"], default);
        Assert.False(await client.WaitForQuoteSubscriptionAsync(1, ["NVDA"], [], TimeSpan.FromMilliseconds(30), default));
        client.SetArrivalOrdinal(1);
        client.MarkMarketCommand();
        client.QueueSubscriptionAcknowledgement(Reply("\"MSFT\"", ""), 2, Start);
        await client.WaitForSubscriptionAcknowledgementAsync(["MSFT"], default);
    }

    // A future provider clock is refused before storage; a newer subscription replaces an older one's quote.
    [Fact]
    public void QuoteBookRefusesFutureClocksAndRanksSubscriptionsBeforeProviderTime()
    {
        var book = new LiveQuoteBook();
        Assert.False(book.Update(new AlpacaStreamQuote("NVDA", 1m, 2m, 1m, 1m, Start.AddSeconds(6)), 1, 1, Start));
        Assert.Null(book.Find("NVDA"));
        Assert.True(book.Update(new AlpacaStreamQuote("NVDA", 10m, 11m, 1m, 1m, Start), 1, 1, Start));
        book.Update(new AlpacaStreamQuote("NVDA", 20m, 21m, 1m, 1m, Start.AddSeconds(-30)), 1, 2, Start);
        Assert.Equal(20.5m, book.FindEntry("NVDA")!.Quote.MidPrice);
        Assert.Equal(2, book.FindEntry("NVDA")!.SubscriptionGeneration);
        book.Update(new AlpacaStreamQuote("NVDA", 30m, 31m, 1m, 1m, Start.AddSeconds(1)), 1, 1, Start.AddSeconds(1));
        Assert.Equal(20.5m, book.Find("NVDA")!.MidPrice);
    }

    // A capability works once, only for its user's admitted socket; a consumed or foreign one gets the same refusal.
    [Fact]
    public async Task CapabilityIsSingleUseAndBoundToItsUserAndSocket()
    {
        var (views, subscriptions, _) = Create();
        var offer = await views.RenewAsync(Guid.NewGuid(), "alice", ["nvda", "MSFT"], default);
        Assert.NotNull(offer.Capability);
        Assert.Equal(new[] { "MSFT", "NVDA" }, Assert.Single(subscriptions.Scopes.Values).Order(StringComparer.Ordinal));
        Assert.True(views.TryConnect("socket-b", "bob"));
        Assert.Null(views.TryBind(offer.Capability!, "bob", "socket-b"));
        Assert.True(views.TryConnect("socket-a", "alice"));
        Assert.Null(views.TryBind(offer.Capability!, "alice", "socket-a"));

        var fresh = await views.RenewAsync(offer.ViewId, "alice", ["NVDA", "MSFT"], default);
        Assert.NotNull(fresh.Capability);
        Assert.Null(views.TryBind(fresh.Capability!, "alice", "unadmitted-socket"));
        // An unconsumed, unexpired capability is not duplicated by the next renewal.
        Assert.Null((await views.RenewAsync(offer.ViewId, "alice", ["NVDA", "MSFT"], default)).Capability);
        Assert.Equal(offer.ViewId, views.TryBind(fresh.Capability!, "alice", "socket-a"));
    }

    // A bound view gets no new capability; the socket sees membership changes and loses the view on revoke.
    [Fact]
    public async Task BoundViewFollowsServerMembershipAndEndsOnRevoke()
    {
        var (views, subscriptions, _) = Create();
        var id = Guid.NewGuid();
        var offer = await views.RenewAsync(id, "alice", ["NVDA", "MSFT"], default);
        views.TryConnect("socket-a", "alice");
        Assert.Equal(id, views.TryBind(offer.Capability!, "alice", "socket-a"));
        Assert.True(views.IsBound("socket-a"));
        Assert.Null((await views.RenewAsync(id, "alice", ["NVDA"], default)).Capability);
        var state = views.Read(id, "socket-a")!;
        Assert.Equal(new[] { "NVDA" }, state.Symbols);
        Assert.Null(views.Read(id, "other-socket"));

        await views.RevokeAsync(id);
        Assert.Null(views.Read(id, "socket-a"));
        Assert.Empty(subscriptions.Scopes);
    }

    // Without a successful renewal for 120 seconds the view expires and its subscription is released.
    [Fact]
    public async Task UnrenewedViewExpiresAfterItsLease()
    {
        var (views, subscriptions, clock) = Create();
        var id = Guid.NewGuid();
        var offer = await views.RenewAsync(id, "alice", ["NVDA"], default);
        views.TryConnect("socket-a", "alice");
        views.TryBind(offer.Capability!, "alice", "socket-a");
        clock.UtcNow += DeskQuoteViews.Lease - TimeSpan.FromSeconds(1);
        Assert.NotNull(views.Read(id, "socket-a"));
        clock.UtcNow += TimeSpan.FromSeconds(1);
        Assert.Null(views.Read(id, "socket-a"));
        await subscriptions.Released.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(subscriptions.Scopes);
    }

    // A view re-created by a fresh renewal after expiry owns a new subscription scope, so the delayed release of the
    // expired instance cannot remove the new one's stocks.
    [Fact]
    public async Task ReleasingAnExpiredViewNeverRemovesItsReplacement()
    {
        var (views, subscriptions, clock) = Create();
        var id = Guid.NewGuid();
        await views.RenewAsync(id, "alice", ["NVDA"], default);
        clock.UtcNow += DeskQuoteViews.Lease;
        Assert.NotNull((await views.RenewAsync(id, "alice", ["NVDA"], default)).Capability);
        await subscriptions.Released.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "NVDA" }, Assert.Single(subscriptions.Scopes.Values));
    }

    // Two views and two sockets per user, 32 of each in the process, and an expired pending capability is replaced.
    [Fact]
    public async Task ViewSocketAndCapabilityBudgetsAreEnforced()
    {
        var (views, _, clock) = Create();
        var first = await views.RenewAsync(Guid.NewGuid(), "alice", ["NVDA"], default);
        await views.RenewAsync(Guid.NewGuid(), "alice", ["NVDA"], default);
        var third = await views.RenewAsync(Guid.NewGuid(), "alice", ["NVDA"], default);
        Assert.Null(third.Capability);
        Assert.Contains("Close another Desk tab", third.Reason);
        for (var user = 0; user < 15; user++)
        {
            await views.RenewAsync(Guid.NewGuid(), "user" + user, ["NVDA"], default);
            await views.RenewAsync(Guid.NewGuid(), "user" + user, ["NVDA"], default);
        }
        Assert.Contains("capacity is full", (await views.RenewAsync(Guid.NewGuid(), "carol", ["NVDA"], default)).Reason);

        Assert.True(views.TryConnect("s1", "alice"));
        Assert.True(views.TryConnect("s2", "alice"));
        Assert.False(views.TryConnect("s3", "alice"));
        for (var socket = 0; socket < 30; socket++) Assert.True(views.TryConnect("x" + socket, "user" + socket));
        Assert.False(views.TryConnect("x30", "dave"));

        Assert.Null((await views.RenewAsync(first.ViewId, "alice", ["NVDA"], default)).Capability);
        clock.UtcNow += DeskQuoteViews.CapabilityLifetime;
        var replaced = await views.RenewAsync(first.ViewId, "alice", ["NVDA"], default);
        Assert.NotNull(replaced.Capability);
        Assert.Null(views.TryBind(first.Capability!, "alice", "s1"));
        Assert.Equal(first.ViewId, views.TryBind(replaced.Capability!, "alice", "s1"));
    }

    // Symbols over the shared display limit are refused with a stated reason; sign-out ends all of that user's views
    // for good and leaves other users' views alone.
    [Fact]
    public async Task RefusedSymbolsAreReportedAndSignOutClosesTheUsersViews()
    {
        var (views, subscriptions, _) = Create(maximumAdmitted: 1);
        var offer = await views.RenewAsync(Guid.NewGuid(), "alice", ["NVDA", "AMD"], default);
        Assert.Equal(new[] { "NVDA" }, offer.Refused);
        Assert.Contains("500", offer.Reason);
        var second = await views.RenewAsync(Guid.NewGuid(), "alice", ["AMD"], default);
        await views.RenewAsync(Guid.NewGuid(), "bob", ["TSLA"], default);

        await views.RevokeUserAsync("alice");
        Assert.Equal(new[] { "TSLA" }, Assert.Single(subscriptions.Scopes.Values));

        // A stream still running for a signed-out view cannot re-create it or re-subscribe its stocks.
        foreach (var id in new[] { offer.ViewId, second.ViewId })
        {
            var again = await views.RenewAsync(id, "alice", ["NVDA", "AMD"], default);
            Assert.True(again.Closed);
            Assert.Null(again.Capability);
        }
        Assert.Single(subscriptions.Scopes);
    }

    // Watch starts are limited to ten per user per minute; a closed socket frees its slot and its view's binding.
    [Fact]
    public async Task WatchStartLimitAndDisconnectCleanup()
    {
        var (views, _, clock) = Create();
        for (var start = 0; start < DeskQuoteViews.MaximumWatchStartsPerMinute; start++) Assert.True(views.AllowWatchStart("alice"));
        Assert.False(views.AllowWatchStart("alice"));
        Assert.True(views.AllowWatchStart("bob"));
        clock.UtcNow += TimeSpan.FromMinutes(1);
        Assert.True(views.AllowWatchStart("alice"));

        var offer = await views.RenewAsync(Guid.NewGuid(), "alice", ["NVDA"], default);
        Assert.True(views.TryConnect("s1", "alice"));
        Assert.True(views.TryConnect("s2", "alice"));
        Assert.Equal(offer.ViewId, views.TryBind(offer.Capability!, "alice", "s1"));
        views.Disconnect("s1");
        Assert.False(views.IsBound("s1"));
        Assert.True(views.TryConnect("s3", "alice"));
        Assert.NotNull((await views.RenewAsync(offer.ViewId, "alice", ["NVDA"], default)).Capability);
    }

    // Frames carry only changes, prices only for an acknowledged subscription and never a future provider clock.
    [Fact]
    public void WatchSendsOnlyChangesAndWithholdsUnacknowledgedOrFuturePrices()
    {
        var id = Guid.NewGuid();
        var watch = new DeskQuoteWatch(id);
        var quote = new LiveQuoteEntry(new AlpacaLatestQuote("NVDA", 10m, 11m, 1m, 2m, Start), 3, 7, Start.AddMilliseconds(40));
        var readings = new Dictionary<string, DisplayQuoteReading>
        {
            ["NVDA"] = new(DisplayQuoteState.Acknowledged, quote),
            ["AMD"] = new(DisplayQuoteState.Pending, null)
        };
        var view = new DeskQuoteViewState(id, 1, ["AMD", "NVDA"], ["TSLA"]);
        var first = watch.Next(view, symbol => readings[symbol], Start)!;
        Assert.Equal("connected", first.Upstream);
        Assert.Equal(new[] { "AMD", "NVDA", "TSLA" }, first.Quotes.Select(item => item.Symbol).Order(StringComparer.Ordinal));
        var nvda = first.Quotes.Single(item => item.Symbol == "NVDA");
        Assert.Equal((10.5m, "acknowledged", "sip"), (nvda.MidPrice!.Value, nvda.State, nvda.Feed!));
        Assert.Equal("refused", first.Quotes.Single(item => item.Symbol == "TSLA").State);
        Assert.Null(first.Quotes.Single(item => item.Symbol == "AMD").MidPrice);
        Assert.Null(watch.Next(view, symbol => readings[symbol], Start));

        readings["NVDA"] = new(DisplayQuoteState.Acknowledged, quote with { Quote = quote.Quote with { Timestamp = Start.AddSeconds(30) } });
        var future = watch.Next(view, symbol => readings[symbol], Start)!;
        Assert.Null(Assert.Single(future.Quotes).MidPrice);

        var removed = watch.Next(view with { Revision = 2, Symbols = ["NVDA"], Refused = [] }, symbol => readings[symbol], Start)!;
        Assert.Equal(new[] { "AMD", "TSLA" }, removed.Removed.Order(StringComparer.Ordinal));
        readings["NVDA"] = new(DisplayQuoteState.UpstreamUnavailable, null);
        Assert.Equal("unavailable", watch.Next(view with { Revision = 2, Symbols = ["NVDA"], Refused = [] }, symbol => readings[symbol], Start)!.Upstream);
        Assert.Equal("ended", watch.End("lease").Kind);
    }

    // A full view's worst-case frame stays within the 32 KiB frame bound.
    [Fact]
    public void FullViewFrameStaysWithinItsSizeBound()
    {
        var symbols = Enumerable.Range(0, DeskQuoteViews.MaximumSymbols).Select(index => ("SYMBOLNAME" + index).PadRight(16, 'X')).ToArray();
        var item = new DeskQuoteItem("", "upstream_unavailable", 123456.1234m, 123456.1234m, 123456.1234m, 999999999m, 999999999m,
            Start, Start, "overnight");
        var frame = new DeskQuoteFrame("quotes", Guid.NewGuid(), Int64.MaxValue, Int64.MaxValue, "unavailable",
            symbols.Select(symbol => item with { Symbol = symbol }).ToArray(), symbols, new string('x', 200));
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(frame, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length <= 32 * 1024);
    }

    private static (DeskQuoteViews Views, RecordingSubscriptions Subscriptions, TestClock Clock) Create(int maximumAdmitted = Int32.MaxValue)
    {
        var clock = new TestClock(Start);
        var subscriptions = new RecordingSubscriptions(maximumAdmitted);
        return (new DeskQuoteViews(subscriptions, clock, NullLogger<DeskQuoteViews>.Instance), subscriptions, clock);
    }

    // Records each view's admitted symbols; admits at most the configured number per call, in symbol order.
    private sealed class RecordingSubscriptions(int maximumAdmitted) : IDisplayQuoteSubscriptions
    {
        public System.Collections.Concurrent.ConcurrentDictionary<Guid, IReadOnlySet<string>> Scopes { get; } = new();
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ReplaceDisplayQuotesAsync(Guid scopeId, IReadOnlyCollection<string> symbols, CancellationToken token)
        {
            Scopes[scopeId] = symbols.ToHashSet();
            return Task.CompletedTask;
        }

        public Task RemoveDisplayQuotesAsync(Guid scopeId, CancellationToken token)
        {
            Scopes.TryRemove(scopeId, out _);
            Released.TrySetResult();
            return Task.CompletedTask;
        }

        public Task<IReadOnlySet<string>> ReplaceBoundedDisplayQuotesAsync(Guid scopeId, IReadOnlyCollection<string> symbols,
            int maximumUnion, CancellationToken token)
        {
            Assert.Equal(DeskQuoteViews.MaximumDisplayUnion, maximumUnion);
            IReadOnlySet<string> admitted = symbols.Order(StringComparer.Ordinal).Take(maximumAdmitted).ToHashSet();
            Scopes[scopeId] = admitted;
            return Task.FromResult(admitted);
        }

        public DisplayQuoteReading ReadDisplayQuote(string symbol) => new(DisplayQuoteState.UpstreamUnavailable, null);
    }
}
