using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Earnings;

namespace TradingFlow.Data.Earnings;

public sealed class SqliteEarningsRepository : IEarningsRepository
{
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
        foreach (var idBatch in ids.Chunk(500))
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
        target.EpsEstimate = source.EpsEstimate;
        target.EpsActual = source.EpsActual;
        target.EpsSurprisePercent = source.EpsSurprisePercent;
        target.ReportedEpsEstimate = source.ReportedEpsEstimate;
        target.ReportedEpsActual = source.ReportedEpsActual;
        target.ReportedEpsSurprisePercent = source.ReportedEpsSurprisePercent;
        target.RevenueEstimateMillions = source.RevenueEstimateMillions;
        target.RevenueActualMillions = source.RevenueActualMillions;
        target.RevenueSurprisePercent = source.RevenueSurprisePercent;
        target.OneDayPriceReactionPercent = source.OneDayPriceReactionPercent;
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
