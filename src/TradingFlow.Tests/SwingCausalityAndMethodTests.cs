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
        var snapshots=new IndicatorEngine().Compute(bars).Select((x,i)=>x with {Atr=1,Sma200=100,Rsi2=i>=9?99:2,Sma5=100}).ToArray();
        var context=new PreparedTickerEvaluationContext("SPY",new Dictionary<string,IReadOnlyList<OhlcvBar>>{{"1d",bars}},
            new Dictionary<string,IReadOnlyList<IndicatorSnapshot>>{{"1d",snapshots}},bars,bars.Length,[],null);
        var contexts=new Dictionary<string,PreparedTickerEvaluationContext>{{"SPY",context}};
        var catalog=new StrategyArtifactCatalog(root,Path.Combine(root,"configs","strategy-catalog.json"),reader);
        var runner=new BacktestRunner(reader,catalog);
        var method=typeof(BacktestRunner).GetMethod("ProcessPreparedStrategyTickerAsync",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
        var scratch=Path.Combine(Path.GetTempPath(),"TradingFlowWarmup-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(scratch);
        try {
            async Task<StrategyTickerBacktestBatch> Evaluate(bool late,int required=3,
                IReadOnlyDictionary<string,PreparedTickerEvaluationContext>? selectedContexts=null,
                TradingFlow.Domain.Strategies.StrategyDefinition? selectedStrategy=null)=>await (Task<StrategyTickerBacktestBatch>)method.Invoke(runner,[
                profile with {Engine=profile.Engine with {IndicatorWarmupBars=required,AllowLateWarmup=late}},selectedContexts??contexts,selectedStrategy??strategy,"SPY",
                bars[1].Timestamp,bars[^1].Timestamp.AddDays(1),null,RegimeCalendar.Inactive,new ExecutionAuditor(100),
                Path.Combine(scratch,$"{late}-{required}-{Guid.NewGuid():N}.ndjson"),new RetainedCandidateBudget(100),CancellationToken.None])!;
            var strict=await Evaluate(false);Assert.False(strict.Succeeded);Assert.Contains("insufficient_indicator_warmup",strict.ErrorMessage);
            var late=await Evaluate(true);Assert.True(late.Succeeded,late.ErrorMessage);Assert.NotEmpty(late.CandidateTrades);
            Assert.All(late.CandidateTrades,trade=>Assert.True(trade.EntryTimestamp>=bars[3].Timestamp));
            var warming=await Evaluate(true,30);Assert.True(warming.Succeeded,warming.ErrorMessage);
            Assert.Empty(warming.CandidateTrades);
            Assert.Equal("still_warming_at_study_end",warming.HistoryEligibility!.Status);
            var delayed=bars.Select((bar,i)=>i is >=2 and <=4 ? bar with {KnownAtUtc=bars[9].Timestamp}:bar).ToArray();
            var delayedContext=context with {BarsByTimeframe=new Dictionary<string,IReadOnlyList<OhlcvBar>>{{"1d",delayed}}};
            var delayedResult=await Evaluate(true,3,new Dictionary<string,PreparedTickerEvaluationContext>{{"SPY",delayedContext}});
            Assert.False(delayedResult.Succeeded);Assert.Empty(delayedResult.CandidateTrades);
            Assert.Contains("point_in_time_indicator_snapshots_unavailable",delayedResult.ErrorMessage);
            var observedHistory=BacktestRunner.DescribeObservedHistoryEligibility("SPY",strategy.StrategyId,"1d",delayed,3,
                bars[1].Timestamp,bars[^1].Timestamp.AddDays(1));
            Assert.Equal(bars[6].Timestamp,observedHistory.FirstEligibleDecisionUtc);
            Assert.Equal(1,observedHistory.AvailableAtStart);Assert.Equal(12,observedHistory.AvailableAtCutoff);
            var missing=context with {BarsByTimeframe=new Dictionary<string,IReadOnlyList<OhlcvBar>>()};
            var missingResult=await Evaluate(true,3,new Dictionary<string,PreparedTickerEvaluationContext>{{"SPY",missing}});
            Assert.False(missingResult.Succeeded);Assert.Contains("missing_signal_timeframe",missingResult.ErrorMessage);
            var missingExecution=await Evaluate(true,3,selectedStrategy:strategy with {Execution=strategy.Execution with {Timeframe="15m"}});
            Assert.False(missingExecution.Succeeded);Assert.Contains("missing_execution_timeframe",missingExecution.ErrorMessage);
            var gap=context with {ErrorMessage="Required completed daily session 2026-06-05 is absent from the prepared source."};
            var gapResult=await Evaluate(true,3,new Dictionary<string,PreparedTickerEvaluationContext>{{"SPY",gap}});
            Assert.False(gapResult.Succeeded);Assert.Contains("2026-06-05",gapResult.ErrorMessage);
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

    /// <summary>Prepares the injected frozen bars and retains its owned input copy pending quant cleanup review.</summary>
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
            Assert.True(Directory.Exists(owned.InputRoot));
        } finally {Directory.Delete(scratch,true);}
    }

    /// <summary>Keeps generated audit paths short and retains their owned spool until quant cleanup approval.</summary>
    [Fact]
    public async Task AuditJournalNames_KeepProductionLengthSqlitePathsShort_AndOwned()
    {
        var root = TestRepository.FindRoot();
        var reader = new SimpleYamlReader();
        var run = reader.ReadBacktestRun(Path.Combine(root, "configs/backtest/swing-backtest-profile.yaml"));
        var runner = new BacktestRunner(reader, new StrategyArtifactCatalog(root, Path.Combine(root, "configs/strategy-catalog.json"), reader));
        var scratch = Path.Combine(Path.GetTempPath(), "TradingFlowPathLength-" + Guid.NewGuid().ToString("N"));
        var productionOwnerLength = Path.GetFullPath(Path.Combine(root, "../../outputs/technical-cycle-20261008/results/tech-cycle-v5/portfolio")).Length;
        var owner = Path.Combine(scratch, new string('p', Math.Max(1, productionOwnerLength - scratch.Length - 1)));
        Directory.CreateDirectory(owner);
        try
        {
            var workspace = runner.CreateWorkspace(run, Path.Combine(owner, "result.json"));
            var strategy = Read("swing-connors-rsi2-sma5-risk-managed.bt-v5.yaml");
            var path = workspace.Runner.CreateCandidateAuditJournalPath(strategy, "CRDO");
            Assert.DoesNotContain(strategy.StrategyId, Path.GetFileName(path));
            Assert.True(Path.GetFileName(path).Length < 70);
            Assert.True((BacktestCandidateJournal.StatePath(path) + "-journal").Length < 260);
            Assert.Contains("CRDO", Path.GetFileName(path));
            Assert.EndsWith(".ndjson", path);
            await workspace.ExecuteAsync(() =>
            {
                using var journal = new BacktestCandidateJournal(path);
                Assert.True(File.Exists(BacktestCandidateJournal.StatePath(path)));
                return Task.FromResult(1);
            });
            Assert.True(Directory.Exists(workspace.InputRoot));
        }
        finally { Directory.Delete(scratch, true); }
    }

    [Theory]
    [InlineData("swing-connors-rsi2-sma5-risk-managed.bt-v6.yaml", "swing.connors-rsi2", "6.0.0")]
    public void CanonicalRuntimeIdentity_SurvivesResearchYamlAlias(string file, string id, string version)
    {
        var root=TestRepository.FindRoot();var reader=new SimpleYamlReader();
        var catalog=new StrategyArtifactCatalog(root,Path.Combine(root,"configs","strategy-catalog.json"),reader);
        var path=Path.Combine(root,"configs","backtest","strategies",file);
        var validated=new StrategyRunArtifactValidator(reader,catalog).ReadAndValidateRuntimeArtifact(
            path,TradingFlow.Domain.Strategies.StrategySelectionMode.Backtest);
        var runtime=StrategyDecisionRequestAssembler.CreateBacktestRuntimeStrategy(validated.Definition,catalog);
        Assert.NotEqual(id,validated.Definition.StrategyId);
        Assert.Equal(id,runtime.Identity.StrategyId);Assert.Equal(version,runtime.Identity.SemanticVersion);
        Assert.Equal(validated.Identity,runtime.Identity);Assert.Equal(validated.AdmissionProfile,runtime.AdmissionProfile);
    }

    [Fact]
    public async Task ValidatedSnapshotIdentity_IsWrittenToPublishedAudit_AndOwnedInputsRetainedForReview()
    {
        var root=TestRepository.FindRoot();var reader=new SimpleYamlReader();
        var catalog=new StrategyArtifactCatalog(root,Path.Combine(root,"configs","strategy-catalog.json"),reader);
        var profile=reader.ReadBacktestRun(Path.Combine(root,"configs","backtest","swing-backtest-profile.yaml"));
        var artifact=catalog.RequireSourcePath(Path.Combine(root,"configs","backtest","strategies","swing-connors-rsi2-sma5-risk-managed.bt-v6.yaml"));
        const string version="6.0.0-test-snapshot";
        var hash=TradingFlow.Domain.Research.EvidenceCanonicalJson.ComputeSha256(new TradingFlow.Domain.Strategies.CanonicalStrategyDocument(
            artifact.Identity.StrategyId,version,artifact.ResolvedStrategy,artifact.AdmissionProfile));
        var runtime=new TradingFlow.Domain.Strategies.AuthorizedRuntimeStrategy(
            new(artifact.Identity.StrategyId,version,hash),TradingFlow.Domain.Strategies.StrategySelectionMode.Backtest,
            artifact.ResolvedStrategy,artifact.AdmissionProfile);
        var bars=Enumerable.Range(0,300).Select(i=>Bar("2025-01-01T05:00:00Z",100+i/10m) with {
            Timestamp=DateTimeOffset.Parse("2025-01-01T05:00:00Z").AddDays(i)}).ToArray();
        var scratch=Path.Combine(Path.GetTempPath(),"tf-id-"+Guid.NewGuid().ToString("N")[..8]);Directory.CreateDirectory(scratch);
        try {
            var run=profile with {Provider="csv",Tickers=["SPY"],Intervals=["1d"],ResultsRoot=scratch,
                News=profile.News with {Enabled=false},Universe=profile.Universe! with {Mode="resolved_snapshot"},
                DerivedTimeframes=new TradingFlow.Domain.Backtesting.DerivedTimeframeConfig("1d"),
                TimeWindow=profile.TimeWindow with {Type="fixed",Start=bars[270].Timestamp,End=bars[^1].Timestamp.AddDays(1),WarmupLookbackDays=400}};
            var runner=new BacktestRunner(reader,catalog,marketDataProviderFactory:_=>new FrozenProvider(bars));
            await runner.RunValidatedAsync(run,[runtime],DateTimeOffset.UtcNow,Path.Combine(scratch,"result.json"),CancellationToken.None);
            var audits=String.Join("\n",Directory.EnumerateFiles(scratch,"*.ndjson",SearchOption.AllDirectories).Select(File.ReadAllText));
            Assert.Contains(version,audits);Assert.Contains(hash,audits);Assert.Contains("causal-market-exits-v3",audits);
            Assert.DoesNotContain("0.0.0-backtest",audits);Assert.NotEmpty(Directory.EnumerateDirectories(scratch,".run-inputs-*"));
            // Mutating a validated definition must fail rather than relabel it as diagnostic.
            var changed=runtime with {Definition=runtime.Definition with {StrategyName="changed after validation"}};
            var changedError=await Assert.ThrowsAsync<InvalidDataException>(()=>runner.RunValidatedAsync(run,[changed],
                DateTimeOffset.UtcNow,Path.Combine(scratch,"changed.json"),CancellationToken.None));
            Assert.Contains("differs from its validated artifact identity",changedError.Message);
            Assert.NotEmpty(Directory.EnumerateDirectories(scratch,".run-inputs-*"));
            await Assert.ThrowsAsync<ArgumentException>(()=>runner.RunValidatedAsync(run,[runtime,runtime],DateTimeOffset.UtcNow,
                Path.Combine(scratch,"duplicate.json"),CancellationToken.None));
            Assert.NotEmpty(Directory.EnumerateDirectories(scratch,".run-inputs-*"));
        } finally {Directory.Delete(scratch,true);}
    }

    [Fact]
    public void MissedMoveEntriesUseActualFilledTradesAndCompatiblePeakSliceReceipt()
    {
        var strategy=Read("minervini-technical-template-vcp.bt-v10.yaml");
        var bars=new[]{Bar("2026-04-07T04:00:00Z",10),Bar("2026-04-08T04:00:00Z",12)};
        var filled=new TradingFlow.Domain.Backtesting.BacktestTrade("SPY",strategy.StrategyName,"long",
            bars[1].Timestamp.AddDays(1),bars[1].Timestamp.AddDays(2),1,10,12,9,15,"technical_exit",2,0,2);
        var actual=Assert.Single(BacktestRunner.BuildMissedMoveAudits([strategy],[filled],bars));
        Assert.Contains(strategy.StrategyName,actual.StrategiesWithEntries);
        var unfilled=Assert.Single(BacktestRunner.BuildMissedMoveAudits([strategy],[],bars));
        Assert.Empty(unfilled.StrategiesWithEntries);
    }

    private sealed class FrozenProvider(OhlcvBar[] bars):TradingFlow.Engine.Abstractions.IMarketDataProvider,
        TradingFlow.Engine.Abstractions.IMarketSessionScheduleProvider
    {
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(IReadOnlyCollection<string> symbols,IReadOnlyCollection<string> frames,
            DateTimeOffset start,DateTimeOffset end,[System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken token)
        {
            await Task.Yield();foreach(var bar in bars){token.ThrowIfCancellationRequested();yield return bar;}
        }

        // The fixture explicitly supplies session metadata; production does not infer a calendar from these sample bars.
        public Task<IReadOnlyDictionary<DateOnly,MarketSessionSchedule>> LoadMarketSessionSchedulesAsync(DateOnly start,DateOnly end,CancellationToken token)
        {
            IReadOnlyDictionary<DateOnly,MarketSessionSchedule> calendar=Enumerable.Range(0,end.DayNumber-start.DayNumber+1)
                .Select(i=>start.AddDays(i)).ToDictionary(day=>day,
                    day=>day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? MarketSessionSchedule.Closed(day) : MarketSessionSchedule.RegularDay(day));
            return Task.FromResult(calendar);
        }
    }

    private static TradingFlow.Domain.Strategies.StrategyDefinition Read(string file)=>
        new SimpleYamlReader().ReadStrategy(Path.Combine(TestRepository.FindRoot(),"configs","backtest","strategies",file));
    private static OhlcvBar Bar(string time,decimal close)=>new("SPY",DateTimeOffset.Parse(time),"1d",close,close+1,Math.Max(.1m,close-1),close,1000,"sip","all");
}
