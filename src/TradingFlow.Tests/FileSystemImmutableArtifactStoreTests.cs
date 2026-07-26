using System.Security.Cryptography;
using System.Text;
using TradingFlow.Data.Evidence;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Tests;

public sealed class FileSystemImmutableArtifactStoreTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "trading-flow-evidence-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PutIfAbsentAsync_PublishesAndReusesVerifiedObject()
    {
        var store = CreateStore();
        var bytes = Encoding.UTF8.GetBytes("{\"value\":1}");
        var artifact = Artifact(bytes, "raw/alpaca", "application/json");

        var created = await store.PutIfAbsentAsync(new(artifact), bytes);
        var reused = await store.PutIfAbsentAsync(new(artifact), bytes);
        var verification = await store.VerifyAsync(artifact);
        await using var stream = await store.OpenReadAsync(artifact);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);

        Assert.False(created.AlreadyExisted);
        Assert.True(reused.AlreadyExisted);
        Assert.True(verification.IsValid);
        Assert.Equal(bytes, buffer.ToArray());
        Assert.Equal(created.ObjectKey, reused.ObjectKey);
    }

    [Fact]
    public async Task PutIfAbsentAsync_RejectsHashOrLengthMismatch()
    {
        var store = CreateStore();
        var bytes = Encoding.UTF8.GetBytes("actual");
        var wrongHash = new EvidenceArtifactReference(
            new EvidenceContentAddress(new string('a', 64), bytes.Length, "text/plain"),
            new EvidenceObjectNamespace("raw"));
        var wrongLength = new EvidenceArtifactReference(
            new EvidenceContentAddress(Hash(bytes), bytes.Length + 1, "text/plain"),
            new EvidenceObjectNamespace("raw"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.PutIfAbsentAsync(new(wrongHash), bytes));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.PutIfAbsentAsync(new(wrongLength), bytes));
    }

    [Fact]
    public async Task VerifyAsync_DetectsTamperedContent()
    {
        var store = CreateStore();
        var bytes = Encoding.UTF8.GetBytes("evidence");
        var artifact = Artifact(bytes, "raw", "text/plain");
        var receipt = await store.PutIfAbsentAsync(new(artifact), bytes);
        var contentPath = Path.Combine(
            rootPath,
            receipt.ObjectKey.Replace('/', Path.DirectorySeparatorChar),
            "content.bin");
        await File.WriteAllTextAsync(contentPath, "tampered");

        var verification = await store.VerifyAsync(artifact);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.PutIfAbsentAsync(new(artifact), bytes));

        Assert.False(verification.IsValid);
        Assert.Contains("do not match", verification.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ParallelWriters_CommitOneVerifiedObject()
    {
        var firstStore = CreateStore();
        var secondStore = CreateStore();
        var bytes = Encoding.UTF8.GetBytes(new string('x', 32_768));
        var artifact = Artifact(bytes, "partitions", "application/x-parquet");

        var receipts = await Task.WhenAll(
            firstStore.PutIfAbsentAsync(new(artifact), bytes),
            secondStore.PutIfAbsentAsync(new(artifact), bytes));

        Assert.Single(receipts, receipt => !receipt.AlreadyExisted);
        Assert.Single(receipts, receipt => receipt.AlreadyExisted);
        Assert.True((await firstStore.VerifyAsync(artifact)).IsValid);
    }

    [Fact]
    public async Task ScanAsync_ReturnsPublishedObjectsAndIgnoresTemporaryDirectories()
    {
        var store = CreateStore();
        var first = Artifact(Encoding.UTF8.GetBytes("first"), "raw/alpaca", "text/plain");
        var second = Artifact(Encoding.UTF8.GetBytes("second"), "raw/alpaca", "text/plain");
        await store.PutIfAbsentAsync(new(first), Encoding.UTF8.GetBytes("first"));
        await store.PutIfAbsentAsync(new(second), Encoding.UTF8.GetBytes("second"));
        Directory.CreateDirectory(Path.Combine(rootPath, "raw", "alpaca", "aa", ".tmp-incomplete"));

        var values = new List<ImmutableArtifactScanEntry>();
        await foreach (var value in store.ScanAsync(new EvidenceObjectNamespace("raw/alpaca")))
        {
            values.Add(value);
        }

        Assert.Equal(2, values.Count);
        Assert.All(values, value => Assert.True(value.IsValid));
        Assert.Equal(
            new[] { first.Content.Sha256, second.Content.Sha256 }
                .OrderBy(value => value, StringComparer.Ordinal),
            values.Select(value => value.DeclaredArtifact!.Content.Sha256)
                .OrderBy(value => value, StringComparer.Ordinal));
    }

    [Fact]
    public async Task ScanAsync_CompletesVerifiedDeleteDirectoryLeftByCrash()
    {
        var store = CreateStore();
        var bytes = Encoding.UTF8.GetBytes("delete-after-crash");
        var artifact = Artifact(bytes, "raw/alpaca", "text/plain");
        var receipt = await store.PutIfAbsentAsync(new(artifact), bytes);
        var objectDirectory = ObjectDirectory(receipt);
        var stranded = Path.Combine(
            Path.GetDirectoryName(objectDirectory)!,
            $".delete-{artifact.Content.Sha256}-{Guid.NewGuid():N}");
        Directory.Move(objectDirectory, stranded);

        var entries = await ScanAsync(store);

        Assert.Empty(entries);
        Assert.False(Directory.Exists(stranded));
        Assert.False((await store.VerifyAsync(artifact)).IsValid);
    }

    [Fact]
    public async Task ScanAsync_RemovesOnlyStaleStoreTemporaryDirectories()
    {
        var store = new FileSystemImmutableArtifactStore(
            new ImmutableArtifactStoreOptions(
                rootPath,
                temporaryDirectoryGracePeriod: TimeSpan.FromMinutes(5)));
        var hash = new string('a', 64);
        var shard = Path.Combine(rootPath, "raw", "alpaca", "aa");
        var stale = Path.Combine(shard, $".tmp-{hash}-{Guid.NewGuid():N}");
        var fresh = Path.Combine(shard, $".tmp-{hash}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stale);
        Directory.CreateDirectory(fresh);
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-1));

        var entries = await ScanAsync(store);

        Assert.Empty(entries);
        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(fresh));
    }

    [Fact]
    public async Task ScanAsync_LeavesMalformedDeleteRemnantVisibleAndFailClosed()
    {
        var store = CreateStore();
        var hash = new string('b', 64);
        var directory = Path.Combine(
            rootPath,
            "raw",
            "alpaca",
            "bb",
            $".delete-{hash}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "content.bin"), "wrong");
        await File.WriteAllTextAsync(Path.Combine(directory, "metadata.json"), "{broken");

        var entry = Assert.Single(await ScanAsync(store));

        Assert.False(entry.IsValid);
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public async Task ScanAsync_ReportsMalformedMetadataAndContinuesToValidObject()
    {
        var store = CreateStore();
        var corrupt = Artifact(Encoding.UTF8.GetBytes("corrupt-metadata"), "raw/alpaca", "text/plain");
        var valid = Artifact(Encoding.UTF8.GetBytes("valid-metadata"), "raw/alpaca", "text/plain");
        var corruptReceipt = await store.PutIfAbsentAsync(
            new(corrupt),
            Encoding.UTF8.GetBytes("corrupt-metadata"));
        await store.PutIfAbsentAsync(new(valid), Encoding.UTF8.GetBytes("valid-metadata"));
        await File.WriteAllTextAsync(
            Path.Combine(ObjectDirectory(corruptReceipt), "metadata.json"),
            "{broken-json");

        var entries = await ScanAsync(store, new EvidenceObjectNamespace("raw/alpaca"));

        Assert.Equal(2, entries.Count);
        Assert.Contains(
            entries,
            entry => entry.Findings.Any(finding =>
                finding.Code == ImmutableArtifactScanIssue.MetadataMalformed));
        Assert.Contains(entries, entry => entry.IsValid);
    }

    [Fact]
    public async Task ScanAsync_ReportsNamespaceAndHashPathMismatchWithoutFollowingMetadataPath()
    {
        var store = CreateStore();
        var bytes = Encoding.UTF8.GetBytes("path-mismatch");
        var artifact = Artifact(bytes, "raw/alpaca", "text/plain");
        var receipt = await store.PutIfAbsentAsync(new(artifact), bytes);
        var originalDirectory = ObjectDirectory(receipt);
        var falseHash = new string(artifact.Content.Sha256[0] == 'a' ? 'b' : 'a', 64);
        var falseDirectory = Path.Combine(
            rootPath,
            "raw",
            "finviz",
            falseHash[..2],
            falseHash);
        Directory.CreateDirectory(Path.GetDirectoryName(falseDirectory)!);
        Directory.Move(originalDirectory, falseDirectory);

        var entry = Assert.Single(await ScanAsync(store));

        Assert.Contains(
            entry.Findings,
            finding => finding.Code == ImmutableArtifactScanIssue.NamespacePathMismatch);
        Assert.Contains(
            entry.Findings,
            finding => finding.Code == ImmutableArtifactScanIssue.HashPathMismatch);
        Assert.Equal(Hash(bytes), entry.ActualSha256);
    }

    [Fact]
    public async Task ScanAsync_ReportsSameLengthContentCorruptionAndContinues()
    {
        var store = CreateStore();
        var corruptBytes = Encoding.UTF8.GetBytes("alpha");
        var validBytes = Encoding.UTF8.GetBytes("bravo");
        var corrupt = Artifact(corruptBytes, "raw/alpaca", "text/plain");
        var valid = Artifact(validBytes, "raw/alpaca", "text/plain");
        var receipt = await store.PutIfAbsentAsync(new(corrupt), corruptBytes);
        await store.PutIfAbsentAsync(new(valid), validBytes);
        await File.WriteAllBytesAsync(
            Path.Combine(ObjectDirectory(receipt), "content.bin"),
            Encoding.UTF8.GetBytes("other"));

        var entries = await ScanAsync(store, new EvidenceObjectNamespace("raw/alpaca"));

        Assert.Equal(2, entries.Count);
        var corruptEntry = Assert.Single(
            entries,
            entry => entry.Findings.Any(finding =>
                finding.Code == ImmutableArtifactScanIssue.ContentHashMismatch));
        Assert.DoesNotContain(
            corruptEntry.Findings,
            finding => finding.Code == ImmutableArtifactScanIssue.ContentLengthMismatch);
        Assert.Contains(entries, entry => entry.IsValid);
    }

    [Fact]
    public async Task ScanAsync_ReportsMissingMetadataAndMissingContent()
    {
        var store = CreateStore();
        var metadataMissingBytes = Encoding.UTF8.GetBytes("metadata-missing");
        var contentMissingBytes = Encoding.UTF8.GetBytes("content-missing");
        var metadataMissing = Artifact(metadataMissingBytes, "raw/alpaca", "text/plain");
        var contentMissing = Artifact(contentMissingBytes, "raw/alpaca", "text/plain");
        var metadataReceipt = await store.PutIfAbsentAsync(new(metadataMissing), metadataMissingBytes);
        var contentReceipt = await store.PutIfAbsentAsync(new(contentMissing), contentMissingBytes);
        File.Delete(Path.Combine(ObjectDirectory(metadataReceipt), "metadata.json"));
        File.Delete(Path.Combine(ObjectDirectory(contentReceipt), "content.bin"));

        var entries = await ScanAsync(store, new EvidenceObjectNamespace("raw/alpaca"));

        Assert.Contains(
            entries,
            entry => entry.Findings.Any(finding =>
                finding.Code == ImmutableArtifactScanIssue.MetadataMissing));
        Assert.Contains(
            entries,
            entry => entry.Findings.Any(finding =>
                finding.Code == ImmutableArtifactScanIssue.ContentMissing));
    }

    [Fact]
    public async Task ScanAsync_ReportsMissingArtifactRootAsIncomplete()
    {
        var store = CreateStore();
        Directory.Delete(rootPath, recursive: true);

        var entry = Assert.Single(await ScanAsync(store));

        Assert.False(entry.IsValid);
        Assert.Contains(
            entry.Findings,
            finding => finding.Code == ImmutableArtifactScanIssue.RootUnavailable);
    }

    [Fact]
    public void Options_RejectOperationalStorageOverlap()
    {
        var operational = Path.Combine(rootPath, "data", "candles");

        Assert.Throws<ArgumentException>(() =>
            new ImmutableArtifactStoreOptions(
                Path.Combine(operational, "evidence"),
                [operational]));
    }

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private FileSystemImmutableArtifactStore CreateStore() =>
        new(new ImmutableArtifactStoreOptions(rootPath));

    private string ObjectDirectory(ImmutableArtifactReceipt receipt) =>
        Path.Combine(
            rootPath,
            receipt.ObjectKey.Replace('/', Path.DirectorySeparatorChar));

    private static async Task<List<ImmutableArtifactScanEntry>> ScanAsync(
        IImmutableArtifactMaintenanceScanner scanner,
        EvidenceObjectNamespace? objectNamespace = null)
    {
        var entries = new List<ImmutableArtifactScanEntry>();
        await foreach (var entry in scanner.ScanAsync(objectNamespace))
        {
            entries.Add(entry);
        }

        return entries;
    }

    private static EvidenceArtifactReference Artifact(
        byte[] bytes,
        string objectNamespace,
        string mediaType) =>
        new(
            new EvidenceContentAddress(Hash(bytes), bytes.Length, mediaType),
            new EvidenceObjectNamespace(objectNamespace));

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
