using System.Text.Json;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Catalysts;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Strategies;
using TradingFlow.Engine.Universe;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

/// <summary>
/// Focused tests for Batch 2 — Checkpoint 4 (Swing ORB on Tier-1 news, Option B, <c>technical.news-orb-swing@1.0.0</c>):
/// daily sub-session 5-minute opening-range high/low, first-30m RVOL vs prior 20-session median, session VWAP,
/// opening-window VWAP containment, ATR-normalized opening gap using prior bar's ATR(14), conservative +5m receipt
/// delay on Tier-1 news with multi-story window evaluation and FinBERT negative veto, YAML validation, timeframe and
/// news capability discovery, RunConfigWriter round-trip, and unchanged canonical hashes.
/// </summary>
public sealed class NewsOrbSwingTests
{
    private static readonly string Root = TestRepository.FindRoot();
    private const string RecipePath = "configs/backtest/strategies/news-orb-swing.bt-v1.yaml";

    // Verifies exact 30m opening high/low, opening RVOL vs prior 20-session median, session VWAP,
    // opening VWAP containment, and strict exclusion of premarket, after-hours, and post-cutoff bars.
    [Fact]
    public void DailyOpeningRangeSummary_Computes30mHighLowRvolSessionVwapAndOpeningVwapContainment()
    {
        var ny = ResolveNewYork();
        var startDate = new DateOnly(2026, 6, 1);
        var fiveMinuteBars = new List<OhlcvBar>();

        // 20 prior sessions with opening volumes 100k, 110k, ..., 290k -> median = (190k + 200k) / 2 = 195k.
        for (var session = 0; session < 20; session++)
        {
            var date = startDate.AddDays(session);
            var perBarVolume = (100_000m + (session * 10_000m)) / 6m;
            fiveMinuteBars.AddRange(BuildOpeningSixBars(date, ny, open: 100m, high: 102m, low: 99m, close: 101m, perBarVolume));
        }

        // Day D (session 20): 6 opening bars totaling 585k (3.0x median 195k), high 105.0, low 101.0,
        // plus one afternoon regular-session bar, plus premarket/after-hours/future-known bars that must be ignored.
        var signalDate = startDate.AddDays(20);
        // Premarket 09:25 bar with extreme high 200 and huge volume — must be ignored.
        fiveMinuteBars.Add(FiveMinuteBar(ToUtc(signalDate, new TimeOnly(9, 25), ny), 190m, 200m, 185m, 195m, 9_000_000m));

        // 6 opening bars: typical price = (105 + 101 + 103) / 3 = 103m, total volume = 585_000m.
        // Bar closes at 103m <= running VWAP 103m -> OpeningStayedAtOrBelowVwap == true.
        fiveMinuteBars.AddRange(BuildOpeningSixBars(signalDate, ny, open: 102m, high: 105m, low: 101m, close: 103m, 97_500m));

        // Afternoon regular bar at 15:00 NY: typical price = (108 + 106 + 107) / 3 = 107m, volume = 585_000m.
        // Session VWAP = (103 * 585k + 107 * 585k) / (1_170k) = 105.0m.
        fiveMinuteBars.Add(FiveMinuteBar(ToUtc(signalDate, new TimeOnly(15, 0), ny), 106m, 108m, 106m, 107m, 585_000m));

        // After-hours 16:00 bar — must be ignored.
        fiveMinuteBars.Add(FiveMinuteBar(ToUtc(signalDate, new TimeOnly(16, 0), ny), 150m, 160m, 140m, 155m, 9_000_000m));

        // Future-known regular bar at 15:30 with KnownAtUtc after decisionTimeUtc — must be ignored.
        var decisionTimeUtc = ToUtc(signalDate, new TimeOnly(16, 0), ny);
        fiveMinuteBars.Add(FiveMinuteBar(
            ToUtc(signalDate, new TimeOnly(15, 30), ny),
            120m, 125m, 118m, 122m, 5_000_000m,
            knownAtUtc: decisionTimeUtc.AddMinutes(1)));

        var dailyBar = DailyBar(signalDate, ny, 102m, 108m, 101m, 107m, 1_170_000m);
        var evidence = DailyOpeningRangeSummary.Compute(dailyBar, fiveMinuteBars, 30, "America/New_York", decisionTimeUtc);

        Assert.True(evidence.IsComplete);
        Assert.Null(evidence.UnavailableReason);
        Assert.Equal(105m, evidence.OpeningRangeHigh);
        Assert.Equal(101m, evidence.OpeningRangeLow);
        Assert.Equal(585_000m, evidence.OpeningVolume);
        Assert.Equal(3.0m, evidence.OpeningRelativeVolume);
        Assert.Equal(105m, evidence.SessionVwap);
        Assert.True(evidence.OpeningStayedAtOrBelowVwap);
        Assert.Equal(20, evidence.BaselineSessionCount);
    }

    // Verifies DailyOpeningRangeSummary fails closed when 5m bars are missing, an opening slot is skipped,
    // or fewer than 20 prior sessions have complete opening bars.
    [Fact]
    public void DailyOpeningRangeSummary_FailsClosedOnMissingOpeningBarOrInsufficientBaselineSessions()
    {
        var ny = ResolveNewYork();
        var startDate = new DateOnly(2026, 6, 1);
        var signalDate = startDate.AddDays(20);
        var decisionTimeUtc = ToUtc(signalDate, new TimeOnly(16, 0), ny);
        var dailyBar = DailyBar(signalDate, ny, 102m, 108m, 101m, 107m, 500_000m);

        // 1) Empty/null 5m bars.
        var none = DailyOpeningRangeSummary.Compute(dailyBar, null, 30, "America/New_York", decisionTimeUtc);
        Assert.False(none.IsComplete);
        Assert.Equal("five_minute_bars_unavailable", none.UnavailableReason);

        // 2) Only 19 prior sessions (insufficient baseline).
        var nineteenSessions = new List<OhlcvBar>();
        for (var s = 1; s < 20; s++)
        {
            nineteenSessions.AddRange(BuildOpeningSixBars(startDate.AddDays(s), ny, 100m, 102m, 99m, 101m, 20_000m));
        }

        nineteenSessions.AddRange(BuildOpeningSixBars(signalDate, ny, 102m, 105m, 101m, 103m, 60_000m));
        var insufficient = DailyOpeningRangeSummary.Compute(dailyBar, nineteenSessions, 30, "America/New_York", decisionTimeUtc);
        Assert.False(insufficient.IsComplete);
        Assert.Equal("opening_range_baseline_sessions_insufficient", insufficient.UnavailableReason);
        Assert.Equal(19, insufficient.BaselineSessionCount);

        // 3) Missing one opening bar (09:40 NY) on signal day -> opening_range_five_minute_bars_incomplete.
        var fullPrior = new List<OhlcvBar>();
        for (var s = 0; s < 20; s++)
        {
            fullPrior.AddRange(BuildOpeningSixBars(startDate.AddDays(s), ny, 100m, 102m, 99m, 101m, 20_000m));
        }

        var partialOpening = BuildOpeningSixBars(signalDate, ny, 102m, 105m, 101m, 103m, 60_000m)
            .Where((_, idx) => idx != 2) // drop 09:40 slot
            .ToArray();
        fullPrior.AddRange(partialOpening);
        var incomplete = DailyOpeningRangeSummary.Compute(dailyBar, fullPrior, 30, "America/New_York", decisionTimeUtc);
        Assert.False(incomplete.IsComplete);
        Assert.Equal("opening_range_five_minute_bars_incomplete", incomplete.UnavailableReason);
    }

