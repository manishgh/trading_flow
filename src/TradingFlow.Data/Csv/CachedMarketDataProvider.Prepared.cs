using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Pipeline;

namespace TradingFlow.Data.Csv;

public sealed partial class CachedMarketDataProvider
{
    private const int PreparedSchemaVersion = 1;
    private const string PreparedParserContract = "prepared_csv_v3";

    private sealed record PreparedVersionManifest(int SchemaVersion, string RequestSha256,
        string EndpointIdentity, string ParserVersion, string CalendarIdentity,
        MarketDataCacheSourceDescriptor Source, MarketDataCacheManifest? Bars,
        MarketSessionCalendarCacheManifest? Calendar, HistoricalQueryEvidence Evidence,
        bool OmittedSubDailyIntervalsMeanNoQualifyingTrades,
        IReadOnlyList<PreparedMarketDataEvidenceFile> Files);

    /// <summary>Computes the exact source contract expected by recovery without reading pointers or contacting the provider.</summary>
    public string GetPreparedBarsRequestSha256(string ticker, string timeframe, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        if (endUtc < startUtc) throw new ArgumentOutOfRangeException(nameof(endUtc));
        return PreparedRequestHash(sourceDescriptor ?? throw new InvalidOperationException("Prepared history requires source provenance."),
            RequireEvidenceProvider(), NormalizePathSegment(ticker, nameof(ticker), upperCase: true),
            NormalizePathSegment(timeframe, nameof(timeframe), upperCase: false), startUtc, endUtc, isCalendar: false);
    }

    /// <summary>Computes the exact profile and date contract expected by calendar recovery without provider collection.</summary>
    public string GetPreparedCalendarRequestSha256(DateOnly startDate, DateOnly endDate)
    {
        if (endDate < startDate) throw new ArgumentOutOfRangeException(nameof(endDate));
        return PreparedRequestHash(sourceDescriptor ?? throw new InvalidOperationException("Prepared calendars require source provenance."),
            RequireEvidenceProvider(), String.Empty, String.Empty, CalendarUtc(startDate), CalendarUtc(endDate), isCalendar: true);
    }

