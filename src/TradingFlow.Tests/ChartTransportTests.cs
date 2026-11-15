using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TradingFlow.Contracts.V1;
using TradingFlow.Data.Context;
using TradingFlow.Web;
using TradingFlow.Web.Services.Charts;

namespace TradingFlow.Tests;

public sealed class ChartTransportTests
{
    // Cookie-bearing WebSockets must not be accepted from another browser origin.
    [Theory]
    [InlineData("https://desk.example", true)]
    [InlineData("https://desk.example:443", true)]
    [InlineData("http://desk.example", false)]
    [InlineData("https://evil.example", false)]
    [InlineData("https://desk.example.evil.example", false)]
    [InlineData("null", false)]
    [InlineData("", false)]
    public void OriginMustMatchTrustedRequest(string origin, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("desk.example");
        context.Request.Headers.Origin = origin;
        Assert.Equal(expected, ChartEndpoints.HasSameOrigin(context.Request));
    }

    // A separately serialized chunk must never replace half of a Unicode surrogate pair.
    [Fact]
    public void ChunkBoundariesPreserveUnicode()
    {
        var value = new string('x', 7999) + Char.ConvertFromUtf32(0x1F4C8) + new string('y', 8000);
        var chunks = StockChartHub.SplitSnapshot(value);
        var roundTripped = chunks.Select(chunk => JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(chunk)));
        Assert.Equal(value, String.Concat(roundTripped));
        Assert.All(chunks, chunk => Assert.InRange(chunk.Length, 1, 8000));
    }

    // A stolen or stale cursor cannot select another operator, interval or structural context.
    [Fact]
    public void CursorBindsUserSeriesContextAndExpiry()
    {
        var clock = new Clock();
        var cursors = new ChartHistoryCursor(new EphemeralDataProtectionProvider(), clock);
        var key = new ChartSeriesKey("AAPL", "1d", "regular", "year");
        var before = clock.GetUtcNow().AddDays(-20);
        var value = cursors.Create("reader", key, before)!;
        Assert.Equal(before, cursors.Read(value, "reader", key));
        Assert.Throws<ArgumentException>(() => cursors.Read(value, "other", key));
        Assert.Throws<ArgumentException>(() => cursors.Read(value, "reader", key with { Context = "week" }));
        Assert.Throws<ArgumentException>(() => cursors.Read(value + "bad", "reader", key));
        clock.Now = clock.Now.AddMinutes(16);
        Assert.Throws<ArgumentException>(() => cursors.Read(value, "reader", key));
    }

    // A burst of clicks shares a single per-operator budget across tabs and releases connection slots.
    [Fact]
    public void ConnectionAndReadBudgetsRemainBounded()
    {
        var clock = new Clock();
        var limits = new ChartTransportLimits(clock);
        Assert.True(limits.Connect("one", "reader"));
        Assert.True(limits.Connect("two", "reader"));
        Assert.True(limits.Connect("three", "reader"));
        Assert.True(limits.Connect("four", "reader"));
        Assert.False(limits.Connect("five", "reader"));
        limits.Disconnect("one");
        Assert.True(limits.Connect("five", "reader"));
        for (var i = 0; i < 20; i++) Assert.True(limits.Request("reader"));
        Assert.False(limits.Request("reader"));
        Assert.True(limits.Request("another"));
        clock.Now = clock.Now.AddMinutes(1);
        Assert.True(limits.Request("reader"));
    }

    // A configured per-operator cap cannot remove the separate process-wide cap.
    [Fact]
    public void ConfiguredTabLimitsKeepTheProcessBounded()
    {
        var limits = new ChartTransportLimits(new Clock(), Options.Create(new ChartTransportOptions { MaximumConnectionsPerUser = 6 }));
        for (var index = 0; index < 6; index++) Assert.True(limits.Connect($"same-{index}", "operator"));
        Assert.False(limits.Connect("extra-operator-tab", "operator"));
        for (var index = 6; index < 128; index++) Assert.True(limits.Connect($"other-{index}", $"other-{index}"));
        Assert.False(limits.Connect("another", "another"));
        limits.Disconnect("other-127");
        Assert.True(limits.Connect("another", "another"));
    }

    // Full baselines arrive in bounded chunks and reconstruct the exact series, including event evidence.
    [Fact]
    public async Task StreamChunksReconstructAnAtomicSnapshotAndReleaseOnDispose()
    {
        var clock = new Clock();
        var user = User("reader");
        var access = new ChartAccessPolicy(Mock.Of<IDbContextFactory<TradingFlowDbContext>>(), clock);
        access.RecordDisplayed(user, ["AAPL"]);
        var reader = new Mock<IChartSeriesReader>();
        var key = new ChartSeriesKey("AAPL", "1d", "regular", "year");
        var snapshot = Snapshot(key, clock);
        reader.Setup(x => x.ReadAsync(key, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChartSeriesPage(snapshot, clock.Now.AddDays(-252)));
        var context = Context(user);
        var hub = new StockChartHub(reader.Object, access, new ChartTransportLimits(clock),
            new ChartHistoryCursor(new EphemeralDataProtectionProvider(), clock), clock, NullLogger<StockChartHub>.Instance)
        { Context = context.Object };
        var parts = new List<ChartFrame>();
        await using (var stream = hub.Watch(key, CancellationToken.None).GetAsyncEnumerator())
        {
            do { Assert.True(await stream.MoveNextAsync()); parts.Add(stream.Current); }
            while (parts.Count < parts[0].Parts);
        }
        Assert.True(parts.Count > 1);
        Assert.All(parts, frame => Assert.InRange(frame.Payload.Length, 1, 8000));
        Assert.All(parts, frame => Assert.Equal(parts[0].Epoch, frame.Epoch));
        var actual = JsonSerializer.Deserialize<ChartSeriesSnapshot>(String.Concat(parts.Select(x => x.Payload)), StockChartHub.Json)!;
        Assert.Equal(key, actual.SeriesKey);
        Assert.Equal(252, actual.Bars.Count);
        Assert.Equal(snapshot.Markers[0], actual.Markers[0]);
        Assert.NotNull(actual.HistoryCursor);
        Assert.Empty(context.Object.Items);
        reader.Verify(x => x.ReadAsync(key, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    // The optional frame keeps the signed history boundary, completed payload and source-read cadence.
    [Fact]
    public async Task FormingFrameFollowsBaselineAndDuplicateTickOnlyEmitsHeartbeat()
    {
        var clock = new Clock();
        var user = User("reader");
        var access = new ChartAccessPolicy(Mock.Of<IDbContextFactory<TradingFlowDbContext>>(), clock);
        access.RecordDisplayed(user, ["AAPL"]);
        var key = new ChartSeriesKey("AAPL", "5m", "regular", "intraday");
        var options = new ChartWatchOptions(true);
        var snapshot = Snapshot(key, clock);
        var before = clock.Now.AddDays(-2);
        var preview = new ChartFormingCandle("waiting_for_trade", StartUtc: clock.Now, EndUtc: clock.Now.AddMinutes(5));
        var read = new ChartSeriesPage(snapshot, before) { ReadForming = () => preview };
        var reader = new Mock<IChartSeriesReader>(MockBehavior.Strict);
        reader.Setup(x => x.ReadWatchedAsync(key, options, It.IsAny<CancellationToken>())).ReturnsAsync(read);
        reader.Setup(x => x.ReadAsync(key, null, It.IsAny<CancellationToken>())).ReturnsAsync(new ChartSeriesPage(snapshot, before));
        var cursors = new ChartHistoryCursor(new EphemeralDataProtectionProvider(), clock);
        var hub = new StockChartHub(reader.Object, access, new ChartTransportLimits(clock), cursors, clock,
            NullLogger<StockChartHub>.Instance) { Context = Context(user).Object };

        ChartSeriesSnapshot after;
        await using (var optedIn = hub.WatchWithOptions(key, options, default).GetAsyncEnumerator())
        {
            after = await Baseline(optedIn);
            Assert.True(await optedIn.MoveNextAsync());
            var side = optedIn.Current;
            Assert.Equal("preview", side.Kind);
            Assert.Equal(after.Epoch, side.Epoch);
            Assert.Equal(after.Sequence, side.Sequence);
            Assert.Equal(1L, side.PreviewSequence);
            Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(side, StockChartHub.Json).Length, 1, StockChartHub.MaximumPreviewBytes);
            Assert.Equal(preview, JsonSerializer.Deserialize<ChartFormingCandle>(side.Payload, StockChartHub.Json));

            Assert.True(await optedIn.MoveNextAsync());
            Assert.Equal("heartbeat", optedIn.Current.Kind);
            Assert.True(await optedIn.MoveNextAsync());
            Assert.Equal("heartbeat", optedIn.Current.Kind);
            preview = new ChartFormingCandle("not_streaming");
            Assert.True(await optedIn.MoveNextAsync());
            Assert.Equal("preview", optedIn.Current.Kind);
            Assert.Equal(2L, optedIn.Current.PreviewSequence);
            Assert.Equal(after.Sequence, optedIn.Current.Sequence);
        }

        await using var completed = hub.Watch(key, default).GetAsyncEnumerator();
        var baseline = await Baseline(completed);
        Assert.Equal(before, cursors.Read(after.HistoryCursor!, "reader", key));
        Assert.Equal(before, cursors.Read(baseline.HistoryCursor!, "reader", key));
        Assert.Equal(JsonSerializer.Serialize(baseline with { Epoch = null, HistoryCursor = null }, StockChartHub.Json),
            JsonSerializer.Serialize(after with { Epoch = null, HistoryCursor = null }, StockChartHub.Json));
        reader.Verify(x => x.ReadWatchedAsync(key, options, It.IsAny<CancellationToken>()), Times.Exactly(3));
        reader.Verify(x => x.ReadAsync(key, null, It.IsAny<CancellationToken>()), Times.Once);

        // Read only a complete ordered baseline to ensure a preview could not arrive between chunks.
        static async Task<ChartSeriesSnapshot> Baseline(IAsyncEnumerator<ChartFrame> stream)
        {
            var parts = new List<ChartFrame>();
            do
            {
                Assert.True(await stream.MoveNextAsync());
                Assert.Equal("snapshot", stream.Current.Kind);
                parts.Add(stream.Current);
            } while (parts.Count < parts[0].Parts);
            return JsonSerializer.Deserialize<ChartSeriesSnapshot>(String.Concat(parts.Select(frame => frame.Payload)),
                StockChartHub.Json)!;
        }
    }

    // A replacement selection cancels the previous source stream before a different context can arrive.
    [Fact]
    public async Task SelectionReplacementCancelsOldViewAndDoesNotRemoveNewOwner()
    {
        var clock = new Clock();
        var user = User("reader");
        var access = new ChartAccessPolicy(Mock.Of<IDbContextFactory<TradingFlowDbContext>>(), clock);
        access.RecordDisplayed(user, ["AAPL"]);
        var reader = new Mock<IChartSeriesReader>();
        reader.Setup(x => x.ReadAsync(It.IsAny<ChartSeriesKey>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ChartSeriesKey key, DateTimeOffset? _, CancellationToken _) => new ChartSeriesPage(Snapshot(key, clock), null));
        var context = Context(user);
        var hub = new StockChartHub(reader.Object, access, new ChartTransportLimits(clock),
            new ChartHistoryCursor(new EphemeralDataProtectionProvider(), clock), clock, NullLogger<StockChartHub>.Instance)
        { Context = context.Object };
        await using var first = hub.Watch(new("AAPL", "1d", "regular", "year"), CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await first.MoveNextAsync());
        await using var second = hub.Watch(new("AAPL", "1d", "regular", "month"), CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await second.MoveNextAsync());
        await first.DisposeAsync();
        Assert.Single(context.Object.Items);
        await second.DisposeAsync();
        Assert.Empty(context.Object.Items);
    }

    // A slow authorization query for an older selection must not cancel or publish into a newer chart.
    [Fact]
    public async Task OlderAuthorizationReadCannotReplaceNewerSelection()
    {
        var clock = new Clock();
        var user = User("reader");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<TradingFlowDbContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new Mock<IDbContextFactory<TradingFlowDbContext>>();
        factory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(async token => { entered.TrySetResult(); return await never.Task.WaitAsync(token); });
        var access = new ChartAccessPolicy(factory.Object, clock);
        access.RecordDisplayed(user, ["AAPL"]);
        var reader = new Mock<IChartSeriesReader>();
        reader.Setup(x => x.ReadAsync(It.IsAny<ChartSeriesKey>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ChartSeriesKey key, DateTimeOffset? _, CancellationToken _) => new ChartSeriesPage(Snapshot(key, clock), null));
        var context = Context(user);
        var hub = new StockChartHub(reader.Object, access, new ChartTransportLimits(clock),
            new ChartHistoryCursor(new EphemeralDataProtectionProvider(), clock), clock, NullLogger<StockChartHub>.Instance)
        { Context = context.Object };
        await using var old = hub.Watch(new("MSFT", "1d", "regular", "year"), CancellationToken.None).GetAsyncEnumerator();
        var waiting = old.MoveNextAsync().AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using var current = hub.Watch(new("AAPL", "1d", "regular", "year"), CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await current.MoveNextAsync());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Single(context.Object.Items);
        reader.Verify(x => x.ReadAsync(It.Is<ChartSeriesKey>(key => key.Symbol == "MSFT"), null, It.IsAny<CancellationToken>()), Times.Never);
    }

    // Repeated server snapshots from a second Desk tab must not revoke the first tab's recent symbol.
    [Fact]
    public async Task DisplayedMembershipUnionsTabsWithoutLoadingSources()
    {
        var clock = new Clock();
        var user = User("reader");
        var factory = new Mock<IDbContextFactory<TradingFlowDbContext>>(MockBehavior.Strict);
        var access = new ChartAccessPolicy(factory.Object, clock);
        access.RecordDisplayed(user, ["AAPL"]);
        access.RecordDisplayed(user, ["MSFT"]);
        Assert.True(await access.CanReadAsync(user, "AAPL", CancellationToken.None));
        Assert.True(await access.CanReadAsync(user, "MSFT", CancellationToken.None));
        factory.VerifyNoOtherCalls();
    }

    // Test snapshots deliberately contain enough candles to exercise chunking, without market/provider access.
    private static ChartSeriesSnapshot Snapshot(ChartSeriesKey key, Clock clock) => new(key, clock.Now, "history_only", null,
        new("fixture", "sip", "all", "verified", clock.Now, clock.Now, clock.Now, null, true, [], []),
        Enumerable.Range(0, 252).Select(i => new ChartCandle(clock.Now.AddDays(i - 252), clock.Now.AddDays(i - 251),
            DateOnly.FromDateTime(clock.Now.AddDays(i - 252).UtcDateTime), "regular", 100, 102, 99, 101, 10000,
            "fixture", null, clock.Now, clock.Now)).ToArray(), [], [],
        [new("event", "news", clock.Now, clock.Now, "Stored headline", "Original receipt retained", "fixture")]);

    // A stable claims identity matches the production access-policy key.
    private static ClaimsPrincipal User(string id) => new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, id)], "fixture"));

    // Only SignalR context primitives are mocked; the actual hub stream and cursor implementations run.
    private static Mock<HubCallerContext> Context(ClaimsPrincipal user)
    {
        var context = new Mock<HubCallerContext>();
        context.SetupGet(x => x.User).Returns(user);
        context.SetupGet(x => x.ConnectionId).Returns("fixture");
        context.SetupGet(x => x.Items).Returns(new Dictionary<object, object?>());
        return context;
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-11-15T12:00:00Z");
        // Fixed clocks make expiry and cross-view behavior independent from the wall clock.
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
