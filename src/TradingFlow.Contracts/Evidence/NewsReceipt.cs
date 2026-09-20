using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TradingFlow.Contracts.Evidence;

/// <summary>Raw transport receipt only. It does not establish issuer attribution or trading admission.</summary>
public sealed record NewsPageRequest(string Symbol, DateTimeOffset StartUtc, DateTimeOffset EndUtc,
    string? PageToken, bool IncludeContent, int Limit);

public sealed record NewsHttpReceipt(string Producer, string ProducerRevision, NewsPageRequest Request,
    DateTimeOffset ReceivedAtUtc, string PayloadSha256, int PayloadBytes);

public sealed class ValidatedNewsReceipt
{
    internal ValidatedNewsReceipt(NewsHttpReceipt manifest, byte[] manifestBytes, byte[] payloadBytes)
    {
        Manifest = manifest;
        ManifestBytes = manifestBytes;
        PayloadBytes = payloadBytes;
        ReceiptId = Convert.ToHexStringLower(SHA256.HashData(manifestBytes));
    }

    public NewsHttpReceipt Manifest { get; }
    public ReadOnlyMemory<byte> ManifestBytes { get; }
    public ReadOnlyMemory<byte> PayloadBytes { get; }
    public string ReceiptId { get; }

    public bool ObservableAt(DateTimeOffset cutoff)
    {
        if (cutoff.Offset != TimeSpan.Zero || cutoff.Ticks % 10 != 0)
            throw new ArgumentException("Cutoff must be UTC with microsecond precision.", nameof(cutoff));
        return Manifest.ReceivedAtUtc <= cutoff;
    }
}

public static partial class NewsReceipt
{
    public const int MaximumManifestBytes = 16_384;
    public const int MaximumPayloadBytes = 8_388_608;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static ValidatedNewsReceipt Validate(ReadOnlySpan<byte> manifestBytes, ReadOnlySpan<byte> payloadBytes)
    {
        if (manifestBytes.Length is < 1 or > MaximumManifestBytes || payloadBytes.Length is < 1 or > MaximumPayloadBytes)
            throw new ArgumentException("News receipt exceeds its byte limits.");
        _ = StrictUtf8.GetCharCount(manifestBytes);
        _ = StrictUtf8.GetCharCount(payloadBytes);
        using var manifestDocument = JsonDocument.Parse(manifestBytes.ToArray());
        var root = manifestDocument.RootElement;
        ValidateJson(root);
        RequireFields(root, "schema_version", "producer", "producer_revision", "endpoint", "transport", "status_code",
            "byte_representation", "availability_basis", "request", "received_at_utc", "payload_sha256", "payload_bytes");
        Require(root, "schema_version", "alpaca.news_http_receipt.v1");
        Require(root, "endpoint", "alpaca.news");
        Require(root, "transport", "http");
        Require(root, "byte_representation", "decoded_http_body_utf8");
        Require(root, "availability_basis", "observed_receipt");
        var producer = Text(root, "producer");
        if (producer is not ("market_predictor" or "trading_flow") || root.GetProperty("status_code").GetInt32() != 200)
            throw new ArgumentException("Unsupported news receipt producer or HTTP status.");
        var revision = Text(root, "producer_revision");
        var digest = Text(root, "payload_sha256");
        if (!RevisionPattern().IsMatch(revision) || !HashPattern().IsMatch(digest))
            throw new ArgumentException("Invalid news receipt digest or producer revision.");
        var length = root.GetProperty("payload_bytes").GetInt32();
        if (length != payloadBytes.Length || !String.Equals(digest, Convert.ToHexStringLower(SHA256.HashData(payloadBytes)), StringComparison.Ordinal))
            throw new ArgumentException("News receipt payload integrity mismatch.");
        var request = root.GetProperty("request");
        RequireFields(request, "symbol", "start_utc", "end_utc", "page_token", "include_content", "limit");
        var symbol = Text(request, "symbol");
        var start = Clock(request, "start_utc");
        var end = Clock(request, "end_utc");
        var received = Clock(root, "received_at_utc");
        var limit = request.GetProperty("limit").GetInt32();
        if (!SymbolPattern().IsMatch(symbol) || start >= end || received < end || limit is < 1 or > 50)
            throw new ArgumentException("Invalid news request bounds.");
        var token = PageToken(request.GetProperty("page_token"));
        var includeContent = request.GetProperty("include_content").GetBoolean();
        using var payloadDocument = JsonDocument.Parse(payloadBytes.ToArray());
        var body = payloadDocument.RootElement;
        ValidateJson(body);
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("news", out var news) ||
            news.ValueKind != JsonValueKind.Array || news.GetArrayLength() > limit ||
            news.EnumerateArray().Any(row => row.ValueKind != JsonValueKind.Object))
            throw new ArgumentException("News HTTP body must contain a bounded news array.");
        if (body.TryGetProperty("next_page_token", out var next)) _ = PageToken(next);
        return new ValidatedNewsReceipt(
            new NewsHttpReceipt(producer, revision, new NewsPageRequest(symbol, start, end, token, includeContent, limit), received, digest, length),
            manifestBytes.ToArray(), payloadBytes.ToArray());
    }

    private static string Text(JsonElement obj, string key) => obj.GetProperty(key).GetString()
        ?? throw new ArgumentException($"Missing text field {key}.");

    private static void Require(JsonElement obj, string key, string expected)
    {
        if (!String.Equals(Text(obj, key), expected, StringComparison.Ordinal))
            throw new ArgumentException($"Unsupported news receipt {key}.");
    }

    private static DateTimeOffset Clock(JsonElement obj, string key)
    {
        var text = Text(obj, key);
        if (!ClockPattern().IsMatch(text) || !DateTimeOffset.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
            throw new ArgumentException("Receipt clocks require UTC with six fractional digits and Z.");
        return value;
    }

    private static string? PageToken(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null) return null;
        var value = element.GetString();
        if (value is null || value.Length is < 1 or > 2048 || value.Any(c => c < '!' || c > '~'))
            throw new ArgumentException("Invalid news page token.");
        return value;
    }

    private static void RequireFields(JsonElement obj, params string[] fields)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal).SetEquals(fields))
            throw new ArgumentException("News receipt has missing or unknown fields.");
    }

    private static void ValidateJson(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new ArgumentException("Duplicate JSON key.");
                ValidateJson(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray()) ValidateJson(item);
        }
        else if (node.ValueKind == JsonValueKind.Number && (!node.TryGetDouble(out var number) || !Double.IsFinite(number)))
            throw new ArgumentException("Non-finite JSON number.");
        else if (node.ValueKind == JsonValueKind.String)
            _ = StrictUtf8.GetByteCount(node.GetString()!);
    }

    [GeneratedRegex("\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)] private static partial Regex HashPattern();
    [GeneratedRegex("\\A[0-9a-f]{40}\\z", RegexOptions.CultureInvariant)] private static partial Regex RevisionPattern();
    [GeneratedRegex("\\A[A-Z][A-Z0-9.-]{0,14}\\z", RegexOptions.CultureInvariant)] private static partial Regex SymbolPattern();
    [GeneratedRegex("\\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\\.[0-9]{6}Z\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ClockPattern();
}