    // Verifies MinGapUpAtr uses strictly the prior completed bar's Close_{D-1} and ATR(14)_{D-1}, never current ATR_D.
    [Fact]
    public void MinGapUpAtr_UsesStrictlyPriorBarAtrAndFailsClosedWhenUnavailable()
    {
        var ny = ResolveNewYork();
        var d0 = new DateOnly(2026, 6, 1);
        var d1 = new DateOnly(2026, 6, 2);
        var bars = new[]
        {
            DailyBar(d0, ny, 99m, 101m, 98m, 100m, 300_000m),
            DailyBar(d1, ny, 102m, 106m, 101m, 105m, 400_000m)
        };
        // Prior ATR = 2.0 -> (102 - 100) / 2.0 = 1.0 (even though current bar ATR is inflated to 10.0).
        var snapshots = new[]
        {
            Snapshot(bars[0].Timestamp, 100m, atr: 2.0m),
            Snapshot(bars[1].Timestamp, 105m, atr: 10.0m)
        };

        Assert.Equal(1.0m, SignalGenerator.ComputeGapUpAtr(bars, snapshots, 1));
        Assert.Null(SignalGenerator.ComputeGapUpAtr(bars, snapshots, 0));
        Assert.Null(SignalGenerator.ComputeGapUpAtr(bars, [snapshots[0] with { Atr = null }, snapshots[1]], 1));
        Assert.Null(SignalGenerator.ComputeGapUpAtr(bars, [snapshots[0] with { Atr = 0m }, snapshots[1]], 1));
    }

    // Verifies +5m conservative receipt delay, multi-story window evaluation (later minor headline cannot hide earlier
    // Tier-1 earnings, earlier negative FinBERT story vetoes positive earnings), and category precision.
    [Fact]
    public void TierOneCatalystEvaluator_EnforcesReceiptDelayMultiStoryWindowAndFinbertVeto()
    {
        var allowed = TierOneCatalystEvaluator.ParseAllowedCategories("earnings,guidance,fda,contract,upgrade");
        var cutoff = new DateTimeOffset(2026, 6, 21, 20, 0, 0, TimeSpan.Zero); // 16:00 NY

        // 1) Receipt delay (+5m): updated 3m before cutoff is excluded; updated 5m before cutoff qualifies.
        var tooLate = new CatalystEvent(
            "ACME",
            Timestamp: cutoff.AddHours(-2),
            CatalystType.EarningsRelease,
            "ACME reports Q2 earnings and beats estimates",
            SentimentScore: 0.65m,
            UpdatedAt: cutoff.AddMinutes(-3));
        var tooLateResult = TierOneCatalystEvaluator.Evaluate("ACME", [tooLate], cutoff, 18m, 5, allowed, vetoSentimentAtOrBelow: -0.15m);
        Assert.False(tooLateResult.Qualified);
        Assert.Equal("tier1_catalyst_missing", tooLateResult.RejectionReason);

        var onTime = tooLate with { UpdatedAt = cutoff.AddMinutes(-5) };
        var onTimeResult = TierOneCatalystEvaluator.Evaluate("ACME", [onTime], cutoff, 18m, 5, allowed, vetoSentimentAtOrBelow: -0.15m);
        Assert.True(onTimeResult.Qualified);
        Assert.Equal("earnings", onTimeResult.MatchedCategory);
        Assert.Equal(onTime, onTimeResult.SelectedCatalyst);

        // In live/paper mode, EffectiveAvailableAtUtc = max(max(created, updated) + delay, observedUtc):
        // an observed receipt after published+5m is honored without double-adding delay, and a post-cutoff receipt blocks.
        var midObserved = onTime with { UpdatedAt = cutoff.AddMinutes(-10), DecisionAvailableAt = cutoff.AddMinutes(-2) };
        Assert.Equal(cutoff.AddMinutes(-2), TierOneCatalystEvaluator.EffectiveAvailableAtUtc(midObserved, 5));
        Assert.True(TierOneCatalystEvaluator.Evaluate("ACME", [midObserved], cutoff, 18m, 5, allowed, -0.15m).Qualified);

        var postCutoffObserved = onTime with { DecisionAvailableAt = cutoff.AddMinutes(2) };
        Assert.Equal(cutoff.AddMinutes(2), TierOneCatalystEvaluator.EffectiveAvailableAtUtc(postCutoffObserved, 5));
        Assert.False(TierOneCatalystEvaluator.Evaluate("ACME", [postCutoffObserved], cutoff, 18m, 5, allowed, -0.15m).Qualified);

        // 2) Multi-story window: later minor non-Tier-1 wire item (10m ago) does NOT mask earlier earnings+guidance (4h ago).
        // Uses CatalystType.NewsReport so EconomicNewsRuleClassifier multi-cue matching is also verified directly.
        var earnings = new CatalystEvent(
            "ACME",
            cutoff.AddHours(-4),
            CatalystType.NewsReport,
            "ACME reports Q2 earnings and raises full-year guidance",
            SentimentScore: 0.70m,
            ReceivedAt: cutoff.AddHours(-4),
            DecisionAvailableAt: cutoff.AddHours(-4));
        var minorChatter = new CatalystEvent(
            "ACME",
            cutoff.AddMinutes(-10),
            CatalystType.NewsReport,
            "Why ACME shares are up in afternoon trading",
            SentimentScore: 0.10m,
            ReceivedAt: cutoff.AddMinutes(-10),
            DecisionAvailableAt: cutoff.AddMinutes(-10));
        var multiResult = TierOneCatalystEvaluator.Evaluate("ACME", [earnings, minorChatter], cutoff, 18m, 5, allowed, vetoSentimentAtOrBelow: -0.15m);
        Assert.True(multiResult.Qualified);
        Assert.Equal(earnings, multiResult.SelectedCatalyst);
        Assert.Equal("earnings", multiResult.MatchedCategory);

        // 3) Multi-story FinBERT negative veto: an earlier negative story (<= -0.15) vetoes even when a positive
        // earnings story arrived later in the same 18h window.
        var negativeStory = new CatalystEvent(
            "ACME",
            cutoff.AddHours(-6),
            CatalystType.NewsReport,
            "ACME faces investigation over accounting controls",
            SentimentScore: -0.45m,
            ReceivedAt: cutoff.AddHours(-6),
            DecisionAvailableAt: cutoff.AddHours(-6));
        var vetoResult = TierOneCatalystEvaluator.Evaluate("ACME", [negativeStory, earnings], cutoff, 18m, 5, allowed, vetoSentimentAtOrBelow: -0.15m);
        Assert.False(vetoResult.Qualified);
        Assert.StartsWith("negative_news_sentiment", vetoResult.RejectionReason);
        Assert.Equal(negativeStory, vetoResult.SelectedCatalyst);

        // 4) Rumored/speculative headline is rejected; SEC fine does not match fda; downgrade does not match upgrade.
        var rumor = new CatalystEvent("ACME", cutoff.AddHours(-2), CatalystType.NewsReport, "ACME reportedly expected to report Q2 earnings", 0.3m);
        var secFine = new CatalystEvent("ACME", cutoff.AddHours(-2), CatalystType.NewsReport, "SEC fines ACME $10M over disclosures", 0.0m);
        var downgrade = new CatalystEvent("ACME", cutoff.AddHours(-2), CatalystType.AnalystDowngrade, "Bank downgrades ACME to Sell", 0.0m);
        Assert.Equal("tier1_catalyst_category_not_matched",
            TierOneCatalystEvaluator.Evaluate("ACME", [rumor, secFine, downgrade], cutoff, 18m, 5, allowed, -0.15m).RejectionReason);

        // 5) Each supported Tier-1 category matches cleanly.
        Assert.Equal("guidance", TierOneCatalystEvaluator.MatchAllowedCategory(
            new CatalystEvent("ACME", cutoff.AddHours(-2), CatalystType.NewsReport, "ACME raises full-year guidance", 0.4m), allowed, cutoff));
        Assert.Equal("fda", TierOneCatalystEvaluator.MatchAllowedCategory(
            new CatalystEvent("ACME", cutoff.AddHours(-2), CatalystType.NewsReport, "FDA approves ACME oncology therapy", 0.5m), allowed, cutoff));
        Assert.Equal("contract", TierOneCatalystEvaluator.MatchAllowedCategory(
            new CatalystEvent("ACME", cutoff.AddHours(-2), CatalystType.NewsReport, "ACME wins $250M defense contract", 0.5m), allowed, cutoff));
        Assert.Equal("upgrade", TierOneCatalystEvaluator.MatchAllowedCategory(
            new CatalystEvent("ACME", cutoff.AddHours(-2), CatalystType.AnalystUpgrade, "Goldman Sachs upgrades ACME to Buy", 0.5m), allowed, cutoff));
    }

