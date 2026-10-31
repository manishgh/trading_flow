using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using TradingFlow.Data.Csv;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Backtesting;

/// <summary>Owns verified read-only source leases for a fixed comparison; no provider exposed here can collect replacements.</summary>
public sealed class PreparedMarketDataHandle : IDisposable
{
    private readonly IReadOnlyList<FileStream> leases;
    private readonly PreparedMarketDataRequest request;
    private readonly IReadOnlyList<PreparedMarketDataService.Query> queries;
    private readonly IReadOnlyList<PreparedMarketDataPart> parts;
    private bool disposed;

    /// <summary>Constructs a handle only after the complete request, selection and source files have been verified.</summary>
    internal PreparedMarketDataHandle(PreparedMarketDataRequest request,
        IReadOnlyList<PreparedMarketDataService.Query> queries, IReadOnlyList<PreparedMarketDataPart> parts,
        PreparedMarketDataSummary summary, IReadOnlyList<FileStream> leases)
    {
        this.request = request;
        this.queries = queries;
        this.parts = parts;
        this.leases = leases;
        Summary = summary;
        VerifiedFiles = leases.Select(stream => stream.Name).ToArray();
    }

    public PreparedMarketDataSummary Summary { get; }
    public IReadOnlyList<string> VerifiedFiles { get; }

    /// <summary>Restricts a child to admitted book requirements while ignoring its disposable workspace paths.</summary>
    public IMarketDataProvider CreateProvider(BacktestRunConfig run)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var resolved = PreparedMarketDataService.ResolveRun(run, request.EvaluationCutoffUtc);
        var matches = request.Books.Where(book => Matches(PreparedMarketDataService.ResolveRun(book.Run, request.EvaluationCutoffUtc), resolved))
            .Select(book => book.BookId).ToHashSet(StringComparer.Ordinal);
        if (matches.Count == 0) throw new InvalidDataException("Run is not a book admitted to this prepared comparison.");
        var allowed = queries.Where(query => query.Books.Any(matches.Contains)).Select(query => query.Id).ToHashSet(StringComparer.Ordinal);
        var versions = parts.Where(part => allowed.Contains(part.QueryId)).Select(part => part.Version).ToArray();
        return versions.Any(version => version.IsCalendar)
            ? new CalendarProvider(this, versions, run.Engine.MaxRetainedReplayBars)
            : new PinnedProvider(this, versions, run.Engine.MaxRetainedReplayBars);
    }

    /// <summary>Matches source semantics and accepted bounds, permitting only resolved historical-universe subsets.</summary>
    private static bool Matches(BacktestRunConfig admitted, BacktestRunConfig actual) =>
        admitted.Provider.Equals(actual.Provider, StringComparison.OrdinalIgnoreCase) &&
        admitted.Providers.Alpaca.DataFeed.Equals(actual.Providers.Alpaca.DataFeed, StringComparison.OrdinalIgnoreCase) &&
        admitted.Engine.ExecutionReplayPolicyId == actual.Engine.ExecutionReplayPolicyId &&
        admitted.TimeWindow == actual.TimeWindow &&
        actual.Tickers.All(ticker => admitted.Tickers.Concat(admitted.Universe?.Candidates ?? []).Contains(ticker, StringComparer.OrdinalIgnoreCase)) &&
        actual.Intervals.All(interval => admitted.Intervals.Contains(interval, StringComparer.OrdinalIgnoreCase));

    /// <summary>Releases read sharing only; committed request, candles, calendars and original receipts remain retained.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var lease in leases) lease.Dispose();
    }

    private class PinnedProvider : IMarketDataProvider, IMarketDataProvenanceProvider, IMarketDataCompletenessProvider
    {
        protected readonly PreparedMarketDataHandle Owner;
        protected readonly IReadOnlyList<PreparedMarketDataVersion> Versions;
        private readonly long maximum;

        /// <summary>Retains immutable version descriptors without opening another source adapter.</summary>
        public PinnedProvider(PreparedMarketDataHandle owner, IReadOnlyList<PreparedMarketDataVersion> versions, long maximum)
        { Owner = owner; Versions = versions; this.maximum = maximum; }

        public bool OmittedSubDailyIntervalsMeanNoQualifyingTrades =>
            Versions.Where(version => !version.IsCalendar).All(version => version.OmittedSubDailyIntervalsMeanNoQualifyingTrades);

        public MarketDataProvenance MarketDataProvenance
        {
            get
            {
                var identities = Versions.Where(version => !version.IsCalendar).Select(version =>
                    new MarketDataProvenance(version.ProviderIdentity, version.DataFeed, version.AdjustmentPolicy)).Distinct().ToArray();
                return identities.Length == 1 ? identities[0] : throw new InvalidDataException("Prepared book contains inconsistent source provenance.");
            }
        }

        /// <summary>Reads only exact admitted source slices; absent or corrupt versions fail without any network fallback.</summary>
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes, DateTimeOffset start, DateTimeOffset end,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Owner.disposed, Owner);
            foreach (var ticker in tickers)
                foreach (var timeframe in timeframes)
                {
                    var selected = Versions.Where(version => !version.IsCalendar &&
                        version.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase) &&
                        version.Timeframe.Equals(timeframe, StringComparison.OrdinalIgnoreCase) &&
                        version.StartUtc == start && version.EndUtc == end).ToArray();
                    if (selected.Length != 1) throw new InvalidDataException($"No unique pinned source request for {ticker} {timeframe} {start:O} through {end:O}.");
                    var bars = await CachedMarketDataProvider.ReadPreparedBarsAsync(selected[0], maximum, cancellationToken);
                    foreach (var bar in bars) { cancellationToken.ThrowIfCancellationRequested(); yield return bar; }
                }
        }
    }

    private sealed class CalendarProvider : PinnedProvider, IMarketSessionScheduleProvider
    {
        /// <summary>Exposes calendars only for books whose admitted source actually supplied authoritative sessions.</summary>
        public CalendarProvider(PreparedMarketDataHandle owner, IReadOnlyList<PreparedMarketDataVersion> versions, long maximum)
            : base(owner, versions, maximum) { }

        /// <summary>Reads the exact selected calendar; newly requested dates cannot trigger provider collection.</summary>
        public async Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>> LoadMarketSessionSchedulesAsync(
            DateOnly startDateInclusive, DateOnly endDateInclusive, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Owner.disposed, Owner);
            var selected = Versions.Where(version => version.IsCalendar &&
                DateOnly.FromDateTime(version.StartUtc.UtcDateTime) == startDateInclusive &&
                DateOnly.FromDateTime(version.EndUtc.UtcDateTime) == endDateInclusive).ToArray();
            if (selected.Length != 1) throw new InvalidDataException("Requested exchange dates are not pinned in this prepared book.");
            return await CachedMarketDataProvider.ReadPreparedCalendarAsync(selected[0], cancellationToken);
        }
    }
}

