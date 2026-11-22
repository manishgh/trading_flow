using TradingFlow.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Risk;
using TradingFlow.Engine.Strategies;
using TradingFlow.Engine.Universe;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

/// <summary>
/// Focused tests for Batch 1 (Checkpoints 5 &amp; 6 + shared strategy-owned ADR%/dollar-volume filter):
/// EMA-21, MTR-14, MaxTr14, rising SMA-50, strategy-owned 20-session ADR%/dollar-volume parity with DailyScreen,
/// ema21_pullback_reclaim (ATR-14 control and V2-01 MTR-14 treatment), combined Bollinger + RSI(2) rubber-band reclaim
/// with 48h fresh-news veto, YAML validation, RunConfigWriter round-trip, and catalog lineage.
/// </summary>
public sealed class SwingLongArchetypesTests
{
    private static readonly string Root = TestRepository.FindRoot();
    private const string Ema21ControlPath = "configs/backtest/strategies/ema21-trend-pullback.bt-v1.yaml";
    private const string Ema21Mtr14Path = "configs/backtest/strategies/ema21-trend-pullback-mtr14.bt-v1.yaml";
    private const string RubberBandPath = "configs/backtest/strategies/rubber-band-reclaim.bt-v1.yaml";

    // Verifies EMA-21 warmup, exact median of 14 sorted True Ranges (average of 7th and 8th), and gap-inflation
    // resistance where a single shock bar inflates Wilder ATR(14) and MaxTr14 (> 3 * Mtr14) without moving Mtr14.
    [Fact]
    public void IndicatorEngine_ComputesEma21Mtr14AndMaxTr14_WithExactMedianAndGapInflationDetection()
    {
        var engine = new IndicatorEngine();

        // 1) Distinct True Ranges 1..14 so sorted window[6]=7, window[7]=8 -> Mtr14 = 7.5, MaxTr14 = 14.
        var ascendingBars = new List<OhlcvBar> { DailyBar(0, 100m, 100m, 100m, 100m, 1_000_000m) };
        for (var i = 1; i <= 24; i++)
        {
            var range = i <= 14 ? (decimal)i : 5m;
            ascendingBars.Add(DailyBar(i, 100m, 100m + range, 100m, 100m, 1_000_000m));
        }

        var ascendingSnapshots = engine.Compute(ascendingBars);
        for (var i = 0; i < 14; i++)
        {
            Assert.Null(ascendingSnapshots[i].Mtr14);
            Assert.Null(ascendingSnapshots[i].MaxTr14);
        }

        Assert.Equal(7.5m, ascendingSnapshots[14].Mtr14);
        Assert.Equal(14m, ascendingSnapshots[14].MaxTr14);
        Assert.False(ascendingSnapshots[14].MaxTr14 > 3m * ascendingSnapshots[14].Mtr14);
        Assert.Null(ascendingSnapshots[19].Ema21);
        Assert.Equal(100m, ascendingSnapshots[20].Ema21);

        // 2) Gap-shock scenario: 13 calm bars (TR = 4) + 1 overnight gap shock (TR = 24).
        // MTR-14 stays 4.0 while Wilder ATR(14) jumps above 5.4 and MaxTr14 = 24 > 3 * 4.
        var shockBars = new List<OhlcvBar> { DailyBar(0, 100m, 102m, 98m, 100m, 1_000_000m) };
        for (var i = 1; i <= 13; i++)
        {
            shockBars.Add(DailyBar(i, 100m, 102m, 98m, 100m, 1_000_000m));
        }

        shockBars.Add(DailyBar(14, 120m, 124m, 118m, 122m, 1_000_000m));
        var shockSnapshots = engine.Compute(shockBars);
        Assert.Equal(4m, shockSnapshots[14].Mtr14);
        Assert.Equal(24m, shockSnapshots[14].MaxTr14);
        Assert.True(shockSnapshots[14].MaxTr14 > 3m * shockSnapshots[14].Mtr14);
        Assert.True(shockSnapshots[14].Atr > shockSnapshots[14].Mtr14);
    }

    // The two ADR% methods on a falling stock: dividing earlier, higher-priced ranges by the lower latest close overstates
    // the range; the per-day method measures each day against its own low. Hand-checked: 10 days 110/100 then 10 days 55/50.
    [Fact]
    public void AverageRangeMethods_MeasureAFallingStockAsDocumented()
    {
        var bars = Enumerable.Range(0, 20)
            .Select(i => i < 10 ? DailyBar(i, 105m, 110m, 100m, 100m, 1_000_000m) : DailyBar(i, 52m, 55m, 50m, 50m, 1_000_000m))
            .ToArray();

        Assert.Equal(15m, SignalGenerator.ComputeAverageRangeAndDollarVolume(bars, 19).AverageRangePct);
        Assert.Equal(15m, SignalGenerator.ComputeAverageRangeAndDollarVolume(bars, 19, 20, AverageRangeMethods.LatestClose).AverageRangePct);
        var perDay = SignalGenerator.ComputeAverageRangeAndDollarVolume(bars, 19, 20, AverageRangeMethods.PerDay);
        Assert.Equal(10m, perDay.AverageRangePct);
        Assert.Equal(75_000_000m, perDay.AverageDollarVolume);

        // A day without a usable low has no per-day range, so the whole measure is unavailable.
        bars[5] = DailyBar(5, 105m, 110m, 0m, 100m, 1_000_000m);
        Assert.Null(SignalGenerator.ComputeAverageRangeAndDollarVolume(bars, 19, 20, AverageRangeMethods.PerDay).AverageRangePct);
        Assert.Throws<ArgumentException>(() => SignalGenerator.ComputeAverageRangeAndDollarVolume(bars, 19, 20, "median"));
    }

