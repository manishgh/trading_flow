using System.Text.Json;
using TradingFlow.Alpaca;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Indicators;
using TradingFlow.Web.Services.Charts;

namespace TradingFlow.Tests;

public sealed class ChartFormingPreviewTests
{
    // The provider table uses literal space on A/B and @ on C, with different eligibility for B by tape.
    [Theory]
    [InlineData('A', " ", 7)]
    [InlineData('B', " B", 4)]
    [InlineData('C', "@B", 7)]
    [InlineData('C', "@FTI", 4)]
    [InlineData('A', " T", 7)]
    [InlineData('B', " P", 4)]
    [InlineData('C', "@4", 4)]
    [InlineData('C', "@9I", 0)]
    [InlineData('A', " Q", 0)]
    [InlineData('C', "@M", 0)]
    public void FieldEligibilityUsesMinuteAndStrictestCondition(char tape, string codes, int expected)
    {
        Assert.True(AlpacaMarketDataMessageParser.TryResolveSipMinuteFields(tape, codes, out var fields));
        Assert.Equal((AlpacaSipMinuteFields)expected, fields);
    }

    // Unknown tape, missing conditions and a known code on the wrong tape cannot contribute any assumed fields.
    [Theory]
    [InlineData('O', "@")]
    [InlineData('A', "@")]
    [InlineData('C', " ")]
    [InlineData('C', "@?")]
    [InlineData('A', "")]
    public void UnknownEligibilityDoesNotGuess(char tape, string codes) =>
        Assert.False(AlpacaMarketDataMessageParser.TryResolveSipMinuteFields(tape, codes, out _));

    // Exact provider clock order survives out-of-order delivery and duplicate receipts.
    [Fact]
    public void NanosecondOrderAndVolumeOnlyConditionsDoNotMovePrice()
    {
        var fixture = new Fixture();
        fixture.Observe(Print(1, 105, 10, "14:31:02.123456789"), "14:31:02.400");
        fixture.Observe(Print(2, 100, 4, "14:31:02.123456781"), "14:31:02.500");
        fixture.Observe(Print(3, 900, 3, "14:31:03", "@", "I"), "14:31:03.100");
        fixture.Observe(Print(4, 1, 2000, "14:31:03.800", "M"), "14:31:04");
        var expected = fixture.Read("1m", "14:31:05");
        fixture.Observe(Print(2, 100, 4, "14:31:02.123456781"), "14:31:08");
        Assert.Equal(expected, fixture.Read("1m", "14:31:50"));
        Assert.Equal((100m, 105m, 100m, 105m, 17m),
            (expected.Open, expected.High, expected.Low, expected.Close, expected.EligibleVolume));
        Assert.Equal("2026-11-13T14:31:03.800000000Z", expected.LastEventAtUtc);
        Assert.Equal(At("14:31:04"), expected.LastObservedAtUtc);
    }

    // A correction replaces fields at the original event position, and an error removes the identified print.
    [Fact]
    public void ExactCorrectionsAndErrorsKeepOriginalOrder()
    {
        var fixture = new Fixture();
        fixture.Observe(Print(7, 110, 10, "14:31:10"), "14:31:10.100");
        fixture.Observe(Print(8, 120, 20, "14:31:11"), "14:31:11.100");
        var correction = Mutation("14:31:12", 7, 9, 110, 90, 10, 5);
        fixture.Observe(correction, "14:31:12.200");
        fixture.Observe(correction, "14:31:12.300");
        var corrected = fixture.Read("1m", "14:31:13");
        Assert.Equal((90m, 120m, 90m, 120m, 25m),
            (corrected.Open, corrected.High, corrected.Low, corrected.Close, corrected.EligibleVolume));
        Assert.Equal(At("14:31:12.200"), corrected.LastObservedAtUtc);
        fixture.Observe(Cancel("14:31:14", 8, 120, 20, "E"), "14:31:14.200");
        var after = fixture.Read("1m", "14:31:15");
        Assert.Equal((90m, 90m, 90m, 90m, 5m), (after.Open, after.High, after.Low, after.Close, after.EligibleVolume));
        Assert.Equal(At("14:31:14.200"), after.LastObservedAtUtc);
    }

