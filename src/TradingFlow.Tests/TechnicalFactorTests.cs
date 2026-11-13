using System.Text.Json;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Strategies;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class TechnicalFactorTests
{
    private static readonly TimeZoneInfo Ny = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static OhlcvBar[] Bars(int count = 330, string symbol = "X")
    {
        var result = new List<OhlcvBar>(); var day = new DateOnly(2025, 1, 6);
        while (result.Count < count)
        {
            if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
                var close = 100m + (result.Count % 7) - (result.Count % 3);
                result.Add(new(symbol, new DateTimeOffset(local, Ny.GetUtcOffset(local)).ToUniversalTime(), "1d",
                    close, close + 2, close - 2, close, 10000, "sip", "all"));
            }
            day = day.AddDays(1);
        }
        return result.ToArray();
    }
    private static IndicatorSnapshot[] Snapshots(OhlcvBar[] bars) => new IndicatorEngine().Compute(bars).ToArray();
    private static TechnicalFactorEvidence Factor(TechnicalFactorRules rules, OhlcvBar[] bars,
        IndicatorSnapshot[] snapshots, int index) => TechnicalFactorCalculator.Analyze(rules, bars, snapshots,
            index, null, bars[index].Timestamp.AddDays(1));

    [Fact]
    public void AtrDistance_IsSignedAndScaledByPositiveAtr_AndRocUsesPercentageUnits()
    {
        var bars = Bars(); var snapshots = Snapshots(bars); var i = 300;
        bars[i] = bars[i] with { Close = 90 }; bars[i - 3] = bars[i - 3] with { Close = 100 };
        snapshots[i] = snapshots[i] with { Sma20 = 100, Atr = 5 };
        Assert.Equal(-2, Factor(new(TechnicalFactorRules.AtrDistance,20,MaxValue:-1),bars,snapshots,i).Value);
        Assert.Equal(-10, Factor(new(TechnicalFactorRules.Roc,3,MaxValue:-5),bars,snapshots,i).Value);
        snapshots[i] = snapshots[i] with { Atr = 0 };
        Assert.Null(Factor(new(TechnicalFactorRules.AtrDistance,20,MaxValue:-1),bars,snapshots,i).Value);
    }

    [Fact]
    public void Ibs_FlatRangeIsUnavailable_AndCurrentCloseLocationIsExact()
    {
        var bars=Bars();var snapshots=Snapshots(bars);var i=300;
        bars[i]=bars[i] with { Low=100,High=110,Close=102 };
        Assert.Equal(.2m,Factor(new(TechnicalFactorRules.Ibs,1,MaxValue:.2m),bars,snapshots,i).Value);
        bars[i]=bars[i] with { Low=100,High=100,Close=100 };
        var result=Factor(new(TechnicalFactorRules.Ibs,1,MaxValue:.2m),bars,snapshots,i);
        Assert.Null(result.Value);Assert.Equal("zero_range_bar",result.UnavailableReason);
    }

    [Fact]
    public void Squeeze_RanksPriorWidthAgainstEarlierWidths_TiesAndCurrentExpansionDoNotChangeIt()
    {
        var bars=Bars();var snapshots=Snapshots(bars);var i=300;
        for(var j=i-101;j<=i;j++)snapshots[j]=snapshots[j] with { BollingerMiddle=100,BollingerUpper=101,BollingerLower=99 };
        bars[i]=bars[i] with {Close=102};var rules=new TechnicalFactorRules(TechnicalFactorRules.Squeeze,100,MinValue:0,MaxContextValue:20);
        var first=Factor(rules,bars,snapshots,i);Assert.Equal(0,first.ContextValue);Assert.True(first.Value>0);
        snapshots[i]=snapshots[i] with {BollingerUpper=10000,BollingerLower=-10000};
        Assert.Equal(first,Factor(rules,bars,snapshots,i));
        snapshots[i-1]=snapshots[i-1] with {BollingerUpper=102,BollingerLower=98};
        Assert.Equal(100,Factor(rules,bars,snapshots,i).ContextValue);
    }

    [Theory]
    [InlineData(TechnicalFactorRules.AtrDistance,20,-1)]
    [InlineData(TechnicalFactorRules.Roc,3,-5)]
    [InlineData(TechnicalFactorRules.Ibs,1,0.2)]
    [InlineData(TechnicalFactorRules.Connors,100,10)]
    [InlineData(TechnicalFactorRules.Squeeze,100,0)]
    public void FactorAtCompletedPrefix_IsUnaffectedByFutureBars(string metric,int period,double threshold)
    {
        var bars=Bars(400);var i=300;
        var rules=metric==TechnicalFactorRules.Squeeze?new TechnicalFactorRules(metric,period,MinValue:0,MaxContextValue:20):new(metric,period,MaxValue:(decimal)threshold);
        var before=Factor(rules,bars,Snapshots(bars),i);
        var changed=bars.Select((bar,j)=>j>i?bar with {Close=10000+j,High=20000+j,Low=9000,Open=12000,Volume=9999999}:bar).ToArray();
        Assert.Equal(before,Factor(rules,changed,Snapshots(changed),i));
    }

    [Fact]
    public void Connors_UsesThreeComponentsAndRequiresConvergenceHistory()
    {
        var bars=Bars();var result=Factor(new(TechnicalFactorRules.Connors,100,MaxValue:10),bars,Snapshots(bars),300);
        Assert.NotNull(result.Value);Assert.NotNull(result.PriceRsi);Assert.NotNull(result.StreakRsi);Assert.NotNull(result.PercentRank);
        Assert.InRange(Math.Abs(result.Value!.Value-(result.PriceRsi!.Value+result.StreakRsi!.Value+result.PercentRank!.Value)/3),0,.000000001m);
        Assert.Null(Factor(new(TechnicalFactorRules.Connors,100,MaxValue:10),bars,Snapshots(bars),200).Value);
        var flat=bars.Select(bar=>bar with {Open=100,High=102,Low=98,Close=100}).ToArray();
        var flatResult=Factor(new(TechnicalFactorRules.Connors,100,MaxValue:10),flat,Snapshots(flat),300);
        Assert.Equal(100,flatResult.PriceRsi);Assert.Equal(100,flatResult.StreakRsi);Assert.Equal(0,flatResult.PercentRank);
        Assert.InRange(Math.Abs(flatResult.Value!.Value-200m/3),0,.000000001m);
        double Rsi(double[] closes,int period)
        {
            var gain=0d;var loss=0d;
            for(var j=1;j<=period;j++){gain+=Math.Max(0,closes[j]-closes[j-1]);loss+=Math.Max(0,closes[j-1]-closes[j]);}
            gain/=period;loss/=period;
            for(var j=period+1;j<closes.Length;j++){gain=(gain*(period-1)+Math.Max(0,closes[j]-closes[j-1]))/period;loss=(loss*(period-1)+Math.Max(0,closes[j-1]-closes[j]))/period;}
            return loss<=0?100:100-100/(1+gain/loss);
        }
        var price=bars.Take(301).Select(bar=>(double)bar.Close).ToArray();var streak=new double[price.Length];
        for(var j=1;j<price.Length;j++)streak[j]=price[j]==price[j-1]?0:price[j]>price[j-1]?(streak[j-1]>0?streak[j-1]+1:1):(streak[j-1]<0?streak[j-1]-1:-1);
        var changes=price.Select((value,j)=>j==0?0:(value-price[j-1])/price[j-1]).ToArray();
        Assert.InRange(Math.Abs(result.PriceRsi!.Value-(decimal)Rsi(price,3)),0,.000000001m);
        Assert.InRange(Math.Abs(result.StreakRsi!.Value-(decimal)Rsi(streak,2)),0,.000000001m);
        Assert.Equal(changes.Skip(200).Take(100).Count(value=>value<changes[300]),result.PercentRank);
    }

    [Fact]
    public void RelativeRoc_UsesAlignedEndpointsAndRejectsMissingStaleOrFutureBenchmark()
    {
        var stock=Bars();var i=300;var reference=stock.Select(bar=>bar with {Ticker="SPY",Close=100}).ToArray();
        stock[i-20]=stock[i-20] with {Close=100};stock[i]=stock[i] with {Close=110};reference[i]=reference[i] with {Close=102};
        var sessions=stock.Skip(i-20).Take(21).Select(bar=>DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(bar.Timestamp,Ny).DateTime)).ToArray();
        var rules=new TechnicalFactorRules(TechnicalFactorRules.RelativeRoc,20,MinValue:5);
        var asOf=stock[i].Timestamp.AddDays(1);var snapshots=Snapshots(stock);
        var available=reference.Take(i+1).ToArray();
        Assert.Equal(8,TechnicalFactorCalculator.Analyze(rules,stock,snapshots,i,available,asOf,sessions).Value);
        Assert.Throws<InvalidDataException>(()=>TechnicalFactorCalculator.Analyze(rules,stock,snapshots,i,available.Where((_,j)=>j!=i-10).ToArray(),asOf,sessions));
        Assert.Throws<InvalidDataException>(()=>TechnicalFactorCalculator.Analyze(rules,stock,snapshots,i,available.Take(i).ToArray(),asOf,sessions));
        var late=available.Select((bar,j)=>j==i?bar with {KnownAtUtc=asOf.AddMinutes(1)}:bar).ToArray();
        Assert.Throws<InvalidDataException>(()=>TechnicalFactorCalculator.Analyze(rules,stock,snapshots,i,late,asOf,sessions));
    }

    [Fact]
    public void CalendarFreshness_UsesLatestCompletedTradingSessionAcrossWeekend()
    {
        var calendar=new Dictionary<DateOnly,MarketSessionSchedule>();
        for(var day=new DateOnly(2026,5,1);day<=new DateOnly(2026,6,8);day=day.AddDays(1))
            calendar[day]=day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday?MarketSessionSchedule.Closed(day):MarketSessionSchedule.RegularDay(day);
        var expected=TechnicalFactorCalculator.ExpectedCompletedSessions(calendar,DateTimeOffset.Parse("2026-06-08T04:00:00Z"),21);
        Assert.Equal(new DateOnly(2026,6,5),expected[^1]);
        calendar.Remove(new DateOnly(2026,6,7));
        Assert.Throws<InvalidDataException>(()=>TechnicalFactorCalculator.ExpectedCompletedSessions(calendar,DateTimeOffset.Parse("2026-06-08T04:00:00Z"),21));
    }

    [Fact]
    public void ConfigRoundTrip_PreservesOptionalRulesAndExistingCanonicalIdentity()
    {
        var root=TestRepository.FindRoot();var reader=new SimpleYamlReader();var catalog=RetiredStrategyFixtures.Catalog(reader);
        var path=RetiredStrategyFixtures.Strategy("swing-connors-rsi2-sma5-risk-managed.bt-v7.yaml");
        var runtime=new StrategyRunArtifactValidator(reader,catalog).ReadAndValidateRuntimeArtifact(path,StrategySelectionMode.Backtest);
        Assert.Equal(runtime.Identity.ContentSha256,EvidenceCanonicalJson.ComputeSha256(new CanonicalStrategyDocument(runtime.Identity.StrategyId,runtime.Identity.SemanticVersion,runtime.Definition,runtime.AdmissionProfile)));
        Assert.DoesNotContain("TechnicalFactor",JsonSerializer.Serialize(runtime.Definition));
        var rules=new TechnicalFactorRules(TechnicalFactorRules.Roc,3,MaxValue:-5);
        var trial=runtime.Definition with {EntryRules=runtime.Definition.EntryRules with {SetupType="momentum",TechnicalFactor=rules}};
        var temp=Path.Combine(Path.GetTempPath(),"technical-factor-"+Guid.NewGuid().ToString("N")+".yaml");
        try{File.WriteAllText(temp,RunConfigWriter.SerializeStrategyYaml(trial));Assert.Equal(trial,reader.ReadStrategy(temp));}
        finally{File.Delete(temp);}
        var signal=new SignalGenerator().CreateTradeSignal(runtime.Definition,Bars(),Snapshots(Bars()),300)!;
        Assert.DoesNotContain("TechnicalFactor",JsonSerializer.Serialize(signal));
    }
    private static StrategyDecisionRequest RelativeRequest(bool regimeOn = true,
        Func<OhlcvBar[],OhlcvBar[]>? changeReference = null)
    {
        var root=TestRepository.FindRoot();var reader=new SimpleYamlReader();
        var catalog=RetiredStrategyFixtures.Catalog(reader);
        var baseline=new StrategyRunArtifactValidator(reader,catalog).ReadAndValidateRuntimeArtifact(
            RetiredStrategyFixtures.Strategy("swing-connors-rsi2-sma5-risk-managed.bt-v7.yaml"),StrategySelectionMode.Backtest);
        var definition=baseline.Definition with {EntryRules=baseline.Definition.EntryRules with {SetupType="momentum",
            TechnicalFactor=new(TechnicalFactorRules.RelativeRoc,20,MinValue:5)}};
        var profile=baseline.AdmissionProfile with {ProfileId="technical-factor-test",ProfileVersion="6.0.0",
            Indicators=baseline.AdmissionProfile.Indicators with {DerivedIndicatorProfileVersion="2.2.0"}};
        var identity=new StrategyArtifactIdentity("test.relative-factor","1.0.0",
            EvidenceCanonicalJson.ComputeSha256(new CanonicalStrategyDocument("test.relative-factor","1.0.0",definition,profile)));
        var runtime=new AuthorizedRuntimeStrategy(identity,StrategySelectionMode.Backtest,definition,profile);
        var bars=Bars();var asOf=bars[^1].Timestamp.AddDays(1).AddHours(2);
        var reference=bars.Select(bar=>bar with {Ticker="SPY",KnownAtUtc=bar.Timestamp.AddDays(1)}).ToArray();
        if(changeReference is not null)reference=changeReference(reference);
        var dates=bars.TakeLast(21).Select(bar=>DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(bar.Timestamp,Ny).DateTime)).ToArray();
        var discovery=StrategyDecisionRequestAssembler.CreateBacktestDiscovery("X","fixture",asOf,asOf.AddDays(1));
        return StrategyDecisionRequestAssembler.Create(runtime,"X",
            new Dictionary<string,IReadOnlyList<OhlcvBar>>{{"1d",bars}},
            new Dictionary<string,IReadOnlyList<IndicatorSnapshot>>{{"1d",Snapshots(bars)}},[],asOf,discovery,
            new(true,"fixture","universe",asOf,"{}"),new(regimeOn,"fixture","regime",asOf,"{}"),
            StrategyCandidateState.DataWarming,0,"fixture",asOf,asOf.AddDays(1),260,Guid.Parse("11111111-2222-3333-4444-555555555555"),reference,dates);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingRequiredBenchmarkFailsBeforeTradingFilters_EvenWhenRegimeOff(bool regimeOn)
    {
        var request=RelativeRequest(regimeOn,bars=>bars.Take(bars.Length-1).ToArray());
        Assert.Throws<InvalidDataException>(()=>new StrategyDecisionKernel().Evaluate(request));
    }

    [Fact]
    public void ReferenceVersions_UseLatestKnownAsOf_AndExcludeLaterRevision()
    {
        var request=RelativeRequest(changeReference:bars=>bars.Concat([
            bars[^1] with {Close=90,KnownAtUtc=bars[^1].KnownAtUtc!.Value.AddHours(1)},
            bars[^1] with {Close=999,KnownAtUtc=bars[^1].KnownAtUtc!.Value.AddHours(3)}]).ToArray());
        Assert.Equal(90,request.ReferenceBars!.Value[^1].Close);
        Assert.True(request.ReferenceBars.Value.All(bar=>bar.KnownAtUtc<=request.AsOfUtc));
        _=new StrategyDecisionKernel().Evaluate(request);
    }

    [Fact]
    public void ReferenceEqualKnownTimesWithDifferentValuesAreRejected()
    {
        Assert.Throws<InvalidDataException>(()=>RelativeRequest(changeReference:bars=>bars.Append(bars[^1] with {Close=999}).ToArray()));
    }

    [Fact]
    public void UnknownFactorAndUnboundedThresholdAreRejected()
    {
        Assert.Throws<InvalidOperationException>(()=>new TechnicalFactorRules("unknown",20,MaxValue:1).Validate());
        Assert.Throws<InvalidOperationException>(()=>new TechnicalFactorRules(TechnicalFactorRules.Roc,3).Validate());
        Assert.Equal(501,new TechnicalFactorRules(TechnicalFactorRules.RelativeRoc,500,MinValue:5).RequiredCompletedBars);
    }

    [Fact]
    public async Task PreviewDoesNotAdvanceAsOfToAFormingDailyBar()
    {
        var root=TestRepository.FindRoot();var reader=new SimpleYamlReader();
        var catalog=RetiredStrategyFixtures.Catalog(reader);
        var path=RetiredStrategyFixtures.Strategy("swing-connors-rsi2-sma5-risk-managed.bt-v7.yaml");
        var runtime=new StrategyRunArtifactValidator(reader,catalog).ReadAndValidateRuntimeArtifact(path,StrategySelectionMode.Backtest);
        var bars=Bars();var asOf=bars[300].Timestamp.AddHours(12);
        var config=reader.ReadBacktestRun(Path.Combine(root,"configs/backtest/swing-backtest-profile.yaml")) with {
            Provider="csv",Tickers=["X"],Intervals=["1d"],DerivedTimeframes=new TradingFlow.Domain.Backtesting.DerivedTimeframeConfig("1d")};
        var result=await new TradingFlow.Backtesting.StrategyEvaluation.StrategyEvaluationEngine().EvaluateAsync(
            new(null,null,["X"],0,bars[0].Timestamp,asOf),new PreviewProvider(bars),config,runtime,path,CancellationToken.None);
        var row=Assert.Single(result.Results);Assert.Equal(bars[299].Timestamp,row.Timestamp);
        Assert.Equal(asOf,result.End);
    }
    private sealed class PreviewProvider(OhlcvBar[] bars):TradingFlow.Engine.Abstractions.IMarketDataProvider
    {
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> frames,DateTimeOffset start,DateTimeOffset end,
            [System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken token)
        {
            await Task.Yield();
            foreach(var bar in bars.Where(bar=>tickers.Contains(bar.Ticker)&&frames.Contains(bar.Timeframe)&&bar.Timestamp>=start&&bar.Timestamp<end))
            {token.ThrowIfCancellationRequested();yield return bar;}
        }
    }

}
