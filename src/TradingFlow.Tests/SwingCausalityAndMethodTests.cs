using TradingFlow.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Regime;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Tests;

public sealed class SwingCausalityAndMethodTests
{
    [Fact]
    public void DecisionRegime_DoesNotUseLaterStockEntryDateOrFutureSpyVersions()
    {
        var bars = new[] { Bar("2026-06-01T04:00:00Z", 10), Bar("2026-06-02T04:00:00Z", 20),
            Bar("2026-06-03T04:00:00Z", 1), Bar("2026-06-04T04:00:00Z", 1) };
        var decision = DateTimeOffset.Parse("2026-06-03T04:00:00Z");
        var original = RegimeCalendarBuilder.Build(bars, 2);
        var changed = RegimeCalendarBuilder.Build(bars.Select(x => x.Timestamp >= decision ? x with { Close = 1000 + x.Timestamp.Day * 100 } : x)
            .Append(bars[0] with { Close = 9999, KnownAtUtc = DateTimeOffset.Parse("2026-06-10T12:00:00Z") }).ToArray(), 2);
        Assert.True(original.IsOn(decision));
        Assert.Equal(original.IsOn(decision), changed.IsOn(decision));
        Assert.False(original.IsOn(DateTimeOffset.Parse("2026-06-05T04:00:00Z")));
        Assert.True(changed.IsOn(DateTimeOffset.Parse("2026-06-05T04:00:00Z")));
    }

    [Fact]
    public void SetupExpiry_UsesScheduledSession_NotNextObservedStockBar()
    {
        var available = DateTimeOffset.Parse("2026-07-03T04:00:00Z");
        var schedule = new Dictionary<DateOnly, MarketSessionSchedule> {
            [new(2026,7,3)] = MarketSessionSchedule.Closed(new(2026,7,3)),
            [new(2026,7,4)] = MarketSessionSchedule.Closed(new(2026,7,4)),
            [new(2026,7,5)] = MarketSessionSchedule.Closed(new(2026,7,5)),
            [new(2026,7,6)] = MarketSessionSchedule.RegularDay(new(2026,7,6)),
            [new(2026,7,7)] = MarketSessionSchedule.RegularDay(new(2026,7,7)) };
        var strategy = Read("swing-connors-rsi2-sma5-risk-managed.bt-v5.yaml");
        var expiry = BacktestDecisionTiming.ResolveExpiry(available, "1d", strategy.Session, schedule);
        Assert.Equal(DateTimeOffset.Parse("2026-07-07T04:00:00Z"), expiry);
        // A stock reopening Wednesday cannot extend the setup's already frozen Tuesday deadline.
        Assert.True(DateTimeOffset.Parse("2026-07-08T04:00:00Z") >= expiry);
        Assert.Throws<InvalidOperationException>(() => BacktestDecisionTiming.ResolveExpiry(available,"1d",strategy.Session,
            new Dictionary<DateOnly,MarketSessionSchedule>()));
    }

    [Fact]
    public void Sma5_IsArithmeticMean_AndUnaffectedByFuturePrices()
    {
        var bars = new decimal[] {1,2,3,4,100,6,7,8,9,10}.Select((x,i) => Bar("2026-06-01T04:00:00Z",x) with {
            Timestamp=DateTimeOffset.Parse("2026-06-01T04:00:00Z").AddDays(i) }).ToArray();
        var original = new IndicatorEngine().Compute(bars);
        var future = new IndicatorEngine().Compute(bars.Concat([bars[^1] with {Timestamp=bars[^1].Timestamp.AddDays(1),Close=99999}]).ToArray());
        Assert.Equal(23m, original[5].Sma5);
        Assert.NotEqual(original[5].Ema5,original[5].Sma5);
        Assert.Equal(original[5].Sma5,future[5].Sma5);
    }

    [Fact]
    public void ConnorsSma5Exit_IsStrict_AndDoesNotUseEma5()
    {
        var strategy=Read("swing-connors-rsi2-sma5-risk-managed.bt-v5.yaml");
        var snapshot=new IndicatorEngine().Compute(Enumerable.Range(1,30).Select(i=>Bar("2026-06-01T04:00:00Z",i) with {
            Timestamp=DateTimeOffset.Parse("2026-06-01T04:00:00Z").AddDays(i) }).ToArray())[^1] with {
                CurrentPrice=101,Sma5=100,Ema5=105,Rsi2=10};
        var engine=new TechnicalExecutionEngine();
        Assert.Equal("technical_exit_above_sma5",engine.GetTechnicalExitReason(strategy,snapshot,1));
        Assert.Null(engine.GetTechnicalExitReason(strategy,snapshot with {CurrentPrice=100},1));
        Assert.Null(engine.GetTechnicalExitReason(strategy,snapshot with {Sma5=null,Ema5=99},1));
    }