    // A contradictory repeat cannot clear the first proof by reusing an exchange/tape ID.
    [Fact]
    public void ConflictingIdentityAndMalformedConditionsSuppressTheMinute()
    {
        var fixture = new Fixture();
        fixture.Observe(Print(7, 110, 10, "14:31:10"), "14:31:10.100");
        fixture.Observe(Print(7, 111, 10, "14:31:10"), "14:31:10.200");
        Assert.Equal("suppressed", fixture.Read("1m", "14:31:15").Status);
        fixture.Observe(Print(8, 110, 10, "14:32:10", "?"), "14:32:10.100");
        Assert.Equal("suppressed", fixture.Read("1m", "14:32:15").Status);
        fixture.Observe(Print(9, 110, 10, "14:33:10", "II"), "14:33:10.100", malformed: true);
        Assert.Equal("suppressed", fixture.Read("1m", "14:33:15").Status);
    }

    // Larger buckets use confirmed earlier children, never the subtotal retained before the rollover.
    [Fact]
    public void EarlierChildRequiresCanonicalReplacementOrQuietProof()
    {
        var fixture = new Fixture();
        fixture.Observe(Print(1, 110, 5, "14:31:01"), "14:31:02");
        var first = Bar("14:30:00", 100, 20, "14:31:00.200");
        Assert.Equal(25m, fixture.Read("5m", "14:31:05", [first]).EligibleVolume);
        fixture.Observe(Print(2, 120, 3, "14:32:01"), "14:32:02");
        Assert.Equal("coverage_unverified", fixture.Read("5m", "14:32:05", [first]).Status);
        var confirmed = Bar("14:31:00", 105, 10, "14:32:04");
        var replaced = fixture.Read("5m", "14:32:05", [first, confirmed]);
        Assert.Equal((100m, 120m, 100m, 120m, 33m),
            (replaced.Open, replaced.High, replaced.Low, replaced.Close, replaced.EligibleVolume));

        var quiet = new Fixture();
        quiet.Observe(Print(1, 120, 3, "14:32:01"), "14:32:02");
        Assert.Equal("coverage_unverified", quiet.Read("5m", "14:32:05", [first]).Status);
        Assert.Equal(23m, quiet.Read("5m", "14:32:05", [first], [At("14:31:00")], At("14:32:04")).EligibleVolume);
    }

    // A correction without its original ID cannot infer a target minute from the correction message time.
    [Fact]
    public void UnresolvedMutationRequiresNewCanonicalEvidenceForPotentialChildren()
    {
        var fixture = new Fixture();
        fixture.Observe(Print(1, 110, 5, "14:31:01"), "14:31:02");
        fixture.Observe(Cancel("14:31:11", 99, 120, 20, "C"), "14:31:12");
        Assert.Equal("suppressed", fixture.Read("1m", "14:31:15").Status);
        fixture.Observe(Print(2, 120, 3, "14:32:01"), "14:32:02");
        Assert.Equal("forming", fixture.Read("1m", "14:32:05").Status);
        var first = Bar("14:30:00", 100, 20, "14:31:00.200");
        var second = Bar("14:31:00", 106, 8, "14:32:03");
        Assert.Equal("coverage_unverified", fixture.Read("15m", "14:32:05", [first, second]).Status);
        Assert.Equal("forming", fixture.Read("15m", "14:32:05",
            [first with { KnownAtUtc = At("14:32:04") }, second]).Status);
    }

