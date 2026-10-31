using TradingFlow.Research.Statistics;

namespace TradingFlow.Research.Signals;

/// <summary>One open-to-open return while a signal's trade is held, before costs.</summary>
public sealed record SignalHoldingDay(DateOnly Date, double GrossReturn);

/// <summary>A single signal followed from its next tradable open to its exit. Sourced from the shared backtest path, never recomputed here.</summary>
public sealed record SignalOutcome(
    string Ticker,
    string IssuerId,
    DateOnly SignalDate,
    IReadOnlyList<SignalHoldingDay> HoldingDays,
    double CostPerSide,
    bool OutcomeKnown = true,
    bool Unviewed = false);

/// <summary>The equal-weight open-to-open return of every stock eligible on that date.</summary>
public sealed record EligibleUniverseDay(DateOnly Date, double EqualWeightReturn);

/// <summary>Pass rules frozen before any result is read. Rates are fractions: 0.0015 means 0.15%.</summary>
public sealed record SignalStudyPassRules(
    int MinimumTrades = 2000,
    int MinimumSignalDates = 500,
    int MinimumIssuers = 300,
    double MinimumExcessPerTrade = 0.0015,
    double MinimumT = 3.0,
    double RejectBelowT = 2.0,
    double MinimumUnviewedT = 2.0,
    double MinimumPositiveYearShare = 0.70,
    double MaximumSingleYearProfitShare = 0.35,
    double MaximumMissingOutcomeShare = 0.01,
    int NeweyWestLag = 10,
    int BootstrapBlockDays = 20,
    int BootstrapReplicates = 5000,
    int BootstrapSeed = 20261031);

public sealed record SignalStudyMonth(int Year, int Month, int Trades, double MeanNetExcess);

public sealed record SignalStudyReport(
    string Decision,
    IReadOnlyList<string> Reasons,
    int Trades,
    int SignalDates,
    int Issuers,
    double MissingOutcomeShare,
    double MeanNetExcessPerTrade,
    double MeanNetExcessAtDoubleCost,
    double DailyExcessT,
    double BootstrapLowerBound,
    double UnviewedDailyExcessT,
    double UnviewedMeanNetExcess,
    double PositiveYearShare,
    double LargestYearProfitShare,
    IReadOnlyList<SignalStudyMonth> Months);

/// <summary>
/// Judges a signal against the eligible stocks it was chosen from, using date-based statistics
/// so that trades held at the same time are not counted as independent evidence.
/// </summary>
public static class SignalStudyAnalyzer
{
    public const string Reject = "REJECT";
    public const string RetainResearch = "RETAIN_RESEARCH";
    public const string AdvanceToHoldout = "ADVANCE_TO_HOLDOUT";

    public static SignalStudyReport Analyze(
        IReadOnlyList<SignalOutcome> outcomes,
        IReadOnlyList<EligibleUniverseDay> universe,
        SignalStudyPassRules rules)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentNullException.ThrowIfNull(universe);
        ArgumentNullException.ThrowIfNull(rules);
        var baseline = universe.ToDictionary(day => day.Date, day => day.EqualWeightReturn);
        var known = outcomes.Where(outcome => outcome.OutcomeKnown).ToArray();
        foreach (var outcome in known)
        {
            if (outcome.HoldingDays.Count == 0) throw new InvalidDataException($"{outcome.Ticker} {outcome.SignalDate} has no holding days.");
            if (outcome.HoldingDays.Any(day => !baseline.ContainsKey(day.Date)))
                throw new InvalidDataException($"{outcome.Ticker} {outcome.SignalDate} holds a date missing from the eligible universe.");
        }

        var excess = known.Select(outcome => NetExcess(outcome, baseline, 1)).ToArray();
        var doubled = known.Select(outcome => NetExcess(outcome, baseline, 2)).ToArray();
        var daily = DailyExcess(known, baseline);
        var inference = daily.Count > rules.NeweyWestLag + 1 ? ResearchInference.NeweyWestMean(daily, rules.NeweyWestLag) : null;
        var interval = daily.Count >= rules.BootstrapBlockDays
            ? ResearchInference.BlockBootstrapMean(daily, rules.BootstrapBlockDays, rules.BootstrapReplicates, 0.95, rules.BootstrapSeed)
            : null;
        var unviewed = known.Where(outcome => outcome.Unviewed).ToArray();
        var unviewedDaily = DailyExcess(unviewed, baseline);
        var unviewedInference = unviewedDaily.Count > rules.NeweyWestLag + 1 ? ResearchInference.NeweyWestMean(unviewedDaily, rules.NeweyWestLag) : null;
        var unviewedMean = unviewed.Length == 0 ? 0 : unviewed.Average(outcome => NetExcess(outcome, baseline, 1));

