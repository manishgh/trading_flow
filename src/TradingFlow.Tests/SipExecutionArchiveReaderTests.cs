using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingFlow.Alpaca;

namespace TradingFlow.Tests;

public sealed class SipExecutionArchiveReaderTests
{
    private static readonly long Origin = SipNanosecondTime.Parse("2026-05-21T16:00:00Z");

    [Fact]
    public async Task MergePreservesNanosecondsMetadataAndCrossPageSameTimeBatches()
    {
        using var fixture = new ArchiveFixture();
        var ns = Origin + 123456789;
        var q = await fixture.Write("quotes", [Quote(ns)]);
        var t = await fixture.Write("trades", [Trade(ns, id: 271)], [Trade(ns, id: 272), Trade(ns + 1)]);
        var batches = await All(SipExecutionArchiveReader.MergeAsync(q, t));
        Assert.Equal(2, batches.Count); Assert.Equal(ns, batches[0].TimestampNanoseconds);
        Assert.Single(batches[0].Quotes); Assert.Equal(2, batches[0].Trades.Length);
        var trade = batches[0].Trades[0];
        Assert.Equal("271", trade.ProviderId); Assert.Equal("U", trade.TradeExchange); Assert.Equal("C", trade.Tape);
        Assert.Equal(new[] { "@", "I" }, trade.Conditions);
        Assert.Equal(64, trade.ContentSha256.Length); Assert.Equal(t.Identity.ManifestSha256, trade.SourceManifestSha256);
        Assert.Equal(1, batches[1].TimestampNanoseconds - batches[0].TimestampNanoseconds);
    }

    [Fact]
    public async Task RowContentHashIsIndependentOfPropertyOrderButRetainsUnknownFields()
    {
        using var fixture = new ArchiveFixture();
        var first = Quote(Origin + 1);
        var reversed = JsonSerializer.SerializeToElement(first.EnumerateObject().Reverse().ToDictionary(p => p.Name, p => p.Value));
        var extra = first.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        extra.Add("future_provider_field", JsonSerializer.SerializeToElement("retained_in_hash"));
        var q = await fixture.Write("quotes", [first, reversed, JsonSerializer.SerializeToElement(extra)]);
        var rows = await All(q.ReadEventsAsync());
        Assert.Equal(rows[0].ContentSha256, rows[1].ContentSha256);
        Assert.NotEqual(rows[0].ContentSha256, rows[2].ContentSha256);
        Assert.Equal(3, rows.Count); // no hidden deduplication of provider rows
    }

    [Fact]
    public async Task FrozenManifestRequestAndCachedReceiptMustAgree()
    {
        using var fixture = new ArchiveFixture();
        var q = await fixture.Write("quotes", [Quote(Origin + 1)]);
        await Assert.ThrowsAsync<InvalidDataException>(() => SipExecutionArchiveReader.OpenAsync(q.Identity.Directory,
            q.Identity.Request with { Symbol = "OTHER" }));
        await Assert.ThrowsAsync<InvalidDataException>(() => SipExecutionArchiveReader.OpenAsync(q.Identity.Directory,
            expectedManifestSha256: new string('f', 64)));
        var page = q.Identity.Pages[0] with { Count = 2 };
        await File.WriteAllBytesAsync(Path.Combine(q.Identity.Directory, "page-0000001.json"), JsonSerializer.SerializeToUtf8Bytes(page));
        await Assert.ThrowsAsync<InvalidDataException>(() => SipExecutionArchiveReader.OpenAsync(q.Identity.Directory));
    }

