using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TradingFlow.Alpaca;

/// <summary>A raw, read-only source archive. Capture time is not a historical receipt claim.</summary>
public sealed record SipArchiveRequest(string Symbol, string Kind, string StartInclusive,
    string EndExclusive, string MappingAsOf);
public sealed record SipArchivePage(int Page, string? InputToken, string? NextToken,
    DateTimeOffset CapturedUtc, int Count, long? FirstNanoseconds, long? LastNanoseconds,
    string Payload, string CompressedSha256, string UncompressedSha256, long CompressedBytes);
public sealed record SipArchiveManifest(string Version, SipArchiveRequest Request,
    IReadOnlyList<SipArchivePage> Pages, bool PaginationComplete, long Rows,
    string QuoteSizeContract, bool HistoricalReceiptClaim = false);

public static partial class SipNanosecondTime
{
    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,9}))?Z$")]
    private static partial Regex TimestampPattern();

    public static long Parse(string value)
    {
        var match = TimestampPattern().Match(value);
        if (!match.Success) throw new InvalidDataException("Expected UTC RFC3339 with at most nine fractional digits.");
        var seconds = DateTimeOffset.ParseExact(match.Groups[1].Value + "Z", "yyyy-MM-dd'T'HH:mm:ss'Z'",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUnixTimeSeconds();
        var fraction = match.Groups[2].Value.PadRight(9, '0');
        return checked(seconds * 1_000_000_000L + long.Parse(fraction, CultureInfo.InvariantCulture));
    }

    public static string Format(long value)
    {
        var seconds = Math.DivRem(value, 1_000_000_000L, out var nanos);
        if (nanos < 0) { seconds--; nanos += 1_000_000_000L; }
        return DateTimeOffset.FromUnixTimeSeconds(seconds).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)
            + "." + nanos.ToString("D9", CultureInfo.InvariantCulture) + "Z";
    }
}

