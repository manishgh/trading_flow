using System.Collections.Immutable;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using TradingFlow.Alpaca;
using TradingFlow.Application.Jobs;
using TradingFlow.Backtesting;
using TradingFlow.Cli;
using TradingFlow.Contracts.V1;
using TradingFlow.Data.Reference;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Regime;
using TradingFlow.Engine.Risk;
using TradingFlow.Engine.Universe;
using TradingFlow.Web.Services;
using TradingFlow.Web.Services.Desk;

namespace TradingFlow.Tests;

/// <summary>
/// Checkpoint 3 of the 18 Nov 2026 regime design: VIX sizing from FRED's VIXCLS close of the session before the entry.
/// Below 18 full risk, 18 to 22 (both included) half risk, above 22 no new long entry; every missing piece refuses. The same
/// rule serves the backtest calendar, the paper/Desk gate, shadow replay, and the shared order planner.
/// </summary>
public sealed class VixSizingTests
{
    private static readonly RegimeRules Rule = new("SPY", RegimeRules.PriceAboveSma, 3, VixSeries: "VIXCLS",
        VixFullRiskBelow: 18m, VixNoLongsAbove: 22m, VixReducedRiskScale: 0.5m);

    private static readonly RegimeRules PlainRule = new("SPY", RegimeRules.PriceAboveSma, 3);

    // FRED's file: strips leading UTF-8 BOM, accepts observation_date or DATE header, skips "." or empty values, and refuses
    // malformed column-0 headers, wrong series, bad rows, non-positive closes and duplicate dates.
    [Fact]
    public void FredFile_ParsesClosesHandlesBomAndRefusesMalformedHeadersOrRows()
    {
        var series = VixCloseSeries.ParseFredCsv("\uFEFFobservation_date,VIXCLS\r\n2026-09-04,14.53\r\n2026-09-07,.\r\n2026-09-08,\r\n2026-09-09,16.46\r\n");

        Assert.Equal(2, series.Count);
        Assert.True(series.TryGetClose(new(2026, 9, 4), out var close) && close == 14.53m);
        Assert.False(series.TryGetClose(new(2026, 9, 7), out _));
        Assert.Equal((new DateOnly(2026, 9, 4), new DateOnly(2026, 9, 9)), (series.FirstDate!.Value, series.LastDate!.Value));
        Assert.Equal(1, series.Before(new(2026, 9, 9)).Count);

        var legacyHeader = VixCloseSeries.ParseFredCsv("DATE,VIXCLS\n2026-09-04,15.20\n");
        Assert.True(legacyHeader.TryGetClose(new(2026, 9, 4), out var legacyClose) && legacyClose == 15.20m);

        Assert.Throws<InvalidDataException>(() => VixCloseSeries.ParseFredCsv("2026-09-04,VIXCLS\n2026-09-08,14\n"));
        Assert.Throws<InvalidDataException>(() => VixCloseSeries.ParseFredCsv("observation_date,DGS10\n2026-09-04,4.1\n"));
        Assert.Throws<InvalidDataException>(() => VixCloseSeries.ParseFredCsv("observation_date,VIXCLS\n04/09/2026,14\n"));
        Assert.Throws<InvalidDataException>(() => VixCloseSeries.ParseFredCsv("observation_date,VIXCLS\n2026-09-04,14\n2026-09-04,15\n"));
        Assert.Throws<InvalidDataException>(() => VixCloseSeries.ParseFredCsv("observation_date,VIXCLS\n2026-09-04,-1\n"));
    }

    // The thresholds as approved, including both boundaries of the half-risk band and every fail-closed reason.
    [Theory]
    [InlineData("17.99", null, "1")]
    [InlineData("18", null, "0.5")]
    [InlineData("22", null, "0.5")]
    [InlineData("22.01", VixRegime.OffReason, "0")]
    public void Decide_AppliesTheApprovedThresholds(string vix, string? rejection, string scale)
    {
        var session = new DateOnly(2026, 11, 17);
        var series = VixCloseSeries.From([new(session, decimal.Parse(vix, System.Globalization.CultureInfo.InvariantCulture))]);

        var decision = VixRegime.Decide(Rule, series, session);

        Assert.Equal(rejection, decision.Rejection);
        Assert.Equal(decimal.Parse(scale, System.Globalization.CultureInfo.InvariantCulture), decision.RiskScale);
    }

    [Fact]
    public void Decide_RefusesMissingDataPartialRulesAndIgnoresStrategiesWithoutVix()
    {
        var series = VixCloseSeries.From([new(new DateOnly(2026, 11, 16), 15m)]);

        Assert.Equal(VixRegime.CloseUnknownReason, VixRegime.Decide(Rule, series, new DateOnly(2026, 11, 17)).Rejection);
        Assert.Equal(VixRegime.CloseUnknownReason, VixRegime.Decide(Rule, series, null).Rejection);
        Assert.Equal(VixRegime.LookupFailedReason, VixRegime.Decide(Rule, null, new DateOnly(2026, 11, 16)).Rejection);
        Assert.Same(VixEntryDecision.NotRequired, VixRegime.Decide(PlainRule, null, null));

        // Partial VIX settings constructed in code are refused by RegimeRules.Validate, Decide and WithVix.
        var partial = new RegimeRules("SPY", RegimeRules.PriceAboveSma, 3, VixSeries: "VIXCLS", VixFullRiskBelow: 18m);
        Assert.Throws<InvalidOperationException>(() => partial.Validate("long"));
        Assert.Throws<InvalidOperationException>(() => VixRegime.Decide(partial, series, new DateOnly(2026, 11, 16)));
        Assert.Throws<InvalidOperationException>(() => RegimeCalendarBuilder.Build(Rising("SPY", new DateOnly(2026, 5, 15), 10), 3).WithVix(partial, series));
    }

