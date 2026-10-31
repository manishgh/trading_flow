using TradingFlow.Research.Statistics;

namespace TradingFlow.Research.Signals;

/// <summary>One open-to-open return while a signal's trade is held, before costs. Date is the day of the starting open.</summary>
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
    int MinimumTradesPerCountedYear = 30,
    double MaximumSingleYearProfitShare = 0.35,
    double MaximumMissingOutcomeShare = 0.01,
    double MissingOutcomeWorstCaseReturn = -0.30,
    int NeweyWestLag = 10,
    int BootstrapBlockDays = 20,
    int BootstrapReplicates = 5000,
    int BootstrapSeed = 20261031);

/// <summary>Per-month evidence for display. Figures are mean excess per trade, not account returns.</summary>
public sealed record SignalStudyMonth(int Year, int Month, int Trades, int UnviewedTrades, double MeanNetExcessPerTrade);

/// <summary>Whole-sample figures exist to apply the frozen gates and for audit; present results through Months.</summary>
public sealed record SignalStudyReport(
    string StrategyIdentity,
    string Decision,
    IReadOnlyList<string> Reasons,
    int Trades,
    int SignalDates,
    int Issuers,
    double MissingOutcomeShare,
    double MeanNetExcessPerTrade,
    double WorstCaseMeanNetExcessPerTrade,
    double MeanNetExcessAtDoubleCost,
    double DailyExcessT,
    double BootstrapLowerBound,
    double ActiveDayShare,
    double MarketSensitivity,
    double DailyInterceptAfterMarketSensitivity,
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
        string strategyIdentity,
        IReadOnlyList<SignalOutcome> outcomes,
        IReadOnlyList<EligibleUniverseDay> universe,
        SignalStudyPassRules rules)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyIdentity);
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentNullException.ThrowIfNull(universe);
        ArgumentNullException.ThrowIfNull(rules);
        var baseline = new SortedDictionary<DateOnly, double>(universe.ToDictionary(day => day.Date, day => day.EqualWeightReturn));
        var known = outcomes.Where(outcome => outcome.OutcomeKnown).ToArray();
        foreach (var outcome in known) Validate(outcome, baseline);

        var excess = known.Select(outcome => NetExcess(outcome, baseline, 1)).ToArray();
        var doubled = known.Select(outcome => NetExcess(outcome, baseline, 2)).ToArray();
        var missing = outcomes.Count - known.Length;
        var daily = DailySeries(known, baseline);
        var inference = TStatistic(daily.Excess, rules);
        var interval = daily.Excess.Count >= rules.BootstrapBlockDays
            ? ResearchInference.BlockBootstrapMean(daily.Excess, rules.BootstrapBlockDays, rules.BootstrapReplicates, 0.95, rules.BootstrapSeed)
            : null;
        var unviewed = known.Where(outcome => outcome.Unviewed).ToArray();
        var unviewedMean = unviewed.Length == 0 ? 0 : unviewed.Average(outcome => NetExcess(outcome, baseline, 1));
        var (sensitivity, intercept) = MarketRegression(daily.ActiveSignalReturns, daily.ActiveUniverseReturns);

        var years = known.Select((outcome, index) => (outcome.HoldingDays[0].Date.Year, Excess: excess[index]))
            .GroupBy(item => item.Year)
            .Where(group => group.Count() >= rules.MinimumTradesPerCountedYear)
            .Select(group => group.Sum(item => item.Excess)).ToArray();
        var positiveYears = years.Where(value => value > 0).ToArray();
        var report = new SignalStudyReport(
            strategyIdentity,
            RetainResearch,
            [],
            known.Length,
            known.Select(outcome => outcome.SignalDate).Distinct().Count(),
            known.Select(outcome => outcome.IssuerId).Distinct(StringComparer.Ordinal).Count(),
            outcomes.Count == 0 ? 0 : (double)missing / outcomes.Count,
            excess.Length == 0 ? 0 : excess.Average(),
            outcomes.Count == 0 ? 0 : (excess.Sum() + missing * rules.MissingOutcomeWorstCaseReturn) / outcomes.Count,
            doubled.Length == 0 ? 0 : doubled.Average(),
            inference,
            interval?.LowerBound ?? 0,
            daily.Excess.Count == 0 ? 0 : (double)daily.ActiveSignalReturns.Count / daily.Excess.Count,
            sensitivity,
            intercept,
            TStatistic(DailySeries(unviewed, baseline).Excess, rules),
            unviewedMean,
            years.Length == 0 ? 0 : (double)positiveYears.Length / years.Length,
            positiveYears.Length == 0 ? 0 : positiveYears.Max() / positiveYears.Sum(),
            known.Select((outcome, index) => (outcome.HoldingDays[0].Date, outcome.Unviewed, Excess: excess[index]))
                .GroupBy(item => (item.Date.Year, item.Date.Month)).OrderBy(group => group.Key)
                .Select(group => new SignalStudyMonth(group.Key.Year, group.Key.Month, group.Count(),
                    group.Count(item => item.Unviewed), group.Average(item => item.Excess)))
                .ToArray());
        var (decision, reasons) = Decide(report, unviewed.Length, rules);
        return report with { Decision = decision, Reasons = reasons };
    }

    // An underpowered study cannot reject or advance. With enough evidence, rejection is final for this rule set.
    private static (string Decision, IReadOnlyList<string> Reasons) Decide(SignalStudyReport report, int unviewedTrades, SignalStudyPassRules rules)
    {
        var sample = new List<string>();
        if (report.Trades < rules.MinimumTrades) sample.Add($"Trades {report.Trades} are below {rules.MinimumTrades}.");
        if (report.SignalDates < rules.MinimumSignalDates) sample.Add($"Signal dates {report.SignalDates} are below {rules.MinimumSignalDates}.");
        if (report.Issuers < rules.MinimumIssuers) sample.Add($"Issuers {report.Issuers} are below {rules.MinimumIssuers}.");
        if (sample.Count > 0) return (RetainResearch, sample);

        var reject = new List<string>();
        if (report.MeanNetExcessPerTrade <= 0) reject.Add("Net excess per trade is not positive.");
        if (report.DailyExcessT < rules.RejectBelowT) reject.Add($"Daily excess t is below {rules.RejectBelowT:F2}.");
        if (unviewedTrades > 0 && report.UnviewedMeanNetExcess < 0) reject.Add("Net excess in the unviewed data is negative.");
        if (reject.Count > 0) return (Reject, reject);

        var unmet = new List<string>();
        if (report.MissingOutcomeShare > rules.MaximumMissingOutcomeShare) unmet.Add("Too many trades have no known outcome.");
        if (report.MeanNetExcessPerTrade < rules.MinimumExcessPerTrade) unmet.Add("Net excess per trade is below the hurdle.");
        if (report.WorstCaseMeanNetExcessPerTrade < rules.MinimumExcessPerTrade) unmet.Add("Net excess falls below the hurdle if missing outcomes took the frozen worst-case loss.");
        if (report.MeanNetExcessAtDoubleCost <= 0) unmet.Add("Net excess is not positive at double costs.");
        if (report.DailyExcessT < rules.MinimumT) unmet.Add($"Daily excess t is below {rules.MinimumT:F2}.");
        if (report.BootstrapLowerBound <= 0) unmet.Add("The bootstrap lower bound is not above zero.");
        if (unviewedTrades == 0) unmet.Add("No unviewed data was tested.");
        else if (report.UnviewedDailyExcessT < rules.MinimumUnviewedT) unmet.Add($"Unviewed t is below {rules.MinimumUnviewedT:F2}.");
        if (report.PositiveYearShare < rules.MinimumPositiveYearShare) unmet.Add("Too few years were positive.");
        if (report.LargestYearProfitShare > rules.MaximumSingleYearProfitShare) unmet.Add("One year supplies too much of the profit.");
        return unmet.Count == 0 ? (AdvanceToHoldout, ["Every frozen pass rule was met."]) : (RetainResearch, unmet);
    }

    private static void Validate(SignalOutcome outcome, IReadOnlyDictionary<DateOnly, double> baseline)
    {
        if (outcome.HoldingDays.Count == 0) throw new InvalidDataException($"{outcome.Ticker} {outcome.SignalDate} has no holding days.");
        if (outcome.HoldingDays[0].Date <= outcome.SignalDate)
            throw new InvalidDataException($"{outcome.Ticker} {outcome.SignalDate} is held on or before its signal date.");
        for (var index = 0; index < outcome.HoldingDays.Count; index++)
        {
            if (!baseline.ContainsKey(outcome.HoldingDays[index].Date))
                throw new InvalidDataException($"{outcome.Ticker} {outcome.SignalDate} holds a date missing from the eligible universe.");
            if (index > 0 && outcome.HoldingDays[index].Date <= outcome.HoldingDays[index - 1].Date)
                throw new InvalidDataException($"{outcome.Ticker} {outcome.SignalDate} has holding days out of order.");
        }
    }

    // A flat series has no measurable noise, so it earns no statistical credit rather than an infinite t.
    private static double TStatistic(IReadOnlyList<double> series, SignalStudyPassRules rules)
    {
        if (series.Count <= rules.NeweyWestLag + 1) return 0;
        var value = ResearchInference.NeweyWestMean(series, rules.NeweyWestLag).TestStatistic;
        return double.IsFinite(value) ? value : 0;
    }

    // Costs are charged once on entry and once on exit, then the same-days universe return is removed.
    private static double NetExcess(SignalOutcome outcome, IReadOnlyDictionary<DateOnly, double> baseline, int costMultiple)
    {
        var compounded = outcome.HoldingDays.Aggregate(1d, (value, day) => value * (1 + day.GrossReturn));
        var net = compounded * Math.Pow(1 - outcome.CostPerSide * costMultiple, 2) - 1;
        var universe = outcome.HoldingDays.Aggregate(1d, (value, day) => value * (1 + baseline[day.Date])) - 1;
        return net - universe;
    }

    // One observation per calendar slot: open trades are equal-weighted and days with none count as zero, so time is not compressed.
    private static (IReadOnlyList<double> Excess, IReadOnlyList<double> ActiveSignalReturns, IReadOnlyList<double> ActiveUniverseReturns) DailySeries(
        IReadOnlyList<SignalOutcome> outcomes, SortedDictionary<DateOnly, double> baseline)
    {
        var byDate = new Dictionary<DateOnly, List<double>>();
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

        var excess = new List<double>();
        var signal = new List<double>();
        var market = new List<double>();
        if (byDate.Count == 0) return (excess, signal, market);
        var first = byDate.Keys.Min();
        var last = byDate.Keys.Max();
        foreach (var (date, universeReturn) in baseline)
        {
            if (date < first || date > last) continue;
            if (!byDate.TryGetValue(date, out var returns))
            {
                excess.Add(0);
                continue;
            }

            excess.Add(returns.Average() - universeReturn);
            signal.Add(returns.Average());
            market.Add(universeReturn);
        }

        return (excess, signal, market);
    }

    // Ordinary least squares of signal returns on universe returns, so market exposure is not mistaken for skill.
    private static (double Sensitivity, double Intercept) MarketRegression(IReadOnlyList<double> signal, IReadOnlyList<double> market)
    {
        if (signal.Count < 2) return (0, 0);
        var signalMean = signal.Average();
        var marketMean = market.Average();
        var variance = market.Sum(value => (value - marketMean) * (value - marketMean));
        if (variance <= 0) return (0, signalMean - marketMean);
        var covariance = signal.Select((value, index) => (value - signalMean) * (market[index] - marketMean)).Sum();
        var sensitivity = covariance / variance;
        return (sensitivity, signalMean - sensitivity * marketMean);
    }
}