    // The method is opt-in: no catalogued strategy sets it, an unset method leaves the serialized rules unchanged, and a
    // strategy that sets it uses it for its signal and round-trips through the run writer's YAML setting.
    [Fact]
    public void AverageRangeMethod_IsOptInAndAbsentFromExistingStrategies()
    {
        var reader = new SimpleYamlReader();
        foreach (var path in Directory.GetFiles(Path.Combine(Root, "configs", "backtest", "strategies"), "*.yaml"))
            Assert.Null(reader.ReadStrategy(path).EntryRules.AverageRangeMethod);

        var control = reader.ReadStrategy(Path.Combine(Root, Ema21ControlPath));
        Assert.DoesNotContain("AverageRangeMethod", System.Text.Json.JsonSerializer.Serialize(control.EntryRules), StringComparison.Ordinal);

        var perDay = control with { EntryRules = control.EntryRules with { AverageRangeMethod = AverageRangeMethods.PerDay } };
        var bars = Enumerable.Range(0, 20)
            .Select(i => i < 10 ? DailyBar(i, 105m, 110m, 100m, 100m, 1_000_000m) : DailyBar(i, 52m, 55m, 50m, 50m, 1_000_000m))
            .ToArray();
        var snapshots = bars.Select((bar, index) => Snapshot(index, bar.Close, atr: 2m)).ToArray();
        Assert.Equal(15m, new SignalGenerator().CreateTradeSignal(control, bars, snapshots, 19)!.AverageRangePct);
        Assert.Equal(10m, new SignalGenerator().CreateTradeSignal(perDay, bars, snapshots, 19)!.AverageRangePct);

        var written = Path.Combine(Path.GetTempPath(), $"tf-adr-{Guid.NewGuid():N}.yaml");
        try
        {
            File.WriteAllText(written, RunConfigWriter.SerializeStrategyYaml(perDay));
            Assert.Equal(AverageRangeMethods.PerDay, reader.ReadStrategy(written).EntryRules.AverageRangeMethod);
            File.WriteAllText(written, RunConfigWriter.SerializeStrategyYaml(control));
            Assert.Null(reader.ReadStrategy(written).EntryRules.AverageRangeMethod);
        }
        finally
        {
            File.Delete(written);
        }

        AssertInvalidYamlEdit(Ema21ControlPath, "  min_average_range_pct: 2.5", "  min_average_range_pct: 2.5\n  average_range_method: median");
        AssertInvalidYamlEdit(Ema21ControlPath, "  min_average_range_pct: 2.5", "  average_range_method: per_day");
    }

