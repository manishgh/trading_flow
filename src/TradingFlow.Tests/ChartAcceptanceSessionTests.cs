using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using TradingFlow.Alpaca;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Storage;
using TradingFlow.Web;
using TradingFlow.Web.Services.Charts;

namespace TradingFlow.Tests;

public sealed class ChartAcceptanceSessionTests
{
    internal const string Subscription =
        """{"T":"subscription","trades":["AAPL"],"corrections":["AAPL"],"cancelErrors":["AAPL"],"bars":["AAPL"],"updatedBars":["AAPL"]}""";
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-16T14:31:10Z");
    private static readonly ChartSeriesKey Key = new("AAPL", "1m", "regular", "intraday");

    // Verify a queued Desk quote acknowledgement cannot satisfy the new diagnostic scope command.
    [Fact]
    public async Task DiagnosticAcknowledgementUsesTheCausalReplyAndItsOwnMutationChannels()
    {
        using var socket = new AlpacaStreamClient(AlpacaOptions.Create(ProductionProfile.Paper));
        var before = Start.AddSeconds(-1);
        socket.QueueSubscriptionAcknowledgement(JsonSerializer.Deserialize<JsonElement>(
            """{"T":"subscription","quotes":["MSFT"],"bars":[],"trades":[],"updatedBars":[],"statuses":[],"corrections":[],"cancelErrors":[]}"""), 6, before);
        socket.QueueSubscriptionAcknowledgement(JsonSerializer.Deserialize<JsonElement>(
            """{"T":"subscription","quotes":["NVDA"],"bars":[],"trades":[],"updatedBars":[],"statuses":[],"corrections":[],"cancelErrors":[]}"""), 8, Start);
        var joined = Subscription.Replace("\"updatedBars\":[\"AAPL\"]", "\"updatedBars\":[\"AAPL\"],\"statuses\":[\"AAPL\"]");
        socket.QueueSubscriptionAcknowledgement(JsonSerializer.Deserialize<JsonElement>(joined), 9, Start.AddSeconds(1));
        var subscribe = await socket.WaitForDiagnosticSubscriptionAcknowledgementAsync(["AAPL"], "AAPL", 7, default);
        Assert.Equal(9, subscribe.ArrivalOrdinal);
        Assert.Equal(Start.AddSeconds(1), subscribe.ReceivedAtUtc);
        Assert.True(subscribe.MutationChannelsConfirmed);

        socket.QueueSubscriptionAcknowledgement(JsonSerializer.Deserialize<JsonElement>(
            """{"T":"subscription","bars":[],"trades":[],"updatedBars":[],"statuses":[],"corrections":["AAPL"],"cancelErrors":[]}"""),
            11, Start.AddSeconds(2));
        socket.QueueSubscriptionAcknowledgement(JsonSerializer.Deserialize<JsonElement>(
            """{"T":"subscription","bars":[],"trades":[],"updatedBars":[],"statuses":[],"corrections":[],"cancelErrors":[]}"""),
            12, Start.AddSeconds(3));
        var removal = await socket.WaitForDiagnosticSubscriptionAcknowledgementAsync([], "AAPL", 10, default);
        Assert.Equal(11, removal.ArrivalOrdinal);
        Assert.False(removal.MutationChannelsConfirmed);
    }

