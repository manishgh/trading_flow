using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using TradingFlow.Alpaca;

namespace TradingFlow.Tests;

public sealed class SipPortfolioExecutionSourceTests
{
    private static readonly long Open = SipNanosecondTime.Parse("2026-05-21T13:30:00Z");

    [Fact]
    // Verify exact global nanosecond grouping, prefix routing flags and complete physical source reconciliation.
    public async Task GlobalFramesGroupSymbolsWithoutInventingTieOrderAndConsumeEveryPhysicalSource()
    {
        using var fixture = new Sources();
        var a = await fixture.Session("AAA", Open, [Quote(Open - 1)], [Quote(Open + 1)], [Trade(Open + 2)]);
        var b = await fixture.Session("BBB", Open, [Quote(Open - 1)], [Quote(Open + 1)], [Trade(Open + 2)]);
        var source = SipPortfolioExecutionSource.Create([b, a], Policy(delay: 25));
        Assert.Null(source.CompletedAudit);
        var frames = await All(source.ReadAsync());
        Assert.Equal(3, frames.Count);
        Assert.All(frames, frame => {
            Assert.Equal(2, frame.Batches.Length); Assert.False(frame.CrossSymbolOrderingProven);
            Assert.Equal(frame.EventTimeNanoseconds + 25, frame.KnownAtNanoseconds);
            Assert.All(frame.Batches, batch => Assert.Equal(frame.EventTimeNanoseconds, batch.Market.EventTimeNanoseconds));
        });
        Assert.All(frames[0].Batches, batch => Assert.False(batch.InRegularSession));
        Assert.All(frames.Skip(1).SelectMany(frame => frame.Batches), batch => Assert.True(batch.InRegularSession));
        Assert.All(frames.SelectMany(frame => frame.Batches), batch => Assert.Single(batch.Observations));
        var audit = Assert.IsType<SipPortfolioSourceAudit>(source.CompletedAudit);
        Assert.True(audit.FullyConsumedAndVerified); Assert.Equal(4, audit.QuoteRows); Assert.Equal(2, audit.TradeRows); Assert.Equal(2, audit.Sessions.Length);
        Assert.Equal(SipPortfolioExecutionSource.ComputePolicySha256(Policy(delay: 25)), audit.PolicySha256);
        Assert.False(audit.OriginalReceiptProven); Assert.False(audit.HistoricalSecurityIdentityProven); Assert.False(audit.OrderPriceQuantityConversionProven);
    }

    [Theory]
    [InlineData("ordinary", true, 0, 0)]
    [InlineData("modifiers", true, 0, 0)]
    [InlineData("modifier_only", false, 1, 0)]
    [InlineData("unknown", false, 1, 0)]
    [InlineData("ineligible", false, 0, 1)]
    // Verify each diagnostic condition disposition preserves its row and reports exact eligibility uncertainty.
    public async Task ConditionDispositionsPreserveRowsAndNeverPromoteUnknownOrModifierOnlyTrades(string kind, bool eligible, int unknown, int ineligible)
    {
        using var fixture = new Sources();
        string[] codes = kind switch {
            "ordinary" => ["@"], "modifiers" => ["@", "F", "I"], "modifier_only" => ["I"],
            "unknown" => ["@", "NEW"], _ => ["@", "4"] };
        var session = await fixture.Session("AAA", Open, [Quote(Open - 1)], [], [Trade(Open + 1, codes: codes)]);
        var source = SipPortfolioExecutionSource.Create([session], Policy());
        var frames = await All(source.ReadAsync()); var batch = frames.Single(frame => frame.EventTimeNanoseconds == Open + 1).Batches.Single();
        Assert.Equal(eligible, batch.Market.Trades.Single().IsEligible);
        Assert.Single(batch.Observations); Assert.Equal(codes, batch.Observations.Single().Conditions);
        Assert.Equal(unknown, batch.UnknownObservationRows); Assert.Equal(ineligible, batch.KnownIneligibleObservationRows);
        Assert.Equal(unknown > 0, batch.UnknownEvidenceReasons.Length > 0); Assert.Equal(ineligible > 0, batch.IneligibleObservationReasons.Length > 0);
        Assert.Equal(unknown, source.CompletedAudit!.UnknownConditionOrGeometryRows);
        Assert.Equal(ineligible, source.CompletedAudit.KnownIneligibleRows);
    }

