using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TradingFlow.Alpaca;

public enum SipArchiveEventKind { Quote, Trade }

/// <summary>Provider observations only. No condition code or price comparison certifies fill eligibility.</summary>
public sealed record SipArchiveEvent(string Symbol, SipArchiveEventKind Kind, long TimestampNanoseconds,
    string? ProviderId, decimal? Bid, decimal? Ask, long? BidSizeShares, long? AskSizeShares,
    decimal? TradePrice, long? TradeSizeShares, string? BidExchange, string? AskExchange,
    string? TradeExchange, ImmutableArray<string> Conditions, string? Tape, string ContentSha256,
    string SourceManifestSha256, int Page, int RowIndex);

/// <summary>Same timestamp is a batch, not evidence of quote-before-trade order.</summary>
public sealed record SipArchiveEventBatch(string Symbol, long TimestampNanoseconds,
    ImmutableArray<SipArchiveEvent> Quotes, ImmutableArray<SipArchiveEvent> Trades);

public sealed record SipArchiveReaderIdentity(string Directory, string ManifestSha256, string RequestSha256,
    SipArchiveRequest Request, ImmutableArray<SipArchivePage> Pages, ImmutableArray<string> PageReceiptSha256,
    long Rows, string QuoteSizeContract, DateTimeOffset EarliestCapturedUtc, DateTimeOffset LatestCapturedUtc);

/// <summary>
/// Bounded page streaming over the original gzip archive; no duplicate JSON-text store or provider calls.
/// Full archive verification completes only when enumeration completes. Do not publish partial enumeration as complete.
/// </summary>
public sealed class SipExecutionArchiveReader
{
    private static readonly JsonSerializerOptions Json = new() {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true };
    private static readonly long ShareSizeStart = SipNanosecondTime.Parse("2025-11-03T00:00:00Z");
    public SipArchiveReaderIdentity Identity { get; }
    private readonly long start;
    private readonly long end;

    private SipExecutionArchiveReader(SipArchiveReaderIdentity identity) {
        Identity = identity; start = SipNanosecondTime.Parse(identity.Request.StartInclusive); end = SipNanosecondTime.Parse(identity.Request.EndExclusive);
    }