/// <summary>Credentials belong to the supplied HttpClient headers; never serialized to the archive.</summary>
public sealed class SipExecutionArchiveCollector(HttpClient client)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public const string Version = "alpaca-sip-raw-nanosecond-archive-v1";
    public const string QuoteSizeContract = "alpaca-cta-utp-shares-from-2025-11-03";
    private static readonly long ShareSizeStart = SipNanosecondTime.Parse("2025-11-03T00:00:00Z");
    // Market data endpoints are profile-neutral; the resolver still owns the address.
    private static readonly Uri MarketDataRest = AlpacaEndpointResolver.Resolve(TradingFlow.Engine.Configuration.ProductionProfile.Paper).MarketDataRest;

    public async Task<SipArchiveManifest> CollectAsync(SipArchiveRequest request, string directory,
        long minimumFreeBytes = 2_000_000_000, int maximumPages = 1_000_000,
        Action<SipArchivePage>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!Regex.IsMatch(request.Symbol, "^[A-Z][A-Z0-9.\\-]{0,14}$") ||
            request.Kind is not ("quotes" or "trades")) throw new ArgumentException("Invalid symbol or source kind.");
        var start = SipNanosecondTime.Parse(request.StartInclusive);
        var end = SipNanosecondTime.Parse(request.EndExclusive);
        if (end <= start || start < ShareSizeStart || maximumPages <= 0 || minimumFreeBytes < 0)
            throw new ArgumentException("Invalid interval or unsupported pre-share-size historical contract.");
        if (end > checked((DateTimeOffset.UtcNow.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100L))
            throw new ArgumentException("A historical execution archive cannot certify a future interval.");
        _ = DateOnly.ParseExact(request.MappingAsOf, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        Directory.CreateDirectory(directory);
        var requestPath = Path.Combine(directory, "request.json");
        if (File.Exists(requestPath))
        {
            var existing = JsonSerializer.Deserialize<SipArchiveRequest>(await File.ReadAllTextAsync(requestPath, cancellationToken));
            if (existing != request) throw new InvalidDataException("Archive request identity changed.");
        }
        else await WriteExclusive(requestPath, request, cancellationToken);

        var pages = new List<SipArchivePage>();
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;
        long? previousTimestamp = null;
        while (pages.Count < maximumPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var number = pages.Count + 1;
            var prefix = $"page-{number:D7}";
            var receiptPath = Path.Combine(directory, prefix + ".json");
            SipArchivePage page;
            if (File.Exists(receiptPath))
            {
                page = JsonSerializer.Deserialize<SipArchivePage>(await File.ReadAllTextAsync(receiptPath, cancellationToken))
                    ?? throw new InvalidDataException("Null cached receipt.");
                if (page.Page != number || page.InputToken != token || page.Payload != prefix + ".json.gz")
                    throw new InvalidDataException("Cached pagination identity changed.");
                var cached = await ReadVerifiedPageAsync(directory, page, cancellationToken);
                var audit = Inspect(cached, request, start, end);
                if (audit.Count != page.Count || audit.First != page.FirstNanoseconds || audit.Last != page.LastNanoseconds ||
                    audit.Next != page.NextToken) throw new InvalidDataException("Cached receipt disagrees with raw response.");
            }
            else
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!);
                if (drive.AvailableFreeSpace < minimumFreeBytes)
                    throw new IOException($"Execution archive stopped: free bytes {drive.AvailableFreeSpace}, required reserve {minimumFreeBytes}.");
                // Alpaca's end is inclusive. Subtract ONE NANOSECOND, not one .NET tick (100 ns).
                var query = $"symbols={Uri.EscapeDataString(request.Symbol)}&start={Uri.EscapeDataString(request.StartInclusive)}" +
                    $"&end={Uri.EscapeDataString(SipNanosecondTime.Format(end - 1))}&feed=sip&sort=asc&limit=10000" +
                    $"&asof={request.MappingAsOf}" + (token is null ? "" : "&page_token=" + Uri.EscapeDataString(token));
                var bytes = await FetchAsync($"{MarketDataRest.AbsoluteUri}v2/stocks/{request.Kind}?{query}", cancellationToken);
                var audit = Inspect(bytes, request, start, end);
                var compressed = Compress(bytes);
                var payloadPath = Path.Combine(directory, prefix + ".json.gz");
                if (File.Exists(payloadPath))
                    throw new InvalidDataException("Unreceipted raw page exists; investigate before resuming this archive.");
                await using (var stream = new FileStream(payloadPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await stream.WriteAsync(compressed, cancellationToken);
                page = new(number, token, audit.Next, DateTimeOffset.UtcNow, audit.Count, audit.First, audit.Last,
                    Path.GetFileName(payloadPath), Hash(compressed), Hash(bytes), compressed.LongLength);
                await WriteExclusive(receiptPath, page, cancellationToken);
            }
            if (previousTimestamp is not null && page.FirstNanoseconds < previousTimestamp)
                throw new InvalidDataException("Provider timestamp order regressed across pages.");
            previousTimestamp = page.LastNanoseconds ?? previousTimestamp;
            pages.Add(page);
            progress?.Invoke(page);
            token = page.NextToken;
            if (token is null)
            {
                var manifest = new SipArchiveManifest(Version, request, pages, true, pages.Sum(p => (long)p.Count), QuoteSizeContract);
                var manifestPath = Path.Combine(directory, "manifest.json");
                if (!File.Exists(manifestPath)) await WriteExclusive(manifestPath, manifest, cancellationToken);
                else if (await File.ReadAllTextAsync(manifestPath, cancellationToken) != JsonSerializer.Serialize(manifest, Json))
                    throw new InvalidDataException("Sealed archive manifest changed.");
                return manifest;
            }
            if (!tokens.Add(token)) throw new InvalidDataException("Repeated pagination token.");
        }
        throw new InvalidDataException("Execution archive page ceiling reached; pagination incomplete.");
    }

    public static async Task<byte[]> ReadVerifiedPageAsync(string directory, SipArchivePage page, CancellationToken ct = default)
    {
        if (Path.GetFileName(page.Payload) != page.Payload) throw new InvalidDataException("Unsafe archive page path.");
        var compressed = await File.ReadAllBytesAsync(Path.Combine(directory, page.Payload), ct);
        if (compressed.LongLength != page.CompressedBytes || Hash(compressed) != page.CompressedSha256)
            throw new InvalidDataException("Raw compressed source hash differs.");
        using var input = new GZipStream(new MemoryStream(compressed), CompressionMode.Decompress);
        using var output = new MemoryStream();
        await input.CopyToAsync(output, ct);
        var bytes = output.ToArray();
        if (Hash(bytes) != page.UncompressedSha256) throw new InvalidDataException("Uncompressed source hash differs.");
        return bytes;
    }

    private static (int Count, long? First, long? Last, string? Next) Inspect(byte[] bytes,
        SipArchiveRequest request, long start, long end)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        var map = root.GetProperty(request.Kind);
        long? first = null, last = null;
        var count = 0;
        if (map.ValueKind != JsonValueKind.Null)
        foreach (var group in map.EnumerateObject())
        {
            if (group.Name != request.Symbol) throw new InvalidDataException("Unexpected symbol in SIP response.");
            foreach (var row in group.Value.EnumerateArray())
            {
                var ns = SipNanosecondTime.Parse(row.GetProperty("t").GetString()!);
                if (ns < start || ns >= end || (last is not null && ns < last))
                    throw new InvalidDataException("Out-of-range or unordered SIP event.");
                first ??= ns; last = ns; count++;
            }
        }
        var next = root.GetProperty("next_page_token");
        return (count, first, last, next.ValueKind == JsonValueKind.Null ? null : next.GetString());
    }

    private async Task<byte[]> FetchAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode) return await response.Content.ReadAsByteArrayAsync(ct);
            if (attempt >= 4 || (response.StatusCode != HttpStatusCode.TooManyRequests && (int)response.StatusCode < 500))
                throw new HttpRequestException($"SIP source request failed with HTTP {(int)response.StatusCode}.");
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt + 1))), ct);
        }
    }

    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) gzip.Write(bytes);
        return output.ToArray();
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static async Task WriteExclusive<T>(string path, T value, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, value, Json, ct);
    }
}
