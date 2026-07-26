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
        int? decisionCadenceBars = null)
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
            .Select(group => BuildRobustness(group, decisionCadenceBars))
            .OrderBy(value => value.Cell, StringComparer.Ordinal)
            .ThenBy(value => value.Segment, StringComparer.Ordinal)
            .ThenBy(value => value.ForwardHorizonBars)
            .ToArray();

        return new MomentumResearchAuditReport(tickers, formations, robustness);
    }

    private static MomentumRobustnessAudit BuildRobustness(
        IGrouping<AuditGroupKey, MomentumRankObservation> group,
        int? decisionCadenceBars)
    {
        var observations = group.ToArray();
        var formationReturns = FormationReturns(observations);
        var tickerTotals = observations
            .GroupBy(value => value.Ticker, StringComparer.OrdinalIgnoreCase)
            .Select(values => new
            {
                Ticker = values.Key,
                Total = values.Sum(value => value.ForwardReturnPct)
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
            .Where(value => !value.Ticker.Equals(
                topTicker,
                StringComparison.OrdinalIgnoreCase))
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
            withoutTopTicker.Length == 0
                ? (decimal?)null
                : Average(FormationReturns(withoutTopTicker)
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
                value.SlotReturnSource == MomentumSlotReturnSource.Cash)
        };
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
    decimal TopTickerTotalNetReturnPct,
    string BottomTicker,
    decimal BottomTickerTotalNetReturnPct,
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
}

public sealed record MomentumPortfolioPathAudit(
    int FormationDateCount,
    decimal FormationPeriodsPerYear,
    decimal CumulativeNetReturnPct,
    decimal? AnnualizedNetReturnPct,
    decimal MaximumDrawdownPct,
    decimal? AnnualizedSharpe);