    [Fact]
    // Prove the real single-space sale code is retained and odd lots require the injected ordinary-base contract.
    public async Task CtaSpaceMarkerAndInformationalOddLotRemainSupportedOnlyByTheirInjectedContract()
    {
        using var fixture = new Sources();
        var session = await fixture.Session("AAA", Open, [], [], [Trade(Open + 1, tape: "A", codes: [" ", "I"])]);
        var source = SipPortfolioExecutionSource.Create([session], Policy());
        var frame = Assert.Single(await All(source.ReadAsync()));
        Assert.True(Assert.Single(frame.Batches.Single().Market.Trades).IsEligible);
        Assert.Contains(" ", frame.Batches.Single().Observations.Single().Conditions);
    }

    [Fact]
    // Ensure an unknown quote can clear prior NBBO state rather than silently disappearing.
    public async Task UnknownQuoteStillReachesTheBookAsIneligibleInsteadOfDisappearingOrCarryingOldNbbo()
    {
        using var fixture = new Sources();
        var session = await fixture.Session("AAA", Open, [Quote(Open - 1)], [Quote(Open + 1, code: "NEW")], []);
        var frames = await All(SipPortfolioExecutionSource.Create([session], Policy()).ReadAsync());
        var quote = frames[1].Batches.Single();
        Assert.Single(quote.Market.Quotes); Assert.False(quote.Market.Quotes.Single().IsEligible);
        Assert.NotEmpty(quote.UnknownEvidenceReasons); Assert.Single(quote.Observations);
    }

    [Fact]
    // Keep simultaneous quote/trade evidence atomic and count unknown versus excluded rows exactly.
    public async Task MixedSameTimeRowsHaveExactPerObservationDispositionCountsAndRemainOneBatch()
    {
        using var fixture = new Sources();
        var session = await fixture.Session("AAA", Open, [], [Quote(Open + 1)],
            [Trade(Open + 1, id: 1), Trade(Open + 1, id: 2, codes: ["@", "NEW"]), Trade(Open + 1, id: 3, codes: ["@", "4"])]);
        var source = SipPortfolioExecutionSource.Create([session], Policy());
        var frame = Assert.Single(await All(source.ReadAsync())); var batch = frame.Batches.Single();
        Assert.Single(batch.Market.Quotes); Assert.Equal(3, batch.Market.Trades.Count);
        Assert.Equal(1, batch.UnknownObservationRows); Assert.Equal(1, batch.KnownIneligibleObservationRows);
        Assert.Equal(1, source.CompletedAudit!.UnknownConditionOrGeometryRows); Assert.Equal(1, source.CompletedAudit.KnownIneligibleRows);
    }

    [Fact]
    // Prevent duplicated physical source observations from creating invented executable liquidity.
    public async Task DuplicateTradeRowsAreRetainedWithTheirSameIdentityAndExplicitUnknownRatherThanExtraCapacity()
    {
        using var fixture = new Sources(); var row = Trade(Open + 1);
        var session = await fixture.Session("AAA", Open, [], [], [row, row]);
        var source = SipPortfolioExecutionSource.Create([session], Policy());
        var batch = Assert.Single(await All(source.ReadAsync())).Batches.Single();
        Assert.Equal(2, batch.Observations.Length); Assert.Equal(2, batch.Market.Trades.Count);
        Assert.Single(batch.Market.Trades.Select(trade => trade.EventId).Distinct());
        Assert.All(batch.Market.Trades, trade => Assert.False(trade.IsEligible));
        Assert.Contains("duplicate_source_trade_observation", batch.UnknownEvidenceReasons);
        Assert.Equal(2, source.CompletedAudit!.UnknownConditionOrGeometryRows);
    }

    [Fact]
    // Prove source windows remain chronological per symbol and reject overlapping prefix evidence.
    public async Task SymbolSessionsAreConsumedChronologicallyAndPrefixCannotOverlapEarlierSourceWindows()
    {
        using var fixture = new Sources();
        var first = await fixture.Session("AAA", Open, [Quote(Open - 1)], [], [Trade(Open + 1)]);
        var tomorrow = Open + 86_400_000_000_000L;
        var second = await fixture.Session("AAA", tomorrow, [Quote(tomorrow - 1)], [], [Trade(tomorrow + 1)]);
        var source = SipPortfolioExecutionSource.Create([second, first], Policy());
        var frames = await All(source.ReadAsync());
        Assert.Equal(frames.Select(frame => frame.EventTimeNanoseconds).Order(), frames.Select(frame => frame.EventTimeNanoseconds));
        Assert.Equal(new long[] { 1, 2, 3, 4 }, frames.Select(frame => frame.Batches.Single().Market.Sequence));
        Assert.Equal(2, source.CompletedAudit!.Sessions.Length);
        Assert.Throws<InvalidDataException>(() => SipPortfolioExecutionSource.Create([first, first with { SessionKey = "overlap" }], Policy()));
    }

