using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Earnings;

namespace TradingFlow.Data.Earnings;

public sealed class SqliteEarningsRepository : IEarningsRepository
{
    private const int MatchWindowDays = 2;

    private readonly IDbContextFactory<TradingFlowDbContext> dbFactory;

    public SqliteEarningsRepository(IDbContextFactory<TradingFlowDbContext> dbFactory)
    {
        this.dbFactory = dbFactory;
    }

    public async Task ReplaceProviderWindowAsync(
        string provider,
        DateOnly fromExchangeDate,
        DateOnly toExchangeDate,
        IReadOnlyCollection<EarningsCalendarEvent> events,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        if (toExchangeDate < fromExchangeDate)
        {
            throw new ArgumentOutOfRangeException(nameof(toExchangeDate));
        }

        var normalizedProvider = provider.Trim().ToLowerInvariant();
        foreach (var item in events)
        {
            NormalizeUtc(item);
        }
        var incoming = events.ToDictionary(item => item.Id, StringComparer.Ordinal);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var existing = await db.EarningsCalendarEvents
            .Where(item => item.Provider == normalizedProvider &&
                item.ReportDateExchange >= fromExchangeDate &&
                item.ReportDateExchange <= toExchangeDate)
            .ToArrayAsync(cancellationToken);

        foreach (var stale in existing.Where(item => !incoming.ContainsKey(item.Id)))
        {
            db.EarningsCalendarEvents.Remove(stale);
        }

        foreach (var item in incoming.Values)
        {
            var target = existing.FirstOrDefault(candidate => candidate.Id == item.Id);
            if (target is null)
            {
                item.Provider = normalizedProvider;
                db.EarningsCalendarEvents.Add(item);
                continue;
            }

            CopyProviderState(item, target);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> TryApplyNewsResultAsync(
        string ticker,
        DateTimeOffset observedAtUtc,
        EarningsNewsResult result,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);
        ArgumentNullException.ThrowIfNull(result);
        if (!result.HasResult)
        {
            return false;
        }

        var normalizedTicker = ticker.Trim().ToUpperInvariant();
        var observedUtc = observedAtUtc.ToUniversalTime();
        // A company reports quarterly, so the nearest event inside this window is unambiguous. The
        // window is two-sided because the provider schedule is an estimate and a release routinely
        // lands ahead of it. Report dates carry the filter because SQLite cannot compare
        // DateTimeOffset in SQL; the nearest-match on the scheduled time is done in memory.
        var earliestDate = DateOnly.FromDateTime(observedUtc.AddDays(-MatchWindowDays).UtcDateTime);
        var latestDate = DateOnly.FromDateTime(observedUtc.AddDays(MatchWindowDays).UtcDateTime);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var candidates = await db.EarningsCalendarEvents
            .Where(item => item.Ticker == normalizedTicker &&
                item.ReportDateExchange >= earliestDate &&
                item.ReportDateExchange <= latestDate)
            .ToArrayAsync(cancellationToken);
        var target = candidates
            .OrderBy(item => (item.ScheduledAtUtc - observedUtc).Duration())
            .FirstOrDefault();
        if (target is null)
        {
            return false;
        }

        // Estimate, actual, and surprise are written as one set. Pairing this headline's actual
        // with the provider's estimate would invent a surprise neither source reports, and the two
        // consensus figures do disagree: a headline reading "EPS $0.11 Misses $0.12 Estimate"
        // against a provider estimate of $0.11 would otherwise be recorded as having met.
        var applied = false;
        if (target.EpsActual is null && result.EpsActual.HasValue)
        {
            target.EpsEstimate = result.EpsEstimate ?? target.EpsEstimate;
            target.EpsActual = result.EpsActual;
            target.EpsSurprisePercent = result.EpsSurprisePercent;
            applied = true;
        }

        if (target.RevenueActualMillions is null && result.RevenueActualMillions.HasValue)
        {
            target.RevenueEstimateMillions = result.RevenueEstimateMillions ?? target.RevenueEstimateMillions;
            target.RevenueActualMillions = result.RevenueActualMillions;
            target.RevenueSurprisePercent = result.RevenueSurprisePercent;
            applied = true;
        }

        if (!applied)
        {
            return false;
        }

        target.ResultFirstSeenAtUtc ??= observedUtc;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<EarningsCalendarEvent>> GetCalendarAsync(
        DateOnly fromExchangeDate,
        DateOnly toExchangeDate,
        IReadOnlyCollection<string>? tickers,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var results = await db.EarningsCalendarEvents
            .AsNoTracking()
            .Where(item => item.ReportDateExchange >= fromExchangeDate && item.ReportDateExchange <= toExchangeDate)
            .ToArrayAsync(cancellationToken);

        if (tickers is not null)
        {
            var normalized = tickers
                .Select(ticker => ticker.Trim().ToUpperInvariant())
                .Where(ticker => ticker.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (normalized.Count == 0)
            {
                return Array.Empty<EarningsCalendarEvent>();
            }

            results = results
                .Where(item => normalized.Contains(item.Ticker))
                .ToArray();
        }

        return results
            .OrderBy(item => item.ScheduledAtUtc)
            .ThenBy(item => item.Ticker, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task UpsertAnalysisAsync(EarningsAnalysisSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Id = snapshot.Id == Guid.Empty ? Guid.NewGuid() : snapshot.Id;
        snapshot.Ticker = snapshot.Ticker.Trim().ToUpperInvariant();
        snapshot.AnalyzedAtUtc = snapshot.AnalyzedAtUtc.ToUniversalTime();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.EarningsAnalysisSnapshots.Add(snapshot);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, EarningsAnalysisSnapshot>> GetLatestAnalysesAsync(
        IReadOnlyCollection<string> eventIds,
        CancellationToken cancellationToken)
    {
        if (eventIds.Count == 0)
        {
            return new Dictionary<string, EarningsAnalysisSnapshot>(StringComparer.Ordinal);
        }

        var ids = eventIds.Distinct(StringComparer.Ordinal).ToArray();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var snapshots = new List<EarningsAnalysisSnapshot>();
        foreach (var idBatch in ids.Chunk(100))
        {
            snapshots.AddRange(await db.EarningsAnalysisSnapshots
                .AsNoTracking()
                .Where(snapshot => idBatch.Contains(snapshot.EarningsEventId))
                .ToArrayAsync(cancellationToken));
        }

        return snapshots
            .GroupBy(snapshot => snapshot.EarningsEventId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(snapshot => snapshot.AnalyzedAtUtc).First(),
                StringComparer.Ordinal);
    }

    private static void CopyProviderState(EarningsCalendarEvent source, EarningsCalendarEvent target)
    {
        target.Ticker = source.Ticker;
        target.CompanyName = source.CompanyName;
        target.ReportDateExchange = source.ReportDateExchange;
        target.ScheduledAtUtc = source.ScheduledAtUtc;
        target.ReleaseWindow = source.ReleaseWindow;
        target.IsScheduleEstimate = source.IsScheduleEstimate;
        target.MarketCapMillions = source.MarketCapMillions;
        // Estimate/actual/surprise move as one set so a provider estimate is never paired with a
        // headline actual. The provider wins whenever it has an actual of its own; until then a
        // headline-derived result stands, because the provider publishes no after-close actual for
        // roughly two hours after the release and overwriting would erase it on the next refresh.
        if (source.EpsActual is not null || target.EpsActual is null)
        {
            target.EpsEstimate = source.EpsEstimate;
            target.EpsActual = source.EpsActual;
            target.EpsSurprisePercent = source.EpsSurprisePercent;
        }

        if (source.RevenueActualMillions is not null || target.RevenueActualMillions is null)
        {
            target.RevenueEstimateMillions = source.RevenueEstimateMillions;
            target.RevenueActualMillions = source.RevenueActualMillions;
            target.RevenueSurprisePercent = source.RevenueSurprisePercent;
        }

        // Provider-only mirror fields; the headline path never writes these, so preserving a
        // non-null value simply guards against the provider dropping one it already reported.
        target.ReportedEpsEstimate = source.ReportedEpsEstimate;
        target.ReportedEpsActual = source.ReportedEpsActual ?? target.ReportedEpsActual;
        target.ReportedEpsSurprisePercent = source.ReportedEpsSurprisePercent ?? target.ReportedEpsSurprisePercent;
        target.OneDayPriceReactionPercent = source.OneDayPriceReactionPercent ?? target.OneDayPriceReactionPercent;
        target.SourceUrl = source.SourceUrl;
        target.SourceArtifactSha256 = source.SourceArtifactSha256;
        target.ProviderReceivedAtUtc = source.ProviderReceivedAtUtc;
        target.ResultFirstSeenAtUtc ??= source.ResultFirstSeenAtUtc;
        target.LastSeenAtUtc = source.LastSeenAtUtc;
    }

    private static void NormalizeUtc(EarningsCalendarEvent item)
    {
        item.ScheduledAtUtc = item.ScheduledAtUtc.ToUniversalTime();
        item.ProviderReceivedAtUtc = item.ProviderReceivedAtUtc.ToUniversalTime();
        item.ResultFirstSeenAtUtc = item.ResultFirstSeenAtUtc?.ToUniversalTime();
        item.LastSeenAtUtc = item.LastSeenAtUtc.ToUniversalTime();
    }
}