    // Scan a received batch while it is valid and prove the archive has each original relevant span.
    [Fact]
    public async Task ExactOriginalProviderSpansAndOrdinalsExcludeOtherPricesAndControlPayloads()
    {
        var archive = new MemoryArchive();
        var clock = new TestClock(Start);
        var session = NewSession(clock, archive);
        const string trade = """{ "T":"t","S":"AAPL","i":9,"p":100.000,"t":"2026-11-16T14:31:11.123456789Z","s":1 }""";
        const string correction = """{"T":"c","S":"AAPL","oi":9,"ci":10,"cp":101.0}""";
        var receipt = Start.AddSeconds(2);
        ReadExactBatch(session, receipt, 29,
            $$"""[ {{Subscription}} , {{trade}}, {"T":"t","S":"MSFT","price":1000}, {"T":"success","key":"ignored"}, {{correction}} ]""");
        session.End("stopped", "test finished");
        var chunks = await session.Archive.DrainAsync(default);

        var chunk = Assert.Single(archive.Entries);
        Assert.Equal(receipt, chunk.Request.ReceivedAtUtc);
        Assert.Equal(Assert.Single(chunks).Sha256, chunk.Hash);
        var entries = Decode(chunk.Payload);
        Assert.Equal([Subscription, trade, correction], entries.Select(entry => entry.Payload));
        Assert.Equal([29L, 30L, 33L], entries.Select(entry => entry.Header.GetProperty("ordinal").GetInt64()));
        Assert.Equal(2, entries[^1].Header.GetProperty("ignoredBefore").GetInt64());
        Assert.All(entries, entry =>
        {
            Assert.Equal(receipt, entry.Header.GetProperty("receivedAtUtc").GetDateTimeOffset());
            Assert.Equal(7, entry.Header.GetProperty("fence").GetInt64());
            Assert.Equal(12, entry.Header.GetProperty("generation").GetInt64());
        });
    }

    // Scope the one-use capability to the owner, the approved series, one connection and one epoch.
    [Fact]
    public async Task CorrelationOnlyAttachesOnceAndNeverCapturesAnotherWatch()
    {
        var archive = new MemoryArchive();
        var session = NewSession(new(Start), archive);
        var correlation = session.StartResponse.Correlation;
        Assert.False(session.TryAttachWatch("other-admin", Key, correlation, "second-tab", "wrong"));
        Assert.False(session.TryAttachWatch("admin", Key with { Interval = "5m" }, correlation, "tab", "wrong"));
        Assert.True(session.TryAttachWatch("admin", Key, correlation, "tab", "epoch"));
        Assert.False(session.TryAttachWatch("admin", Key, correlation, "second-tab", "another"));

        session.ObserveChart("second-tab", new("preview", Key, "epoch", 1, 0, 1, "excluded", 1));
        session.ObserveChart("tab", new("preview", Key, "new-epoch", 1, 0, 1, "excluded", 1));
        session.ObserveChart("tab", new("preview", Key, "epoch", 1, 0, 1, "actual payload", 1));
        session.DetachWatch("second-tab", "epoch");
        Assert.False(session.Ended.IsCompleted);
        session.DetachWatch("tab", "epoch");
        Assert.Equal("incomplete", (await session.Ended).State);
        await session.Archive.DrainAsync(default);
        var captured = Assert.Single(Decode(Assert.Single(archive.Entries).Payload));
        Assert.Equal("actual payload", captured.Payload);
        Assert.Equal("epoch", captured.Header.GetProperty("epoch").GetString());
    }

    // Reject overflow before copying and do not leave a window for further receipt offers.
    [Fact]
    public async Task QueueEventAndByteLimitsEndCaptureBeforeAdmittingTheRefusedPayload()
    {
        var archive = new MemoryArchive();
        var session = NewSession(new(Start), archive);
        for (var index = 0; index < ChartAcceptanceArchive.MaximumQueuedEvents; index++)
            Assert.True(session.Archive.Offer("{}", "payload"u8, null, Start));
        Assert.False(session.Archive.Offer("{}", "overflow"u8, null, Start));
        Assert.Equal("incomplete", (await session.Ended).State);
        Assert.False(session.Archive.Offer("{}", "after-overflow"u8, null, Start));
        await session.Archive.DrainAsync(default);
        Assert.Equal(ChartAcceptanceArchive.MaximumQueuedEvents, Decode(Assert.Single(archive.Entries).Payload).Count);

        var other = NewSession(new(Start), new());
        var oversized = new byte[ChartAcceptanceArchive.ChunkBytes];
        Assert.False(other.Archive.Offer("{}", oversized, null, Start));
        Assert.Equal("incomplete", (await other.Ended).State);
        Assert.Empty(await other.Archive.DrainAsync(default));
    }