    // FRED lists 7 Sep 2026 (Labor Day). The previous session comes from SPY's completed bars, so a Tuesday 8 Sep entry reads
    // Friday 4 Sep's close and the holiday value is never used; Wednesday reads Tuesday.
    [Fact]
    public void PreviousSession_SkipsHolidayValuesAndTodaysUnfinishedBar()
    {
        var spy = new[] { Bar("SPY", new(2026, 9, 3)), Bar("SPY", new(2026, 9, 4)), Bar("SPY", new(2026, 9, 8)) };
        var series = VixCloseSeries.ParseFredCsv("observation_date,VIXCLS\n2026-09-04,14.53\n2026-09-07,30.00\n2026-09-08,19.00\n");

        var tuesdayOpen = new DateTimeOffset(2026, 9, 8, 13, 30, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2026, 9, 4), VixRegime.PreviousSession(spy, tuesdayOpen));
        Assert.Equal(1m, VixRegime.Decide(Rule, series, VixRegime.PreviousSession(spy, tuesdayOpen)).RiskScale);
        var wednesdayOpen = new DateTimeOffset(2026, 9, 9, 13, 30, 0, TimeSpan.Zero);
        Assert.Equal(0.5m, VixRegime.Decide(Rule, series, VixRegime.PreviousSession(spy, wednesdayOpen)).RiskScale);
        Assert.Null(VixRegime.PreviousSession([], wednesdayOpen));
    }

    // DST Spring-Forward (6 Mar EST -> 9 Mar EDT) and Fall-Back (30 Oct EDT -> 2 Nov EST) plus premarket, regular, postmarket
    // and late-evening New York decision timestamps align identically across VixRegime and RegimeCalendar.
    [Fact]
    public void PreviousSession_AlignsAcrossDstTransitionsAndIntradayDecisionTimes()
    {
        var springSpy = new[]
        {
            new OhlcvBar("SPY", new DateTimeOffset(2026, 3, 5, 5, 0, 0, TimeSpan.Zero), "1d", 100m, 100m, 100m, 100m, 1_000m),
            new OhlcvBar("SPY", new DateTimeOffset(2026, 3, 6, 5, 0, 0, TimeSpan.Zero), "1d", 101m, 101m, 101m, 101m, 1_000m),
            new OhlcvBar("SPY", new DateTimeOffset(2026, 3, 9, 4, 0, 0, TimeSpan.Zero), "1d", 102m, 102m, 102m, 102m, 1_000m),
            new OhlcvBar("SPY", new DateTimeOffset(2026, 3, 10, 4, 0, 0, TimeSpan.Zero), "1d", 103m, 103m, 103m, 103m, 1_000m)
        };
        // Monday 9 Mar (first trading day after spring-forward): premarket (13:00Z), regular (14:30Z), postmarket (21:30Z),
        // and late evening NY (Tue 01:30Z = Mon 21:30 EDT) all read Friday 6 Mar; Tue 04:00Z rolls to Monday 9 Mar.
        Assert.Equal(new DateOnly(2026, 3, 6), VixRegime.PreviousSession(springSpy, new DateTimeOffset(2026, 3, 9, 13, 0, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 3, 6), VixRegime.PreviousSession(springSpy, new DateTimeOffset(2026, 3, 9, 14, 30, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 3, 6), VixRegime.PreviousSession(springSpy, new DateTimeOffset(2026, 3, 9, 21, 30, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 3, 6), VixRegime.PreviousSession(springSpy, new DateTimeOffset(2026, 3, 10, 1, 30, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 3, 9), VixRegime.PreviousSession(springSpy, new DateTimeOffset(2026, 3, 10, 4, 0, 0, TimeSpan.Zero)));

        var fallSpy = new[]
        {
            new OhlcvBar("SPY", new DateTimeOffset(2026, 10, 29, 4, 0, 0, TimeSpan.Zero), "1d", 100m, 100m, 100m, 100m, 1_000m),
            new OhlcvBar("SPY", new DateTimeOffset(2026, 10, 30, 4, 0, 0, TimeSpan.Zero), "1d", 101m, 101m, 101m, 101m, 1_000m),
            new OhlcvBar("SPY", new DateTimeOffset(2026, 11, 2, 5, 0, 0, TimeSpan.Zero), "1d", 102m, 102m, 102m, 102m, 1_000m)
        };
        // Monday 2 Nov (first trading day after fall-back): premarket, regular, postmarket, and late evening (Tue 01:30Z = Mon 20:30 EST)
        // all read Friday 30 Oct; Tue 05:00Z rolls to Monday 2 Nov.
        Assert.Equal(new DateOnly(2026, 10, 30), VixRegime.PreviousSession(fallSpy, new DateTimeOffset(2026, 11, 2, 13, 0, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 10, 30), VixRegime.PreviousSession(fallSpy, new DateTimeOffset(2026, 11, 2, 14, 30, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 10, 30), VixRegime.PreviousSession(fallSpy, new DateTimeOffset(2026, 11, 2, 21, 30, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 10, 30), VixRegime.PreviousSession(fallSpy, new DateTimeOffset(2026, 11, 3, 1, 30, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 11, 2), VixRegime.PreviousSession(fallSpy, new DateTimeOffset(2026, 11, 3, 5, 0, 0, TimeSpan.Zero)));
    }

    // Authoritative exchange session calendar: a stale SPY feed ending on D-2 when D-1 completed fails closed as
    // market_regime_off_on_entry_day instead of reusing D-2's VIX close, and Sunday/holiday resampled bars are ignored.
    [Fact]
    public async Task AuthoritativeSchedules_RefuseStaleBenchmarkAndIgnoreNonTradingDayBars()
    {
        // Trading days Mon 2026-08-31 .. Fri 2026-09-04, Labor Day Mon 2026-09-07 closed, Tue 2026-09-08 & Wed 2026-09-09 open.
        var schedules = BuildSchedules(new DateOnly(2026, 8, 20), new DateOnly(2026, 9, 12), holidays: [new DateOnly(2026, 9, 7)]);
        var spyWithHoliday = RisingTradingDays("SPY", schedules, new DateOnly(2026, 8, 24), new DateOnly(2026, 9, 4))
            .Append(new OhlcvBar("SPY", new DateTimeOffset(2026, 9, 7, 4, 0, 0, TimeSpan.Zero), "1d", 200m, 200m, 200m, 200m, 1_000m))
            .ToArray();
        var vix = VixCloseSeries.From([
            new(new DateOnly(2026, 9, 4), 15m),
            new(new DateOnly(2026, 9, 7), 35m), // Labor Day holiday entry in FRED — must never be used.
            new(new DateOnly(2026, 9, 8), 30m)  // D-1 on Wed 9 Sep is > 22!
        ]);

        // Tuesday 8 Sep: holiday bar on 7 Sep is ignored -> reads Friday 4 Sep (VIX 15 -> full risk).
        var tuesdayOpen = new DateTimeOffset(2026, 9, 8, 13, 30, 0, TimeSpan.Zero);
        var calendar = RegimeCalendarBuilder.Build(spyWithHoliday, 3, schedules: schedules).WithVix(Rule, vix, schedules);
        var tuesdayDecision = calendar.EntryDecisionFor(tuesdayOpen, "AAA");
        Assert.Null(tuesdayDecision.Rejection);
        Assert.Equal(1m, tuesdayDecision.RiskScale);
        Assert.Equal(new DateOnly(2026, 9, 4), tuesdayDecision.Vix!.Session);

        // Wednesday 9 Sep: SPY stops at Friday 4 Sep (missing Tue 8 Sep D-1 bar). Must fail closed as MarketOffReason
        // instead of falling back to Friday 4 Sep's VIX = 15 when Tuesday 8 Sep's VIX was 30!
        var wednesdayOpen = new DateTimeOffset(2026, 9, 9, 13, 30, 0, TimeSpan.Zero);
        Assert.Equal(RegimeCalendar.MarketOffReason, calendar.RejectionFor(wednesdayOpen, "AAA"));

        // Live/paper RegimeGateService enforces the exact same rule when provider supplies authoritative schedules.
        var scheduledProvider = new ScheduledDailyProvider(spyWithHoliday, schedules);
        var liveGate = new RegimeGateService(new FixedVix(vix));
        var liveTuesday = await liveGate.EntryDecisionAsync(Rule, scheduledProvider, ["1d"], tuesdayOpen, "AAA", null, default);
        Assert.Null(liveTuesday.Rejection);
        Assert.Equal(1m, liveTuesday.RiskScale);
        Assert.Equal(new DateOnly(2026, 9, 4), liveTuesday.Vix!.Session);

        var liveWednesday = await liveGate.EntryDecisionAsync(Rule, scheduledProvider, ["1d"], wednesdayOpen, "AAA", null, default);
        Assert.Equal(RegimeCalendar.MarketOffReason, liveWednesday.Rejection);
    }

    // Pre-indexed O(log N) RegimeCalendar matches linear VixRegime.PreviousSession and RegimeCalendarBuilder.IsRegimeOnAt
    // identically even when KnownAtUtc timestamps arrive out of bar-date order.
    [Fact]
    public void PreIndexedCalendar_MatchesLinearPreviousSessionAndIsRegimeOnAtIncludingOutOfOrderKnownAt()
    {
        var baseDay = new DateOnly(2026, 5, 1);
        var bars = Enumerable.Range(0, 12).Select(i =>
        {
            var ts = new DateTimeOffset(baseDay.AddDays(i).ToDateTime(new TimeOnly(4, 0)), TimeSpan.Zero);
            // Delay bar 6's KnownAtUtc past bar 7 so KnownAtUtc order differs from Timestamp order.
            var known = i == 6 ? ts.AddDays(3) : ts.AddDays(1);
            return new OhlcvBar("SPY", ts, "1d", 100m + i, 100m + i, 100m + i, 100m + i, 1_000m, KnownAtUtc: known);
        }).ToArray();

        var calendar = RegimeCalendarBuilder.Build(bars, 3, risingLookback: 2);
        for (var i = 0; i < 16; i++)
        {
            var probe = new DateTimeOffset(baseDay.AddDays(i).ToDateTime(new TimeOnly(13, 30)), TimeSpan.Zero);
            Assert.Equal(VixRegime.PreviousSession(bars, probe), calendar.PreviousSessionAt(probe));
            Assert.Equal(RegimeCalendarBuilder.IsRegimeOnAt(bars, 3, probe, 2), calendar.IsOn(probe));
        }
    }

    // The backtest calendar gives the same reasons and scale; refusal precedence is strictly SPY -> Nasdaq/QQQ -> VIX.
    [Fact]
    public async Task RefusalPrecedence_SpyThenNasdaqThenVix_MatchesAcrossCalendarLiveAndDesk()
    {
        var fullRule = Rule with { NasdaqListingBenchmark = "QQQ" };
        var spyOn = Rising("SPY", new DateOnly(2026, 5, 15), 20);
        var spyOff = Falling("SPY", new DateOnly(2026, 5, 15), 20);
        var qqqOn = Rising("QQQ", new DateOnly(2026, 5, 15), 20);
        var qqqOff = Falling("QQQ", new DateOnly(2026, 5, 15), 20);
        var vixOff = VixCloseSeries.From([new(new DateOnly(2026, 6, 1), 25m)]);
        var vixHalf = VixCloseSeries.From([new(new DateOnly(2026, 6, 1), 20m)]);
        var june2 = new DateTimeOffset(2026, 6, 2, 13, 30, 0, TimeSpan.Zero);
        var exchanges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["MSFT"] = "NASDAQ", ["JPM"] = "NYSE" };

        // 1. SPY OFF beats QQQ OFF and VIX OFF.
        var calSpyOff = RegimeCalendarBuilder.Build(spyOff, 3).WithNasdaqHomeMarket(RegimeCalendarBuilder.Build(qqqOff, 3), exchanges).WithVix(fullRule, vixOff);
        Assert.Equal(RegimeCalendar.MarketOffReason, calSpyOff.RejectionFor(june2, "MSFT"));
        Assert.Equal(RegimeCalendar.MarketOffReason, RegimeGateService.Evaluate(fullRule, spyOff, june2, "NASDAQ", false, qqqOff, vixOff).Rejection);

        // 2. SPY ON + QQQ OFF beats VIX OFF for Nasdaq-listed MSFT; NYSE-listed JPM reaches VIX OFF.
        var calQqqOff = RegimeCalendarBuilder.Build(spyOn, 3).WithNasdaqHomeMarket(RegimeCalendarBuilder.Build(qqqOff, 3), exchanges).WithVix(fullRule, vixOff);
        Assert.Equal(RegimeCalendar.NasdaqMarketOffReason, calQqqOff.RejectionFor(june2, "MSFT"));
        Assert.Equal(VixRegime.OffReason, calQqqOff.RejectionFor(june2, "JPM"));
        Assert.Equal(RegimeCalendar.NasdaqMarketOffReason, RegimeGateService.Evaluate(fullRule, spyOn, june2, "NASDAQ", false, qqqOff, vixOff).Rejection);
        Assert.Equal(VixRegime.OffReason, RegimeGateService.Evaluate(fullRule, spyOn, june2, "NYSE", false, qqqOff, vixOff).Rejection);

        // 3. SPY ON + QQQ ON + VIX 20 -> allowed at 0.5x on calendar, live gate, and Desk ResolveRegime.
        var calHalf = RegimeCalendarBuilder.Build(spyOn, 3).WithNasdaqHomeMarket(RegimeCalendarBuilder.Build(qqqOn, 3), exchanges).WithVix(fullRule, vixHalf);
        var calDecision = calHalf.EntryDecisionFor(june2, "MSFT");
        Assert.Equal((null, 0.5m, 20m), (calDecision.Rejection, calDecision.RiskScale, calDecision.Vix!.Close));

        var assets = new Mock<IAssetTradingEligibilityProvider>();
        assets.Setup(x => x.GetEligibilityAsync("MSFT", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssetTradingEligibility("MSFT", true, true, false, june2, Exchange: "NASDAQ"));
        var liveDecision = await new RegimeGateService(new FixedVix(vixHalf)).EntryDecisionAsync(
            fullRule, new DailyProvider([.. spyOn, .. qqqOn]), ["1d"], june2, "MSFT", assets.Object, default);
        Assert.Equal((null, 0.5m, 20m), (liveDecision.Rejection, liveDecision.RiskScale, liveDecision.Vix!.Close));

        // Desk ResolveRegime uses the exact same shared evaluator and embeds vix + RiskScale in its evidence JSON.
        var schedules = BuildSchedules(new DateOnly(2026, 5, 1), new DateOnly(2026, 6, 10));
        var strategy = new SimpleYamlReader().ReadStrategy(Path.Combine(TestRepository.FindRoot(),
            "configs/backtest/strategies/bollinger-squeeze-breakout.bt-v1.yaml")) with { Regime = fullRule };
        var deskSpy = DeskBenchmarkBars("SPY", schedules, june2, 20);
        var deskQqq = DeskBenchmarkBars("QQQ", schedules, june2, 20);
        var benchmarks = new Dictionary<string, IReadOnlyList<OhlcvBar>>(StringComparer.OrdinalIgnoreCase) { ["SPY"] = deskSpy, ["QQQ"] = deskQqq };
        var deskHalf = StockStrategyEvidenceLoader.ResolveRegime(strategy, benchmarks, schedules, june2, "NASDAQ", vixHalf, out var missingHalf);
        Assert.Null(missingHalf);
        Assert.NotNull(deskHalf);
        Assert.True(deskHalf!.IsEligible);
        Assert.Contains("\"RiskScale\":0.5", deskHalf.EvidenceJson, StringComparison.Ordinal);
        Assert.Contains("\"Close\":20", deskHalf.EvidenceJson, StringComparison.Ordinal);

        var deskOff = StockStrategyEvidenceLoader.ResolveRegime(strategy, benchmarks, schedules, june2, "NASDAQ", vixOff, out var missingOff);
        Assert.Null(missingOff);
        Assert.NotNull(deskOff);
        Assert.False(deskOff!.IsEligible);
        Assert.Contains(VixRegime.OffReason, deskOff.EvidenceJson, StringComparison.Ordinal);
    }

    // The backtest calendar gives the same reasons and scale; the SPY rule is still checked first.
    [Fact]
    public void Calendar_CombinesTheMarketRuleWithVixSizing()
    {
        var spy = Rising("SPY", new DateOnly(2026, 5, 15), 20);
        var vix = VixCloseSeries.From([new(new DateOnly(2026, 6, 1), 20m), new(new DateOnly(2026, 6, 2), 23m)]);
        var calendar = RegimeCalendarBuilder.Build(spy, 3).WithVix(Rule, vix);

        var june2 = new DateTimeOffset(2026, 6, 2, 13, 30, 0, TimeSpan.Zero);
        Assert.Null(calendar.RejectionFor(june2, "AAA"));
        Assert.Equal(0.5m, calendar.EntryRiskScaleAt(june2));
        Assert.Equal(VixRegime.OffReason, calendar.RejectionFor(june2.AddDays(1), "AAA"));
        Assert.Equal(VixRegime.CloseUnknownReason, calendar.RejectionFor(june2.AddDays(2), "AAA"));
        Assert.Equal(1m, RegimeCalendarBuilder.Build(spy, 3).EntryRiskScaleAt(june2));
        var falling = RegimeCalendarBuilder.Build(Falling("SPY", new DateOnly(2026, 5, 15), 20), 3).WithVix(Rule, vix);
        Assert.Equal(RegimeCalendar.MarketOffReason, falling.RejectionFor(june2, "AAA"));
        Assert.Throws<InvalidOperationException>(() => RegimeCalendar.FromOnDates(new HashSet<DateOnly>()).WithVix(Rule, vix));
    }

    // Event-portfolio and intraday replays use one admission helper: refusals with reasons, and scales only below 1.
    [Fact]
    public void EntryAdmissions_ReturnsRefusalsAndReducedScalesPerCandidate()
    {
        var spy = Rising("SPY", new DateOnly(2026, 5, 15), 20);
        var vix = VixCloseSeries.From([new(new DateOnly(2026, 6, 1), 20m), new(new DateOnly(2026, 6, 2), 15m), new(new DateOnly(2026, 6, 3), 30m)]);
        var calendars = new Dictionary<string, RegimeCalendar> { ["s"] = RegimeCalendarBuilder.Build(spy, 3).WithVix(Rule, vix) };
        var candidates = new[] { Candidate("half", 2), Candidate("full", 3), Candidate("off", 4) };

        var (excluded, scales) = BacktestRunner.EntryAdmissions(candidates, null, calendars);

        Assert.Equal(new Dictionary<string, string> { ["off"] = VixRegime.OffReason }, excluded);
        Assert.Equal(new Dictionary<string, decimal> { ["half"] = 0.5m }, scales);
    }

    // End-to-end EventPortfolioExecution: half-risk candidate gets half shares (50 vs 100), >22 candidate is refused with VIX reason.
    [Fact]
    public async Task EventPortfolioExecution_ScalesHalfRiskQuantityAndRefusesVixOff()
    {
        var epoch = (new DateTimeOffset(2026, 6, 2, 13, 30, 0, TimeSpan.Zero) - DateTimeOffset.UnixEpoch).Ticks * 100L;
        var baseStrategy = StrategyDecisionKernelTests.CreateRequest().Strategy with { StrategyId = "s", StrategyName = "S" };
        var portfolio = new PortfolioConfig(10_000m, 1.0m, 100m, 4, 0m, 0m, 1, true);
        BacktestCandidateTrade EventCand(string id, string ticker) => new(ticker, "S", "long",
            ExecutionEventTime.ToUtc(epoch), 10m, 9m, 12m, ExecutionEventTime.ToUtc(epoch + 1_000_000_000_000L),
            0m, "time_exit", 1m, StrategyId: "s", CandidateId: id)
        {
            ExecutionReplay = new(ExecutionReplayPolicy.DailyRthSipPortfolioV1, DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddDays(1), DateTimeOffset.UnixEpoch.AddDays(2), [])
            { EventExecutionEndUtc = ExecutionEventTime.ToUtc(epoch + 2_000_000_000_000L) }
        };
        var candidates = new[] { EventCand("full", "AAA"), EventCand("half", "BBB"), EventCand("off", "CCC") };
        var excluded = new Dictionary<string, string> { ["off"] = VixRegime.OffReason };
        var scales = new Dictionary<string, decimal> { ["half"] = 0.5m };
        var request = new EventPortfolioReplayRequest("vix-event", portfolio, [baseStrategy], candidates, [], excluded, scales);

        SipPortfolioExecutionBatch Quote(string ticker, long ns) => new(
            new(ticker, epoch + ns, epoch + ns, ns + 10,
                [new("q" + ticker, epoch + ns, 9.95m, 10m, 10_000, 10_000, true)], [], true), true, "session", [], [], []);
        SipPortfolioExecutionBatch Print(string ticker, long ns, decimal price) => new(
            new(ticker, epoch + ns, epoch + ns, ns + 10, [],
                [new("t" + ticker + ns, epoch + ns, price, 10_000, true)], true), true, "session", [], [], []);

        var frames = new[]
        {
            new SipPortfolioExecutionFrame(epoch - 1, epoch - 1, [Quote("AAA", -1), Quote("BBB", -1), Quote("CCC", -1)]),
            new SipPortfolioExecutionFrame(epoch, epoch, [Print("AAA", 0, 10m), Print("BBB", 0, 10m), Print("CCC", 0, 10m)]),
            new SipPortfolioExecutionFrame(epoch + 1_000_000_000_000L, epoch + 1_000_000_000_000L, [Print("AAA", 1_000_000_000_000L, 10.5m), Print("BBB", 1_000_000_000_000L, 10.5m)])
        };
        static async IAsyncEnumerable<SipPortfolioExecutionFrame> Stream(SipPortfolioExecutionFrame[] items)
        { foreach (var item in items) { yield return item; await Task.CompletedTask; } }

        var policy = new ExecutionEventSimulationPolicy(new(100, 0, 0, 0, 0, 0, 0, 0, 0),
            ExecutionProtectionActivation.NativeParentFullyFilled, 30_000_000_000L, 0, true, 1, 1, "vix-fixture");
        var outcome = Assert.Single(await EventPortfolioExecution.ReplayManyAsync([request], Stream(frames), _ => policy));

        Assert.Equal(100, Assert.Single(outcome.Dispositions, d => d.CandidateId == "full").RequestedQuantity);
        Assert.Equal(50, Assert.Single(outcome.Dispositions, d => d.CandidateId == "half").RequestedQuantity);
        var off = Assert.Single(outcome.Dispositions, d => d.CandidateId == "off");
        Assert.False(off.Accepted);
        Assert.Equal(VixRegime.OffReason, off.Reason);
    }

    // End-to-end IntradayPortfolioExecution: half-risk candidate gets half risk budget (49 vs 99 shares at 1 bps slippage),
    // and the >22 candidate is refused with the VIX reason.
    [Fact]
    public void IntradayPortfolioExecution_ScalesHalfRiskQuantityAndRefusesVixOff()
    {
        var start = DateTimeOffset.Parse("2026-06-02T13:30:00Z");
        var day = new DateOnly(2026, 6, 2);
        var sessions = new Dictionary<DateOnly, MarketSessionSchedule> { [day] = new(day, true, new(9, 30), new(10, 15)) };
        var baseStrategy = StrategyDecisionKernelTests.CreateRequest().Strategy;
        var strategy = baseStrategy with
        {
            StrategyId = "s",
            StrategyName = "S",
            Timeframe = "5m",
            Execution = baseStrategy.Execution with { Timeframe = "5m", SlippageBps = 1m },
            Session = baseStrategy.Session with { IsContinuousMarket = false, UseExtendedHours = false }
        };
        var portfolio = new PortfolioConfig(10_000m, 1.0m, 100m, 4, 0m, 0m, 1, true, MaxBarParticipationPct: 100m);
        OhlcvBar[] TickerBars(string ticker) => Enumerable.Range(0, 9)
            .Select(i => new OhlcvBar(ticker, start.AddMinutes(i * 5), "5m", 10m, 10.5m, 9.5m, 10m, 10_000m, KnownAtUtc: start.AddMinutes(i * 5 + 5)))
            .ToArray();
        BacktestCandidateTrade IntraCand(string id, string ticker) => new(ticker, "S", "long", start.AddMinutes(10),
            10m, 9m, 13m, start.AddMinutes(40), null, "session_deadline", 1m, StrategyId: "s", CandidateId: id)
        {
            ExecutionReplay = new(ExecutionReplayPolicy.IntradayRthFiveMinuteV1, start, start, start.AddMinutes(40), []),
            IntradayExecution = new(10m, 0.01m, start.AddMinutes(10), start.AddMinutes(15), start.AddMinutes(40),
                9.8m, 10.2m, start.AddMinutes(0), start.AddMinutes(5), "retest")
        };

        var bars = TickerBars("AAA").Concat(TickerBars("BBB")).Concat(TickerBars("CCC")).ToArray();
        var candidates = new[] { IntraCand("full", "AAA"), IntraCand("half", "BBB"), IntraCand("off", "CCC") };
        var excluded = new Dictionary<string, string> { ["off"] = VixRegime.OffReason };
        var scales = new Dictionary<string, decimal> { ["half"] = 0.5m };

        var replay = IntradayPortfolioExecution.Replay(new(portfolio, [strategy], candidates, bars, sessions,
            start, start.AddMinutes(45), null, null, null, excluded, scales));

        Assert.Equal(99, Assert.Single(replay.Trades, t => t.Ticker == "AAA").ShareQuantity);
        Assert.Equal(49, Assert.Single(replay.Trades, t => t.Ticker == "BBB").ShareQuantity);
        Assert.DoesNotContain(replay.Trades, t => t.Ticker == "CCC");
        var off = Assert.Single(replay.Dispositions, d => d.CandidateId == "off");
        Assert.False(off.Accepted);
        Assert.Equal(VixRegime.OffReason, off.Reason);
    }

    // Independent BacktestOpportunityShadowReplay preserves both half-risk EntryRiskScale and exact VIX/Nasdaq refusal reasons.
    [Fact]
    public void ShadowOpportunityReplay_PreservesVixRiskScaleAndExactRefusalReasons()
    {
        var start = DateTimeOffset.Parse("2026-06-02T13:30:00Z");
        var baseStrategy = StrategyDecisionKernelTests.CreateRequest().Strategy;
        var strategy = baseStrategy with
        {
            StrategyId = "s",
            StrategyName = "S",
            Execution = baseStrategy.Execution with { SlippageBps = 0m }
        };
        var portfolio = new PortfolioConfig(10_000m, 1.0m, 100m, 4, 0m, 0m, 1, true, MaxBarParticipationPct: 100m);
        OhlcvBar Bar5m(int minute, decimal price = 10m) => new("X", start.AddMinutes(minute), "5m", price, price, price, price, 10_000m);
        BacktestCandidateTrade ShadowCand(string id) => new("X", "S", "long", start, 10m, 9m, 12m, start.AddMinutes(10), 0m,
            "technical_exit", 1m, StrategyId: "s", CandidateId: id)
        {
            ExecutionReplay = new(ExecutionReplayPolicy.DailyRthFiveMinuteV1, start.Date.AddHours(4),
                start.Date.AddDays(1).AddHours(4), start.AddMinutes(15), [start, start.AddMinutes(5), start.AddMinutes(10), start.AddMinutes(15)])
        };

        var snapshot = new BacktestOpportunitySnapshot("vix-shadow", portfolio, [strategy],
            [ShadowCand("full"), ShadowCand("half"), ShadowCand("vix_off"), ShadowCand("qqq_off")],
            [Bar5m(0), Bar5m(5), Bar5m(10, 11m), Bar5m(15, 11m)],
            [
                new BacktestOpportunityEntryEligibility("full", start, true, true, RegimeRejection: null, EntryRiskScale: 1m),
                new BacktestOpportunityEntryEligibility("half", start, true, true, RegimeRejection: null, EntryRiskScale: 0.5m),
                new BacktestOpportunityEntryEligibility("vix_off", start, true, false, RegimeRejection: VixRegime.OffReason, EntryRiskScale: 0m),
                new BacktestOpportunityEntryEligibility("qqq_off", start, true, false, RegimeRejection: RegimeCalendar.NasdaqMarketOffReason, EntryRiskScale: 0m)
            ]);

        var outcomes = BacktestOpportunityShadowReplay.Replay(snapshot).ToDictionary(o => o.Candidate.CandidateId);

        Assert.Equal(100, outcomes["full"].CompletedTrade!.ShareQuantity);
        Assert.Equal(50, outcomes["half"].CompletedTrade!.ShareQuantity);
        Assert.Equal("admission_rejected", outcomes["vix_off"].Status);
        Assert.True(outcomes["vix_off"].AdmissionRejectionCounts.ContainsKey("portfolio_" + VixRegime.OffReason));
        Assert.Equal("admission_rejected", outcomes["qqq_off"].Status);
        Assert.True(outcomes["qqq_off"].AdmissionRejectionCounts.ContainsKey("portfolio_" + RegimeCalendar.NasdaqMarketOffReason));
    }

    // The shared planner applies the one per-entry scale to the account risk budget and refuses an invalid scale.
    [Fact]
    public void SharedPlanner_ScalesTheRiskBudgetOnce()
    {
        OrderPlanningResult Plan(decimal scale) => new SharedOrderRiskPlanner().Plan(new OrderPlanningRequest("AAA", PlannedOrderSide.Long,
            10_000m, 10m, PlannedStopRequest.FromResolvedPrice(PlannedStopKind.Structural, 9m),
            new OrderPlanningRiskLimits(0.5m, 100m, scale), new EstimatedOrderExecutionCosts()));

        Assert.Equal(50, Plan(1m).Order!.Quantity);
        Assert.Equal(25, Plan(0.5m).Order!.Quantity);
        Assert.Equal(25m, Plan(0.5m).Order!.AccountRiskBudget);
        Assert.Equal(OrderPlanRejectionCode.InvalidRiskLimits, Plan(0m).Rejection!.Code);
        Assert.Equal(OrderPlanRejectionCode.InvalidRiskLimits, Plan(1.5m).Rejection!.Code);
    }

    // Paper/Desk: the live gate reads FRED's latest closes; a failed read or a missing source refuses with its own reason.
    [Fact]
    public async Task LiveGate_UsesTheSameRuleAndFailsClosed()
    {
        var spy = Rising("SPY", new DateOnly(2026, 10, 20), 30);
        var provider = new DailyProvider(spy);
        var now = new DateTimeOffset(2026, 11, 18, 14, 30, 0, TimeSpan.Zero);
        var source = new FixedVix(VixCloseSeries.From([new(new DateOnly(2026, 11, 17), 19.3m)]));

        var half = await new RegimeGateService(source).EntryDecisionAsync(Rule, provider, ["1d"], now, "AAA", null, default);
        Assert.Equal((null, 0.5m, 19.3m), (half.Rejection, half.RiskScale, half.Vix!.Close));
        Assert.Equal(VixRegime.LookupFailedReason, (await new RegimeGateService().EntryDecisionAsync(Rule, provider, ["1d"], now, "AAA", null, default)).Rejection);
        Assert.Equal(VixRegime.LookupFailedReason,
            (await new RegimeGateService(new FixedVix(null)).EntryDecisionAsync(Rule, provider, ["1d"], now, "AAA", null, default)).Rejection);
        var plain = await new RegimeGateService().EntryDecisionAsync(PlainRule, provider, ["1d"], now, "AAA", null, default);
        Assert.Equal((null, 1m), (plain.Rejection, plain.RiskScale));
    }

    // FredVixCloseSource caches a successful read for 15 minutes, does not cache failures, retries immediately after failure,
    // disposes every created HttpClient, and emits a structured warning log on failure.
    [Fact]
    public async Task FredVixCloseSource_CachesFor15MinutesDoesNotCacheFailuresAndLogsWarnings()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 11, 18, 15, 0, 0, TimeSpan.Zero));
        var calls = 0;
        var disposedClients = 0;
        var responses = new Queue<(HttpStatusCode Status, string Body)>([
            (HttpStatusCode.ServiceUnavailable, ""),
            (HttpStatusCode.OK, "observation_date,VIXCLS\n2026-11-17,19.1\n"),
            (HttpStatusCode.OK, "observation_date,VIXCLS\n2026-11-17,16.2\n")
        ]);
        var logger = new RecordingLogger<FredVixCloseSource>();
        using var source = new FredVixCloseSource(() =>
        {
            var next = responses.Dequeue();
            calls++;
            return new TrackingHttpClient(new FixedResponse(next.Status, next.Body), () => disposedClients++);
        }, clock, logger);

        // 1. Failure is not cached and emits a warning log.
        await Assert.ThrowsAsync<HttpRequestException>(() => source.LoadLatestAsync(default));
        Assert.Equal(1, calls);
        Assert.Equal(1, disposedClients);
        Assert.Contains(logger.Warnings, msg => msg.Contains("503", StringComparison.Ordinal) && msg.Contains(VixRegime.LookupFailedReason, StringComparison.Ordinal));

        // 2. Immediate retry succeeds and is cached within 15 minutes.
        var first = await source.LoadLatestAsync(default);
        Assert.True(first.TryGetClose(new(2026, 11, 17), out var c1) && c1 == 19.1m);
        clock.Advance(TimeSpan.FromMinutes(14));
        var cached = await source.LoadLatestAsync(default);
        Assert.Same(first, cached);
        Assert.Equal(2, calls);
        Assert.Equal(2, disposedClients);

        // 3. After 15 minutes the cache expires and FRED is queried again.
        clock.Advance(TimeSpan.FromMinutes(2));
        var refreshed = await source.LoadLatestAsync(default);
        Assert.True(refreshed.TryGetClose(new(2026, 11, 17), out var c2) && c2 == 16.2m);
        Assert.Equal(3, calls);
        Assert.Equal(3, disposedClients);
    }

    // The recipe's frozen VIX values, and the reader's refusals of partial, unknown, unordered or short-side settings.
    [Fact]
    public void Recipe_ParsesTheApprovedVixValues()
    {
        var strategy = new SimpleYamlReader().ReadStrategy(Path.Combine(TestRepository.FindRoot(),
            "configs/backtest/strategies/bollinger-squeeze-breakout.bt-v1.yaml"));

        Assert.Equal(("VIXCLS", 18m, 22m, 0.5m), (strategy.Regime!.VixSeries, strategy.Regime.VixFullRiskBelow!.Value,
            strategy.Regime.VixNoLongsAbove!.Value, strategy.Regime.VixReducedRiskScale!.Value));
        Assert.Equal("SPY:50:rising20:nasdaqQQQ:vixVIXCLS:18:22:0.5", strategy.Regime.EvidenceRule);
    }

    [Theory]
    [InlineData("  vix_series: VIXCLS\n", "")]
    [InlineData("  vix_series: VIXCLS", "  vix_series: VXN")]
    [InlineData("  vix_no_new_longs_above: 22", "  vix_no_new_longs_above: 17")]
    [InlineData("  vix_reduced_risk_scale: 0.5", "  vix_reduced_risk_scale: 1")]
    [InlineData("direction: long", "direction: short")]
    public void Recipe_RefusesInvalidVixSettings(string original, string replacement)
    {
        var text = File.ReadAllText(Path.Combine(TestRepository.FindRoot(), "configs/backtest/strategies/bollinger-squeeze-breakout.bt-v1.yaml"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains(original, text);
        var path = Path.Combine(Path.GetTempPath(), "tf-vix-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            File.WriteAllText(path, text.Replace(original, replacement, StringComparison.Ordinal));
            Assert.Throws<InvalidOperationException>(() => new SimpleYamlReader().ReadStrategy(path));
        }
        finally { File.Delete(path); }
    }

    // The run-file writer writes the four values the reader needs, and strategies without VIX keep their serialized rules.
    [Fact]
    public void WriterRoundTripsVixSizing_AndUnsetFieldsLeaveSerializedRulesUnchanged()
    {
        var reader = new SimpleYamlReader();
        var strategy = reader.ReadStrategy(Path.Combine(TestRepository.FindRoot(), "configs/backtest/strategies/bollinger-squeeze-breakout.bt-v1.yaml"));
        var path = Path.Combine(Path.GetTempPath(), "tf-vix-roundtrip-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            File.WriteAllText(path, RunConfigWriter.SerializeStrategyYaml(strategy));
            Assert.Equal(strategy.Regime, reader.ReadStrategy(path).Regime);
        }
        finally { File.Delete(path); }
        var json = JsonSerializer.Serialize(new RegimeRules("SPY", RegimeRules.PriceAboveSma, 50, 20, "QQQ"));
        Assert.DoesNotContain("Vix", json, StringComparison.Ordinal);
        Assert.DoesNotContain("vix", RunConfigWriter.SerializeStrategyYaml(strategy with { Regime = strategy.Regime! with { VixSeries = null,
            VixFullRiskBelow = null, VixNoLongsAbove = null, VixReducedRiskScale = null } }), StringComparison.Ordinal);
    }

    // research-daily-screen --fred-vix stores FRED's exact bytes and a receipt; the manifest fingerprints both, a backtest reads
    // them back verified (both CSV and receipt hashes), respects ResearchObservationBoundary truncation, refuses overwriting
    // pre-existing VIX files even with --fred-vix, and refuses a screen published without the copy.
    [Fact]
    public async Task FrozenCopy_IsStoredVerifiedTruncatedAtBoundaryAndNeverOverwritten()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tf-vix-screen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "eligible.csv"), "session_date,ticker\n");
            const string fred = "observation_date,VIXCLS\n2026-10-29,17.1\n2026-10-30,18.4\n2026-11-02,21.0\n";
            using var http = new HttpClient(new FixedResponse(HttpStatusCode.OK, fred));
            var output = new StringWriter();
            await DailyScreenCommand.WriteFrozenVixAsync("VIXCLS", folder, new DateOnly(2026, 10, 30), http,
                new DateTimeOffset(2026, 11, 18, 22, 0, 0, TimeSpan.Zero), output, default);
            Assert.Contains("3 closes from 2026-10-29 to 2026-11-02, HTTP 200", output.ToString());
            var receiptPath = Path.Combine(folder, ResearchScreenManifest.FredVixReceiptFileName);
            var receiptText = await File.ReadAllTextAsync(receiptPath);
            Assert.Contains("\"requested_at_utc\": \"2026-11-18T22:00:00.0000000Z\"", receiptText);
            var sha = await PublishAsync(folder);

            var snapshot = PublishedResearchScreen.ReadFrozenVix(folder, sha);
            Assert.Equal(3, snapshot.Closes.Count);
            Assert.Equal(ResearchScreenManifest.Sha256(System.Text.Encoding.UTF8.GetBytes(fred)), snapshot.FileSha256);
            Assert.Equal(ResearchScreenManifest.Sha256(await File.ReadAllBytesAsync(receiptPath)), snapshot.ReceiptSha256);
            Assert.Null(snapshot.ProtectedCutoffDate);

            // ResearchObservationBoundary at 2026-10-31T04:00:00Z (midnight EDT 2026-10-31) keeps <= 2026-10-30 and drops 2026-11-02.
            using (ResearchObservationBoundary.Enter(new DateTimeOffset(2026, 10, 31, 4, 0, 0, TimeSpan.Zero)))
            {
                var bounded = PublishedResearchScreen.ReadFrozenVix(folder, sha);
                Assert.Equal(2, bounded.Closes.Count);
                Assert.Equal(new DateOnly(2026, 10, 31), bounded.ProtectedCutoffDate);
                Assert.False(bounded.Closes.TryGetClose(new DateOnly(2026, 11, 2), out _));
            }

            // Tampering with the receipt JSON refuses the screen even when the CSV bytes are untouched.
            await File.WriteAllTextAsync(receiptPath, receiptText + " ");
            Assert.Throws<InvalidDataException>(() => PublishedResearchScreen.ReadFrozenVix(folder, sha));
            await File.WriteAllTextAsync(receiptPath, receiptText);

            // Tampering with the CSV refuses the screen.
            await File.AppendAllTextAsync(Path.Combine(folder, ResearchScreenManifest.FredVixFileName), "2026-11-03,40\n");
            Assert.Throws<InvalidDataException>(() => PublishedResearchScreen.ReadFrozenVix(folder, sha));

            // Pre-existing VIX files are refused both without --fred-vix and with --fred-vix VIXCLS (never overwritten).
            await Assert.ThrowsAsync<InvalidOperationException>(() => DailyScreenCommand.WriteFrozenVixAsync(null, folder,
                new DateOnly(2026, 10, 30), http, DateTimeOffset.UtcNow, output, default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => DailyScreenCommand.WriteFrozenVixAsync("VIXCLS", folder,
                new DateOnly(2026, 10, 30), http, DateTimeOffset.UtcNow, output, default));

            File.Delete(Path.Combine(folder, ResearchScreenManifest.FredVixFileName));
            File.Delete(receiptPath);
            var withoutCopy = await PublishAsync(folder);
            Assert.Contains("--fred-vix", Assert.Throws<InvalidDataException>(() => PublishedResearchScreen.ReadFrozenVix(folder, withoutCopy)).Message);
            using var failing = new HttpClient(new FixedResponse(HttpStatusCode.ServiceUnavailable, ""));
            await Assert.ThrowsAsync<HttpRequestException>(() => DailyScreenCommand.WriteFrozenVixAsync("VIXCLS", folder,
                new DateOnly(2026, 10, 30), failing, DateTimeOffset.UtcNow, output, default));
            Assert.False(File.Exists(Path.Combine(folder, ResearchScreenManifest.FredVixFileName)));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // ResearchScreenReference carries FredVixSha256 into SameInputs and ResearchQueue.WorkflowVariant, and pre-flight
    // validation refuses missing VIX copy or missing security master before trial registration.
    [Fact]
    public async Task ScreenReferenceAndPreflight_RecordVixFingerprintAndRefuseMissingInputsBeforeTrialStart()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tf-vix-ref-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "eligible.csv"), "session_date,ticker\n2026-10-30,AAA\n");
            using var http = new HttpClient(new FixedResponse(HttpStatusCode.OK, "observation_date,VIXCLS\n2026-10-30,18.4\n"));
            await DailyScreenCommand.WriteFrozenVixAsync("VIXCLS", folder, new DateOnly(2026, 10, 30), http,
                new DateTimeOffset(2026, 11, 18, 22, 0, 0, TimeSpan.Zero), TextWriter.Null, default);
            var manifestSha = await PublishAsync(folder);
            var screen = PublishedResearchScreen.Read(folder, manifestSha);

            var reference = ResearchScreenSources.ResolveReference(screen, []);
            Assert.Equal(screen.Manifest.Outputs[ResearchScreenManifest.FredVixFileName], reference.FredVixSha256);
            Assert.False(reference.SameInputs(reference with { FredVixSha256 = "different" }));

            var strategyRef = new StrategyReference("technical.bollinger-squeeze-breakout", "1.0.0", new string('a', 64));
            var runReq = new StartBacktestRequest(ContractVersions.VersionOne, "r", Guid.NewGuid(), [strategyRef],
                new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero),
                100_000m, 0.5m, 20m, 10, "use_cache", "full", "k");
            var variant = ResearchQueue.WorkflowVariant(strategyRef, runReq, reference, null);
            Assert.Contains("fred-vixcls:" + reference.FredVixSha256, variant.DatasetIds);

            var vixSettings = new BacktestResolvedSettingsResponse("cfg", new string('b', 64), 0m, 0m, 0m, 0m, 0m, 10m, 0m, null, null,
                [new BacktestStrategySettingsResponse(strategyRef, "Squeeze", 5m, null, null, RequiresFrozenVix: true, RequiresSecurityMaster: true)]);
            Assert.Throws<InvalidOperationException>(() => RunWorkflowService.RequireRegimeScreenInputs(vixSettings, null));
            Assert.Throws<InvalidOperationException>(() => RunWorkflowService.RequireRegimeScreenInputs(vixSettings, reference with { FredVixSha256 = null }));
            Assert.Throws<InvalidOperationException>(() => RunWorkflowService.RequireRegimeScreenInputs(vixSettings, reference with { SecurityMasterDatasetId = null }));
            // Complete screen with both FredVixSha256 and SecurityMasterDatasetId passes pre-flight cleanly.
            RunWorkflowService.RequireRegimeScreenInputs(vixSettings, reference with { SecurityMasterDatasetId = "sec-master-1" });
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // Publishes a minimal manifest over whatever outputs the folder holds.
    private static Task<string> PublishAsync(string folder) => ResearchScreenManifest.PublishAsync(folder,
        new ResearchScreenInputs("daily", "actions", null, "sessions"),
        new DailyScreenRules(5m, DailyScreenPriceBasis.PriorClose, 20, MinAverageDollarVolume: 20_000_000m, MinHistorySessions: 253),
        null, new DateOnly(2026, 5, 1), new DateOnly(2026, 10, 30), "test", null, default);

    private static IReadOnlyDictionary<DateOnly, MarketSessionSchedule> BuildSchedules(
        DateOnly from, DateOnly to, DateOnly[]? holidays = null, bool allDaysTrading = false)
    {
        var holidaySet = (holidays ?? []).ToHashSet();
        var map = new Dictionary<DateOnly, MarketSessionSchedule>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var open = allDaysTrading
                ? !holidaySet.Contains(day)
                : day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !holidaySet.Contains(day);
            map[day] = new MarketSessionSchedule(day, open, new(9, 30), new(16, 0));
        }
        return map;
    }

    // Builds rising SIP/all daily benchmark bars stamped at New York midnight matching Desk MissingBenchmark checks.
    private static OhlcvBar[] DeskBenchmarkBars(string symbol, IReadOnlyDictionary<DateOnly, MarketSessionSchedule> schedules,
        DateTimeOffset asOf, int count)
    {
        var zone = VixRegime.NewYorkTimeZone;
        return TradingFlow.Engine.Strategies.TechnicalFactorCalculator.ExpectedCompletedSessions(schedules, asOf, count)
            .Select((day, i) =>
            {
                var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
                var stamp = new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
                return new OhlcvBar(symbol, stamp, "1d", 100m + i, 101m + i, 99m + i, 100m + i,
                    1_000_000m, "sip", "all", stamp.AddDays(1), asOf);
            }).ToArray();
    }

    private static OhlcvBar[] RisingTradingDays(string symbol, IReadOnlyDictionary<DateOnly, MarketSessionSchedule> schedules, DateOnly from, DateOnly to) =>
        schedules.Where(pair => pair.Key >= from && pair.Key <= to && pair.Value.IsTradingDay)
            .OrderBy(pair => pair.Key)
            .Select((pair, i) => new OhlcvBar(symbol, new DateTimeOffset(pair.Key.ToDateTime(new TimeOnly(4, 0)), TimeSpan.Zero),
                "1d", 100m + i, 100m + i, 100m + i, 100m + i, 1_000m))
            .ToArray();

    private static OhlcvBar Bar(string symbol, DateOnly day) =>
        new(symbol, new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddHours(4), "1d", 100m, 100m, 100m, 100m, 1_000m);

    private static OhlcvBar[] Rising(string symbol, DateOnly from, int days) => Enumerable.Range(0, days)
        .Select(i => new OhlcvBar(symbol, new DateTimeOffset(from.AddDays(i).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), "1d",
            100m + i, 100m + i, 100m + i, 100m + i, 1_000m)).ToArray();

    private static OhlcvBar[] Falling(string symbol, DateOnly from, int days) => Enumerable.Range(0, days)
        .Select(i => new OhlcvBar(symbol, new DateTimeOffset(from.AddDays(i).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), "1d",
            200m - i, 200m - i, 200m - i, 200m - i, 1_000m)).ToArray();

    // A long candidate of strategy "s" entering at 13:31 UTC on the given June 2026 day.
    private static BacktestCandidateTrade Candidate(string id, int juneDay) => new("AAA", "S", "long",
        new DateTimeOffset(2026, 6, juneDay, 13, 31, 0, TimeSpan.Zero), 10m, 9m, 12m,
        new DateTimeOffset(2026, 6, juneDay, 13, 40, 0, TimeSpan.Zero), 10m, "time_exit", 1m, StrategyId: "s", CandidateId: id);

    // A VIX source returning fixed closes, or failing like a refused HTTP request when given none.
    private sealed class FixedVix(VixCloseSeries? closes) : IVixCloseSource
    {
        public Task<VixCloseSeries> LoadLatestAsync(CancellationToken cancellationToken) =>
            closes is null ? throw new HttpRequestException("FRED returned HTTP 503.") : Task.FromResult(closes);
    }

    // Daily bars of one or more benchmarks, filtered to the requested window.
    private sealed class DailyProvider(IReadOnlyList<OhlcvBar> bars) : IMarketDataProvider
    {
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> timeframes,
            DateTimeOffset start, DateTimeOffset end, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (timeframes.First() == "1d")
                foreach (var bar in bars.Where(bar => tickers.Contains(bar.Ticker) && bar.Timestamp >= start && bar.Timestamp <= end))
                    yield return bar;
            await Task.CompletedTask;
        }
    }

    // Market data provider supplying both daily bars and authoritative session schedules.
    private sealed class ScheduledDailyProvider(IReadOnlyList<OhlcvBar> bars, IReadOnlyDictionary<DateOnly, MarketSessionSchedule> schedules)
        : IMarketDataProvider, IMarketSessionScheduleProvider
    {
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> timeframes,
            DateTimeOffset start, DateTimeOffset end, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (timeframes.First() == "1d")
                foreach (var bar in bars.Where(bar => tickers.Contains(bar.Ticker) && bar.Timestamp >= start && bar.Timestamp <= end))
                    yield return bar;
            await Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>> LoadMarketSessionSchedulesAsync(
            DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>>(
                schedules.Where(pair => pair.Key >= startDate && pair.Key <= endDate)
                    .ToDictionary(pair => pair.Key, pair => pair.Value));
    }

    // One fixed HTTP response for every request.
    private sealed class FixedResponse(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    // Tracks HttpClient disposal in FredVixCloseSource tests.
    private sealed class TrackingHttpClient(HttpMessageHandler handler, Action onDispose) : HttpClient(handler)
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing) onDispose();
            base.Dispose(disposing);
        }
    }

    // Controllable UTC clock for deterministic cache-lifetime tests.
    private sealed class MutableClock(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset current = initial;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan delta) => current = current.Add(delta);
    }

    // Captures warning log messages emitted during failure tests.
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}