    [Fact]
    // Require full successful source exhaustion before a completed physical audit can exist.
    public async Task PartialConsumptionAndSourceCorruptionNeverPublishACompletedSourceAudit()
    {
        using var fixture = new Sources();
        var session = await fixture.Session("AAA", Open, [Quote(Open - 1)], [Quote(Open + 1)], [Trade(Open + 2)]);
        var partial = SipPortfolioExecutionSource.Create([session], Policy());
        await using (var enumerator = partial.ReadAsync().GetAsyncEnumerator()) Assert.True(await enumerator.MoveNextAsync());
        Assert.Null(partial.CompletedAudit);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => { _ = await All(partial.ReadAsync()); });
        var corrupt = SipPortfolioExecutionSource.Create([session], Policy());
        await File.AppendAllTextAsync(Path.Combine(session.TradesDirectory, "page-0000001.json.gz"), "changed");
        await Assert.ThrowsAsync<InvalidDataException>(async () => { _ = await All(corrupt.ReadAsync()); });
        Assert.Null(corrupt.CompletedAudit);
    }

    [Fact]
    // Keep empty archives genuine and reject undeclared availability assumptions without identity claims.
    public async Task EmptyVerifiedSourcesProduceNoSyntheticMarketBoundaryAndNoIdentityOrConversionClaim()
    {
        using var fixture = new Sources(); var session = await fixture.Session("AAA", Open, [], [], []);
        var source = SipPortfolioExecutionSource.Create([session], Policy());
        Assert.Empty(await All(source.ReadAsync())); Assert.True(source.CompletedAudit!.FullyConsumedAndVerified);
        Assert.Equal(0, source.CompletedAudit.QuoteRows); Assert.Equal(0, source.CompletedAudit.TradeRows);
        Assert.False(source.CompletedAudit.OrderPriceQuantityConversionProven);
        Assert.Throws<InvalidDataException>(() => SipPortfolioExecutionSource.Create([session], Policy() with { KnownAtBasis = SipPortfolioKnownAtBasis.Unspecified }));
        Assert.Throws<InvalidDataException>(() => SipPortfolioExecutionSource.Create([session], Policy() with { SourceAvailabilityDelayNanoseconds = -1 }));
    }

    [Fact]
    // Check that a later raw source suffix cannot revise an earlier market projection.
    public async Task FutureSourceSuffixCannotChangeAnEarlierProjectedMarketFrame()
    {
        using var fixtureA = new Sources(); using var fixtureB = new Sources();
        var a = await fixtureA.Session("AAA", Open, [Quote(Open - 1)], [Quote(Open + 1)], [Trade(Open + 2)]);
        var b = await fixtureB.Session("AAA", Open, [Quote(Open - 1)], [Quote(Open + 1)], [Trade(Open + 2), Trade(Open + 3, id: 2)]);
        var left = await All(SipPortfolioExecutionSource.Create([a], Policy()).ReadAsync());
        var right = await All(SipPortfolioExecutionSource.Create([b], Policy()).ReadAsync());
        Assert.Equal(JsonSerializer.Serialize(left[0].Batches.Single().Market), JsonSerializer.Serialize(right[0].Batches.Single().Market));
    }

    // Construct an explicit fixture-only condition contract without adopting a production whitelist.
    internal static SipPortfolioSourcePolicy Policy(long delay = 0)
    {
        var metadata = ImmutableArray.CreateBuilder<SipExecutionConditionMetadata>();
        var rules = ImmutableArray.CreateBuilder<SipExecutionConditionRule>();
        foreach (var tape in new[] { "A", "C" }) {
            foreach (var kind in Enum.GetValues<SipArchiveEventKind>()) metadata.Add(new(kind, tape, new string('a', 64)));
            rules.Add(new(SipArchiveEventKind.Quote, tape, "R", "Regular quote", SipExecutionConditionDisposition.Eligible));
            rules.Add(new(SipArchiveEventKind.Trade, tape, tape == "A" ? " " : "@", "Regular sale", SipExecutionConditionDisposition.Eligible));
            rules.Add(new(SipArchiveEventKind.Trade, tape, "I", "Odd lot informational modifier", SipExecutionConditionDisposition.Eligible, IsModifierOnly: true));
            rules.Add(new(SipArchiveEventKind.Trade, tape, "F", "Sweep informational modifier", SipExecutionConditionDisposition.Eligible, IsModifierOnly: true));
            rules.Add(new(SipArchiveEventKind.Trade, tape, "4", "Diagnostic excluded condition", SipExecutionConditionDisposition.Ineligible));
        }
        return new("fixture-diagnostic-source-v1", SipPortfolioKnownAtBasis.RetrospectiveSourceTimePlusDeclaredDelay, delay,
            new("fixture-explicit-conditions-v1", metadata.ToImmutable(), rules.ToImmutable()));
    }
    // Encode a genuine raw quote shape for the physical archive fixture.
    private static JsonElement Quote(long at, string code = "R") => JsonSerializer.SerializeToElement(new {
        t = SipNanosecondTime.Format(at), bp = 100, ap = 101, bs = 1000, @as = 1200, bx = "Q", ax = "P", c = new[] { code }, z = "C" });
    // Encode a genuine raw trade shape with preserved provider ID, tape and conditions.
    private static JsonElement Trade(long at, int id = 1, string tape = "C", string[]? codes = null) => JsonSerializer.SerializeToElement(new {
        t = SipNanosecondTime.Format(at), i = id, p = 100.5m, s = 1, x = "U", c = codes ?? ["@", "I"], z = tape });
    // Exhaust the async stream so assertions can distinguish complete from partial verification.
    private static async Task<List<T>> All<T>(IAsyncEnumerable<T> stream) { var result = new List<T>(); await foreach (var item in stream) result.Add(item); return result; }

    internal sealed class Sources : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "tf-sip-portfolio-" + Guid.NewGuid().ToString("N"));
        // Create exact frozen prefix, quote and trade archives for one fixture session.
        public async Task<SipPortfolioSessionArchive> Session(string symbol, long open, JsonElement[] prefix, JsonElement[] quotes, JsonElement[] trades,
            long sessionDurationNanoseconds = 300_000_000_000L)
        {
            var key = symbol + "-" + open; var directory = Path.Combine(root, key);
            var regular = new SipArchiveRequest(symbol, "quotes", SipNanosecondTime.Format(open), SipNanosecondTime.Format(open + sessionDurationNanoseconds), "2026-10-06");
            var prefixRequest = regular with { StartInclusive = SipNanosecondTime.Format(open - 30_000_000_000L), EndExclusive = regular.StartInclusive };
            var prefixHash = await Write(Path.Combine(directory, "prefix"), prefixRequest, prefix);
            var quoteHash = await Write(Path.Combine(directory, "quotes"), regular, quotes);
            var tradeHash = await Write(Path.Combine(directory, "trades"), regular with { Kind = "trades" }, trades);
            return new(key, regular, Path.Combine(directory, "quotes"), quoteHash, Path.Combine(directory, "trades"), tradeHash,
                prefixRequest, Path.Combine(directory, "prefix"), prefixHash);
        }
        // Seal a real gzip page, receipt, request and manifest so tests exercise the actual reader.
        private static async Task<string> Write(string directory, SipArchiveRequest request, JsonElement[] rows)
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, "request.json"), JsonSerializer.SerializeToUtf8Bytes(request));
            var raw = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?> {
                [request.Kind] = new Dictionary<string, JsonElement[]> { [request.Symbol] = rows }, ["next_page_token"] = null });
            using var output = new MemoryStream(); using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true)) gzip.Write(raw);
            var compressed = output.ToArray(); await File.WriteAllBytesAsync(Path.Combine(directory, "page-0000001.json.gz"), compressed);
            var page = new SipArchivePage(1, null, null, new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero), rows.Length,
                rows.Length == 0 ? null : SipNanosecondTime.Parse(rows[0].GetProperty("t").GetString()!),
                rows.Length == 0 ? null : SipNanosecondTime.Parse(rows[^1].GetProperty("t").GetString()!),
                "page-0000001.json.gz", Hash(compressed), Hash(raw), compressed.Length);
            await File.WriteAllBytesAsync(Path.Combine(directory, "page-0000001.json"), JsonSerializer.SerializeToUtf8Bytes(page));
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new SipArchiveManifest(SipExecutionArchiveCollector.Version, request, [page], true,
                rows.Length, SipExecutionArchiveCollector.QuoteSizeContract));
            await File.WriteAllBytesAsync(Path.Combine(directory, "manifest.json"), manifest); return Hash(manifest);
        }
        // Hash physical fixture bytes using the same SHA256 format as frozen archives.
        private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
        // Remove only this fixture's verified absolute temporary directory.
        public void Dispose() {
            var full = Path.GetFullPath(root); var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("tf-sip-portfolio-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the task-owned source fixture.");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
}