    // Verifies that SignalGenerator.ComputeAverageRangeAndDollarVolume matches DailyScreen.Evaluate exactly,
    // fails closed with fewer than 20 completed bars, and BasicStrategyEvaluator enforces both floors.
    [Fact]
    public void AverageRangeAndDollarVolume_MatchesDailyScreenAndEnforcesStrategyOwnedFilter()
    {
        var bars = Enumerable.Range(0, 20)
            .Select(i => DailyBar(i, 100m, 103m, 99m, 100m, 250_000m))
            .ToArray();
        var (adrPct, avgDollarVolume) = SignalGenerator.ComputeAverageRangeAndDollarVolume(bars, 19);
        var screen = DailyScreen.Evaluate(
            "TEST",
            bars,
            sessionOpen: 100m,
            new DailyScreenRules(
                MinPrice: 5m,
                AverageLookbackSessions: 20,
                MinAverageDollarVolume: 20_000_000m,
                MinAverageRangePercent: 2.5m));

        Assert.True(screen.Eligible);
        Assert.Equal(screen.Values!.AverageRangePercent, adrPct);
        Assert.Equal(screen.Values.AverageDollarVolume, avgDollarVolume);
        Assert.Equal(4m, adrPct);
        Assert.Equal(25_000_000m, avgDollarVolume);

        // Fewer than 20 bars fails closed (returns nulls).
        var shortResult = SignalGenerator.ComputeAverageRangeAndDollarVolume(bars, 18);
        Assert.Null(shortResult.AverageRangePct);
        Assert.Null(shortResult.AverageDollarVolume);

        var generator = new SignalGenerator();
        var evaluator = new BasicStrategyEvaluator();
        var baseStrategy = new SimpleYamlReader().ReadStrategy(Path.Combine(Root, Ema21ControlPath)) with
        {
            EntryRules = new SimpleYamlReader().ReadStrategy(Path.Combine(Root, Ema21ControlPath)).EntryRules with
            {
                SetupType = "momentum",
                PullbackLookbackBars = 0,
                MaxEma21PullbackAtr = null,
                MaxPullbackRelativeVolume = null,
                RequirePullbackLowAboveSma50 = false,
                RequirePriceAboveSma50 = false,
                Sma50RisingLookbackBars = 0
            }
        };

        var snapshots = bars.Select((b, idx) => Snapshot(idx, b.Close, atr: 2m)).ToArray();

        // Low ADR% (1.0% < 2.5%) is rejected.
        var lowAdrBars = Enumerable.Range(0, 20)
            .Select(i => DailyBar(i, 100m, 100.5m, 99.5m, 100m, 300_000m))
            .ToArray();
        var lowAdrSignal = generator.CreateTradeSignal(baseStrategy, lowAdrBars, snapshots, 19)!;
        Assert.Equal(1m, lowAdrSignal.AverageRangePct);
        Assert.StartsWith("average_range_pct_below_minimum", evaluator.GetLongEntryRejection(baseStrategy, lowAdrSignal, 1m));

        // Low average dollar volume ($10M < $20M) is rejected.
        var lowDvBars = Enumerable.Range(0, 20)
            .Select(i => DailyBar(i, 100m, 103m, 99m, 100m, 100_000m))
            .ToArray();
        var lowDvSignal = generator.CreateTradeSignal(baseStrategy, lowDvBars, snapshots, 19)!;
        Assert.Equal(10_000_000m, lowDvSignal.AverageDollarVolume);
        Assert.StartsWith("average_dollar_volume_below_minimum", evaluator.GetLongEntryRejection(baseStrategy, lowDvSignal, 1m));

        // Passing ADR% (4%) and dollar volume ($25M) succeeds.
        var passingSignal = generator.CreateTradeSignal(baseStrategy, bars, snapshots, 19)!;
        Assert.Null(evaluator.GetLongEntryRejection(baseStrategy, passingSignal, 1m));

        // Strategies without strategy-owned liquidity floors leave those fields null on TradeSignal.
        var noFilterStrategy = baseStrategy with
        {
            EntryRules = baseStrategy.EntryRules with
            {
                MinAverageRangePct = null,
                MinAverageDollarVolume = null
            }
        };
        var noFilterSignal = generator.CreateTradeSignal(noFilterStrategy, bars, snapshots, 19)!;
        Assert.Null(noFilterSignal.AverageRangePct);
        Assert.Null(noFilterSignal.AverageDollarVolume);
    }

    // Verifies sma50_rising_lookback_bars rejects flat, falling, or missing SMA-50 history and accepts strictly rising SMA-50.
    [Fact]
    public void Sma50RisingLookback_RejectsFlatOrFallingSma50AndAcceptsRising()
    {
        var strategy = new SimpleYamlReader().ReadStrategy(Path.Combine(Root, Ema21ControlPath)) with
        {
            EntryRules = new SimpleYamlReader().ReadStrategy(Path.Combine(Root, Ema21ControlPath)).EntryRules with
            {
                SetupType = "momentum",
                PullbackLookbackBars = 0,
                MaxEma21PullbackAtr = null,
                MaxPullbackRelativeVolume = null,
                RequirePullbackLowAboveSma50 = false,
                MinAverageRangePct = null,
                MinAverageDollarVolume = null,
                RequirePriceAboveSma50 = true,
                Sma50RisingLookbackBars = 20
            }
        };

        var bars = Enumerable.Range(0, 25).Select(i => DailyBar(i, 100m, 103m, 99m, 105m, 300_000m)).ToArray();
        var generator = new SignalGenerator();
        var evaluator = new BasicStrategyEvaluator();

        // Flat SMA-50 (100 at bar 4 and 100 at bar 24) fails closed with sma50_not_rising.
        var flatSnapshots = Enumerable.Range(0, 25).Select(i => Snapshot(i, 105m, atr: 2m, sma50: 100m)).ToArray();
        var flatSignal = generator.CreateTradeSignal(strategy, bars, flatSnapshots, 24)!;
        Assert.False(flatSignal.IsSma50Rising);
        Assert.Equal("sma50_not_rising", evaluator.GetLongEntryRejection(strategy, flatSignal, 1m));

        // Strictly rising SMA-50 (95 at bar 4, 100 at bar 24) passes.
        var risingSnapshots = Enumerable.Range(0, 25).Select(i => Snapshot(i, 105m, atr: 2m, sma50: 95m + (i >= 24 ? 5m : 0m))).ToArray();
        var risingSignal = generator.CreateTradeSignal(strategy, bars, risingSnapshots, 24)!;
        Assert.True(risingSignal.IsSma50Rising);
        Assert.Null(evaluator.GetLongEntryRejection(strategy, risingSignal, 1m));
    }

