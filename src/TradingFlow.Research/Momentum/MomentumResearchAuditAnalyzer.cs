namespace TradingFlow.Research.Momentum;

/// <summary>
/// Explains where a momentum study's selected-cohort return came from without
/// changing its signals. Full-period summaries are derived from the frozen
/// development, validation, and holdout observations.
/// </summary>
public sealed class MomentumResearchAuditAnalyzer
{
    public MomentumResearchAuditReport Analyze(
        IReadOnlyList<MomentumRankObservation> observations,
        int? decisionCadenceBars = null,
        MomentumExecutionCostAssumptions? executionCosts = null,
        MomentumAdditiveResearchEvidence? additiveEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(observations);
        if (decisionCadenceBars is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(decisionCadenceBars),
                "Decision cadence must be positive when supplied.");
        }

        var selected = observations
            .Where(observation => observation.IsPrimarySelection)
            .ToArray();
        if (selected.Length == 0)
        {
            return new MomentumResearchAuditReport([], [], []);
        }

        var segmented = selected
            .SelectMany(observation => new[]
            {
                observation,
                observation with { Segment = MomentumStudySegment.Full }
            })
            .ToArray();

        var tickers = segmented
            .GroupBy(
                observation => new
                {
                    observation.Cell,
                    observation.Segment,
                    observation.ForwardHorizonBars,
                    observation.Ticker
                })
            .Select(group => new MomentumTickerAudit(
                group.Key.Cell,
                group.Key.Segment,
                group.Key.ForwardHorizonBars,
                group.Key.Ticker,
                group.Count(),
                group.Select(value => value.DecisionDate).Distinct().Count(),
                Average(group.Select(value => value.ForwardReturnPct)),
                Median(group.Select(value => value.ForwardReturnPct)),
                Percentage(group.Count(value => value.ForwardReturnPct > 0m), group.Count()),
                group.Sum(value => value.ForwardReturnPct),
                Average(group.Select(value => value.MaximumFavorableExcursionPct)),
                Average(group.Select(value => value.MaximumAdverseExcursionPct))))
            .OrderBy(value => value.Cell, StringComparer.Ordinal)
            .ThenBy(value => value.Segment, StringComparer.Ordinal)
            .ThenBy(value => value.ForwardHorizonBars)
            .ThenByDescending(value => value.TotalNetReturnPct)
            .ThenBy(value => value.Ticker, StringComparer.Ordinal)
            .ToArray();

        var formations = segmented
            .GroupBy(
                observation => new
                {
                    observation.Cell,
                    observation.Segment,
                    observation.ForwardHorizonBars,
                    observation.DecisionDate
                })
            .Select(group => new MomentumFormationAudit(
                group.Key.Cell,
                group.Key.Segment,
                group.Key.ForwardHorizonBars,
                group.Key.DecisionDate,
                group.Count(),
                Average(group.Select(value => value.ForwardReturnPct)),
                Median(group.Select(value => value.ForwardReturnPct)),
                Percentage(group.Count(value => value.ForwardReturnPct > 0m), group.Count()),
                group.Min(value => value.ForwardReturnPct),
                group.Max(value => value.ForwardReturnPct))
            {
                ConfiguredSlotCount = group.Count(),
                InvestedSlotCount = group.Count(value =>
                    value.SlotReturnSource == MomentumSlotReturnSource.Asset),
                CashSlotCount = group.Count(value =>
                    value.SlotReturnSource == MomentumSlotReturnSource.Cash),
                GateFailureCount = group.Count(value => !value.GatePassed)
            })
            .OrderBy(value => value.Cell, StringComparer.Ordinal)
            .ThenBy(value => value.Segment, StringComparer.Ordinal)
            .ThenBy(value => value.ForwardHorizonBars)
            .ThenBy(value => value.DecisionDate)
            .ToArray();

        var robustness = segmented
            .GroupBy(
                observation => new AuditGroupKey(
                    observation.Cell,
                    observation.Segment,
                    observation.ForwardHorizonBars))
            .Select(group => BuildRobustness(
                group,
                decisionCadenceBars,
                executionCosts,
                additiveEvidence))
            .OrderBy(value => value.Cell, StringComparer.Ordinal)
            .ThenBy(value => value.Segment, StringComparer.Ordinal)
            .ThenBy(value => value.ForwardHorizonBars)
            .ToArray();

