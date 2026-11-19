using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Execution;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Catalysts;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Regime;
using TradingFlow.Engine.Risk;
using TradingFlow.Engine.Strategies;
using TradingFlow.Web.Services;
using TradingFlow.Web.Services.Desk;

namespace TradingFlow.Tests;

/// <summary>
/// Focused tests for Checkpoints 7 &amp; 8 (Shared Short Foundation + All 4 Short Mirror Strategies + V2-01 Short):
/// reversed SPY/QQQ falling-SMA regime, short VIX sizing, short borrow cost accounting &amp; ledger reconciliation,
/// live/paper/Desk EasyToBorrow enforcement, SEC Rule 201 (day D and D+1 open), short DailyRthFiveMinuteV1 replay,
/// all 4 short setups + V2-01 short MTR-14, YAML round-trips, and preserved canonical strategy hashes.
/// </summary>
public sealed class SwingShortArchetypesTests
{
    private static readonly TimeZoneInfo NewYork = VixRegime.NewYorkTimeZone;

    [Fact]
    public void BearishRegime_PriceBelowFallingSma_AppliesToSpyAndNasdaqQqqOnly()
    {
        // 80 declining weekday closes for SPY -> close < SMA(50) AND SMA(50) < SMA(50)_{-20}.
        var spyDeclining = BuildDailySeries("SPY", new DateOnly(2026, 2, 2), 80, i => 500m - i * 1.5m);
        // 80 rising weekday closes for QQQ -> fails bearish rule.
        var qqqRising = BuildDailySeries("QQQ", new DateOnly(2026, 2, 2), 80, i => 300m + i * 1.5m);
        // Close < SMA(50) on bar 79 (495 < 499.9), but SMA(50) is RISING vs 20 bars earlier (499.9 > 496.0) -> bearish OFF.
        var spyRisingSma = BuildDailySeries("SPY", new DateOnly(2026, 2, 2), 80, i => i < 30 ? 490m : i == 79 ? 495m : 500m);

        var decisionTime = spyDeclining[^1].Timestamp.AddDays(1);
        var spyBearish = RegimeCalendarBuilder.Build(spyDeclining, smaPeriod: 50, risingLookback: 20, bearish: true);
        var qqqBearish = RegimeCalendarBuilder.Build(qqqRising, smaPeriod: 50, risingLookback: 20, bearish: true);
        var risingBearish = RegimeCalendarBuilder.Build(spyRisingSma, smaPeriod: 50, risingLookback: 20, bearish: true);

        Assert.True(spyBearish.IsOn(decisionTime));
        Assert.False(qqqBearish.IsOn(decisionTime));
        Assert.False(risingBearish.IsOn(decisionTime));

        var withNasdaq = spyBearish.WithNasdaqHomeMarket(
            qqqBearish,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["NVDA"] = RegimeRules.NasdaqExchange,
                ["JPM"] = "NYSE"
            });

