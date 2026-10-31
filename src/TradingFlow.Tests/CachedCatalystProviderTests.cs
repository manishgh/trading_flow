using System.Text.Json;
using System.Diagnostics;
using System.Reflection;
using TradingFlow.Data.Catalysts;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Tests;

public sealed class CachedCatalystProviderTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Proves the cache and immutable archive contain the same sorted bytes and unchanged assessment clocks.</summary>
    [Fact]
    public async Task GetCatalystsAsync_ArchivesExactBytesAndPreservesClocks()
    {
        using var fixture = new CacheFixture();
        var before = DateTimeOffset.UtcNow;
        var first = await fixture.Provider("use_cache").GetCatalystsAsync("POET", fixture.Start, fixture.End, CancellationToken.None);
        var after = DateTimeOffset.UtcNow;
        var receipt = await fixture.ReadReceiptAsync();
        var bytes = await File.ReadAllBytesAsync(fixture.CachePath);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(receipt.PayloadPath));
        var stored = JsonSerializer.Deserialize<CatalystEvent[]>(bytes, JsonOptions)!;
        Assert.Equal(first.OrderBy(item => item.Timestamp), stored);
        Assert.Equal(fixture.Start.AddMinutes(20), stored[0].ReceivedAt);
        Assert.Equal(fixture.Start.AddMinutes(30), stored[0].DecisionAvailableAt);
        Assert.Equal("classification-1", stored[0].ClassificationVersion);
        Assert.InRange(receipt.Manifest.ReceivedAtUtc, before, after);
        Assert.Equal(CachedCatalystProvider.AssessmentArtifactType, receipt.Manifest.ArtifactType);
        Assert.Equal(bytes.LongLength, receipt.Manifest.ByteLength);
    }

    /// <summary>Exact and covering cache hits reuse frozen assessments without another fetch or archive publication.</summary>
    [Fact]
    public async Task GetCatalystsAsync_ReusesExactAndCoveringWindowsWithoutReassessment()
    {
        using var fixture = new CacheFixture();
        var provider = fixture.Provider("use_cache");
        await provider.GetCatalystsAsync("POET", fixture.Start, fixture.End, CancellationToken.None);
        var cacheBytes = await File.ReadAllBytesAsync(fixture.CachePath);
        var receiptBytes = await File.ReadAllBytesAsync(fixture.ReceiptPath);
        var exact = await provider.GetCatalystsAsync("POET", fixture.Start, fixture.End, CancellationToken.None);
        var covering = await provider.GetCatalystsAsync("POET", fixture.Start.AddMinutes(45), fixture.End.AddHours(-1), CancellationToken.None);
        Assert.Equal(2, exact.Count);
        Assert.Equal(exact, covering);
        Assert.Equal(1, fixture.Inner.CallCount);
        Assert.Equal(cacheBytes, await File.ReadAllBytesAsync(fixture.CachePath));
        Assert.Equal(receiptBytes, await File.ReadAllBytesAsync(fixture.ReceiptPath));
        Assert.Equal(2, Directory.GetFiles(fixture.ArchiveRoot, "*", SearchOption.AllDirectories).Length);
    }

    /// <summary>A failed archive write cannot publish a new cache or its proof.</summary>
    [Fact]
    public async Task GetCatalystsAsync_ArchiveFailureDoesNotPublishCache()
    {
        using var fixture = new CacheFixture();
        await Assert.ThrowsAsync<IOException>(() => fixture.Provider("use_cache", new FailingArchiveWriter())
            .GetCatalystsAsync("POET", fixture.Start, fixture.End, CancellationToken.None));
        Assert.False(File.Exists(fixture.CachePath));
        Assert.False(File.Exists(fixture.ReceiptPath));
        Assert.Equal(1, fixture.Inner.CallCount);
    }

    /// <summary>Legacy originals remain readable byte-for-byte and cannot be refreshed without independently verified preservation.</summary>
    [Fact]
    public async Task GetCatalystsAsync_PreservesLegacyHitsAndRefusesLegacyRefresh()
    {
        using var fixture = new CacheFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.CachePath)!);
        var original = JsonSerializer.SerializeToUtf8Bytes(fixture.Inner.Events("POET", fixture.Start), JsonOptions);
        await File.WriteAllBytesAsync(fixture.CachePath, original);
        var read = await fixture.Provider("use_cache").GetCatalystsAsync("POET", fixture.Start, fixture.End, CancellationToken.None);
        Assert.Equal(2, read.Count);
        var covering = await fixture.Provider("cache_only").GetCatalystsAsync("POET", fixture.Start.AddMinutes(45), fixture.End.AddHours(-1), CancellationToken.None);
        Assert.Equal(read, covering);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Provider("refresh")
            .GetCatalystsAsync("POET", fixture.Start, fixture.End, CancellationToken.None));
        Assert.Equal(0, fixture.Inner.CallCount);
        Assert.Equal(original, await File.ReadAllBytesAsync(fixture.CachePath));
        Assert.False(File.Exists(fixture.ReceiptPath));
        Assert.False(Directory.Exists(fixture.ArchiveRoot));
    }

    /// <summary>Refreshing a proven cache keeps both immutable assessment versions, including their original clocks.</summary>
    [Fact]
    public async Task GetCatalystsAsync_ArchivedRefreshPreservesBothVersions()
    {
        using var fixture = new CacheFixture();
        await fixture.Provider("use_cache").GetCatalystsAsync("POET", fixture.Start, fixture.End, CancellationToken.None);
        var first = await fixture.ReadReceiptAsync();
        var firstBytes = await File.ReadAllBytesAsync(first.PayloadPath);
        await fixture.Provider("refresh").GetCatalystsAsync("POET", fixture.Start, fixture.End, CancellationToken.None);
        var second = await fixture.ReadReceiptAsync();
        Assert.NotEqual(first.PayloadPath, second.PayloadPath);
        Assert.Equal(firstBytes, await File.ReadAllBytesAsync(first.PayloadPath));
        Assert.Equal(await File.ReadAllBytesAsync(fixture.CachePath), await File.ReadAllBytesAsync(second.PayloadPath));
        Assert.Equal(2, fixture.Inner.CallCount);
        Assert.Equal(4, Directory.GetFiles(fixture.ArchiveRoot, "*", SearchOption.AllDirectories).Length);
        Assert.Contains("classification-1", await File.ReadAllTextAsync(first.PayloadPath));
        Assert.Contains("classification-2", await File.ReadAllTextAsync(second.PayloadPath));
    }

    /// <summary>Refresh archive failures leave the previous cache, sidecar and durable assessments intact.</summary>
    [Fact]
    public async Task GetCatalystsAsync_RefreshArchiveFailurePreservesExistingCache()
    {
        using var fixture = new CacheFixture();
        await fixture.Provider("use_cache").GetCatalystsAsync("POET", fixture.Start, fixture.End, CancellationToken.None);
        var original = await File.ReadAllBytesAsync(fixture.CachePath);
        var receipt = await File.ReadAllBytesAsync(fixture.ReceiptPath);
        await Assert.ThrowsAsync<IOException>(() => fixture.Provider("refresh", new FailingArchiveWriter())
            .GetCatalystsAsync("POET", fixture.Start, fixture.End, CancellationToken.None));
        Assert.Equal(original, await File.ReadAllBytesAsync(fixture.CachePath));
        Assert.Equal(receipt, await File.ReadAllBytesAsync(fixture.ReceiptPath));
    }

    /// <summary>Cache, receipt, manifest and payload damage must fail before reuse or another provider assessment.</summary>
    [Theory]
    [InlineData("cache", "use_cache")]
    [InlineData("cache", "refresh")]
    [InlineData("receipt", "use_cache")]
    [InlineData("manifest", "use_cache")]
    [InlineData("payload", "use_cache")]
    public async Task GetCatalystsAsync_RefusesTamperedArchiveBoundCache(string target, string policy)
    {
        using var fixture = new CacheFixture();
        await fixture.Provider("use_cache").GetCatalystsAsync("POET", fixture.Start, fixture.End, CancellationToken.None);
        var receipt = await fixture.ReadReceiptAsync();
        var path = target switch
        {
            "cache" => fixture.CachePath,
            "receipt" => fixture.ReceiptPath,
            "manifest" => receipt.ManifestPath,
            _ => receipt.PayloadPath
        };
        await File.WriteAllTextAsync(path, "null");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Provider(policy)
            .GetCatalystsAsync("POET", fixture.Start, fixture.End, CancellationToken.None));
        Assert.Equal(1, fixture.Inner.CallCount);
        Assert.Equal("null", await File.ReadAllTextAsync(path));
    }

    /// <summary>Cache-only misses never fetch or classify provider news.</summary>
    [Fact]
    public async Task GetCatalystsAsync_CacheOnlyMissFailsWithoutCallingInnerProvider()
    {
        using var fixture = new CacheFixture();
        await Assert.ThrowsAsync<FileNotFoundException>(() => fixture.Provider("cache_only")
            .GetCatalystsAsync("POET", fixture.Start, fixture.End, CancellationToken.None));
        Assert.Equal(0, fixture.Inner.CallCount);
    }

    /// <summary>Internally consistent proof is rejected when it names the cache itself, another run's copy or an unconfigured archive.</summary>
    [Theory]
    [InlineData("self")]
    [InlineData("run-owned")]
    [InlineData("outside-root")]
    public async Task GetCatalystsAsync_RefusesProofOutsideConfiguredDurableOwner(string target)
    {
        using var fixture = new CacheFixture();
        await fixture.Provider("use_cache").GetCatalystsAsync("POET", fixture.Start, fixture.End, CancellationToken.None);
        var bytes = await File.ReadAllBytesAsync(fixture.CachePath);
        var receipt = await fixture.ReadReceiptAsync();
        if (target == "self")
        {
            receipt = receipt with { PayloadPath = fixture.CachePath, ManifestPath = fixture.CachePath + ".manifest.json",
                Manifest = receipt.Manifest with { PayloadFileName = Path.GetFileName(fixture.CachePath) } };
            await File.WriteAllBytesAsync(receipt.ManifestPath, JsonSerializer.SerializeToUtf8Bytes(receipt.Manifest, JsonOptions));
        }
        else
        {
            var otherRoot = Path.Combine(fixture.Root, target == "run-owned" ? ".run-inputs-untrusted" : "other-archive");
            receipt = await new FileSystemRawArchiveWriter(new RawArchiveOptions(otherRoot)).ArchiveAsync(
                new("test", CachedCatalystProvider.AssessmentArtifactType, DateTimeOffset.UtcNow, "json", "application/json"), bytes);
        }
        await File.WriteAllBytesAsync(fixture.ReceiptPath, JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Provider("refresh")
            .GetCatalystsAsync("POET", fixture.Start, fixture.End, CancellationToken.None));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.CachePath));
        Assert.Equal(1, fixture.Inner.CallCount);
    }

    /// <summary>Even a configured root cannot make a disposable run-input sibling into a durable archive owner.</summary>
    [Fact]
    public async Task GetCatalystsAsync_RefusesArchiveOwnerInsideRunWorkspace()
    {
        using var fixture = new CacheFixture();
        var workspace = Path.Combine(fixture.Root, ".run-inputs-synthetic");
        var raw = Path.Combine(workspace, "raw");
        var provider = new CachedCatalystProvider(fixture.Inner, Path.Combine(workspace, "normalized"), "use_cache",
            new FileSystemRawArchiveWriter(new RawArchiveOptions(raw)), raw);
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetCatalystsAsync("POET", fixture.Start, fixture.End, CancellationToken.None));
        Assert.Equal(0, fixture.Inner.CallCount);
    }

    /// <summary>A canonical-looking archived path cannot escape through a junction to a disposable external copy.</summary>
    [Fact]
    public async Task GetCatalystsAsync_RefusesArchiveJunctionEscape()
    {
        using var fixture = new CacheFixture();
        await fixture.Provider("use_cache").GetCatalystsAsync("POET", fixture.Start, fixture.End, CancellationToken.None);
        var receipt = await fixture.ReadReceiptAsync();
        var day = Path.GetDirectoryName(receipt.PayloadPath)!;
        var external = Path.Combine(fixture.Root, "untrusted-day-copy");
        Directory.Move(day, external);
        CreateDirectoryLink(day, external);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Provider("refresh")
                .GetCatalystsAsync("POET", fixture.Start, fixture.End, CancellationToken.None));
            Assert.Equal(1, fixture.Inner.CallCount);
        }
        finally { Directory.Delete(day); }
    }

    /// <summary>Two cache instances for the same key hold one filesystem lease across fetch, archival and both publications.</summary>
    [Fact]
    public async Task GetCatalystsAsync_ConcurrentSameKeyMissFetchesAndArchivesOnce()
    {
        using var fixture = new CacheFixture();
        var blocking = new BlockingArchiveWriter(new FileSystemRawArchiveWriter(new RawArchiveOptions(fixture.ArchiveRoot)));
        var first = fixture.Provider("use_cache", blocking).GetCatalystsAsync("POET", fixture.Start, fixture.End, CancellationToken.None);
        await blocking.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = fixture.Provider("use_cache").GetCatalystsAsync("POET", fixture.Start, fixture.End, CancellationToken.None);
        try
        {
            Assert.False(second.IsCompleted);
            Assert.Equal(1, fixture.Inner.CallCount);
        }
        finally { blocking.Release.TrySetResult(); }
        var results = await Task.WhenAll(first, second);
        Assert.Equal(results[0].OrderBy(item => item.Timestamp), results[1]);
        Assert.Equal(1, fixture.Inner.CallCount);
        Assert.Equal(2, Directory.GetFiles(fixture.ArchiveRoot, "*", SearchOption.AllDirectories).Length);
    }

    /// <summary>Exact and covering readers wait for an in-flight refresh and see its matching completed pair.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetCatalystsAsync_ReadersWaitForArchiveBoundRefresh(bool covering)
    {
        using var fixture = new CacheFixture();
        await fixture.Provider("use_cache").GetCatalystsAsync("POET", fixture.Start, fixture.End, CancellationToken.None);
        var blocking = new BlockingArchiveWriter(new FileSystemRawArchiveWriter(new RawArchiveOptions(fixture.ArchiveRoot)));
        var refresh = fixture.Provider("refresh", blocking).GetCatalystsAsync("POET", fixture.Start, fixture.End, CancellationToken.None);
        await blocking.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var read = fixture.Provider("use_cache").GetCatalystsAsync("POET", covering ? fixture.Start.AddMinutes(45) : fixture.Start,
            covering ? fixture.End.AddHours(-1) : fixture.End, CancellationToken.None);
        try { Assert.False(read.IsCompleted); }
        finally { blocking.Release.TrySetResult(); }
        await refresh;
        Assert.All(await read, item => Assert.Equal("classification-2", item.ClassificationVersion));
        Assert.Equal(2, fixture.Inner.CallCount);
    }

    /// <summary>A legacy original arriving during provider collection is retained and cannot acquire a fabricated archive proof.</summary>
    [Fact]
    public async Task GetCatalystsAsync_RefusesLegacyOriginalAppearingDuringCollection()
    {
        using var fixture = new CacheFixture();
        var blocking = new BlockingArchiveWriter(new FileSystemRawArchiveWriter(new RawArchiveOptions(fixture.ArchiveRoot)));
        var fetch = fixture.Provider("use_cache", blocking).GetCatalystsAsync("POET", fixture.Start, fixture.End, CancellationToken.None);
        await blocking.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var legacy = JsonSerializer.SerializeToUtf8Bytes(fixture.Inner.Events("POET", fixture.Start), JsonOptions);
        await File.WriteAllBytesAsync(fixture.CachePath, legacy);
        blocking.Release.TrySetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fetch);
        Assert.Equal(legacy, await File.ReadAllBytesAsync(fixture.CachePath));
        Assert.False(File.Exists(fixture.ReceiptPath));
    }

    /// <summary>The synchronous publication boundary refuses a legacy target arriving after all asynchronous checks and staging.</summary>
    [Fact]
    public async Task InitialPublication_ExclusiveMovePreservesLateLegacyOriginal()
    {
        using var fixture = new CacheFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.CachePath)!);
        var staged = fixture.CachePath + ".synthetic-staged";
        var stagedReceipt = fixture.ReceiptPath + ".synthetic-staged";
        await File.WriteAllTextAsync(staged, "new archived assessment");
        await File.WriteAllTextAsync(stagedReceipt, "new archive proof");
        await File.WriteAllTextAsync(fixture.CachePath, "late original assessment");
        // Reflection targets only the private atomic commit boundary; no runtime injection hook is added to production.
        var publish = typeof(CachedCatalystProvider).GetMethod("PublishCachePair", BindingFlags.Static | BindingFlags.NonPublic)!;
        var exception = Assert.Throws<TargetInvocationException>(() => publish.Invoke(null,
            [staged, fixture.CachePath, stagedReceipt, fixture.ReceiptPath, false]));
        Assert.IsType<IOException>(exception.InnerException);
        Assert.Equal("late original assessment", await File.ReadAllTextAsync(fixture.CachePath));
        Assert.False(File.Exists(fixture.ReceiptPath));
        Assert.True(File.Exists(staged));
        Assert.True(File.Exists(stagedReceipt));
    }

    /// <summary>Creates only an isolated test link; Windows junctions do not require developer-mode privileges.</summary>
    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        var start = new ProcessStartInfo("cmd.exe") { CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, "Synthetic junction creation failed: " + process.StandardError.ReadToEnd());
    }

    private sealed class CacheFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "tradingflow-catalyst-archive-tests", Guid.NewGuid().ToString("N"));
        public string Root => root;
        public DateTimeOffset Start { get; } = DateTimeOffset.Parse("2026-06-01T00:00:00Z");
        public DateTimeOffset End => Start.AddDays(1);
        public CountingCatalystProvider Inner { get; } = new();
        public string ArchiveRoot => Path.Combine(root, "archive");
        public string CachePath => Path.Combine(root, "cache", "POET", "catalysts_test_202606010000_202606020000.json");
        public string ReceiptPath => CachePath + CachedCatalystProvider.ArchiveReceiptSuffix;

        /// <summary>Creates only isolated synthetic cache/archive providers under this test's owned temporary directory.</summary>
        public CachedCatalystProvider Provider(string policy, IRawArchiveWriter? writer = null) =>
            new(Inner, Path.Combine(root, "cache"), policy,
                writer ?? new FileSystemRawArchiveWriter(new RawArchiveOptions(ArchiveRoot)), ArchiveRoot);

        /// <summary>Reads the cache's immutable archive reference without touching operator data.</summary>
        public async Task<RawArchiveReceipt> ReadReceiptAsync() =>
            JsonSerializer.Deserialize<RawArchiveReceipt>(await File.ReadAllBytesAsync(ReceiptPath), JsonOptions)!;

        /// <summary>Removes only the unique synthetic workspace created by this fixture after its providers finish.</summary>
        public void Dispose()
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class CountingCatalystProvider : ICatalystProvider
    {
        public int CallCount { get; private set; }
        public string ProviderName => "test";

        /// <summary>Counts synthetic fetch/classification calls and returns a new identifiable assessment version.</summary>
        public Task<IReadOnlyList<CatalystEvent>> GetCatalystsAsync(string ticker, DateTimeOffset windowStart,
            DateTimeOffset windowEnd, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(Events(ticker, windowStart));
        }

        /// <summary>Returns deliberately reversed events with explicit source, receipt and assessment clocks.</summary>
        public IReadOnlyList<CatalystEvent> Events(string ticker, DateTimeOffset start) =>
        [
            Event(ticker, start.AddHours(2), start),
            Event(ticker, start.AddHours(1), start)
        ];

        /// <summary>Builds deterministic source text and separate receipt/classification availability for round-trip checks.</summary>
        private CatalystEvent Event(string ticker, DateTimeOffset timestamp, DateTimeOffset start) =>
            new(ticker, timestamp, CatalystType.NewsReport, "Synthetic café headline", 0.82m,
                ProviderName, "article-" + timestamp.Hour, "Synthetic archived text", "testwire", "https://example.test/news",
                ReceivedAt: start.AddMinutes(20), UpdatedAt: start.AddMinutes(10),
                AvailabilityEvidence: CatalystAvailabilityEvidence.ObservedReceiptTime,
                DecisionAvailableAt: start.AddMinutes(30),
                DecisionAvailabilityEvidence: CatalystAvailabilityEvidence.NewsAndAssessmentObservedTime,
                ClassificationVersion: "classification-" + CallCount);
    }

    private sealed class FailingArchiveWriter : IRawArchiveWriter
    {
        /// <summary>Simulates durable archive failure before any cache publication.</summary>
        public Task<RawArchiveReceipt> ArchiveAsync(RawArchiveRequest request, ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default) => throw new IOException("Synthetic archive write failure.");

        /// <summary>Never expires any archive, including this isolated fixture.</summary>
        public Task<RawArchiveRetentionResult> EnforceRetentionAsync(DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class BlockingArchiveWriter(IRawArchiveWriter inner) : IRawArchiveWriter
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Pauses one synthetic archive call so concurrency and late-original tests select the exact execution interval.</summary>
        public async Task<RawArchiveReceipt> ArchiveAsync(RawArchiveRequest request, ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return await inner.ArchiveAsync(request, payload, cancellationToken);
        }

        /// <summary>Never expires preserved assessments while exercising isolated cache ownership.</summary>
        public Task<RawArchiveRetentionResult> EnforceRetentionAsync(DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