    // Verifies ema21_pullback_reclaim triggers on a clean low-volume pullback into 0.5*ATR of EMA-21 above SMA-50
    // followed by a reclaim close > EMA-21 and > prior high, and rejects every required negative condition explicitly.
    [Fact]
    public void Ema21PullbackReclaim_TriggersOnValidDryUpPullbackAndRejectsEachFailureCase()
    {
        var strategy = new SimpleYamlReader().ReadStrategy(Path.Combine(Root, Ema21ControlPath));
        var generator = new SignalGenerator();
        var evaluator = new BasicStrategyEvaluator();

        static (OhlcvBar[] Bars, IndicatorSnapshot[] Snapshots) BuildBaseSeries()
        {
            var bars = new OhlcvBar[25];
            var snapshots = new IndicatorSnapshot[25];
            for (var i = 0; i < 25; i++)
            {
                bars[i] = DailyBar(i, 105m, 108m, 104m, 106m, 300_000m);
                snapshots[i] = Snapshot(
                    i,
                    close: 106m,
                    atr: 2m,
                    sma50: i < 20 ? 95m : 100m,
                    ema21: 105m,
                    relativeVolume: 1.0m,
                    mtr14: 1.5m);
            }

            // Pullback bar (index 23): low 105.4 <= EMA21 (105) + 0.5 * ATR (2) = 106.0; low >= SMA50 (100); RVOL 0.60 < 0.85; high 107.0.
            bars[23] = DailyBar(23, 106.5m, 107.0m, 105.4m, 105.8m, 180_000m);
            snapshots[23] = snapshots[23] with { CurrentPrice = 105.8m, RelativeVolume = 0.60m };

            // Trigger bar (index 24): low 105.5 >= SMA50 (100), close 108.5 > EMA21 (105) and > prior high (107.0).
            bars[24] = DailyBar(24, 106.0m, 109.0m, 105.5m, 108.5m, 300_000m);
            snapshots[24] = snapshots[24] with { CurrentPrice = 108.5m, RelativeVolume = 1.10m };
            return (bars, snapshots);
        }

        // Positive case passes completely.
        var (okBars, okSnaps) = BuildBaseSeries();
        var okSignal = generator.CreateTradeSignal(strategy, okBars, okSnaps, 24)!;
        Assert.True(okSignal.IsEma21PullbackReclaim);
        Assert.Null(okSignal.Ema21PullbackRejectionDetail);
        Assert.Null(evaluator.GetLongEntryRejection(strategy, okSignal, 1.10m));

        // Failure 1: pullback never reaches within 0.5 * ATR of EMA-21 (all prior lows 107 > 106).
        var (noTouchBars, noTouchSnaps) = BuildBaseSeries();
        for (var i = 19; i <= 23; i++)
        {
            noTouchBars[i] = DailyBar(i, 107.5m, 108.0m, 107.0m, 107.5m, 300_000m);
            noTouchSnaps[i] = noTouchSnaps[i] with { CurrentPrice = 107.5m, RelativeVolume = 0.60m };
        }
        noTouchBars[24] = DailyBar(24, 107.5m, 110.0m, 107.2m, 109.5m, 300_000m);
        noTouchSnaps[24] = noTouchSnaps[24] with { CurrentPrice = 109.5m };
        var noTouchSignal = generator.CreateTradeSignal(strategy, noTouchBars, noTouchSnaps, 24)!;
        Assert.False(noTouchSignal.IsEma21PullbackReclaim);
        Assert.Contains("no_ema21_zone_pullback_in_lookback", evaluator.GetLongEntryRejection(strategy, noTouchSignal, 1.10m)!);

        // Failure 2: pullback RVOL >= 0.85 (not dried up).
        var (highVolBars, highVolSnaps) = BuildBaseSeries();
        for (var i = 19; i <= 23; i++)
        {
            highVolSnaps[i] = highVolSnaps[i] with { RelativeVolume = 0.90m };
        }
        var highVolSignal = generator.CreateTradeSignal(strategy, highVolBars, highVolSnaps, 24)!;
        Assert.False(highVolSignal.IsEma21PullbackReclaim);
        Assert.Contains("pullback_volume_not_dried_up", evaluator.GetLongEntryRejection(strategy, highVolSignal, 1.10m)!);

        // Failure 3: pullback RVOL is missing -> fails closed.
        var (nullRvolBars, nullRvolSnaps) = BuildBaseSeries();
        for (var i = 19; i <= 23; i++)
        {
            nullRvolSnaps[i] = nullRvolSnaps[i] with { RelativeVolume = null };
        }
        var nullRvolSignal = generator.CreateTradeSignal(strategy, nullRvolBars, nullRvolSnaps, 24)!;
        Assert.False(nullRvolSignal.IsEma21PullbackReclaim);
        Assert.Contains("pullback_rvol_unavailable", evaluator.GetLongEntryRejection(strategy, nullRvolSignal, 1.10m)!);

        // Failure 4: pullback low undercuts SMA-50 (99.5 < 100.0).
        var (undercutBars, undercutSnaps) = BuildBaseSeries();
        undercutBars[23] = undercutBars[23] with { Low = 99.5m };
        var undercutSignal = generator.CreateTradeSignal(strategy, undercutBars, undercutSnaps, 24)!;
        Assert.False(undercutSignal.IsEma21PullbackReclaim);
        Assert.Contains("pullback_low_below_sma50_or_sma50_missing", evaluator.GetLongEntryRejection(strategy, undercutSignal, 1.10m)!);

        // Failure 5: trigger bar low undercuts SMA-50 (99.0 < 100.0).
        var (triggerLowBars, triggerLowSnaps) = BuildBaseSeries();
        triggerLowBars[24] = triggerLowBars[24] with { Low = 99.0m };
        var triggerLowSignal = generator.CreateTradeSignal(strategy, triggerLowBars, triggerLowSnaps, 24)!;
        Assert.False(triggerLowSignal.IsEma21PullbackReclaim);
        Assert.Contains("trigger_low_below_sma50", evaluator.GetLongEntryRejection(strategy, triggerLowSignal, 1.10m)!);

        // Failure 6: trigger bar does not close above prior bar's high (106.5 <= 107.0).
        var (noReclaimBars, noReclaimSnaps) = BuildBaseSeries();
        noReclaimBars[24] = noReclaimBars[24] with { Close = 106.5m };
        noReclaimSnaps[24] = noReclaimSnaps[24] with { CurrentPrice = 106.5m };
        var noReclaimSignal = generator.CreateTradeSignal(strategy, noReclaimBars, noReclaimSnaps, 24)!;
        Assert.False(noReclaimSignal.IsEma21PullbackReclaim);
        Assert.Contains("trigger_close_not_above_prior_high", evaluator.GetLongEntryRejection(strategy, noReclaimSignal, 1.10m)!);

        // Failure 7: trigger bar closes below EMA-21 (104.5 <= 105.0).
        var (belowEmaBars, belowEmaSnaps) = BuildBaseSeries();
        belowEmaBars[23] = belowEmaBars[23] with { High = 104.0m };
        belowEmaBars[24] = belowEmaBars[24] with { Close = 104.5m };
        belowEmaSnaps[24] = belowEmaSnaps[24] with { CurrentPrice = 104.5m };
        var belowEmaSignal = generator.CreateTradeSignal(strategy, belowEmaBars, belowEmaSnaps, 24)!;
        Assert.False(belowEmaSignal.IsEma21PullbackReclaim);
        Assert.Contains("trigger_close_not_above_ema21", evaluator.GetLongEntryRejection(strategy, belowEmaSignal, 1.10m)!);
    }

