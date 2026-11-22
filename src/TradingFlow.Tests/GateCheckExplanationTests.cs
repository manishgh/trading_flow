using System.Text.Json;
using TradingFlow.Application.Candidates;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Regime;

namespace TradingFlow.Tests;

public sealed class GateCheckExplanationTests
{
    private static readonly IReadOnlyDictionary<string, string?> NoInputs = new Dictionary<string, string?>();

    // Every number in the sentence is one the engine recorded with the code.
    [Theory]
    [InlineData("average_range_pct_below_minimum (Actual: 1.82, Required: 2.50)", "20-day ADR 1.82%: needs at least 2.50%")]
    [InlineData("average_range_pct_below_minimum (Actual: n/a, Required: 2.50)", "20-day ADR no value available: needs at least 2.50%")]
    [InlineData("average_dollar_volume_below_minimum (Actual: 12345678, Required: 20000000)", "20-day average dollar volume $12,345,678: needs at least $20,000,000")]
    [InlineData("rsi_outside_range (Actual: 74.10, Required: 30.00-70.00)", "RSI 74.10: needs between 30.00 and 70.00")]
    [InlineData("short_rsi_below_range (Actual: 35.00, RequiredMin: 50.00)", "Short RSI 35.00: needs at least 50.00")]
    [InlineData("gap_up_below_minimum (Actual: -0.40, Required: 2.00)", "Gap up -0.40%: needs at least 2.00%")]
    [InlineData("catalyst_entry_too_extended (Actual: 9.10, RequiredMax: 6.00)", "Price move since the news 9.10%: needs at most 6.00%")]
    [InlineData("price_too_far_below_52_week_high (Actual: -31.00, RequiredMin: -25.00)", "Price versus the 52-week high -31.00%: needs at least -25.00%")]
    [InlineData("close_not_far_enough_below_sma5 (Actual: 0.40, Required: 1.00)", "Close below its 5-day average by 0.40%: needs at least 1.00%")]
    [InlineData("setup_news_orb_swing_not_triggered (opening_relative_volume_below_minimum (Actual: 0.80, Required: 1.50))", "Setup news ORB swing not triggered: Opening relative volume 0.80×: needs at least 1.50×")]
    [InlineData("setup_news_orb_swing_not_triggered (opening_range_reclaimed_session_vwap)", "Setup news ORB swing not triggered: Opening range reclaimed session VWAP")]
    public void Comparison_NamesTheMeasuredValueAndTheRequirement(string code, string expected) =>
        Assert.Equal(expected, GateCheckExplanation.Describe(code, NoInputs));

    [Theory]
    [InlineData("price_not_above_sma50", "Price not above SMA50")]
    [InlineData("outside_session_window", "Outside the strategy's trading hours")]
    [InlineData("REJECT_WIDE_SPREAD", "Wide spread")]
    [InlineData("completed_bar", "Completed bar")]
    [InlineData("daily_close_not_above_opening_range_high (Close: 10.00, OpeningHigh: 12.00)", "Daily close not above opening range high: close 10.00, opening high 12.00")]
    [InlineData("technical_factor_unavailable (relative_roc)", "Technical factor unavailable: Relative roc")]
    [InlineData("setup_ema21_pullback_reclaim_not_triggered (Reason: no_pullback_bar, LookbackBars: 5, MaxAtr: 0.50)", "Setup EMA21 pullback reclaim not triggered: reason no pullback bar, lookback bars 5, max atr 0.50")]
    [InlineData("confluence_missing_ema20 (Timeframe: 1d, Bar: 2026-11-20)", "Confluence missing EMA20: timeframe 1d, bar 2026-11-20")]
    [InlineData("fresh_news_veto (AgeHours: 2.0, VetoWithin: 24.0, Headline: Profit, loss and a (short) warning)", "Fresh news veto: age hours 2.0, veto within 24.0, headline Profit, loss and a (short) warning")]
    public void OtherCodes_UseTheirSentenceOrReadableName(string code, string expected) =>
        Assert.Equal(expected, GateCheckExplanation.Describe(code, NoInputs));

