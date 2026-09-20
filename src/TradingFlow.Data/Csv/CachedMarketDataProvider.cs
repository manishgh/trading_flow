using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Data.Csv;

/// <summary>
/// Caches one exact ticker/timeframe/range slice and reuses it only when its
/// non-secret source descriptor, manifest, and CSV contents agree.
/// </summary>
public sealed class CachedMarketDataProvider :
    IMarketDataProvider,
    IMarketSessionScheduleProvider,
    IMarketDataCompletenessProvider,
    IMarketDataProvenanceProvider
{
    internal const string CsvHeader =
        "ticker,timestamp,open,high,low,close,volume,data_feed,adjustment_policy,known_at_utc,coverage_verified_through_utc";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathGates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IMarketDataProvider innerProvider;
    private readonly string normalizedRoot;
    private readonly string cachePolicy;
    private readonly MarketDataCacheSourceDescriptor? sourceDescriptor;
    private readonly MarketDataCacheFreshnessPolicy freshnessPolicy;

    public CachedMarketDataProvider(
        IMarketDataProvider innerProvider,
        string normalizedRoot,
        string cachePolicy)
        : this(innerProvider, normalizedRoot, cachePolicy, sourceDescriptor: null)
    {
    }

    public CachedMarketDataProvider(
        IMarketDataProvider innerProvider,
        string normalizedRoot,
        string cachePolicy,
        MarketDataCacheSourceDescriptor? sourceDescriptor,
        MarketDataCacheFreshnessPolicy? freshnessPolicy = null)
    {
        this.innerProvider = innerProvider ?? throw new ArgumentNullException(nameof(innerProvider));
        this.normalizedRoot = String.IsNullOrWhiteSpace(normalizedRoot)
            ? throw new ArgumentException("A cache root is required.", nameof(normalizedRoot))
            : Path.GetFullPath(normalizedRoot);
        this.cachePolicy = cachePolicy ?? throw new ArgumentNullException(nameof(cachePolicy));
        this.freshnessPolicy = freshnessPolicy ?? MarketDataCacheFreshnessPolicy.ProductionDefault;
        this.sourceDescriptor = sourceDescriptor ??
            (innerProvider as IMarketDataCacheSourceDescriptorProvider)?.CacheSourceDescriptor ??
            ResolveSourceDescriptor(innerProvider);
    }

    /// <summary>
    /// Omission semantics are authoritative only when the wrapped provider says so.
    /// An incapable provider remains fail-closed rather than inheriting Alpaca rules.
    /// </summary>
    public bool OmittedSubDailyIntervalsMeanNoQualifyingTrades =>
        innerProvider is IMarketDataCompletenessProvider completenessProvider &&
        completenessProvider.OmittedSubDailyIntervalsMeanNoQualifyingTrades;

    public MarketDataProvenance MarketDataProvenance =>
        innerProvider is IMarketDataProvenanceProvider provenanceProvider
            ? provenanceProvider.MarketDataProvenance
            : throw new InvalidOperationException(
                "The wrapped market-data provider does not expose authoritative provenance.");

    public async Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>> LoadMarketSessionSchedulesAsync(
        DateOnly startDateInclusive,
        DateOnly endDateInclusive,
        CancellationToken cancellationToken)
    {
        if (endDateInclusive < startDateInclusive)
        {
            throw new ArgumentOutOfRangeException(
                nameof(endDateInclusive),
                "Calendar range end must not precede start.");
        }

        var calendarRoot = Path.Combine(normalizedRoot, "_market_sessions");
        var manifestPath = Path.Combine(calendarRoot, "manifest.json");
        var gate = PathGates.GetOrAdd(manifestPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(calendarRoot);
            await using var fileLock = await AcquireExclusiveFileLockAsync(
                $"{manifestPath}.lock",
                cancellationToken);
            if (!cachePolicy.Equals("refresh", StringComparison.OrdinalIgnoreCase) &&
                sourceDescriptor is not null)
            {
                var cached = await TryReadCalendarAsync(
                    calendarRoot,
                    manifestPath,
                    sourceDescriptor.ProviderIdentity,
                    startDateInclusive,
                    endDateInclusive,
                    freshnessPolicy.TimeProvider.GetUtcNow(),
                    cancellationToken);
                if (cached is not null)
                {
                    return cached;
                }
            }

            if (innerProvider is not IMarketSessionScheduleProvider scheduleProvider)
            {
                throw new InvalidOperationException(
                    "No complete authoritative exchange calendar is cached and the wrapped provider cannot load one.");
            }

            var downloaded = await scheduleProvider.LoadMarketSessionSchedulesAsync(
                startDateInclusive,
                endDateInclusive,
                cancellationToken);
            var validated = ValidateCalendar(downloaded, startDateInclusive, endDateInclusive);
            if (sourceDescriptor is not null)
            {
                await WriteCommittedCalendarAsync(
                    calendarRoot,
                    manifestPath,
                    sourceDescriptor.ProviderIdentity,
                    startDateInclusive,
                    endDateInclusive,
                    validated,
                    cancellationToken);
            }

            return validated;
        }
        finally
        {
            gate.Release();
        }
    }

    private static MarketDataCacheSourceDescriptor? ResolveSourceDescriptor(IMarketDataProvider provider)
    {
        if (provider is not IMarketDataProvenanceProvider provenanceProvider)
        {
            return null;
        }

        var provenance = provenanceProvider.MarketDataProvenance;
        return new MarketDataCacheSourceDescriptor(
            provenance.ProviderIdentity,
            provenance.DataFeed,
            provenance.AdjustmentPolicy);
    }

    private static async Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>?> TryReadCalendarAsync(
        string calendarRoot,
        string manifestPath,
        string providerIdentity,
        DateOnly requestedStart,
        DateOnly requestedEnd,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            var manifest = await MarketSessionCalendarCacheStore.ReadAsync<MarketSessionCalendarCacheManifest>(
                manifestPath,
                cancellationToken);
            if (manifest is null || !manifest.FullyCovers(
                providerIdentity,
                requestedStart,
                requestedEnd,
                nowUtc))
            {
                return null;
            }

            var dataPath = ResolveContainedPath(calendarRoot, manifest.DataFileName);
            if (dataPath is null)
            {
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(dataPath, cancellationToken);
            if (!String.Equals(
                manifest.ContentSha256,
                Convert.ToHexString(SHA256.HashData(bytes)),
                StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var document = await MarketSessionCalendarCacheStore.ReadAsync<MarketSessionCalendarCacheDocument>(
                dataPath,
                cancellationToken);
            if (document is null ||
                document.Sessions is null ||
                document.SchemaVersion != MarketSessionCalendarCacheManifest.CurrentSchemaVersion ||
                document.CoverageStart != manifest.CoverageStart ||
                document.CoverageEnd != manifest.CoverageEnd)
            {
                return null;
            }

            var complete = ValidateCalendar(
                document.Sessions.ToDictionary(x => x.TradeDate),
                document.CoverageStart,
                document.CoverageEnd);
            return complete
                .Where(x => x.Key >= requestedStart && x.Key <= requestedEnd)
                .ToDictionary(x => x.Key, x => x.Value);
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            ArgumentException or
            System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<DateOnly, MarketSessionSchedule> ValidateCalendar(
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule> schedules,
        DateOnly startDateInclusive,
        DateOnly endDateInclusive)
    {
        var expectedCount = endDateInclusive.DayNumber - startDateInclusive.DayNumber + 1;
        if (schedules.Count != expectedCount)
        {
            throw new InvalidDataException(
                "Authoritative exchange calendar did not return every requested date, including closed dates.");
        }

        var validated = new SortedDictionary<DateOnly, MarketSessionSchedule>();
        for (var date = startDateInclusive; date <= endDateInclusive; date = date.AddDays(1))
        {
            if (!schedules.TryGetValue(date, out var schedule) || schedule.TradeDate != date)
            {
                throw new InvalidDataException(
                    $"Authoritative exchange calendar is incomplete for {date:yyyy-MM-dd}.");
            }

            if (schedule.IsTradingDay && schedule.RegularOpen >= schedule.RegularClose)
            {
                throw new InvalidDataException(
                    $"Authoritative exchange calendar has invalid session hours for {date:yyyy-MM-dd}.");
            }

            if (!schedule.IsTradingDay &&
                (schedule.RegularOpen != default || schedule.RegularClose != default))
            {
                throw new InvalidDataException(
                    $"Closed exchange date {date:yyyy-MM-dd} unexpectedly contains session hours.");
            }

            validated.Add(date, schedule);
        }

        return validated;
    }

    private async Task WriteCommittedCalendarAsync(
        string calendarRoot,
        string manifestPath,
        string providerIdentity,
        DateOnly coverageStart,
        DateOnly coverageEnd,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule> schedules,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(calendarRoot);
        var transactionId = Guid.NewGuid().ToString("N");
        var dataFileName = $"sessions-{transactionId}.json";
        var dataPath = Path.Combine(calendarRoot, dataFileName);
        var dataTempPath = $"{dataPath}.tmp";
        var manifestTempPath = $"{manifestPath}.{transactionId}.tmp";
        string? priorDataPath = null;
        var committed = false;

        try
        {
            priorDataPath = await TryResolvePriorCalendarDataPathAsync(
                calendarRoot,
                manifestPath,
                providerIdentity,
                cancellationToken);

            var document = new MarketSessionCalendarCacheDocument(
                MarketSessionCalendarCacheManifest.CurrentSchemaVersion,
                coverageStart,
                coverageEnd,
                schedules.OrderBy(x => x.Key).Select(x => x.Value).ToArray());
            await MarketSessionCalendarCacheStore.WriteAsync(dataTempPath, document, cancellationToken);
            var bytes = await File.ReadAllBytesAsync(dataTempPath, cancellationToken);
            var manifest = new MarketSessionCalendarCacheManifest(
                MarketSessionCalendarCacheManifest.CurrentSchemaVersion,
                providerIdentity,
                coverageStart,
                coverageEnd,
                freshnessPolicy.TimeProvider.GetUtcNow().ToUniversalTime(),
                schedules.Count,
                dataFileName,
                Convert.ToHexString(SHA256.HashData(bytes)));
            await MarketSessionCalendarCacheStore.WriteAsync(manifestTempPath, manifest, cancellationToken);

            File.Move(dataTempPath, dataPath);
            if (File.Exists(manifestPath))
            {
                File.Replace(manifestTempPath, manifestPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(manifestTempPath, manifestPath);
            }
            committed = true;

            if (priorDataPath is not null &&
                !String.Equals(priorDataPath, dataPath, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(priorDataPath);
            }
        }
        finally
        {
            TryDelete(dataTempPath);
            TryDelete(manifestTempPath);
            if (!committed)
            {
                TryDelete(dataPath);
            }
        }
    }

    private static async Task<string?> TryResolvePriorCalendarDataPathAsync(
        string calendarRoot,
        string manifestPath,
        string providerIdentity,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            var prior = await MarketSessionCalendarCacheStore.ReadAsync<MarketSessionCalendarCacheManifest>(
                manifestPath,
                cancellationToken);
            return prior is not null &&
                   prior.SchemaVersion == MarketSessionCalendarCacheManifest.CurrentSchemaVersion &&
                   String.Equals(prior.ProviderIdentity, providerIdentity, StringComparison.Ordinal) &&
                   MarketSessionCalendarCacheManifest.IsSafeDataFileName(prior.DataFileName)
                ? ResolveContainedPath(calendarRoot, prior.DataFileName)
                : null;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
        IReadOnlyCollection<string> tickers,
        IReadOnlyCollection<string> timeframes,
        DateTimeOffset start,
        DateTimeOffset end,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var requestStartUtc = start.ToUniversalTime();
        var requestEndUtc = end.ToUniversalTime();
        if (requestEndUtc < requestStartUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(end), "Cache range end must not precede start.");
        }

        foreach (var ticker in tickers)
        {
            var normalizedTicker = NormalizePathSegment(ticker, nameof(tickers), upperCase: true);
            foreach (var timeframe in timeframes)
            {
                var normalizedTimeframe = NormalizePathSegment(timeframe, nameof(timeframes), upperCase: false);
                var dataPath = GetPath(normalizedTicker, normalizedTimeframe);
                var manifestPath = MarketDataCacheManifestStore.GetManifestPath(dataPath);
                var gate = PathGates.GetOrAdd(dataPath, static _ => new SemaphoreSlim(1, 1));
                IReadOnlyList<OhlcvBar> result;

                await gate.WaitAsync(cancellationToken);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dataPath)!);
                    await using var fileLock = await AcquireExclusiveFileLockAsync(
                        $"{manifestPath}.lock",
                        cancellationToken);
                    result = await ResolveSliceAsync(
                        normalizedTicker,
                        normalizedTimeframe,
                        requestStartUtc,
                        requestEndUtc,
                        dataPath,
                        manifestPath,
                        cancellationToken);
                }
                finally
                {
                    gate.Release();
                }

                foreach (var bar in result)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return bar;
                }
            }
        }
    }

    private async Task<IReadOnlyList<OhlcvBar>> ResolveSliceAsync(
        string ticker,
        string timeframe,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        string dataPath,
        string manifestPath,
        CancellationToken cancellationToken)
    {
        var forceRefresh = cachePolicy.Equals("refresh", StringComparison.OrdinalIgnoreCase);
        if (!forceRefresh && sourceDescriptor is not null)
        {
            var cached = await TryReadValidatedSliceAsync(
                dataPath,
                manifestPath,
                ticker,
                timeframe,
                startUtc,
                endUtc,
                sourceDescriptor,
                freshnessPolicy,
                cancellationToken);
            if (cached is not null)
            {
                return cached;
            }
        }

        var downloaded = new List<OhlcvBar>();
        await foreach (var bar in innerProvider.GetBarsAsync(
            [ticker],
            [timeframe],
            startUtc,
            endUtc,
            cancellationToken))
        {
            downloaded.Add(bar);
        }

        var effectiveDescriptor = sourceDescriptor ??
            MarketDataCacheSourceDescriptor.FromDownloadedBars(innerProvider, downloaded);
        var validated = ValidateDownloadedBars(
            downloaded,
            ticker,
            timeframe,
            startUtc,
            endUtc,
            effectiveDescriptor);

        await WriteCommittedSliceAsync(
            dataPath,
            manifestPath,
            ticker,
            timeframe,
            startUtc,
            endUtc,
            effectiveDescriptor,
            validated,
            freshnessPolicy,
            cancellationToken);

        return validated;
    }

    private static async Task<IReadOnlyList<OhlcvBar>?> TryReadValidatedSliceAsync(
        string dataPath,
        string manifestPath,
        string ticker,
        string timeframe,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        MarketDataCacheSourceDescriptor descriptor,
        MarketDataCacheFreshnessPolicy freshnessPolicy,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            var manifest = await MarketDataCacheManifestStore.ReadAsync(manifestPath, cancellationToken);
            if (manifest is null || !manifest.MatchesRequest(
                descriptor,
                ticker,
                timeframe,
                startUtc,
                endUtc,
                freshnessPolicy,
                freshnessPolicy.TimeProvider.GetUtcNow()))
            {
                return null;
            }

            var committedDataPath = Path.Combine(
                Path.GetDirectoryName(dataPath)!,
                manifest.DataFileName);
            if (!File.Exists(committedDataPath))
            {
                return null;
            }

            var csvBytes = await File.ReadAllBytesAsync(committedDataPath, cancellationToken);
            if (!String.Equals(
                manifest.ContentSha256,
                Convert.ToHexString(SHA256.HashData(csvBytes)),
                StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var bars = ParseCsv(csvBytes, timeframe);
            return ValidateCachedBars(bars, manifest, ticker, timeframe, startUtc, endUtc, descriptor)
                ? bars
                : null;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            FormatException or
            OverflowException or
            KeyNotFoundException or
            System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<OhlcvBar> ValidateDownloadedBars(
        IReadOnlyCollection<OhlcvBar> bars,
        string ticker,
        string timeframe,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        MarketDataCacheSourceDescriptor descriptor)
    {
        var ordered = bars.OrderBy(x => x.Timestamp).ToArray();
        var previousTimestamp = DateTimeOffset.MinValue;
        foreach (var bar in ordered)
        {
            var timestampUtc = bar.Timestamp.ToUniversalTime();
            if (!String.Equals(bar.Ticker, ticker, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(bar.Timeframe, timeframe, StringComparison.OrdinalIgnoreCase) ||
                timestampUtc < startUtc || timestampUtc > endUtc ||
                timestampUtc <= previousTimestamp ||
                !descriptor.MatchesBar(bar))
            {
                throw new InvalidDataException(
                    $"Provider returned a bar outside the declared cache slice for {ticker}/{timeframe}.");
            }

            previousTimestamp = timestampUtc;
        }

        return ordered;
    }

    private static bool ValidateCachedBars(
        IReadOnlyList<OhlcvBar> bars,
        MarketDataCacheManifest manifest,
        string ticker,
        string timeframe,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        MarketDataCacheSourceDescriptor descriptor)
    {
        if (bars.Count != manifest.BarCoverage.BarCount)
        {
            return false;
        }

        DateTimeOffset? first = null;
        DateTimeOffset? last = null;
        var previousTimestamp = DateTimeOffset.MinValue;
        foreach (var bar in bars)
        {
            var timestampUtc = bar.Timestamp.ToUniversalTime();
            if (!String.Equals(bar.Ticker, ticker, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(bar.Timeframe, timeframe, StringComparison.OrdinalIgnoreCase) ||
                timestampUtc < startUtc || timestampUtc > endUtc ||
                timestampUtc <= previousTimestamp ||
                !descriptor.MatchesBar(bar))
            {
                return false;
            }

            first ??= timestampUtc;
            last = timestampUtc;
            previousTimestamp = timestampUtc;
        }

        return first == manifest.BarCoverage.FirstBarUtc &&
            last == manifest.BarCoverage.LastBarUtc;
    }

    private static async Task WriteCommittedSliceAsync(
        string dataPath,
        string manifestPath,
        string ticker,
        string timeframe,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        MarketDataCacheSourceDescriptor descriptor,
        IReadOnlyList<OhlcvBar> bars,
        MarketDataCacheFreshnessPolicy freshnessPolicy,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(dataPath)!;
        Directory.CreateDirectory(directory);
        var transactionId = Guid.NewGuid().ToString("N");
        var dataFileName = $"{Path.GetFileNameWithoutExtension(dataPath)}-{transactionId}.csv";
        var committedDataPath = Path.Combine(directory, dataFileName);
        var dataTempPath = $"{committedDataPath}.tmp";
        var manifestTempPath = $"{manifestPath}.{transactionId}.tmp";
        var priorDataPath = await TryResolveCommittedDataPathAsync(
            dataPath,
            manifestPath,
            ticker,
            timeframe,
            descriptor,
            cancellationToken);
        var manifestCommitted = false;

        try
        {
            await WriteCsvAsync(dataTempPath, bars, cancellationToken);
            var csvBytes = await File.ReadAllBytesAsync(dataTempPath, cancellationToken);
            var manifest = MarketDataCacheManifest.Create(
                descriptor,
                ticker,
                timeframe,
                startUtc,
                endUtc,
                bars,
                dataFileName,
                Convert.ToHexString(SHA256.HashData(csvBytes)),
                freshnessPolicy,
                freshnessPolicy.TimeProvider.GetUtcNow());
            await MarketDataCacheManifestStore.WriteTempAsync(
                manifestTempPath,
                manifest,
                cancellationToken);

            // Data files are immutable. Publishing the small manifest is the only commit
            // point, so interruption before that switch leaves the prior slice readable.
            File.Move(dataTempPath, committedDataPath);
            if (File.Exists(manifestPath))
            {
                File.Replace(manifestTempPath, manifestPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(manifestTempPath, manifestPath);
            }

            manifestCommitted = true;
            CleanupUnreferencedDataFiles(dataPath, committedDataPath, priorDataPath);
        }
        finally
        {
            TryDelete(dataTempPath);
            TryDelete(manifestTempPath);
            if (!manifestCommitted)
            {
                TryDelete(committedDataPath);
            }
        }
    }

    private static async Task<string?> TryResolveCommittedDataPathAsync(
        string logicalDataPath,
        string manifestPath,
        string ticker,
        string timeframe,
        MarketDataCacheSourceDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            var manifest = await MarketDataCacheManifestStore.ReadAsync(
                manifestPath,
                cancellationToken);
            if (manifest is null ||
                manifest.ManifestSchemaVersion != MarketDataCacheManifest.CurrentManifestSchemaVersion ||
                manifest.CsvSchemaVersion != MarketDataCacheManifest.CurrentCsvSchemaVersion ||
                !String.Equals(manifest.ProviderIdentity, descriptor.ProviderIdentity, StringComparison.Ordinal) ||
                !String.Equals(manifest.DataFeed, descriptor.DataFeed, StringComparison.Ordinal) ||
                !String.Equals(manifest.AdjustmentPolicy, descriptor.AdjustmentPolicy, StringComparison.Ordinal) ||
                !String.Equals(manifest.RestatementRevision, descriptor.RestatementRevision, StringComparison.Ordinal) ||
                !String.Equals(manifest.Ticker, ticker, StringComparison.Ordinal) ||
                !String.Equals(manifest.Timeframe, timeframe, StringComparison.Ordinal) ||
                !MarketDataCacheManifest.IsSafeDataFileName(manifest.DataFileName, timeframe))
            {
                return null;
            }

            var directory = Path.GetFullPath(Path.GetDirectoryName(logicalDataPath)!);
            var candidate = Path.GetFullPath(Path.Combine(directory, manifest.DataFileName));
            return String.Equals(Path.GetDirectoryName(candidate), directory, StringComparison.OrdinalIgnoreCase)
                ? candidate
                : null;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static void CleanupUnreferencedDataFiles(
        string logicalDataPath,
        string committedDataPath,
        string? priorDataPath)
    {
        try
        {
            if (priorDataPath is not null &&
                !String.Equals(priorDataPath, committedDataPath, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(priorDataPath);
            }

            var directory = Path.GetDirectoryName(logicalDataPath)!;
            var pattern = $"{Path.GetFileNameWithoutExtension(logicalDataPath)}-*.csv";
            foreach (var candidate in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
            {
                if (!String.Equals(candidate, committedDataPath, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(candidate);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cache publication already committed. Cleanup is bounded best effort and
            // must never turn a valid committed slice into a failed provider read.
        }
    }

    private string GetPath(string ticker, string timeframe) =>
        Path.Combine(normalizedRoot, ticker, $"bars_{timeframe}.csv");

    private static async Task<FileStream> AcquireExclusiveFileLockAsync(
        string lockPath,
        CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        IOException? lastContention = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
            }
            catch (IOException exception)
            {
                lastContention = exception;
                if (elapsed.Elapsed >= TimeSpan.FromSeconds(30))
                {
                    throw new TimeoutException(
                        $"Timed out waiting for exclusive market-data cache lock '{lockPath}'.",
                        lastContention);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }
        }
    }

    private static string? ResolveContainedPath(string directory, string fileName)
    {
        var normalizedDirectory = Path.GetFullPath(directory);
        var candidate = Path.GetFullPath(Path.Combine(normalizedDirectory, fileName));
        return String.Equals(
            Path.GetDirectoryName(candidate),
            normalizedDirectory,
            StringComparison.OrdinalIgnoreCase)
                ? candidate
                : null;
    }

    private static async Task WriteCsvAsync(
        string path,
        IReadOnlyCollection<OhlcvBar> bars,
        CancellationToken cancellationToken)
    {
        await using var stream = File.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        await writer.WriteLineAsync(CsvHeader.AsMemory(), cancellationToken);
        foreach (var bar in bars)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(
                FormattableString.Invariant(
                    $"{bar.Ticker},{bar.Timestamp.ToUniversalTime():O},{bar.Open},{bar.High},{bar.Low},{bar.Close},{bar.Volume},{bar.DataFeed},{bar.AdjustmentPolicy},{bar.KnownAtUtc?.ToUniversalTime():O},{bar.CoverageVerifiedThroughUtc?.ToUniversalTime():O}")
                    .AsMemory(),
                cancellationToken);
        }

        await writer.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private static IReadOnlyList<OhlcvBar> ParseCsv(byte[] bytes, string timeframe)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var header = reader.ReadLine() ??
            throw new InvalidDataException("Cached market-data CSV has no schema header.");
        if (!String.Equals(header, CsvHeader, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Cached market-data CSV schema does not match the manifest schema.");
        }

        var columns = header.Split(',');
        var index = columns
            .Select((name, i) => (Name: name.Trim(), Index: i))
            .ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);
        var bars = new List<OhlcvBar>();

        while (reader.ReadLine() is { } line)
        {
            if (String.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var fields = line.Split(',');
            if (fields.Length != columns.Length)
            {
                throw new InvalidDataException("Cached market-data row does not match the CSV schema.");
            }

            bars.Add(new OhlcvBar(
                GetString(fields, index, "ticker"),
                DateTimeOffset.Parse(
                    GetString(fields, index, "timestamp"),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                timeframe,
                GetDecimal(fields, index, "open"),
                GetDecimal(fields, index, "high"),
                GetDecimal(fields, index, "low"),
                GetDecimal(fields, index, "close"),
                GetDecimal(fields, index, "volume"),
                GetString(fields, index, "data_feed"),
                GetString(fields, index, "adjustment_policy"),
                ParseOptionalTimestamp(GetString(fields, index, "known_at_utc")),
                ParseOptionalTimestamp(GetString(fields, index, "coverage_verified_through_utc"))));
        }

        return bars;
    }

    private static string NormalizePathSegment(string value, string parameterName, bool upperCase)
    {
        var normalized = (upperCase ? value?.Trim().ToUpperInvariant() : value?.Trim().ToLowerInvariant()) ?? String.Empty;
        if (normalized.Length == 0 || normalized.Length > 32 ||
            normalized.Any(character => !Char.IsLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
        {
            throw new ArgumentException("Cache ticker and timeframe must be simple market identifiers.", parameterName);
        }

        return normalized;
    }

    private static string GetString(string[] fields, IReadOnlyDictionary<string, int> index, string name) =>
        fields[index[name]].Trim();

    private static decimal GetDecimal(string[] fields, IReadOnlyDictionary<string, int> index, string name) =>
        Decimal.Parse(GetString(fields, index, name), CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseOptionalTimestamp(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp)
            ? timestamp.ToUniversalTime()
            : null;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A stale temp file is never a committed cache and is ignored by readers.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup failure does not make an uncommitted temp file authoritative.
        }
    }
}