    /// <summary>Selects verified containing history before collection; refresh appends an immutable exact provider request.</summary>
    public async Task<PreparedMarketDataVersion> SelectPreparedBarsAsync(string ticker, string timeframe,
        DateTimeOffset startUtc, DateTimeOffset endUtc, long maximumBars, CancellationToken cancellationToken)
    {
        var symbol = NormalizePathSegment(ticker, nameof(ticker), upperCase: true);
        var frame = NormalizePathSegment(timeframe, nameof(timeframe), upperCase: false);
        startUtc = startUtc.ToUniversalTime();
        endUtc = endUtc.ToUniversalTime();
        if (endUtc < startUtc || maximumBars <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBars), "Exact bounds and a positive retained-bar budget are required.");
        var evidenceProvider = RequireEvidenceProvider();
        var source = sourceDescriptor ?? throw new InvalidOperationException("Prepared history requires explicit provider/feed/price-basis provenance.");
        var hash = PreparedRequestHash(source, evidenceProvider, symbol, frame, startUtc, endUtc, isCalendar: false);
        var request = new PreparedSelectionRequest(hash, symbol, frame, startUtc, endUtc, false);
        return await SelectPreparedAsync(request, async directory => {
            var captured = await evidenceProvider.CaptureHistoricalBarsAsync(symbol, frame, startUtc, endUtc,
                maximumBars, cancellationToken);
            RequireCollectionEvidence(captured.Evidence);
            EnforcePreparedBudget(captured.Bars.Count, maximumBars);
            var bars = ValidateDownloadedBars(captured.Bars, symbol, frame, startUtc, endUtc, source);
            ValidatePreparedBarStructures(bars);
            var id = Guid.NewGuid().ToString("N");
            var dataPath = Path.Combine(directory, $"bars_{frame}-{id}.csv");
            var temporaryPath = dataPath + ".tmp";
            try
            {
                await WriteCsvAsync(temporaryPath, bars, cancellationToken);
                var file = await DescribePreparedFileAsync(temporaryPath, cancellationToken);
                File.Move(temporaryPath, dataPath);
                file = file with { Path = dataPath };
                var manifest = MarketDataCacheManifest.Create(source, symbol, frame, startUtc, endUtc, bars,
                    Path.GetFileName(dataPath), file.Sha256, freshnessPolicy, captured.Evidence.CollectionCompletedAtUtc);
                var files = await SourceEvidenceFilesAsync(captured.Evidence, cancellationToken);
                files.Insert(0, file);
                return new PreparedVersionManifest(PreparedSchemaVersion, hash, evidenceProvider.HistoricalEndpointIdentity,
                    PreparedParserContract + "+" + evidenceProvider.HistoricalParserVersion,
                    evidenceProvider.HistoricalCalendarIdentity, source, manifest, null, captured.Evidence,
                    OmittedSubDailyIntervalsMeanNoQualifyingTrades, files);
            }
            finally { TryDelete(temporaryPath); }
        }, maximumBars, cancellationToken);
    }

    /// <summary>Reuses a verified containing calendar or captures the exact requested dates, including explicit closed dates.</summary>
    public async Task<PreparedMarketDataVersion> SelectPreparedCalendarAsync(DateOnly startDate, DateOnly endDate,
        CancellationToken cancellationToken)
    {
        if (endDate < startDate) throw new ArgumentOutOfRangeException(nameof(endDate));
        var evidenceProvider = RequireEvidenceProvider();
        var source = sourceDescriptor ?? throw new InvalidOperationException("Prepared calendars require explicit source provenance.");
        var start = new DateTimeOffset(startDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = new DateTimeOffset(endDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var hash = PreparedRequestHash(source, evidenceProvider, String.Empty, String.Empty, start, end, isCalendar: true);
        var request = new PreparedSelectionRequest(hash, String.Empty, String.Empty, start, end, true);
        return await SelectPreparedAsync(request, async directory => {
            var captured = await evidenceProvider.CaptureHistoricalCalendarAsync(startDate, endDate, cancellationToken);
            RequireCollectionEvidence(captured.Evidence);
            var sessions = ValidateCalendar(captured.Sessions, startDate, endDate);
            var dataPath = Path.Combine(directory, $"sessions-{Guid.NewGuid():N}.json");
            var temporaryPath = dataPath + ".tmp";
            try
            {
                await MarketSessionCalendarCacheStore.WriteAsync(temporaryPath,
                    new MarketSessionCalendarCacheDocument(MarketSessionCalendarCacheManifest.CurrentSchemaVersion,
                        startDate, endDate, sessions.Values.OrderBy(x => x.TradeDate).ToArray()), cancellationToken);
                var file = await DescribePreparedFileAsync(temporaryPath, cancellationToken);
                File.Move(temporaryPath, dataPath);
                file = file with { Path = dataPath };
                var calendar = new MarketSessionCalendarCacheManifest(MarketSessionCalendarCacheManifest.CurrentSchemaVersion,
                    source.ProviderIdentity, startDate, endDate, captured.Evidence.CollectionCompletedAtUtc, sessions.Count,
                    Path.GetFileName(dataPath), file.Sha256);
                var files = await SourceEvidenceFilesAsync(captured.Evidence, cancellationToken);
                files.Insert(0, file);
                return new PreparedVersionManifest(PreparedSchemaVersion, hash, evidenceProvider.HistoricalEndpointIdentity,
                    PreparedParserContract + "+" + evidenceProvider.HistoricalParserVersion,
                    evidenceProvider.HistoricalCalendarIdentity, source, null, calendar, captured.Evidence, false, files);
            }
            finally { TryDelete(temporaryPath); }
        }, Int64.MaxValue, cancellationToken);
    }

    /// <summary>Verifies the full pinned parent and returns only admitted rows; budgets count the admitted slice.</summary>
    public static async Task<IReadOnlyList<OhlcvBar>> ReadPreparedBarsAsync(PreparedMarketDataVersion version,
        long maximumBars, CancellationToken cancellationToken)
    {
        var manifest = await VerifyPreparedVersionAsync(version, cancellationToken);
        var admitted = ReadPreparedBarSlice(version, manifest, version.StartUtc, version.EndUtc, maximumBars, true, cancellationToken);
        if (admitted.RowCount != version.RowCount)
            throw new InvalidDataException("Pinned candle slice row count differs from its admitted receipt.");
        EnforcePreparedBudget(admitted.RowCount, maximumBars);
        return admitted.Bars;
    }

    /// <summary>Checks every original calendar date and returns only admitted dates without replacement collection.</summary>
    public static async Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>> ReadPreparedCalendarAsync(
        PreparedMarketDataVersion version, CancellationToken cancellationToken)
    {
        var manifest = await VerifyPreparedVersionAsync(version, cancellationToken);
        var calendar = manifest.Calendar ?? throw new InvalidDataException("Selected version is not a calendar query.");
        var document = await MarketSessionCalendarCacheStore.ReadAsync<MarketSessionCalendarCacheDocument>(version.DataPath, cancellationToken);
        if (document?.Sessions is null || document.SchemaVersion != MarketSessionCalendarCacheManifest.CurrentSchemaVersion ||
            document.CoverageStart != calendar.CoverageStart || document.CoverageEnd != calendar.CoverageEnd)
            throw new InvalidDataException("Pinned calendar document does not match its selected request.");
        var sessions = ValidateCalendar(document.Sessions.ToDictionary(x => x.TradeDate), calendar.CoverageStart, calendar.CoverageEnd);
        var start = DateOnly.FromDateTime(version.StartUtc.UtcDateTime);
        var end = DateOnly.FromDateTime(version.EndUtc.UtcDateTime);
        return ValidateCalendar(sessions.Where(pair => pair.Key >= start && pair.Key <= end).ToDictionary(), start, end);
    }

    /// <summary>Returns the complete already verified file set so the runner can retain read leases through execution.</summary>
    public static async Task<IReadOnlyList<PreparedMarketDataEvidenceFile>> GetPreparedEvidenceAsync(
        PreparedMarketDataVersion version, CancellationToken cancellationToken)
    {
        var manifest = await VerifyPreparedVersionAsync(version, cancellationToken);
        return new[] { await DescribePreparedFileAsync(version.ManifestPath, cancellationToken) }.Concat(manifest.Files).ToArray();
    }

    /// <summary>Serializes selection only; immutable versions outlive selection pointers and never expire by deletion.</summary>
    private async Task<PreparedMarketDataVersion> SelectPreparedAsync(PreparedSelectionRequest request,
        Func<string, Task<PreparedVersionManifest>> collect, long maximumBars, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(normalizedRoot, "_historical", request.Hash);
        var pointer = Path.Combine(directory, "latest.json");
        var gate = PathGates.GetOrAdd(pointer, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(directory);
            await using var fileLock = await AcquireExclusiveFileLockAsync(pointer + ".lock", cancellationToken);
            if (!cachePolicy.Equals("refresh", StringComparison.OrdinalIgnoreCase))
            {
                var selected = await FindExistingPreparedAsync(request, maximumBars, cancellationToken);
                if (selected is not null) return selected;
            }
            var manifest = await collect(directory);
            var path = Path.Combine(directory, $"version-{Guid.NewGuid():N}.json");
            var temporaryPath = path + ".tmp";
            try
            {
                await MarketSessionCalendarCacheStore.WriteAsync(temporaryPath, manifest, cancellationToken);
                var stagedVersion = await DescribePreparedVersionAsync(temporaryPath, manifest, reused: false, cancellationToken);
                await VerifyPreparedVersionAsync(stagedVersion, cancellationToken);
                File.Move(temporaryPath, path);
                var version = await DescribePreparedVersionAsync(path, manifest, reused: false, cancellationToken);
                var pointerTemporaryPath = pointer + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await MarketSessionCalendarCacheStore.WriteAsync(pointerTemporaryPath, version, cancellationToken);
                    File.Move(pointerTemporaryPath, pointer, overwrite: true);
                }
                finally { TryDelete(pointerTemporaryPath); }
                return version;
            }
            finally { TryDelete(temporaryPath); }
        }
        finally { gate.Release(); }
    }

    /// <summary>Checks every original source hash and reconstructs admitted bounds against its immutable parent before any payload read.</summary>
    private static async Task<PreparedVersionManifest> VerifyPreparedVersionAsync(PreparedMarketDataVersion version,
        CancellationToken cancellationToken)
    {
        var described = await DescribePreparedFileAsync(version.ManifestPath, cancellationToken);
        if (!String.Equals(described.Sha256, version.ManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Pinned prepared manifest changed; replacement collection is forbidden.");
        var manifest = await MarketSessionCalendarCacheStore.ReadAsync<PreparedVersionManifest>(version.ManifestPath, cancellationToken)
            ?? throw new InvalidDataException("Pinned prepared manifest is empty.");
        if (!HasPreparedCandidateStructure(manifest))
            throw new InvalidDataException("Pinned prepared manifest has an incomplete source or evidence structure.");
        RequireCollectionEvidence(manifest.Evidence);
        if (manifest.SchemaVersion != PreparedSchemaVersion || manifest.RequestSha256 != (version.SourceRequestSha256 ?? version.RequestSha256) ||
            manifest.Source is null || String.IsNullOrWhiteSpace(manifest.EndpointIdentity) ||
            String.IsNullOrWhiteSpace(manifest.CalendarIdentity) ||
            manifest.ParserVersion is null || !manifest.ParserVersion.StartsWith(PreparedParserContract + "+", StringComparison.Ordinal) ||
            (manifest.Bars is null) == (manifest.Calendar is null) || manifest.Files is not { Count: > 0 })
            throw new InvalidDataException("Pinned prepared manifest has an unsupported or inconsistent contract.");
        if (manifest.Bars is { } bars &&
            (bars.ManifestSchemaVersion != MarketDataCacheManifest.CurrentManifestSchemaVersion ||
             bars.CsvSchemaVersion != MarketDataCacheManifest.CurrentCsvSchemaVersion ||
             bars.RequestedEndUtc < bars.RequestedStartUtc || bars.BarCoverage is null || bars.BarCoverage.BarCount < 0 ||
             bars.ProviderIdentity != manifest.Source.ProviderIdentity || bars.DataFeed != manifest.Source.DataFeed ||
             bars.AdjustmentPolicy != manifest.Source.AdjustmentPolicy || bars.RestatementRevision != manifest.Source.RestatementRevision ||
             !MarketDataCacheManifest.IsSafeDataFileName(bars.DataFileName, bars.Timeframe)))
            throw new InvalidDataException("Pinned candle manifest has an inconsistent source, schema or row count.");
        if (manifest.Calendar is { } calendar &&
            (calendar.SchemaVersion != MarketSessionCalendarCacheManifest.CurrentSchemaVersion ||
             calendar.ProviderIdentity != manifest.Source.ProviderIdentity || calendar.CoverageEnd < calendar.CoverageStart ||
             calendar.DateCount != calendar.CoverageEnd.DayNumber - calendar.CoverageStart.DayNumber + 1 ||
             !MarketSessionCalendarCacheManifest.IsSafeDataFileName(calendar.DataFileName)))
            throw new InvalidDataException("Pinned calendar manifest has an inconsistent source, schema or date count.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(version.ManifestPath))!;
        var dataFileName = manifest.Bars?.DataFileName ?? manifest.Calendar!.DataFileName;
        var dataHash = manifest.Bars?.ContentSha256 ?? manifest.Calendar!.ContentSha256;
        if (Path.GetFileName(dataFileName) != dataFileName ||
            !String.Equals(manifest.Files[0].Path, Path.Combine(directory, dataFileName), StringComparison.OrdinalIgnoreCase) ||
            !String.Equals(manifest.Files[0].Sha256, dataHash, StringComparison.OrdinalIgnoreCase) ||
            manifest.Files.Count != manifest.Files.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count())
            throw new InvalidDataException("Pinned prepared payload does not belong to its immutable version directory.");
        foreach (var response in manifest.Evidence.Responses)
        {
            if (!manifest.Files.Any(x => String.Equals(x.Path, Path.GetFullPath(response.PayloadPath), StringComparison.OrdinalIgnoreCase) &&
                    x.Length == response.Manifest.ByteLength && String.Equals(x.Sha256, response.Manifest.Sha256, StringComparison.OrdinalIgnoreCase)) ||
                !manifest.Files.Any(x => String.Equals(x.Path, Path.GetFullPath(response.ManifestPath), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Pinned prepared version omits an original source payload or receipt.");
        }
        foreach (var expected in manifest.Files)
        {
            var actual = await DescribePreparedFileAsync(expected.Path, cancellationToken);
            if (actual.Length != expected.Length || !String.Equals(actual.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Pinned source evidence changed: {expected.Path}");
        }
        var expectedVersion = await DescribePreparedVersionAsync(version.ManifestPath, manifest, version.Reused, cancellationToken);
        if (version.SourceRequestSha256 is not null)
            expectedVersion = AdmitPreparedSlice(expectedVersion, version.RequestSha256, version.StartUtc, version.EndUtc, version.RowCount);
        if (expectedVersion != version) throw new InvalidDataException("Pinned prepared reference does not match its immutable manifest.");
        return manifest;
    }

    /// <summary>Builds the transport reference exclusively from committed manifest fields and its actual bytes.</summary>
    private static async Task<PreparedMarketDataVersion> DescribePreparedVersionAsync(string path,
        PreparedVersionManifest manifest, bool reused, CancellationToken cancellationToken)
    {
        var file = await DescribePreparedFileAsync(path, cancellationToken);
        var data = manifest.Files[0];
        var bars = manifest.Bars;
        var calendar = manifest.Calendar;
        return new(manifest.RequestSha256, path, file.Sha256, data.Path, data.Sha256,
            bars?.BarCoverage.BarCount ?? calendar!.DateCount, manifest.Source.ProviderIdentity,
            manifest.Source.DataFeed, manifest.Source.AdjustmentPolicy, bars?.Ticker ?? String.Empty,
            bars?.Timeframe ?? String.Empty, bars?.RequestedStartUtc ?? CalendarUtc(calendar!.CoverageStart),
            bars?.RequestedEndUtc ?? CalendarUtc(calendar!.CoverageEnd), manifest.Evidence.CollectionCompletedAtUtc,
            reused, calendar is not null, manifest.OmittedSubDailyIntervalsMeanNoQualifyingTrades);
    }

    /// <summary>Hashes a source request including endpoint/profile, parser, basis and full UTC tick precision.</summary>
    private static string PreparedRequestHash(MarketDataCacheSourceDescriptor source,
        IHistoricalMarketDataEvidenceProvider provider, string ticker, string timeframe,
        DateTimeOffset start, DateTimeOffset end, bool isCalendar)
    {
        var components = new[] { PreparedSchemaVersion.ToString(CultureInfo.InvariantCulture), PreparedParserContract,
            MarketDataCacheManifest.CurrentManifestSchemaVersion.ToString(CultureInfo.InvariantCulture),
            MarketDataCacheManifest.CurrentCsvSchemaVersion.ToString(CultureInfo.InvariantCulture),
            source.ProviderIdentity, source.DataFeed, source.AdjustmentPolicy, source.RestatementRevision,
            provider.HistoricalEndpointIdentity, provider.HistoricalParserVersion, provider.HistoricalCalendarIdentity,
            provider is IMarketDataCompletenessProvider completeness && completeness.OmittedSubDailyIntervalsMeanNoQualifyingTrades
                ? "omitted_subdaily_no_qualifying_trades" : "omission_unknown",
            ticker, timeframe, start.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            end.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), isCalendar ? "calendar" : "bars" };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(String.Join('\n', components)))).ToLowerInvariant();
    }

    /// <summary>Requires explicit collection evidence capability; ordinary legacy CSVs cannot silently become verified history.</summary>
    private IHistoricalMarketDataEvidenceProvider RequireEvidenceProvider() =>
        innerProvider as IHistoricalMarketDataEvidenceProvider ?? throw new InvalidOperationException(
            "Prepared reuse requires archived exact-query receipts and completed pagination from the source adapter.");

    /// <summary>Rejects incomplete pagination and missing original receipts without interpreting empty responses as absent trades.</summary>
    private static void RequireCollectionEvidence(HistoricalQueryEvidence evidence)
    {
        if (evidence is null || !evidence.PaginationCompleted || evidence.Responses is not { Count: > 0 } ||
            evidence.CollectionStartedAtUtc == default || evidence.CollectionCompletedAtUtc < evidence.CollectionStartedAtUtc)
            throw new InvalidDataException("Prepared history requires completed pagination, actual collection times and original response receipts.");
    }

    /// <summary>Records byte-exact original payload and receipt hashes and checks payloads against their archive receipt.</summary>
    private static async Task<List<PreparedMarketDataEvidenceFile>> SourceEvidenceFilesAsync(HistoricalQueryEvidence evidence,
        CancellationToken cancellationToken)
    {
        var files = new List<PreparedMarketDataEvidenceFile>();
        foreach (var response in evidence.Responses)
        {
            var storedReceipt = await MarketSessionCalendarCacheStore.ReadAsync<TradingFlow.Engine.Storage.RawArchiveManifest>(
                response.ManifestPath, cancellationToken);
            if (storedReceipt is null || storedReceipt.ArchiveId != response.Manifest.ArchiveId ||
                storedReceipt.ReceivedAtUtc != response.Manifest.ReceivedAtUtc ||
                storedReceipt.ByteLength != response.Manifest.ByteLength || storedReceipt.Sha256 != response.Manifest.Sha256 ||
                storedReceipt.Source != response.Manifest.Source || storedReceipt.PayloadFileName != Path.GetFileName(response.PayloadPath))
                throw new InvalidDataException("Original source receipt does not match its committed archive manifest.");
            var payload = await DescribePreparedFileAsync(response.PayloadPath, cancellationToken);
            if (payload.Length != response.Manifest.ByteLength ||
                !String.Equals(payload.Sha256, response.Manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Original source response does not match its archived receipt.");
            files.Add(payload);
            files.Add(await DescribePreparedFileAsync(response.ManifestPath, cancellationToken));
        }
        return files.DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Hashes files as streams so raw receipts do not require whole-response memory during verification.</summary>
    private static async Task<PreparedMarketDataEvidenceFile> DescribePreparedFileAsync(string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new(Path.GetFullPath(path), stream.Length,
            Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)));
    }

    /// <summary>Applies the referencing book's retained-source allowance before selecting or accepting a version.</summary>
    private static void EnforcePreparedBudget(long rows, long maximumBars)
    {
        if (maximumBars <= 0 || rows > maximumBars)
            throw new InvalidOperationException($"Prepared source query has {rows} rows, exceeding the retained replay-bar allowance {maximumBars}.");
    }

    /// <summary>Uses the pipeline's shared price, volume and timestamp rules before a reusable version can be published or read.</summary>
    private static void ValidatePreparedBarStructures(IReadOnlyList<OhlcvBar> bars)
    {
        foreach (var bar in bars)
            if (!CanonicalMarketBarValidator.IsValid(CanonicalMarketBarValidator.Normalize(bar), requireOneMinuteSource: false, out var failure))
                throw new InvalidDataException($"Prepared candle {bar.Ticker}/{bar.Timeframe} at {bar.Timestamp:O} failed shared ingestion checks: {failure}");
    }

    /// <summary>Represents a calendar's source date at UTC midnight without changing its exchange-session hours.</summary>
    private static DateTimeOffset CalendarUtc(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
}