    // A failed archive starts no continuation and records the first failure immediately.
    [Fact]
    public async Task WriterFailureEndsCaptureAndRejectsNewEntries()
    {
        var archive = new MemoryArchive { FailWrites = true };
        var session = NewSession(new(Start), archive);
        var payload = new byte[600_000];
        Assert.True(session.Archive.Offer("{}", payload, null, Start));
        var writing = session.Archive.DrainAsync(default);
        Assert.True(session.Archive.Offer("{}", payload, null, Start.AddSeconds(1)));
        await Assert.ThrowsAsync<IOException>(async () => await writing);
        Assert.Equal("incomplete", (await session.Ended).State);
        Assert.False(session.Archive.Offer("{}", "after-error"u8, null, Start));
    }

    // Match the emitted boundary to the processor's disposition even if its continuation runs after the hub.
    [Fact]
    public async Task CompletedBoundaryRequiresTheExactAcceptedCanonicalReceipt()
    {
        var clock = new TestClock(Start);
        var session = NewSession(clock, new());
        Assert.True(session.TryAttachWatch("admin", Key, session.StartResponse.Correlation, "tab", "epoch"));
        ReadExactBatch(session, Start, 1, $"[{Subscription}]");
        session.MarkWarm(12, Start.AddSeconds(2));
        var minute = DateTimeOffset.Parse("2026-11-16T14:32:00Z");
        clock.UtcNow = minute.AddSeconds(20);
        session.ObserveChart("tab", new("preview", Key, "epoch", 2, 0, 1, "preview", 3),
            forming: new("forming", StartUtc: minute, EndUtc: minute.AddMinutes(1), EligibleVolume: 10));
        clock.UtcNow = minute.AddMinutes(1).AddSeconds(1);
        const string raw = """{"T":"b","S":"AAPL","t":"2026-11-16T14:32:00Z","o":100,"h":103,"l":99,"c":102,"v":20}""";
        ReadExactBatch(session, clock.UtcNow, 2, $"[{raw}]");
        var rejected = Assert.IsType<ChartAcceptanceRawBarReceipt>(session.ClaimRawBar(7, 12, 2));
        var bar = new OhlcvBar("AAPL", minute, "1m", 100, 103, 99, 102, 20, "sip", "all", clock.UtcNow);
        var canonical = new MarketBarEvent("alpaca", "sip", bar, MarketBarEventKind.CompletedBar, clock.UtcNow, 7);
        var visible = new ChartCandle(minute, minute.AddMinutes(1), DateOnly.FromDateTime(minute.UtcDateTime), "regular",
            100, 103, 99, 102, 20, "canonical_stream", clock.UtcNow, null, clock.UtcNow);
        var source = new ChartSeriesEvidence("alpaca", "sip", "all", "live", null, null, null, "7:12", false, [], []);
        var snapshot = new ChartSeriesSnapshot(Key, clock.UtcNow, "live", null, source, [visible], [], [], [], Sequence: 3);
        session.ObserveChart("tab", new("snapshot", Key, "epoch", 3, 0, 1, "snapshot"), snapshot);
        session.ObserveCanonical(rejected, canonical, nameof(MarketBarDisposition.ConflictingDuplicate), clock.UtcNow);
        Assert.Empty(session.BuildResult("stopped", "test", new("not-subscribed"), []).MatchedMinutes);
        ReadExactBatch(session, clock.UtcNow, 3, $"[{raw}]");
        var accepted = Assert.IsType<ChartAcceptanceRawBarReceipt>(session.ClaimRawBar(7, 12, 3));
        session.ObserveCanonical(accepted, canonical, nameof(MarketBarDisposition.Accepted), clock.UtcNow);
        var matched = Assert.Single(session.BuildResult("stopped", "test", new("not-subscribed"), []).MatchedMinutes);
        Assert.Equal((3L, 3L, 3L), (matched.PreviewSequence, matched.CompletedSequence, matched.CanonicalReceiptOrdinal));
        clock.UtcNow = session.DeadlineUtc;
        Assert.False(session.IsCapturing);
        session.End("expired", "test deadline");
        await session.Archive.DrainAsync(default);
    }