    // Verifies end-to-end SignalGenerator + BasicStrategyEvaluator evaluation of news-orb-swing.bt-v1.yaml across
    // the passing setup and every individual failing gate.
    [Fact]
    public void NewsOrbSwing_EndToEndSignalAndEvaluator_AcceptsValidSetupAndRejectsEachFailingCondition()
    {
        var ny = ResolveNewYork();
        var strategy = new SimpleYamlReader().ReadStrategy(Path.Combine(Root, RecipePath));
        var generator = new SignalGenerator();
        var evaluator = new BasicStrategyEvaluator();
        var startDate = new DateOnly(2026, 6, 1);

        var dailyBars = new OhlcvBar[21];
        var snapshots = new IndicatorSnapshot[21];
        var fiveMinuteBars = new List<OhlcvBar>();
        for (var i = 0; i < 20; i++)
        {
            var date = startDate.AddDays(i);
            dailyBars[i] = DailyBar(date, ny, 100m, 105m, 100m, 100m, 300_000m);
            snapshots[i] = Snapshot(dailyBars[i].Timestamp, 100m, atr: 2.0m);
            fiveMinuteBars.AddRange(BuildOpeningSixBars(date, ny, 100m, 102m, 99m, 101m, 20_000m)); // 120k baseline
        }

        // Day 20 (signal day): opens at 102.0 (GapUpAtr = (102 - 100) / 2 = 1.0 >= 0.75), closes at 107.0.
        // Opening range high = 105.0, opening volume = 300k (RVOL = 2.5 >= 2.0), session VWAP = 104.0.
        var signalDate = startDate.AddDays(20);
        dailyBars[20] = DailyBar(signalDate, ny, 102m, 108m, 101.5m, 107m, 500_000m);
        snapshots[20] = Snapshot(dailyBars[20].Timestamp, 107m, atr: 2.5m);
        var signalFiveMin = BuildOpeningSixBars(signalDate, ny, 102m, 105m, 101.5m, 104m, 50_000m).ToList();
        signalFiveMin.Add(FiveMinuteBar(ToUtc(signalDate, new TimeOnly(15, 0), ny), 104m, 107m, 103.5m, 106.5m, 200_000m));
        var allFiveMin = fiveMinuteBars.Concat(signalFiveMin).ToArray();
        var cutoff = ToUtc(signalDate, new TimeOnly(16, 0), ny);

        var earnings = new CatalystEvent(
            "ACME",
            cutoff.AddHours(-7),
            CatalystType.EarningsRelease,
            "ACME reports Q2 earnings and beats estimates",
            SentimentScore: 0.60m,
            ReceivedAt: cutoff.AddHours(-7),
            DecisionAvailableAt: cutoff.AddHours(-7));

        // 1) Passing setup.
        var okSignal = generator.CreateTradeSignal(
            strategy, dailyBars, snapshots, 20,
            asOfUtc: cutoff, fiveMinuteBars: allFiveMin, availableCatalysts: [earnings])!;
        Assert.Equal(1.0m, okSignal.GapUpAtr);
        Assert.Equal(2.5m, okSignal.OpeningRelativeVolume);
        Assert.Equal(105m, okSignal.OpeningRangeHigh);
        Assert.Equal("earnings", okSignal.MatchedCatalystCategory);
        Assert.Null(evaluator.GetLongEntryRejection(strategy, okSignal, 1.0m));

        // 2) Gap below 0.75 * ATR(14)_{D-1} -> gap_up_atr_below_minimum.
        var smallGapBars = (OhlcvBar[])dailyBars.Clone();
        smallGapBars[20] = smallGapBars[20] with { Open = 101.0m }; // gap = 0.5 ATR
        var smallGapSignal = generator.CreateTradeSignal(
            strategy, smallGapBars, snapshots, 20,
            asOfUtc: cutoff, fiveMinuteBars: allFiveMin, availableCatalysts: [earnings])!;
        Assert.StartsWith("gap_up_atr_below_minimum", evaluator.GetLongEntryRejection(strategy, smallGapSignal, 1.0m));

        // 3) Opening RVOL below 2.0 -> opening_relative_volume_below_minimum.
        var lowRvolFiveMin = fiveMinuteBars
            .Concat(BuildOpeningSixBars(signalDate, ny, 102m, 105m, 101.5m, 104m, 30_000m)) // 180k / 120k = 1.5x
            .ToArray();
        var lowRvolSignal = generator.CreateTradeSignal(
            strategy, dailyBars, snapshots, 20,
            asOfUtc: cutoff, fiveMinuteBars: lowRvolFiveMin, availableCatalysts: [earnings])!;
        Assert.Contains("opening_relative_volume_below_minimum", evaluator.GetLongEntryRejection(strategy, lowRvolSignal, 1.0m)!);

        // 4) Daily close <= 09:30-10:00 opening high -> daily_close_not_above_opening_range_high.
        var belowOrhBars = (OhlcvBar[])dailyBars.Clone();
        belowOrhBars[20] = belowOrhBars[20] with { Close = 104.8m };
        var belowOrhSnaps = (IndicatorSnapshot[])snapshots.Clone();
        belowOrhSnaps[20] = belowOrhSnaps[20] with { CurrentPrice = 104.8m };
        var belowOrhSignal = generator.CreateTradeSignal(
            strategy, belowOrhBars, belowOrhSnaps, 20,
            asOfUtc: cutoff, fiveMinuteBars: allFiveMin, availableCatalysts: [earnings])!;
        Assert.Contains("daily_close_not_above_opening_range_high", evaluator.GetLongEntryRejection(strategy, belowOrhSignal, 1.0m)!);

        // 5) Daily close > opening high (105.0) but <= session VWAP (~107.2) -> daily_close_not_above_session_vwap.
        var highVwapFiveMin = fiveMinuteBars
            .Concat(BuildOpeningSixBars(signalDate, ny, 102m, 105m, 101.5m, 104m, 50_000m))
            .Append(FiveMinuteBar(ToUtc(signalDate, new TimeOnly(14, 0), ny), 107.5m, 109.5m, 107.0m, 108.5m, 2_000_000m))
            .ToArray();
        var belowVwapBars = (OhlcvBar[])dailyBars.Clone();
        belowVwapBars[20] = belowVwapBars[20] with { Close = 106.0m };
        var belowVwapSnaps = (IndicatorSnapshot[])snapshots.Clone();
        belowVwapSnaps[20] = belowVwapSnaps[20] with { CurrentPrice = 106.0m };
        var belowVwapSignal = generator.CreateTradeSignal(
            strategy, belowVwapBars, belowVwapSnaps, 20,
            asOfUtc: cutoff, fiveMinuteBars: highVwapFiveMin, availableCatalysts: [earnings])!;
        Assert.Contains("daily_close_not_above_session_vwap", evaluator.GetLongEntryRejection(strategy, belowVwapSignal, 1.0m)!);

        // 6) Missing Tier-1 catalyst -> tier1_catalyst_missing.
        var noNewsSignal = generator.CreateTradeSignal(
            strategy, dailyBars, snapshots, 20,
            asOfUtc: cutoff, fiveMinuteBars: allFiveMin, availableCatalysts: Array.Empty<CatalystEvent>())!;
        Assert.Equal("tier1_catalyst_missing", evaluator.GetLongEntryRejection(strategy, noNewsSignal, 1.0m));

        // 7) Negative FinBERT veto in window -> negative_news_sentiment.
        var negativeNews = new CatalystEvent("ACME", cutoff.AddHours(-3), CatalystType.NewsReport, "ACME cut by regulator", -0.35m);
        var vetoedSignal = generator.CreateTradeSignal(
            strategy, dailyBars, snapshots, 20,
            asOfUtc: cutoff, fiveMinuteBars: allFiveMin, availableCatalysts: [earnings, negativeNews])!;
        Assert.StartsWith("negative_news_sentiment", evaluator.GetLongEntryRejection(strategy, vetoedSignal, 1.0m));
    }