        return new MomentumResearchAuditReport(tickers, formations, robustness);
    }

    private static MomentumRobustnessAudit BuildRobustness(
        IGrouping<AuditGroupKey, MomentumRankObservation> group,
        int? decisionCadenceBars,
        MomentumExecutionCostAssumptions? executionCosts,
        MomentumAdditiveResearchEvidence? additiveEvidence)
    {
        var observations = group.ToArray();
        var formationReturns = FormationReturns(observations);
        var concentration = AbsolutePnlConcentration(observations);
        var tickerTotals = observations
            .GroupBy(value => value.Ticker, StringComparer.OrdinalIgnoreCase)
            .Select(values => new
            {
                Ticker = values.Key,
                Total = EqualWeightedPnlContribution(values, observations)
            })
            .OrderByDescending(value => value.Total)
            .ThenBy(value => value.Ticker, StringComparer.Ordinal)
            .ToArray();
        var topTicker = tickerTotals[0].Ticker;
        var bottomTicker = tickerTotals[^1].Ticker;
        var topFormation = formationReturns
            .OrderByDescending(value => value.ReturnPct)
            .ThenBy(value => value.Date)
            .First();
        var bottomFormation = formationReturns
            .OrderBy(value => value.ReturnPct)
            .ThenBy(value => value.Date)
            .First();

        var withoutTopTicker = observations
            .Select(value => value.Ticker.Equals(
                    topTicker,
                    StringComparison.OrdinalIgnoreCase)
                ? value with { ForwardReturnPct = 0m }
                : value)
            .ToArray();
        var withoutTopFormation = formationReturns
            .Where(value => value.Date != topFormation.Date)
            .ToArray();
        var overlapMultiple = decisionCadenceBars is null
            ? (int?)null
            : (int)Math.Ceiling(
                (decimal)group.Key.ForwardHorizonBars /
                decisionCadenceBars.Value);
        var outcomesOverlap = overlapMultiple is null
            ? (bool?)null
            : overlapMultiple.Value > 1;
        var independentFormationCount = overlapMultiple is null
            ? (int?)null
            : (int)Math.Ceiling(
                (decimal)formationReturns.Length /
                overlapMultiple.Value);
        var portfolioPath = outcomesOverlap == false
            ? BuildPortfolioPath(formationReturns, decisionCadenceBars!.Value)
            : null;
        var leaveOneTicker = LeaveOneTicker(observations);
        var leaveOneYear = LeaveOneYear(observations);
        var monthAudit = LeaveBestMonth(observations);
        var robustnessBlockers = new List<string>();
        var leaveOneSector = LeaveOneSector(
            observations,
            additiveEvidence,
            robustnessBlockers);
        var costStress = BuildCostStress(
            observations,
            executionCosts,
            robustnessBlockers);

        return new MomentumRobustnessAudit(
            group.Key.Cell,
            group.Key.Segment,
            group.Key.ForwardHorizonBars,
            formationReturns.Length,
            observations.Select(value => value.Ticker).Distinct(
                StringComparer.OrdinalIgnoreCase).Count(),
            Average(formationReturns.Select(value => value.ReturnPct)),
            Percentage(
                formationReturns.Count(value => value.ReturnPct > 0m),
                formationReturns.Length),
            topTicker,
            tickerTotals[0].Total,
            bottomTicker,
            tickerTotals[^1].Total,
            topFormation.Date,
            topFormation.ReturnPct,
            bottomFormation.Date,
            bottomFormation.ReturnPct,
            Average(FormationReturns(withoutTopTicker)
                .Select(value => value.ReturnPct)),
            withoutTopFormation.Length == 0
                ? (decimal?)null
                : Average(withoutTopFormation.Select(value => value.ReturnPct)),
            decisionCadenceBars,
            outcomesOverlap,
            overlapMultiple,
            independentFormationCount,
            portfolioPath)
        {
            ParentCell = MomentumResearchCell.ParentOf(group.Key.Cell),
            GatePassRatePct = Percentage(
                observations.Count(value => value.GatePassed),
                observations.Length),
            CashSlotObservationCount = observations.Count(value =>
                value.SlotReturnSource == MomentumSlotReturnSource.Cash),
            LargestTickerAbsolutePnlContributionPct =
                concentration.LargestTickerContributionPct,
            LargestMonthAbsolutePnlContributionPct =
                concentration.LargestMonthContributionPct,
            LargestFormationAbsolutePnlContributionPct =
                concentration.LargestFormationContributionPct,
            BestMonth = monthAudit.BestMonth,
            MeanNetReturnWithoutBestMonthPct =
                monthAudit.MeanWithoutBestMonthPct,
            LeaveOneTicker = leaveOneTicker,
            LeaveOneSector = leaveOneSector,
            LeaveOneYear = leaveOneYear,
            CostStress = costStress,
            RobustnessBlockers = robustnessBlockers
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray()
        };
    }

    // Leave-one-out audits are counterfactual diagnostics, not parameter searches.
    // Ticker/sector exclusions turn affected slots into zero-return cash so the
    // frozen formation slot count and all other selections remain unchanged.
    private static IReadOnlyList<MomentumLeaveOneOutAudit> LeaveOneTicker(
        IReadOnlyList<MomentumRankObservation> observations) =>
        observations
            .Select(value => value.Ticker)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Select(ticker =>
            {
                var counterfactual = observations
                    .Select(value => value.Ticker.Equals(
                            ticker,
                            StringComparison.OrdinalIgnoreCase)
                        ? value with { ForwardReturnPct = 0m }
                        : value)
                    .ToArray();
                var returns = FormationReturns(counterfactual);
                return new MomentumLeaveOneOutAudit(
                    MomentumRobustnessDimension.Ticker,
                    ticker,
                    returns.Length,
                    Average(returns.Select(value => value.ReturnPct)));
            })
            .ToArray();

    private static IReadOnlyList<MomentumLeaveOneOutAudit> LeaveOneSector(
        IReadOnlyList<MomentumRankObservation> observations,
        MomentumAdditiveResearchEvidence? evidence,
        ICollection<string> blockers)
    {
        if (evidence?.PointInTimeSectorCoverageConfirmed != true)
        {
            blockers.Add("point_in_time_sector_evidence_unavailable");
            return [];
        }

        var sectorByObservation = new Dictionary<MomentumRankObservation, string>();
        foreach (var observation in observations)
        {
            if (!evidence.SectorBySymbolByFormationDate.TryGetValue(
                    observation.DecisionDate,
                    out var sectors) ||
                !sectors.TryGetValue(observation.Ticker, out var sector) ||
                String.IsNullOrWhiteSpace(sector))
            {
                blockers.Add(
                    $"point_in_time_sector_evidence_missing:{observation.DecisionDate:O}:{observation.Ticker}");
                return [];
            }

            sectorByObservation[observation] = sector.Trim();
        }

        return sectorByObservation.Values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Select(sector =>
            {
                var counterfactual = observations
                    .Select(value => sectorByObservation[value].Equals(
                            sector,
                            StringComparison.OrdinalIgnoreCase)
                        ? value with { ForwardReturnPct = 0m }
                        : value)
                    .ToArray();
                var returns = FormationReturns(counterfactual);
                return new MomentumLeaveOneOutAudit(
                    MomentumRobustnessDimension.Sector,
                    sector,
                    returns.Length,
                    Average(returns.Select(value => value.ReturnPct)));
            })
            .ToArray();
    }

    private static IReadOnlyList<MomentumLeaveOneOutAudit> LeaveOneYear(
        IReadOnlyList<MomentumRankObservation> observations) =>
        observations
            .Select(value => value.DecisionDate.Year)
            .Distinct()
            .Order()
            .Select(year =>
            {
                var retained = observations
                    .Where(value => value.DecisionDate.Year != year)
                    .ToArray();
                var returns = FormationReturns(retained);
                return new MomentumLeaveOneOutAudit(
                    MomentumRobustnessDimension.Year,
                    year.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    returns.Length,
                    Average(returns.Select(value => value.ReturnPct)));
            })
            .ToArray();

    private static BestMonthAudit LeaveBestMonth(
        IReadOnlyList<MomentumRankObservation> observations)
    {
        var formationReturns = FormationReturns(observations);
        var monthly = formationReturns
            .GroupBy(value => $"{value.Date.Year:D4}-{value.Date.Month:D2}")
            .Select(group => new
            {
                Month = group.Key,
                Mean = Average(group.Select(value => value.ReturnPct))
            })
            .OrderByDescending(value => value.Mean)
            .ThenBy(value => value.Month, StringComparer.Ordinal)
            .ToArray();
        if (monthly.Length == 0)
        {
            return new BestMonthAudit(null, null);
        }

        var bestMonth = monthly[0].Month;
        var retained = formationReturns
            .Where(value =>
                $"{value.Date.Year:D4}-{value.Date.Month:D2}" != bestMonth)
            .ToArray();
        return new BestMonthAudit(
            bestMonth,
            retained.Length == 0
                ? null
                : Average(retained.Select(value => value.ReturnPct)));
    }

    // The stress family is frozen at exactly 1x, 2x, and 3x the preregistered
    // round-trip cost. Cash slots are not charged an artificial execution cost.
    private static IReadOnlyList<MomentumCostStressAudit> BuildCostStress(
        IReadOnlyList<MomentumRankObservation> observations,
        MomentumExecutionCostAssumptions? executionCosts,
        ICollection<string> blockers)
    {
        if (executionCosts is null)
        {
            blockers.Add("execution_cost_assumptions_unavailable");
            return [];
        }

        return new[] { 1m, 2m, 3m }
            .Select(multiplier =>
            {
                var stressed = observations
                    .Select(value => value with
                    {
                        ForwardReturnPct =
                            value.SlotReturnSource == MomentumSlotReturnSource.Cash
                                ? value.ForwardReturnPct
                                : value.GrossForwardReturnPct -
                                  (executionCosts.RoundTripCostPct * multiplier)
                    })
                    .ToArray();
                var returns = FormationReturns(stressed);
                return new MomentumCostStressAudit(
                    multiplier,
                    returns.Length,
                    Average(returns.Select(value => value.ReturnPct)));
            })
            .ToArray();
    }

    private static AbsolutePnlConcentrationResult AbsolutePnlConcentration(
        IReadOnlyList<MomentumRankObservation> observations)
    {
        if (observations.Count == 0)
        {
            return new AbsolutePnlConcentrationResult(0m, 0m, 0m);
        }

        var formationCount = observations
            .Select(value => value.DecisionDate)
            .Distinct()
            .Count();
        var weighted = observations
            .GroupBy(value => value.DecisionDate)
            .SelectMany(formation =>
            {
                var slots = formation.Count();
                return formation.Select(value => new WeightedPnlContribution(
                    value.Ticker,
                    value.DecisionDate,
                    value.ForwardReturnPct / slots / formationCount));
            })
            .ToArray();
        return new AbsolutePnlConcentrationResult(
            LargestAbsolutePnlContribution(weighted, value => value.Ticker),
            LargestAbsolutePnlContribution(
                weighted,
                value => $"{value.DecisionDate.Year:D4}-{value.DecisionDate.Month:D2}"),
            LargestAbsolutePnlContribution(weighted, value => value.DecisionDate));
    }

    private static decimal EqualWeightedPnlContribution(
        IEnumerable<MomentumRankObservation> selected,
        IReadOnlyList<MomentumRankObservation> all)
    {
        var formationCount = all.Select(value => value.DecisionDate).Distinct().Count();
        var slotsByDate = all
            .GroupBy(value => value.DecisionDate)
            .ToDictionary(group => group.Key, group => group.Count());
        return decimal.Round(
            selected.Sum(value =>
                value.ForwardReturnPct /
                slotsByDate[value.DecisionDate] /
                formationCount),
            6);
    }

    private static decimal LargestAbsolutePnlContribution<TKey>(
        IReadOnlyList<WeightedPnlContribution> contributions,
        Func<WeightedPnlContribution, TKey> keySelector)
        where TKey : notnull
    {
        var groupedAbsolutePnl = contributions
            .GroupBy(keySelector)
            .Select(group => group.Sum(value =>
                Math.Abs(value.ContributionPct)))
            .ToArray();
        var denominator = groupedAbsolutePnl.Sum();
        return denominator == 0m
            ? 0m
            : decimal.Round(
                groupedAbsolutePnl.Max() / denominator * 100m,
                4);
    }

    private static MomentumPortfolioPathAudit BuildPortfolioPath(
        IReadOnlyList<FormationReturn> formationReturns,
        int decisionCadenceBars)
    {
        var equity = 1m;
        var peak = 1m;
        var maximumDrawdownPct = 0m;
        foreach (var formation in formationReturns.OrderBy(value => value.Date))
        {
            equity *= 1m + (formation.ReturnPct / 100m);
            peak = Math.Max(peak, equity);
            if (peak > 0m)
            {
                maximumDrawdownPct = Math.Min(
                    maximumDrawdownPct,
                    100m * ((equity / peak) - 1m));
            }
        }

        var periodsPerYear = 252m / decisionCadenceBars;
        var years = formationReturns.Count / periodsPerYear;
        var annualizedReturnPct = years <= 0m || equity <= 0m
            ? (decimal?)null
            : 100m * ((decimal)Math.Pow(
                (double)equity,
                (double)(1m / years)) - 1m);
        var returns = formationReturns
            .Select(value => value.ReturnPct)
            .ToArray();
        var standardDeviation = StandardDeviation(returns);
        var annualizedSharpe = standardDeviation == 0m
            ? (decimal?)null
            : (Average(returns) / standardDeviation) *
              (decimal)Math.Sqrt((double)periodsPerYear);

        return new MomentumPortfolioPathAudit(
            formationReturns.Count,
            decimal.Round(periodsPerYear, 4),
            decimal.Round(100m * (equity - 1m), 6),
            annualizedReturnPct is null
                ? null
                : decimal.Round(annualizedReturnPct.Value, 6),
            decimal.Round(maximumDrawdownPct, 6),
            annualizedSharpe is null
                ? null
                : decimal.Round(annualizedSharpe.Value, 6));
    }

    private static FormationReturn[] FormationReturns(
        IReadOnlyCollection<MomentumRankObservation> observations) =>
        observations
            .GroupBy(value => value.DecisionDate)
            .Select(values => new FormationReturn(
                values.Key,
                Average(values.Select(value => value.ForwardReturnPct))))
            .OrderBy(value => value.Date)
            .ToArray();

    private static decimal Average(IEnumerable<decimal> values)
    {
        var materialized = values.ToArray();
        return materialized.Length == 0
            ? 0m
            : decimal.Round(materialized.Average(), 6);
    }

    private static decimal Median(IEnumerable<decimal> values)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0)
        {
            return 0m;
        }

        var midpoint = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? decimal.Round((ordered[midpoint - 1] + ordered[midpoint]) / 2m, 6)
            : ordered[midpoint];
    }

    private static decimal Percentage(int numerator, int denominator) =>
        denominator == 0
            ? 0m
            : decimal.Round(100m * numerator / denominator, 4);

    private static decimal StandardDeviation(IReadOnlyList<decimal> values)
    {
        if (values.Count < 2)
        {
            return 0m;
        }

        var average = values.Average();
        var variance = values.Sum(value =>
            (value - average) * (value - average)) / (values.Count - 1);
        return (decimal)Math.Sqrt((double)variance);
    }

    private sealed record FormationReturn(DateOnly Date, decimal ReturnPct);

    private sealed record WeightedPnlContribution(
        string Ticker,
        DateOnly DecisionDate,
        decimal ContributionPct);

    private sealed record AbsolutePnlConcentrationResult(
        decimal LargestTickerContributionPct,
        decimal LargestMonthContributionPct,
        decimal LargestFormationContributionPct);

    private sealed record BestMonthAudit(
        string? BestMonth,
        decimal? MeanWithoutBestMonthPct);

    private sealed record AuditGroupKey(
        string Cell,
        string Segment,
        int ForwardHorizonBars);
}

