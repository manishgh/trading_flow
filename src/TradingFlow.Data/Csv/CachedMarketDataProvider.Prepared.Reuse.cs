using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;
using System.Collections.Concurrent;

namespace TradingFlow.Data.Csv;

public sealed partial class CachedMarketDataProvider
{
    // Cache only immutable discovery metadata for this reader's lifetime; every selected file is independently re-read and hashed.
    private readonly ConcurrentDictionary<string, PreparedVersionManifest> preparedDiscoveryMetadata = new(StringComparer.OrdinalIgnoreCase);

    private sealed record PreparedSelectionRequest(string Hash, string Ticker, string Timeframe,
        DateTimeOffset Start, DateTimeOffset End, bool IsCalendar);

    /// <summary>Reads a compatible verified history slice for ordinary cache consumers without collecting on a miss.</summary>
    public async Task<IReadOnlyList<OhlcvBar>?> TryReadExistingPreparedBarsAsync(string ticker, string timeframe,
        DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken)
    {
        if (!CanReusePrepared()) return null;
        var symbol = NormalizePathSegment(ticker, nameof(ticker), upperCase: true);
        var frame = NormalizePathSegment(timeframe, nameof(timeframe), upperCase: false);
        var request = new PreparedSelectionRequest(GetPreparedBarsRequestSha256(symbol, frame, startUtc, endUtc),
            symbol, frame, startUtc.ToUniversalTime(), endUtc.ToUniversalTime(), false);
        var version = await FindExistingPreparedAsync(request, Int64.MaxValue, cancellationToken);
        return version is null ? null : await ReadPreparedBarsAsync(version, Int64.MaxValue, cancellationToken);
    }

    /// <summary>Reads explicit exchange dates from a compatible parent calendar without provider fallback.</summary>
    public async Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>?> TryReadExistingPreparedCalendarAsync(
        DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken)
    {
        if (!CanReusePrepared()) return null;
        var request = new PreparedSelectionRequest(GetPreparedCalendarRequestSha256(startDate, endDate),
            String.Empty, String.Empty, CalendarUtc(startDate), CalendarUtc(endDate), true);
        var version = await FindExistingPreparedAsync(request, Int64.MaxValue, cancellationToken);
        return version is null ? null : await ReadPreparedCalendarAsync(version, cancellationToken);
    }

    /// <summary>Returns a complete verified calendar prefix through the first date when it includes a prior real trading session.</summary>
    public async Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>?> TryReadExistingPreparedSessionContextAsync(
        DateOnly firstDate, CancellationToken cancellationToken)
    {
        if (!CanReusePrepared()) return null;
        var request = new PreparedSelectionRequest(GetPreparedCalendarRequestSha256(firstDate, firstDate),
            String.Empty, String.Empty, CalendarUtc(firstDate), CalendarUtc(firstDate), true);
        foreach (var candidate in await FindPreparedCandidatesAsync(request, cancellationToken))
        {
            try
            {
                var version = await DescribePreparedVersionAsync(candidate.Path, candidate.Manifest, true, cancellationToken);
                var sessions = await ReadPreparedCalendarAsync(version, cancellationToken);
                var prefix = sessions.Where(pair => pair.Key <= firstDate).ToDictionary();
                if (prefix.Values.Any(session => session.TradeDate < firstDate && session.IsTradingDay)) return prefix;
            }
            catch (Exception ex) when (IsPreparedCacheMiss(ex)) { }
        }
        return null;
    }

    /// <summary>Requires captured provenance and respects explicit refresh for every ordinary prepared-history lookup.</summary>
    private bool CanReusePrepared() => !cachePolicy.Equals("refresh", StringComparison.OrdinalIgnoreCase) &&
        sourceDescriptor is not null && innerProvider is IHistoricalMarketDataEvidenceProvider;

    /// <summary>Chooses the first deterministic valid parent and freezes its exact admitted slice without copying source files.</summary>
    private async Task<PreparedMarketDataVersion?> FindExistingPreparedAsync(PreparedSelectionRequest request,
        long maximumBars, CancellationToken cancellationToken)
    {
        foreach (var candidate in await FindPreparedCandidatesAsync(request, cancellationToken))
        {
            try
            {
                var parent = await DescribePreparedVersionAsync(candidate.Path, candidate.Manifest, true, cancellationToken);
                long rows;
                if (request.IsCalendar)
                {
                    _ = await ReadPreparedCalendarAsync(parent, cancellationToken);
                    rows = (DateOnly.FromDateTime(request.End.UtcDateTime).DayNumber -
                        DateOnly.FromDateTime(request.Start.UtcDateTime).DayNumber) + 1L;
                }
                else
                {
                    var manifest = await VerifyPreparedVersionAsync(parent, cancellationToken);
                    rows = ReadPreparedBarSlice(parent, manifest, request.Start, request.End,
                        maximumBars, false, cancellationToken).RowCount;
                }
                EnforcePreparedBudget(rows, maximumBars);
                return parent.RequestSha256 == request.Hash ? parent :
                    AdmitPreparedSlice(parent, request.Hash, request.Start, request.End, rows);
            }
            catch (Exception ex) when (IsPreparedCacheMiss(ex)) { }
        }
        return null;
    }

