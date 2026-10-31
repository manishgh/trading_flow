using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using TradingFlow.Engine.Configuration;

namespace TradingFlow.Alpaca;

public sealed record SipMetadataCapture(string Kind, string? Tape, string PayloadPath,
    string ReceiptPath, string PayloadSha256, string ReceiptSha256);

/// <summary>Shared form of the metadata-capture tool: retain original provider bytes and UTC receipts, never historical receipt claims.</summary>
public sealed class SipExecutionMetadataCollector(HttpClient client, ProductionProfile profile)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Capture the real exchange calendar; weekday arithmetic never supplies a trading session.</summary>
    public Task<SipMetadataCapture> CaptureCalendarAsync(DateOnly start, DateOnly end, string directory,
        CancellationToken cancellationToken = default)
    {
        if (end < start) throw new ArgumentException("Calendar end precedes start.");
        var uri = new Uri(AlpacaEndpointResolver.Resolve(profile).TradingRest,
            $"v2/calendar?start={start:yyyy-MM-dd}&end={end:yyyy-MM-dd}");
        return CaptureAsync(uri, "calendar", null, directory, "expanded-calendar", start, end, cancellationToken);
    }

    /// <summary>Preserve all six official condition-code dictionaries; classification is a separate explicit contract.</summary>
    public async Task<IReadOnlyList<SipMetadataCapture>> CaptureConditionsAsync(string directory,
        CancellationToken cancellationToken = default)
    {
        var captures = new List<SipMetadataCapture>();
        foreach (var tape in new[] { "A", "B", "C" })
        foreach (var kind in new[] { "quote", "trade" })
        {
            var uri = new Uri(AlpacaEndpointResolver.Resolve(profile).MarketDataRest,
                $"v2/stocks/meta/conditions/{kind}?tape={tape}");
            captures.Add(await CaptureAsync(uri, kind, tape, directory, kind + "-" + tape,
                null, null, cancellationToken));
        }
        return captures;
    }

    /// <summary>Write a new capture only; an earlier attempt's metadata is never overwritten.</summary>
    private async Task<SipMetadataCapture> CaptureAsync(Uri uri, string kind, string? tape, string directory,
        string name, DateOnly? start, DateOnly? end, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        using var response = await client.GetAsync(uri, cancellationToken);
        var captured = DateTimeOffset.UtcNow;
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Execution metadata HTTP {(int)response.StatusCode} for {kind}/{tape ?? "calendar"}.");
        using var parsed = JsonDocument.Parse(bytes);
        if (kind == "calendar" ? parsed.RootElement.ValueKind != JsonValueKind.Array : parsed.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Execution metadata response has the wrong JSON shape.");
        var payloadPath = Path.GetFullPath(Path.Combine(directory, name + ".json"));
        var receiptPath = Path.GetFullPath(Path.Combine(directory, name + ".receipt.json"));
        var payloadHash = Hash(bytes);
        await WriteExclusiveAsync(payloadPath, bytes, cancellationToken);
        object receipt = kind == "calendar"
            ? new { Uri = uri, HttpStatus = (int)response.StatusCode, CapturedUtc = captured,
                Start = start!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                End = end!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Sha256 = payloadHash,
                RowCount = parsed.RootElement.GetArrayLength(), OriginalHistoricalReceiptClaim = false }
            : new { Uri = uri, HttpStatus = (int)response.StatusCode, CapturedUtc = captured,
                Payload = Path.GetFileName(payloadPath), Sha256 = payloadHash,
                CodeCount = parsed.RootElement.EnumerateObject().Count(), OriginalHistoricalReceiptClaim = false };
        var receiptBytes = JsonSerializer.SerializeToUtf8Bytes(receipt, Json);
        await WriteExclusiveAsync(receiptPath, receiptBytes, cancellationToken);
        return new(kind, tape, payloadPath, receiptPath, payloadHash, Hash(receiptBytes));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static async Task WriteExclusiveAsync(string path, byte[] bytes, CancellationToken token)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await output.WriteAsync(bytes, token);
    }
}