    // Verifies Experiment V2-01 (volatility_unit: mtr14): initial stop uses Mtr14 instead of Wilder ATR,
    // 2.5R target scales from Mtr14, missing Mtr14 fails closed, and trailing stop respects ResolveVolatilityUnit.
    [Fact]
    public void StrategyInitialStopResolver_Mtr14UsesMedianTrueRangeAndFailsClosedWhenUnavailable()
    {
        var reader = new SimpleYamlReader();
        var control = reader.ReadStrategy(Path.Combine(Root, Ema21ControlPath));
        var treatment = reader.ReadStrategy(Path.Combine(Root, Ema21Mtr14Path));
        var bars = new[] { DailyBar(0, 100m, 103m, 98m, 100m, 500_000m), DailyBar(1, 100m, 104m, 99m, 102m, 500_000m) };
        var snapshots = new[]
        {
            Snapshot(0, 100m, atr: 6.0m, mtr14: 3.0m, ema21: 99m, sma50: 95m),
            Snapshot(1, 102m, atr: 6.0m, mtr14: 3.0m, ema21: 100m, sma50: 96m)
        };

        var generator = new SignalGenerator();
        var controlSignal = generator.CreateTradeSignal(control, bars, snapshots, 1)!;
        var treatmentSignal = generator.CreateTradeSignal(treatment, bars, snapshots, 1)!;

        // Control leaves CurrentMtr14 null; treatment populates CurrentMtr14 = 3.0.
        Assert.Null(controlSignal.CurrentMtr14);
        Assert.Equal(3.0m, treatmentSignal.CurrentMtr14);

        var resolver = new StrategyInitialStopResolver();
        var controlStop = resolver.Resolve(new StrategyInitialStopRequest(
            control, controlSignal, PlannedOrderSide.Long, EntryPrice: 100m, StopContextIndex: 1, bars, snapshots));
        var treatmentStop = resolver.Resolve(new StrategyInitialStopRequest(
            treatment, treatmentSignal, PlannedOrderSide.Long, EntryPrice: 100m, StopContextIndex: 1, bars, snapshots));

        Assert.True(controlStop.IsResolved);
        Assert.Equal(94.0m, controlStop.StopPrice);
        Assert.Equal(6.0m, controlStop.StopDistance);

        Assert.True(treatmentStop.IsResolved);
        Assert.Equal(97.0m, treatmentStop.StopPrice);
        Assert.Equal(3.0m, treatmentStop.StopDistance);
        // 2.5R target derived from stopDistance = 100 + 2.5 * 3.0 = 107.5 (vs 115.0 for ATR control).
        Assert.Equal(107.5m, 100m + (treatmentStop.StopDistance!.Value * treatment.ExitRules.TargetRMultiple));

        // Missing Mtr14 fails closed with mtr14_unavailable_for_initial_stop.
        var missingMtrStop = resolver.Resolve(new StrategyInitialStopRequest(
            treatment, treatmentSignal with { CurrentMtr14 = null }, PlannedOrderSide.Long, EntryPrice: 100m, StopContextIndex: 1, bars, snapshots));
        Assert.False(missingMtrStop.IsResolved);
        Assert.Equal("mtr14_unavailable_for_initial_stop", missingMtrStop.RejectionReason);

        // Trailing stop calculation also honors volatility_unit: mtr14 via ExitRules.ResolveVolatilityUnit.
        var trailingTreatment = treatment with { ExitRules = treatment.ExitRules with { EnableAtrTrailingStop = true, TrailingStopAtrMultiple = 2.0m, TrailingActivationR = 1.0m } };
        var trailed = new TechnicalExecutionEngine().CalculateEffectiveStopLoss(
            trailingTreatment, snapshots[1], entryPrice: 100m, stopDistance: 3.0m, initialStopLossPrice: 97.0m, currentStopLossPrice: 97.0m, highestHighSinceEntry: 110m);
        Assert.Equal(104.0m, trailed); // 110 - 2 * 3.0 (MTR-14), not 110 - 2 * 6.0 (ATR-14).
    }

