using TradingFlow.Research.Signals;

namespace TradingFlow.Tests;

public sealed class SignalStudyAnalyzerTests
{
    private const string Identity = "swing.connors-rsi2@7.0.0";
    private static readonly DateOnly Start = new(2018, 6, 1);
    private static readonly SignalStudyPassRules SmallSample = new(MinimumTrades: 10, MinimumSignalDates: 10, MinimumIssuers: 3);

    private static EligibleUniverseDay[] Universe(int days, double value = 0.0002) =>
        Enumerable.Range(0, days).Select(index => new EligibleUniverseDay(Start.AddDays(index),
            value + (index % 2 == 0 ? 0.0001 : -0.0001))).ToArray();

    // Small day-to-day variation keeps the statistics defined while the mean edge stays exact.
    private static SignalOutcome[] Signals(int count, double dailyReturn, int spacing = 5, bool unviewedFirstHalf = true) =>
        Enumerable.Range(0, count).Select(index => new SignalOutcome($"T{index % 40}", $"I{index % 40}", Start.AddDays(index * spacing),
            [new SignalHoldingDay(Start.AddDays(index * spacing + 1), dailyReturn + (index % 2 == 0 ? 0.001 : -0.001)),
             new SignalHoldingDay(Start.AddDays(index * spacing + 2), dailyReturn + (index % 3 == 0 ? 0.001 : -0.0005))],
            0.0005, Unviewed: unviewedFirstHalf && index < count / 2)).ToArray();

    [Fact]
    public void CostsAndSameDayUniverseAreRemovedFromEachTrade()
    {
        var outcome = new SignalOutcome("A", "A", Start, [new SignalHoldingDay(Start.AddDays(1), 0.02), new SignalHoldingDay(Start.AddDays(2), 0.01)], 0.001);
        var universe = new[] { new EligibleUniverseDay(Start.AddDays(1), 0.005), new EligibleUniverseDay(Start.AddDays(2), 0.005) };

        var report = SignalStudyAnalyzer.Analyze(Identity, [outcome], universe, SmallSample);

        var expected = 1.02 * 1.01 * 0.999 * 0.999 - 1 - (1.005 * 1.005 - 1);
        Assert.Equal(expected, report.MeanNetExcessPerTrade, 12);
        Assert.Equal(Identity, report.StrategyIdentity);
    }

    [Fact]
    public void AStrongSignalThatMeetsEveryRuleAdvancesToHoldout()
    {
        var count = 2400;
        var report = SignalStudyAnalyzer.Analyze(Identity, Signals(count, 0.004, spacing: 1), Universe(count + 5), SmallSample);

        Assert.Equal(SignalStudyAnalyzer.AdvanceToHoldout, report.Decision);
        Assert.True(report.DailyExcessT >= 3);
    }

    [Fact]
    public void ANegativeUnviewedPeriodRejectsEvenWhenTheWholeSampleIsPositive()
    {
        var unviewedLosses = Signals(100, -0.002).Select(outcome => outcome with { Unviewed = true }).ToArray();
        var viewedGains = Signals(1500, 0.01, spacing: 1, unviewedFirstHalf: false)
            .Select(outcome => outcome with { Ticker = "V" + outcome.Ticker, IssuerId = "V" + outcome.IssuerId }).ToArray();

        var report = SignalStudyAnalyzer.Analyze(Identity, [.. unviewedLosses, .. viewedGains], Universe(1600), SmallSample);

        Assert.True(report.MeanNetExcessPerTrade > 0);
        Assert.Equal(SignalStudyAnalyzer.Reject, report.Decision);
        Assert.Contains(report.Reasons, reason => reason.Contains("unviewed", StringComparison.Ordinal));
    }

    [Fact]
    public void ASignalNoBetterThanTheUniverseIsRejected()
    {
        var report = SignalStudyAnalyzer.Analyze(Identity, Signals(400, 0.0002), Universe(2100), SmallSample);

        Assert.Equal(SignalStudyAnalyzer.Reject, report.Decision);
    }