    // A normalized symbol or parser result cannot stand in for a raw AAPL b/u span refused by the observer.
    [Fact]
    public async Task AnIgnoredRawEventCannotBeClaimedAsCanonical()
    {
        var session = NewSession(new(Start), new());
        ReadExactBatch(session, Start, 1, """[{"T":"b","S":"aapl","t":"2026-11-16T14:31:00Z"}]""");
        Assert.Null(session.ClaimRawBar(7, 12, 1));
        Assert.Equal("incomplete", (await session.Ended).State);
        Assert.Empty(await session.Archive.DrainAsync(default));
    }

    // Supply deterministic fence and subscription identity without starting an operator host.
    private static ChartAcceptanceSession NewSession(TestClock clock, MemoryArchive archive)
    {
        var session = new ChartAcceptanceSession("admin", Start, clock, archive);
        session.BindFence(7);
        session.BindGeneration(7, 12);
        return session;
    }

    // Exercise the same UTF-8 offset scanner as the real post-authentication WebSocket receive path.
    private static void ReadExactBatch(ChartAcceptanceSession session, DateTimeOffset received, long ordinal, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        using var document = JsonDocument.Parse(bytes);
        var offset = 1;
        var observer = new Observer(session);
        foreach (var element in document.RootElement.EnumerateArray())
            offset = AlpacaStreamClient.ObserveEvent(observer, element, bytes, offset, received, ordinal++);
    }

    // Decode the public result's length framing so whitespace and lexical prices survive a round trip.
    private static IReadOnlyList<(JsonElement Header, string Payload)> Decode(byte[] bytes)
    {
        var entries = new List<(JsonElement Header, string Payload)>();
        var input = bytes.AsSpan();
        while (!input.IsEmpty)
        {
            var headerLength = BinaryPrimitives.ReadInt32LittleEndian(input);
            var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(input[4..]);
            var header = JsonSerializer.Deserialize<JsonElement>(input.Slice(8, headerLength));
            var payload = Encoding.UTF8.GetString(input.Slice(8 + headerLength, payloadLength));
            entries.Add((header, payload));
            input = input[(8 + headerLength + payloadLength)..];
        }
        return entries;
    }

    private sealed class Observer(ChartAcceptanceSession session) : IAlpacaMarketDataReceiptObserver
    {
        public bool IsCapturing => session.IsCapturing;
        // Bind the synthetic batch to its one fenced session.
        public void Observe(JsonElement element, ReadOnlySpan<byte> originalUtf8, DateTimeOffset receivedAtUtc, long arrivalOrdinal) =>
            session.ObserveProvider(7, element, originalUtf8, receivedAtUtc, arrivalOrdinal);
    }

    internal sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        // Advance UTC in tests without sleeping until a future market boundary.
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    internal sealed class MemoryArchive : IRawArchiveWriter
    {
        public bool FailWrites { get; init; }
        public List<(RawArchiveRequest Request, byte[] Payload, string Hash)> Entries { get; } = [];

        // Prove the archive input and manifest tie to their exact immutable bytes without touching real evidence.
        public Task<RawArchiveReceipt> ArchiveAsync(RawArchiveRequest request, ReadOnlyMemory<byte> payload, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (FailWrites) throw new IOException("Synthetic write failure.");
            var bytes = payload.ToArray();
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            Entries.Add((request, bytes, hash));
            var manifest = new RawArchiveManifest(1, "chunk-" + Entries.Count, request.Source, request.ArtifactType,
                request.ReceivedAtUtc, "payload", request.ContentType, payload.Length, hash, null, null, null,
                request.RunId, null, null, null, request.Attributes ?? new Dictionary<string, string>());
            return Task.FromResult(new RawArchiveReceipt("synthetic-payload", "synthetic-manifest", manifest));
        }

        // No test or acceptance operation can request raw archive deletion.
        public Task<RawArchiveRetentionResult> EnforceRetentionAsync(DateTimeOffset nowUtc, CancellationToken token = default) =>
            throw new NotSupportedException();
    }
}
