using Microsoft.EntityFrameworkCore;
using TradingFlow.Application.Preparation;
using TradingFlow.Contracts.Preparation;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Earnings;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.News;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;
namespace TradingFlow.Web.Services;

public sealed class PreparationAuthorization(IDbContextFactory<TradingFlowDbContext> factory,TimeProvider clock) : IPreparationAuthorization
{
    public async Task<string?> StampAsync(string ownerId,CancellationToken token)
    {
        if(!Guid.TryParse(ownerId,out var id)) return null;
        await using var db=await factory.CreateDbContextAsync(token);
        var user=await db.Users.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,token);
        return user is null || user.LockoutEnd>clock.GetUtcNow()?null:user.SecurityStamp;
    }
}
public sealed class PreparationCalendarAdapter(Lazy<IMarketDataProvider> provider,TimeProvider clock) : IPreparationCalendar
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(DateOnly Start,DateOnly End),PreparationCalendar> cache=new();
    public static readonly TimeZoneInfo Exchange=TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    public static DateTimeOffset Utc(DateOnly date,TimeOnly time) => new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(time,DateTimeKind.Unspecified),Exchange));
    public async Task<PreparationCalendar> LoadAsync(DateOnly start,DateOnly end,CancellationToken token)
    {
        if(cache.TryGetValue((start,end),out var cached) && clock.GetUtcNow()-cached.RetrievedAtUtc<TimeSpan.FromHours(1)) return cached;
        foreach(var entry in cache.Where(x=>clock.GetUtcNow()-x.Value.RetrievedAtUtc>=TimeSpan.FromHours(1)).ToArray()) cache.TryRemove(entry.Key,out _);
        if(provider.Value is not IMarketSessionScheduleProvider schedule) throw new InvalidOperationException("Provider has no authoritative exchange calendar.");
        var sessions=await schedule.LoadMarketSessionSchedulesAsync(start,end,token);
        if(sessions.Count!=end.DayNumber-start.DayNumber+1 || Enumerable.Range(0,end.DayNumber-start.DayNumber+1).Any(i=>!sessions.ContainsKey(start.AddDays(i))))
            throw new InvalidOperationException("Provider returned incomplete exchange calendar coverage.");
        var captured=new PreparationCalendar(clock.GetUtcNow(),"alpaca-calendar",sessions.Values.OrderBy(x=>x.TradeDate).Select(x=>new PreparationSession(x.TradeDate,x.IsTradingDay,
            Utc(x.TradeDate,x.RegularOpen),Utc(x.TradeDate,x.RegularClose))).ToArray());
        if(cache.Count<64)cache[(start,end)]=captured;return captured;
    }
}
public sealed class PreparationEvidenceAdapter(Lazy<IMarketDataProvider> provider,IPreparationCalendar calendar,
    INewsFeedRepository news,IEarningsRepository earnings) : IPreparationEvidence
{
    public async Task<PreparationSymbol> CollectAsync(string symbol,PreparationSlot slot,PreparationSettings settings,DateTimeOffset cutoff,CancellationToken token)
    {
        var reasons=new List<string>(); var context=new List<string>(); var bars=new List<OhlcvBar>();
        var intraday=new List<OhlcvBar>(); PreparationCalendar? capturedCalendar=null;
        PreparationNews[] articles=[]; decimal? close=null,shares=null,dollars=null,trend=null,range=null,gap=null,rvol=null;
        string feed="unavailable",adjustment="unavailable"; DateOnly? dailyDate=null;
        try
        {
            var start=slot.PreviousSession.Date.AddDays(-settings.HistorySessions*2-20);
            var schedules=await calendar.LoadAsync(start,slot.TargetDate,token); capturedCalendar=schedules;
            var expected=schedules.Sessions.Where(x=>x.TradingDay && x.Date<=slot.PreviousSession.Date).TakeLast(settings.HistorySessions).ToArray();
            await foreach(var bar in provider.Value.GetBarsAsync([symbol],["1d"],PreparationCalendarAdapter.Utc(start,TimeOnly.MinValue),cutoff,token))
            {
                var date=DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(bar.Timestamp,PreparationCalendarAdapter.Exchange).DateTime);
                var session=expected.FirstOrDefault(x=>x.Date==date);
                if(session is not null && session.CloseUtc<=cutoff && bar.CoverageVerifiedThroughUtc>=session.CloseUtc && (bar.KnownAtUtc is null || bar.KnownAtUtc<=cutoff)) bars.Add(bar);
            }
            bars=bars.OrderBy(x=>x.Timestamp).ToList();
            if(bars.Count!=expected.Length || expected.Length!=settings.HistorySessions || bars.Select(x=>x.Timestamp).Distinct().Count()!=bars.Count)
                reasons.Add("Completed daily history coverage is incomplete.");
            var last=bars.LastOrDefault();
            if(last is not null)
            {
                dailyDate=DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(last.Timestamp,PreparationCalendarAdapter.Exchange).DateTime);
                close=last.Close; feed=String.Join(',',bars.Select(x=>x.DataFeed).Distinct()); adjustment=String.Join(',',bars.Select(x=>x.AdjustmentPolicy).Distinct());
                if(dailyDate!=slot.PreviousSession.Date || close<=0 || bars.Any(x=>x.High<x.Low || x.Volume<0)) reasons.Add("Last daily bar is missing or invalid.");
                if(feed!="sip" || adjustment!="all") reasons.Add("Daily evidence requires consistent SIP feed and all-adjusted provenance.");
                shares=bars.TakeLast(20).Average(x=>x.Volume); dollars=bars.TakeLast(20).Average(x=>x.Close*x.Volume);
                range=close>0?100*(last.High-last.Low)/close:null;
                if(settings.Filters.TrendAverageDays is {} days && bars.Count>=days) trend=bars.TakeLast(days).Average(x=>x.Close);
            }
            else reasons.Add("Final daily bar is unavailable.");
            if(slot.Phase=="morning")
            {
                try
                {
                    var intradayStart=settings.RelativeVolumeAnnotation is null?slot.TargetDate:slot.TargetDate.AddDays(-60);
                    await foreach(var bar in provider.Value.GetBarsAsync([symbol],["5m"],PreparationCalendarAdapter.Utc(intradayStart,new TimeOnly(4,0)),cutoff,token))
                        if(bar.Timestamp.AddMinutes(5)<=cutoff && bar.CoverageVerifiedThroughUtc>=bar.Timestamp.AddMinutes(5) && (bar.KnownAtUtc is null || bar.KnownAtUtc<=cutoff)) intraday.Add(bar);
                    var latest=intraday.OrderBy(x=>x.Timestamp).LastOrDefault();
                    if(latest is not null && latest.Timestamp>=PreparationCalendarAdapter.Utc(slot.TargetDate,new TimeOnly(4,0)) && cutoff-latest.Timestamp<=TimeSpan.FromMinutes(10) && close>0)
                    { gap=100*(latest.Close-close)/close; context.Add($"Gap reference: {dailyDate} close {close}; completed price at {latest.Timestamp:O}."); }
                    else context.Add("Gap incomplete: no fresh completed premarket price.");
                    if(settings.RelativeVolumeAnnotation is not null)
                    {
                        var profile=MarketEvidenceProfile.ProductionDefault.WithSessionSchedules(schedules.Sessions.ToDictionary(x=>x.Date,x=>new MarketSessionSchedule(x.Date,x.TradingDay,
                            TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(x.OpenUtc,PreparationCalendarAdapter.Exchange).DateTime),TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(x.CloseUtc,PreparationCalendarAdapter.Exchange).DateTime))));
                        var computed=new IndicatorEngine(profile).Compute(intraday).LastOrDefault();
                        if(computed?.RelativeVolumeCohort=="premarket" && computed.RelativeVolumeSampleCount>=profile.MinimumValidSamples && gap is not null) rvol=computed.RelativeVolume;
                        context.Add($"Same-time RVOL: {profile.Version}; {computed?.RelativeVolumeSampleCount ?? 0} valid baseline samples; {(rvol is null?"incomplete":"available")}.");
                    }
                    else context.Add("Same-time RVOL collection is disabled; configure its annotation threshold to collect the baseline.");
                }
                catch(Exception ex) when(ex is not OperationCanceledException) { context.Add("Premarket context unavailable: "+ex.Message); }
            }
            try
            {
                var items=await news.GetRecentAsync(cutoff.AddDays(-4),201,symbol,token);
                articles=items.Where(x=>x.IngestedAt<=cutoff && x.Timestamp<=cutoff).Take(200)
                    .Select(x=>new PreparationNews(x.Id,x.Headline,x.Url,x.Provider,x.Timestamp,x.IngestedAt)).ToArray();
                context.Add(items.Count>200?"News incomplete: 200 article capture limit reached.":articles.Length==0?"News unavailable: no known articles in the local four-day window.":"News captured from the local feed; provider coverage is not guaranteed.");
                var events=await earnings.GetCalendarAsync(slot.TargetDate,slot.TargetDate.AddDays(settings.EarningsWithinDays ?? 7),[symbol],token);
                foreach(var item in events.Where(x=>x.ProviderReceivedAtUtc<=cutoff && x.LastSeenAtUtc<=cutoff))
                    context.Add($"Earnings {item.ReportDateExchange}: {item.ReleaseWindow}; {item.Provider}; observed {item.ProviderReceivedAtUtc:O}; source {item.SourceArtifactSha256}.");
                if(events.Count==0) context.Add("Earnings context unavailable in the local calendar window.");
            }
            catch(Exception ex) when(ex is not OperationCanceledException) { context.Add("News/earnings context unavailable: "+ex.Message); }
        }
        catch(Exception ex) when(ex is not OperationCanceledException) { reasons.Add("Market evidence incomplete: "+ex.Message); }
        var incomplete=reasons.Count>0;
        if(!incomplete)
        {
            if(settings.Filters.MinimumPrice is {} price && close<price) reasons.Add("Below configured minimum price.");
            if(settings.Filters.MinimumAverageShares is {} volume && shares<volume) reasons.Add("Below configured 20-session average share volume.");
            if(settings.Filters.MinimumAverageDollarVolume is {} value && dollars<value) reasons.Add("Below configured 20-session average close-times-volume dollars.");
            if(settings.Filters.TrendAverageDays is not null && (trend is null || close<trend)) reasons.Add("Below configured simple moving average.");
            if(settings.Filters.MaximumRangePercent is {} maximum && range>maximum) reasons.Add("Daily high-low range divided by close exceeds configured percent.");
        }
        if(settings.GapAnnotationPercent is {} gapLimit && Math.Abs(gap ?? 0)>=gapLimit) context.Add("Configured absolute gap annotation threshold reached.");
        if(settings.RelativeVolumeAnnotation is {} rvolLimit && rvol>=rvolLimit) context.Add("Configured same-time relative volume annotation threshold reached.");
        var inputs=PreparationJson.Write(new { dailyBars=bars,intradayBars=intraday,calendar=capturedCalendar,profile=MarketEvidenceProfile.ProductionDefault.Version,settings.Filters,cutoff,close,shares,dollars,trend,range,gap,rvol,articles,context });
        return new(symbol,"",incomplete?"incomplete":reasons.Count>0?"excluded":"complete",reasons.ToArray(),dailyDate,bars.Count,close,shares,dollars,trend,range,gap,rvol,feed,adjustment,
            PreparationJson.Hash(inputs),articles,context.ToArray(),inputs);
    }
}
public sealed class PreparationSchedulerHostedService(TradingFlow.Application.Preparation.PreparationService service,ILogger<PreparationSchedulerHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try { await service.TickAsync(stoppingToken); }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { return; }
            catch(Exception exception) { logger.LogError(exception,"Preparation scheduling failed closed; no guessed session will be used."); }
        } while(await timer.WaitForNextTickAsync(stoppingToken));
    }
}
