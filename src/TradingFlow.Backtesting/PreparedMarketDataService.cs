using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using TradingFlow.Data.Csv;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Backtesting;

/// <summary>Prepares the comparison union under current-host locks, commits every choice, then releases the gate before execution.</summary>
public sealed partial class PreparedMarketDataService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConfiguredDataProviders dataProviders;
    private readonly Func<BacktestRunConfig, TradingFlow.Engine.Abstractions.IMarketDataProvider>? providerFactory;
    private readonly IPreparedMarketDataBindingStore? bindingStore;

    /// <summary>Shares the host's normal adapters and allows explicit evidence-producing frozen providers in offline callers.</summary>
    public PreparedMarketDataService(ConfiguredDataProviders dataProviders,
        Func<BacktestRunConfig, TradingFlow.Engine.Abstractions.IMarketDataProvider>? marketDataProviderFactory = null,
        IPreparedMarketDataBindingStore? bindingStore = null)
    {
        this.dataProviders = dataProviders;
        providerFactory = marketDataProviderFactory;
        this.bindingStore = bindingStore;
    }

    /// <summary>Freezes admission parameters through existing exclusive publication without collecting provider data.</summary>
    public static async Task<PreparedMarketDataRequestReference> WriteRequestAsync(PreparedMarketDataRequest request,
        string absolutePath, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(absolutePath))
            throw new ArgumentException("Prepared request path must be absolute.", nameof(absolutePath));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, Json);
        await PublishExactAsync(absolutePath, bytes, token);
        return new(Path.GetFullPath(absolutePath), Hash(bytes));
    }

    /// <summary>Reads the admitted immutable request bytes and uses their directory for comparison-owned selections.</summary>
    public async Task<PreparedMarketDataHandle> PrepareAsync(PreparedMarketDataRequestReference reference,
        IProgress<BacktestProgress>? progress, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(reference.Path)) throw new InvalidDataException("Prepared request path must be absolute.");
        var bytes = await File.ReadAllBytesAsync(reference.Path, cancellationToken);
        if (!Hash(bytes).Equals(reference.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Prepared comparison request changed after admission.");
        var request = JsonSerializer.Deserialize<PreparedMarketDataRequest>(bytes, Json)
            ?? throw new InvalidDataException("Prepared comparison request is empty.");
        var root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reference.Path))!,
            "_prepared-" + Hash(System.Text.Encoding.UTF8.GetBytes(request.ComparisonId)));
        return await PrepareCoreAsync(request, reference.Sha256.ToLowerInvariant(), root, reference.Path, progress, cancellationToken);
    }

    /// <summary>Standalone and optimizer callers publish the same immutable request beside their retained result artifacts.</summary>
    public async Task<PreparedMarketDataHandle> PrepareAsync(PreparedMarketDataRequest request,
        IProgress<BacktestProgress>? progress, CancellationToken cancellationToken)
    {
        if (request.Books is not { Count: > 0 }) throw new InvalidDataException("Prepared comparison has no books.");
        var root = Path.Combine(Path.GetFullPath(request.Books[0].Run.ResultsRoot), "_prepared",
            Hash(System.Text.Encoding.UTF8.GetBytes(request.ComparisonId)));
        Directory.CreateDirectory(root);
        var requestPath = Path.Combine(root, "request.json");
        var reference = await WriteRequestAsync(request, requestPath, cancellationToken);
        return await PrepareCoreAsync(request, reference.Sha256, root, requestPath, progress, cancellationToken);
    }

    /// <summary>Recovers per-query choices, validates a completed snapshot and binds it once before returning any executable provider.</summary>
    private async Task<PreparedMarketDataHandle> PrepareCoreAsync(PreparedMarketDataRequest request,
        string requestHash, string root, string requestPath, IProgress<BacktestProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(BacktestProgress.StageOnly("preparing_candles", "Preparing the comparison's fixed candles and exchange calendars."));
        var queries = BuildQueries(request);
        Directory.CreateDirectory(root);
        var gate = Gates.GetOrAdd(root, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var fileLock = await AcquireLockAsync(Path.Combine(root, "preparation.lock"), cancellationToken);
            await PublishExactAsync(Path.Combine(root, "request-identity.json"),
                JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 1, RequestSha256 = requestHash }, Json), cancellationToken);
            var snapshotPath = Path.Combine(root, "snapshot.json");
            var bindingPath = Path.Combine(root, "selection.json");
            var binding = bindingStore is null
                ? File.Exists(bindingPath) ? await ReadAsync<PreparedMarketDataBinding>(bindingPath, cancellationToken) : null
                : await bindingStore.GetAsync(request.ComparisonId, cancellationToken);
            if (binding is not null)
            {
                if (!binding.RequestSha256.Equals(requestHash, StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetFullPath(binding.SnapshotPath).Equals(snapshotPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Comparison is already bound to different prepared inputs.");
                return await OpenHandleAsync(request, queries, binding, requestPath, cancellationToken);
            }

            PreparedMarketDataSelection selection;
            if (File.Exists(snapshotPath))
            {
                selection = await ReadAsync<PreparedMarketDataSelection>(snapshotPath, cancellationToken);
                RequireSelection(selection, requestHash, queries);
                await VerifyPartsAsync(request, queries, selection.Parts, cancellationToken);
            }
            else
            {
                var parts = await SelectPartsAsync(request, requestHash, queries, root, progress, cancellationToken);
                await VerifyFrozenCalendarsAsync(request, parts, [], cancellationToken);
                selection = new(1, requestHash, DateTimeOffset.UtcNow, parts);
                await PublishExactAsync(snapshotPath, JsonSerializer.SerializeToUtf8Bytes(selection, Json), cancellationToken);
            }
            var snapshotBytes = await File.ReadAllBytesAsync(snapshotPath, cancellationToken);
            var snapshotHash = Hash(snapshotBytes);
            binding = new(requestHash, snapshotPath, snapshotHash, Summary(selection, snapshotHash));
            // Obtain verified read leases before persisting the binding; no book sees an unverified publication.
            var handle = await OpenHandleAsync(request, queries, binding, requestPath, cancellationToken);
            try
            {
                if (bindingStore is null) await PublishExactAsync(bindingPath, JsonSerializer.SerializeToUtf8Bytes(binding, Json), cancellationToken);
                else await bindingStore.BindAsync(request.ComparisonId, binding, cancellationToken);
                return handle;
            }
            catch { handle.Dispose(); throw; }
        }
        finally { gate.Release(); }
    }

    /// <summary>Commits immutable query receipts and shares discovery metadata within preparation; refresh recovery never recollects selected parts.</summary>
    private static async Task<IReadOnlyList<PreparedMarketDataPart>> SelectPartsAsync(PreparedMarketDataRequest request,
        string requestHash, IReadOnlyList<Query> queries, string root, IProgress<BacktestProgress>? progress, CancellationToken token)
    {
        var parts = new List<PreparedMarketDataPart>();
        var used = request.Books.ToDictionary(book => book.BookId, _ => 0L, StringComparer.Ordinal);
        var limits = request.Books.ToDictionary(book => book.BookId, book => (long)book.Run.Engine.MaxRetainedReplayBars, StringComparer.Ordinal);
        var readers = new Dictionary<(IMarketDataProvider Provider, string Root, string Policy), CachedMarketDataProvider>();
        foreach (var query in queries)
        {
            token.ThrowIfCancellationRequested();
            var partPath = Path.Combine(root, "part-" + query.Id + ".json");
            var maximum = query.CalendarStart.HasValue ? Int64.MaxValue : query.Books.Min(book => limits[book] - used[book]);
            if (maximum <= 0) throw new InvalidOperationException("Prepared source rows exceed engine.max_retained_replay_bars for a referencing book.");
            PreparedMarketDataPart part;
            if (File.Exists(partPath)) part = await ReadAsync<PreparedMarketDataPart>(partPath, token);
            else
            {
                var key = (query.Provider, query.Run.NormalizedRoot, query.Run.CachePolicy);
                if (!readers.TryGetValue(key, out var cache))
                {
                    cache = new CachedMarketDataProvider(query.Provider, query.Run.NormalizedRoot, query.Run.CachePolicy);
                    readers.Add(key, cache);
                }
                var version = query.CalendarStart is { } first
                    ? await cache.SelectPreparedCalendarAsync(first, query.CalendarEnd!.Value, token)
                    : await cache.SelectPreparedBarsAsync(query.Ticker!, query.Timeframe!, query.Start, query.End, maximum, token);
                part = new(query.Id, version);
                await PublishExactAsync(partPath, JsonSerializer.SerializeToUtf8Bytes(part, Json), token);
            }
            await VerifyPartAsync(query, part, maximum, token);
            if (!part.Version.IsCalendar)
                foreach (var book in query.Books) used[book] = checked(used[book] + part.Version.RowCount);
            parts.Add(part);
            progress?.Report(new BacktestProgress("preparing_candles", $"Prepared {parts.Count}/{queries.Count} source requests.", query.Ticker, parts.Count, queries.Count));
        }
        return parts;
    }

    /// <summary>Checks exact request membership and row totals during recovery without consulting latest pointers or provider APIs.</summary>
    private static async Task VerifyPartsAsync(PreparedMarketDataRequest request, IReadOnlyList<Query> queries, IReadOnlyList<PreparedMarketDataPart> parts, CancellationToken token)
    {
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        var limits = request.Books.ToDictionary(book => book.BookId, book => (long)book.Run.Engine.MaxRetainedReplayBars, StringComparer.Ordinal);
        foreach (var query in queries)
        {
            var part = parts.Single(part => part.QueryId == query.Id);
            await VerifyPartAsync(query, part, query.Books.Min(book => limits[book]), token);
            if (part.Version.IsCalendar) continue;
            foreach (var book in query.Books)
                totals[book] = checked(totals.GetValueOrDefault(book) + part.Version.RowCount);
        }
        foreach (var book in limits.Keys)
            if (totals.GetValueOrDefault(book) > limits[book])
                throw new InvalidDataException("Prepared source row totals exceed a referencing book's replay allowance.");
    }

    /// <summary>Rejects altered receipt geometry before strict leaf readers verify payload and original response evidence.</summary>
    private static async Task VerifyPartAsync(Query query, PreparedMarketDataPart part, long maximum, CancellationToken token)
    {
        var version = part.Version;
        var cache = new CachedMarketDataProvider(query.Provider, query.Run.NormalizedRoot, query.Run.CachePolicy);
        var expectedRequestHash = query.CalendarStart is { } first
            ? cache.GetPreparedCalendarRequestSha256(first, query.CalendarEnd!.Value)
            : cache.GetPreparedBarsRequestSha256(query.Ticker!, query.Timeframe!, query.Start, query.End);
        if (part.QueryId != query.Id || version.IsCalendar != query.CalendarStart.HasValue ||
            version.RequestSha256 != expectedRequestHash ||
            (!version.IsCalendar && (version.Ticker != query.Ticker || version.Timeframe != query.Timeframe || version.StartUtc != query.Start || version.EndUtc != query.End)) ||
            (version.IsCalendar && (DateOnly.FromDateTime(version.StartUtc.UtcDateTime) != query.CalendarStart || DateOnly.FromDateTime(version.EndUtc.UtcDateTime) != query.CalendarEnd)))
            throw new InvalidDataException("Prepared query receipt differs from the admitted source request.");
        if (version.IsCalendar) _ = await CachedMarketDataProvider.ReadPreparedCalendarAsync(version, token);
        else _ = await CachedMarketDataProvider.ReadPreparedBarsAsync(version, maximum, token);
    }

    /// <summary>Requires the whole admitted query graph, so a published partial selection cannot authorize execution.</summary>
    private static void RequireSelection(PreparedMarketDataSelection selection, string requestHash, IReadOnlyList<Query> queries)
    {
        if (selection.SchemaVersion != 1 || selection.RequestSha256 != requestHash || selection.Parts.Count != queries.Count ||
            !selection.Parts.Select(part => part.QueryId).Order().SequenceEqual(queries.Select(query => query.Id).Order()))
            throw new InvalidDataException("Prepared snapshot does not contain the complete admitted request.");
    }

    /// <summary>Reports actual capture times and the historical revision limitation without exposing storage paths.</summary>
    private static PreparedMarketDataSummary Summary(PreparedMarketDataSelection selection, string hash)
    {
        var versions = selection.Parts.Select(part => part.Version).ToArray();
        var bars = versions.Where(version => !version.IsCalendar).ToArray();
        return new(hash[..24], hash, selection.PreparedAtUtc, bars.Min(version => version.StartUtc), bars.Max(version => version.EndUtc),
            bars.Select(version => version.ProviderIdentity).Distinct().Order().ToArray(), bars.Select(version => version.DataFeed).Distinct().Order().ToArray(),
            bars.Select(version => version.AdjustmentPolicy).Distinct().Order().ToArray(), versions.Count(version => version.Reused), versions.Count(version => !version.Reused),
            $"Historical bars captured between {versions.Min(version => version.CollectedAtUtc):O} and {versions.Max(version => version.CollectedAtUtc):O}; original historical revision availability remains unproved where applicable.");
    }

    /// <summary>Publishes immutable artifacts through an exclusively created same-directory file and refuses different existing bytes.</summary>
    private static async Task PublishExactAsync(string path, byte[] bytes, CancellationToken token)
    {
        try
        {
            await AtomicFileArtifactWriter.Instance.WriteStreamExclusiveAsync(path,
                (stream, cancellationToken) => stream.WriteAsync(bytes, cancellationToken).AsTask(), token);
        }
        catch (IOException) when (File.Exists(path))
        {
            var existing = await File.ReadAllBytesAsync(path, token);
            if (!bytes.AsSpan().SequenceEqual(existing))
                throw new InvalidDataException("Immutable comparison artifact already exists with different bytes.");
        }
    }

    /// <summary>Waits asynchronously for the current host's preparation owner; cancellation leaves all committed data intact.</summary>
    private static async Task<FileStream> AcquireLockAsync(string path, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(100, token); }
        }
    }

    /// <summary>Reads retained selection metadata; malformed committed JSON fails instead of triggering recollection.</summary>
    private static async Task<T> ReadAsync<T>(string path, CancellationToken token) =>
        JsonSerializer.Deserialize<T>(await File.ReadAllBytesAsync(path, token), Json) ?? throw new InvalidDataException("Prepared artifact is empty.");
}
