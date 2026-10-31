using System.Net;
using TradingFlow.Alpaca;
using TradingFlow.Data.Csv;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Storage;
using TradingFlow.Engine.Abstractions;
using System.Text.Json.Nodes;

namespace TradingFlow.Tests;

public sealed class PreparedMarketDataVersionTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-06-08T14:00:00Z");
    private static readonly DateTimeOffset End = Start.AddHours(1);

    /// <summary>New comparisons reuse eligible versions without HTTP; refresh preserves earlier pinned versions byte-for-byte.</summary>
    [Fact]
    public async Task ReuseAndRefresh_PreservePinnedVersionAndOriginalReceipts()
    {
        using var owner = new OwnedAlpacaTestArchive();
        using var handler = new FixtureHandler();
        using var http = new HttpClient(handler);
        var source = CreateSource(http, owner);
        var root = Path.Combine(owner.Root, "normalized");
        var cache = new CachedMarketDataProvider(source, root, "use_cache");
        var first = await cache.SelectPreparedBarsAsync("AMD", "5m", Start, End, 10, default);
        var second = await cache.SelectPreparedBarsAsync("AMD", "5m", Start, End, 10, default);
        Assert.Equal(1, handler.Calls);
        Assert.True(second.Reused);
        Assert.Equal(first.ManifestSha256, second.ManifestSha256);
        var refreshed = await new CachedMarketDataProvider(source, root, "refresh")
            .SelectPreparedBarsAsync("AMD", "5m", Start, End, 10, default);
        Assert.Equal(2, handler.Calls);
        Assert.NotEqual(first.ManifestPath, refreshed.ManifestPath);
        Assert.Single(await CachedMarketDataProvider.ReadPreparedBarsAsync(first, 10, default));
        Assert.Single(await CachedMarketDataProvider.ReadPreparedBarsAsync(refreshed, 10, default));
        var files = await CachedMarketDataProvider.GetPreparedEvidenceAsync(first, default);
        Assert.Equal(4, files.Count);
        Assert.All(files, file => Assert.True(File.Exists(file.Path)));
    }

    /// <summary>Changing a pinned raw response fails locally and cannot silently select or download replacement history.</summary>
    [Fact]
    public async Task PinnedRawCorruption_FailsWithoutNetwork()
    {
        using var owner = new OwnedAlpacaTestArchive();
        using var handler = new FixtureHandler();
        using var http = new HttpClient(handler);
        var selected = await new CachedMarketDataProvider(CreateSource(http, owner), Path.Combine(owner.Root, "normalized"), "use_cache")
            .SelectPreparedBarsAsync("AMD", "5m", Start, End, 10, default);
        var files = await CachedMarketDataProvider.GetPreparedEvidenceAsync(selected, default);
        var rawPayload = files.Single(x => x.Path != selected.ManifestPath && x.Path != selected.DataPath &&
            !x.Path.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase));
        await File.AppendAllTextAsync(rawPayload.Path, "synthetic corruption");
        await Assert.ThrowsAsync<InvalidDataException>(() => CachedMarketDataProvider.ReadPreparedBarsAsync(selected, 10, default));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>Price bases, exact ranges and parser source identity cannot share the same prepared request directory.</summary>
    [Fact]
    public async Task ExactRangeAndPriceBasis_HaveDistinctRequestIdentities()
    {
        using var owner = new OwnedAlpacaTestArchive();
        using var handler = new FixtureHandler();
        using var allHttp = new HttpClient(handler);
        using var rawHttp = new HttpClient(handler);
        var root = Path.Combine(owner.Root, "normalized");
        var all = new CachedMarketDataProvider(CreateSource(allHttp, owner), root, "use_cache");
        var raw = new CachedMarketDataProvider(CreateSource(rawHttp, owner, "raw"), root, "use_cache");
        var first = await all.SelectPreparedBarsAsync("AMD", "5m", Start, End, 10, default);
        var changedBasis = await raw.SelectPreparedBarsAsync("AMD", "5m", Start, End, 10, default);
        var changedRange = await all.SelectPreparedBarsAsync("AMD", "5m", Start, End.AddTicks(1), 10, default);
        Assert.NotEqual(first.RequestSha256, changedBasis.RequestSha256);
        Assert.NotEqual(first.RequestSha256, changedRange.RequestSha256);
        Assert.Equal("raw", Assert.Single(await CachedMarketDataProvider.ReadPreparedBarsAsync(changedBasis, 10, default)).AdjustmentPolicy);
        Assert.Equal(3, handler.Calls);
    }

    /// <summary>The provider row allowance aborts before further pagination and does not publish an oversized prepared version.</summary>
    [Fact]
    public async Task RowBudget_StopsWhileParsingBeforePublishing()
    {
        using var owner = new OwnedAlpacaTestArchive();
        using var handler = new FixtureHandler(twoRows: true);
        using var http = new HttpClient(handler);
        var root = Path.Combine(owner.Root, "normalized");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CachedMarketDataProvider(CreateSource(http, owner), root, "use_cache")
                .SelectPreparedBarsAsync("AMD", "5m", Start, End, 1, default));
        Assert.Equal(1, handler.Calls);
        Assert.Empty(Directory.EnumerateFiles(root, "version-*.json", SearchOption.AllDirectories));
        Assert.NotEmpty(Directory.EnumerateFiles(owner.Root, "*.manifest.json", SearchOption.AllDirectories));
    }

    /// <summary>TTL does not force a replacement capture for a closed historical request; its original revision remains pinned.</summary>
    [Fact]
    public async Task PinnedVersion_IgnoresSelectionExpiry()
    {
        using var owner = new OwnedAlpacaTestArchive();
        using var handler = new FixtureHandler();
        using var http = new HttpClient(handler);
        var clock = new AdjustableClock();
        var cache = new CachedMarketDataProvider(CreateSource(http, owner), Path.Combine(owner.Root, "normalized"),
            "use_cache", null, new MarketDataCacheFreshnessPolicy("fixture", TimeSpan.FromHours(1), clock));
        var first = await cache.SelectPreparedBarsAsync("AMD", "5m", Start, End, 10, default);
        clock.Offset = TimeSpan.FromHours(2);
        Assert.Single(await CachedMarketDataProvider.ReadPreparedBarsAsync(first, 10, default));
        var next = await cache.SelectPreparedBarsAsync("AMD", "5m", Start, End, 10, default);
        Assert.Equal(first.ManifestPath, next.ManifestPath);
        Assert.True(next.Reused);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>An expired parent with future context still supplies a historical calendar slice without HTTP; its future request retains expiry.</summary>
    [Fact]
    public async Task ExpiredFutureCalendar_HistoricalSubsetReusesOriginalSourceWithoutHttp()
    {
        using var owner = new OwnedAlpacaTestArchive();
        using var handler = new FixtureHandler();
        using var http = new HttpClient(handler);
        var clock = new AdjustableClock();
        var source = CreateSource(http, owner);
        var cache = new CachedMarketDataProvider(source, Path.Combine(owner.Root, "normalized"),
            "use_cache", null, new MarketDataCacheFreshnessPolicy("fixture", TimeSpan.FromHours(24), clock));
        var first = new DateOnly(2026, 6, 8);
        var future = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime).AddDays(10);
        var parent = await cache.SelectPreparedCalendarAsync(first, future, default);
        var original = await File.ReadAllBytesAsync(parent.ManifestPath);
        clock.Offset = TimeSpan.FromHours(27);
        var historical = await cache.SelectPreparedCalendarAsync(first, first.AddDays(1), default);
        Assert.True(historical.Reused);
        Assert.Equal(parent.ManifestPath, historical.ManifestPath);
        Assert.Equal(parent.DataPath, historical.DataPath);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(2, (await CachedMarketDataProvider.ReadPreparedCalendarAsync(historical, default)).Count);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var current = await cache.SelectPreparedCalendarAsync(first, today, default);
        Assert.NotEqual(parent.ManifestPath, current.ManifestPath);
        Assert.Equal(2, handler.Calls);
        var refreshedFuture = await cache.SelectPreparedCalendarAsync(first, future, default);
        Assert.NotEqual(parent.ManifestPath, refreshedFuture.ManifestPath);
        Assert.Equal(3, handler.Calls);
        var explicitRefresh = await new CachedMarketDataProvider(source, Path.Combine(owner.Root, "normalized"),
            "refresh", null, new MarketDataCacheFreshnessPolicy("fixture", TimeSpan.FromHours(24), clock))
            .SelectPreparedCalendarAsync(first, first.AddDays(1), default);
        Assert.NotEqual(parent.ManifestPath, explicitRefresh.ManifestPath);
        Assert.Equal(4, handler.Calls);
        Assert.Equal(original, await File.ReadAllBytesAsync(parent.ManifestPath));
    }

    /// <summary>A request whose end is still open retains finite freshness; expiry creates a new immutable capture.</summary>
    [Fact]
    public async Task StillOpenRequest_ExpiresWhileEarlierPinnedVersionRemainsReadable()
    {
        using var owner = new OwnedAlpacaTestArchive();
        using var handler = new FixtureHandler();
        using var http = new HttpClient(handler);
        var clock = new AdjustableClock();
        var cache = new CachedMarketDataProvider(CreateSource(http, owner), Path.Combine(owner.Root, "normalized"),
            "use_cache", null, new MarketDataCacheFreshnessPolicy("fixture", TimeSpan.FromHours(1), clock));
        var openEnd = DateTimeOffset.UtcNow.AddDays(1);
        var first = await cache.SelectPreparedBarsAsync("AMD", "5m", Start, openEnd, 10, default);
        clock.Offset = TimeSpan.FromHours(2);
        var next = await cache.SelectPreparedBarsAsync("AMD", "5m", Start, openEnd, 10, default);
        Assert.NotEqual(first.ManifestPath, next.ManifestPath);
        Assert.Equal(2, handler.Calls);
        Assert.Single(await CachedMarketDataProvider.ReadPreparedBarsAsync(first, 10, default));
    }

    /// <summary>Containing ranges cannot bridge different feeds, revisions, parsers, endpoints, calendars or omission contracts.</summary>
    [Theory]
    [InlineData("feed")]
    [InlineData("revision")]
    [InlineData("parser")]
    [InlineData("endpoint")]
    [InlineData("calendar")]
    [InlineData("omission")]
    public async Task ContainingHistory_RequiresEverySourceContractField(string changedField)
    {
        using var owner = new OwnedAlpacaTestArchive();
        using var handler = new FixtureHandler(twoRows: true);
        using var http = new HttpClient(handler);
        var source = CreateSource(http, owner);
        var root = Path.Combine(owner.Root, "normalized");
        await new CachedMarketDataProvider(source, root, "use_cache").SelectPreparedBarsAsync("AMD", "5m", Start, End, 10, default);
        var changed = new ContractSource(source, changedField);
        Assert.Null(await new CachedMarketDataProvider(changed, root, "use_cache")
            .TryReadExistingPreparedBarsAsync("AMD", "5m", Start.AddMinutes(5), End, default));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>Both five-minute and daily books retain their exact admitted bounds while sharing larger immutable source files.</summary>
    [Theory]
    [InlineData("5m")]
    [InlineData("1d")]
    public async Task ContainingHistory_AdmitsOnlyRequestedRowsAndChargesSubsetBudget(string timeframe)
    {
        using var owner = new OwnedAlpacaTestArchive();
        using var handler = new FixtureHandler(twoRows: true);
        using var http = new HttpClient(handler);
        var cache = new CachedMarketDataProvider(CreateSource(http, owner), Path.Combine(owner.Root, "normalized"), "use_cache");
        var parent = await cache.SelectPreparedBarsAsync("AMD", timeframe, Start, End, 10, default);
        var manifest = await File.ReadAllBytesAsync(parent.ManifestPath);
        var csv = await File.ReadAllBytesAsync(parent.DataPath);
        var slice = await cache.SelectPreparedBarsAsync("AMD", timeframe, Start.AddMinutes(5), End, 1, default);
        Assert.Equal(1, handler.Calls);
        Assert.True(slice.Reused);
        Assert.Equal(parent.RequestSha256, slice.SourceRequestSha256);
        Assert.NotEqual(parent.RequestSha256, slice.RequestSha256);
        Assert.Equal(parent.ManifestPath, slice.ManifestPath);
        Assert.Equal(parent.ManifestSha256, slice.ManifestSha256);
        Assert.Equal(parent.DataPath, slice.DataPath);
        Assert.Equal(parent.CollectedAtUtc, slice.CollectedAtUtc);
        Assert.Equal(Start.AddMinutes(5), slice.StartUtc);
        Assert.Equal(1, slice.RowCount);
        Assert.Equal(Start.AddMinutes(5), Assert.Single(await CachedMarketDataProvider.ReadPreparedBarsAsync(slice, 1, default)).Timestamp);
        Assert.Equal(2, (await CachedMarketDataProvider.ReadPreparedBarsAsync(parent, 10, default)).Count);
        Assert.Equal(manifest, await File.ReadAllBytesAsync(parent.ManifestPath));
        Assert.Equal(csv, await File.ReadAllBytesAsync(parent.DataPath));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(owner.Root, "normalized"), "*.csv", SearchOption.AllDirectories));
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.SelectPreparedBarsAsync("AMD", timeframe, Start, End, 1, default));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>Containing calendars retain original receipts, explicitly closed dates and exact admitted date counts without HTTP.</summary>
    [Fact]
    public async Task ContainingCalendar_AdmitsOnlyRequestedDatesAndProvidesPriorSessionContext()
    {
        using var owner = new OwnedAlpacaTestArchive();
        using var handler = new FixtureHandler();
        using var http = new HttpClient(handler);
        var cache = new CachedMarketDataProvider(CreateSource(http, owner), Path.Combine(owner.Root, "normalized"), "use_cache");
        var day = new DateOnly(2026, 6, 8);
        var parent = await cache.SelectPreparedCalendarAsync(day, day.AddDays(2), default);
        var slice = await cache.SelectPreparedCalendarAsync(day.AddDays(1), day.AddDays(2), default);
        Assert.Equal(parent.RequestSha256, slice.SourceRequestSha256);
        Assert.Equal(parent.ManifestPath, slice.ManifestPath);
        Assert.Equal(2, slice.RowCount);
        var dates = await CachedMarketDataProvider.ReadPreparedCalendarAsync(slice, default);
        Assert.Equal(2, dates.Count);
        Assert.DoesNotContain(day, dates.Keys);
        Assert.All(dates.Values, schedule => Assert.False(schedule.IsTradingDay));
        var context = await cache.TryReadExistingPreparedSessionContextAsync(day.AddDays(1), default);
        Assert.NotNull(context);
        Assert.Equal(2, context.Count);
        Assert.True(context[day].IsTradingDay);
        Assert.Null(await cache.TryReadExistingPreparedSessionContextAsync(day, default));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>Selection recovers an older verified containing parent when the latest parent is damaged, without changing pinned failures.</summary>
    [Fact]
    public async Task ContainingHistory_SkipsCorruptLatestParentButPinnedReadStillFails()
    {
        using var owner = new OwnedAlpacaTestArchive();
        using var handler = new FixtureHandler(twoRows: true);
        using var http = new HttpClient(handler);
        var source = CreateSource(http, owner);
        var root = Path.Combine(owner.Root, "normalized");
        var first = await new CachedMarketDataProvider(source, root, "use_cache")
            .SelectPreparedBarsAsync("AMD", "5m", Start, End, 10, default);
        var newer = await new CachedMarketDataProvider(source, root, "refresh")
            .SelectPreparedBarsAsync("AMD", "5m", Start, End, 10, default);
        await File.AppendAllTextAsync(newer.DataPath, "synthetic corruption");
        var slice = await new CachedMarketDataProvider(source, root, "use_cache")
            .SelectPreparedBarsAsync("AMD", "5m", Start.AddMinutes(5), End, 1, default);
        Assert.Equal(first.ManifestPath, slice.ManifestPath);
        Assert.Equal(2, handler.Calls);
        await Assert.ThrowsAsync<InvalidDataException>(() => CachedMarketDataProvider.ReadPreparedBarsAsync(newer, 10, default));
    }

    /// <summary>Malformed compatible discovery metadata cannot hide an older valid parent, and its previously pinned reference still fails locally.</summary>
    [Theory]
    [InlineData("files_null")]
    [InlineData("files_empty")]
    [InlineData("file_null")]
    [InlineData("coverage_null")]
    [InlineData("response_null")]
    [InlineData("receipt_manifest_null")]
    public async Task ContainingHistory_SkipsMalformedCandidateMetadata(string corruption)
    {
        using var owner = new OwnedAlpacaTestArchive();
        using var handler = new FixtureHandler(twoRows: true);
        using var http = new HttpClient(handler);
        var source = CreateSource(http, owner);
        var root = Path.Combine(owner.Root, "normalized");
        var first = await new CachedMarketDataProvider(source, root, "use_cache")
            .SelectPreparedBarsAsync("AMD", "5m", Start, End, 10, default);
        var newer = await new CachedMarketDataProvider(source, root, "refresh")
            .SelectPreparedBarsAsync("AMD", "5m", Start, End, 10, default);
        var cache = new CachedMarketDataProvider(source, root, "use_cache");
        Assert.Single((await cache.TryReadExistingPreparedBarsAsync("AMD", "5m", Start.AddMinutes(5), End, default))!);
        var node = JsonNode.Parse(await File.ReadAllTextAsync(newer.ManifestPath))!;
        if (corruption == "coverage_null") node["bars"]!["barCoverage"] = null;
        else if (corruption == "file_null") node["files"]![0] = null;
        else if (corruption == "response_null") node["evidence"]!["responses"]![0] = null;
        else if (corruption == "receipt_manifest_null") node["evidence"]!["responses"]![0]!["manifest"] = null;
        else node["files"] = corruption == "files_empty" ? new JsonArray() : null;
        await File.WriteAllTextAsync(newer.ManifestPath, node.ToJsonString());
        var selected = await cache.SelectPreparedBarsAsync("AMD", "5m", Start.AddMinutes(5), End, 1, default);
        Assert.Equal(first.ManifestPath, selected.ManifestPath);
        Assert.Equal(2, handler.Calls);
        await Assert.ThrowsAsync<InvalidDataException>(() => CachedMarketDataProvider.ReadPreparedBarsAsync(newer, 10, default));
    }

    /// <summary>Parent choice is stable across repeated reads and prefers the smallest containing request; empty slices remain empty.</summary>
    [Fact]
    public async Task ContainingHistory_SelectsSmallestParentAndDoesNotFabricateRows()
    {
        using var owner = new OwnedAlpacaTestArchive();
        using var handler = new FixtureHandler(twoRows: true);
        using var http = new HttpClient(handler);
        var source = CreateSource(http, owner);
        var root = Path.Combine(owner.Root, "normalized");
        var narrow = await new CachedMarketDataProvider(source, root, "refresh")
            .SelectPreparedBarsAsync("AMD", "5m", Start, End, 10, default);
        _ = await new CachedMarketDataProvider(source, root, "refresh")
            .SelectPreparedBarsAsync("AMD", "5m", Start, End.AddHours(1), 10, default);
        var cache = new CachedMarketDataProvider(source, root, "use_cache");
        var first = await cache.SelectPreparedBarsAsync("AMD", "5m", Start.AddMinutes(10), End.AddTicks(-1), 1, default);
        var second = await cache.SelectPreparedBarsAsync("AMD", "5m", Start.AddMinutes(10), End.AddTicks(-1), 1, default);
        Assert.Equal(narrow.ManifestPath, first.ManifestPath);
        Assert.Equal(first, second);
        Assert.Equal(0, first.RowCount);
        Assert.Empty(await CachedMarketDataProvider.ReadPreparedBarsAsync(first, 1, default));
        Assert.Equal(2, handler.Calls);
    }

    /// <summary>Concurrent new comparisons selecting the same reusable query publish only one collected source version.</summary>
    [Fact]
    public async Task ConcurrentUseCache_CollectsExactQueryOnce()
    {
        using var owner = new OwnedAlpacaTestArchive();
        using var handler = new FixtureHandler();
        using var http = new HttpClient(handler);
        var source = CreateSource(http, owner);
        var root = Path.Combine(owner.Root, "normalized");
        var selections = await Task.WhenAll(
            new CachedMarketDataProvider(source, root, "use_cache").SelectPreparedBarsAsync("AMD", "5m", Start, End, 10, default),
            new CachedMarketDataProvider(source, root, "use_cache").SelectPreparedBarsAsync("AMD", "5m", Start, End, 10, default));
        Assert.Equal(1, handler.Calls);
        Assert.Equal(selections[0].ManifestSha256, selections[1].ManifestSha256);
        Assert.Single(selections, x => x.Reused);
    }

    /// <summary>The selected calendar stores explicit closed dates and retains its archived endpoint response for offline reopening.</summary>
    [Fact]
    public async Task Calendar_ExactVersionReusesWithoutHttpAndPreservesClosedDates()
    {
        using var owner = new OwnedAlpacaTestArchive();
        using var handler = new FixtureHandler();
        using var http = new HttpClient(handler);
        var cache = new CachedMarketDataProvider(CreateSource(http, owner), Path.Combine(owner.Root, "normalized"), "use_cache");
        var day = new DateOnly(2026, 6, 8);
        var first = await cache.SelectPreparedCalendarAsync(day, day.AddDays(1), default);
        var reused = await cache.SelectPreparedCalendarAsync(day, day.AddDays(1), default);
        Assert.True(reused.Reused);
        Assert.Equal(first.ManifestSha256, reused.ManifestSha256);
        Assert.Equal(1, handler.Calls);
        var calendar = await CachedMarketDataProvider.ReadPreparedCalendarAsync(first, default);
        Assert.Equal(2, calendar.Count);
        Assert.True(calendar[day].IsTradingDay);
        Assert.False(calendar[day.AddDays(1)].IsTradingDay);
    }

    /// <summary>Malformed pagination is retained as raw evidence but cannot be labeled a completed reusable query.</summary>
    [Fact]
    public async Task MissingPaginationCompletion_DoesNotPublishVersion()
    {
        using var owner = new OwnedAlpacaTestArchive();
        using var handler = new FixtureHandler(missingPagination: true);
        using var http = new HttpClient(handler);
        var root = Path.Combine(owner.Root, "normalized");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new CachedMarketDataProvider(CreateSource(http, owner), root, "use_cache")
                .SelectPreparedBarsAsync("AMD", "5m", Start, End, 10, default));
        Assert.Empty(Directory.EnumerateFiles(root, "version-*.json", SearchOption.AllDirectories));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>Both response pages remain part of the immutable version rather than only the final successful page.</summary>
    [Fact]
    public async Task TwoPageQuery_RetainsEveryResponseAndRow()
    {
        using var owner = new OwnedAlpacaTestArchive();
        using var handler = new FixtureHandler(twoPages: true);
        using var http = new HttpClient(handler);
        var version = await new CachedMarketDataProvider(CreateSource(http, owner), Path.Combine(owner.Root, "normalized"), "use_cache")
            .SelectPreparedBarsAsync("AMD", "5m", Start, End, 10, default);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(2, (await CachedMarketDataProvider.ReadPreparedBarsAsync(version, 10, default)).Count);
        Assert.Equal(6, (await CachedMarketDataProvider.GetPreparedEvidenceAsync(version, default)).Count);
    }

    /// <summary>A provider repeating a continuation token cannot make an incomplete query appear finished.</summary>
    [Fact]
    public async Task RepeatedPageToken_DoesNotPublishVersion()
    {
        using var owner = new OwnedAlpacaTestArchive();
        using var handler = new FixtureHandler(twoPages: true, repeatedToken: true);
        using var http = new HttpClient(handler);
        var root = Path.Combine(owner.Root, "normalized");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new CachedMarketDataProvider(CreateSource(http, owner), root, "use_cache")
                .SelectPreparedBarsAsync("AMD", "5m", Start, End, 10, default));
        Assert.Equal(2, handler.Calls);
        Assert.Empty(Directory.EnumerateFiles(root, "version-*.json", SearchOption.AllDirectories));
        Assert.Equal(2, Directory.EnumerateFiles(owner.Root, "*.manifest.json", SearchOption.AllDirectories).Count());
    }

    /// <summary>The shared pipeline's candle geometry checks run before immutable preparation publication.</summary>
    [Fact]
    public async Task MalformedOhlc_DoesNotPublishVersion()
    {
        using var owner = new OwnedAlpacaTestArchive();
        using var handler = new FixtureHandler(malformed: true);
        using var http = new HttpClient(handler);
        var root = Path.Combine(owner.Root, "normalized");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new CachedMarketDataProvider(CreateSource(http, owner), root, "use_cache")
                .SelectPreparedBarsAsync("AMD", "5m", Start, End, 10, default));
        Assert.Empty(Directory.EnumerateFiles(root, "version-*.json", SearchOption.AllDirectories));
        Assert.Single(Directory.EnumerateFiles(owner.Root, "*.manifest.json", SearchOption.AllDirectories));
    }

    /// <summary>Run cancellation cannot discard a buffered provider response while its original archive is committing.</summary>
    [Fact]
    public async Task CancellationDuringArchival_PreservesOriginalAndPublishesNoVersion()
    {
        using var owner = new OwnedAlpacaTestArchive();
        using var handler = new FixtureHandler();
        using var http = new HttpClient(handler);
        var writer = new PausingArchiveWriter(owner.Writer);
        var source = new AlpacaMarketDataProvider(http, AlpacaOptions.Create(ProductionProfile.Paper) with {
            KeyId = "fixture", SecretKey = "fixture" }, writer);
        var root = Path.Combine(owner.Root, "normalized");
        using var cancellation = new CancellationTokenSource();
        var selection = new CachedMarketDataProvider(source, root, "use_cache")
            .SelectPreparedBarsAsync("AMD", "5m", Start, End, 10, cancellation.Token);
        await writer.Entered.Task;
        cancellation.Cancel();
        writer.Continue.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selection);
        Assert.False(writer.ArchiveToken.CanBeCanceled);
        Assert.Single(Directory.EnumerateFiles(owner.Root, "*.manifest.json", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(root, "version-*.json", SearchOption.AllDirectories));
    }

    /// <summary>Builds an actual archive-backed provider using an in-process HTTP fixture with no reachable network.</summary>
    private static AlpacaMarketDataProvider CreateSource(HttpClient http, OwnedAlpacaTestArchive owner, string basis = "all") =>
        new(http, AlpacaOptions.Create(ProductionProfile.Paper) with { KeyId = "fixture", SecretKey = "fixture" },
            owner.Writer, adjustmentPolicy: basis);

    private sealed class AdjustableClock : TimeProvider
    {
        public TimeSpan Offset { get; set; }
        /// <summary>Advances only cache selection time; source capture keeps its actual receipt timestamps.</summary>
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + Offset;
    }

    private sealed class ContractSource(AlpacaMarketDataProvider source, string changedField) :
        IMarketDataProvider, IHistoricalMarketDataEvidenceProvider, IMarketDataCacheSourceDescriptorProvider,
        IMarketDataCompletenessProvider
    {
        private IHistoricalMarketDataEvidenceProvider Evidence => source;
        public MarketDataCacheSourceDescriptor CacheSourceDescriptor => new(
            source.MarketDataProvenance.ProviderIdentity,
            changedField == "feed" ? "iex" : source.MarketDataProvenance.DataFeed,
            source.MarketDataProvenance.AdjustmentPolicy,
            changedField == "revision" ? "changed" : "unversioned");
        public string HistoricalEndpointIdentity => changedField == "endpoint" ? "changed" : Evidence.HistoricalEndpointIdentity;
        public string HistoricalParserVersion => changedField == "parser" ? "changed" : Evidence.HistoricalParserVersion;
        public string HistoricalCalendarIdentity => changedField == "calendar" ? "changed" : Evidence.HistoricalCalendarIdentity;
        public bool OmittedSubDailyIntervalsMeanNoQualifyingTrades => changedField == "omission"
            ? !source.OmittedSubDailyIntervalsMeanNoQualifyingTrades : source.OmittedSubDailyIntervalsMeanNoQualifyingTrades;
        /// <summary>Forwards only the fixture's captured bars; changed contracts are tested through a read-only miss.</summary>
        public Task<HistoricalBarsCapture> CaptureHistoricalBarsAsync(string ticker, string timeframe, DateTimeOffset startUtc,
            DateTimeOffset endUtc, long maximumBars, CancellationToken cancellationToken) =>
            Evidence.CaptureHistoricalBarsAsync(ticker, timeframe, startUtc, endUtc, maximumBars, cancellationToken);
        /// <summary>Preserves fixture calendar capture capability without adding another source.</summary>
        public Task<HistoricalCalendarCapture> CaptureHistoricalCalendarAsync(DateOnly startDate, DateOnly endDate,
            CancellationToken cancellationToken) => Evidence.CaptureHistoricalCalendarAsync(startDate, endDate, cancellationToken);
        /// <summary>Uses the fixture's ordinary adapter only if explicitly requested by a test.</summary>
        public IAsyncEnumerable<TradingFlow.Domain.Market.OhlcvBar> GetBarsAsync(IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken) =>
            source.GetBarsAsync(tickers, timeframes, start, end, cancellationToken);
    }

    private sealed class PausingArchiveWriter(IRawArchiveWriter inner) : IRawArchiveWriter
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ArchiveToken { get; private set; }
        /// <summary>Pauses only the owned synthetic original archive so cancellation can race with its durable commit.</summary>
        public async Task<RawArchiveReceipt> ArchiveAsync(RawArchiveRequest request, ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default)
        {
            ArchiveToken = cancellationToken;
            Entered.TrySetResult();
            await Continue.Task;
            return await inner.ArchiveAsync(request, payload, cancellationToken);
        }
        /// <summary>Retains the source writer's prohibition on automatic original-data deletion.</summary>
        public Task<RawArchiveRetentionResult> EnforceRetentionAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default) =>
            inner.EnforceRetentionAsync(nowUtc, cancellationToken);
    }

    private sealed class FixtureHandler(bool twoRows = false, bool missingPagination = false,
        bool twoPages = false, bool repeatedToken = false, bool malformed = false) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        /// <summary>Returns deterministic, explicitly synthetic source bytes for one exact AMD query.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (request.RequestUri!.AbsolutePath == "/v2/calendar")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                    RequestMessage = request,
                    Content = new StringContent("[{\"date\":\"2026-06-08\",\"open\":\"09:30\",\"close\":\"16:00\"}]") });
            var bar = twoPages && Calls > 1
                ? "{\"t\":\"2026-06-08T14:05:00Z\",\"o\":100,\"h\":101,\"l\":99,\"c\":100,\"v\":1000}"
                : "{\"t\":\"2026-06-08T14:00:00Z\",\"o\":100,\"h\":101,\"l\":99,\"c\":100,\"v\":1000}";
            if (malformed) bar = bar.Replace("\"h\":101", "\"h\":98", StringComparison.Ordinal);
            var second = twoRows ? ",{\"t\":\"2026-06-08T14:05:00Z\",\"o\":100,\"h\":101,\"l\":99,\"c\":100,\"v\":1000}" : String.Empty;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                RequestMessage = request,
                Content = new StringContent("{\"bars\":{\"AMD\":[" + bar + second + "]}" +
                    (missingPagination ? "}" : twoPages && (Calls == 1 || repeatedToken)
                        ? ",\"next_page_token\":\"page2\"}" : ",\"next_page_token\":null}")) });
        }
    }
}