public sealed partial class PreparedMarketDataService
{
    /// <summary>Verifies binding bytes and all evidence before leasing the entire comparison source graph.</summary>
    private static async Task<PreparedMarketDataHandle> OpenHandleAsync(PreparedMarketDataRequest request,
        IReadOnlyList<Query> queries, PreparedMarketDataBinding binding, string requestPath, CancellationToken token)
    {
        var bytes = await File.ReadAllBytesAsync(binding.SnapshotPath, token);
        if (Hash(bytes) != binding.SnapshotSha256) throw new InvalidDataException("Pinned comparison snapshot changed after binding.");
        var selection = JsonSerializer.Deserialize<PreparedMarketDataSelection>(bytes, Json)
            ?? throw new InvalidDataException("Prepared comparison snapshot is empty.");
        RequireSelection(selection, binding.RequestSha256, queries);
        await VerifyPartsAsync(request, queries, selection.Parts, token);
        if (JsonSerializer.Serialize(Summary(selection, binding.SnapshotSha256), Json) != JsonSerializer.Serialize(binding.Summary, Json))
            throw new InvalidDataException("Prepared binding summary differs from its immutable snapshot.");
        var files = new List<PreparedMarketDataEvidenceFile>();
        foreach (var part in selection.Parts)
            files.AddRange(await CachedMarketDataProvider.GetPreparedEvidenceAsync(part.Version, token));
        files.Add(new(requestPath, new FileInfo(requestPath).Length, binding.RequestSha256));
        files.Add(new(binding.SnapshotPath, bytes.Length, binding.SnapshotSha256));
        await VerifyFrozenCalendarsAsync(request, selection.Parts, files, token);
        var streams = new List<FileStream>();
        try
        {
            foreach (var file in files.DistinctBy(file => Path.GetFullPath(file.Path), StringComparer.OrdinalIgnoreCase).OrderBy(file => file.Path))
            {
                token.ThrowIfCancellationRequested();
                var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                streams.Add(stream);
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
                if (stream.Length != file.Length || !hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Prepared source evidence changed before its read lease was acquired.");
                stream.Position = 0;
            }
            return new(request, queries, selection.Parts, binding.Summary, streams);
        }
        catch { foreach (var stream in streams) stream.Dispose(); throw; }
    }

    /// <summary>Rejects any conflicting REST session while preserving the existing frozen SIP calendar's execution authority.</summary>
    private static async Task VerifyFrozenCalendarsAsync(PreparedMarketDataRequest request,
        IReadOnlyList<PreparedMarketDataPart> parts, List<PreparedMarketDataEvidenceFile> files, CancellationToken token)
    {
        foreach (var book in request.Books.Where(book => book.Run.Engine.ExecutionSourceManifestPath is not null))
        {
            var engine = book.Run.Engine;
            var source = EventPortfolioSourceBinding.ReadManifest(engine.ExecutionSourceManifestPath!, engine.ExecutionSourceManifestSha256!);
            var frozen = FrozenExchangeCalendar.Read(source.CalendarReceiptPath, source.CalendarReceiptSha256);
            files.Add(new(engine.ExecutionSourceManifestPath!, new FileInfo(engine.ExecutionSourceManifestPath!).Length, engine.ExecutionSourceManifestSha256!));
            files.Add(new(source.CalendarReceiptPath, new FileInfo(source.CalendarReceiptPath).Length, source.CalendarReceiptSha256));
            files.Add(new(frozen.PayloadPath, new FileInfo(frozen.PayloadPath).Length, frozen.PayloadSha256));
            var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            foreach (var part in parts.Where(part => part.Version.IsCalendar))
                foreach (var schedule in (await CachedMarketDataProvider.ReadPreparedCalendarAsync(part.Version, token)).Values.Where(schedule => schedule.TradeDate >= frozen.From && schedule.TradeDate <= frozen.Through))
                {
                    var session = frozen.Sessions.SingleOrDefault(session => session.Date == schedule.TradeDate);
                    if (schedule.IsTradingDay != (session is not null) || session is not null &&
                        (TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(session.OpenUtc, zone).DateTime) != schedule.RegularOpen ||
                         TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(session.CloseUtc, zone).DateTime) != schedule.RegularClose))
                        throw new InvalidDataException($"Prepared calendar conflicts with frozen SIP exchange session {schedule.TradeDate:yyyy-MM-dd}.");
                }
        }
    }
}
