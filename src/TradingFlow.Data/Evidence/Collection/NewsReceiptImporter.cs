using System.Security.Cryptography;
using TradingFlow.Contracts.Evidence;

namespace TradingFlow.Data.Evidence.Collection;

/// <summary>Read a portable raw receipt without assigning local import time to the source observation.</summary>
public static class NewsReceiptImporter
{
    public static ValidatedNewsReceipt Read(string manifestPath, string payloadPath, string expectedReceiptSha256)
    {
        var manifest = ReadBounded(manifestPath, NewsReceipt.MaximumManifestBytes);
        if (expectedReceiptSha256 is null || expectedReceiptSha256.Length != 64 ||
            !String.Equals(Convert.ToHexStringLower(SHA256.HashData(manifest)), expectedReceiptSha256, StringComparison.Ordinal))
            throw new InvalidDataException("News receipt manifest does not match its independent integrity pin.");
        return NewsReceipt.Validate(manifest, ReadBounded(payloadPath, NewsReceipt.MaximumPayloadBytes));
    }

    private static byte[] ReadBounded(string path, int maximumBytes)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > maximumBytes) throw new InvalidDataException("News receipt file exceeds its byte limit.");
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = stream.Read(chunk, 0, Math.Min(chunk.Length, maximumBytes + 1 - checked((int)buffer.Length)));
            if (read == 0) break;
            buffer.Write(chunk, 0, read);
            if (buffer.Length > maximumBytes) throw new InvalidDataException("News receipt file exceeds its byte limit.");
        }
        return buffer.ToArray();
    }
}