        // NYSE stock passes when SPY is bearish even if QQQ is rising; Nasdaq stock fails on QQQ; unknown listing fails closed.
        Assert.Null(withNasdaq.EntryDecisionFor(decisionTime, "JPM").Rejection);
        Assert.Equal(RegimeCalendar.NasdaqMarketOffReason, withNasdaq.EntryDecisionFor(decisionTime, "NVDA").Rejection);
        Assert.Equal(RegimeCalendar.ListingExchangeUnknownReason, withNasdaq.EntryDecisionFor(decisionTime, "UNKN").Rejection);
    }

    [Fact]
    public void ShortVixSizing_EnforcesThresholdsFailClosedAndDirectionValidation()
    {
        var shortRegime = new RegimeRules(
            "SPY",
            RegimeRules.PriceBelowFallingSma,
            50,
            NasdaqListingBenchmark: "QQQ",
            VixSeries: RegimeRules.FredVixSeries,
            VixReducedRiskScale: 0.5m,
            SmaFallingLookbackBars: 20,
            VixNoShortsBelow: 18m,
            VixShortFullRiskAbove: 22m);
        shortRegime.Validate("short");

        // Direction & mutual-exclusion invariants.
        Assert.Throws<InvalidOperationException>(() => shortRegime.Validate("long"));
        Assert.Throws<InvalidOperationException>(() => (shortRegime with { VixFullRiskBelow = 18m }).Validate("short"));
        Assert.Throws<InvalidOperationException>(() => (shortRegime with { SmaRisingLookbackBars = 20 }).Validate("short"));

        var d1 = new DateOnly(2026, 6, 1);
        var d2 = new DateOnly(2026, 6, 2);
        var d3 = new DateOnly(2026, 6, 3);
        var d4 = new DateOnly(2026, 6, 4);
        var d5 = new DateOnly(2026, 6, 5);
        var series = VixCloseSeries.From(new Dictionary<DateOnly, decimal>
        {
            [d1] = 17.99m,
            [d2] = 18.00m,
            [d3] = 22.00m,
            [d4] = 22.01m
        });

        var low = VixRegime.Decide(shortRegime, series, d1);
        Assert.Equal(VixRegime.ShortOffReason, low.Rejection);
        Assert.Equal(0m, low.RiskScale);

        var midLow = VixRegime.Decide(shortRegime, series, d2);
        Assert.Null(midLow.Rejection);
        Assert.Equal(0.5m, midLow.RiskScale);

        var midHigh = VixRegime.Decide(shortRegime, series, d3);
        Assert.Null(midHigh.Rejection);
        Assert.Equal(0.5m, midHigh.RiskScale);

        var high = VixRegime.Decide(shortRegime, series, d4);
        Assert.Null(high.Rejection);
        Assert.Equal(1.0m, high.RiskScale);

        var missingClose = VixRegime.Decide(shortRegime, series, d5);
        Assert.Equal(VixRegime.CloseUnknownReason, missingClose.Rejection);

        var missingSource = VixRegime.Decide(shortRegime, null, d4);
        Assert.Equal(VixRegime.LookupFailedReason, missingSource.Rejection);
    }

    [Fact]
    public void ShortBorrowCost_ChargesAccruedAnnualizedFeeAndReconcilesWithDailyLedger()
    {
        // Formula: entryNotional * (aprPct / 100) * max(1, heldCalendarDays) / 365
        var tenDayFee = ShortBorrowCostCalculator.Calculate(10_000m, 1.0m, new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 11));
        Assert.Equal(Decimal.Round(10_000m * 0.01m * 10m / 365m, 4), tenDayFee);
        var sameDayFee = ShortBorrowCostCalculator.Calculate(10_000m, 1.0m, new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 1));
        Assert.Equal(Decimal.Round(10_000m * 0.01m * 1m / 365m, 4), sameDayFee);
        Assert.Equal(0m, ShortBorrowCostCalculator.Calculate(10_000m, null, new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 11)));

        // End-to-end portfolio accounting: ChronologicalBacktestExecutionCoordinator + NativeDailySessionLedger reconcile identically.
        var strategy = ReadCatalogStrategy("configs/backtest/strategies/bollinger-squeeze-breakdown.bt-v1.yaml") with
        {
            Execution = ReadCatalogStrategy("configs/backtest/strategies/bollinger-squeeze-breakdown.bt-v1.yaml").Execution with
            {
                SlippageBps = 0m,
                ShortBorrowAprPct = 3.65m
            }
        };
        var portfolio = new PortfolioConfig(100_000m, 1.0m, 20.0m, 5, 0m, 0m, 1, true);
        // Every trading day from 2026-06-01 (index 0) through 2026-06-12 (index 9; index 8 = 2026-06-11).
        var bars = BuildDailySeries("TEST", new DateOnly(2026, 6, 1), 10, i => i >= 8 ? 90m : 100m, halfRange: 1m, volume: 1_000_000m);
        var schedules = BuildWeekdaySchedules(new DateOnly(2026, 5, 29), new DateOnly(2026, 6, 15));
        var candidate = new BacktestCandidateTrade(
            "TEST",
            strategy.StrategyName,
            "short",
            bars[0].Timestamp,
            100m,
            105m,
            87.5m,
            bars[8].Timestamp,
            90m,
            "max_hold",
            5m,
            1_000_000m,
            0m,
            "atr",
            strategy.StrategyId,
            "cand-borrow-1");

        var coordinator = new ChronologicalBacktestExecutionCoordinator(portfolio, bars, fillSchedules: schedules);
        Assert.True(coordinator.TrySchedule(new ChronologicalExecutionPlan(candidate, strategy, 100), out _));
        coordinator.Complete();

        var trade = Assert.Single(coordinator.CompletedTrades);
        var expectedBorrowFee = ShortBorrowCostCalculator.Calculate(100 * 100m, 3.65m, new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 11));
        Assert.Equal(10.0000m, expectedBorrowFee);
        Assert.Equal(expectedBorrowFee, trade.Fees);
        Assert.Equal(1_000m - expectedBorrowFee, trade.NetProfit);

        // Verify NativeDailySessionLedger replays state.Fills with the exact same borrow-adjusted equity.
        var cutoff = bars[^1].Timestamp.AddDays(1);
        var snapshot = coordinator.Snapshot(cutoff);
        var context = new NativeDailyAccountingContext(bars[0].Timestamp, cutoff, schedules, AllowOpenAtCutoff: false);
        var analyzed = NativeDailySessionLedger.Analyze(portfolio.StartingCapital, snapshot, bars, context);
        Assert.Equal(portfolio.StartingCapital + trade.NetProfit, analyzed.Sessions[^1].Equity);
    }

    [Fact]
    public void EasyToBorrow_IsRequiredForShortEntriesInSharedEligibilityAndDesk()
    {
        var shortStrategy = ReadCatalogStrategy("configs/backtest/strategies/bollinger-squeeze-breakdown.bt-v1.yaml");
        var now = new DateTimeOffset(2026, 7, 22, 14, 30, 0, TimeSpan.Zero);

        var htb = new AssetTradingEligibility("HTB", Active: true, Tradable: true, OvernightTradable: false, now, Shortable: true, BorrowStatus: "hard_to_borrow", EasyToBorrow: false);
        var etb = htb with { Symbol = "ETB", BorrowStatus = "easy_to_borrow", EasyToBorrow = true };
        var notShortable = etb with { Symbol = "NOSHRT", Shortable = false };

        Assert.False(ShortBorrowEligibility.IsShortableAndEasyToBorrow(htb));
        Assert.False(ShortBorrowEligibility.IsShortableAndEasyToBorrow(notShortable));
        Assert.False(ShortBorrowEligibility.IsShortableAndEasyToBorrow(null));
        Assert.True(ShortBorrowEligibility.IsShortableAndEasyToBorrow(etb));

        Assert.Equal(ShortBorrowEligibility.NotEasyToBorrowReason, StockStrategyEvidenceLoader.GetShortBorrowRejection(shortStrategy, htb, lookupFailed: false));
        Assert.Equal(ShortBorrowEligibility.NotEasyToBorrowReason, StockStrategyEvidenceLoader.GetShortBorrowRejection(shortStrategy, etb, lookupFailed: true));
        Assert.Null(StockStrategyEvidenceLoader.GetShortBorrowRejection(shortStrategy, etb, lookupFailed: false));
    }

    [Fact]
    public void SecRule201_RefusesShortOnDayDIntradayTenPercentDrop()
    {
        var strategy = ReadCatalogStrategy("configs/backtest/strategies/bollinger-squeeze-breakdown.bt-v1.yaml");
        var bars = BuildDailySeries("DROP", new DateOnly(2026, 2, 2), 90, i => 100m);
        // Signal bar drops >= 10% intraday from prior close 100 (Low = 89.5 <= 90.0).
        bars[^1] = bars[^1] with { Open = 96m, High = 97m, Low = 89.5m, Close = 90.5m, Volume = 2_000_000m };
        var snapshots = new IndicatorEngine().Compute(bars);

        var signal = new SignalGenerator().CreateTradeSignal(strategy, bars, snapshots, bars.Count - 1);
        Assert.NotNull(signal);
        Assert.True(signal!.IsSecRule201Triggered);

        // Override ADR/ADV so we reach the Rule 201 check cleanly.
        var readySignal = signal with { AverageRangePct = 4.0m, AverageDollarVolume = 50_000_000m };
        var rejection = new BasicStrategyEvaluator().GetShortEntryRejection(strategy, readySignal, 2.0m);
        Assert.Equal("sec_rule_201_short_restriction", rejection);
    }

    [Fact]
    public async Task SecRule201_RefusesShortWhenNextSessionOpensDownTenPercent()
    {
        // Build a simple momentum short strategy with EnforceSecRule201 = true and run through BacktestRunner.
        var baseShort = ReadCatalogStrategy("configs/backtest/strategies/bollinger-squeeze-breakdown.bt-v1.yaml");
        var strategy = baseShort with
        {
            StrategyId = "test.short-rule201-open",
            EntryRules = baseShort.EntryRules with
            {
                ShortSetupType = "momentum",
                BollingerSqueezeLookbackBars = 0,
                MaxBollingerWidthPercentile = null,
                RecentLowLookbackBars = 0,
                MinVolumeSpike = 0m,
                VolumeConfirmationMode = "none",
                MaxShortCloseLocationValue = null,
                MinAverageRangePct = null,
                MinAverageDollarVolume = null
            },
            Regime = null
        };

        // 40 daily bars: day 35 completes cleanly at 100, day 36 opens at 89.0 (-11% gap -> Rule 201 at open!).
        var bars = BuildDailySeries("GAPDN", new DateOnly(2026, 4, 1), 40, _ => 100m, volume: 1_000_000m);
        bars[36] = bars[36] with { Open = 89m, High = 91m, Low = 88m, Close = 89.5m };
        // Keep only bars 35..39 inside the evaluation window so bar 35 is the entry signal bar.
        var evalStart = bars[35].Timestamp;
        var evalEnd = bars[^1].Timestamp.AddDays(1);
        var schedules = BuildWeekdaySchedules(new DateOnly(2026, 3, 1), new DateOnly(2026, 7, 15));

        var result = await RunSyntheticBacktestAsync(strategy, bars, [], schedules, evalStart, evalEnd, replayPolicyId: null);
        var diag = Assert.Single(result.Diagnostics);
        Assert.True(diag.RejectionCounts.TryGetValue("sec_rule_201_short_restriction", out var count) && count >= 1,
            $"Expected sec_rule_201_short_restriction in rejections: {string.Join(", ", diag.RejectionCounts.Keys)}");
    }

    [Fact]
    public async Task ShortDailyRthFiveMinuteV1_ReplaysProtectiveStopAndChargesShortBorrowFee()
    {
        var repoRoot = TestRepository.FindRoot();
        var realStrategyPath = Path.Combine(repoRoot, "configs", "backtest", "strategies", "bollinger-squeeze-breakdown.bt-v1.yaml");
        var baseShort = new SimpleYamlReader().ReadStrategy(realStrategyPath);
        var strategy = baseShort with
        {
            EntryRules = baseShort.EntryRules with
            {
                ShortSetupType = "momentum",
                BollingerSqueezeLookbackBars = 0,
                MaxBollingerWidthPercentile = null,
                RecentLowLookbackBars = 0,
                MinVolumeSpike = 0m,
                VolumeConfirmationMode = "none",
                MaxShortCloseLocationValue = null,
                MinAverageRangePct = null,
                MinAverageDollarVolume = null
            },
            ExitRules = baseShort.ExitRules with { StopAtrMultiple = 1.0m, TargetRMultiple = 2.5m, MaxHoldBars = 10 },
            Execution = baseShort.Execution with { SlippageBps = 0m, ShortBorrowAprPct = 3.65m, EnforceSecRule201 = false },
            Regime = null
        };

        // 37 daily bars (price = 100, range = 2 -> ATR ~= 2 -> short initial stop ~= 102, target ~= 95).
        // Signal fires on bar 35 close, enters at bar 36 09:30 NY open, and stops out at bar 36 09:40 NY (no bar 37).
        var daily = BuildDailySeries("SHRT", new DateOnly(2026, 4, 1), 37, _ => 100m, halfRange: 1m, volume: 1_000_000m);
        var signalIndex = 35;
        var evalStart = daily[signalIndex].Timestamp;
        var evalEnd = daily[^1].Timestamp.AddDays(1);
        var schedules = BuildWeekdaySchedules(new DateOnly(2026, 3, 1), new DateOnly(2026, 7, 15));

        var fineBars = BuildFiveMinuteSeries("SHRT", daily, signalIndex, spikeBarIndex: signalIndex + 1, spikeSliceIndex: 2);

        // Verify BacktestExecutionProfileResolver automatically adds "5m" when DailyRthFiveMinuteV1 is requested on swing-backtest-profile.
        var swingProfile = new SimpleYamlReader().ReadBacktestRun(
            Path.Combine(repoRoot, "configs", "backtest", "swing-backtest-profile.yaml"));
        var resolvedFiveMinute = BacktestExecutionProfileResolver.Resolve(
            swingProfile,
            [baseShort],
            ExecutionReplayPolicy.DailyRthFiveMinuteV1);
        Assert.Equal(ExecutionReplayPolicy.DailyRthFiveMinuteV1, resolvedFiveMinute.Engine.ExecutionReplayPolicyId);
        Assert.Contains(ExecutionReplayPolicy.Timeframe, resolvedFiveMinute.Intervals, StringComparer.OrdinalIgnoreCase);

        var observer = new CapturingOpportunityObserver();
        var (result, runConfig) = await RunSyntheticBacktestWithConfigAsync(
            strategy,
            daily,
            fineBars,
            schedules,
            evalStart,
            evalEnd,
            replayPolicyId: ExecutionReplayPolicy.DailyRthFiveMinuteV1,
            precommittedLiquidationSourceUtc: null,
            opportunityObserver: observer);

        var strategyResult = Assert.Single(result.StrategyResults);
        Assert.True(strategyResult.EconomicResultsComplete);
        var firstTrade = Assert.Single(strategyResult.CompletedTrades);
        Assert.Equal("short", firstTrade.Direction);
        Assert.Equal("stop_loss", firstTrade.ExitReason);
        Assert.True(firstTrade.Fees > 0m, "Expected short_borrow_apr_pct borrow fee on closed short trade.");
        Assert.True(firstTrade.NetProfit < firstTrade.GrossProfit);

        // NativeDailySessionLedger reconciles 5m fills and closing-fill borrow fees identically with CompletedTrade.Fees.
        Assert.NotNull(strategyResult.PortfolioState);
        Assert.Empty(strategyResult.PortfolioState!.OpenPositions);
        Assert.All(strategyResult.PortfolioState.Fills, fill => Assert.Equal(ExecutionReplayPolicy.Timeframe, fill.FillTimeframe));
        Assert.Equal(firstTrade.Fees, strategyResult.PortfolioState.Fills.Sum(fill => fill.Fees));
        NativeDailySessionLedger.VerifyRecordedState(
            100_000m,
            strategyResult.PortfolioState,
            new NativeDailyAccountingContext(evalStart.ToUniversalTime(), evalEnd.ToUniversalTime(), schedules, AllowOpenAtCutoff: true));

        // Shadow replay observes the exact same closing-fill borrow fee through IExecutionEventSink.
        Assert.NotNull(observer.Snapshot);
        var shadowOutcomes = BacktestOpportunityShadowReplay.Replay(observer.Snapshot!);
        var shadow = Assert.Single(shadowOutcomes, outcome => outcome.Candidate.CandidateId == firstTrade.CandidateId);
        Assert.Equal("completed", shadow.Status);
        Assert.NotNull(shadow.CompletedTrade);
        Assert.Equal(shadow.CompletedTrade!.Fees, shadow.Fees);
        Assert.Equal(firstTrade.Fees, shadow.Fees);

        // MonthlyRunResultWriter produces verified marked-to-market monthly figures for DailyRthFiveMinuteV1 with PortfolioState.
        var reader = new SimpleYamlReader();
        var catalog = new StrategyArtifactCatalog(repoRoot, Path.Combine(repoRoot, "configs", "strategy-catalog.json"), reader);
        var monthlyWriter = new MonthlyRunResultWriter(new StrategyRunArtifactValidator(reader, catalog));
        var monthlyConfig = runConfig with { Strategies = [realStrategyPath] };
        var monthlyReport = monthlyWriter.Create(monthlyConfig, result, evalEnd, "synthetic-short-5m");
        Assert.Null(monthlyReport.UnavailableReason);
        var monthRow = Assert.Single(monthlyReport.Results);
        Assert.NotNull(monthRow.ReturnPct);
        Assert.True(monthRow.ReturnPct < 0m);
        Assert.Null(monthRow.UnavailableReason);
    }

    [Fact]
    public async Task ShortDailyRthFiveMinuteV1_OpenAtStudyEnd_RetainsOpenPositionAndAcceptedCount()
    {
        var repoRoot = TestRepository.FindRoot();
        var realStrategyPath = Path.Combine(repoRoot, "configs", "backtest", "strategies", "bollinger-squeeze-breakdown.bt-v1.yaml");
        var baseShort = new SimpleYamlReader().ReadStrategy(realStrategyPath);
        var strategy = baseShort with
        {
            EntryRules = baseShort.EntryRules with
            {
                ShortSetupType = "momentum",
                BollingerSqueezeLookbackBars = 0,
                MaxBollingerWidthPercentile = null,
                RecentLowLookbackBars = 0,
                MinVolumeSpike = 0m,
                VolumeConfirmationMode = "none",
                MaxShortCloseLocationValue = null,
                MinAverageRangePct = null,
                MinAverageDollarVolume = null
            },
            ExitRules = baseShort.ExitRules with { StopAtrMultiple = 1.0m, TargetRMultiple = 2.5m, MaxHoldBars = 10 },
            Execution = baseShort.Execution with { SlippageBps = 0m, ShortBorrowAprPct = 3.65m, EnforceSecRule201 = false },
            Regime = null
        };

        // 37 daily bars with flat 5m prices (no stop/target touch): window covers only the final session (bar 36) so
        // bar 35 signals once -> enters bar 36 at 09:30 NY -> remains open_at_study_end with exactly 1 candidate.
        var daily = BuildDailySeries("OPEN", new DateOnly(2026, 4, 1), 37, _ => 100m, halfRange: 1m, volume: 1_000_000m);
        var signalIndex = 35;
        var evalStart = daily[signalIndex + 1].Timestamp;
        var evalEnd = daily[^1].Timestamp.AddDays(1);
        var schedules = BuildWeekdaySchedules(new DateOnly(2026, 3, 1), new DateOnly(2026, 7, 15));
        var fineBars = BuildFiveMinuteSeries("OPEN", daily, signalIndex, spikeBarIndex: null, spikeSliceIndex: null);

        var (result, runConfig) = await RunSyntheticBacktestWithConfigAsync(
            strategy,
            daily,
            fineBars,
            schedules,
            evalStart,
            evalEnd,
            replayPolicyId: ExecutionReplayPolicy.DailyRthFiveMinuteV1,
            precommittedLiquidationSourceUtc: null);

        var strategyResult = Assert.Single(result.StrategyResults);
        Assert.True(strategyResult.EconomicResultsComplete);
        Assert.Empty(strategyResult.CompletedTrades);
        Assert.Equal(1, strategyResult.CandidateTradeCount);
        Assert.Equal(1, strategyResult.AcceptedTradeCount);
        Assert.Equal(0, strategyResult.RejectedTradeCount);

        Assert.NotNull(strategyResult.PortfolioState);
        var openPosition = Assert.Single(strategyResult.PortfolioState!.OpenPositions);
        Assert.Equal("OPEN", openPosition.Ticker);
        Assert.True(openPosition.SignedQuantity < 0);
        Assert.Equal(100m, openPosition.MarkPrice);

        var reader = new SimpleYamlReader();
        var catalog = new StrategyArtifactCatalog(repoRoot, Path.Combine(repoRoot, "configs", "strategy-catalog.json"), reader);
        var monthlyWriter = new MonthlyRunResultWriter(new StrategyRunArtifactValidator(reader, catalog));
        var monthlyReport = monthlyWriter.Create(runConfig with { Strategies = [realStrategyPath] }, result, evalEnd, "synthetic-short-open");
        Assert.Null(monthlyReport.UnavailableReason);
        var monthRow = Assert.Single(monthlyReport.Results);
        Assert.NotNull(monthRow.ReturnPct);
        Assert.Null(monthRow.UnavailableReason);
    }

    [Fact]
    public void BollingerSqueezeBreakdown_TriggersOnCompressedBreakoutAndRejectsHighClvOrWideBands()
    {
        var strategy = ReadCatalogStrategy("configs/backtest/strategies/bollinger-squeeze-breakdown.bt-v1.yaml");
        var bars = BuildSqueezeBreakdownBars(compressed: true, triggerClose: 95.1m, triggerHigh: 98m, triggerLow: 95m);
        var snapshots = new IndicatorEngine().Compute(bars);

        var signal = new SignalGenerator().CreateTradeSignal(strategy, bars, snapshots, bars.Count - 1);
        Assert.NotNull(signal);
        var readySignal = signal! with { AverageRangePct = 3.5m, AverageDollarVolume = 30_000_000m };

        Assert.Null(new BasicStrategyEvaluator().GetShortEntryRejection(strategy, readySignal, 2.0m));

        // Shared StrategyDecisionBrain enforces min_volume_spike (1.5) via hard_gate on the cataloged short squeeze strategy.
        var brain = new StrategyDecisionBrain();
        var readySnapshot = snapshots[^1] with { RelativeVolumeSampleCount = Math.Max(snapshots[^1].RelativeVolumeSampleCount, 20) };
        var lowVolumeRejection = brain.GetShortEntryRejection(strategy, readySignal, readySnapshot, 1.2m);
        Assert.NotNull(lowVolumeRejection);
        Assert.Contains("relative_volume_below_minimum", lowVolumeRejection, StringComparison.Ordinal);
        Assert.Null(brain.GetShortEntryRejection(strategy, readySignal, readySnapshot, 1.6m));

        // High CLV (> 0.25) rejects with short_close_location_above_maximum.
        var badClvBars = BuildSqueezeBreakdownBars(compressed: true, triggerClose: 97.5m, triggerHigh: 98m, triggerLow: 95m);
        var badClvSignal = new SignalGenerator().CreateTradeSignal(strategy, badClvBars, new IndicatorEngine().Compute(badClvBars), badClvBars.Count - 1)!
            with { AverageRangePct = 3.5m, AverageDollarVolume = 30_000_000m };
        var clvRejection = new BasicStrategyEvaluator().GetShortEntryRejection(strategy, badClvSignal, 2.0m);
        Assert.Contains("short_close_location_above_maximum", clvRejection, StringComparison.Ordinal);

        // Wide prior bands reject with setup_bollinger_squeeze_breakdown_not_triggered.
        var wideBars = BuildSqueezeBreakdownBars(compressed: false, triggerClose: 95.1m, triggerHigh: 98m, triggerLow: 95m);
        var wideSignal = new SignalGenerator().CreateTradeSignal(strategy, wideBars, new IndicatorEngine().Compute(wideBars), wideBars.Count - 1)!
            with { AverageRangePct = 3.5m, AverageDollarVolume = 30_000_000m };
        var wideRejection = new BasicStrategyEvaluator().GetShortEntryRejection(strategy, wideSignal, 2.0m);
        Assert.Contains("setup_bollinger_squeeze_breakdown_not_triggered", wideRejection, StringComparison.Ordinal);
    }

    [Fact]
    public void NewsOrbBreakdown_EvaluatesNegativeCatalystPositiveVetoGapDownAndBearishOpeningRange()
    {
        var strategy = ReadCatalogStrategy("configs/backtest/strategies/news-orb-breakdown.bt-v1.yaml");
        var daily = BuildDailySeries("NORB", new DateOnly(2026, 4, 1), 35, _ => 100m, halfRange: 2m, volume: 1_000_000m);
        var signalDay = ToNewYorkDate(daily[^1].Timestamp);
        // Prior close = 100, ATR ~= 4 -> Open = 95 gives gapDownAtr = 5/4 = 1.25 >= 0.75; Low = 91 (> 90 so no Rule 201), Close = 91.5.
        daily[^1] = daily[^1] with { Open = 95m, High = 95.5m, Low = 91m, Close = 91.5m, Volume = 3_000_000m };
        var snapshots = new IndicatorEngine().Compute(daily);
        var schedules = BuildWeekdaySchedules(new DateOnly(2026, 3, 15), new DateOnly(2026, 7, 15));

        var fiveMin = BuildBearishOrbFiveMinuteBars("NORB", daily, staysAtOrBelowVwap: true);
        var publishedUtc = ToNewYorkUtc(signalDay, new TimeOnly(8, 30));
        var negativeNews = new[]
        {
            new CatalystEvent(
                "NORB",
                publishedUtc,
                CatalystType.AnalystDowngrade,
                "Analyst downgrades NORB to Sell and cuts guidance",
                -0.45m,
                Provider: "benzinga",
                ReceivedAt: publishedUtc.AddMinutes(1),
                UpdatedAt: publishedUtc,
                DecisionAvailableAt: publishedUtc.AddMinutes(1))
        };

        var signal = new SignalGenerator().CreateTradeSignal(
            strategy, daily, snapshots, daily.Count - 1,
            fiveMinuteBars: fiveMin, availableCatalysts: negativeNews, sessionSchedules: schedules,
            ignoreObservedReceiptForHistoricalResearch: true, fiveMinuteBarsSortedAscending: true)!
            with { AverageRangePct = 3.5m, AverageDollarVolume = 30_000_000m };

        Assert.Null(new BasicStrategyEvaluator().GetShortEntryRejection(strategy, signal, 2.0m));

        // Positive FinBERT >= +0.15 anywhere in the window vetoes the short entry.
        var positiveVetoNews = negativeNews.Append(new CatalystEvent(
            "NORB",
            publishedUtc.AddMinutes(30),
            CatalystType.NewsReport,
            "NORB CEO upbeat on long-term pipeline",
            0.25m,
            Provider: "benzinga",
            ReceivedAt: publishedUtc.AddMinutes(31),
            UpdatedAt: publishedUtc.AddMinutes(30),
            DecisionAvailableAt: publishedUtc.AddMinutes(31))).ToArray();
        var vetoedSignal = new SignalGenerator().CreateTradeSignal(
            strategy, daily, snapshots, daily.Count - 1,
            fiveMinuteBars: fiveMin, availableCatalysts: positiveVetoNews, sessionSchedules: schedules,
            ignoreObservedReceiptForHistoricalResearch: true, fiveMinuteBarsSortedAscending: true)!
            with { AverageRangePct = 3.5m, AverageDollarVolume = 30_000_000m };
        var vetoRejection = new BasicStrategyEvaluator().GetShortEntryRejection(strategy, vetoedSignal, 2.0m);
        Assert.Contains("positive_news_sentiment_veto", vetoRejection, StringComparison.Ordinal);

        // Non-negative earnings headline with neutral sentiment (0.05) fails tier1_negative_catalyst_not_confirmed.
        var neutralNews = new[]
        {
            new CatalystEvent(
                "NORB",
                publishedUtc,
                CatalystType.EarningsRelease,
                "NORB reports quarterly results",
                0.05m,
                Provider: "benzinga",
                ReceivedAt: publishedUtc.AddMinutes(1),
                UpdatedAt: publishedUtc,
                DecisionAvailableAt: publishedUtc.AddMinutes(1))
        };
        var neutralSignal = new SignalGenerator().CreateTradeSignal(
            strategy, daily, snapshots, daily.Count - 1,
            fiveMinuteBars: fiveMin, availableCatalysts: neutralNews, sessionSchedules: schedules,
            ignoreObservedReceiptForHistoricalResearch: true, fiveMinuteBarsSortedAscending: true)!
            with { AverageRangePct = 3.5m, AverageDollarVolume = 30_000_000m };
        Assert.Equal("tier1_negative_catalyst_not_confirmed", new BasicStrategyEvaluator().GetShortEntryRejection(strategy, neutralSignal, 2.0m));

        // Reclaiming VWAP during opening 30m fails opening_range_reclaimed_session_vwap.
        var reclaimedFiveMin = BuildBearishOrbFiveMinuteBars("NORB", daily, staysAtOrBelowVwap: false);
        var reclaimedSignal = new SignalGenerator().CreateTradeSignal(
            strategy, daily, snapshots, daily.Count - 1,
            fiveMinuteBars: reclaimedFiveMin, availableCatalysts: negativeNews, sessionSchedules: schedules,
            ignoreObservedReceiptForHistoricalResearch: true, fiveMinuteBarsSortedAscending: true)!
            with { AverageRangePct = 3.5m, AverageDollarVolume = 30_000_000m };
        Assert.Contains("opening_range_reclaimed_session_vwap", new BasicStrategyEvaluator().GetShortEntryRejection(strategy, reclaimedSignal, 2.0m), StringComparison.Ordinal);
    }

    [Fact]
    public void Ema21RejectionShort_AndV201Mtr14_TriggerRejectAndDifferOnOutlierStopSizing()
    {
        var control = ReadCatalogStrategy("configs/backtest/strategies/ema21-rejection-short.bt-v1.yaml");
        var treatment = ReadCatalogStrategy("configs/backtest/strategies/ema21-rejection-short-mtr14.bt-v1.yaml");
        var engine = new IndicatorEngine();

        var bars = BuildEma21RejectionBars(bounceHighAboveSma50: false, dryBounceVolume: true, includeOutlierTrBar: true);
        var snapshots = engine.Compute(bars).ToArray();
        snapshots[83] = snapshots[83] with { RelativeVolume = 0.60m };
        var signalControl = new SignalGenerator().CreateTradeSignal(control, bars, snapshots, bars.Count - 1)!
            with { AverageRangePct = 4.0m, AverageDollarVolume = 40_000_000m };
        var signalMtr = new SignalGenerator().CreateTradeSignal(treatment, bars, snapshots, bars.Count - 1)!
            with { AverageRangePct = 4.0m, AverageDollarVolume = 40_000_000m };

        Assert.Null(new BasicStrategyEvaluator().GetShortEntryRejection(control, signalControl, 1.0m));
        Assert.Null(new BasicStrategyEvaluator().GetShortEntryRejection(treatment, signalMtr, 1.0m));

        // V2-01 MTR-14 stop distance is tighter than Wilder ATR(14) when a single outlier bar inflates ATR.
        var stopResolver = new StrategyInitialStopResolver();
        var atrStop = stopResolver.Resolve(new StrategyInitialStopRequest(control, signalControl, PlannedOrderSide.Short, bars[^1].Close, bars.Count - 1, bars, snapshots));
        var mtrStop = stopResolver.Resolve(new StrategyInitialStopRequest(treatment, signalMtr, PlannedOrderSide.Short, bars[^1].Close, bars.Count - 1, bars, snapshots));
        Assert.True(atrStop.IsResolved && mtrStop.IsResolved);
        Assert.True(mtrStop.StopDistance < atrStop.StopDistance,
            $"Expected MTR-14 stop distance ({mtrStop.StopDistance}) < ATR-14 ({atrStop.StopDistance}) when an outlier bar exists.");

        // Bounce high breaking above SMA(50) rejects with bounce_high_above_sma50_or_sma50_missing.
        var highAboveBars = BuildEma21RejectionBars(bounceHighAboveSma50: true, dryBounceVolume: true, includeOutlierTrBar: false);
        var highAboveSnaps = engine.Compute(highAboveBars).ToArray();
        highAboveSnaps[83] = highAboveSnaps[83] with { RelativeVolume = 0.60m };
        var highAboveSignal = new SignalGenerator().CreateTradeSignal(control, highAboveBars, highAboveSnaps, highAboveBars.Count - 1)!
            with { AverageRangePct = 4.0m, AverageDollarVolume = 40_000_000m };
        Assert.Contains("bounce_high_above_sma50", new BasicStrategyEvaluator().GetShortEntryRejection(control, highAboveSignal, 1.0m), StringComparison.Ordinal);

        // Wet bounce volume (RVOL >= 0.85) rejects with bounce_volume_not_dried_up.
        var wetBars = BuildEma21RejectionBars(bounceHighAboveSma50: false, dryBounceVolume: false, includeOutlierTrBar: false);
        var wetSnaps = engine.Compute(wetBars).ToArray();
        wetSnaps[83] = wetSnaps[83] with { RelativeVolume = 1.10m };
        var wetSignal = new SignalGenerator().CreateTradeSignal(control, wetBars, wetSnaps, wetBars.Count - 1)!
            with { AverageRangePct = 4.0m, AverageDollarVolume = 40_000_000m };
        Assert.Contains("bounce_volume_not_dried_up", new BasicStrategyEvaluator().GetShortEntryRejection(control, wetSignal, 1.0m), StringComparison.Ordinal);
    }

    [Fact]
    public void ParabolicShootingStarBreakdown_TriggersAndRejectsInvalidCandleGeometry()
    {
        var strategy = ReadCatalogStrategy("configs/backtest/strategies/parabolic-exhaustion-short.bt-v1.yaml");
        var bars = BuildParabolicBars(shootingStarHigh: 120m, shootingStarOpen: 109.5m, shootingStarClose: 109m, shootingStarLow: 108m, triggerClose: 107m);
        var snapshots = new IndicatorEngine().Compute(bars);

        var signal = new SignalGenerator().CreateTradeSignal(strategy, bars, snapshots, bars.Count - 1)!
            with { AverageRangePct = 4.0m, AverageDollarVolume = 40_000_000m };
        Assert.Null(new BasicStrategyEvaluator().GetShortEntryRejection(strategy, signal, 1.0m));

        // Too-small upper wick rejects.
        var smallWickBars = BuildParabolicBars(shootingStarHigh: 120m, shootingStarOpen: 118m, shootingStarClose: 110m, shootingStarLow: 108m, triggerClose: 107m);
        var smallWickSignal = new SignalGenerator().CreateTradeSignal(strategy, smallWickBars, new IndicatorEngine().Compute(smallWickBars), smallWickBars.Count - 1)!
            with { AverageRangePct = 4.0m, AverageDollarVolume = 40_000_000m };
        Assert.Contains("prior_bar_upper_wick_too_small", new BasicStrategyEvaluator().GetShortEntryRejection(strategy, smallWickSignal, 1.0m), StringComparison.Ordinal);

        // High CLV (> 0.35) rejects.
        var highClvBars = BuildParabolicBars(shootingStarHigh: 120m, shootingStarOpen: 113m, shootingStarClose: 112.8m, shootingStarLow: 108m, triggerClose: 107m);
        var highClvSignal = new SignalGenerator().CreateTradeSignal(strategy, highClvBars, new IndicatorEngine().Compute(highClvBars), highClvBars.Count - 1)!
            with { AverageRangePct = 4.0m, AverageDollarVolume = 40_000_000m };
        Assert.Contains("prior_bar_close_location_too_high", new BasicStrategyEvaluator().GetShortEntryRejection(strategy, highClvSignal, 1.0m), StringComparison.Ordinal);

        // Trigger failing to close below prior low rejects.
        var noBreakBars = BuildParabolicBars(shootingStarHigh: 120m, shootingStarOpen: 109.5m, shootingStarClose: 109m, shootingStarLow: 108m, triggerClose: 108.5m);
        var noBreakSignal = new SignalGenerator().CreateTradeSignal(strategy, noBreakBars, new IndicatorEngine().Compute(noBreakBars), noBreakBars.Count - 1)!
            with { AverageRangePct = 4.0m, AverageDollarVolume = 40_000_000m };
        Assert.Contains("trigger_close_not_below_prior_low", new BasicStrategyEvaluator().GetShortEntryRejection(strategy, noBreakSignal, 1.0m), StringComparison.Ordinal);
    }

    [Fact]
    public void ShortCatalogRecipes_RoundTripCleanlyAndPreserveAllPriorCanonicalHashes()
    {
        var repoRoot = TestRepository.FindRoot();
        var reader = new SimpleYamlReader();
        var catalog = new StrategyArtifactCatalog(repoRoot, Path.Combine(repoRoot, "configs", "strategy-catalog.json"), reader);
        var byKey = catalog.GetSnapshot().ExecutableArtifacts
            .ToDictionary(a => $"{a.Identity.StrategyId}@{a.Identity.SemanticVersion}", StringComparer.Ordinal);

        // All five prior long swing hashes remain byte-for-byte unchanged.
        Assert.Equal("ee56bf94bb160cbcd27dbde297cfe38ae366acb0911475d3ecee4f530ef9628d", byKey["technical.bollinger-squeeze-breakout@1.0.0"].Identity.ContentSha256);
        Assert.Equal("0a2cac80c7a629eae0a9c1def135ae757a806210545e4d4ca9e5b9884452e72c", byKey["technical.ema21-trend-pullback@1.0.0"].Identity.ContentSha256);
        Assert.Equal("445cef6d533becfb1a76b87c1d4187a502a9f7bc39b0836fb90271aa72592eec", byKey["technical.ema21-trend-pullback@1.1.0"].Identity.ContentSha256);
        Assert.Equal("d556e04c828b9c2096be3c2e88ac3da937d076d47dac659f75c4a95e33b953c2", byKey["technical.rubber-band-reclaim@1.0.0"].Identity.ContentSha256);
        Assert.Equal("15a4a43efd2b65e520603647856f4d4c6fa9c5b3fc383581e504fe12975d26f2", byKey["technical.news-orb-swing@1.0.0"].Identity.ContentSha256);

        // V2-01 short treatment is explicitly bound to 1.0.0's immutable content hash.
        var shortControl = byKey["technical.ema21-rejection-short@1.0.0"];
        var shortMtr = byKey["technical.ema21-rejection-short@1.1.0"];
        Assert.Equal(shortControl.Identity.ContentSha256, shortMtr.DerivedFromContentSha256);
        Assert.NotEqual(shortControl.Identity.ContentSha256, shortMtr.Identity.ContentSha256);

        // All 5 short recipes round-trip through RunConfigWriter.SerializeStrategyYaml with identical canonical hashes.
        var shortKeys = new[]
        {
            "technical.bollinger-squeeze-breakdown@1.0.0",
            "technical.news-orb-breakdown@1.0.0",
            "technical.ema21-rejection-short@1.0.0",
            "technical.ema21-rejection-short@1.1.0",
            "technical.parabolic-exhaustion-short@1.0.0"
        };
        using var tmp = new TemporaryFolder();
        foreach (var key in shortKeys)
        {
            var original = byKey[key].ResolvedStrategy;
            var yaml = RunConfigWriter.SerializeStrategyYaml(original);
            var path = Path.Combine(tmp.Path, key.Replace('@', '-') + ".yaml");
            File.WriteAllText(path, yaml);
            var reparsed = reader.ReadStrategy(path);
            Assert.Equal(EvidenceCanonicalJson.ComputeSha256(original), EvidenceCanonicalJson.ComputeSha256(reparsed));
        }
    }

    [Fact]
    public void ShortYamlValidation_RejectsShortOnlyFieldsOnLongStrategyAndOutOfRangeSentimentVeto()
    {
        var reader = new SimpleYamlReader();
        var longBase = ReadCatalogStrategy("configs/backtest/strategies/bollinger-squeeze-breakout.bt-v1.yaml");
        var shortOrb = ReadCatalogStrategy("configs/backtest/strategies/news-orb-breakdown.bt-v1.yaml");
        using var tmp = new TemporaryFolder();

        var borrowOnLongPath = Path.Combine(tmp.Path, "borrow-on-long.yaml");
        File.WriteAllText(borrowOnLongPath, RunConfigWriter.SerializeStrategyYaml(
            longBase with { Execution = longBase.Execution with { ShortBorrowAprPct = 3.0m } }));
        Assert.Throws<InvalidOperationException>(() => reader.ReadStrategy(borrowOnLongPath));

        var rule201OnLongPath = Path.Combine(tmp.Path, "rule201-on-long.yaml");
        File.WriteAllText(rule201OnLongPath, RunConfigWriter.SerializeStrategyYaml(
            longBase with { Execution = longBase.Execution with { EnforceSecRule201 = true } }));
        Assert.Throws<InvalidOperationException>(() => reader.ReadStrategy(rule201OnLongPath));

        var outOfRangeVetoPath = Path.Combine(tmp.Path, "veto-out-of-range.yaml");
        File.WriteAllText(outOfRangeVetoPath, RunConfigWriter.SerializeStrategyYaml(
            shortOrb with { EntryRules = shortOrb.EntryRules with { VetoShortNewsSentimentAtOrAbove = 1.5m } }));
        Assert.Throws<InvalidOperationException>(() => reader.ReadStrategy(outOfRangeVetoPath));

        var vetoWithoutCategoriesPath = Path.Combine(tmp.Path, "veto-no-categories.yaml");
        File.WriteAllText(vetoWithoutCategoriesPath, RunConfigWriter.SerializeStrategyYaml(
            shortOrb with { EntryRules = shortOrb.EntryRules with { AllowedCatalystCategories = null, VetoShortNewsSentimentAtOrAbove = 0.15m } }));
        Assert.Throws<InvalidOperationException>(() => reader.ReadStrategy(vetoWithoutCategoriesPath));
    }

    private static StrategyDefinition ReadCatalogStrategy(string relativePath) =>
        new SimpleYamlReader().ReadStrategy(Path.Combine(TestRepository.FindRoot(), relativePath));

    private static OhlcvBar DailyBar(string ticker, DateOnly date, decimal open, decimal high, decimal low, decimal close, decimal volume) =>
        new(ticker, ToNewYorkUtc(date, TimeOnly.MinValue), "1d", open, high, low, close, volume, DataFeed: "sip", AdjustmentPolicy: "raw");

    private static DateOnly ToNewYorkDate(DateTimeOffset utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, NewYork).DateTime);

    private static List<OhlcvBar> BuildDailySeries(string ticker, DateOnly start, int count, Func<int, decimal> closeAt, decimal halfRange = 1m, decimal volume = 1_000_000m)
    {
        var result = new List<OhlcvBar>(count);
        var day = start;
        for (var i = 0; i < count; i++)
        {
            while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) day = day.AddDays(1);
            var close = closeAt(i);
            result.Add(DailyBar(ticker, day, close, close + halfRange, Math.Max(0.5m, close - halfRange), close, volume));
            day = day.AddDays(1);
        }
        return result;
    }

    private static List<OhlcvBar> BuildSqueezeBreakdownBars(bool compressed, decimal triggerClose, decimal triggerHigh, decimal triggerLow)
    {
        // 90 weekday bars: when compressed, bars 0..69 oscillate +/- 4m while bars 70..88 tighten to +/- 0.10m so prior
        // bar 88 has minimum BB width rank; when wide, bars 0..69 oscillate +/- 1.5m while bars 70..88 widen to +/- 4m
        // (close on bar 88 = 104m so triggerLow 95m stays > 0.90 * 104m = 93.6m and does not trip SEC Rule 201).
        var bars = BuildDailySeries("SQZ", new DateOnly(2026, 1, 5), 90, i =>
        {
            if (i < 70) return 100m + (i % 2 == 0 ? (compressed ? 4m : 1.5m) : (compressed ? -4m : -1.5m));
            return compressed ? 100m + (i % 2 == 0 ? 0.10m : -0.10m) : 100m + (i % 2 == 0 ? 4m : -4m);
        }, halfRange: 0.4m, volume: 1_000_000m);

        bars[^1] = bars[^1] with { Open = 98m, High = triggerHigh, Low = triggerLow, Close = triggerClose, Volume = 2_500_000m };
        return bars;
    }

    private static List<OhlcvBar> BuildBearishOrbFiveMinuteBars(string ticker, IReadOnlyList<OhlcvBar> daily, bool staysAtOrBelowVwap)
    {
        var bars = new List<OhlcvBar>();
        // Populate prior 21 sessions + signal day with regular-session 5m bars (09:30-16:00 NY).
        var startIdx = Math.Max(0, daily.Count - 22);
        for (var idx = startIdx; idx < daily.Count; idx++)
        {
            var day = ToNewYorkDate(daily[idx].Timestamp);
            var isSignalDay = idx == daily.Count - 1;
            var openUtc = ToNewYorkUtc(day, new TimeOnly(9, 30));
            for (var m = 0; m < 78; m++)
            {
                var start = openUtc.AddMinutes(m * 5);
                if (!isSignalDay)
                {
                    bars.Add(new OhlcvBar(ticker, start, "5m", 100m, 100.3m, 99.7m, 100m, 10_000m, DataFeed: "sip", AdjustmentPolicy: "raw"));
                    continue;
                }

                // Signal day: first 6 bars (09:30-10:00) have 3x baseline volume (30k each -> RVOL = 3.0 >= 2.0),
                // falling monotonically from 95 -> 92.5 with close near bar low so every bar closes <= running VWAP
                // (unless staysAtOrBelowVwap == false, which spikes bar 4 above running VWAP).
                var basePrice = m < 6 ? 95m - (m * 0.45m) : 92m - Math.Min(0.5m, (m - 6) * 0.005m);
                var high = basePrice + 0.2m;
                var low = basePrice - 0.2m;
                var close = basePrice - 0.15m;
                if (!staysAtOrBelowVwap && m == 4)
                {
                    high = 96.5m;
                    close = 96.0m;
                }
                bars.Add(new OhlcvBar(ticker, start, "5m", basePrice, high, low, close, m < 6 ? 30_000m : 15_000m, DataFeed: "sip", AdjustmentPolicy: "raw"));
            }
        }
        return bars;
    }

    private static List<OhlcvBar> BuildEma21RejectionBars(bool bounceHighAboveSma50, bool dryBounceVolume, bool includeOutlierTrBar)
    {
        // 85 weekday bars trending steadily downward so Close < SMA(50) and SMA(50) is falling over 20 bars.
        var bars = BuildDailySeries("REJ", new DateOnly(2026, 1, 5), 85, i => 200m - i * 0.8m, halfRange: 1.0m, volume: 1_000_000m);
        if (includeOutlierTrBar)
        {
            // One wide-range bar inside the 14-bar ATR/MTR window (bar 76) inflates Wilder ATR(14) without inflating MTR-14.
            bars[76] = bars[76] with { High = bars[76].Close + 12m, Low = bars[76].Close - 12m };
        }

        // Compute current EMA(21) and SMA(50) at bar 83 to place a precise weak-volume bounce into the EMA(21) zone.
        var preSnaps = new IndicatorEngine().Compute(bars);
        var ema21Bounce = preSnaps[83].Ema21!.Value;
        var sma50Bounce = preSnaps[83].Sma50!.Value;

        var bounceHigh = bounceHighAboveSma50 ? sma50Bounce + 2.0m : ema21Bounce + 0.2m;
        var bounceClose = ema21Bounce - 0.3m;
        bars[83] = bars[83] with
        {
            Open = bounceClose - 0.4m,
            High = bounceHigh,
            Low = bounceClose - 1.0m,
            Close = bounceClose,
            Volume = dryBounceVolume ? 500_000m : 1_500_000m
        };

        // Trigger bar 84 closes strictly below EMA(21)_84 and strictly below Low_83 (and stays <= SMA50, no Rule 201).
        var triggerClose = bars[83].Low - 1.5m;
        bars[84] = bars[84] with
        {
            Open = bars[83].Close,
            High = bars[83].Close + 0.3m,
            Low = triggerClose - 0.2m,
            Close = triggerClose,
            Volume = 1_200_000m
        };
        return bars;
    }

    private static List<OhlcvBar> BuildParabolicBars(decimal shootingStarHigh, decimal shootingStarOpen, decimal shootingStarClose, decimal shootingStarLow, decimal triggerClose)
    {
        // 40 flat weekday bars around 100 with halfRange = 1 -> ATR(14) ~= 2.0, EMA(21) ~= 100.
        // Bar 38 spikes high = 120 (>= 100 + 2.5 * 2 = 105) with shooting star geometry; bar 39 breaks below bar 38 low.
        var bars = BuildDailySeries("PARA", new DateOnly(2026, 3, 2), 40, _ => 100m, halfRange: 1m, volume: 1_000_000m);
        bars[38] = bars[38] with
        {
            Open = shootingStarOpen,
            High = shootingStarHigh,
            Low = shootingStarLow,
            Close = shootingStarClose,
            Volume = 2_000_000m
        };
        bars[39] = bars[39] with
        {
            Open = shootingStarLow + 0.2m,
            High = shootingStarLow + 0.4m,
            Low = triggerClose - 0.3m,
            Close = triggerClose,
            Volume = 1_500_000m
        };
        return bars;
    }

    private static Dictionary<DateOnly, MarketSessionSchedule> BuildWeekdaySchedules(DateOnly start, DateOnly end)
    {
        var map = new Dictionary<DateOnly, MarketSessionSchedule>();
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            var isWeekday = d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
            map[d] = new MarketSessionSchedule(d, isWeekday, new TimeOnly(9, 30), new TimeOnly(16, 0));
        }
        return map;
    }

    private static DateTimeOffset ToNewYorkUtc(DateOnly day, TimeOnly time)
    {
        var local = day.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, NewYork.GetUtcOffset(local)).ToUniversalTime();
    }

    // Builds complete 5-minute regular-session bars from signalIndex onward, optionally injecting an intraday stop-touch spike.
    private static List<OhlcvBar> BuildFiveMinuteSeries(
        string ticker,
        IReadOnlyList<OhlcvBar> daily,
        int startIndex,
        int? spikeBarIndex,
        int? spikeSliceIndex)
    {
        var fineBars = new List<OhlcvBar>();
        for (var idx = startIndex; idx < daily.Count; idx++)
        {
            var day = ToNewYorkDate(daily[idx].Timestamp);
            var openUtc = ToNewYorkUtc(day, new TimeOnly(9, 30));
            for (var m = 0; m < 78; m++)
            {
                var start = openUtc.AddMinutes(m * 5);
                var spike = spikeBarIndex == idx && spikeSliceIndex == m;
                fineBars.Add(new OhlcvBar(
                    ticker,
                    start,
                    "5m",
                    spike ? 100.5m : 100m,
                    spike ? 105m : 100.2m,
                    spike ? 100m : 99.8m,
                    spike ? 104m : 100m,
                    200_000m,
                    DataFeed: "sip",
                    AdjustmentPolicy: "raw"));
            }
        }

        return fineBars;
    }

    private static async Task<BacktestResult> RunSyntheticBacktestAsync(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> dailyBars,
        IReadOnlyList<OhlcvBar> fineBars,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule> schedules,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        string? replayPolicyId,
        DateTimeOffset? precommittedLiquidationSourceUtc = null)
    {
        var (result, _) = await RunSyntheticBacktestWithConfigAsync(
            strategy, dailyBars, fineBars, schedules, windowStart, windowEnd, replayPolicyId, precommittedLiquidationSourceUtc);
        return result;
    }

    // Executes an isolated synthetic backtest through BacktestRunner and returns both the result and frozen run config.
    private static async Task<(BacktestResult Result, BacktestRunConfig Config)> RunSyntheticBacktestWithConfigAsync(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> dailyBars,
        IReadOnlyList<OhlcvBar> fineBars,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule> schedules,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        string? replayPolicyId,
        DateTimeOffset? precommittedLiquidationSourceUtc = null,
        IBacktestOpportunityObserver? opportunityObserver = null)
    {
        using var tmp = new TemporaryFolder();
        var strategyPath = Path.Combine(tmp.Path, "strategy.yaml");
        File.WriteAllText(strategyPath, RunConfigWriter.SerializeStrategyYaml(strategy));
        var intervals = fineBars.Count > 0 ? new[] { "1d", "5m" } : new[] { "1d" };
        var ticker = dailyBars[0].Ticker;

        var run = new BacktestRunConfig(
            "short-test",
            "backtest",
            new EngineConfig(
                "tpl", 1, 64, 20, 30, true,
                PrecommittedLiquidationSourceUtc: precommittedLiquidationSourceUtc,
                ExecutionReplayPolicyId: replayPolicyId),
            new TimeWindowConfig("fixed", 0, windowStart, windowEnd, WarmupLookbackDays: 90),
            [ticker],
            "csv",
            intervals,
            Path.Combine(tmp.Path, "raw"),
            Path.Combine(tmp.Path, "norm"),
            Path.Combine(tmp.Path, "results"),
            "use_cache",
            new DerivedTimeframeConfig("1d"),
            new ValidationConfig(
                new OutOfSampleConfig(false, 0m),
                new WalkForwardConfig(false, 0, 0),
                new BenchmarkConfig(false, "SPY"),
                new DataQualityConfig(false, 0, 0, 100m),
                new BiasRiskConfig("static", null, "raw")),
            new ProviderConfig(new AlpacaProviderConfig("sip")),
            new PortfolioConfig(100_000m, 0.5m, 20m, 5, 0m, 0m, 1, true),
            new SignalSourceConfig("internal_candles", false, string.Empty, 300, 0),
            new ExecutionConfig("simulated", "none", true, false, "market", "day", "market", false),
            new NewsConfig(false, "none", 60, -0.5m, 10, 3),
            new ScreenerConfig(false, "finviz", []),
            new ArtifactRetentionConfig("full"),
            [strategyPath],
            Universe: UniverseConfig.StaticUniverse with
            {
                Mode = UniverseConfig.ResolvedSnapshotMode,
                ResolvedSource = "wishlist",
                WishlistName = "synthetic-short-test",
                SelectedSourceTickers = [ticker],
                ResolvedAtUtc = windowStart
            });

        var repoRoot = TestRepository.FindRoot();
        var reader = new SimpleYamlReader();
        var catalog = new StrategyArtifactCatalog(repoRoot, Path.Combine(repoRoot, "configs", "strategy-catalog.json"), reader);
        var baseArtifact = catalog.GetSnapshot().ExecutableArtifacts
            .First(a => a.Identity.StrategyId == "technical.bollinger-squeeze-breakdown");
        var derived = catalog.CreateDerivedArtifact(baseArtifact, strategy, "1.0.1-test");
        var runtime = new AuthorizedRuntimeStrategy(
            derived.Identity,
            StrategySelectionMode.Backtest,
            derived.ResolvedStrategy,
            derived.AdmissionProfile);

        var provider = new InMemoryMarketDataProvider(dailyBars.Concat(fineBars).ToArray(), schedules);
        var runner = new BacktestRunner(
            reader,
            catalog,
            marketDataProviderFactory: _ => provider,
            opportunityObserver: opportunityObserver);
        var result = await runner.RunValidatedAsync(run, [runtime], DateTimeOffset.UtcNow, Path.Combine(run.ResultsRoot, "result.json"), CancellationToken.None);
        return (result, run);
    }

    private sealed class CapturingOpportunityObserver : IBacktestOpportunityObserver
    {
        public BacktestOpportunitySnapshot? Snapshot { get; private set; }

        public Task ObserveAsync(BacktestOpportunitySnapshot snapshot, CancellationToken cancellationToken)
        {
            Snapshot = snapshot;
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryMarketDataProvider(
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule> schedules)
        : IMarketDataProvider, IMarketSessionScheduleProvider
    {
        public string ProviderName => "in-memory-short-test";

        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> intervals,
            DateTimeOffset start,
            DateTimeOffset end,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var tickerSet = tickers.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var intervalSet = intervals.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var bar in bars)
            {
                if (tickerSet.Contains(bar.Ticker) && intervalSet.Contains(bar.Timeframe) &&
                    bar.Timestamp >= start && bar.Timestamp < end)
                {
                    yield return bar;
                }
            }
            await Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>> LoadMarketSessionSchedulesAsync(
            DateOnly startDate,
            DateOnly endDate,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>>(
                schedules.Where(p => p.Key >= startDate && p.Key <= endDate).ToDictionary(p => p.Key, p => p.Value));
    }
}
