using TradingFlow.Research.Signals;

namespace TradingFlow.Tests;

public sealed class SignalStudyAnalyzerTests
{
    private static readonly DateOnly Start = new(2018, 6, 1);
    private static readonly SignalStudyPassRules SmallSample = new(MinimumTrades: 10, MinimumSignalDates: 10, MinimumIssuers: 3);

    private static EligibleUniverseDay[] Universe(int days, double value = 0.0002) =>
        Enumerable.Range(0, days).Select(index => new EligibleUniverseDay(Start.AddDays(index), value)).ToArray();

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

        var report = SignalStudyAnalyzer.Analyze([outcome], universe, SmallSample);

        var expected = 1.02 * 1.01 * 0.999 * 0.999 - 1 - (1.005 * 1.005 - 1);
        Assert.Equal(expected, report.MeanNetExcessPerTrade, 12);
    }

    [Fact]
    public void AStrongSignalThatMeetsEveryRuleAdvancesToHoldout()
    {
        var count = 2400;
        var report = SignalStudyAnalyzer.Analyze(Signals(count, 0.004, spacing: 1), Universe(count + 5), SmallSample);

        Assert.Equal(SignalStudyAnalyzer.AdvanceToHoldout, report.Decision);
        Assert.True(report.DailyExcessT >= 3);
    }

    [Fact]
    public void ANegativeUnviewedPeriodRejectsEvenWhenTheWholeSampleIsPositive()
    {
        var unviewedLosses = Signals(100, -0.002).Select(outcome => outcome with { Unviewed = true }).ToArray();
        var viewedGains = Signals(1500, 0.01, spacing: 1, unviewedFirstHalf: false)
            .Select(outcome => outcome with { Ticker = "V" + outcome.Ticker, IssuerId = "V" + outcome.IssuerId }).ToArray();

        var report = SignalStudyAnalyzer.Analyze([.. unviewedLosses, .. viewedGains], Universe(1600), SmallSample);

        Assert.True(report.MeanNetExcessPerTrade > 0);
        Assert.Equal(SignalStudyAnalyzer.Reject, report.Decision);
        Assert.Contains(report.Reasons, reason => reason.Contains("unviewed", StringComparison.Ordinal));
    }

    [Fact]
    public void ASignalNoBetterThanTheUniverseIsRejected()
    {
        var report = SignalStudyAnalyzer.Analyze(Signals(400, 0.0002), Universe(2100), SmallSample);

        Assert.Equal(SignalStudyAnalyzer.Reject, report.Decision);
    }

    [Fact]
    public void ToFewTradesCannotAdvanceWithoutFailingTheSignal()
    {
        var count = 2400;
        var rules = SmallSample with { MinimumTrades = 5000 };

        var report = SignalStudyAnalyzer.Analyze(Signals(count, 0.004, spacing: 1), Universe(count + 5), rules);

        Assert.Equal(SignalStudyAnalyzer.RetainResearch, report.Decision);
        Assert.Contains(report.Reasons, reason => reason.Contains("Trades", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingOutcomesAreCountedAgainstTheStudyInsteadOfDisappearing()
    {
        var count = 2400;
        var outcomes = Signals(count, 0.004, spacing: 1).Select((outcome, index) => outcome with { OutcomeKnown = index % 10 != 0 }).ToArray();

        var report = SignalStudyAnalyzer.Analyze(outcomes, Universe(count + 5), SmallSample);

        Assert.InRange(report.MissingOutcomeShare, 0.09, 0.11);
        Assert.Equal(SignalStudyAnalyzer.RetainResearch, report.Decision);
    }

    [Fact]
    public void AHoldingDayOutsideTheEligibleUniverseIsAnErrorNotASilentZero()
    {
        var outcome = new SignalOutcome("A", "A", Start, [new SignalHoldingDay(Start.AddDays(1), 0.01)], 0);

        Assert.Throws<InvalidDataException>(() => SignalStudyAnalyzer.Analyze([outcome], [], SmallSample));
    }

    [Fact]
    public void ResultsAreReportedByMonthAndRepeatExactly()
    {
        var outcomes = Signals(600, 0.002);

        var first = SignalStudyAnalyzer.Analyze(outcomes, Universe(3100), SmallSample);
        var second = SignalStudyAnalyzer.Analyze(outcomes, Universe(3100), SmallSample);

        Assert.Equal(first.BootstrapLowerBound, second.BootstrapLowerBound);
        Assert.Equal(600, first.Months.Sum(month => month.Trades));
        Assert.Equal(first.Months.OrderBy(month => (month.Year, month.Month)), first.Months);
    }
}