    [Fact]
    public void TooFewTradesAreRetainedRatherThanRejectedOrAdvanced()
    {
        var weak = SignalStudyAnalyzer.Analyze(Identity, Signals(5, -0.01), Universe(60), new SignalStudyPassRules());
        var strong = SignalStudyAnalyzer.Analyze(Identity, Signals(2400, 0.004, spacing: 1), Universe(2405), SmallSample with { MinimumTrades = 5000 });

        Assert.Equal(SignalStudyAnalyzer.RetainResearch, weak.Decision);
        Assert.Equal(SignalStudyAnalyzer.RetainResearch, strong.Decision);
        Assert.Contains(strong.Reasons, reason => reason.Contains("Trades", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingOutcomesCannotHideALossLargerThanTheHurdle()
    {
        var count = 2400;
        var outcomes = Signals(count, 0.001, spacing: 1).Select((outcome, index) => outcome with { OutcomeKnown = index % 200 != 0 }).ToArray();

        var report = SignalStudyAnalyzer.Analyze(Identity, outcomes, Universe(count + 5), SmallSample);

        Assert.InRange(report.MissingOutcomeShare, 0.004, 0.006);
        Assert.True(report.WorstCaseMeanNetExcessPerTrade < report.MeanNetExcessPerTrade - 0.001);
    }

    [Fact]
    public void TooManyMissingOutcomesBlockAdvancing()
    {
        var count = 2400;
        var outcomes = Signals(count, 0.004, spacing: 1).Select((outcome, index) => outcome with { OutcomeKnown = index % 10 != 0 }).ToArray();

        Assert.Equal(SignalStudyAnalyzer.RetainResearch, SignalStudyAnalyzer.Analyze(Identity, outcomes, Universe(count + 5), SmallSample).Decision);
    }

    [Fact]
    public void DaysWithNoOpenTradeCountAsZeroSoTimeIsNotCompressed()
    {
        var report = SignalStudyAnalyzer.Analyze(Identity, Signals(100, 0.003, spacing: 10), Universe(1100), SmallSample);

        Assert.InRange(report.ActiveDayShare, 0.15, 0.25);
    }

    [Fact]
    public void MarketSensitivityIsMeasuredSoExposureIsNotMistakenForSkill()
    {
        var universe = Enumerable.Range(0, 300).Select(index => new EligibleUniverseDay(Start.AddDays(index), index % 2 == 0 ? 0.01 : -0.008)).ToArray();
        var outcomes = Enumerable.Range(0, 298).Select(index => new SignalOutcome("A", "A", Start.AddDays(index),
            [new SignalHoldingDay(Start.AddDays(index + 1), 1.5 * universe[index + 1].EqualWeightReturn)], 0)).ToArray();

        var report = SignalStudyAnalyzer.Analyze(Identity, outcomes, universe, SmallSample);

        Assert.Equal(1.5, report.MarketSensitivity, 6);
        Assert.Equal(0, report.DailyInterceptAfterMarketSensitivity, 6);
    }

    [Fact]
    public void AYearWithAHandfulOfTradesDoesNotCountAsAPositiveYear()
    {
        var outcomes = Signals(5, 0.01);

        var report = SignalStudyAnalyzer.Analyze(Identity, outcomes, Universe(60), SmallSample);

        Assert.Equal(0, report.PositiveYearShare);
    }

    [Fact]
    public void AConstantSeriesEarnsNoStatisticalCredit()
    {
        var universe = Enumerable.Range(0, 40).Select(index => new EligibleUniverseDay(Start.AddDays(index), 0)).ToArray();
        var outcomes = Enumerable.Range(0, 38).Select(index => new SignalOutcome("A", "A", Start.AddDays(index),
            [new SignalHoldingDay(Start.AddDays(index + 1), 0.01)], 0)).ToArray();

        Assert.Equal(0, SignalStudyAnalyzer.Analyze(Identity, outcomes, universe, SmallSample).DailyExcessT);
    }

    [Fact]
    public void InvalidHoldingDaysAreErrorsNotSilentValues()
    {
        var universe = Universe(10);
        var outside = new SignalOutcome("A", "A", Start, [new SignalHoldingDay(Start.AddDays(50), 0.01)], 0);
        var early = new SignalOutcome("A", "A", Start.AddDays(3), [new SignalHoldingDay(Start.AddDays(3), 0.01)], 0);
        var disordered = new SignalOutcome("A", "A", Start, [new SignalHoldingDay(Start.AddDays(2), 0.01), new SignalHoldingDay(Start.AddDays(1), 0.01)], 0);

        Assert.Throws<InvalidDataException>(() => SignalStudyAnalyzer.Analyze(Identity, [outside], universe, SmallSample));
        Assert.Throws<InvalidDataException>(() => SignalStudyAnalyzer.Analyze(Identity, [early], universe, SmallSample));
        Assert.Throws<InvalidDataException>(() => SignalStudyAnalyzer.Analyze(Identity, [disordered], universe, SmallSample));
    }

    [Fact]
    public void ResultsAreReportedByMonthAndRepeatExactly()
    {
        var outcomes = Signals(600, 0.002);

        var first = SignalStudyAnalyzer.Analyze(Identity, outcomes, Universe(3100), SmallSample);
        var second = SignalStudyAnalyzer.Analyze(Identity, outcomes, Universe(3100), SmallSample);

        Assert.Equal(first.BootstrapLowerBound, second.BootstrapLowerBound);
        Assert.Equal(600, first.Months.Sum(month => month.Trades));
        Assert.Equal(300, first.Months.Sum(month => month.UnviewedTrades));
        Assert.Equal(first.Months.OrderBy(month => (month.Year, month.Month)), first.Months);
    }
}