    // Verifies Checkpoint 6 (rubber-band reclaim): requires a stretch bar within 3 sessions satisfying BOTH
    // Close < BollingerLower AND Rsi2 < 10 (rejecting BB-only and RSI2-only), followed by close > prior high,
    // and enforces the 48-hour fresh-news veto regardless of sentiment.
    [Fact]
    public void RubberBandReclaim_RequiresCombinedBollingerAndRsi2StretchAndEnforcesFreshNewsVeto()
    {
        var strategy = new SimpleYamlReader().ReadStrategy(Path.Combine(Root, RubberBandPath));
        var generator = new SignalGenerator();
        var evaluator = new BasicStrategyEvaluator();

        static (OhlcvBar[] Bars, IndicatorSnapshot[] Snapshots) BuildSeries(decimal stretchClose, decimal stretchBbLower, decimal stretchRsi2)
        {
            var bars = new OhlcvBar[25];
            var snapshots = new IndicatorSnapshot[25];
            for (var i = 0; i < 25; i++)
            {
                bars[i] = DailyBar(i, 105m, 109m, 104m, 107m, 300_000m);
                snapshots[i] = Snapshot(
                    i,
                    close: 107m,
                    atr: 2m,
                    sma50: i < 20 ? 95m : 100m,
                    bollingerLower: 103m,
                    rsi2: 50m);
            }

            // Stretch bar at index 23.
            bars[23] = DailyBar(23, 104m, 105.0m, 100.5m, stretchClose, 300_000m);
            snapshots[23] = snapshots[23] with { CurrentPrice = stretchClose, BollingerLower = stretchBbLower, Rsi2 = stretchRsi2 };

            // Reclaim trigger bar at index 24: closes at 106.0 > prior high (105.0) and > SMA-50 (100.0).
            bars[24] = DailyBar(24, 102.5m, 107.0m, 102.0m, 106.0m, 300_000m);
            snapshots[24] = snapshots[24] with { CurrentPrice = 106.0m, BollingerLower = 103m, Rsi2 = 65m };
            return (bars, snapshots);
        }

        // BB-only stretch (Close 101 < BB lower 103, but RSI2 = 25 >= 10) -> rejected.
        var (bbOnlyBars, bbOnlySnaps) = BuildSeries(stretchClose: 101m, stretchBbLower: 103m, stretchRsi2: 25m);
        var bbOnlySignal = generator.CreateTradeSignal(strategy, bbOnlyBars, bbOnlySnaps, 24)!;
        Assert.False(bbOnlySignal.IsMeanReversionReclaim);
        Assert.Equal("setup_mean_reversion_reclaim_not_triggered", evaluator.GetLongEntryRejection(strategy, bbOnlySignal, 1m));

        // RSI2-only stretch (RSI2 = 5 < 10, but Close 104 >= BB lower 103) -> rejected.
        var (rsiOnlyBars, rsiOnlySnaps) = BuildSeries(stretchClose: 104m, stretchBbLower: 103m, stretchRsi2: 5m);
        var rsiOnlySignal = generator.CreateTradeSignal(strategy, rsiOnlyBars, rsiOnlySnaps, 24)!;
        Assert.False(rsiOnlySignal.IsMeanReversionReclaim);
        Assert.Equal("setup_mean_reversion_reclaim_not_triggered", evaluator.GetLongEntryRejection(strategy, rsiOnlySignal, 1m));

        // Combined BB + RSI2 stretch (Close 101 < 103 AND RSI2 = 5 < 10) + reclaim close 106 > 105 -> passes!
        var (bothBars, bothSnaps) = BuildSeries(stretchClose: 101m, stretchBbLower: 103m, stretchRsi2: 5m);
        var bothSignal = generator.CreateTradeSignal(strategy, bothBars, bothSnaps, 24)!;
        Assert.True(bothSignal.IsMeanReversionReclaim);
        Assert.Null(evaluator.GetLongEntryRejection(strategy, bothSignal, 1m));

        // Fresh news within 48 hours vetoes even when sentiment is positive.
        var freshNews = new CatalystEvent("TEST", bothSignal.Timestamp.AddHours(-12), CatalystType.NewsReport, "Guidance update", SentimentScore: 0.80m);
        var vetoedSignal = bothSignal with { Catalyst = freshNews, CatalystAgeHours = 12m };
        Assert.StartsWith("fresh_news_veto", evaluator.GetLongEntryRejection(strategy, vetoedSignal, 1m));

        // Older news (> 48 hours) does not veto.
        var staleNews = freshNews with { Timestamp = bothSignal.Timestamp.AddHours(-60) };
        var staleSignal = bothSignal with { Catalyst = staleNews, CatalystAgeHours = 60m };
        Assert.Null(evaluator.GetLongEntryRejection(strategy, staleSignal, 1m));
    }