    [Theory]
    [InlineData("compressed")]
    [InlineData("receipt")]
    [InlineData("manifest")]
    [InlineData("request")]
    public async Task SourceMutationAfterOpenCannotPublishACompleteInventory(string file)
    {
        using var fixture = new ArchiveFixture();
        var q = await fixture.Write("quotes", [Quote(Origin + 1)]);
        var t = await fixture.Write("trades", [Trade(Origin + 2)]);
        var name = file switch { "compressed" => "page-0000001.json.gz", "receipt" => "page-0000001.json", "manifest" => "manifest.json", _ => "request.json" };
        await File.AppendAllTextAsync(Path.Combine(q.Identity.Directory, name), " ");
        await Assert.ThrowsAsync<InvalidDataException>(() => SipExecutionCalibrationInventory.MeasureAsync(q, t, 1_000_000));
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("path")]
    [InlineData("token")]
    [InlineData("receipt_claim")]
    public async Task IncompleteUnsafeOrHistoricallyClaimedManifestFailsClosed(string failure)
    {
        using var fixture = new ArchiveFixture();
        var q = await fixture.Write("quotes", [Quote(Origin + 1)]);
        var manifest = JsonSerializer.Deserialize<SipArchiveManifest>(await File.ReadAllBytesAsync(Path.Combine(q.Identity.Directory, "manifest.json")))!;
        manifest = failure switch {
            "incomplete" => manifest with { PaginationComplete = false },
            "path" => manifest with { Pages = [manifest.Pages[0] with { Payload = "../outside.json.gz" }] },
            "token" => manifest with { Pages = [manifest.Pages[0] with { NextToken = "never-fetched" }] },
            _ => manifest with { HistoricalReceiptClaim = true } };
        await File.WriteAllBytesAsync(Path.Combine(q.Identity.Directory, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest));
        await Assert.ThrowsAsync<InvalidDataException>(() => SipExecutionArchiveReader.OpenAsync(q.Identity.Directory));
    }

