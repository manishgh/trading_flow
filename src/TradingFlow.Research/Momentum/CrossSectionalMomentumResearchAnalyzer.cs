using TradingFlow.Domain.Market;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Strategies;
using System.Text.Json.Serialization;

namespace TradingFlow.Research.Momentum;

/// <summary>
/// Tests whether a completed-bar cross-sectional momentum rank predicts later returns.
/// This component is deliberately research-only: it does not size, route, or simulate orders.
/// </summary>
public sealed class CrossSectionalMomentumResearchAnalyzer
{
    public CrossSectionalMomentumReport Analyze(
        IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> dailyBarsByTicker,
        CrossSectionalMomentumStudyDefinition definition,
        IReadOnlyDictionary<DateOnly, IReadOnlySet<string>>? eligibleSymbolsByDate = null,
        IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>>? asTradedDailyBarsByTicker = null,
        MomentumExecutionCostAssumptions? executionCosts = null,
        MomentumAdditiveResearchEvidence? additiveEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(dailyBarsByTicker);
        ArgumentNullException.ThrowIfNull(definition);
        Validate(definition);
        if (executionCosts is not null &&
            (executionCosts.CommissionPerSideBps < 0m ||
             executionCosts.RegulatoryExitBps < 0m ||
             executionCosts.FullSpreadBps < 0m ||
             executionCosts.SlippagePerSideBps < 0m))
        {
            throw new ArgumentOutOfRangeException(
                nameof(executionCosts),
                "Momentum execution-cost assumptions cannot be negative.");
        }

        var options = definition.Options;
        var series = dailyBarsByTicker
            .Select(entry => BuildSeries(entry.Key, entry.Value, options.ExchangeTimezone))
            .Where(entry => entry.Bars.Length > 0)
            .ToDictionary(entry => entry.Ticker, StringComparer.OrdinalIgnoreCase);
        var asTradedSeries = (asTradedDailyBarsByTicker ?? dailyBarsByTicker)
            .Select(entry => BuildSeries(entry.Key, entry.Value, options.ExchangeTimezone))
            .Where(entry => entry.Bars.Length > 0)
            .ToDictionary(entry => entry.Ticker, StringComparer.OrdinalIgnoreCase);
        if (!series.TryGetValue(definition.BenchmarkTicker, out var benchmark))
        {
            throw new InvalidOperationException(
                $"Benchmark {definition.BenchmarkTicker} is missing from the supplied daily bars.");
        }

        var minimumHistoryIndex = Math.Max(
            options.MomentumLookbackBars,
            Math.Max(options.SlowTrendSmaBars - 1, options.AverageDollarVolumeBars - 1));
        var decisions = new List<DecisionSnapshot>();
        var skippedForCandidateCount = 0;

        foreach (var benchmarkIndex in DecisionIndices(
                     benchmark,
                     minimumHistoryIndex,
                     options))
        {
            var decisionDate = benchmark.SessionDates[benchmarkIndex];
            var benchmarkOutcomes = BuildForwardOutcomes(
                benchmark.Bars,
                benchmarkIndex,
                options.ForwardHorizons,
                benchmark.SessionDates);
            var marketTrendPassed = IsMarketUptrend(
                benchmark.Bars,
                benchmarkIndex,
                options.SlowTrendSmaBars);
            var unfilteredCandidates = BuildCandidates(
                series,
                definition.BenchmarkTicker,
                decisionDate,
                minimumHistoryIndex,
                options,
                eligibleSymbolsByDate,
                asTradedSeries);

            var ranked = unfilteredCandidates
                .OrderByDescending(candidate => candidate.MomentumReturnPct)
                .ThenBy(candidate => candidate.Ticker, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (ranked.Length < options.MinimumCandidatesPerDate)
            {
                skippedForCandidateCount++;
                continue;
            }

            var primaryCount = Math.Max(
                1,
                (int)Math.Ceiling(ranked.Length * options.PrimarySelectionFraction));
            var formationRank = ranked
                .Select((candidate, rank) => new FormationRankedMomentumCandidate(
                    candidate,
                    rank + 1,
                    Math.Min(
                        options.QuantileCount,
                        ((rank * options.QuantileCount) / ranked.Length) + 1),
                    rank < primaryCount,
                    rank >= ranked.Length - primaryCount))
                .ToArray();

            foreach (var cell in options.Cells)
            {
                var cellCandidates = formationRank
                    .Select(candidate => new RankedMomentumCandidate(
                        candidate.Candidate,
                        candidate.Rank,
                        candidate.Quantile,
                        candidate.IsPrimarySelection,
                        candidate.IsBottomSelection,
                        GatePassed(
                            candidate.Candidate,
                            cell,
                            marketTrendPassed,
                            decisionDate,
                            additiveEvidence)))
                    .ToArray();
                decisions.Add(new DecisionSnapshot(
                    cell,
                    decisionDate,
                    benchmark.Bars[benchmarkIndex].Timestamp,
                    cellCandidates,
                    benchmarkOutcomes));
            }
        }

        var partition = Partition(decisions, options, additiveEvidence);
        var observationBuild = BuildObservations(partition, executionCosts);
        var observations = observationBuild.Observations;
        var baselines = BuildBaselines(
            partition,
            options.ForwardHorizons,
            executionCosts);
        var cohorts = BuildCohorts(observations, options);
        var comparisons = BuildComparisons(observations, baselines, options);
        var blockers = BuildEvidenceBlockers(
            definition,
            partition,
            observations,
            additiveEvidence);

        var promotionBlockers = new List<string>(blockers);
        if (executionCosts is null)
        {
            promotionBlockers.Add(
                "momentum_forward_returns_are_gross_before_execution_costs");
        }

        promotionBlockers.Add(
            "research_report_requires_shared_brain_validation_before_promotion");

        var report = new CrossSectionalMomentumReport(
            definition.StudyName,
            definition.UniverseDescription,
            definition.BenchmarkTicker.ToUpperInvariant(),
            definition.PointInTimeUniverseEvidence,
            definition.AdjustedPricesConfirmed,
            blockers.Count == 0,
            false,
            promotionBlockers,
            series.Count,
            partition.Decisions.Select(item => item.DecisionDate).Distinct().Count(),
            partition.DevelopmentCount,
            partition.ValidationCount,
            partition.HoldoutCount,
            partition.ValidationStartDate,
            partition.HoldoutStartDate,
            observationBuild.PurgedOutcomeCount,
            skippedForCandidateCount,
            executionCosts,
            options,
            cohorts,
            comparisons,
            observations);
        return report with
        {
            FormationLedger = MomentumFormationLedgerEntry.FromObservations(observations)
        };
    }

    private static IReadOnlyList<MomentumCandidate> BuildCandidates(
        IReadOnlyDictionary<string, Series> series,
        string benchmarkTicker,
        DateOnly decisionDate,
        int minimumHistoryIndex,
        CrossSectionalMomentumResearchOptions options,
        IReadOnlyDictionary<DateOnly, IReadOnlySet<string>>? eligibleSymbolsByDate,
        IReadOnlyDictionary<string, Series> asTradedSeries)
    {
        IReadOnlySet<string>? eligibleSymbols = null;
        if (eligibleSymbolsByDate is not null &&
            !eligibleSymbolsByDate.TryGetValue(decisionDate, out eligibleSymbols))
        {
            return [];
        }

        var candidates = new List<MomentumCandidate>();
        foreach (var tickerSeries in series.Values)
        {
            if (tickerSeries.Ticker.Equals(benchmarkTicker, StringComparison.OrdinalIgnoreCase) ||
                (eligibleSymbols is not null && !eligibleSymbols.Contains(tickerSeries.Ticker)) ||
                !tickerSeries.IndexBySessionDate.TryGetValue(decisionDate, out var index) ||
                !asTradedSeries.TryGetValue(tickerSeries.Ticker, out var asTradedTickerSeries) ||
                !asTradedTickerSeries.IndexBySessionDate.TryGetValue(
                    decisionDate,
                    out var asTradedIndex) ||
                index < minimumHistoryIndex)
            {
                continue;
            }

            var averageDollarVolume = AverageDollarVolume(
                asTradedTickerSeries.Bars,
                asTradedIndex,
                options.AverageDollarVolumeBars);
            if (averageDollarVolume < options.MinimumAverageDollarVolume)
            {
                continue;
            }

            var momentum = MomentumReturn(
                tickerSeries.Bars,
                index,
                options.MomentumLookbackBars,
                options.SkipRecentBars);
            var outcomes = BuildForwardOutcomes(
                tickerSeries.Bars,
                index,
                options.ForwardHorizons,
                tickerSeries.SessionDates);
            if (momentum is null)
            {
                continue;
            }

            candidates.Add(new MomentumCandidate(
                tickerSeries.Ticker,
                momentum.Value,
                averageDollarVolume,
                IsStockUptrend(
                    tickerSeries.Bars,
                    index,
                    options.FastTrendSmaBars,
                    options.SlowTrendSmaBars),
                options.Cells.Any(MomentumResearchCell.RequiresVcp) &&
                IsFrozenV7VcpBreakout(tickerSeries.Bars, index),
                outcomes));
        }

        return candidates;
    }

    private static bool GatePassed(
        MomentumCandidate candidate,
        string cell,
        bool marketTrendPassed,
        DateOnly decisionDate,
        MomentumAdditiveResearchEvidence? evidence) =>
        cell switch
        {
            MomentumResearchCell.MomentumOnly => true,
            MomentumResearchCell.MomentumStockTrend =>
                candidate.StockTrendPassed,
            MomentumResearchCell.MomentumStockTrendVcpV7 =>
                candidate.StockTrendPassed && candidate.FrozenV7VcpPassed,
            MomentumResearchCell.MomentumStockTrendVcpV7ClassifiedCatalyst =>
                candidate.StockTrendPassed &&
                candidate.FrozenV7VcpPassed &&
                HasPositiveClassifiedCatalyst(evidence, decisionDate, candidate.Ticker),
            MomentumResearchCell.MomentumStockAndMarketTrend =>
                candidate.StockTrendPassed && marketTrendPassed,
            _ => throw new InvalidOperationException($"Unsupported momentum research cell: {cell}.")
        };

    private static bool HasPositiveClassifiedCatalyst(
        MomentumAdditiveResearchEvidence? evidence,
        DateOnly decisionDate,
        string ticker) =>
        evidence?.PositiveClassifiedCatalystSymbolsByFormationDate
            .TryGetValue(decisionDate, out var symbols) == true &&
        symbols.Contains(ticker);

    private static bool IsFrozenV7VcpBreakout(
        IReadOnlyList<OhlcvBar> bars,
        int completedBarIndex)
    {
        var analysis = new VcpSwingPivotAnalyzer().AnalyzeCompletedBar(
            bars,
            completedBarIndex,
            MomentumFrozenV7Vcp.Definition);
        return analysis.HasValidStructure &&
               analysis.IsBreakout &&
               analysis.BreakoutVolumeRatio is >= MomentumFrozenV7Vcp.MinimumBreakoutVolumeRatio;
    }

    private static IEnumerable<int> DecisionIndices(
        Series benchmark,
        int minimumHistoryIndex,
        CrossSectionalMomentumResearchOptions options)
    {
        for (var index = minimumHistoryIndex; index < benchmark.Bars.Length; index++)
        {
            var isDecision = options.FormationSchedule switch
            {
                MomentumFormationSchedule.MonthEnd =>
                    index + 1 < benchmark.SessionDates.Length &&
                    benchmark.SessionDates[index].Month != benchmark.SessionDates[index + 1].Month,
                MomentumFormationSchedule.EveryNBars =>
                    (index - minimumHistoryIndex) % options.DecisionCadenceBars == 0,
                _ => throw new InvalidOperationException(
                    $"Unsupported momentum formation schedule: {options.FormationSchedule}.")
            };
            if (isDecision)
            {
                yield return index;
            }
        }
    }

    private static PartitionedDecisions Partition(
        IReadOnlyList<DecisionSnapshot> decisions,
        CrossSectionalMomentumResearchOptions options,
        MomentumAdditiveResearchEvidence? additiveEvidence)
    {
        var dates = decisions
            .Select(item => item.DecisionDate)
            .Distinct()
            .Order()
            .ToArray();
        if (dates.Length == 0)
        {
            return new PartitionedDecisions(
                [],
                0,
                0,
                0,
                null,
                null,
                options,
                additiveEvidence);
        }

        DateOnly? validationStart;
        DateOnly? holdoutStart;
        if (options.ValidationStartDate is not null || options.HoldoutStartDate is not null)
        {
            if (options.ValidationStartDate is null ||
                options.HoldoutStartDate is null ||
                options.HoldoutStartDate <= options.ValidationStartDate)
            {
                throw new ArgumentException(
                    "Frozen momentum partitions require validation and holdout starts in chronological order.",
                    nameof(options));
            }

            validationStart = options.ValidationStartDate;
            holdoutStart = options.HoldoutStartDate;
        }
        else
        {
            var validationIndex = Math.Clamp(
                (int)Math.Floor(dates.Length * options.DevelopmentFraction),
                1,
                dates.Length);
            var holdoutIndex = Math.Clamp(
                (int)Math.Floor(
                    dates.Length * (options.DevelopmentFraction + options.ValidationFraction)),
                validationIndex,
                dates.Length);
            validationStart = validationIndex < dates.Length
                ? dates[validationIndex]
                : (DateOnly?)null;
            holdoutStart = holdoutIndex < dates.Length
                ? dates[holdoutIndex]
                : (DateOnly?)null;
        }
        var kept = new List<SegmentedDecision>();
        foreach (var decision in decisions)
        {
            var segment = decision.DecisionDate < (validationStart ?? DateOnly.MaxValue)
                ? MomentumStudySegment.Development
                : decision.DecisionDate < (holdoutStart ?? DateOnly.MaxValue)
                    ? MomentumStudySegment.Validation
                    : MomentumStudySegment.Holdout;
            kept.Add(new SegmentedDecision(decision, segment));
        }

        return new PartitionedDecisions(
            kept,
            kept.Where(item => item.Segment == MomentumStudySegment.Development)
                .Select(item => item.Decision.DecisionDate).Distinct().Count(),
            kept.Where(item => item.Segment == MomentumStudySegment.Validation)
                .Select(item => item.Decision.DecisionDate).Distinct().Count(),
            kept.Where(item => item.Segment == MomentumStudySegment.Holdout)
                .Select(item => item.Decision.DecisionDate).Distinct().Count(),
            validationStart,
            holdoutStart,
            options,
            additiveEvidence);
    }

    private static ObservationBuildResult BuildObservations(
        PartitionedDecisions partition,
        MomentumExecutionCostAssumptions? executionCosts)
    {
        var observations = new List<MomentumRankObservation>();
        var purgedOutcomes = 0;
        foreach (var item in partition.Decisions)
        {
            foreach (var ranked in item.Decision.Candidates)
            {
                foreach (var outcome in ranked.Candidate.ForwardOutcomes.Values)
                {
                    if (!OutcomeBelongsToSegment(
                            item.Segment,
                            outcome.TargetDate,
                            partition.ValidationStartDate,
                            partition.HoldoutStartDate))
                    {
                        purgedOutcomes++;
                        continue;
                    }

                    var usesCashBaseline =
                        ranked.IsPrimarySelection && !ranked.GatePassed;
                    var assetGrossForwardReturnPct = outcome.NextOpenReturnPct;
                    var grossForwardReturnPct = usesCashBaseline
                        ? CashReturnPct(
                            partition.Options.AnnualCashReturnPct,
                            outcome.HorizonBars)
                        : assetGrossForwardReturnPct;
                    var netForwardReturnPct =
                        executionCosts is null || usesCashBaseline
                            ? grossForwardReturnPct
                            : grossForwardReturnPct - executionCosts.RoundTripCostPct;
                    observations.Add(new MomentumRankObservation(
                        item.Decision.Cell,
                        item.Decision.DecisionDate,
                        item.Decision.DecisionTimestamp,
                        item.Segment,
                        ranked.Candidate.Ticker,
                        ranked.Rank,
                        ranked.Quantile,
                        ranked.IsPrimarySelection,
                        ranked.IsBottomSelection,
                        ranked.Candidate.MomentumReturnPct,
                        ranked.Candidate.AverageDollarVolume,
                        outcome.HorizonBars,
                        grossForwardReturnPct,
                        netForwardReturnPct,
                        outcome.FormationCloseReturnPct,
                        outcome.MaximumFavorableExcursionPct,
                        outcome.MaximumAdverseExcursionPct)
                    {
                        ParentCell = MomentumResearchCell.ParentOf(item.Decision.Cell),
                        GatePassed = ranked.GatePassed,
                        SlotReturnSource = usesCashBaseline
                            ? MomentumSlotReturnSource.Cash
                            : MomentumSlotReturnSource.Asset,
                        AssetGrossForwardReturnPct = assetGrossForwardReturnPct
                    });
                }
            }
        }

        return new ObservationBuildResult(observations, purgedOutcomes);
    }

    private static IReadOnlyList<MomentumBaselineObservation> BuildBaselines(
        PartitionedDecisions partition,
        IReadOnlyList<int> horizons,
        MomentumExecutionCostAssumptions? executionCosts)
    {
        var baselines = new List<MomentumBaselineObservation>();
        var previousUniverseByCell = new Dictionary<string, IReadOnlySet<string>>(
            StringComparer.Ordinal);
        foreach (var item in partition.Decisions
                     .OrderBy(value => value.DecisionDate)
                     .ThenBy(value => value.Cell, StringComparer.Ordinal))
        {
            var currentUniverse = item.Decision.Candidates
                .Select(candidate => candidate.Candidate.Ticker)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var turnoverPct = PortfolioTurnoverPct(
                previousUniverseByCell.GetValueOrDefault(item.Cell),
                currentUniverse);
            foreach (var horizon in horizons)
            {
                if (!item.Decision.BenchmarkForwardOutcomes.TryGetValue(horizon, out var benchmarkOutcome) ||
                    !OutcomeBelongsToSegment(
                        item.Segment,
                        benchmarkOutcome.TargetDate,
                        partition.ValidationStartDate,
                        partition.HoldoutStartDate))
                {
                    continue;
                }

                var eligibleOutcomes = item.Decision.Candidates
                    .Select(candidate => candidate.Candidate.ForwardOutcomes.GetValueOrDefault(horizon))
                    .Where(outcome =>
                        outcome is not null &&
                        OutcomeBelongsToSegment(
                            item.Segment,
                            outcome.TargetDate,
                            partition.ValidationStartDate,
                            partition.HoldoutStartDate))
                    .ToArray();
                if (eligibleOutcomes.Length == 0)
                {
                    continue;
                }

                var roundTripCostPct =
                    (executionCosts?.RoundTripCostPct ?? 0m) * turnoverPct / 100m;
                var sectorNeutral = SectorNeutralTopMinusBottom(
                    item,
                    horizon,
                    partition.AdditiveEvidence);
                baselines.Add(new MomentumBaselineObservation(
                    item.Decision.Cell,
                    item.Decision.DecisionDate,
                    item.Segment,
                    horizon,
                    eligibleOutcomes.Average(outcome => outcome!.NextOpenReturnPct) -
                    roundTripCostPct,
                    benchmarkOutcome.NextOpenReturnPct - roundTripCostPct)
                {
                    EligibleUniverseTurnoverPct = turnoverPct,
                    SectorNeutralTopMinusBottomReturnPct = sectorNeutral
                });
            }

            previousUniverseByCell[item.Cell] = currentUniverse;
        }

        return baselines;
    }

    private static decimal CashReturnPct(decimal annualCashReturnPct, int horizonBars) =>
        annualCashReturnPct == 0m
            ? 0m
            : 100m * ((decimal)Math.Pow(
                (double)(1m + (annualCashReturnPct / 100m)),
                (double)horizonBars / 252d) - 1m);

    private static decimal PortfolioTurnoverPct(
        IReadOnlySet<string>? previous,
        IReadOnlySet<string> current)
    {
        if (current.Count == 0)
        {
            return 0m;
        }

        if (previous is null || previous.Count == 0)
        {
            return 100m;
        }

        var symbols = previous.Concat(current)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var previousWeight = 1m / previous.Count;
        var currentWeight = 1m / current.Count;
        var oneWayTurnover = symbols.Sum(symbol =>
            Math.Abs(
                (previous.Contains(symbol) ? previousWeight : 0m) -
                (current.Contains(symbol) ? currentWeight : 0m))) / 2m;
        return Round(oneWayTurnover * 100m);
    }

    private static decimal? SectorNeutralTopMinusBottom(
        SegmentedDecision item,
        int horizon,
        MomentumAdditiveResearchEvidence? evidence)
    {
        if (evidence?.PointInTimeSectorCoverageConfirmed != true ||
            !evidence.SectorBySymbolByFormationDate.TryGetValue(
                item.DecisionDate,
                out var sectors))
        {
            return null;
        }

        var pairedSectorReturns = item.Decision.Candidates
            .Where(candidate =>
                candidate.Candidate.ForwardOutcomes.ContainsKey(horizon) &&
                sectors.ContainsKey(candidate.Candidate.Ticker))
            .GroupBy(candidate => sectors[candidate.Candidate.Ticker])
            .Select(group =>
            {
                var top = group.Where(candidate => candidate.IsPrimarySelection)
                    .Select(candidate =>
                        candidate.Candidate.ForwardOutcomes[horizon].NextOpenReturnPct)
                    .ToArray();
                var bottom = group.Where(candidate => candidate.IsBottomSelection)
                    .Select(candidate =>
                        candidate.Candidate.ForwardOutcomes[horizon].NextOpenReturnPct)
                    .ToArray();
                return top.Length == 0 || bottom.Length == 0
                    ? (decimal?)null
                    : top.Average() - bottom.Average();
            })
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToArray();
        return pairedSectorReturns.Length == 0
            ? null
            : Round(pairedSectorReturns.Average());
    }

    private static IReadOnlyList<MomentumCohortResult> BuildCohorts(
        IReadOnlyList<MomentumRankObservation> observations,
        CrossSectionalMomentumResearchOptions options)
    {
        var results = new List<MomentumCohortResult>();
        foreach (var cell in options.Cells)
        {
            foreach (var segment in MomentumStudySegment.All)
            {
                foreach (var horizon in options.ForwardHorizons)
                {
                    foreach (var quantile in Enumerable.Range(1, options.QuantileCount))
                    {
                        var matched = FilterSegment(observations, segment)
                            .Where(item =>
                                item.Cell == cell &&
                                item.ForwardHorizonBars == horizon &&
                                item.Quantile == quantile)
                            .ToArray();
                        results.Add(BuildCohort(cell, segment, horizon, quantile, matched));
                    }
                }
            }
        }

        return results;
    }

    private static IReadOnlyList<MomentumComparisonResult> BuildComparisons(
        IReadOnlyList<MomentumRankObservation> observations,
        IReadOnlyList<MomentumBaselineObservation> baselines,
        CrossSectionalMomentumResearchOptions options)
    {
        var results = new List<MomentumComparisonResult>();
        foreach (var cell in options.Cells)
        {
            foreach (var segment in MomentumStudySegment.All)
            {
                foreach (var horizon in options.ForwardHorizons)
                {
                    var segmentItems = FilterSegment(observations, segment)
                        .Where(item => item.Cell == cell && item.ForwardHorizonBars == horizon)
                        .ToArray();
                    var primary = segmentItems.Where(item => item.IsPrimarySelection).ToArray();
                    var bottom = segmentItems.Where(item => item.IsBottomSelection).ToArray();
                    var segmentBaselines = FilterSegment(baselines, segment)
                        .Where(item => item.Cell == cell && item.ForwardHorizonBars == horizon)
                        .ToArray();
                    var primaryMean = Mean(primary.Select(item => item.ForwardReturnPct));
                    var bottomMean = Mean(bottom.Select(item => item.ForwardReturnPct));
                    var universeMean = Mean(segmentBaselines.Select(item => item.EligibleUniverseReturnPct));
                    var benchmarkMean = Mean(segmentBaselines.Select(item => item.BenchmarkReturnPct));
                    var parentCell = MomentumResearchCell.ParentOf(cell);
                    var parentPrimary = parentCell is null
                        ? []
                        : FilterSegment(observations, segment)
                            .Where(item =>
                                item.Cell == parentCell &&
                                item.ForwardHorizonBars == horizon &&
                                item.IsPrimarySelection)
                            .ToArray();
                    var parentMean = parentCell is null
                        ? (decimal?)null
                        : Round(Mean(parentPrimary.Select(item => item.ForwardReturnPct)));
                    var comparison = new MomentumComparisonResult(
                        cell,
                        segment,
                        horizon,
                        segmentBaselines.Select(item => item.DecisionDate).Distinct().Count(),
                        primary.Length,
                        Round(primaryMean),
                        Round(Median(primary.Select(item => item.ForwardReturnPct).Order().ToArray())),
                        Round(bottomMean),
                        Round(universeMean),
                        Round(benchmarkMean),
                        Round(primaryMean - bottomMean),
                        Round(primaryMean - universeMean),
                        Round(primaryMean - benchmarkMean),
                        Round(LargestShare(primary, item => item.Ticker)),
                        Round(LargestShare(primary, item => item.DecisionDate.ToString("O"))))
                    {
                        ParentCell = parentCell,
                        ParentPrimaryMeanReturnPct = parentMean,
                        IncrementalReturnVsParentPct = parentMean is null
                            ? null
                            : Round(primaryMean - parentMean.Value),
                        EligibleUniverseMeanTurnoverPct = Round(Mean(
                            segmentBaselines.Select(item =>
                                item.EligibleUniverseTurnoverPct))),
                        SectorNeutralTopMinusBottomPct = MeanNullable(
                            segmentBaselines.Select(item =>
                                item.SectorNeutralTopMinusBottomReturnPct)),
                        BenchmarkBlockers = BuildBenchmarkBlockers(segmentBaselines)
                    };
                    results.Add(comparison);
                }
            }
        }

        return results;
    }

    private static decimal? MeanNullable(IEnumerable<decimal?> values)
    {
        var available = values.Where(value => value is not null)
            .Select(value => value!.Value)
            .ToArray();
        return available.Length == 0
            ? null
            : Round(available.Average());
    }

    private static IReadOnlyList<string> BuildBenchmarkBlockers(
        IReadOnlyList<MomentumBaselineObservation> observations)
    {
        var blockers = new List<string>();
        if (observations.Count == 0)
        {
            blockers.Add("benchmark_observations_unavailable");
        }

        if (observations.Any(value =>
                value.SectorNeutralTopMinusBottomReturnPct is null))
        {
            blockers.Add("point_in_time_sector_evidence_unavailable");
        }

        return blockers;
    }

    private static MomentumCohortResult BuildCohort(
        string cell,
        string segment,
        int horizon,
        int quantile,
        IReadOnlyList<MomentumRankObservation> observations)
    {
        var returns = observations.Select(item => item.ForwardReturnPct).Order().ToArray();
        return new MomentumCohortResult(
            cell,
            segment,
            horizon,
            quantile,
            observations.Count,
            observations.Select(item => item.DecisionDate).Distinct().Count(),
            observations.Select(item => item.Ticker).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            Round(Mean(returns)),
            Round(Median(returns)),
            returns.Length == 0
                ? 0m
                : Round((decimal)returns.Count(value => value > 0m) / returns.Length * 100m),
            Round(LargestShare(observations, item => item.Ticker)));
    }

    private static List<string> BuildEvidenceBlockers(
        CrossSectionalMomentumStudyDefinition definition,
        PartitionedDecisions partition,
        IReadOnlyList<MomentumRankObservation> observations,
        MomentumAdditiveResearchEvidence? additiveEvidence)
    {
        var blockers = new List<string>();
        if (!definition.PointInTimeUniverseEvidence)
        {
            blockers.Add("static_current_universe_without_historical_membership_evidence");
        }

        if (!definition.AdjustedPricesConfirmed)
        {
            blockers.Add("adjusted_price_and_corporate_action_evidence_missing");
        }

        if (!definition.AsTradedLiquidityPricesConfirmed)
        {
            blockers.Add("as_traded_price_and_dollar_volume_evidence_missing");
        }

        if (!definition.SecurityAndIssuerIdentityConfirmed)
        {
            blockers.Add("security_and_issuer_identity_evidence_missing");
        }

        if (!definition.CorporateActionsReconciled)
        {
            blockers.Add("corporate_action_reconciliation_incomplete");
        }

        if (!definition.TerminalOutcomesReconciled)
        {
            blockers.Add("terminal_outcome_reconciliation_incomplete");
        }

        if (!definition.ListedBarCoverageConfirmed)
        {
            blockers.Add("listed_bar_coverage_below_requirement");
        }

        if (!definition.BenchmarkCoverageConfirmed)
        {
            blockers.Add("benchmark_coverage_incomplete");
        }

        if (!definition.UniverseDecisionCutoffsConfirmed)
        {
            blockers.Add("universe_snapshot_decision_cutoff_not_confirmed");
        }

        if (definition.Options.Cells.Contains(
                MomentumResearchCell.MomentumStockTrendVcpV7ClassifiedCatalyst) &&
            additiveEvidence?.ClassifiedCatalystCoverageConfirmed != true)
        {
            blockers.Add("classified_catalyst_evidence_unavailable");
        }

        if (additiveEvidence?.PointInTimeSectorCoverageConfirmed != true)
        {
            blockers.Add("point_in_time_sector_evidence_unavailable");
        }

        if (definition.Options.ValidationStartDate is null ||
            definition.Options.HoldoutStartDate is null)
        {
            blockers.Add("study_partitions_not_frozen");
        }

        if (definition.Options.PrimarySelectionFraction != 0.30m)
        {
            blockers.Add(
                "primary_selection_fraction_not_frozen_top_30:" +
                $"{definition.Options.PrimarySelectionFraction:0.####}");
        }

        var totalDates = partition.Decisions.Select(item => item.Decision.DecisionDate).Distinct().Count();
        if (totalDates < definition.Options.MinimumFormationDates)
        {
            blockers.Add(
                $"insufficient_formation_dates:{totalDates}/{definition.Options.MinimumFormationDates}");
        }

        if (partition.HoldoutCount < definition.Options.MinimumHoldoutFormationDates)
        {
            blockers.Add(
                $"insufficient_holdout_dates:{partition.HoldoutCount}/{definition.Options.MinimumHoldoutFormationDates}");
        }

        var distinctHoldoutSelections = observations
            .Where(item =>
                item.Segment == MomentumStudySegment.Holdout &&
                item.IsPrimarySelection)
            .Select(item => item.Ticker)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (distinctHoldoutSelections < definition.Options.MinimumDistinctSelectedTickers)
        {
            blockers.Add(
                $"insufficient_distinct_holdout_tickers:{distinctHoldoutSelections}/{definition.Options.MinimumDistinctSelectedTickers}");
        }

        if (definition.Options.QuantileCount >= 10)
        {
            var smallestDecisionUniverse = observations
                .GroupBy(item => new { item.Cell, item.DecisionDate, item.ForwardHorizonBars })
                .Select(group => group.Select(item => item.Ticker)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count())
                .DefaultIfEmpty(0)
                .Min();
            if (smallestDecisionUniverse < 100)
            {
                blockers.Add($"insufficient_candidates_for_deciles:{smallestDecisionUniverse}/100");
            }
        }

        var primary = observations.Where(item => item.IsPrimarySelection).ToArray();
        var largestTickerShare = LargestShare(primary, item => item.Ticker);
        if (largestTickerShare > 10m)
        {
            blockers.Add(
                $"ticker_concentration_above_limit:{Round(largestTickerShare):0.####}/10");
        }

        var largestFormationShare = LargestShare(primary, item => item.DecisionDate);
        if (largestFormationShare > 10m)
        {
            blockers.Add(
                $"formation_concentration_above_limit:{Round(largestFormationShare):0.####}/10");
        }

        return blockers;
    }

    private static IEnumerable<T> FilterSegment<T>(
        IEnumerable<T> observations,
        string segment)
        where T : IMomentumSegmentObservation =>
        segment == MomentumStudySegment.Full
            ? observations
            : observations.Where(item => item.Segment == segment);

    private static Series BuildSeries(
        string ticker,
        IReadOnlyList<OhlcvBar> bars,
        string exchangeTimezone)
    {
        var ordered = bars
            .Where(bar => bar.Open > 0m && bar.High > 0m && bar.Low > 0m && bar.Close > 0m)
            .OrderBy(bar => bar.Timestamp)
            .ToArray();
        var sessionDates = ordered
            .Select(bar => ExecutionRunContextFactory.ResolveSessionDate(bar.Timestamp, exchangeTimezone))
            .ToArray();
        var indexByDate = sessionDates
            .Select((date, index) => new { date, index })
            .GroupBy(item => item.date)
            .ToDictionary(group => group.Key, group => group.Last().index);
        return new Series(ticker.ToUpperInvariant(), ordered, sessionDates, indexByDate);
    }

    private static bool IsMarketUptrend(
        IReadOnlyList<OhlcvBar> bars,
        int index,
        int slowPeriod)
    {
        var slow = Sma(bars, index, slowPeriod);
        return slow is not null && bars[index].Close > slow.Value;
    }

    private static bool IsStockUptrend(
        IReadOnlyList<OhlcvBar> bars,
        int index,
        int fastPeriod,
        int slowPeriod)
    {
        var fast = Sma(bars, index, fastPeriod);
        var slow = Sma(bars, index, slowPeriod);
        return fast is not null &&
               slow is not null &&
               bars[index].Close > slow.Value &&
               fast.Value > slow.Value;
    }

    private static decimal? MomentumReturn(
        IReadOnlyList<OhlcvBar> bars,
        int index,
        int lookback,
        int skipRecent)
    {
        var startIndex = index - lookback;
        var endIndex = index - skipRecent;
        if (startIndex < 0 || endIndex <= startIndex || bars[startIndex].Close <= 0m)
        {
            return null;
        }

        return PercentChange(bars[startIndex].Close, bars[endIndex].Close);
    }

    private static IReadOnlyDictionary<int, ForwardOutcome> BuildForwardOutcomes(
        IReadOnlyList<OhlcvBar> bars,
        int decisionIndex,
        IReadOnlyList<int> horizons,
        IReadOnlyList<DateOnly>? sessionDates = null)
    {
        if (decisionIndex + 1 >= bars.Count || bars[decisionIndex + 1].Open <= 0m)
        {
            return new Dictionary<int, ForwardOutcome>();
        }

        var entryPrice = bars[decisionIndex + 1].Open;
        var formationClose = bars[decisionIndex].Close;
        var outcomes = new Dictionary<int, ForwardOutcome>();
        foreach (var horizon in horizons)
        {
            var exitIndex = decisionIndex + horizon;
            if (exitIndex >= bars.Count)
            {
                continue;
            }

            var path = bars.Skip(decisionIndex + 1).Take(horizon).ToArray();
            outcomes[horizon] = new ForwardOutcome(
                horizon,
                sessionDates is not null
                    ? sessionDates[exitIndex]
                    : DateOnly.FromDateTime(bars[exitIndex].Timestamp.UtcDateTime),
                PercentChange(entryPrice, bars[exitIndex].Close),
                PercentChange(formationClose, bars[exitIndex].Close),
                PercentChange(entryPrice, path.Max(bar => bar.High)),
                PercentChange(entryPrice, path.Min(bar => bar.Low)));
        }

        return outcomes;
    }

    private static decimal AverageDollarVolume(
        IReadOnlyList<OhlcvBar> bars,
        int index,
        int period)
    {
        var start = index - period + 1;
        if (start < 0)
        {
            return 0m;
        }

        decimal sum = 0m;
        for (var i = start; i <= index; i++)
        {
            sum += bars[i].Close * bars[i].Volume;
        }

        return sum / period;
    }

    private static decimal? Sma(IReadOnlyList<OhlcvBar> bars, int index, int period)
    {
        var start = index - period + 1;
        if (start < 0 || index >= bars.Count)
        {
            return null;
        }

        decimal sum = 0m;
        for (var i = start; i <= index; i++)
        {
            sum += bars[i].Close;
        }

        return sum / period;
    }

    private static decimal LargestShare<T, TKey>(
        IReadOnlyCollection<T> observations,
        Func<T, TKey> keySelector)
        where TKey : notnull
    {
        if (observations.Count == 0)
        {
            return 0m;
        }

        var largest = observations.GroupBy(keySelector).Max(group => group.Count());
        return (decimal)largest / observations.Count * 100m;
    }

    private static decimal PercentChange(decimal start, decimal end) =>
        start == 0m ? 0m : ((end / start) - 1m) * 100m;

    private static decimal Mean(IEnumerable<decimal> values)
    {
        var materialized = values.ToArray();
        return materialized.Length == 0 ? 0m : materialized.Average();
    }

    private static decimal Median(IReadOnlyList<decimal> sorted)
    {
        if (sorted.Count == 0)
        {
            return 0m;
        }

        return sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2m;
    }

    private static decimal Round(decimal value) =>
        Math.Round(value, 4, MidpointRounding.AwayFromZero);

    private static void Validate(CrossSectionalMomentumStudyDefinition definition)
    {
        var options = definition.Options;
        if (String.IsNullOrWhiteSpace(definition.StudyName) ||
            String.IsNullOrWhiteSpace(definition.BenchmarkTicker) ||
            options.MomentumLookbackBars <= options.SkipRecentBars ||
            options.FastTrendSmaBars < 2 ||
            options.SlowTrendSmaBars <= options.FastTrendSmaBars ||
            options.AverageDollarVolumeBars < 1 ||
            options.DecisionCadenceBars < 1 ||
            options.QuantileCount < 2 ||
            options.PrimarySelectionFraction <= 0m ||
            options.PrimarySelectionFraction >= 0.5m ||
            options.AnnualCashReturnPct < 0m ||
            options.MinimumCandidatesPerDate < options.QuantileCount ||
            options.DevelopmentFraction <= 0m ||
            options.ValidationFraction <= 0m ||
            options.HoldoutFraction <= 0m ||
            options.DevelopmentFraction + options.ValidationFraction + options.HoldoutFraction != 1m ||
            options.ForwardHorizons.Count == 0 ||
            options.ForwardHorizons.Any(horizon => horizon < 1) ||
            options.Cells.Count == 0 ||
            options.Cells.Any(cell => !MomentumResearchCell.All.Contains(cell)))
        {
            throw new InvalidOperationException("Momentum research options are invalid.");
        }

        foreach (var cell in options.Cells)
        {
            var parent = MomentumResearchCell.ParentOf(cell);
            if (parent is not null && !options.Cells.Contains(parent))
            {
                throw new InvalidOperationException(
                    $"Momentum research cell {cell} requires parent cell {parent}.");
            }
        }
    }

    private sealed record Series(
        string Ticker,
        OhlcvBar[] Bars,
        DateOnly[] SessionDates,
        IReadOnlyDictionary<DateOnly, int> IndexBySessionDate);

    private sealed record MomentumCandidate(
        string Ticker,
        decimal MomentumReturnPct,
        decimal AverageDollarVolume,
        bool StockTrendPassed,
        bool FrozenV7VcpPassed,
        IReadOnlyDictionary<int, ForwardOutcome> ForwardOutcomes);

    private sealed record FormationRankedMomentumCandidate(
        MomentumCandidate Candidate,
        int Rank,
        int Quantile,
        bool IsPrimarySelection,
        bool IsBottomSelection);

    private sealed record RankedMomentumCandidate(
        MomentumCandidate Candidate,
        int Rank,
        int Quantile,
        bool IsPrimarySelection,
        bool IsBottomSelection,
        bool GatePassed);

    private sealed record DecisionSnapshot(
        string Cell,
        DateOnly DecisionDate,
        DateTimeOffset DecisionTimestamp,
        IReadOnlyList<RankedMomentumCandidate> Candidates,
        IReadOnlyDictionary<int, ForwardOutcome> BenchmarkForwardOutcomes);

    private sealed record SegmentedDecision(
        DecisionSnapshot Decision,
        string Segment)
    {
        public string Cell => Decision.Cell;
        public DateOnly DecisionDate => Decision.DecisionDate;
        public IReadOnlyList<RankedMomentumCandidate> Candidates => Decision.Candidates;
        public IReadOnlyDictionary<int, ForwardOutcome> BenchmarkForwardOutcomes =>
            Decision.BenchmarkForwardOutcomes;
    }

    private sealed record PartitionedDecisions(
        IReadOnlyList<SegmentedDecision> Decisions,
        int DevelopmentCount,
        int ValidationCount,
        int HoldoutCount,
        DateOnly? ValidationStartDate,
        DateOnly? HoldoutStartDate,
        CrossSectionalMomentumResearchOptions Options,
        MomentumAdditiveResearchEvidence? AdditiveEvidence);

    private sealed record ObservationBuildResult(
        IReadOnlyList<MomentumRankObservation> Observations,
        int PurgedOutcomeCount);

    private static bool OutcomeBelongsToSegment(
        string segment,
        DateOnly targetDate,
        DateOnly? validationStart,
        DateOnly? holdoutStart) =>
        segment switch
        {
            MomentumStudySegment.Development =>
                validationStart is null || targetDate < validationStart.Value,
            MomentumStudySegment.Validation =>
                holdoutStart is null || targetDate < holdoutStart.Value,
            MomentumStudySegment.Holdout => true,
            _ => false
        };
}

public sealed record CrossSectionalMomentumStudyDefinition(
    string StudyName,
    string BenchmarkTicker,
    string UniverseDescription,
    [property: JsonIgnore] bool PointInTimeUniverseEvidence,
    [property: JsonIgnore] bool AdjustedPricesConfirmed,
    CrossSectionalMomentumResearchOptions Options)
{
    [JsonIgnore]
    public bool AsTradedLiquidityPricesConfirmed { get; init; }

    [JsonIgnore]
    public bool SecurityAndIssuerIdentityConfirmed { get; init; }

    [JsonIgnore]
    public bool CorporateActionsReconciled { get; init; }

    [JsonIgnore]
    public bool TerminalOutcomesReconciled { get; init; }

    [JsonIgnore]
    public bool ListedBarCoverageConfirmed { get; init; }

    [JsonIgnore]
    public bool BenchmarkCoverageConfirmed { get; init; }

    [JsonIgnore]
    public bool UniverseDecisionCutoffsConfirmed { get; init; }
}

public sealed record CrossSectionalMomentumResearchOptions(
    int MomentumLookbackBars,
    int SkipRecentBars,
    int FastTrendSmaBars,
    int SlowTrendSmaBars,
    int AverageDollarVolumeBars,
    decimal MinimumAverageDollarVolume,
    string FormationSchedule,
    int DecisionCadenceBars,
    IReadOnlyList<int> ForwardHorizons,
    int QuantileCount,
    decimal PrimarySelectionFraction,
    int MinimumCandidatesPerDate,
    decimal DevelopmentFraction,
    decimal ValidationFraction,
    decimal HoldoutFraction,
    int MinimumFormationDates,
    int MinimumHoldoutFormationDates,
    int MinimumDistinctSelectedTickers,
    IReadOnlyList<string> Cells,
    string ExchangeTimezone)
{
    /// <summary>
    /// Frozen partition boundaries from the preregistered study. Catalog research requires
    /// both values; ratio-derived boundaries remain available only for diagnostics.
    /// </summary>
    public DateOnly? ValidationStartDate { get; init; }

    public DateOnly? HoldoutStartDate { get; init; }

    /// <summary>
    /// Frozen annual cash return used by additive cells when a selected slot fails
    /// its gate. The slot remains in the portfolio and is never backfilled.
    /// </summary>
    public decimal AnnualCashReturnPct { get; init; }
}

public sealed record CrossSectionalMomentumReport(
    string StudyName,
    string UniverseDescription,
    string BenchmarkTicker,
    bool PointInTimeUniverseEvidence,
    bool AdjustedPricesConfirmed,
    bool DataEvidenceReady,
    bool PromotionEligible,
    IReadOnlyList<string> PromotionBlockers,
    int LoadedTickerCount,
    int DecisionDateCount,
    int DevelopmentDecisionDateCount,
    int ValidationDecisionDateCount,
    int HoldoutDecisionDateCount,
    DateOnly? ValidationStartDate,
    DateOnly? HoldoutStartDate,
    int PurgedOutcomeCount,
    int SkippedForCandidateCount,
    MomentumExecutionCostAssumptions? ExecutionCosts,
    CrossSectionalMomentumResearchOptions Options,
    IReadOnlyList<MomentumCohortResult> Cohorts,
    IReadOnlyList<MomentumComparisonResult> Comparisons,
    IReadOnlyList<MomentumRankObservation> RankObservations)
{
    public IReadOnlyList<MomentumFormationLedgerEntry> FormationLedger { get; init; } = [];
}

public sealed record MomentumCohortResult(
    string Cell,
    string Segment,
    int ForwardHorizonBars,
    int Quantile,
    int ObservationCount,
    int DecisionDateCount,
    int UniqueTickerCount,
    decimal MeanReturnPct,
    decimal MedianReturnPct,
    decimal WinRatePct,
    decimal LargestTickerObservationSharePct);

public sealed record MomentumComparisonResult(
    string Cell,
    string Segment,
    int ForwardHorizonBars,
    int DecisionDateCount,
    int PrimaryObservationCount,
    decimal PrimaryMeanReturnPct,
    decimal PrimaryMedianReturnPct,
    decimal BottomMeanReturnPct,
    decimal EligibleUniverseMeanReturnPct,
    decimal BenchmarkMeanReturnPct,
    decimal PrimaryMinusBottomPct,
    decimal PrimaryMinusUniversePct,
    decimal PrimaryMinusBenchmarkPct,
    decimal LargestTickerSharePct,
    decimal LargestFormationDateSharePct)
{
    public string? ParentCell { get; init; }

    public decimal? ParentPrimaryMeanReturnPct { get; init; }

    public decimal? IncrementalReturnVsParentPct { get; init; }

    public decimal EligibleUniverseMeanTurnoverPct { get; init; }

    public decimal? SectorNeutralTopMinusBottomPct { get; init; }

    public IReadOnlyList<string> BenchmarkBlockers { get; init; } = [];
}

public interface IMomentumSegmentObservation
{
    string Segment { get; }
}

public sealed record MomentumRankObservation(
    string Cell,
    DateOnly DecisionDate,
    DateTimeOffset DecisionTimestamp,
    string Segment,
    string Ticker,
    int Rank,
    int Quantile,
    bool IsPrimarySelection,
    bool IsBottomSelection,
    decimal MomentumReturnPct,
    decimal AverageDollarVolume,
    int ForwardHorizonBars,
    decimal GrossForwardReturnPct,
    decimal ForwardReturnPct,
    decimal FormationCloseReturnPct,
    decimal MaximumFavorableExcursionPct,
    decimal MaximumAdverseExcursionPct) : IMomentumSegmentObservation
{
    public string? ParentCell { get; init; }

    public bool GatePassed { get; init; } = true;

    public string SlotReturnSource { get; init; } = MomentumSlotReturnSource.Asset;

    public decimal AssetGrossForwardReturnPct { get; init; } = GrossForwardReturnPct;
}

public sealed record MomentumExecutionCostAssumptions(
    decimal CommissionPerSideBps,
    decimal RegulatoryExitBps,
    decimal FullSpreadBps,
    decimal SlippagePerSideBps)
{
    public decimal RoundTripCostBps =>
        (2m * CommissionPerSideBps) +
        RegulatoryExitBps +
        FullSpreadBps +
        (2m * SlippagePerSideBps);

    public decimal RoundTripCostPct => RoundTripCostBps / 100m;
}

public sealed record ForwardOutcome(
    int HorizonBars,
    DateOnly TargetDate,
    decimal NextOpenReturnPct,
    decimal FormationCloseReturnPct,
    decimal MaximumFavorableExcursionPct,
    decimal MaximumAdverseExcursionPct);

internal sealed record MomentumBaselineObservation(
    string Cell,
    DateOnly DecisionDate,
    string Segment,
    int ForwardHorizonBars,
    decimal EligibleUniverseReturnPct,
    decimal BenchmarkReturnPct) : IMomentumSegmentObservation
{
    public decimal EligibleUniverseTurnoverPct { get; init; }

    public decimal? SectorNeutralTopMinusBottomReturnPct { get; init; }
}

public sealed record MomentumAdditiveResearchEvidence(
    IReadOnlyDictionary<DateOnly, IReadOnlySet<string>>
        PositiveClassifiedCatalystSymbolsByFormationDate,
    IReadOnlyDictionary<DateOnly, IReadOnlyDictionary<string, string>>
        SectorBySymbolByFormationDate)
{
    public bool ClassifiedCatalystCoverageConfirmed { get; init; }

    public bool PointInTimeSectorCoverageConfirmed { get; init; }
}

public sealed record MomentumFormationLedgerEntry(
    string Cell,
    string? ParentCell,
    DateOnly DecisionDate,
    DateTimeOffset DecisionTimestamp,
    string Segment,
    string Ticker,
    int Rank,
    int Quantile,
    bool IsPrimarySelection,
    bool IsBottomSelection,
    bool GatePassed,
    string SlotReturnSource,
    int ForwardHorizonBars,
    decimal MomentumReturnPct,
    decimal AssetGrossForwardReturnPct,
    decimal SlotGrossForwardReturnPct,
    decimal SlotNetForwardReturnPct)
{
    internal static IReadOnlyList<MomentumFormationLedgerEntry> FromObservations(
        IEnumerable<MomentumRankObservation> observations) =>
        observations
            .OrderBy(value => value.DecisionDate)
            .ThenBy(value => value.Cell, StringComparer.Ordinal)
            .ThenBy(value => value.ForwardHorizonBars)
            .ThenBy(value => value.Rank)
            .ThenBy(value => value.Ticker, StringComparer.Ordinal)
            .Select(value => new MomentumFormationLedgerEntry(
                value.Cell,
                value.ParentCell,
                value.DecisionDate,
                value.DecisionTimestamp,
                value.Segment,
                value.Ticker,
                value.Rank,
                value.Quantile,
                value.IsPrimarySelection,
                value.IsBottomSelection,
                value.GatePassed,
                value.SlotReturnSource,
                value.ForwardHorizonBars,
                value.MomentumReturnPct,
                value.AssetGrossForwardReturnPct,
                value.GrossForwardReturnPct,
                value.ForwardReturnPct))
            .ToArray();
}

public static class MomentumStudySegment
{
    public const string Full = "full";
    public const string Development = "development";
    public const string Validation = "validation";
    public const string Holdout = "holdout";