    // Verifies all three frozen recipes parse cleanly and invalid combinations of the new YAML fields fail closed.
    [Fact]
    public void Recipes_ParseFrozenValuesAndRefuseInvalidConfigurations()
    {
        var reader = new SimpleYamlReader();
        var control = reader.ReadStrategy(Path.Combine(Root, Ema21ControlPath));
        var mtr14 = reader.ReadStrategy(Path.Combine(Root, Ema21Mtr14Path));
        var rubber = reader.ReadStrategy(Path.Combine(Root, RubberBandPath));

        Assert.Equal(("ema21_pullback_reclaim", 5, 0.5m, 0.85m, true, 20), (
            control.EntryRules.SetupType,
            control.EntryRules.PullbackLookbackBars,
            control.EntryRules.MaxEma21PullbackAtr,
            control.EntryRules.MaxPullbackRelativeVolume,
            control.EntryRules.RequirePullbackLowAboveSma50,
            control.EntryRules.Sma50RisingLookbackBars));
        Assert.Equal((2.5m, 20_000_000m), (control.EntryRules.MinAverageRangePct, control.EntryRules.MinAverageDollarVolume));
        Assert.False(control.ExitRules.UsesMtr14);
        Assert.True(mtr14.ExitRules.UsesMtr14);
        Assert.Equal("mtr14", mtr14.ExitRules.VolatilityUnit);
        Assert.Equal(70, BacktestRunner.LongestIndicatorLookback(control));

        Assert.Equal(("mean_reversion_reclaim", 3, true, 10.0m, true, 48.0m, 20), (
            rubber.EntryRules.SetupType,
            rubber.EntryRules.ReversionStretchLookbackBars,
            rubber.EntryRules.EnableLowerBollingerStretch,
            rubber.EntryRules.MaxReversionRsi2,
            rubber.EntryRules.RequireCombinedBollingerAndRsi2Stretch,
            rubber.EntryRules.VetoFreshNewsHours,
            rubber.EntryRules.Sma50RisingLookbackBars));
        Assert.Equal(70, BacktestRunner.LongestIndicatorLookback(rubber));

        AssertInvalidYamlEdit(Ema21Mtr14Path, "  volatility_unit: mtr14", "  volatility_unit: unknown");
        AssertInvalidYamlEdit(Ema21Mtr14Path, "  initial_stop_mode: atr", "  initial_stop_mode: swing_low");
        AssertInvalidYamlEdit(Ema21ControlPath, "  setup_type: ema21_pullback_reclaim", "  setup_type: momentum");
        AssertInvalidYamlEdit(Ema21ControlPath, "  pullback_lookback_bars: 5", "  pullback_lookback_bars: 0");
        AssertInvalidYamlEdit(Ema21ControlPath, "  min_average_range_pct: 2.5", "  min_average_range_pct: 0");
        AssertInvalidYamlEdit(Ema21ControlPath, "  min_average_dollar_volume: 20000000", "  min_average_dollar_volume: -1");
        AssertInvalidYamlEdit(Ema21ControlPath, "  sma50_rising_lookback_bars: 20", "  sma50_rising_lookback_bars: -5");
        AssertInvalidYamlEdit(RubberBandPath, "  enable_lower_bollinger_stretch: true", "  enable_lower_bollinger_stretch: false");
    }