    // The wire receipt is displayed, but a canonical bar dispatched earlier in the same batch cannot resolve a late child.
    [Fact]
    public void LateKnownChildMustBeReconciledAfterMarketFrameDispatch()
    {
        var fixture = new Fixture();
        fixture.Observe(Print(1, 110, 5, "14:31:01"), "14:31:02");
        fixture.Observe(Print(2, 112, 4, "14:31:59"), "14:32:01", dispatched: "14:32:01.200");
        fixture.Observe(Print(3, 120, 3, "14:32:02"), "14:32:03");
        var first = Bar("14:30:00", 100, 20, "14:31:00.200");
        var second = Bar("14:31:00", 106, 8, "14:32:01.100");
        Assert.Equal("coverage_unverified", fixture.Read("5m", "14:32:05", [first, second]).Status);
        Assert.Equal("forming", fixture.Read("5m", "14:32:05", [first, second with { KnownAtUtc = At("14:32:03") }]).Status);
    }

    // Warmth, all acknowledgement channels and the fence must hold for a full observed minute.
    [Fact]
    public void AcknowledgementRepairGenerationAndLeaseFenceResetCoverage()
    {
        var fixture = new Fixture(acknowledge: false);
        fixture.Observe(Print(1, 110, 5, "14:31:01"), "14:31:02");
        Assert.Equal("coverage_unverified", fixture.Read("1m", "14:31:05").Status);
        fixture.Acknowledge("14:31:20", ["AAPL"]);
        Assert.Equal("coverage_unverified", fixture.Read("1m", "14:31:55").Status);
        fixture.Observe(Print(2, 120, 3, "14:32:01"), "14:32:02");
        Assert.Equal("forming", fixture.Read("1m", "14:32:05").Status);
        Assert.Equal("coverage_unverified", fixture.Read("1m", "14:32:05", fence: 3).Status);
        fixture.Book.MarkWarm("AAPL", 11, null);
        fixture.Book.MarkWarm("AAPL", 11, At("14:32:06"));
        fixture.Observe(Print(3, 122, 3, "14:32:10"), "14:32:12");
        Assert.Equal("coverage_unverified", fixture.Read("1m", "14:32:20").Status);
        fixture.Observe(Print(4, 123, 3, "14:33:01"), "14:33:02");
        Assert.Equal("forming", fixture.Read("1m", "14:33:05").Status);
        fixture.Book.Untrack("AAPL");
        Assert.Equal("not_streaming", fixture.Read("1m", "14:33:05").Status);
        fixture.Book.Track("AAPL", 12);
        fixture.Book.MarkWarm("AAPL", 11, At("14:33:06"));
        Assert.Equal("coverage_unverified", fixture.Read("1m", "14:33:08", generation: 12).Status);
        fixture.Leased = false;
        Assert.Equal("not_streaming", fixture.Read("1m", "14:33:09", generation: 12).Status);
    }

    // An incomplete acknowledgement leaves a symbol subscribed for canonical activity only.
    [Fact]
    public void MissingCancelChannelDoesNotAdmitTradeCoverage()
    {
        var fixture = new Fixture(acknowledge: false);
        fixture.Acknowledge("14:30:20", ["AAPL"], includeCancellations: false);
        fixture.Observe(Print(1, 110, 5, "14:31:01"), "14:31:02");
        Assert.Equal("coverage_unverified", fixture.Read("1m", "14:31:05").Status);
    }

    // Use the verified early close to truncate the preview at the actual regular segment end.
    [Fact]
    public void VerifiedPartialSegmentAndVolumeOnlyMinute()
    {
        var fixture = new Fixture(close: new TimeOnly(13, 2));
        var child = Bar("18:00:00", 100, 10, "18:01:00.200");
        fixture.Observe(Print(1, 110, 5, "18:01:01", "@", "I"), "18:01:02");
        Assert.Equal("waiting_for_trade", fixture.Read("15m", "18:01:05", [child]).Status);
        fixture.Observe(Print(2, 111, 5, "18:01:10"), "18:01:11");
        var preview = fixture.Read("15m", "18:01:20", [child]);
        Assert.Equal(At("18:00:00"), preview.StartUtc);
        Assert.Equal(At("18:02:00"), preview.EndUtc);
        Assert.Equal(20m, preview.EligibleVolume);
        Assert.Equal("closed", fixture.Read("15m", "18:02:00", [child]).Status);
    }