    // Verifies frozen YAML parsing, invalid config refusals, 5m + news capability resolution, RunConfigWriter
    // round-trip, catalog registration, and unchanged canonical hashes for all prior swing strategies.
    [Fact]
    public void Recipe_ParsesFrozenValuesValidatesInvalidConfigsResolves5mAndNewsAndPreservesExistingHashes()
    {
        var reader = new SimpleYamlReader();
        var strategy = reader.ReadStrategy(Path.Combine(Root, RecipePath));

        Assert.Equal(("1d", "long", "news_orb_swing"), (strategy.Timeframe, strategy.Direction, strategy.EntryRules.SetupType));
        Assert.Equal((0.75m, 30, 2.0m, true, true), (
            strategy.EntryRules.MinGapUpAtr,
            strategy.EntryRules.OpeningRangeMinutes,
            strategy.EntryRules.MinOpeningRelativeVolume,
            strategy.EntryRules.RequireDailyCloseAboveOpeningRangeHigh,
            strategy.EntryRules.RequireDailyCloseAboveSessionVwap));
        Assert.Equal((5, 18.0m, "earnings,guidance,fda,contract,upgrade", -0.15m), (
            strategy.EntryRules.CatalystReceiptDelayMinutes,
            strategy.EntryRules.MaxNewsAgeHours,
            strategy.EntryRules.AllowedCatalystCategories,
            strategy.EntryRules.VetoNewsSentimentBelow));
        Assert.Equal((2.5m, 20_000_000m), (strategy.EntryRules.MinAverageRangePct, strategy.EntryRules.MinAverageDollarVolume));
        Assert.Equal(21, BacktestRunner.LongestIndicatorLookback(strategy));

        // Timeframes & news capability discovery.
        Assert.Equal(new[] { "5m", "1d" }, StrategyDecisionRequestAssembler.ResolveRequiredTimeframes(strategy));
        Assert.True(StrategyCapabilityInspector.UsesNews(strategy));
        Assert.Equal(18.0m, StrategyCapabilityInspector.RequiredNewsLookbackHours(strategy));
        var swingProfile = reader.ReadBacktestRun(Path.Combine(Root, "configs", "backtest", "swing-backtest-profile.yaml"));
        Assert.Contains("5m", RunConfigWriter.ResolveDownloadTimeframes(swingProfile, [strategy]), StringComparer.OrdinalIgnoreCase);

        // Invalid YAML mutations fail closed.
        AssertInvalidYamlEdit("  opening_range_minutes: 30", "  opening_range_minutes: 12");
        AssertInvalidYamlEdit("  opening_range_minutes: 30", "  opening_range_minutes: 0");
        AssertInvalidYamlEdit("  min_gap_up_atr: 0.75", "  min_gap_up_atr: 0");
        AssertInvalidYamlEdit("  min_opening_relative_volume: 2.0", "  min_opening_relative_volume: -1");
        AssertInvalidYamlEdit("  catalyst_receipt_delay_minutes: 5", "  catalyst_receipt_delay_minutes: -1");
        AssertInvalidYamlEdit("  allowed_catalyst_categories: earnings,guidance,fda,contract,upgrade", "  allowed_catalyst_categories: earnings,unknown_bucket");

        // RunConfigWriter round-trip.
        var tmp = Path.Combine(Path.GetTempPath(), $"tf-newsorb-rt-{Guid.NewGuid():N}.yaml");
        try
        {
            File.WriteAllText(tmp, RunConfigWriter.SerializeStrategyYaml(strategy));
            Assert.Equivalent(strategy, reader.ReadStrategy(tmp), strict: true);
        }
        finally
        {
            File.Delete(tmp);
        }

        // Catalog registration and byte-for-byte preservation of prior swing strategy hashes.
        var catalog = new StrategyArtifactCatalog(Root, Path.Combine(Root, "configs", "strategy-catalog.json"), reader);
        var artifacts = catalog.GetSnapshot().ExecutableArtifacts;
        var newsOrb = Assert.Single(artifacts, a => a.Identity.StrategyId == "technical.news-orb-swing" && a.Identity.SemanticVersion == "1.0.0");
        Assert.Null(catalog.GetDisabledReason(newsOrb));
        Assert.Equal("swing-daily-v2-as-traded-research-v1", newsOrb.AdmissionProfile.ProfileId);
        Assert.Equal("2.5.0", newsOrb.AdmissionProfile.Indicators.DerivedIndicatorProfileVersion);
        Assert.Contains(catalog.GetSelectable(StrategySelectionMode.Backtest), a => a.Identity == newsOrb.Identity);
        Assert.Empty(catalog.GetSelectable(StrategySelectionMode.RunPaperExperiment));
        Assert.Empty(catalog.GetSelectable(StrategySelectionMode.RunPaperShadow));
        Assert.Empty(catalog.GetSelectable(StrategySelectionMode.RunLive));

        Assert.Equal("ee56bf94bb160cbcd27dbde297cfe38ae366acb0911475d3ecee4f530ef9628d",
            Assert.Single(artifacts, a => a.Identity.StrategyId == "technical.bollinger-squeeze-breakout" && a.Identity.SemanticVersion == "1.0.0").Identity.ContentSha256);
        Assert.Equal("0a2cac80c7a629eae0a9c1def135ae757a806210545e4d4ca9e5b9884452e72c",
            Assert.Single(artifacts, a => a.Identity.StrategyId == "technical.ema21-trend-pullback" && a.Identity.SemanticVersion == "1.0.0").Identity.ContentSha256);
        Assert.Equal("445cef6d533becfb1a76b87c1d4187a502a9f7bc39b0836fb90271aa72592eec",
            Assert.Single(artifacts, a => a.Identity.StrategyId == "technical.ema21-trend-pullback" && a.Identity.SemanticVersion == "1.1.0").Identity.ContentSha256);
        Assert.Equal("d556e04c828b9c2096be3c2e88ac3da937d076d47dac659f75c4a95e33b953c2",
            Assert.Single(artifacts, a => a.Identity.StrategyId == "technical.rubber-band-reclaim" && a.Identity.SemanticVersion == "1.0.0").Identity.ContentSha256);
    }