public sealed record MomentumResearchAuditReport(
    IReadOnlyList<MomentumTickerAudit> Tickers,
    IReadOnlyList<MomentumFormationAudit> Formations,
    IReadOnlyList<MomentumRobustnessAudit> Robustness);

public sealed record MomentumTickerAudit(
    string Cell,
    string Segment,
    int ForwardHorizonBars,
    string Ticker,
    int ObservationCount,
    int FormationDateCount,
    decimal MeanNetReturnPct,
    decimal MedianNetReturnPct,
    decimal WinRatePct,
    decimal TotalNetReturnPct,
    decimal MeanMaximumFavorableExcursionPct,
    decimal MeanMaximumAdverseExcursionPct);

public sealed record MomentumFormationAudit(
    string Cell,
    string Segment,
    int ForwardHorizonBars,
    DateOnly DecisionDate,
    int SelectedTickerCount,
    decimal MeanNetReturnPct,
    decimal MedianNetReturnPct,
    decimal WinRatePct,
    decimal WorstTickerReturnPct,
    decimal BestTickerReturnPct)
{
    public int ConfiguredSlotCount { get; init; }

    public int InvestedSlotCount { get; init; }

    public int CashSlotCount { get; init; }

    public int GateFailureCount { get; init; }
}

public sealed record MomentumRobustnessAudit(
    string Cell,
    string Segment,
    int ForwardHorizonBars,
    int FormationDateCount,
    int UniqueTickerCount,
    decimal EqualWeightedFormationMeanNetReturnPct,
    decimal PositiveFormationRatePct,
    string TopTicker,
    decimal TopTickerEqualWeightedPnlContributionPct,
    string BottomTicker,
    decimal BottomTickerEqualWeightedPnlContributionPct,
    DateOnly TopFormationDate,
    decimal TopFormationMeanNetReturnPct,
    DateOnly BottomFormationDate,
    decimal BottomFormationMeanNetReturnPct,
    decimal? MeanNetReturnWithoutTopTickerPct,
    decimal? MeanNetReturnWithoutTopFormationPct,
    int? DecisionCadenceBars,
    bool? OutcomesOverlap,
    int? ConcurrentCohortCount,
    int? ApproximateIndependentFormationCount,
    MomentumPortfolioPathAudit? PortfolioPath)
{
    public string? ParentCell { get; init; }

    public decimal GatePassRatePct { get; init; }

    public int CashSlotObservationCount { get; init; }

    public decimal LargestTickerAbsolutePnlContributionPct { get; init; }

    public decimal LargestMonthAbsolutePnlContributionPct { get; init; }

    public decimal LargestFormationAbsolutePnlContributionPct { get; init; }

    public string? BestMonth { get; init; }

    public decimal? MeanNetReturnWithoutBestMonthPct { get; init; }

    public IReadOnlyList<MomentumLeaveOneOutAudit> LeaveOneTicker { get; init; } = [];

    public IReadOnlyList<MomentumLeaveOneOutAudit> LeaveOneSector { get; init; } = [];

    public IReadOnlyList<MomentumLeaveOneOutAudit> LeaveOneYear { get; init; } = [];

    public IReadOnlyList<MomentumCostStressAudit> CostStress { get; init; } = [];

    public IReadOnlyList<string> RobustnessBlockers { get; init; } = [];
}

public sealed record MomentumLeaveOneOutAudit(
    string Dimension,
    string ExcludedValue,
    int FormationDateCount,
    decimal EqualWeightedFormationMeanNetReturnPct);

public sealed record MomentumCostStressAudit(
    decimal CostMultiplier,
    int FormationDateCount,
    decimal EqualWeightedFormationMeanNetReturnPct);

public static class MomentumRobustnessDimension
{
    public const string Ticker = "ticker";
    public const string Sector = "sector";
    public const string Year = "year";
}

public sealed record MomentumPortfolioPathAudit(
    int FormationDateCount,
    decimal FormationPeriodsPerYear,
    decimal CumulativeNetReturnPct,
    decimal? AnnualizedNetReturnPct,
    decimal MaximumDrawdownPct,
    decimal? AnnualizedSharpe);