    // Without a coherent current-date schedule the chart cannot tell an operator the exchange is closed.
    [Theory]
    [InlineData("missing", "coverage_unverified")]
    [InlineData("wrong-date", "coverage_unverified")]
    [InlineData("invalid-hours", "coverage_unverified")]
    [InlineData("verified-closed", "closed")]
    public void CalendarRefreshClearsPreviewAndOnlyVerifiedClosureIsClosed(string source, string expected)
    {
        var fixture = new Fixture();
        fixture.Observe(Print(1, 111, 5, "14:31:10"), "14:31:11");
        Assert.Equal("forming", fixture.Read("1m", "14:31:12").Status);

        var date = new DateOnly(2026, 11, 13);
        var schedules = new Dictionary<DateOnly, MarketSessionSchedule>();
        var ordinary = MarketSessionSchedule.RegularDay(date);
        if (source == "wrong-date") schedules.Add(date, ordinary with { TradeDate = date.AddDays(-1) });
        if (source == "invalid-hours") schedules.Add(date, ordinary with { RegularClose = ordinary.RegularOpen });
        if (source == "verified-closed") schedules.Add(date, MarketSessionSchedule.Closed(date));
        fixture.Book.UpdateCalendar(new(schedules), At("14:31:15"));
        fixture.Observe(Print(2, 113, 5, "14:32:01"), "14:32:02");

        var preview = fixture.Read("1m", "14:32:03");
        Assert.Equal(expected, preview.Status);
        Assert.Null(preview.Open);
        Assert.Null(preview.LastObservedAtUtc);
    }

    // Exhausting either memory ceiling suppresses the affected minute while existing other symbols keep their evidence.
    [Fact]
    public void SymbolAndConnectionBudgetsAreBoundedAndRollToNewMinute()
    {
        var fixture = new Fixture(symbolLimit: 2, connectionLimit: 3, symbols: ["AAPL", "MSFT", "NVDA"]);
        fixture.Observe(Print(1, 100, 5, "14:31:01"), "14:31:02");
        fixture.Observe(Print(2, 102, 5, "14:31:03"), "14:31:04");
        fixture.Observe(Print(3, 103, 5, "14:31:05"), "14:31:06");
        Assert.Equal("suppressed", fixture.Read("1m", "14:31:07").Status);
        fixture.Observe(Print(1, 200, 5, "14:31:09").Replace("AAPL", "MSFT"), "14:31:10");
        fixture.Observe(Print(1, 300, 5, "14:31:11").Replace("AAPL", "NVDA"), "14:31:12");
        Assert.Equal("forming", fixture.Read("1m", "14:31:15", symbol: "MSFT").Status);
        Assert.Equal("suppressed", fixture.Read("1m", "14:31:15", symbol: "NVDA").Status);
        fixture.Observe(Print(4, 103, 5, "14:32:05"), "14:32:06");
        Assert.Equal("forming", fixture.Read("1m", "14:32:08").Status);
    }

    private sealed class Fixture
    {
        public SipFormingBook Book { get; }
        public bool Leased { get; set; } = true;

        // Use a fixed Friday and isolated scope, independent of the operator host and provider.
        public Fixture(bool acknowledge = true, TimeOnly? close = null, int symbolLimit = 10_000,
            int connectionLimit = 100_000, string[]? symbols = null)
        {
            var day = new DateOnly(2026, 11, 13);
            Book = new(4, _ => Leased, new(new Dictionary<DateOnly, MarketSessionSchedule>
            {
                [day] = new(day, true, new TimeOnly(9, 30), close ?? new TimeOnly(16, 0))
            }), symbolLimit, connectionLimit);
            symbols ??= ["AAPL"];
            foreach (var symbol in symbols)
            {
                Book.Track(symbol, 11);
                Book.MarkWarm(symbol, 11, At("14:30:20"));
            }
            if (acknowledge) Acknowledge("14:30:10", symbols);
        }

