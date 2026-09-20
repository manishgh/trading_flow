using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingFlow.Contracts.Evidence;
using TradingFlow.Data.Evidence.Collection;

namespace TradingFlow.Tests;

public sealed class NewsReceiptExchangeTests
{
    public static IEnumerable<object[]> Vectors()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "news_receipt_exchange.json")));
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
            yield return [item.GetProperty("name").GetString()!, item.GetProperty("valid").GetBoolean(),
                item.GetProperty("manifest_utf8").GetString()!,
                item.TryGetProperty("payload_base64", out var encoded) ? Convert.FromBase64String(encoded.GetString()!) :
                    Encoding.UTF8.GetBytes(item.GetProperty("payload_utf8").GetString()!)];
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void SharedVectorsHaveMatchingAcceptance(string name, bool valid, string manifest, byte[] payloadBytes)
    {
        _ = name;
        var manifestBytes = Encoding.UTF8.GetBytes(manifest);
        if (!valid)
        {
            Assert.ThrowsAny<Exception>(() => NewsReceipt.Validate(manifestBytes, payloadBytes));
            return;
        }
        var result = NewsReceipt.Validate(manifestBytes, payloadBytes);
        Assert.Equal(manifestBytes, result.ManifestBytes.ToArray());
        Assert.Equal(payloadBytes, result.PayloadBytes.ToArray());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(manifestBytes)), result.ReceiptId);
        Assert.False(result.ObservableAt(new DateTimeOffset(2019, 7, 10, 0, 0, 0, TimeSpan.Zero)));
        Assert.True(result.ObservableAt(result.Manifest.ReceivedAtUtc));
        Assert.False(result.ObservableAt(result.Manifest.ReceivedAtUtc.AddTicks(-10)));
    }

    [Fact]
    public void FileImportPreservesIdentityAndRejectsTampering()
    {
        var vector = Vectors().First();
        var root = Path.Combine(Path.GetTempPath(), "news-receipt-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var manifestPath = Path.Combine(root, "receipt.json");
            var payloadPath = Path.Combine(root, "payload.json");
            File.WriteAllBytes(manifestPath, Encoding.UTF8.GetBytes((string)vector[2]));
            File.WriteAllBytes(payloadPath, (byte[])vector[3]);
            var pin = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes((string)vector[2])));
            var first = NewsReceiptImporter.Read(manifestPath, payloadPath, pin);
            var again = NewsReceiptImporter.Read(manifestPath, payloadPath, pin);
            Assert.Equal(first.ReceiptId, again.ReceiptId);
            Assert.Equal(first.Manifest.ReceivedAtUtc, again.Manifest.ReceivedAtUtc);
            File.AppendAllText(payloadPath, " ");
            Assert.Throws<ArgumentException>(() => NewsReceiptImporter.Read(manifestPath, payloadPath, pin));
            File.WriteAllText(manifestPath, ((string)vector[2]).Replace("2026-09-17T10:00:00", "2019-07-10T10:00:00", StringComparison.Ordinal));
            Assert.Throws<InvalidDataException>(() => NewsReceiptImporter.Read(manifestPath, payloadPath, pin));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SizeLimitsPrecedeJsonParsing()
    {
        Assert.Throws<ArgumentException>(() => NewsReceipt.Validate(new byte[NewsReceipt.MaximumManifestBytes + 1], "{}"u8));
        Assert.Throws<ArgumentException>(() => NewsReceipt.Validate("{}"u8, new byte[NewsReceipt.MaximumPayloadBytes + 1]));
    }
}
