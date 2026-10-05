using System.Text.Json;
using TradingFlow.Domain.Earnings;
using TradingFlow.Domain.Market;
using TradingFlow.Web.Services;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Tests;

public sealed class DeskUniverseEvidenceTests
{
    private static IndicatorSnapshot Snapshot(string timeframe = "1d") => JsonSerializer.Deserialize<IndicatorSnapshot>("""
    {"Ticker":"AAA","Timestamp":"2026-10-05T14:30:00Z","Timeframe":"1d","CurrentPrice":100,"CurrentVolume":1500000,
     "Vwap":95,"Rsi":55,"Atr":2,"RelativeVolume":1.5,"RelativeVolumeSampleCount":20,"RelativeVolumeMinimumSamples":20,
     "RelativeVolumeCohort":"completed-daily","DataFeed":"sip","AdjustmentPolicy":"all"}
    """)! with {Timeframe=timeframe};
    [Fact]
    public void DailyEvidenceNeverBecomesSessionVwapAndVendorRvolNeverBecomesMarketRvol()
    {
        var daily = new DailyObservationResult("ready","Completed daily evidence",Snapshot(),ObservedAtUtc:DateTimeOffset.Parse("2026-10-05T14:32:00Z"),CompletedSessionGapPercent:-2);
        var row=DeskUniverseEvidenceService.Project(daily,null,new("AAA",99,"Alpha","Technology",2000),null,new(2026,10,5),DateTimeOffset.Parse("2026-10-05T12:00:00Z"));
        Assert.Equal(1500000,row.Volume); Assert.Equal(1.5m,row.DailyRvol); Assert.Equal(2,row.AtrPercent); Assert.Equal(-2,row.GapPercent);
        Assert.Null(row.VwapDistancePercent); Assert.Null(row.EarningsInDays); Assert.Null(row.HasCatalyst);
        Assert.Contains("samples 20/20",row.Detail); Assert.Contains("coverage is unknown",row.EarningsDetail);
        var warm=DeskUniverseEvidenceService.Project(daily,Snapshot("1m") with {CurrentPrice=110,Vwap=100},null,null,new(2026,10,5),null);
        Assert.Equal(10,warm.VwapDistancePercent);
        var untrusted=DeskUniverseEvidenceService.Project(daily with {Snapshot=Snapshot() with {DataFeed="iex"}},null,null,null,new(2026,10,5),null);
        Assert.Null(untrusted.Volume); Assert.Null(untrusted.DailyRvol); Assert.Null(untrusted.GapPercent);
    }
    [Fact]
    public void EarningsCapFallbackNamesTheActualProviderAndUnknownFieldsDoNotPassBounds()
    {
        var upcoming=new EarningsCalendarEvent {Ticker="AAA",ReportDateExchange=new(2026,10,7),Provider="calendar-provider",ProviderReceivedAtUtc=DateTimeOffset.Parse("2026-10-05T12:00:00Z"),MarketCapMillions=2500};
        var evidence=DeskUniverseEvidenceService.Project(new("loading","Loading"),null,null,upcoming,new(2026,10,5),null);
        Assert.Equal(2500,evidence.MarketCapMillions); Assert.Equal(2,evidence.EarningsInDays);
        Assert.Contains("stored calendar-provider earnings calendar",evidence.Detail);
        var query=new DeskGridQuery(null,null,null,null,null,null,null,null,null,MinMarketCap:2000,MaxEarningsDays:3);
        Assert.True(DeskGridFilter.Matches(new("AAA","Alpha",null,null,null,null,false,evidence),query));
        Assert.False(DeskGridFilter.Matches(new("AAA","Alpha",null,null,null,null,false),query));
        Assert.False(DeskGridFilter.Matches(new("AAA","Alpha",null,null,null,null,false,evidence),query with {MinRvol=1}));
        Assert.False(DeskGridFilter.Matches(new("AAA","Alpha",null,null,null,null,false,evidence),query with {CatalystOnly=true}));
    }
    [Fact]
    public void ExtendedFiltersValidateRangesAndSelectFullFiveHundredSymbolUniverseBeforePaging()
    {
        var query=new DeskGridQuery(null,null,null,null,null,null,null,null,null,Sector:"Technology",MinRvol:2,MinVolume:100000,MinRsi:40,MaxRsi:60);
        var rows=Enumerable.Range(0,550).Select(i=>new DeskGridValues($"T{i:D3}","Fixture",100,null,null,null,false,
            new(Sector:i%2==0?"Technology":"Energy",Volume:200000,DailyRvol:i>=400?3:1,Rsi:50)));
        var page=DeskGridPage<DeskGridValues>.Create(rows.Where(x=>DeskGridFilter.Matches(x,query)).OrderBy(x=>x.Ticker),2,25);
        Assert.Equal(75,page.TotalMatching); Assert.Equal("T450",page.Rows[0].Ticker);
        Assert.Throws<InvalidOperationException>(()=>DeskGridFilter.Validate(query with {MaxRsi=101}));
        Assert.Throws<InvalidOperationException>(()=>DeskGridFilter.Validate(query with {MinGap=3,MaxGap=2}));
    }
}
