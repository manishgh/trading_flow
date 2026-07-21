using System.Security.Cryptography;
using System.Text.Json;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Tests;

public sealed class RawArchiveWriterTests
{
    [Fact]
    public async Task ArchiveAsync_RoundTripsBytesAndPersistsReplayManifest()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var writer = new FileSystemRawArchiveWriter(new RawArchiveOptions(root));
            byte[] payload = [0x00, 0xFF, 0xC3, 0x28, 0x0A, 0x0D, 0x7F];
            var receivedAt = new DateTimeOffset(2026, 7, 21, 12, 34, 56, TimeSpan.Zero);
            var request = new RawArchiveRequest(
                "finviz",
                "screener_csv",
                receivedAt,
                "csv",
                "text/csv",
                PresetId: "volatile-v1",
                CorrelationId: "pull-42",
                RunId: "run-7",
                ConfigHash: "config-hash",
                CodeVersion: "git-sha",
                Http: new RawArchiveHttpMetadata(
                    new Uri("https://elite.finviz.com/export.ashx?v=111&auth=super-secret"),
                    200,
                    new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["Content-Type"] = ["text/csv"],
                        ["Authorization"] = ["Bearer secret"]
                    }),
                Attributes: new Dictionary<string, string> { ["preset_version"] = "1" });

            var receipt = await writer.ArchiveAsync(request, payload);

            Assert.Equal(payload, await File.ReadAllBytesAsync(receipt.PayloadPath));
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
                receipt.Manifest.Sha256);
            Assert.Equal(payload.LongLength, receipt.Manifest.ByteLength);
            Assert.Contains(Path.Combine("finviz", "2026-07-21"), receipt.PayloadPath);
            Assert.Equal("volatile-v1", receipt.Manifest.PresetId);
            Assert.Equal("1", receipt.Manifest.Attributes["preset_version"]);
            Assert.DoesNotContain("auth=", receipt.Manifest.Http!.RequestUri!.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Authorization", receipt.Manifest.Http.Headers!.Keys, StringComparer.OrdinalIgnoreCase);

            var persistedManifestBytes = await File.ReadAllBytesAsync(receipt.ManifestPath);
            var persistedManifestText = System.Text.Encoding.UTF8.GetString(persistedManifestBytes);
            Assert.DoesNotContain("super-secret", persistedManifestText, StringComparison.Ordinal);
            Assert.DoesNotContain("Bearer secret", persistedManifestText, StringComparison.Ordinal);

            var persistedManifest = JsonSerializer.Deserialize<RawArchiveManifest>(
                persistedManifestBytes,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(persistedManifest);
            Assert.Equivalent(receipt.Manifest, persistedManifest, strict: true);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task ArchiveAsync_ConcurrentWritesNeverOverwrite()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var writer = new FileSystemRawArchiveWriter(new RawArchiveOptions(root));
            var request = new RawArchiveRequest(
                "alpaca",
                "news_payload",
                new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero),
                "json");

            var receipts = await Task.WhenAll(
                Enumerable.Range(0, 32)
                    .Select(index => writer.ArchiveAsync(request, BitConverter.GetBytes(index))));

            Assert.Equal(32, receipts.Select(receipt => receipt.PayloadPath).Distinct().Count());
            Assert.Equal(32, receipts.Select(receipt => receipt.ManifestPath).Distinct().Count());
            Assert.All(receipts, receipt => Assert.True(File.Exists(receipt.PayloadPath)));
            Assert.All(receipts, receipt => Assert.True(File.Exists(receipt.ManifestPath)));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Theory]
    [InlineData("../alpaca", "news")]
    [InlineData("alpaca", "../news")]
    [InlineData("alpaca/news", "payload")]
    [InlineData("alpaca", "news payload")]
    public async Task ArchiveAsync_RejectsUnsafePathSegments(string source, string artifactType)
    {
        var root = CreateTemporaryRoot();
        try
        {
            var writer = new FileSystemRawArchiveWriter(new RawArchiveOptions(root));
            var request = new RawArchiveRequest(
                source,
                artifactType,
                DateTimeOffset.UtcNow,
                "json");

            await Assert.ThrowsAsync<ArgumentException>(() => writer.ArchiveAsync(request, new byte[] { 1, 2, 3 }));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task ArchiveAsync_RejectsSecretAttributesBeforeWriting()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var writer = new FileSystemRawArchiveWriter(new RawArchiveOptions(root));
            var request = new RawArchiveRequest(
                "alpaca",
                "broker_response",
                DateTimeOffset.UtcNow,
                "json",
                Attributes: new Dictionary<string, string> { ["api_key"] = "must-not-persist" });

            await Assert.ThrowsAsync<ArgumentException>(() => writer.ArchiveAsync(request, new byte[] { 1, 2, 3 }));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task ArchiveAsync_PreCancelledWriteLeavesNoTemporaryFiles()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var writer = new FileSystemRawArchiveWriter(new RawArchiveOptions(root));
            var request = new RawArchiveRequest("alpaca", "news_payload", DateTimeOffset.UtcNow, "json");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => writer.ArchiveAsync(request, new byte[1024], cancellation.Token));

            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task ArchiveAsync_RejectsMissingReceiveTimestampBeforeWriting()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var writer = new FileSystemRawArchiveWriter(new RawArchiveOptions(root));
            var request = new RawArchiveRequest("alpaca", "news_payload", default, "json");

            await Assert.ThrowsAsync<ArgumentException>(
                () => writer.ArchiveAsync(request, new byte[] { 1, 2, 3 }));

            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task EnforceRetentionAsync_DeletesOnlyExpiredDatedDirectories()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var writer = new FileSystemRawArchiveWriter(new RawArchiveOptions(root, 730));
            await writer.ArchiveAsync(
                new RawArchiveRequest("finviz", "csv", new DateTimeOffset(2024, 7, 20, 1, 0, 0, TimeSpan.Zero), "csv"),
                new byte[] { 1 });
            await writer.ArchiveAsync(
                new RawArchiveRequest("finviz", "csv", new DateTimeOffset(2024, 7, 22, 1, 0, 0, TimeSpan.Zero), "csv"),
                new byte[] { 2 });
            var unrelatedDirectory = Path.Combine(root, "finviz", "manual-review");
            Directory.CreateDirectory(unrelatedDirectory);
            await File.WriteAllTextAsync(Path.Combine(unrelatedDirectory, "keep.txt"), "keep");

            var result = await writer.EnforceRetentionAsync(
                new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));

            Assert.Equal(new DateOnly(2024, 7, 21), result.DeleteBeforeDate);
            Assert.Equal(2, result.DateDirectoriesExamined);
            Assert.Equal(1, result.DateDirectoriesDeleted);
            Assert.False(Directory.Exists(Path.Combine(root, "finviz", "2024-07-20")));
            Assert.True(Directory.Exists(Path.Combine(root, "finviz", "2024-07-22")));
            Assert.True(File.Exists(Path.Combine(unrelatedDirectory, "keep.txt")));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public void Options_RejectRetentionBelowBindingMinimum()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new RawArchiveOptions(root, 729));
            Assert.Contains("730", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "trading-flow-raw-archive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTemporaryRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