    // The exact decision records the engine serializes, so a renamed property fails here rather than on the page.
    [Fact]
    public void Regime_ExplainsTheRecordedReasonWithItsOwnNumbers()
    {
        var session = new DateOnly(2026, 11, 19);
        var rule = new RegimeRules("SPY", RegimeRules.PriceAboveSma, 50, 20) with { VixFullRiskBelow = 18m, VixNoLongsAbove = 22m, VixReducedRiskScale = 0.5m };
        var vix = new VixEntryDecision(VixRegime.OffReason, 0m, session, 28.44m);

        Assert.Equal("VIX 28.44 is above the long limit of 22 (close of 2026-11-19)",
            Regime(new { eligible = false, rule, rejection = VixRegime.OffReason, vix }));
        Assert.Equal("VIX 28.44 is above the long limit of 22 (close of 2026-11-19)",
            Regime(new { eligible = false, Regime = rule, regimeRejection = VixRegime.OffReason, vix }));
        Assert.Equal("The VIX close of the previous session (close of 2026-11-19) is not available, so new entries are refused",
            Regime(new { rule, rejection = VixRegime.CloseUnknownReason, vix = vix with { Close = null } }));
        Assert.Equal("SPY and its 50-day average do not meet the strategy's market rule, or their recent bars could not be checked",
            Regime(new { rule, rejection = RegimeCalendar.MarketOffReason }));
        Assert.Equal("QQQ, the home market of Nasdaq listings, does not meet the strategy's market rule, or its recent bars could not be checked",
            Regime(new { rule = rule with { NasdaqListingBenchmark = "QQQ" }, rejection = RegimeCalendar.NasdaqMarketOffReason }));
        Assert.Equal("The VIX source could not be read, so new entries are refused",
            Regime(new { rule, rejection = VixRegime.LookupFailedReason }));
        Assert.Equal("Short not easy to borrow", Regime(new { eligible = false, rejection = TradingFlow.Engine.Execution.ShortBorrowEligibility.NotEasyToBorrowReason }));
        Assert.Equal("The market rule does not allow new entries", GateCheckExplanation.Describe("market_regime_off", NoInputs));
        Assert.Equal("The market rule does not allow new entries",
            GateCheckExplanation.Describe("market_regime_off", new Dictionary<string, string?> { ["EvidenceJson"] = "not json" }));
    }

    // Two order gates sharing one reject code stay distinguishable by their recorded values; nested objects are left out.
    [Fact]
    public void OrderGate_ShowsItsRecordedValues()
    {
        Assert.Equal("Degraded data: quote age ms 4200, limit 2000", GateCheckExplanation.DescribeOrderGate("REJECT_DEGRADED_DATA",
            new Dictionary<string, string?> { ["QuoteAgeMs"] = "4200", ["limit"] = "2000", ["quote"] = "{\"bid\":1}" }));
        Assert.Equal("Passed", GateCheckExplanation.DescribeOrderGate(null, NoInputs));
    }

    [Fact]
    public void Warmup_ListsOnlyTimeframesStillShortOfBars()
    {
        var inputs = new Dictionary<string, string?>
        {
            ["CompletedBarsByTimeframe"] = """{"1d":120,"5m":400}""",
            ["RequiredWarmupBarsByTimeframe"] = """{"1d":260,"5m":100,"1h":50,"15m":2.5}"""
        };
        Assert.Equal("Not enough completed bars are loaded yet: 1d 120 of 260, 1h 0 of 50",
            GateCheckExplanation.Describe("market_state_not_warm", inputs));
        Assert.Equal("Not enough completed bars are loaded yet", GateCheckExplanation.Describe("market_state_not_warm", NoInputs));
    }

    // Describes a regime refusal carrying the given evidence object, as the engine records it.
    private static string Regime(object evidence) => GateCheckExplanation.Describe("market_regime_off",
        new Dictionary<string, string?> { ["IsEligible"] = "false", ["EvidenceJson"] = JsonSerializer.Serialize(evidence) });
}