    [Fact]
    public void VcpVolumeBaseline_ExcludesBreakoutAndFutureBars()
    {
        var strategy=Read("minervini-technical-template-vcp.bt-v9.yaml");
        var bars=Enumerable.Range(0,65).Select(i=>Bar("2026-01-01T04:00:00Z",105) with {
            Timestamp=DateTimeOffset.Parse("2026-01-01T04:00:00Z").AddDays(i),Volume=i==60?150:100}).ToArray();
        var snapshots=new IndicatorEngine().Compute(bars).Select((x,i)=>x with {Atr=1,Sma50=100,Sma150=99,Sma200=i>=60?98:97}).ToArray();
        var signal=new SignalGenerator().CreateTradeSignal(strategy,bars,snapshots,60);
        Assert.NotNull(signal);Assert.Equal(1.5m,signal!.BreakoutVolumeVs50DayAverage);Assert.True(signal.IsSma200Rising);
        var mutated=bars.Select((x,i)=>i>60?x with {Volume=999999,Close=999999}:x).ToArray();
        var repeated=new SignalGenerator().CreateTradeSignal(strategy,mutated,snapshots,60);
        Assert.Equal(signal.BreakoutVolumeVs50DayAverage,repeated!.BreakoutVolumeVs50DayAverage);
    }

    [Fact]
    public async Task ProviderCutoff_ExcludesIncompleteIntervalsAndFutureKnownVersions()
    {
        var end=DateTimeOffset.Parse("2026-10-07T04:00:00Z");
        var bars=new[] {Bar("2026-10-06T04:00:00Z",10),Bar("2026-10-07T04:00:00Z",20),
            Bar("2026-10-05T04:00:00Z",99) with {KnownAtUtc=end.AddHours(1)}};
        var provider=new CompletedBacktestDataProvider(new FrozenProvider(bars));
        var received=new List<OhlcvBar>();
        await foreach(var bar in provider.GetBarsAsync(["SPY"],["1d"],end.AddDays(-10),end,CancellationToken.None)) received.Add(bar);
        Assert.Equal(DateTimeOffset.Parse("2026-10-06T04:00:00Z"),Assert.Single(received).Timestamp);
    }

