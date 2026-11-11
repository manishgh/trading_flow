using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using TradingFlow.Data.Csv;
using TradingFlow.Data.Evidence.Parquet;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Research;
using TradingFlow.Engine.Storage;
using TradingFlow.Engine.Universe;

namespace TradingFlow.Data.Evidence;

/// <summary>
/// Read-only market data from the immutable evidence catalog for research-screen backtests. Serves bars only from the dataset
/// IDs frozen in the run settings (as-traded daily bars, and the five-minute and one-minute datasets collected for the run's
/// ranked list) and exchange sessions only from the frozen XNYS dataset. Every dataset is checked against its frozen ID (the
/// ID is the manifest's content fingerprint) and every partition artifact against its SHA-256 before rows are returned. It has
/// no network client: nothing is ever requested from Alpaca. Each answer is archived as a small receipt naming the exact
/// datasets and partitions read, so prepared data records them and a rerun reads identical inputs.
/// </summary>
public sealed class EvidenceCatalogMarketDataProvider : IMarketDataProvider, IMarketSessionScheduleProvider,
    IMarketDataProvenanceProvider, IMarketDataCacheSourceDescriptorProvider, IHistoricalMarketDataEvidenceProvider, IShareSplitProvider
{
    public const string ProviderName = "evidence_catalog";
    private const string Exchange = "XNYS";
    private const string DataFeed = "sip";
    private const string Adjustment = "raw";
    private const string ProviderIdentity = "tradingflow_evidence_catalog";
    // Decoded partitions kept for reuse; prepared runs ask for one stock at a time, so a partition holding many stocks is read again.
    private const long CachedRowLimit = 2_000_000;
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private readonly EvidenceCatalogProviderConfig config;
    private readonly Func<string, CancellationToken, Task<EvidenceDatasetManifest?>> getDataset;
    private readonly IEvidencePartitionDataReader reader;
    private readonly IRawArchiveWriter archive;
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<(EvidenceDatasetManifest Dataset, EvidenceDatasetPartitionManifest Partition)>>>> partitions = new(StringComparer.Ordinal);
    private readonly Lazy<Task<SessionCalendar>> calendar;
    private readonly PartitionCache cache = new(CachedRowLimit);

    /// <summary>
    /// Creates the adapter over a dataset lookup and partition reader; the lookup must return the committed manifest for an ID.
    /// <paramref name="archive"/> receives one receipt per answered request.
    /// </summary>
    public EvidenceCatalogMarketDataProvider(EvidenceCatalogProviderConfig config,
        Func<string, CancellationToken, Task<EvidenceDatasetManifest?>> getDataset, IEvidencePartitionDataReader reader, IRawArchiveWriter archive)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.getDataset = getDataset ?? throw new ArgumentNullException(nameof(getDataset));
        this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
        this.archive = archive ?? throw new ArgumentNullException(nameof(archive));
        // Daily research screens need no intraday datasets; a five-minute or one-minute request then simply finds no bars.
        if (config.RankedListSha256.Length != 64)
            throw new ArgumentException("The evidence catalog provider needs the SHA-256 of the screen's membership list.", nameof(config));
        calendar = new(() => LoadCalendarAsync(CancellationToken.None));
        CacheSourceDescriptor = new(ProviderIdentity, DataFeed, Adjustment, "datasets-" + ResearchHash(String.Join('\n',
            new[] { config.RankedListSha256, config.DailyDatasetId, config.ExchangeSessionsDatasetId }
                .Concat(config.FiveMinuteDatasetIds.Order(StringComparer.Ordinal)).Concat(config.OneMinuteDatasetIds.Order(StringComparer.Ordinal))))[..40]);
    }

    /// <summary>
    /// Opens the configured SQLite catalog and artifact store on first use only, so validating a plan touches no file. The
    /// catalog is opened read-only: no migration, no journal change and no full integrity scan of the large file.
    /// </summary>
    public static EvidenceCatalogMarketDataProvider Open(EvidenceCatalogProviderConfig config, IRawArchiveWriter archive)
    {
        var store = new Lazy<FileSystemImmutableArtifactStore>(() => new(new ImmutableArtifactStoreOptions(Path.GetFullPath(config.ArtifactRoot))));
        var catalog = new Lazy<SqliteEvidenceCatalog>(() => new(new EvidenceCatalogOptions(Path.GetFullPath(config.CatalogPath),
            EvidenceCatalogOpenMode.ReadOnly), store.Value));
        return new(config, (id, token) => catalog.Value.GetDatasetAsync(id, token),
            new LazyPartitionReader(() => new ParquetEvidencePartitionDataReader(new EvidenceParquetCodec(), store.Value)), archive);
    }

    public MarketDataCacheSourceDescriptor CacheSourceDescriptor { get; }
    public MarketDataProvenance MarketDataProvenance => new(ProviderIdentity, DataFeed, Adjustment);
    public string HistoricalEndpointIdentity => "tradingflow-evidence-catalog-v1";
    public string HistoricalParserVersion => "evidence-catalog-market-bars-v1";
    public string HistoricalCalendarIdentity => "evidence-catalog-exchange-sessions:" + config.ExchangeSessionsDatasetId;

    /// <summary>Returns the stored bars of each stock and timeframe in [start, end), in time order, from the frozen datasets only.</summary>
    public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> timeframes,
        DateTimeOffset start, DateTimeOffset end, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var ticker in tickers)
        foreach (var timeframe in timeframes)
        {
            var (bars, _) = await ReadSliceAsync(ticker, timeframe, start, end, cancellationToken);
            foreach (var bar in bars) yield return bar;
        }
    }

    /// <summary>Answers a prepared-data request with the exact stored bars and a receipt naming the datasets and partitions read.</summary>
    public async Task<HistoricalBarsCapture> CaptureHistoricalBarsAsync(string ticker, string timeframe, DateTimeOffset startUtc,
        DateTimeOffset endUtc, long maximumBars, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var (bars, sources) = await ReadSliceAsync(ticker, timeframe, startUtc, endUtc, cancellationToken);
        if (bars.Count > maximumBars)
            throw new InvalidOperationException($"{ticker} {timeframe} has {bars.Count} stored bars, above the run's allowance of {maximumBars}.");
        var receipt = new
        {
            provider = ProviderName, ticker = ticker.ToUpperInvariant(), timeframe, startUtc, endUtc, rankedListSha256 = config.RankedListSha256,
            rows = bars.Count, sources = sources.Select(source => new { source.Dataset.DatasetId, source.Partition.PartitionId,
                artifactSha256 = source.Partition.Artifact.Content.Sha256, source.Partition.RowCount })
        };
        return new(bars, await ArchiveAsync("bars", receipt, started, cancellationToken));
    }

    /// <summary>Answers a prepared calendar request from the frozen exchange-session dataset, with a receipt naming it.</summary>
    public async Task<HistoricalCalendarCapture> CaptureHistoricalCalendarAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var sessions = await LoadMarketSessionSchedulesAsync(startDate, endDate, cancellationToken);
        var receipt = new { provider = ProviderName, exchange = Exchange, datasetId = config.ExchangeSessionsDatasetId, startDate, endDate,
            tradingDays = sessions.Values.Count(session => session.IsTradingDay) };
        return new(sessions, await ArchiveAsync("calendar", receipt, started, cancellationToken));
    }

    /// <summary>
    /// Every date from <paramref name="startDateInclusive"/> to <paramref name="endDateInclusive"/>: a trading day with its
    /// recorded New York open and close, or closed. Refuses dates outside the dataset's recorded coverage, so a calendar that
    /// does not reach the window end stops the run instead of being read as closed days.
    /// </summary>
    public async Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>> LoadMarketSessionSchedulesAsync(DateOnly startDateInclusive,
        DateOnly endDateInclusive, CancellationToken cancellationToken)
    {
        if (endDateInclusive < startDateInclusive) throw new ArgumentException("The calendar end precedes its start.", nameof(endDateInclusive));
        var loaded = await calendar.Value.WaitAsync(cancellationToken);
        if (startDateInclusive < loaded.From || endDateInclusive >= loaded.ToExclusive)
            throw new InvalidDataException($"Exchange-session dataset {config.ExchangeSessionsDatasetId} covers {loaded.From:yyyy-MM-dd} to " +
                $"{loaded.ToExclusive.AddDays(-1):yyyy-MM-dd}; the run needs {startDateInclusive:yyyy-MM-dd} to {endDateInclusive:yyyy-MM-dd}.");
        var result = new Dictionary<DateOnly, MarketSessionSchedule>();
        for (var date = startDateInclusive; date <= endDateInclusive; date = date.AddDays(1))
            result[date] = loaded.Sessions.TryGetValue(date, out var session) ? session : MarketSessionSchedule.Closed(date);
        return result;
    }

    /// <summary>
    /// The stored bars of one stock and timeframe in [start, end), ordered, with the partitions they came from. Two frozen
    /// datasets supplying the same bar refuse the request rather than choosing one.
    /// </summary>
    private async Task<(IReadOnlyList<OhlcvBar> Bars, IReadOnlyList<(EvidenceDatasetManifest Dataset, EvidenceDatasetPartitionManifest Partition)> Sources)> ReadSliceAsync(
        string ticker, string timeframe, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        var symbol = ticker.Trim().ToUpperInvariant();
        var frame = NormalizeTimeframe(timeframe);
        var sources = (await PartitionsAsync(frame, cancellationToken))
            .Where(item => item.Partition.Provenance.Symbols.Contains(symbol, StringComparer.Ordinal)).ToArray();
        var bars = new List<OhlcvBar>();
        foreach (var source in sources)
        {
            var rows = await cache.GetOrReadAsync(source.Dataset.DatasetId, source.Partition.PartitionId,
                () => ReadPartitionAsync(source.Dataset, source.Partition, frame, cancellationToken));
            if (rows.TryGetValue(symbol, out var stored)) bars.AddRange(stored.Where(bar => bar.Timestamp >= start && bar.Timestamp < end));
        }
        var ordered = bars.OrderBy(bar => bar.Timestamp).ToArray();
        for (var index = 1; index < ordered.Length; index++)
            if (ordered[index].Timestamp == ordered[index - 1].Timestamp)
                throw new InvalidDataException($"Two frozen {frame} datasets both hold the {symbol} bar at {ordered[index].Timestamp:O}.");
        return (ordered, sources);
    }

    /// <summary>The verified partitions of every frozen dataset of a timeframe, loaded once per timeframe.</summary>
    private Task<IReadOnlyList<(EvidenceDatasetManifest Dataset, EvidenceDatasetPartitionManifest Partition)>> PartitionsAsync(string frame,
        CancellationToken cancellationToken) =>
        partitions.GetOrAdd(frame, key => new(() => LoadPartitionsAsync(key, CancellationToken.None))).Value.WaitAsync(cancellationToken);

    /// <summary>Loads and checks the frozen datasets of one timeframe; the five-minute and one-minute ones must belong to the ranked list.</summary>
    private async Task<IReadOnlyList<(EvidenceDatasetManifest Dataset, EvidenceDatasetPartitionManifest Partition)>> LoadPartitionsAsync(string frame,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> ids = frame switch
        {
            "1d" => [config.DailyDatasetId],
            "5m" => config.FiveMinuteDatasetIds,
            "1m" => config.OneMinuteDatasetIds,
            _ => throw new NotSupportedException($"The evidence catalog provider serves 1d, 5m and 1m bars, not {frame}.")
        };
        var result = new List<(EvidenceDatasetManifest, EvidenceDatasetPartitionManifest)>();
        foreach (var id in ids)
        {
            var dataset = await RequireDatasetAsync(id, EvidenceDatasetKind.MarketBarsAsTraded, cancellationToken);
            if (dataset.Partitions.Any(partition => partition.Provenance.Timeframe != frame) || dataset.DataFeed != DataFeed)
                throw new InvalidDataException($"Dataset {id} does not hold {DataFeed} {frame} bars only.");
            if (frame != "1d" && dataset.Attributes.GetValueOrDefault("ranked_list_sha256") != config.RankedListSha256)
                throw new InvalidDataException($"Dataset {id} was not collected for ranked list {config.RankedListSha256}.");
            result.AddRange(dataset.Partitions.Select(partition => (dataset, partition)));
        }
        return result;
    }

    /// <summary>
    /// Every split in the frozen corporate-actions dataset (providers.evidence_catalog.corporate_actions_dataset), by symbol.
    /// Refuses when no corporate-actions dataset is configured, so a split can never be silently missed.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ShareSplit>>> LoadShareSplitsAsync(CancellationToken cancellationToken)
    {
        var id = config.CorporateActionsDatasetId ?? throw new InvalidOperationException(
            "providers.evidence_catalog.corporate_actions_dataset is not set; the run cannot see split ex-dates.");
        var dataset = await RequireDatasetAsync(id, EvidenceDatasetKind.CorporateActions, cancellationToken);
        return ShareSplits.FromCorporateActions(await reader.ReadCorporateActionsAsync(dataset, cancellationToken));
    }

    /// <summary>
    /// Reads a frozen dataset's committed manifest and refuses it unless its content fingerprint (the dataset ID computed from
    /// the manifest) equals the frozen ID and it has the expected kind.
    /// </summary>
    private async Task<EvidenceDatasetManifest> RequireDatasetAsync(string id, EvidenceDatasetKind kind, CancellationToken cancellationToken)
    {
        var dataset = await getDataset(id, cancellationToken)
            ?? throw new InvalidDataException($"Frozen dataset {id} is not in the evidence catalog.");
        if (!dataset.DatasetId.Equals(id, StringComparison.Ordinal))
            throw new InvalidDataException($"Frozen dataset {id} changed: the catalog now describes dataset {dataset.DatasetId}.");
        if (dataset.Kind != kind) throw new InvalidDataException($"Frozen dataset {id} is a {dataset.Kind} dataset, not {kind}.");
        return dataset;
    }

    /// <summary>Reads one verified partition and groups its rows by symbol as time-ordered bars.</summary>
    private async Task<IReadOnlyDictionary<string, OhlcvBar[]>> ReadPartitionAsync(EvidenceDatasetManifest dataset,
        EvidenceDatasetPartitionManifest partition, string frame, CancellationToken cancellationToken)
    {
        var single = new EvidenceDatasetManifest(dataset.Kind, dataset.SchemaVersion, dataset.CreatedAtUtc, dataset.CollectionJobId,
            dataset.CollectionPlanHash, dataset.ConfigHash, dataset.CodeVersion, dataset.NormalizerVersion, dataset.DataFeed, [partition],
            dataset.Quality, dataset.Attributes);
        var rows = await reader.ReadMarketBarsAsync(single, cancellationToken);
        return rows.GroupBy(row => row.Symbol, StringComparer.Ordinal).ToDictionary(group => group.Key,
            group => group.OrderBy(row => row.BarStartUtc).Select(row => ToBar(row, frame)).ToArray(), StringComparer.Ordinal);
    }

    /// <summary>One stored row as a bar: as-traded prices, SIP feed, the provider's interval start.</summary>
    private static OhlcvBar ToBar(MarketBarEvidenceRow row, string frame) => new(row.Symbol, row.BarStartUtc, frame,
        EvidenceFixedDecimal.FromPriceUnits(row.OpenPriceUnits), EvidenceFixedDecimal.FromPriceUnits(row.HighPriceUnits),
        EvidenceFixedDecimal.FromPriceUnits(row.LowPriceUnits), EvidenceFixedDecimal.FromPriceUnits(row.ClosePriceUnits), row.Volume,
        row.DataFeed, row.Adjustment);

    /// <summary>
    /// Loads the frozen exchange-session dataset once: XNYS sessions with their New York open and close, and the date range
    /// its partitions cover without gaps.
    /// </summary>
    private async Task<SessionCalendar> LoadCalendarAsync(CancellationToken cancellationToken)
    {
        var dataset = await RequireDatasetAsync(config.ExchangeSessionsDatasetId, EvidenceDatasetKind.ExchangeSessions, cancellationToken);
        var segments = dataset.Partitions.Select(partition => (From: DateOnly.FromDateTime(partition.Provenance.RequestedStartUtc.UtcDateTime),
            To: DateOnly.FromDateTime(partition.Provenance.RequestedEndUtc.UtcDateTime))).OrderBy(segment => segment.From).ToArray();
        for (var index = 1; index < segments.Length; index++)
            if (segments[index].From != segments[index - 1].To)
                throw new InvalidDataException($"Exchange-session dataset {dataset.DatasetId} has a gap or overlap at {segments[index].From:yyyy-MM-dd}.");
        var rows = await reader.ReadExchangeSessionsAsync(dataset, cancellationToken);
        var sessions = rows.Where(row => row.Exchange == Exchange).ToDictionary(row => row.TradeDate, row => new MarketSessionSchedule(row.TradeDate, true,
            TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(row.RegularOpenUtc, NewYork).DateTime),
            TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(row.RegularCloseUtc, NewYork).DateTime)));
        return new(segments[0].From, segments[^1].To, sessions);
    }

    /// <summary>Archives one receipt and returns it as completed collection evidence (a single complete answer, no pages).</summary>
    private async Task<HistoricalQueryEvidence> ArchiveAsync(string kind, object receipt, DateTimeOffset started, CancellationToken cancellationToken)
    {
        var stored = await archive.ArchiveAsync(new RawArchiveRequest("evidence-catalog", kind, DateTimeOffset.UtcNow, "json", "application/json",
            Attributes: new Dictionary<string, string> { ["rankedListSha256"] = config.RankedListSha256, ["networkRequest"] = "false" }),
            JsonSerializer.SerializeToUtf8Bytes(receipt), cancellationToken);
        return new(started, DateTimeOffset.UtcNow, true, [stored]);
    }

    /// <summary>The run's timeframe spelling (1d, 5m, 1m) for the requested one.</summary>
    private static string NormalizeTimeframe(string timeframe) => timeframe.Trim().ToLowerInvariant() switch
    {
        "1d" or "1day" => "1d",
        "5m" or "5min" => "5m",
        "1m" or "1min" => "1m",
        var other => other
    };

    /// <summary>Lower-case hex SHA-256 of text.</summary>
    private static string ResearchHash(string text) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    /// <summary>The frozen calendar: covered date range (end exclusive) and its trading sessions.</summary>
    private sealed record SessionCalendar(DateOnly From, DateOnly ToExclusive, IReadOnlyDictionary<DateOnly, MarketSessionSchedule> Sessions);

    /// <summary>
    /// Decoded partitions by dataset and partition ID, evicting the least recently used once the stored rows pass the limit.
    /// Reads of a partition not yet cached run once; concurrent callers share the result.
    /// </summary>
    private sealed class PartitionCache(long rowLimit)
    {
        private readonly object gate = new();
        private readonly Dictionary<(string, string), Task<IReadOnlyDictionary<string, OhlcvBar[]>>> entries = [];
        private readonly LinkedList<(string, string)> order = new();
        private long rows;

        /// <summary>
        /// Returns the cached partition (marking it most recently used), or reads it with <paramref name="read"/> and keeps
        /// it while room remains.
        /// </summary>
        public async Task<IReadOnlyDictionary<string, OhlcvBar[]>> GetOrReadAsync(string dataset, string partition,
            Func<Task<IReadOnlyDictionary<string, OhlcvBar[]>>> read)
        {
            var key = (dataset, partition);
            Task<IReadOnlyDictionary<string, OhlcvBar[]>> task;
            var created = false;
            lock (gate)
            {
                if (entries.TryGetValue(key, out task!))
                {
                    order.Remove(key);
                    order.AddLast(key);
                }
                else
                {
                    task = read();
                    entries[key] = task;
                    order.AddLast(key);
                    created = true;
                }
            }
            IReadOnlyDictionary<string, OhlcvBar[]> result;
            try { result = await task; }
            catch { lock (gate) { entries.Remove(key); order.Remove(key); } throw; }
            if (created) Account(result.Values.Sum(bars => (long)bars.Length));
            return result;
        }

        /// <summary>Adds a newly read partition's rows and evicts the oldest partitions above the limit.</summary>
        private void Account(long added)
        {
            lock (gate)
            {
                rows += added;
                while (rows > rowLimit && order.Count > 1)
                {
                    var oldest = order.First!.Value;
                    if (!entries.TryGetValue(oldest, out var evicted) || !evicted.IsCompletedSuccessfully) break;
                    entries.Remove(oldest);
                    order.RemoveFirst();
                    rows -= evicted.Result.Values.Sum(bars => (long)bars.Length);
                }
            }
        }
    }

    /// <summary>Creates the Parquet partition reader on first use only.</summary>
    private sealed class LazyPartitionReader(Func<IEvidencePartitionDataReader> create) : IEvidencePartitionDataReader
    {
        private readonly Lazy<IEvidencePartitionDataReader> inner = new(create);
        public Task<IReadOnlyList<MarketBarEvidenceRow>> ReadMarketBarsAsync(EvidenceDatasetManifest manifest, CancellationToken cancellationToken = default) =>
            inner.Value.ReadMarketBarsAsync(manifest, cancellationToken);
        public Task<IReadOnlyList<ExchangeSessionEvidenceRow>> ReadExchangeSessionsAsync(EvidenceDatasetManifest manifest, CancellationToken cancellationToken = default) =>
            inner.Value.ReadExchangeSessionsAsync(manifest, cancellationToken);
        public Task<IReadOnlyList<NewsRevisionEvidenceRow>> ReadNewsRevisionsAsync(EvidenceDatasetManifest manifest, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The evidence catalog provider reads market bars and exchange sessions only.");
        public Task<IReadOnlyList<SentimentAssessmentEvidenceRow>> ReadSentimentAssessmentsAsync(EvidenceDatasetManifest manifest, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The evidence catalog provider reads market bars and exchange sessions only.");
        public Task<IReadOnlyList<SecurityMasterSnapshotEvidenceRow>> ReadSecurityMasterSnapshotsAsync(EvidenceDatasetManifest manifest, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The evidence catalog provider reads market bars and exchange sessions only.");
        public Task<IReadOnlyList<SymbolIntervalEvidenceRow>> ReadSymbolIntervalsAsync(EvidenceDatasetManifest manifest, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The evidence catalog provider reads market bars and exchange sessions only.");
        public Task<IReadOnlyList<CorporateActionEvidenceRow>> ReadCorporateActionsAsync(EvidenceDatasetManifest manifest, CancellationToken cancellationToken = default) =>
            inner.Value.ReadCorporateActionsAsync(manifest, cancellationToken);
        public Task<IReadOnlyList<UniverseMembershipEvidenceRow>> ReadUniverseMembershipAsync(EvidenceDatasetManifest manifest, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The evidence catalog provider reads market bars and exchange sessions only.");
        public Task<IReadOnlyList<SipQuoteEvidenceRow>> ReadSipQuotesAsync(EvidenceDatasetManifest manifest, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The evidence catalog provider reads market bars and exchange sessions only.");
    }
}