    [Fact]
    public async Task RawCountAndChronologicalExtremaMustMatchReceipts()
    {
        using var fixture = new ArchiveFixture();
        var q = await fixture.Write("quotes", [Quote(Origin + 1)]);
        var manifestPath = Path.Combine(q.Identity.Directory, "manifest.json");
        var manifest = JsonSerializer.Deserialize<SipArchiveManifest>(await File.ReadAllBytesAsync(manifestPath))!;
        var page = manifest.Pages[0] with { Count = 2 };
        await File.WriteAllBytesAsync(Path.Combine(q.Identity.Directory, "page-0000001.json"), JsonSerializer.SerializeToUtf8Bytes(page));
        await File.WriteAllBytesAsync(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest with { Rows = 2, Pages = [page] }));
        var reopened = await SipExecutionArchiveReader.OpenAsync(q.Identity.Directory);
        await Assert.ThrowsAsync<InvalidDataException>(async () => { _ = await All(reopened.ReadEventsAsync()); });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(99)]
    public async Task SourceNanosecondsAfterActualCaptureAreRejectedWithoutTickTruncation(long nanosAfterCapture)
    {
        using var fixture = new ArchiveFixture();
        var q = await fixture.Write("quotes", [Quote(Origin + nanosAfterCapture)]);
        var manifestPath = Path.Combine(q.Identity.Directory, "manifest.json");
        var manifest = JsonSerializer.Deserialize<SipArchiveManifest>(await File.ReadAllBytesAsync(manifestPath))!;
        var capture = DateTimeOffset.UnixEpoch.AddTicks(Origin / 100);
        var page = manifest.Pages[0] with { CapturedUtc = capture };
        await File.WriteAllBytesAsync(Path.Combine(q.Identity.Directory, "page-0000001.json"), JsonSerializer.SerializeToUtf8Bytes(page));
        await File.WriteAllBytesAsync(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest with { Pages = [page] }));
        await Assert.ThrowsAsync<InvalidDataException>(() => SipExecutionArchiveReader.OpenAsync(q.Identity.Directory));
    }

    [Fact]
    public async Task EmptyResponseCapturedBeforeRequestedEndCannotCertifyFutureCoverage()
    {
        using var fixture = new ArchiveFixture();
        var q = await fixture.Write("quotes", Array.Empty<JsonElement>());
        var manifestPath = Path.Combine(q.Identity.Directory, "manifest.json");
        var manifest = JsonSerializer.Deserialize<SipArchiveManifest>(await File.ReadAllBytesAsync(manifestPath))!;
        var futureRequest = manifest.Request with { EndExclusive = "2026-05-21T16:05:00.000000001Z" };
        var page = manifest.Pages[0] with { CapturedUtc = DateTimeOffset.UnixEpoch.AddTicks(
            SipNanosecondTime.Parse("2026-05-21T16:05:00Z") / 100) };
        await File.WriteAllBytesAsync(Path.Combine(q.Identity.Directory, "page-0000001.json"), JsonSerializer.SerializeToUtf8Bytes(page));
        await File.WriteAllBytesAsync(Path.Combine(q.Identity.Directory, "request.json"), JsonSerializer.SerializeToUtf8Bytes(futureRequest));
        await File.WriteAllBytesAsync(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest with { Request = futureRequest, Pages = [page] }));
        await Assert.ThrowsAsync<InvalidDataException>(() => SipExecutionArchiveReader.OpenAsync(q.Identity.Directory));
    }

    [Fact]
    public async Task InventoryMeasuresStrictPriorSourceQuotesWithoutInventingEligibilityOrOrder()
    {
        using var fixture = new ArchiveFixture();
        var q = await fixture.Write("quotes", [Quote(Origin + 1), Quote(Origin + 2, bid: 100, ask: 102),
            Quote(Origin + 4), Quote(Origin + 4, bid: 99), Quote(Origin + 6, bid: 102, ask: 100), Quote(Origin + 9)]);
        var t = await fixture.Write("trades", [Trade(Origin), Trade(Origin + 2, price: 100.5m), Trade(Origin + 3, price: 200),
            Trade(Origin + 5), Trade(Origin + 7), Trade(Origin + 8, size: 0), Trade(Origin + 10), Trade(Origin + 20)]);
        var result = await SipExecutionCalibrationInventory.MeasureAsync(q, t, staleAfterNanoseconds: 2);
        Assert.Equal("RETROSPECTIVE_SOURCE_MEASUREMENTS", result.MeasurementClass);
        Assert.Equal(6, result.QuoteRows); Assert.Equal(8, result.TradeRows);
        Assert.Equal(1, result.QuoteInvalidPriceRows); Assert.Equal(1, result.TradeInvalidPriceOrSizeRows);
        Assert.Equal(1, result.SameNanosecondQuoteTradeBatches); Assert.Equal(1, result.SameNanosecondQuoteTradeRows);
        Assert.Equal(1, result.TradesWithoutStrictlyPriorQuote); Assert.Equal(1, result.TradesWithAmbiguousPriorQuote);
        Assert.Equal(1, result.TradesWithInvalidPriorQuote); Assert.Equal(4, result.TradesComparedToPriorQuote);
        Assert.Equal(3, result.TradesInsidePriorQuote); Assert.Equal(1, result.TradesOutsidePriorQuote); Assert.Equal(1, result.TradesWithStalePriorQuote);
        Assert.Equal(8, result.OddLotConditionITradeRows); Assert.Equal(8, result.TradeRowsWithUnrecognizedConditions);
        Assert.Equal(8, result.UnrecognizedTradeConditionCounts["I"]);
        Assert.Equal(5, result.SpreadBpsFloorHistogram.Values.Sum());
        Assert.Equal(6, result.DisplayedBidSharesHistogram[1000]); Assert.Equal(4, result.PriorQuoteAgeMillisecondsFloorHistogram[0]);
        Assert.True(result.FullyConsumedAndVerified);
        Assert.False(result.OriginalReceiptProven); Assert.False(result.BrokerLatencyCalibrated); Assert.False(result.MarketImpactCalibrated);
        Assert.False(result.FillEligibilityCertified); Assert.False(result.HistoricalSecurityIdentityProven);
    }

    [Fact]
    public async Task ExplicitConditionVocabularyRecognizesCodesWithoutExcludingOddLotsOrCertifyingFills()
    {
        using var fixture = new ArchiveFixture();
        var q = await fixture.Write("quotes", [Quote(Origin + 1)]);
        var t = await fixture.Write("trades", [Trade(Origin + 2), Trade(Origin + 3, conditions: ["UNKNOWN"])]);
        var vocabulary = new SipConditionVocabulary("reviewed-vocabulary-fixture-v1", new string('a', 64), ["R"], ["@", "I"]);
        var result = await SipExecutionCalibrationInventory.MeasureAsync(q, t, 1_000_000, vocabulary);
        Assert.Equal(0, result.QuoteRowsWithUnrecognizedConditions); Assert.Equal(1, result.TradeRowsWithUnrecognizedConditions);
        Assert.Equal(1, result.UnrecognizedTradeConditionCounts["UNKNOWN"]); Assert.Equal(1, result.OddLotConditionITradeRows);
        Assert.Equal(2, result.TradesInsidePriorQuote); Assert.False(result.FillEligibilityCertified);
    }

    [Fact]
    public async Task DifferentIntervalsCannotBeMergedAndEmptySourcesRemainHonestZeroObservations()
    {
        using var fixture = new ArchiveFixture();
        var q = await fixture.Write("quotes", Array.Empty<JsonElement>()); var t = await fixture.Write("trades", Array.Empty<JsonElement>());
        var empty = await SipExecutionCalibrationInventory.MeasureAsync(q, t, 1_000_000);
        Assert.Equal(0, empty.TradeRows); Assert.Empty(empty.SpreadBpsFloorHistogram); Assert.True(empty.FullyConsumedAndVerified);
        var wrong = t.Identity.Request with { EndExclusive = "2026-05-21T16:06:00Z" };
        await Assert.ThrowsAsync<InvalidDataException>(() => SipExecutionArchiveReader.OpenAsync(t.Identity.Directory, wrong));
    }

    private static JsonElement Quote(long at, decimal bid = 100, decimal ask = 101) => JsonSerializer.SerializeToElement(new {
        t = SipNanosecondTime.Format(at), bp = bid, ap = ask, bs = 1000, @as = 1200, bx = "Q", ax = "P", c = new[] { "R" }, z = "C" });
    private static JsonElement Trade(long at, int id = 1, decimal price = 100.5m, long size = 1, string[]? conditions = null) => JsonSerializer.SerializeToElement(new {
        t = SipNanosecondTime.Format(at), i = id, p = price, s = size, x = "U", c = conditions ?? ["@", "I"], z = "C" });
    private static async Task<List<T>> All<T>(IAsyncEnumerable<T> values) { var result = new List<T>(); await foreach (var value in values) result.Add(value); return result; }

    private sealed class ArchiveFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tf-sip-reader-" + Guid.NewGuid().ToString("N"));
        public async Task<SipExecutionArchiveReader> Write(string kind, params JsonElement[][] pages)
        {
            var directory = Path.Combine(Root, kind); Directory.CreateDirectory(directory);
            var request = new SipArchiveRequest("TEST", kind, "2026-05-21T16:00:00Z", "2026-05-21T16:05:00Z", "2026-10-06");
            await File.WriteAllBytesAsync(Path.Combine(directory, "request.json"), JsonSerializer.SerializeToUtf8Bytes(request));
            var receipts = new List<SipArchivePage>(); string? token = null;
            for (var index = 0; index < pages.Length; index++) {
                var next = index == pages.Length - 1 ? null : "token-" + index;
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?> {
                    [kind] = new Dictionary<string, JsonElement[]> { ["TEST"] = pages[index] }, ["next_page_token"] = next });
                using var output = new MemoryStream();
                using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true)) gzip.Write(bytes);
                var compressed = output.ToArray(); var prefix = $"page-{index + 1:D7}";
                await File.WriteAllBytesAsync(Path.Combine(directory, prefix + ".json.gz"), compressed);
                long? first = pages[index].Length == 0 ? null : SipNanosecondTime.Parse(pages[index][0].GetProperty("t").GetString()!);
                long? last = pages[index].Length == 0 ? null : SipNanosecondTime.Parse(pages[index][^1].GetProperty("t").GetString()!);
                var receipt = new SipArchivePage(index + 1, token, next, new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero), pages[index].Length,
                    first, last, prefix + ".json.gz", Hash(compressed), Hash(bytes), compressed.Length);
                await File.WriteAllBytesAsync(Path.Combine(directory, prefix + ".json"), JsonSerializer.SerializeToUtf8Bytes(receipt));
                receipts.Add(receipt); token = next;
            }
            var manifest = new SipArchiveManifest(SipExecutionArchiveCollector.Version, request, receipts, true,
                receipts.Sum(page => (long)page.Count), SipExecutionArchiveCollector.QuoteSizeContract);
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
            await File.WriteAllBytesAsync(Path.Combine(directory, "manifest.json"), manifestBytes);
            return await SipExecutionArchiveReader.OpenAsync(directory, request, Hash(manifestBytes));
        }
        private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
        public void Dispose() {
            var path = Path.GetFullPath(Root); var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(path).StartsWith("tf-sip-reader-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the task-owned fixture root.");
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }
}