    public static async Task<SipExecutionArchiveReader> OpenAsync(string directory,
        SipArchiveRequest? expectedRequest = null, string? expectedManifestSha256 = null, CancellationToken ct = default)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var manifestBytes = await File.ReadAllBytesAsync(Bound(directory, "manifest.json"), ct);
        var manifestSha = Hash(manifestBytes);
        if (expectedManifestSha256 is not null && manifestSha != expectedManifestSha256)
            throw new InvalidDataException("SIP manifest differs from its frozen expected SHA256.");
        var manifest = Read<SipArchiveManifest>(manifestBytes);
        var requestBytes = await File.ReadAllBytesAsync(Bound(directory, "request.json"), ct);
        var request = Read<SipArchiveRequest>(requestBytes);
        if (manifest.Request != request || expectedRequest is not null && request != expectedRequest)
            throw new InvalidDataException("SIP manifest/cache/request identity mismatch.");
        var start = SipNanosecondTime.Parse(request.StartInclusive); var end = SipNanosecondTime.Parse(request.EndExclusive);
        if (manifest.Version != SipExecutionArchiveCollector.Version || !manifest.PaginationComplete || manifest.HistoricalReceiptClaim ||
            manifest.QuoteSizeContract != SipExecutionArchiveCollector.QuoteSizeContract || manifest.Rows < 0 ||
            manifest.Pages is null || manifest.Pages.Count == 0 || !Regex.IsMatch(request.Symbol, "^[A-Z][A-Z0-9.\\-]{0,14}$") ||
            request.Kind is not ("quotes" or "trades") || start < ShareSizeStart || end <= start ||
            !DateOnly.TryParseExact(request.MappingAsOf, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new InvalidDataException("SIP source contract, complete pagination, interval or share-size basis is invalid.");
        var pages = manifest.Pages.ToImmutableArray(); var receipts = ImmutableArray.CreateBuilder<string>();
        var tokens = new HashSet<string>(StringComparer.Ordinal); string? priorToken = null; long? priorTime = null; long count = 0;
        for (var index = 0; index < pages.Length; index++) {
            var page = pages[index]; var prefix = $"page-{index + 1:D7}";
            if (page.Page != index + 1 || page.Payload != prefix + ".json.gz" || page.InputToken != priorToken ||
                page.Count < 0 || page.CompressedBytes <= 0 || !IsHash(page.CompressedSha256) || !IsHash(page.UncompressedSha256) ||
                page.CapturedUtc == default || page.CapturedUtc.Offset != TimeSpan.Zero ||
                page.Count == 0 && (page.FirstNanoseconds is not null || page.LastNanoseconds is not null) ||
                page.Count > 0 && (page.FirstNanoseconds is null || page.LastNanoseconds is null || page.FirstNanoseconds < start ||
                    page.LastNanoseconds >= end || page.LastNanoseconds < page.FirstNanoseconds || page.FirstNanoseconds < priorTime) ||
                index < pages.Length - 1 && String.IsNullOrEmpty(page.NextToken) || index == pages.Length - 1 && page.NextToken is not null)
                throw new InvalidDataException("SIP page identity, timestamps, counts or pagination chain is invalid.");
            if (page.NextToken is { } token && !tokens.Add(token)) throw new InvalidDataException("Repeated SIP pagination token.");
            // Keep the comparison in nanoseconds: converting the event to .NET ticks
            // would silently accept an event up to 99 ns after the actual capture.
            var capturedNanoseconds = checked((page.CapturedUtc.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100L);
            if (page.LastNanoseconds is { } last && last > capturedNanoseconds)
                throw new InvalidDataException("SIP page contains a source timestamp after its actual recorded capture.");
            if (end > capturedNanoseconds)
                throw new InvalidDataException("SIP page was captured before the complete requested interval ended; future coverage is unproved.");
            var receipt = await File.ReadAllBytesAsync(Bound(directory, prefix + ".json"), ct);
            if (Read<SipArchivePage>(receipt) != page) throw new InvalidDataException("SIP cached receipt differs from sealed manifest.");
            receipts.Add(Hash(receipt));
            _ = Bound(directory, page.Payload);
            count = checked(count + page.Count); priorToken = page.NextToken; priorTime = page.LastNanoseconds ?? priorTime;
        }
        if (count != manifest.Rows) throw new InvalidDataException("SIP manifest row count differs from its page counts.");
        return new(new(directory, manifestSha, Hash(requestBytes), request, pages, receipts.ToImmutable(), count,
            manifest.QuoteSizeContract, pages.Min(page => page.CapturedUtc), pages.Max(page => page.CapturedUtc)));
    }

    public async IAsyncEnumerable<SipArchiveEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        long? previous = null; long total = 0;
        for (var pageIndex = 0; pageIndex < Identity.Pages.Length; pageIndex++) {
            var page = Identity.Pages[pageIndex];
            var receipt = await File.ReadAllBytesAsync(Bound(Identity.Directory, $"page-{page.Page:D7}.json"), ct);
            if (Hash(receipt) != Identity.PageReceiptSha256[pageIndex]) throw new InvalidDataException("SIP pinned page receipt changed after opening.");
            _ = Bound(Identity.Directory, page.Payload);
            var bytes = await SipExecutionArchiveCollector.ReadVerifiedPageAsync(Identity.Directory, page, ct);
            using var document = JsonDocument.Parse(bytes); Unique(document.RootElement);
            var root = document.RootElement;
            var next = OptionalString(root, "next_page_token", required: true);
            if (next != page.NextToken) throw new InvalidDataException("SIP raw response pagination differs from its receipt.");
            var map = root.GetProperty(Identity.Request.Kind);
            var events = new List<SipArchiveEvent>();
            if (map.ValueKind != JsonValueKind.Null) {
                if (map.ValueKind != JsonValueKind.Object) throw new InvalidDataException("SIP event map must be object or explicit null.");
                foreach (var group in map.EnumerateObject()) {
                    if (group.Name != Identity.Request.Symbol || group.Value.ValueKind != JsonValueKind.Array)
                        throw new InvalidDataException("Unexpected SIP symbol or row collection.");
                    foreach (var row in group.Value.EnumerateArray()) {
                        var item = Parse(row, page.Page, events.Count);
                        if (item.TimestampNanoseconds < start || item.TimestampNanoseconds >= end || item.TimestampNanoseconds < previous)
                            throw new InvalidDataException("SIP events are outside the request or regress across pages.");
                        previous = item.TimestampNanoseconds; events.Add(item);
                    }
                }
            }
            if (events.Count != page.Count || events.FirstOrDefault()?.TimestampNanoseconds != page.FirstNanoseconds ||
                events.LastOrDefault()?.TimestampNanoseconds != page.LastNanoseconds)
                throw new InvalidDataException("SIP raw page rows/time extrema differ from its receipt.");
            total = checked(total + events.Count);
            foreach (var item in events) { ct.ThrowIfCancellationRequested(); yield return item; }
        }
        if (total != Identity.Rows) throw new InvalidDataException("SIP fully consumed row count differs from the sealed manifest.");
        if (Hash(await File.ReadAllBytesAsync(Bound(Identity.Directory, "manifest.json"), ct)) != Identity.ManifestSha256 ||
            Hash(await File.ReadAllBytesAsync(Bound(Identity.Directory, "request.json"), ct)) != Identity.RequestSha256)
            throw new InvalidDataException("SIP pinned manifest/request changed during enumeration.");
    }

    public static async IAsyncEnumerable<SipArchiveEventBatch> MergeAsync(SipExecutionArchiveReader quotes,
        SipExecutionArchiveReader trades, [EnumeratorCancellation] CancellationToken ct = default)
    {
        RequirePair(quotes, trades);
        await using var q = quotes.ReadEventsAsync(ct).GetAsyncEnumerator(ct);
        await using var t = trades.ReadEventsAsync(ct).GetAsyncEnumerator(ct);
        var hasQuote = await q.MoveNextAsync(); var hasTrade = await t.MoveNextAsync();
        while (hasQuote || hasTrade) {
            var at = !hasQuote ? t.Current.TimestampNanoseconds : !hasTrade ? q.Current.TimestampNanoseconds : Math.Min(q.Current.TimestampNanoseconds, t.Current.TimestampNanoseconds);
            var qs = ImmutableArray.CreateBuilder<SipArchiveEvent>(); var ts = ImmutableArray.CreateBuilder<SipArchiveEvent>();
            while (hasQuote && q.Current.TimestampNanoseconds == at) { qs.Add(q.Current); hasQuote = await q.MoveNextAsync(); }
            while (hasTrade && t.Current.TimestampNanoseconds == at) { ts.Add(t.Current); hasTrade = await t.MoveNextAsync(); }
            yield return new(quotes.Identity.Request.Symbol, at, qs.ToImmutable(), ts.ToImmutable());
        }
    }

    internal static void RequirePair(SipExecutionArchiveReader quotes, SipExecutionArchiveReader trades) {
        var q = quotes.Identity.Request; var t = trades.Identity.Request;
        if (q.Kind != "quotes" || t.Kind != "trades" || q.Symbol != t.Symbol || q.StartInclusive != t.StartInclusive ||
            q.EndExclusive != t.EndExclusive || q.MappingAsOf != t.MappingAsOf)
            throw new InvalidDataException("SIP quote/trade pair must have identical symbol, interval and mapping contract.");
    }

    private SipArchiveEvent Parse(JsonElement row, int page, int index)
    {
        var at = SipNanosecondTime.Parse(row.GetProperty("t").GetString()!);
        var conditions = row.TryGetProperty("c", out var codes) && codes.ValueKind != JsonValueKind.Null
            ? codes.EnumerateArray().Select(code => code.GetString() ?? throw new InvalidDataException("Null SIP condition.")).ToImmutableArray() : ImmutableArray<string>.Empty;
        if (conditions.Any(String.IsNullOrEmpty)) throw new InvalidDataException("Empty SIP condition code.");
        var kind = Identity.Request.Kind == "quotes" ? SipArchiveEventKind.Quote : SipArchiveEventKind.Trade;
        return new(Identity.Request.Symbol, kind, at, Scalar(row, "i"),
            kind == SipArchiveEventKind.Quote ? row.GetProperty("bp").GetDecimal() : null,
            kind == SipArchiveEventKind.Quote ? row.GetProperty("ap").GetDecimal() : null,
            kind == SipArchiveEventKind.Quote ? row.GetProperty("bs").GetInt64() : null,
            kind == SipArchiveEventKind.Quote ? row.GetProperty("as").GetInt64() : null,
            kind == SipArchiveEventKind.Trade ? row.GetProperty("p").GetDecimal() : null,
            kind == SipArchiveEventKind.Trade ? row.GetProperty("s").GetInt64() : null,
            Scalar(row, "bx"), Scalar(row, "ax"), Scalar(row, "x"), conditions, Scalar(row, "z"),
            ContentHash(row), Identity.ManifestSha256, page, index);
    }

    private static string? Scalar(JsonElement row, string key) => !row.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null ? null :
        value.ValueKind == JsonValueKind.String ? value.GetString() : value.ValueKind == JsonValueKind.Number ? value.GetRawText() :
        throw new InvalidDataException("SIP identifier/venue/tape must be string, number or absent.");
    private static string? OptionalString(JsonElement value, string key, bool required) {
        if (!value.TryGetProperty(key, out var field)) { if (required) throw new InvalidDataException("Missing SIP pagination field."); return null; }
        return field.ValueKind == JsonValueKind.Null ? null : field.ValueKind == JsonValueKind.String ? field.GetString() : throw new InvalidDataException("Invalid SIP pagination field.");
    }
    private static T Read<T>(byte[] bytes) { using var doc = JsonDocument.Parse(bytes); Unique(doc.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Null SIP archive document."); }
    private static void Unique(JsonElement value) {
        if (value.ValueKind == JsonValueKind.Object) { var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) { if (!keys.Add(property.Name)) throw new InvalidDataException("Duplicate SIP JSON property."); Unique(property.Value); } }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var row in value.EnumerateArray()) Unique(row);
    }
    private static string ContentHash(JsonElement row) {
        using var output = new MemoryStream(); using (var writer = new Utf8JsonWriter(output)) Canonical(writer, row);
        return Hash(output.ToArray());
    }
    private static void Canonical(Utf8JsonWriter writer, JsonElement value) {
        if (value.ValueKind == JsonValueKind.Object) { writer.WriteStartObject(); foreach (var property in value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal)) {
            writer.WritePropertyName(property.Name); Canonical(writer, property.Value); } writer.WriteEndObject(); }
        else if (value.ValueKind == JsonValueKind.Array) { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Canonical(writer, item); writer.WriteEndArray(); }
        else value.WriteTo(writer);
    }
    private static string Bound(string directory, string name) {
        if (Path.GetFileName(name) != name || name is "." or "..") throw new InvalidDataException("Unsafe SIP archive filename.");
        var path = Path.Combine(directory, name);
        for (var entry = path; entry.Length >= directory.Length; entry = Path.GetDirectoryName(entry) ?? "") {
            if ((File.Exists(entry) || Directory.Exists(entry)) && (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("SIP archive paths cannot traverse symbolic links/reparse points.");
            if (entry == directory) break;
        }
        return path;
    }
    private static bool IsHash(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