    /// <summary>Examines immutable request metadata first and hashes only compatible containing candidates in a stable preference order.</summary>
    private async Task<IReadOnlyList<(string Path, PreparedVersionManifest Manifest)>> FindPreparedCandidatesAsync(
        PreparedSelectionRequest request, CancellationToken cancellationToken)
    {
        var root = Path.Combine(normalizedRoot, "_historical");
        var candidates = new List<(string Path, PreparedVersionManifest Manifest)>();
        if (!Directory.Exists(root)) return candidates;
        foreach (var path in Directory.EnumerateFiles(root, "version-*.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var manifest = preparedDiscoveryMetadata.TryGetValue(path, out var cached)
                    ? cached : await MarketSessionCalendarCacheStore.ReadAsync<PreparedVersionManifest>(path, cancellationToken);
                if (manifest is not null) preparedDiscoveryMetadata.TryAdd(path, manifest);
                if (manifest is not null && PreparedSourceMatches(manifest, request)) candidates.Add((path, manifest));
            }
            catch (Exception ex) when (IsPreparedCacheMiss(ex)) { }
        }
        return candidates.OrderByDescending(candidate => candidate.Manifest.RequestSha256 == request.Hash)
            .ThenBy(candidate => PreparedBounds(candidate.Manifest).End - PreparedBounds(candidate.Manifest).Start)
            .ThenByDescending(candidate => candidate.Manifest.Evidence.CollectionCompletedAtUtc)
            .ThenBy(candidate => candidate.Path, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Requires the same source contract and containing original query; historical reuse preserves the captured revision without claiming a current restatement.</summary>
    private bool PreparedSourceMatches(PreparedVersionManifest manifest, PreparedSelectionRequest request)
    {
        var provider = RequireEvidenceProvider();
        if (!HasPreparedCandidateStructure(manifest) || manifest.SchemaVersion != PreparedSchemaVersion || manifest.Source != sourceDescriptor ||
            manifest.EndpointIdentity != provider.HistoricalEndpointIdentity ||
            manifest.ParserVersion != PreparedParserContract + "+" + provider.HistoricalParserVersion ||
            manifest.CalendarIdentity != provider.HistoricalCalendarIdentity ||
            (manifest.Calendar is not null) != request.IsCalendar ||
            (!request.IsCalendar && (manifest.Bars!.Ticker != request.Ticker || manifest.Bars.Timeframe != request.Timeframe ||
                manifest.OmittedSubDailyIntervalsMeanNoQualifyingTrades != OmittedSubDailyIntervalsMeanNoQualifyingTrades))) return false;
        var (start, end) = PreparedBounds(manifest);
        if (start > request.Start || end < request.End || manifest.RequestSha256 != PreparedRequestHash(
                manifest.Source, provider, request.Ticker, request.Timeframe, start, end, request.IsCalendar)) return false;
        var now = freshnessPolicy.TimeProvider.GetUtcNow();
        if (manifest.Evidence.CollectionCompletedAtUtc > now) return false;
        var historicalEnd = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        if (request.End < historicalEnd) return true;
        return now - manifest.Evidence.CollectionCompletedAtUtc < freshnessPolicy.MaximumAge &&
            (manifest.Bars is null || manifest.Bars.MatchesRequest(manifest.Source, request.Ticker, request.Timeframe,
                start, end, freshnessPolicy, now));
    }

    /// <summary>Screens nullable JSON collection shapes before descriptor construction; malformed candidates cannot hide other retained history.</summary>
    private static bool HasPreparedCandidateStructure(PreparedVersionManifest manifest) =>
        manifest.Source is not null && (manifest.Bars is null) != (manifest.Calendar is null) &&
        manifest.Bars is not { BarCoverage: null } && manifest.Files is { Count: > 0 } &&
        manifest.Files.All(file => file is not null) && manifest.Evidence?.Responses is { Count: > 0 } &&
        manifest.Evidence.Responses.All(response => response is not null && response.Manifest is not null);

    /// <summary>Reads parent coverage from its original manifest rather than a shortened admission descriptor.</summary>
    private static (DateTimeOffset Start, DateTimeOffset End) PreparedBounds(PreparedVersionManifest manifest) =>
        manifest.Bars is { } bars ? (bars.RequestedStartUtc, bars.RequestedEndUtc) :
            (CalendarUtc(manifest.Calendar!.CoverageStart), CalendarUtc(manifest.Calendar.CoverageEnd));

    /// <summary>Binds an admitted query to its original source identity and refuses bounds or counts outside that parent.</summary>
    private static PreparedMarketDataVersion AdmitPreparedSlice(PreparedMarketDataVersion parent, string requestHash,
        DateTimeOffset start, DateTimeOffset end, long rows)
    {
        if (start < parent.StartUtc || end > parent.EndUtc || end < start || rows < 0 || rows > parent.RowCount ||
            parent.IsCalendar && (start != CalendarUtc(DateOnly.FromDateTime(start.UtcDateTime)) ||
                end != CalendarUtc(DateOnly.FromDateTime(end.UtcDateTime)) || rows != (end - start).Days + 1L))
            throw new InvalidDataException("Admitted prepared slice is outside its immutable parent request.");
        return parent with { SourceRequestSha256 = parent.RequestSha256, RequestSha256 = requestHash,
            StartUtc = start, EndUtc = end, RowCount = rows };
    }

    /// <summary>Skips unreadable or invalid candidates only during new selection; pinned recovery still propagates the same failures.</summary>
    private static bool IsPreparedCacheMiss(Exception exception) => exception is IOException or InvalidDataException or
        UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or FormatException or OverflowException;
}