    // Verifies end-to-end StrategyDecisionKernel.Evaluate with a June 2026 Tier-1 catalyst whose observed HTTP
    // ReceivedAt/DecisionAvailableAt is November 2026: triggers in Backtest mode while preserving BOTH original
    // firstReceivedAtUtc and assumedAvailableAtUtc in audit JSON, and blocks in non-backtest mode.
    [Fact]
    public void StrategyDecisionKernel_HistoricalReceiptDelayContract_TriggersInBacktestAndPreservesObservedReceiptAndBlocksLive()
    {
        var ny = ResolveNewYork();
        var reader = new SimpleYamlReader();
        var catalog = new StrategyArtifactCatalog(Root, Path.Combine(Root, "configs", "strategy-catalog.json"), reader);
        var artifact = Assert.Single(catalog.GetSnapshot().ExecutableArtifacts, a => a.Identity.StrategyId == "technical.news-orb-swing" && a.Identity.SemanticVersion == "1.0.0");

        // Build 21 weekday trading sessions (raw/sip provenance matching swing-daily-v2-as-traded-research-v1).
        var tradingDates = new List<DateOnly>(21);
        for (var cursor = new DateOnly(2026, 6, 1); tradingDates.Count < 21; cursor = cursor.AddDays(1))
        {
            if (cursor.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            tradingDates.Add(cursor);
        }

        var dailyBars = new List<OhlcvBar>(21);
        var dailySnaps = new List<IndicatorSnapshot>(21);
        var fiveMinBars = new List<OhlcvBar>();
        var fiveMinSnaps = new List<IndicatorSnapshot>();
        var schedules = new Dictionary<DateOnly, MarketSessionSchedule>();

        for (var i = 0; i < 20; i++)
        {
            var date = tradingDates[i];
            schedules[date] = new MarketSessionSchedule(date, true, new TimeOnly(9, 30), new TimeOnly(16, 0));
            var dBar = DailyBar(date, ny, 100m, 105m, 100m, 100m, 300_000m) with { DataFeed = "sip", AdjustmentPolicy = "raw" };
            dailyBars.Add(dBar);
            dailySnaps.Add(Snapshot(dBar.Timestamp, 100m, atr: 2.0m) with
            {
                DataFeed = "sip",
                AdjustmentPolicy = "raw",
                MarketEvidenceReliability = "verified_same_feed"
            });
            foreach (var b5 in BuildOpeningSixBars(date, ny, 100m, 102m, 99m, 101m, 20_000m))
            {
                var tagged = b5 with { DataFeed = "sip", AdjustmentPolicy = "raw" };
                fiveMinBars.Add(tagged);
                fiveMinSnaps.Add(Snapshot(tagged.Timestamp, tagged.Close, atr: 0.5m) with
                {
                    Timeframe = "5m",
                    DataFeed = "sip",
                    AdjustmentPolicy = "raw",
                    MarketEvidenceReliability = "verified_same_feed"
                });
            }
        }

        var signalDate = tradingDates[20]; // Monday 2026-06-29
        schedules[signalDate] = new MarketSessionSchedule(signalDate, true, new TimeOnly(9, 30), new TimeOnly(16, 0));
        var signalDaily = DailyBar(signalDate, ny, 102m, 108m, 101.5m, 107m, 500_000m) with { DataFeed = "sip", AdjustmentPolicy = "raw" };
        dailyBars.Add(signalDaily);
        dailySnaps.Add(Snapshot(signalDaily.Timestamp, 107m, atr: 2.5m) with
        {
            DataFeed = "sip",
            AdjustmentPolicy = "raw",
            MarketEvidenceReliability = "verified_same_feed"
        });
        var dayFiveMin = BuildOpeningSixBars(signalDate, ny, 102m, 105m, 101.5m, 104m, 50_000m)
            .Append(FiveMinuteBar(ToUtc(signalDate, new TimeOnly(15, 0), ny), 104m, 107m, 103.5m, 106.5m, 200_000m));
        foreach (var b5 in dayFiveMin)
        {
            var tagged = b5 with { DataFeed = "sip", AdjustmentPolicy = "raw" };
            fiveMinBars.Add(tagged);
            fiveMinSnaps.Add(Snapshot(tagged.Timestamp, tagged.Close, atr: 0.5m) with
            {
                Timeframe = "5m",
                DataFeed = "sip",
                AdjustmentPolicy = "raw",
                MarketEvidenceReliability = "verified_same_feed"
            });
        }

        // Catalyst published June 21, 2026 08:00 NY, but downloaded via REST in November 2026!
        var publishedUtc = ToUtc(signalDate, new TimeOnly(8, 0), ny);
        var novReceivedUtc = new DateTimeOffset(2026, 11, 19, 15, 0, 0, TimeSpan.Zero);
        var historicalEarnings = new CatalystEvent(
            "ACME",
            publishedUtc,
            CatalystType.EarningsRelease,
            "ACME reports Q2 earnings and beats estimates",
            SentimentScore: 0.75m,
            Provider: "alpaca",
            ExternalId: "art-june-2026",
            ReceivedAt: novReceivedUtc,
            UpdatedAt: publishedUtc,
            AvailabilityEvidence: CatalystAvailabilityEvidence.ObservedReceiptTime,
            DecisionAvailableAt: novReceivedUtc,
            DecisionAvailabilityEvidence: CatalystAvailabilityEvidence.NewsAndAssessmentObservedTime,
            ClassificationVersion: "finbert-v1");

        var decisionTimeUtc = signalDaily.Timestamp.AddDays(1); // 00:00 NY_{D+1}, as BacktestRunner passes
        var barsByTf = new Dictionary<string, IReadOnlyList<OhlcvBar>> { ["1d"] = dailyBars, ["5m"] = fiveMinBars };
        var snapsByTf = new Dictionary<string, IReadOnlyList<IndicatorSnapshot>> { ["1d"] = dailySnaps, ["5m"] = fiveMinSnaps };
        var setupKey = StrategyDecisionRequestAssembler.CreateSetupKey(artifact.Identity.StrategyId, "1d", signalDaily.Timestamp);
        var discovery = StrategyDecisionRequestAssembler.CreateBacktestDiscovery("ACME", setupKey, decisionTimeUtc, decisionTimeUtc.AddDays(1)) with
        {
            IsPersisted = true,
            IsActive = true
        };
        var eligible = new StrategyEligibilityEvidence(true, "test", "ok", decisionTimeUtc, "{}");
        var runId = Guid.NewGuid();
        var kernel = new StrategyDecisionKernel();

        // 1) Backtest mode: uses frozen assumedAvailableAtUtc (June 21 08:05 NY) and triggers while preserving Nov 19 firstReceivedAtUtc!
        var backtestRuntime = new AuthorizedRuntimeStrategy(artifact.Identity, StrategySelectionMode.Backtest, artifact.ResolvedStrategy, artifact.AdmissionProfile);
        var backtestRequest = StrategyDecisionRequestAssembler.Create(
            backtestRuntime, "ACME", barsByTf, snapsByTf, [historicalEarnings], decisionTimeUtc,
            discovery, eligible, eligible, StrategyCandidateState.DataWarming, 0, setupKey,
            decisionTimeUtc, decisionTimeUtc.AddDays(1), 21, runId, sessionSchedules: schedules);
        var backtestDecision = kernel.Evaluate(backtestRequest);

        Assert.True(backtestDecision.IsTriggered, $"Expected triggered decision; got state={backtestDecision.State}, reason={backtestDecision.NoEntryReason}");
        Assert.Equal("long", backtestDecision.Direction);
        Assert.Equal("earnings", backtestDecision.Signal!.MatchedCatalystCategory);
        // Verify audit JSON retains actual November firstReceivedAtUtc alongside June assumedAvailableAtUtc.
        using (var doc = JsonDocument.Parse(backtestDecision.CanonicalDecisionJson))
        {
            var catalystNode = doc.RootElement.GetProperty("semantic").GetProperty("catalysts")[0];
            Assert.Equal(novReceivedUtc, catalystNode.GetProperty("firstReceivedAtUtc").GetDateTimeOffset());
            Assert.Equal(publishedUtc.AddMinutes(5), catalystNode.GetProperty("assumedAvailableAtUtc").GetDateTimeOffset());
        }

        // 2) Non-backtest mode (PaperShadow / Live): actual November DecisionAvailableAt > June cutoff -> blocks!
        var liveRuntime = new AuthorizedRuntimeStrategy(artifact.Identity, StrategySelectionMode.RunPaperShadow, artifact.ResolvedStrategy, artifact.AdmissionProfile);
        var liveRequest = StrategyDecisionRequestAssembler.Create(
            liveRuntime, "ACME", barsByTf, snapsByTf, [historicalEarnings], decisionTimeUtc,
            discovery, eligible, eligible, StrategyCandidateState.DataWarming, 0, setupKey,
            decisionTimeUtc, decisionTimeUtc.AddDays(1), 21, runId, sessionSchedules: schedules);
        var liveDecision = kernel.Evaluate(liveRequest);

        Assert.False(liveDecision.IsTriggered);
        Assert.Equal("tier1_catalyst_missing", liveDecision.NoEntryReason);

        // 3) Later-eligible stock on early backtest days with 0 completed 5m bars (or absent "5m" key):
        // must fail closed via DataWarming / market_state_not_warm without throwing in ValidateCompletedBarSeries.
        var emptyFiveMinBars = new Dictionary<string, IReadOnlyList<OhlcvBar>> { ["1d"] = dailyBars, ["5m"] = Array.Empty<OhlcvBar>() };
        var emptyFiveMinSnaps = new Dictionary<string, IReadOnlyList<IndicatorSnapshot>> { ["1d"] = dailySnaps, ["5m"] = Array.Empty<IndicatorSnapshot>() };
        var emptyFiveMinRequest = StrategyDecisionRequestAssembler.Create(
            backtestRuntime, "ACME", emptyFiveMinBars, emptyFiveMinSnaps, [historicalEarnings], decisionTimeUtc,
            discovery, eligible, eligible, StrategyCandidateState.DataWarming, 0, setupKey,
            decisionTimeUtc, decisionTimeUtc.AddDays(1), 21, runId, sessionSchedules: schedules);
        var emptyFiveMinDecision = kernel.Evaluate(emptyFiveMinRequest);
        Assert.False(emptyFiveMinDecision.IsTriggered);
        Assert.Equal(StrategyCandidateState.DataWarming, emptyFiveMinDecision.State);
        Assert.Equal("market_state_not_warm", emptyFiveMinDecision.NoEntryReason);

        var absentFiveMinBars = new Dictionary<string, IReadOnlyList<OhlcvBar>> { ["1d"] = dailyBars };
        var absentFiveMinSnaps = new Dictionary<string, IReadOnlyList<IndicatorSnapshot>> { ["1d"] = dailySnaps };
        var absentFiveMinRequest = StrategyDecisionRequestAssembler.Create(
            backtestRuntime, "ACME", absentFiveMinBars, absentFiveMinSnaps, [historicalEarnings], decisionTimeUtc,
            discovery, eligible, eligible, StrategyCandidateState.DataWarming, 0, setupKey,
            decisionTimeUtc, decisionTimeUtc.AddDays(1), 21, runId, sessionSchedules: schedules);
        var absentFiveMinDecision = kernel.Evaluate(absentFiveMinRequest);
        Assert.False(absentFiveMinDecision.IsTriggered);
        Assert.Equal(StrategyCandidateState.DataWarming, absentFiveMinDecision.State);
        Assert.Equal("market_state_not_warm", absentFiveMinDecision.NoEntryReason);
    }

    // Verifies daily session cutoff semantics: D-1 16:15 NY AMC earnings qualifies day D (within 18h of 10:00 NY_D),
    // while day D 16:05 NY post-close earnings is excluded even when asOfUtc is 00:00 NY_{D+1}.
    [Fact]
    public void DailySessionCutoff_AcceptsPriorDayAmcEarningsAndRejectsSameDayPostCloseEarnings()
    {
        var ny = ResolveNewYork();
        var strategy = new SimpleYamlReader().ReadStrategy(Path.Combine(Root, RecipePath));
        var generator = new SignalGenerator();
        var evaluator = new BasicStrategyEvaluator();
        var startDate = new DateOnly(2026, 6, 1);

        var dailyBars = new OhlcvBar[21];
        var snapshots = new IndicatorSnapshot[21];
        var fiveMinBars = new List<OhlcvBar>();
        var schedules = new Dictionary<DateOnly, MarketSessionSchedule>();
        for (var i = 0; i < 20; i++)
        {
            var date = startDate.AddDays(i);
            schedules[date] = new MarketSessionSchedule(date, true, new TimeOnly(9, 30), new TimeOnly(16, 0));
            dailyBars[i] = DailyBar(date, ny, 100m, 105m, 100m, 100m, 300_000m);
            snapshots[i] = Snapshot(dailyBars[i].Timestamp, 100m, atr: 2.0m);
            fiveMinBars.AddRange(BuildOpeningSixBars(date, ny, 100m, 102m, 99m, 101m, 20_000m));
        }

        var priorDate = startDate.AddDays(19);
        var signalDate = startDate.AddDays(20);
        schedules[signalDate] = new MarketSessionSchedule(signalDate, true, new TimeOnly(9, 30), new TimeOnly(16, 0));
        dailyBars[20] = DailyBar(signalDate, ny, 102m, 108m, 101.5m, 107m, 500_000m);
        snapshots[20] = Snapshot(dailyBars[20].Timestamp, 107m, atr: 2.5m);
        fiveMinBars.AddRange(BuildOpeningSixBars(signalDate, ny, 102m, 105m, 101.5m, 104m, 50_000m));
        fiveMinBars.Add(FiveMinuteBar(ToUtc(signalDate, new TimeOnly(15, 0), ny), 104m, 107m, 103.5m, 106.5m, 200_000m));

        // BacktestRunner passes asOfUtc = 00:00 NY_{D+1} for a completed daily bar D.
        var midnightNextDayUtc = dailyBars[20].Timestamp.AddDays(1);

        // D-1 16:15 NY AMC earnings (17h45m before 10:00 NY_D <= 18h) -> qualifies day D!
        var priorAmcEarnings = new CatalystEvent(
            "ACME",
            ToUtc(priorDate, new TimeOnly(16, 15), ny),
            CatalystType.EarningsRelease,
            "ACME reports Q2 earnings and beats estimates",
            SentimentScore: 0.65m);
        var priorAmcSignal = generator.CreateTradeSignal(
            strategy, dailyBars, snapshots, 20,
            asOfUtc: midnightNextDayUtc, fiveMinuteBars: fiveMinBars, availableCatalysts: [priorAmcEarnings],
            sessionSchedules: schedules, ignoreObservedReceiptForHistoricalResearch: true)!;
        Assert.Equal("earnings", priorAmcSignal.MatchedCatalystCategory);
        Assert.Null(evaluator.GetLongEntryRejection(strategy, priorAmcSignal, 1.0m));

        // Day D 16:05 NY post-close earnings (> 16:00 NY_D regular close) -> excluded (no look-ahead into day D)!
        var sameDayPostCloseEarnings = new CatalystEvent(
            "ACME",
            ToUtc(signalDate, new TimeOnly(16, 5), ny),
            CatalystType.EarningsRelease,
            "ACME reports Q2 earnings after the bell",
            SentimentScore: 0.80m);
        var postCloseSignal = generator.CreateTradeSignal(
            strategy, dailyBars, snapshots, 20,
            asOfUtc: midnightNextDayUtc, fiveMinuteBars: fiveMinBars, availableCatalysts: [sameDayPostCloseEarnings],
            sessionSchedules: schedules, ignoreObservedReceiptForHistoricalResearch: true)!;
        Assert.Null(postCloseSignal.MatchedCatalystCategory);
        Assert.Equal("tier1_catalyst_missing", evaluator.GetLongEntryRejection(strategy, postCloseSignal, 1.0m));
    }

    // Verifies 13:00 NY early-close schedules exclude 13:05 NY post-market bars from SessionVwap and exact duplicate
    // 5-minute timestamps are deduplicated so volume and VWAP are never double-counted.
    [Fact]
    public void DailyOpeningRangeSummary_EarlyClose1300ExcludesPostMarketAndDeduplicatesExactTimestamps()
    {
        var ny = ResolveNewYork();
        var startDate = new DateOnly(2026, 6, 13);
        var schedules = new Dictionary<DateOnly, MarketSessionSchedule>();
        var fiveMinBars = new List<OhlcvBar>();
        for (var s = 0; s < 20; s++)
        {
            var date = startDate.AddDays(s);
            schedules[date] = new MarketSessionSchedule(date, true, new TimeOnly(9, 30), new TimeOnly(16, 0));
            fiveMinBars.AddRange(BuildOpeningSixBars(date, ny, 100m, 102m, 99m, 101m, 20_000m));
        }

        var earlyCloseDate = startDate.AddDays(20); // 2026-07-03 (13:00 NY early close)
        schedules[earlyCloseDate] = new MarketSessionSchedule(earlyCloseDate, true, new TimeOnly(9, 30), new TimeOnly(13, 0));
        var openingBars = BuildOpeningSixBars(earlyCloseDate, ny, 102m, 105m, 101m, 103m, 50_000m);
        fiveMinBars.AddRange(openingBars);
        // Duplicate 09:30 bar with extreme volume — must be deduplicated!
        fiveMinBars.Add(openingBars[0] with { Volume = 9_999_999m });
        // Post-market bar at 13:05 NY (after 13:00 NY early close) with extreme price 200m — must be excluded!
        fiveMinBars.Add(FiveMinuteBar(ToUtc(earlyCloseDate, new TimeOnly(13, 5), ny), 195m, 205m, 195m, 200m, 5_000_000m));

        var dailyBar = DailyBar(earlyCloseDate, ny, 102m, 105m, 101m, 104m, 300_000m);
        var evidence = DailyOpeningRangeSummary.Compute(
            dailyBar, fiveMinBars, 30, "America/New_York",
            ToUtc(earlyCloseDate, new TimeOnly(16, 0), ny),
            schedules);

        Assert.True(evidence.IsComplete);
        // Typical price of opening bars = (105 + 101 + 103) / 3 = 103m; neither 13:05 post-market nor duplicate bar affects it.
        Assert.Equal(103m, evidence.SessionVwap);
        // Duplicate 09:30 bar keeps only one instance, so opening volume is NOT 300k + 9.99M.
        Assert.True(evidence.OpeningVolume is 300_000m or (250_000m + 9_999_999m));
        // With deduplicated exact-timestamp clean bars, verify pure exact-copy duplicate yields exact 300_000m.
        var exactDupBars = fiveMinBars.Where(b => b.Volume != 9_999_999m).Append(openingBars[0]).ToArray();
        var exactDupEvidence = DailyOpeningRangeSummary.Compute(
            dailyBar, exactDupBars, 30, "America/New_York",
            ToUtc(earlyCloseDate, new TimeOnly(16, 0), ny),
            schedules);
        Assert.Equal(300_000m, exactDupEvidence.OpeningVolume);
        Assert.Equal(2.5m, exactDupEvidence.OpeningRelativeVolume);
        Assert.Equal(103m, exactDupEvidence.SessionVwap);
    }

    // Verifies ResearchScreenSources binds 5m datasets collected for eligible.csv, WorkflowRunConfiguration and
    // PreparedMarketDataService refuse missing 5m datasets upfront, and GetNewsSentimentRejection fails closed.
    [Fact]
    public async Task ResearchScreenSources_BindsEligibleFiveMinuteDatasetsAndPreFlightGuardsRefuseMissing5m()
    {
        using var root = new TemporaryFolder();
        var folder = Path.Combine(root.Path, "screen-eligible");
        Directory.CreateDirectory(folder);
        await File.WriteAllLinesAsync(Path.Combine(folder, "eligible.csv"),
            ["session_date,ticker", "2026-06-21,ACME"]);
        await ResearchScreenManifest.PublishAsync(
            folder,
            new ResearchScreenInputs("daily-ds", "actions-ds", null, "sessions-ds", "secmaster-ds"),
            new DailyScreenRules(5m, DailyScreenPriceBasis.PriorClose, 20, MinAverageDollarVolume: 20_000_000m, MinHistorySessions: 253, MinAverageRangePercent: 2.5m),
            null,
            new DateOnly(2026, 6, 1),
            new DateOnly(2026, 6, 30),
            "test",
            null,
            default);
        var screen = PublishedResearchScreen.Read(folder);
        using var catalog = new SyntheticEvidenceCatalog();
        var five = await catalog.BarsAsync("5m", screen.RankedListSha256,
            [SyntheticEvidenceCatalog.Row("ACME", "5m", new(2026, 6, 21, 13, 30, 0, TimeSpan.Zero), 100m)]);

        // Eligible-list screen binds its collected 5m dataset and stays empty when none is collected.
        var withFive = ResearchScreenSources.ResolveReference(screen, [five]);
        var withoutFive = ResearchScreenSources.ResolveReference(screen, []);
        Assert.Equal(new[] { five.DatasetId }, withFive.FiveMinuteDatasetIds);
        Assert.Empty(withoutFive.FiveMinuteDatasetIds);

        var reader = new SimpleYamlReader();
        var newsOrb = reader.ReadStrategy(Path.Combine(Root, RecipePath));

        // WorkflowRunConfiguration pre-flight guard refuses missing 5m datasets for news-orb-swing or DailyRthFiveMinuteV1.
        Assert.Throws<InvalidOperationException>(() =>
            WorkflowRunConfiguration.RequireFiveMinuteBarsForDailyStrategies(withoutFive, [newsOrb], null));
        Assert.Throws<InvalidOperationException>(() =>
            WorkflowRunConfiguration.RequireFiveMinuteBarsForDailyStrategies(withoutFive, [], ExecutionReplayPolicy.DailyRthFiveMinuteV1));
        WorkflowRunConfiguration.RequireFiveMinuteBarsForDailyStrategies(withFive, [newsOrb], ExecutionReplayPolicy.DailyRthFiveMinuteV1);

        // PreparedMarketDataService pre-flight guard refuses evidence_catalog runs for news-orb-swing without 5m datasets.
        var baseProfile = reader.ReadBacktestRun(Path.Combine(Root, "configs", "backtest", "swing-backtest-profile.yaml"));
        var catalogConfigWithout5m = new EvidenceCatalogProviderConfig(
            Path.Combine(root.Path, "catalog.db"), Path.Combine(root.Path, "objects"), screen.RankedListSha256,
            "daily-ds", "sessions-ds", [], [], "actions-ds", [], "secmaster-ds");
        var runWithout5m = baseProfile with
        {
            Provider = TradingFlow.Data.Evidence.EvidenceCatalogMarketDataProvider.ProviderName,
            Tickers = ["ACME"],
            Strategies = [Path.Combine(Root, RecipePath)],
            TimeWindow = baseProfile.TimeWindow with
            {
                Type = "fixed",
                Start = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
                End = new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero)
            },
            Providers = baseProfile.Providers with { EvidenceCatalog = catalogConfigWithout5m }
        };
        var preparedService = new PreparedMarketDataService(
            ConfiguredDataProviders.FromEnvironment(),
            _ => new TradingFlow.Data.Evidence.EvidenceCatalogMarketDataProvider(
                catalogConfigWithout5m,
                catalog.GetAsync,
                catalog.Reader,
                new TradingFlow.Engine.Storage.FileSystemRawArchiveWriter(new TradingFlow.Engine.Storage.RawArchiveOptions(catalog.RawRoot))));
        var preparedError = Assert.Throws<InvalidOperationException>(() => preparedService.ValidateRequest(
            new PreparedMarketDataRequest(
                1, "test-no-5m", new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
                [new PreparedMarketDataBook("book-1", runWithout5m, [newsOrb])])));
        Assert.Contains("five_minute_datasets", preparedError.Message, StringComparison.Ordinal);

        // BasicStrategyEvaluator fails closed even if a signal omits both MatchedCatalystCategory and RejectionDetail.
        var momentumWithTier1 = newsOrb with
        {
            EntryRules = newsOrb.EntryRules with
            {
                SetupType = "momentum",
                OpeningRangeMinutes = 0,
                MinOpeningRelativeVolume = null,
                RequireDailyCloseAboveOpeningRangeHigh = false,
                RequireDailyCloseAboveSessionVwap = false,
                MinGapUpAtr = null,
                MinAverageRangePct = null,
                MinAverageDollarVolume = null
            }
        };
        var blankSignal = new TradeSignal(
            "ACME", new DateTimeOffset(2026, 6, 21, 4, 0, 0, TimeSpan.Zero), "1d",
            107m, 500_000m, 55m, 2m,
            true, false, false, false, false, false, false, false,
            true, true, true, null, true, true, true);
        Assert.Equal("tier1_catalyst_missing", new BasicStrategyEvaluator().GetLongEntryRejection(momentumWithTier1, blankSignal, 1.0m));
    }

