using TradingFlow.Domain.Earnings;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.News;
using TradingFlow.Data.News;
using TradingFlow.Engine.Pipeline;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Web.Services;

public sealed record DeskUniverseEvidence(string? Sector = null, decimal? MarketCapMillions = null,
    decimal? Volume = null, decimal? DailyRvol = null, decimal? AtrPercent = null, decimal? GapPercent = null,
    decimal? VwapDistancePercent = null, decimal? Rsi = null, int? EarningsInDays = null,
    bool? HasCatalyst = null, string Detail = "Market evidence has not been read.", string? EarningsDetail = null);

/// <summary>Observational display only. Reads existing warm state; never subscribes or admits a trade.</summary>
public sealed class DeskUniverseEvidenceService(IDailyObservationService daily,
    StreamingMarketStateProcessor stream, IEarningsRepository earnings, SqliteNewsFeedRepository news, TimeProvider clock,
    ILogger<DeskUniverseEvidenceService> logger)
{
    private static readonly TimeZoneInfo Exchange = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    public async Task<IReadOnlyDictionary<string, DeskUniverseEvidence>> ReadAsync(
        IReadOnlyList<RankedDeskRow> rows, ScreenerSyncResult? screener, CancellationToken token)
    {
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Exchange).DateTime);
        var symbols = rows.Select(x => x.Ticker).ToArray();
        IReadOnlyList<EarningsCalendarEvent> events = [];
        IReadOnlyList<PersistedNewsItem> recentNews = [];
        try { if (symbols.Length > 0) events = await earnings.GetCalendarAsync(today, today.AddDays(35), symbols, token); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex,"Desk earnings metadata read unavailable."); }
        try { if (symbols.Length > 0) recentNews = await news.GetRecentForTickersAsync(now.AddHours(-4),1000,symbols,token); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex,"Desk observed news catalyst read unavailable."); }
        var catalysts = recentNews.Where(x => x.Timestamp <= now && x.IngestedAt != default && x.IngestedAt <= now)
            .GroupBy(x => x.Ticker,StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key,x => x.MaxBy(n => n.Timestamp)!,StringComparer.OrdinalIgnoreCase);
        var calendar = events.Where(x => x.ProviderReceivedAtUtc <= now && x.ScheduledAtUtc >= now)
            .GroupBy(x => x.Ticker, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.OrderBy(e => e.ScheduledAtUtc).ThenByDescending(e => e.ProviderReceivedAtUtc).First(), StringComparer.OrdinalIgnoreCase);
        var vendor = (screener?.AllSymbolDetails ?? []).ToDictionary(x => x.Symbol, StringComparer.OrdinalIgnoreCase);
        // Checking membership first avoids creating cold symbol pipelines via the snapshot API.
        var tracked = stream.GetTrackedSymbols().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, DeskUniverseEvidence>(StringComparer.OrdinalIgnoreCase);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(2));
        foreach (var row in rows)
        {
            var observation = await daily.GetAsync(row.Ticker, token);
            IndicatorSnapshot? intraday = null;
            if (tracked.Contains(row.Ticker) && !budget.IsCancellationRequested)
            {
                TickerMarketState? state = null;
                using var read = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                read.CancelAfter(TimeSpan.FromMilliseconds(100));
                try { state = await stream.GetTickerStateAsync(row.Ticker, ["1m"], 1, now, read.Token).WaitAsync(read.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogDebug(ex,"Warm desk snapshot unavailable for {Ticker}.",row.Ticker); }
                token.ThrowIfCancellationRequested();
                intraday = state?.SnapshotsByTimeframe.GetValueOrDefault("1m")?.LastOrDefault();
                if (intraday is not null && (intraday.DataFeed != "sip" || intraday.AdjustmentPolicy != "all" ||
                    intraday.Timestamp.AddMinutes(1) > now || DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(intraday.Timestamp, Exchange).DateTime) != today)) intraday = null;
            }
            vendor.TryGetValue(row.Ticker, out var metadata);
            calendar.TryGetValue(row.Ticker, out var upcoming);
            result[row.Ticker] = Project(observation, intraday,
                metadata, upcoming, today, screener?.ProviderTimestampUtc);
            if (catalysts.TryGetValue(row.Ticker,out var catalyst)) result[row.Ticker] = result[row.Ticker] with {
                HasCatalyst=true, Detail=result[row.Ticker].Detail + $" Observed news catalyst: {catalyst.Provider}/{catalyst.Source}; published {catalyst.Timestamp:O}, ingested {catalyst.IngestedAt:O}; last 4h, up to 1,000 matched symbol records. {catalyst.Headline}" };
        }
        return result;
    }

    public static DeskUniverseEvidence Project(DailyObservationResult daily, IndicatorSnapshot? intraday,
        ScreenerSymbolResult? vendor, EarningsCalendarEvent? upcoming,
        DateOnly today, DateTimeOffset? vendorReceivedAtUtc)
    {
        var snapshot = daily.Snapshot;
        var dailyTrusted = snapshot is { Timeframe:"1d", DataFeed:"sip", AdjustmentPolicy:"all" } && daily.State is "ready" or "insufficient";
        if (!dailyTrusted) snapshot = null;
        var vwap = intraday?.Timeframe == "1m" && intraday.DataFeed == "sip" && intraday.AdjustmentPolicy == "all" ? intraday.Vwap : null;
        var detail = snapshot is null ? daily.Detail : $"Completed 1d SIP/all at {snapshot.Timestamp:O}; read {daily.ObservedAtUtc:O}; {daily.State}. Daily RVOL cohort {snapshot.RelativeVolumeCohort ?? "unavailable"}, samples {snapshot.RelativeVolumeSampleCount}/{snapshot.RelativeVolumeMinimumSamples}. {daily.Detail}";
        detail += intraday is null ? " Session VWAP unavailable: no current completed warm 1m SIP/all snapshot." : $" Session VWAP from completed 1m SIP/all at {intraday.Timestamp:O}.";
        detail += vendor?.Sector is null ? " Sector unavailable: no Finviz sector value for this universe." : $" Sector: Finviz discovery export received {vendorReceivedAtUtc:O}.";
        detail += vendor?.MarketCapMillions is not null ? $" Cap: Finviz export received {vendorReceivedAtUtc:O}, USD millions." : upcoming?.MarketCapMillions is not null ? $" Cap: stored {upcoming.Provider} earnings calendar received {upcoming.ProviderReceivedAtUtc:O}, USD millions." : " Cap unavailable: neither Finviz nor stored earnings provides a value.";
        detail += " Vendor RVOL is separate discovery metadata; it is never substituted for daily SIP RVOL.";
        var earningsDetail = upcoming is null ? "No upcoming record in the stored 35-day calendar; coverage is unknown." : $"{upcoming.Provider}; received {upcoming.ProviderReceivedAtUtc:O}; {(upcoming.IsScheduleEstimate ? "estimated" : "observed")} schedule {upcoming.ScheduledAtUtc:O}.";
        return new(vendor?.Sector, vendor?.MarketCapMillions ?? upcoming?.MarketCapMillions,
            snapshot?.CurrentVolume, snapshot?.RelativeVolume,
            snapshot?.CurrentPrice > 0 ? snapshot.Atr / snapshot.CurrentPrice * 100m : null,
            dailyTrusted ? daily.CompletedSessionGapPercent : null,
            vwap > 0 && intraday?.CurrentPrice > 0 ? (intraday.CurrentPrice / vwap - 1m) * 100m : null, snapshot?.Rsi,
            upcoming is null ? null : upcoming.ReportDateExchange.DayNumber - today.DayNumber,
            snapshot?.Catalyst is null && intraday?.Catalyst is null ? null : true, detail, earningsDetail);
    }
}