    [Fact]
    public async Task LaterHistory_CannotTradeBeforeFullWarmup_ButCanQualifyLater()
    {
        var root=TestRepository.FindRoot();var reader=new SimpleYamlReader();
        var profile=reader.ReadBacktestRun(Path.Combine(root,"configs","backtest","swing-backtest-profile.yaml"));
        var strategy=Read("swing-connors-rsi2-sma5-risk-managed.bt-v5.yaml");
        var bars=Enumerable.Range(0,12).Select(i=>Bar("2026-06-01T04:00:00Z",105) with {
            Timestamp=DateTimeOffset.Parse("2026-06-01T04:00:00Z").AddDays(i)}).ToArray();
        var snapshots=new IndicatorEngine().Compute(bars).Select(x=>x with {Atr=1,Sma200=100,Rsi2=2,Sma5=100}).ToArray();
        var context=new PreparedTickerEvaluationContext("SPY",new Dictionary<string,IReadOnlyList<OhlcvBar>>{{"1d",bars}},
            new Dictionary<string,IReadOnlyList<IndicatorSnapshot>>{{"1d",snapshots}},bars,bars.Length,[],null);
        var contexts=new Dictionary<string,PreparedTickerEvaluationContext>{{"SPY",context}};
        var catalog=new StrategyArtifactCatalog(root,Path.Combine(root,"configs","strategy-catalog.json"),reader);
        var runner=new BacktestRunner(reader,catalog);
        var method=typeof(BacktestRunner).GetMethod("ProcessPreparedStrategyTickerAsync",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
        var scratch=Path.Combine(Path.GetTempPath(),"TradingFlowWarmup-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(scratch);
        try {
            async Task<StrategyTickerBacktestBatch> Evaluate(bool late)=>await (Task<StrategyTickerBacktestBatch>)method.Invoke(runner,[
                profile with {Engine=profile.Engine with {IndicatorWarmupBars=3,AllowLateWarmup=late}},contexts,strategy,"SPY",
                bars[1].Timestamp,bars[^1].Timestamp.AddDays(1),null,RegimeCalendar.Inactive,new ExecutionAuditor(100),
                Path.Combine(scratch,late?"late.ndjson":"strict.ndjson"),new RetainedCandidateBudget(100),CancellationToken.None])!;
            var strict=await Evaluate(false);Assert.False(strict.Succeeded);Assert.Contains("insufficient_indicator_warmup",strict.ErrorMessage);
            var late=await Evaluate(true);Assert.True(late.Succeeded,late.ErrorMessage);Assert.NotEmpty(late.CandidateTrades);
            Assert.All(late.CandidateTrades,trade=>Assert.True(trade.EntryTimestamp>=bars[3].Timestamp.AddDays(1)));
        } finally {Directory.Delete(scratch,true);}
    }

    [Theory]
    [InlineData("minervini-technical-template-vcp.bt-v9.yaml")]
    [InlineData("swing-connors-rsi2-sma5-risk-managed.bt-v5.yaml")]
    public void FrozenMethodSerialization_RoundTripsWithoutLosingRules(string name)
    {
        var original=Read(name);var path=Path.GetTempFileName();
        try {
            File.WriteAllText(path,TradingFlow.Web.Services.RunConfigWriter.SerializeStrategyYaml(original));
            var copy=new SimpleYamlReader().ReadStrategy(path);
            Assert.Equal(TradingFlow.Domain.Research.EvidenceCanonicalJson.ComputeSha256(original),
                TradingFlow.Domain.Research.EvidenceCanonicalJson.ComputeSha256(copy));
        } finally {File.Delete(path);}
    }

    [Fact]
    public async Task FrozenSource_IsPreservedAcrossOwnedRunWorkspace()
    {
        var root=TestRepository.FindRoot();var reader=new SimpleYamlReader();var profile=reader.ReadBacktestRun(Path.Combine(root,"configs","backtest","swing-backtest-profile.yaml"));
        var strategy=Read("swing-connors-rsi2-sma5-risk-managed.bt-v5.yaml");
        var bars=Enumerable.Range(0,12).Select(i=>Bar("2026-06-01T04:00:00Z",105) with {Timestamp=DateTimeOffset.Parse("2026-06-01T04:00:00Z").AddDays(i)}).ToArray();
        var captured=new FrozenProvider(bars);var run=profile with {Provider="csv",Tickers=["SPY"],Intervals=["1d"],
            DerivedTimeframes=new TradingFlow.Domain.Backtesting.DerivedTimeframeConfig("1d"),
            TimeWindow=profile.TimeWindow with {Type="fixed",Start=bars[0].Timestamp,End=bars[^1].Timestamp.AddDays(1),WarmupLookbackDays=0}};
        var runner=new BacktestRunner(reader,new StrategyArtifactCatalog(root,Path.Combine(root,"configs","strategy-catalog.json"),reader),marketDataProviderFactory:_=>captured);
        var scratch=Path.Combine(Path.GetTempPath(),"TradingFlowFrozenSource-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(scratch);
        try {
            var owned=runner.CreateWorkspace(run,Path.Combine(scratch,"result.json"));
            var prepared=await owned.ExecuteAsync(()=>owned.Runner.PrepareMarketAsync(owned.Run,[strategy],CancellationToken.None));
            Assert.True(prepared.MarketState.TickerStates.ContainsKey("SPY"));
            Assert.Equal(12,prepared.MarketState.TickerStates["SPY"].BarsByTimeframe["1d"].Count);
            Assert.False(Directory.Exists(owned.InputRoot));
        } finally {Directory.Delete(scratch,true);}
    }

    private sealed class FrozenProvider(OhlcvBar[] bars):TradingFlow.Engine.Abstractions.IMarketDataProvider
    {
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(IReadOnlyCollection<string> symbols,IReadOnlyCollection<string> frames,
            DateTimeOffset start,DateTimeOffset end,[System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken token)
        {
            await Task.Yield();foreach(var bar in bars){token.ThrowIfCancellationRequested();yield return bar;}
        }
    }

    private static TradingFlow.Domain.Strategies.StrategyDefinition Read(string file)=>
        new SimpleYamlReader().ReadStrategy(Path.Combine(TestRepository.FindRoot(),"configs","backtest","strategies",file));
    private static OhlcvBar Bar(string time,decimal close)=>new("SPY",DateTimeOffset.Parse(time),"1d",close,close+1,Math.Max(.1m,close-1),close,1000,"sip","all");
}
