using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using TradingFlow.Data.Csv;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Tests;

public sealed class CachedMarketDataManifestSliceTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 3, 13, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddMinutes(10);

    [Fact]
    public async Task ExactValidatedSlice_IsReusedWithoutCallingProviderAgain()
    {
        using var directory = new TemporaryDirectory();
        var descriptor = Descriptor("alpaca", "sip", "all");
        var first = new FakeProvider(descriptor, [Bar(Start), Bar(Start.AddMinutes(5))]);
        var second = new FakeProvider(descriptor, [Bar(Start, 999m)]);

        var written = await ReadAllAsync(new CachedMarketDataProvider(first, directory.Path, "reuse", descriptor));
        var reused = await ReadAllAsync(new CachedMarketDataProvider(second, directory.Path, "reuse", descriptor));

        Assert.Equal(2, written.Count);
        Assert.Equal(written, reused);
        Assert.Equal(1, first.CallCount);
        Assert.Equal(0, second.CallCount);
        var manifestPath = Path.Combine(directory.Path, "TEST", "bars_5m.csv.manifest.json");
        Assert.True(File.Exists(manifestPath));
        var dataPath = await ResolveCommittedDataPathAsync(manifestPath);
        Assert.True(File.Exists(dataPath));
        Assert.False(File.Exists(Path.Combine(directory.Path, "TEST", "bars_5m.csv")));
    }

    [Fact]
    public async Task ExpiredSlice_IsRefetchedUsingDeterministicClock()
    {
        using var directory = new TemporaryDirectory();
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero));
        var policy = new MarketDataCacheFreshnessPolicy(
            "test_one_hour_v1",
            TimeSpan.FromHours(1),
            clock);
        var descriptor = Descriptor("alpaca", "sip", "all");
        await ReadAllAsync(new CachedMarketDataProvider(
            new FakeProvider(descriptor, [Bar(Start, 101m)]),
            directory.Path,
            "reuse",
            descriptor,
            policy));

        clock.Advance(TimeSpan.FromHours(1));
        var replacement = new FakeProvider(descriptor, [Bar(Start, 109m)]);
        var refreshed = await ReadAllAsync(new CachedMarketDataProvider(
            replacement,
            directory.Path,
            "reuse",
            descriptor,
            policy));

        Assert.Equal(1, replacement.CallCount);
        Assert.Equal(109m, Assert.Single(refreshed).Close);
    }

    [Fact]
    public async Task RestatementRevisionChange_RefetchesBeforeExpiry()
    {
        using var directory = new TemporaryDirectory();
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero));
        var policy = new MarketDataCacheFreshnessPolicy(
            "test_one_day_v1",
            TimeSpan.FromDays(1),
            clock);
        var original = Descriptor("alpaca", "sip", "all", "corporate_actions_100");
        await ReadAllAsync(new CachedMarketDataProvider(
            new FakeProvider(original, [Bar(Start, 101m)]),
            directory.Path,
            "reuse",
            original,
            policy));

        var restated = Descriptor("alpaca", "sip", "all", "corporate_actions_101");
        var replacement = new FakeProvider(restated, [Bar(Start, 51m)]);
        var refreshed = await ReadAllAsync(new CachedMarketDataProvider(
            replacement,
            directory.Path,
            "reuse",
            restated,
            policy));

        Assert.Equal(1, replacement.CallCount);
        Assert.Equal(51m, Assert.Single(refreshed).Close);
    }

    [Fact]
    public async Task MissingSourceDescriptor_FailsClosedAndRefetches()
    {
        using var directory = new TemporaryDirectory();
        var first = new UndescribedProvider([Bar(Start)]);
        var second = new UndescribedProvider([Bar(Start, 103m)]);

        await ReadAllAsync(new CachedMarketDataProvider(first, directory.Path, "reuse"));
        var result = await ReadAllAsync(new CachedMarketDataProvider(second, directory.Path, "reuse"));

        Assert.Equal(1, first.CallCount);
        Assert.Equal(1, second.CallCount);
        Assert.Equal(103m, Assert.Single(result).Close);
    }

    [Fact]
    public async Task DifferentExactUtcRange_RefetchesInsteadOfSlicingPriorManifest()
    {
        using var directory = new TemporaryDirectory();
        var descriptor = Descriptor("alpaca", "sip", "all");
        var first = new FakeProvider(descriptor, [Bar(Start)]);
        await ReadAllAsync(new CachedMarketDataProvider(first, directory.Path, "reuse", descriptor));

        var shiftedStart = Start.AddMinutes(5);
        var shiftedEnd = End.AddMinutes(5);
        var second = new FakeProvider(descriptor, [Bar(shiftedStart, 102m)]);
        var result = await ReadAllAsync(
            new CachedMarketDataProvider(second, directory.Path, "reuse", descriptor),
            shiftedStart,
            shiftedEnd);

        Assert.Equal(1, second.CallCount);
        Assert.Equal(102m, Assert.Single(result).Close);
    }

    [Theory]
    [InlineData("other-provider", "sip", "all")]
    [InlineData("alpaca", "iex", "all")]
    [InlineData("alpaca", "sip", "raw")]
    public async Task SourceContractMismatch_Refetches(
        string providerIdentity,
        string feed,
        string adjustment)
    {
        using var directory = new TemporaryDirectory();
        var original = Descriptor("alpaca", "sip", "all");
        await ReadAllAsync(new CachedMarketDataProvider(
            new FakeProvider(original, [Bar(Start)]),
            directory.Path,
            "reuse",
            original));

        var changed = Descriptor(providerIdentity, feed, adjustment);
        var replacement = new FakeProvider(
            changed,
            [Bar(Start, 107m, feed, adjustment)]);
        var result = await ReadAllAsync(new CachedMarketDataProvider(
            replacement,
            directory.Path,
            "reuse",
            changed));

        Assert.Equal(1, replacement.CallCount);
        Assert.Equal(107m, Assert.Single(result).Close);
    }

    [Fact]
    public async Task TruncatedCsvCoverage_Refetches()
    {
        using var directory = new TemporaryDirectory();
        var descriptor = Descriptor("alpaca", "sip", "all");
        await ReadAllAsync(new CachedMarketDataProvider(
            new FakeProvider(descriptor, [Bar(Start), Bar(Start.AddMinutes(5))]),
            directory.Path,
            "reuse",
            descriptor));

        var manifestPath = Path.Combine(directory.Path, "TEST", "bars_5m.csv.manifest.json");
        var csvPath = await ResolveCommittedDataPathAsync(manifestPath);
        var lines = await File.ReadAllLinesAsync(csvPath);
        await File.WriteAllLinesAsync(csvPath, lines.Take(2));

        var replacement = new FakeProvider(descriptor, [Bar(Start, 111m)]);
        var result = await ReadAllAsync(new CachedMarketDataProvider(
            replacement,
            directory.Path,
            "reuse",
            descriptor));

        Assert.Equal(1, replacement.CallCount);
        Assert.Equal(111m, Assert.Single(result).Close);
    }

    [Fact]
    public async Task StaleManifestSchema_Refetches()
    {
        using var directory = new TemporaryDirectory();
        var descriptor = Descriptor("alpaca", "sip", "all");
        await ReadAllAsync(new CachedMarketDataProvider(
            new FakeProvider(descriptor, [Bar(Start)]),
            directory.Path,
            "reuse",
            descriptor));

        var manifestPath = Path.Combine(directory.Path, "TEST", "bars_5m.csv.manifest.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        manifest["manifestSchemaVersion"] = 0;
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());

        var replacement = new FakeProvider(descriptor, [Bar(Start, 113m)]);
        var result = await ReadAllAsync(new CachedMarketDataProvider(
            replacement,
            directory.Path,
            "reuse",
            descriptor));

        Assert.Equal(1, replacement.CallCount);
        Assert.Equal(113m, Assert.Single(result).Close);
    }

    [Theory]
    [InlineData("barCoverage")]
    [InlineData("contentSha256")]
    [InlineData("dataFileName")]
    public async Task NullRequiredManifestMember_FailsClosedAndRefetches(string propertyName)
    {
        using var directory = new TemporaryDirectory();
        var descriptor = Descriptor("alpaca", "sip", "all");
        await ReadAllAsync(new CachedMarketDataProvider(
            new FakeProvider(descriptor, [Bar(Start)]),
            directory.Path,
            "reuse",
            descriptor));

        var manifestPath = Path.Combine(directory.Path, "TEST", "bars_5m.csv.manifest.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        manifest[propertyName] = null;
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());

        var replacement = new FakeProvider(descriptor, [Bar(Start, 117m)]);
        var result = await ReadAllAsync(new CachedMarketDataProvider(
            replacement,
            directory.Path,
            "reuse",
            descriptor));

        Assert.Equal(1, replacement.CallCount);
        Assert.Equal(117m, Assert.Single(result).Close);
    }

    [Fact]
    public async Task CorruptManifestCannotDeleteAnotherTimeframesDataFile()
    {
        using var directory = new TemporaryDirectory();
        var descriptor = Descriptor("alpaca", "sip", "all");
        await ReadAllAsync(new CachedMarketDataProvider(
            new FakeProvider(descriptor, [Bar(Start)]),
            directory.Path,
            "reuse",
            descriptor));

        var manifestPath = Path.Combine(directory.Path, "TEST", "bars_5m.csv.manifest.json");
        var protectedFileName = $"bars_1m-{Guid.NewGuid():N}.csv";
        var protectedPath = Path.Combine(Path.GetDirectoryName(manifestPath)!, protectedFileName);
        await File.WriteAllTextAsync(protectedPath, "other-timeframe-data");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        manifest["dataFileName"] = protectedFileName;
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());

        var replacement = new FakeProvider(descriptor, [Bar(Start, 119m)]);
        var result = await ReadAllAsync(new CachedMarketDataProvider(
            replacement,
            directory.Path,
            "reuse",
            descriptor));

        Assert.Equal(119m, Assert.Single(result).Close);
        Assert.True(File.Exists(protectedPath));
    }

    [Fact]
    public async Task FailedRefresh_PreservesLastCommittedSliceAndLeavesNoTempFiles()
    {
        using var directory = new TemporaryDirectory();
        var descriptor = Descriptor("alpaca", "sip", "all");
        await ReadAllAsync(new CachedMarketDataProvider(
            new FakeProvider(descriptor, [Bar(Start, 101m)]),
            directory.Path,
            "reuse",
            descriptor));

        var failing = new FakeProvider(descriptor, [Bar(Start, 999m)], failAfterFirstBar: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ReadAllAsync(new CachedMarketDataProvider(failing, directory.Path, "refresh", descriptor)));

        var shouldNotRun = new FakeProvider(descriptor, [Bar(Start, 555m)]);
        var recovered = await ReadAllAsync(new CachedMarketDataProvider(
            shouldNotRun,
            directory.Path,
            "reuse",
            descriptor));

        Assert.Equal(101m, Assert.Single(recovered).Close);
        Assert.Equal(0, shouldNotRun.CallCount);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SuccessfulRefresh_AtomicallySwitchesToNewImmutableDataFile()
    {
        using var directory = new TemporaryDirectory();
        var descriptor = Descriptor("alpaca", "sip", "all");
        await ReadAllAsync(new CachedMarketDataProvider(
            new FakeProvider(descriptor, [Bar(Start, 101m)]),
            directory.Path,
            "reuse",
            descriptor));

        var manifestPath = Path.Combine(directory.Path, "TEST", "bars_5m.csv.manifest.json");
        var originalDataPath = await ResolveCommittedDataPathAsync(manifestPath);
        var orphanPath = Path.Combine(
            Path.GetDirectoryName(manifestPath)!,
            $"bars_5m-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(orphanPath, "uncommitted-orphan");

        await ReadAllAsync(new CachedMarketDataProvider(
            new FakeProvider(descriptor, [Bar(Start, 109m)]),
            directory.Path,
            "refresh",
            descriptor));

        var refreshedDataPath = await ResolveCommittedDataPathAsync(manifestPath);
        Assert.NotEqual(originalDataPath, refreshedDataPath);
        Assert.False(File.Exists(originalDataPath));
        Assert.False(File.Exists(orphanPath));
        Assert.True(File.Exists(refreshedDataPath));
        Assert.Single(Directory.EnumerateFiles(
            Path.GetDirectoryName(manifestPath)!,
            "bars_5m-*.csv"));
    }

    [Fact]
    public async Task ManifestCommitFailurePreservesPriorCommittedSlice()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var descriptor = Descriptor("alpaca", "sip", "all");
        await ReadAllAsync(new CachedMarketDataProvider(
            new FakeProvider(descriptor, [Bar(Start, 101m)]),
            directory.Path,
            "reuse",
            descriptor));

        var manifestPath = Path.Combine(directory.Path, "TEST", "bars_5m.csv.manifest.json");
        var originalDataPath = await ResolveCommittedDataPathAsync(manifestPath);
        await using (var manifestLock = File.Open(
            manifestPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => ReadAllAsync(new CachedMarketDataProvider(
                new FakeProvider(descriptor, [Bar(Start, 999m)]),
                directory.Path,
                "refresh",
                descriptor)));
        }

        var shouldNotRun = new FakeProvider(descriptor, [Bar(Start, 555m)]);
        var recovered = await ReadAllAsync(new CachedMarketDataProvider(
            shouldNotRun,
            directory.Path,
            "reuse",
            descriptor));

        Assert.Equal(101m, Assert.Single(recovered).Close);
        Assert.Equal(0, shouldNotRun.CallCount);
        Assert.True(File.Exists(originalDataPath));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ContendedCrossProcessLockHonorsCancellationWithoutChangingCommittedSlice()
    {
        using var directory = new TemporaryDirectory();
        var descriptor = Descriptor("alpaca", "sip", "all");
        await ReadAllAsync(new CachedMarketDataProvider(
            new FakeProvider(descriptor, [Bar(Start, 101m)]),
            directory.Path,
            "reuse",
            descriptor));

        var manifestPath = Path.Combine(directory.Path, "TEST", "bars_5m.csv.manifest.json");
        var originalDataPath = await ResolveCommittedDataPathAsync(manifestPath);
        var lockPath = $"{manifestPath}.lock";
        await using (var externalLock = File.Open(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None))
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadAllAsync(
                new CachedMarketDataProvider(
                    new FakeProvider(descriptor, [Bar(Start, 999m)]),
                    directory.Path,
                    "refresh",
                    descriptor),
                cancellationToken: cancellation.Token));
        }

        var shouldNotRun = new FakeProvider(descriptor, [Bar(Start, 555m)]);
        var recovered = await ReadAllAsync(new CachedMarketDataProvider(
            shouldNotRun,
            directory.Path,
            "reuse",
            descriptor));

        Assert.Equal(101m, Assert.Single(recovered).Close);
        Assert.Equal(0, shouldNotRun.CallCount);
        Assert.True(File.Exists(originalDataPath));
    }

    [Fact]
    public async Task ManifestContainsOnlyBoundedNonSecretProvenance()
    {
        using var directory = new TemporaryDirectory();
        const string secret = "super-secret-api-key";
        var descriptor = Descriptor("alpaca", "sip", "all");
        var provider = new FakeProvider(descriptor, [Bar(Start)], secret: secret);
        await ReadAllAsync(new CachedMarketDataProvider(provider, directory.Path, "reuse", descriptor));

        var manifestPath = Path.Combine(directory.Path, "TEST", "bars_5m.csv.manifest.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);

        Assert.DoesNotContain(secret, manifest, StringComparison.Ordinal);
        Assert.Contains("\"providerIdentity\": \"alpaca\"", manifest, StringComparison.Ordinal);
        Assert.Contains("\"cachedAtUtc\"", manifest, StringComparison.Ordinal);
        Assert.Contains("\"expiresAtUtc\"", manifest, StringComparison.Ordinal);
        Assert.Contains("\"freshnessPolicyVersion\"", manifest, StringComparison.Ordinal);
        Assert.Contains("\"restatementRevision\"", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain(directory.Path, manifest, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UseCache_ReusesCompleteAuthoritativeCalendarOffline()
    {
        using var directory = new TemporaryDirectory();
        var descriptor = Descriptor("alpaca", "sip", "all");
        var calendarStart = new DateOnly(2026, 8, 3);
        var calendarEnd = new DateOnly(2026, 8, 9);
        var online = new CalendarProvider(descriptor, BuildCalendar(calendarStart, calendarEnd));
        var first = new CachedMarketDataProvider(online, directory.Path, "use_cache", descriptor);

        var downloaded = await first.LoadMarketSessionSchedulesAsync(
            calendarStart,
            calendarEnd,
            CancellationToken.None);

        var offline = new CalendarProvider(descriptor, null, failWhenCalled: true);
        var cached = new CachedMarketDataProvider(offline, directory.Path, "use_cache", descriptor);
        var reused = await cached.LoadMarketSessionSchedulesAsync(
            calendarStart.AddDays(1),
            calendarEnd.AddDays(-1),
            CancellationToken.None);

        Assert.Equal(7, downloaded.Count);
        Assert.Equal(5, reused.Count);
        Assert.Equal(1, online.CalendarCallCount);
        Assert.Equal(0, offline.CalendarCallCount);
        Assert.False(reused[new DateOnly(2026, 8, 8)].IsTradingDay);
    }

    [Fact]
    public async Task RefreshCalendarFailure_PreservesPriorCommittedOfflineSnapshot()
    {
        using var directory = new TemporaryDirectory();
        var descriptor = Descriptor("alpaca", "sip", "all");
        var calendarStart = new DateOnly(2026, 8, 3);
        var calendarEnd = new DateOnly(2026, 8, 7);
        var original = new CalendarProvider(descriptor, BuildCalendar(calendarStart, calendarEnd));
        await new CachedMarketDataProvider(original, directory.Path, "use_cache", descriptor)
            .LoadMarketSessionSchedulesAsync(calendarStart, calendarEnd, CancellationToken.None);

        var failingRefresh = new CalendarProvider(descriptor, null, failWhenCalled: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CachedMarketDataProvider(failingRefresh, directory.Path, "refresh", descriptor)
                .LoadMarketSessionSchedulesAsync(calendarStart, calendarEnd, CancellationToken.None));

        var offline = new CalendarProvider(descriptor, null, failWhenCalled: true);
        var recovered = await new CachedMarketDataProvider(offline, directory.Path, "use_cache", descriptor)
            .LoadMarketSessionSchedulesAsync(calendarStart, calendarEnd, CancellationToken.None);

        Assert.Equal(5, recovered.Count);
        Assert.Equal(0, offline.CalendarCallCount);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task IncompleteProviderCalendar_FailsClosedAndIsNotCached()
    {
        using var directory = new TemporaryDirectory();
        var descriptor = Descriptor("alpaca", "sip", "all");
        var calendarStart = new DateOnly(2026, 8, 3);
        var calendarEnd = calendarStart.AddDays(1);
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule> incomplete =
            new Dictionary<DateOnly, MarketSessionSchedule>
            {
                [calendarStart] = MarketSessionSchedule.RegularDay(calendarStart)
            };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new CachedMarketDataProvider(
                    new CalendarProvider(descriptor, incomplete),
                    directory.Path,
                    "use_cache",
                    descriptor)
                .LoadMarketSessionSchedulesAsync(
                    calendarStart,
                    calendarEnd,
                    CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(
            directory.Path,
            "_market_sessions",
            "manifest.json")));
    }

    [Fact]
    public async Task NullCalendarManifestMember_FailsClosedAndRefetches()
    {
        using var directory = new TemporaryDirectory();
        var descriptor = Descriptor("alpaca", "sip", "all");
        var calendarStart = new DateOnly(2026, 8, 3);
        var calendarEnd = new DateOnly(2026, 8, 7);
        await new CachedMarketDataProvider(
                new CalendarProvider(descriptor, BuildCalendar(calendarStart, calendarEnd)),
                directory.Path,
                "use_cache",
                descriptor)
            .LoadMarketSessionSchedulesAsync(calendarStart, calendarEnd, CancellationToken.None);

        var manifestPath = Path.Combine(directory.Path, "_market_sessions", "manifest.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        manifest["contentSha256"] = null;
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());

        var replacement = new CalendarProvider(
            descriptor,
            BuildCalendar(calendarStart, calendarEnd));
        var schedules = await new CachedMarketDataProvider(
                replacement,
                directory.Path,
                "use_cache",
                descriptor)
            .LoadMarketSessionSchedulesAsync(calendarStart, calendarEnd, CancellationToken.None);

        Assert.Equal(1, replacement.CalendarCallCount);
        Assert.Equal(5, schedules.Count);
    }

    [Fact]
    public async Task NullCalendarSessionCollection_FailsClosedAndRefetches()
    {
        using var directory = new TemporaryDirectory();
        var descriptor = Descriptor("alpaca", "sip", "all");
        var calendarStart = new DateOnly(2026, 8, 3);
        var calendarEnd = new DateOnly(2026, 8, 7);
        await new CachedMarketDataProvider(
                new CalendarProvider(descriptor, BuildCalendar(calendarStart, calendarEnd)),
                directory.Path,
                "use_cache",
                descriptor)
            .LoadMarketSessionSchedulesAsync(calendarStart, calendarEnd, CancellationToken.None);

        var manifestPath = Path.Combine(directory.Path, "_market_sessions", "manifest.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        var dataFileName = manifest["dataFileName"]!.GetValue<string>();
        var dataPath = Path.Combine(Path.GetDirectoryName(manifestPath)!, dataFileName);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(dataPath))!.AsObject();
        document["sessions"] = null;
        await File.WriteAllTextAsync(dataPath, document.ToJsonString());
        manifest["contentSha256"] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(dataPath)));
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());

        var replacement = new CalendarProvider(descriptor, BuildCalendar(calendarStart, calendarEnd));
        var schedules = await new CachedMarketDataProvider(
                replacement,
                directory.Path,
                "use_cache",
                descriptor)
            .LoadMarketSessionSchedulesAsync(calendarStart, calendarEnd, CancellationToken.None);

        Assert.Equal(1, replacement.CalendarCallCount);
        Assert.Equal(5, schedules.Count);
    }

    private static MarketDataCacheSourceDescriptor Descriptor(
        string provider,
        string feed,
        string adjustment,
        string restatementRevision = "unversioned") =>
        new(provider, feed, adjustment, restatementRevision);

    private static OhlcvBar Bar(
        DateTimeOffset timestamp,
        decimal close = 100m,
        string feed = "sip",
        string adjustment = "all") =>
        new(
            "TEST",
            timestamp,
            "5m",
            close - 1m,
            close + 1m,
            close - 2m,
            close,
            1_000m,
            feed,
            adjustment,
            CoverageVerifiedThroughUtc: End);

    private static IReadOnlyDictionary<DateOnly, MarketSessionSchedule> BuildCalendar(
        DateOnly start,
        DateOnly end)
    {
        var schedules = new Dictionary<DateOnly, MarketSessionSchedule>();
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            schedules[date] = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
                ? MarketSessionSchedule.Closed(date)
                : MarketSessionSchedule.RegularDay(date);
        }

        return schedules;
    }

    private static async Task<List<OhlcvBar>> ReadAllAsync(
        IMarketDataProvider provider,
        DateTimeOffset? start = null,
        DateTimeOffset? end = null,
        CancellationToken cancellationToken = default)
    {
        var bars = new List<OhlcvBar>();
        await foreach (var bar in provider.GetBarsAsync(
            ["TEST"],
            ["5m"],
            start ?? Start,
            end ?? End,
            cancellationToken))
        {
            bars.Add(bar);
        }

        return bars;
    }

    private static async Task<string> ResolveCommittedDataPathAsync(string manifestPath)
    {
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        var dataFileName = manifest["dataFileName"]?.GetValue<string>();
        Assert.False(String.IsNullOrWhiteSpace(dataFileName));
        return Path.Combine(Path.GetDirectoryName(manifestPath)!, dataFileName!);
    }

    private sealed class FakeProvider(
        MarketDataCacheSourceDescriptor descriptor,
        IReadOnlyList<OhlcvBar> bars,
        bool failAfterFirstBar = false,
        string? secret = null) : IMarketDataProvider, IMarketDataCacheSourceDescriptorProvider
    {
        public MarketDataCacheSourceDescriptor CacheSourceDescriptor { get; } = descriptor;
        public int CallCount { get; private set; }
        private string? Secret { get; } = secret;

        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes,
            DateTimeOffset start,
            DateTimeOffset end,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            CallCount++;
            _ = Secret;
            for (var index = 0; index < bars.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return bars[index];
                if (failAfterFirstBar && index == 0)
                {
                    throw new InvalidOperationException("Simulated provider failure.");
                }
            }
        }
    }

    private sealed class UndescribedProvider(IReadOnlyList<OhlcvBar> bars) : IMarketDataProvider
    {
        public int CallCount { get; private set; }

        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes,
            DateTimeOffset start,
            DateTimeOffset end,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            CallCount++;
            foreach (var bar in bars)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return bar;
            }
        }
    }

    private sealed class CalendarProvider(
        MarketDataCacheSourceDescriptor descriptor,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? schedules,
        bool failWhenCalled = false) :
        IMarketDataProvider,
        IMarketDataCacheSourceDescriptorProvider,
        IMarketSessionScheduleProvider
    {
        public MarketDataCacheSourceDescriptor CacheSourceDescriptor { get; } = descriptor;

        public int CalendarCallCount { get; private set; }

        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes,
            DateTimeOffset start,
            DateTimeOffset end,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>> LoadMarketSessionSchedulesAsync(
            DateOnly startDateInclusive,
            DateOnly endDateInclusive,
            CancellationToken cancellationToken)
        {
            CalendarCallCount++;
            if (failWhenCalled)
            {
                throw new InvalidOperationException("Simulated offline calendar provider.");
            }

            return Task.FromResult(schedules ??
                throw new InvalidOperationException("Calendar test data is unavailable."));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"tradingflow-cache-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset currentUtc = utcNow;

        public override DateTimeOffset GetUtcNow() => currentUtc;

        public void Advance(TimeSpan duration) => currentUtc = currentUtc.Add(duration);
    }
}