    // Replaces one line in a temporary copy of news-orb-swing.bt-v1.yaml and asserts SimpleYamlReader throws.
    private static void AssertInvalidYamlEdit(string original, string replacement)
    {
        var text = File.ReadAllText(Path.Combine(Root, RecipePath));
        Assert.Contains(original, text, StringComparison.Ordinal);
        var path = Path.Combine(Path.GetTempPath(), $"tf-newsorb-invalid-{Guid.NewGuid():N}.yaml");
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

    // Builds the six consecutive 5-minute opening bars (09:30..09:55 NY) for a session date.
    private static OhlcvBar[] BuildOpeningSixBars(
        DateOnly date,
        TimeZoneInfo ny,
        decimal open,
        decimal high,
        decimal low,
        decimal close,
        decimal perBarVolume) =>
        Enumerable.Range(0, 6)
            .Select(slot => FiveMinuteBar(ToUtc(date, new TimeOnly(9, 30).AddMinutes(slot * 5), ny), open, high, low, close, perBarVolume))
            .ToArray();

    // Builds a single 5-minute OHLCV bar with optional KnownAtUtc receipt timestamp.
    private static OhlcvBar FiveMinuteBar(
        DateTimeOffset timestampUtc,
        decimal open,
        decimal high,
        decimal low,
        decimal close,
        decimal volume,
        DateTimeOffset? knownAtUtc = null) => new(
        "ACME", timestampUtc, "5m", open, high, low, close, volume, KnownAtUtc: knownAtUtc);

    // Builds a single completed daily OHLCV bar anchored at midnight NY for the given session date.
    private static OhlcvBar DailyBar(
        DateOnly date,
        TimeZoneInfo ny,
        decimal open,
        decimal high,
        decimal low,
        decimal close,
        decimal volume) => new(
        "ACME", ToUtc(date, new TimeOnly(0, 0), ny), "1d", open, high, low, close, volume);

    // Builds a minimal daily IndicatorSnapshot with explicit price and ATR for deterministic signal tests.
    private static IndicatorSnapshot Snapshot(DateTimeOffset timestampUtc, decimal close, decimal? atr) => new(
        "ACME",
        timestampUtc,
        "1d",
        close,
        300_000m,
        Vwap: null,
        Rsi: 50m,
        Atr: atr,
        Ema20: null,
        Ema50: null,
        Ema200: null,
        BollingerMiddle: null,
        BollingerUpper: null,
        BollingerLower: null,
        RelativeVolume: 1.0m,
        MacdLine: null,
        MacdSignal: null,
        MacdHistogram: null);

    // Resolves the New York timezone across Windows and Linux runtimes.
    private static TimeZoneInfo ResolveNewYork()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
    }

    // Converts a New York date and wall-clock time to UTC.
    private static DateTimeOffset ToUtc(DateOnly date, TimeOnly time, TimeZoneInfo ny)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, ny.GetUtcOffset(local)).ToUniversalTime();
    }
}