        // Feed captured-shape acknowledgement from the existing channel set.
        public void Acknowledge(string observed, string[] symbols, bool includeCancellations = true)
        {
            var document = JsonSerializer.SerializeToElement(new
            {
                T = "subscription", trades = symbols, corrections = symbols,
                cancelErrors = includeCancellations ? symbols : []
            });
            Assert.True(Book.ApplyAcknowledgement(document, At(observed)));
        }

        // Replay a provider frame with an independent receipt instant.
        public void Observe(string frame, string observed, bool malformed = false, string? dispatched = null)
        {
            using var document = JsonDocument.Parse(frame);
            var ok = AlpacaMarketDataMessageParser.TryParseSipTrade(document.RootElement, out var parsed, out var failure);
            Assert.Equal(!malformed, ok);
            if (ok) Book.Observe(Assert.IsType<AlpacaSipTradeFrame>(parsed), 11, At(observed), At(dispatched ?? observed));
            else Book.ObserveFailure(Assert.IsType<AlpacaSipTradeFailure>(failure), At(observed), At(dispatched ?? observed));
        }

        // Reuse the sole completed copy and model market-scope or lease replacement explicitly.
        public ChartFormingCandle Read(string interval, string now, OhlcvBar[]? bars = null,
            DateTimeOffset[]? quiet = null, DateTimeOffset? proof = null, long fence = 4,
            long generation = 11, string symbol = "AAPL") =>
            Book.Read(new(symbol, interval, "regular"), new(fence, generation, bars ?? [],
                new HashSet<DateTimeOffset>(quiet ?? []), false, proof), At(now));
    }

    // All relative fixture times are UTC on the same verified exchange date.
    private static DateTimeOffset At(string time) => DateTimeOffset.Parse("2026-11-13T" + time + "Z");

    // Use the provider's positive native candle fields and actual canonical receipt.
    private static OhlcvBar Bar(string at, decimal price, decimal volume, string received) =>
        new("AAPL", At(at), "1m", price, price, price, price, volume, "sip", "all", At(received));

    // Encode exact wire fields for ordinary prints, including blank and multi-condition arrays.
    private static string Print(long id, decimal price, int size, string time, params string[] conditions) =>
        Frame("t", time, ("i", id), ("p", price), ("s", size), ("c", conditions.Length == 0 ? new[] { "@" } : conditions));

    // Encode both old and corrected identities so tests exercise provider mutation parsing.
    private static string Mutation(string time, long original, long corrected, decimal oldPrice, decimal newPrice, int oldSize, int newSize) =>
        Frame("c", time, ("oi", original), ("ci", corrected), ("op", oldPrice), ("cp", newPrice),
            ("os", oldSize), ("cs", newSize), ("oc", new[] { "@" }), ("cc", new[] { "@" }));

    // Model both documented cancel/error actions by their original identity.
    private static string Cancel(string time, long id, decimal price, int size, string action) =>
        Frame("x", time, ("i", id), ("p", price), ("s", size), ("a", action));

    // JSON dictionary keys preserve the wire distinction between the frame type T and event timestamp t.
    private static string Frame(string type, string time, params (string Name, object Value)[] values)
    {
        var fields = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["T"] = type, ["S"] = "AAPL", ["x"] = "D", ["z"] = "C", ["t"] = "2026-11-13T" + time + "Z"
        };
        foreach (var (name, value) in values) fields.Add(name, value);
        return JsonSerializer.Serialize(fields);
    }
}