        var byYear = known.Select((outcome, index) => (outcome.HoldingDays[0].Date.Year, Excess: excess[index]))
            .GroupBy(item => item.Year).Select(group => group.Sum(item => item.Excess)).ToArray();
        var positiveYears = byYear.Where(value => value > 0).ToArray();
        var report = new SignalStudyReport(
            RetainResearch,
            [],
            known.Length,
            known.Select(outcome => outcome.SignalDate).Distinct().Count(),
            known.Select(outcome => outcome.IssuerId).Distinct(StringComparer.Ordinal).Count(),
            outcomes.Count == 0 ? 0 : 1d - (double)known.Length / outcomes.Count,
            excess.Length == 0 ? 0 : excess.Average(),
            doubled.Length == 0 ? 0 : doubled.Average(),
            inference?.TestStatistic ?? 0,
            interval?.LowerBound ?? 0,
            unviewedInference?.TestStatistic ?? 0,
            unviewedMean,
            byYear.Length == 0 ? 0 : (double)positiveYears.Length / byYear.Length,
            positiveYears.Length == 0 ? 0 : positiveYears.Max() / positiveYears.Sum(),
            known.Select((outcome, index) => (outcome.HoldingDays[0].Date, Excess: excess[index]))
                .GroupBy(item => (item.Date.Year, item.Date.Month)).OrderBy(group => group.Key)
                .Select(group => new SignalStudyMonth(group.Key.Year, group.Key.Month, group.Count(), group.Average(item => item.Excess)))
                .ToArray());
        var (decision, reasons) = Decide(report, unviewed.Length, rules);
        return report with { Decision = decision, Reasons = reasons };
    }

    // Rejection is final for this rule set: viewed periods cannot rescue a failed unviewed period.
    private static (string Decision, IReadOnlyList<string> Reasons) Decide(SignalStudyReport report, int unviewedTrades, SignalStudyPassRules rules)
    {
        var reject = new List<string>();
        if (report.MeanNetExcessPerTrade <= 0) reject.Add("Net excess per trade is not positive.");
        if (report.DailyExcessT < rules.RejectBelowT) reject.Add($"Daily excess t {report.DailyExcessT:F2} is below {rules.RejectBelowT:F2}.");
        if (unviewedTrades > 0 && report.UnviewedMeanNetExcess < 0) reject.Add("Net excess in the unviewed data is negative.");
        if (reject.Count > 0) return (Reject, reject);

        var unmet = new List<string>();
        if (report.Trades < rules.MinimumTrades) unmet.Add($"Trades {report.Trades} are below {rules.MinimumTrades}.");
        if (report.SignalDates < rules.MinimumSignalDates) unmet.Add($"Signal dates {report.SignalDates} are below {rules.MinimumSignalDates}.");
        if (report.Issuers < rules.MinimumIssuers) unmet.Add($"Issuers {report.Issuers} are below {rules.MinimumIssuers}.");
        if (report.MissingOutcomeShare > rules.MaximumMissingOutcomeShare) unmet.Add($"Missing outcomes {report.MissingOutcomeShare:P2} exceed {rules.MaximumMissingOutcomeShare:P2}.");
        if (report.MeanNetExcessPerTrade < rules.MinimumExcessPerTrade) unmet.Add($"Net excess per trade {report.MeanNetExcessPerTrade:P3} is below {rules.MinimumExcessPerTrade:P3}.");
        if (report.MeanNetExcessAtDoubleCost <= 0) unmet.Add("Net excess is not positive at double costs.");
        if (report.DailyExcessT < rules.MinimumT) unmet.Add($"Daily excess t {report.DailyExcessT:F2} is below {rules.MinimumT:F2}.");
        if (report.BootstrapLowerBound <= 0) unmet.Add("The bootstrap lower bound is not above zero.");
        if (unviewedTrades == 0) unmet.Add("No unviewed data was tested.");
        else if (report.UnviewedDailyExcessT < rules.MinimumUnviewedT) unmet.Add($"Unviewed t {report.UnviewedDailyExcessT:F2} is below {rules.MinimumUnviewedT:F2}.");
        if (report.PositiveYearShare < rules.MinimumPositiveYearShare) unmet.Add($"Positive years {report.PositiveYearShare:P0} are below {rules.MinimumPositiveYearShare:P0}.");
        if (report.LargestYearProfitShare > rules.MaximumSingleYearProfitShare) unmet.Add($"One year supplies {report.LargestYearProfitShare:P0} of profit, above {rules.MaximumSingleYearProfitShare:P0}.");
        return unmet.Count == 0 ? (AdvanceToHoldout, ["Every frozen pass rule was met."]) : (RetainResearch, unmet);
    }

    // Costs are charged once on entry and once on exit, then the same-days universe return is removed.
    private static double NetExcess(SignalOutcome outcome, IReadOnlyDictionary<DateOnly, double> baseline, int costMultiple)
    {
        var compounded = outcome.HoldingDays.Aggregate(1d, (value, day) => value * (1 + day.GrossReturn));
        var net = compounded * Math.Pow(1 - outcome.CostPerSide * costMultiple, 2) - 1;
        var universe = outcome.HoldingDays.Aggregate(1d, (value, day) => value * (1 + baseline[day.Date])) - 1;
        return net - universe;
    }

    // Equal-weight every trade open on a date, so overlapping holds become one observation per date.
    private static IReadOnlyList<double> DailyExcess(IReadOnlyList<SignalOutcome> outcomes, IReadOnlyDictionary<DateOnly, double> baseline)
    {
        var byDate = new SortedDictionary<DateOnly, List<double>>();
        foreach (var outcome in outcomes)
        {
            for (var index = 0; index < outcome.HoldingDays.Count; index++)
            {
                var day = outcome.HoldingDays[index];
                var sides = (index == 0 ? 1 : 0) + (index == outcome.HoldingDays.Count - 1 ? 1 : 0);
                if (!byDate.TryGetValue(day.Date, out var returns)) byDate[day.Date] = returns = [];
                returns.Add((1 + day.GrossReturn) * Math.Pow(1 - outcome.CostPerSide, sides) - 1);
            }
        }

        return byDate.Select(item => item.Value.Average() - baseline[item.Key]).ToArray();
    }
}