    public static IReadOnlyList<string> All { get; } =
        [Full, Development, Validation, Holdout];
}

public static class MomentumFormationSchedule
{
    public const string MonthEnd = "month_end";
    public const string EveryNBars = "every_n_bars";
}

public static class MomentumResearchCell
{
    public const string MomentumOnly = "momentum_only";
    public const string MomentumStockTrend = "momentum_stock_trend";
    public const string MomentumStockAndMarketTrend = "momentum_stock_market_trend";
    public const string MomentumStockTrendVcpV7 = "momentum_stock_trend_vcp_v7";
    public const string MomentumStockTrendVcpV7ClassifiedCatalyst =
        "momentum_stock_trend_vcp_v7_classified_catalyst";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(
        [
            MomentumOnly,
            MomentumStockTrend,
            MomentumStockTrendVcpV7,
            MomentumStockTrendVcpV7ClassifiedCatalyst,
            MomentumStockAndMarketTrend
        ],
        StringComparer.Ordinal);

    public static string? ParentOf(string cell) =>
        cell switch
        {
            MomentumOnly => null,
            MomentumStockTrend => MomentumOnly,
            MomentumStockTrendVcpV7 => MomentumStockTrend,
            MomentumStockTrendVcpV7ClassifiedCatalyst =>
                MomentumStockTrendVcpV7,
            MomentumStockAndMarketTrend => MomentumStockTrend,
            _ => throw new InvalidOperationException(
                $"Unsupported momentum research cell: {cell}.")
        };

    public static bool RequiresVcp(string cell) =>
        cell is MomentumStockTrendVcpV7 or
            MomentumStockTrendVcpV7ClassifiedCatalyst;
}

public static class MomentumSlotReturnSource
{
    public const string Asset = "asset";
    public const string Cash = "cash";
}

public static class MomentumFrozenV7Vcp
{
    public const decimal MinimumBreakoutVolumeRatio = 1m;

    public static VcpSwingPivotOptions Definition { get; } = new(
        LookbackBars: 60,
        PivotStrengthBars: 2,
        MinimumContractions: 2,
        MaximumContractions: 4,
        MaximumDepthRatioToPrevious: 0.90m,
        MinimumLowRisePct: 0m,
        MaximumContractionToAdvanceVolumeRatio: 0.85m,
        RequireProgressiveContractionVolume: false,
        MaximumVolumeRatioToPreviousContraction: 1m,
        MinimumVolumeReferenceBars: 3,
        BreakoutBufferPct: 0m);
}