    // Verifies RunConfigWriter round-trips all three new strategies cleanly, catalog lineage binds 1.1.0 to 1.0.0's
    // exact canonical ContentSha256, admission profile 2.5.0 is attached, and Bollinger squeeze's hash is unchanged.
    [Fact]
    public void RunConfigWriter_RoundTripsAllThreeNewStrategiesAndCatalogPreservesLineageAndExistingHashes()
    {
        var reader = new SimpleYamlReader();
        var catalog = new StrategyArtifactCatalog(Root, Path.Combine(Root, "configs", "strategy-catalog.json"), reader);
        var artifacts = catalog.GetSnapshot().ExecutableArtifacts;

        var control = Assert.Single(artifacts, a => a.Identity.StrategyId == "technical.ema21-trend-pullback" && a.Identity.SemanticVersion == "1.0.0");
        var treatment = Assert.Single(artifacts, a => a.Identity.StrategyId == "technical.ema21-trend-pullback" && a.Identity.SemanticVersion == "1.1.0");
        var rubber = Assert.Single(artifacts, a => a.Identity.StrategyId == "technical.rubber-band-reclaim" && a.Identity.SemanticVersion == "1.0.0");
        var squeeze = Assert.Single(artifacts, a => a.Identity.StrategyId == "technical.bollinger-squeeze-breakout" && a.Identity.SemanticVersion == "1.0.0");

        Assert.Equal("swing-daily-v2-as-traded-research-v1", control.AdmissionProfile.ProfileId);
        Assert.Equal("2.5.0", control.AdmissionProfile.Indicators.DerivedIndicatorProfileVersion);
        Assert.Equal("2.5.0", treatment.AdmissionProfile.Indicators.DerivedIndicatorProfileVersion);
        Assert.Equal("2.5.0", rubber.AdmissionProfile.Indicators.DerivedIndicatorProfileVersion);
        Assert.Equal("2.4.0", squeeze.AdmissionProfile.Indicators.DerivedIndicatorProfileVersion);

        Assert.Equal(control.Identity, treatment.DerivedFromIdentity);
        Assert.Equal(control.Identity.ContentSha256, treatment.DerivedFromContentSha256);
        Assert.NotEqual(control.Identity.ContentSha256, treatment.Identity.ContentSha256);

        // Existing Bollinger squeeze canonical hash must remain unchanged after adding new optional fields.
        Assert.Equal("ee56bf94bb160cbcd27dbde297cfe38ae366acb0911475d3ecee4f530ef9628d", squeeze.Identity.ContentSha256);

        foreach (var artifact in new[] { control, treatment, rubber })
        {
            var path = Path.Combine(Path.GetTempPath(), $"tf-swing-rt-{Guid.NewGuid():N}.yaml");
            try
            {
                File.WriteAllText(path, RunConfigWriter.SerializeStrategyYaml(artifact.ResolvedStrategy));
                var reloaded = reader.ReadStrategy(path);
                Assert.Equivalent(artifact.ResolvedStrategy, reloaded, strict: true);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    // Applies a single text replacement to a temporary copy of a recipe YAML and asserts SimpleYamlReader refuses it.
    private static void AssertInvalidYamlEdit(string relativePath, string original, string replacement)
    {
        var text = File.ReadAllText(Path.Combine(Root, relativePath));
        Assert.Contains(original, text, StringComparison.Ordinal);
        var path = Path.Combine(Path.GetTempPath(), $"tf-invalid-{Guid.NewGuid():N}.yaml");
        try
        {
            File.WriteAllText(path, text.Replace(original, replacement, StringComparison.Ordinal));
            Assert.Throws<InvalidOperationException>(() => new SimpleYamlReader().ReadStrategy(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Builds one completed daily OHLCV bar at sequential trading days for deterministic unit tests.
    private static OhlcvBar DailyBar(int dayIndex, decimal open, decimal high, decimal low, decimal close, decimal volume) => new(
        "TEST",
        new DateTimeOffset(2026, 1, 2, 5, 0, 0, TimeSpan.Zero).AddDays(dayIndex),
        "1d",
        open,
        high,
        low,
        close,
        volume);

    // Builds one completed daily IndicatorSnapshot with explicit values for deterministic evaluator/signal tests.
    private static IndicatorSnapshot Snapshot(
        int dayIndex,
        decimal close,
        decimal atr,
        decimal? sma50 = null,
        decimal? ema21 = null,
        decimal? bollingerLower = null,
        decimal? rsi2 = null,
        decimal? relativeVolume = 1.0m,
        decimal? mtr14 = null) => new(
        "TEST",
        new DateTimeOffset(2026, 1, 2, 5, 0, 0, TimeSpan.Zero).AddDays(dayIndex),
        "1d",
        close,
        300_000m,
        Vwap: null,
        Rsi: 50m,
        Atr: atr,
        Ema20: ema21,
        Ema50: sma50,
        Ema200: null,
        BollingerMiddle: bollingerLower is { } lower ? lower + 5m : null,
        BollingerUpper: bollingerLower is { } lower2 ? lower2 + 10m : null,
        BollingerLower: bollingerLower,
        RelativeVolume: relativeVolume,
        MacdLine: null,
        MacdSignal: null,
        MacdHistogram: null,
        Sma50: sma50,
        Rsi2: rsi2,
        Ema21: ema21,
        Mtr14: mtr14);
}
