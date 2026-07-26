using TradingFlow.Domain.Market;
using TradingFlow.Research.Momentum;
using Xunit;

namespace TradingFlow.Tests;

public sealed class CrossSectionalMomentumResearchAnalyzerTests
{
    [Fact]
    public void Analyze_RanksPreDecisionHistory_AndMeasuresFromNextOpen()
    {
        var bars = new Dictionary<string, IReadOnlyList<OhlcvBar>>(StringComparer.OrdinalIgnoreCase)
        {
            ["SPY"] = BuildBars("SPY", 24, index => 100m + index),
            ["FAST"] = BuildBars("FAST", 24, index => 20m + (index * 2m)),
            ["MEDIUM"] = BuildBars("MEDIUM", 24, index => 30m + index),
            ["SLOW"] = BuildBars("SLOW", 24, index => 40m + (index * 0.5m)),
            ["FLAT"] = BuildBars("FLAT", 24, _ => 50m),
        };
        var definition = Definition() with
        {
            Options = Definition().Options with
            {
                Cells = [MomentumResearchCell.MomentumOnly]
            }
        };

        var report = new CrossSectionalMomentumResearchAnalyzer().Analyze(bars, definition);

        var fullOneBar = Assert.Single(
            report.Comparisons,
            result => result.Segment == "full" && result.ForwardHorizonBars == 1);
        var firstDecision = report.RankObservations.Min(item => item.DecisionDate);
        var firstRank = Assert.Single(
            report.RankObservations,
            item =>
                item.DecisionDate == firstDecision &&
                item.ForwardHorizonBars == 1 &&
                item.Rank == 1);
        Assert.Equal("FAST", firstRank.Ticker);
        Assert.True(firstRank.MaximumFavorableExcursionPct >= firstRank.ForwardReturnPct);
        Assert.All(
            report.RankObservations,
            observation => Assert.True(observation.DecisionTimestamp < BarTimestampFor(observation.DecisionDate).AddDays(1)));
        Assert.False(report.PromotionEligible);
        Assert.Contains(
            "static_current_universe_without_historical_membership_evidence",
            report.PromotionBlockers);
        Assert.Contains(
            "adjusted_price_and_corporate_action_evidence_missing",
            report.PromotionBlockers);
    }

    [Fact]
    public void Analyze_SkipRecentBars_PreventsUnconfirmedSpikeFromChangingCurrentRank()
    {
        var bars = new Dictionary<string, IReadOnlyList<OhlcvBar>>(StringComparer.OrdinalIgnoreCase)
        {
            ["SPY"] = BuildBars("SPY", 24, index => 100m + index),
            ["STEADY"] = BuildBars("STEADY", 24, index => 20m + index),
            ["SPIKE"] = BuildBars("SPIKE", 24, index => index >= 10 ? 200m : 20m),
            ["FLAT1"] = BuildBars("FLAT1", 24, _ => 30m),
            ["FLAT2"] = BuildBars("FLAT2", 24, _ => 40m),
        };
        var definition = Definition() with
        {
            Options = Definition().Options with
            {
                MomentumLookbackBars = 8,
                SkipRecentBars = 4,
                Cells = [MomentumResearchCell.MomentumOnly]
            }
        };

        var report = new CrossSectionalMomentumResearchAnalyzer().Analyze(bars, definition);
        var decisionDate = DateOnly.FromDateTime(new DateTime(2026, 1, 12));
        var topTicker = report.RankObservations
            .Where(observation =>
                observation.DecisionDate == decisionDate &&
                observation.ForwardHorizonBars == 1 &&
                observation.Rank == 1)
            .Select(observation => observation.Ticker)
            .Distinct()
            .Single();

        Assert.Equal("STEADY", topTicker);
    }

    [Fact]
    public void Analyze_ReportsGrossReturnAndDeductsFrozenRoundTripCosts()
    {
        var bars = new Dictionary<string, IReadOnlyList<OhlcvBar>>(StringComparer.OrdinalIgnoreCase)
        {
            ["SPY"] = BuildBars("SPY", 24, index => 100m + index),
            ["A"] = BuildBars("A", 24, index => 20m + index),
            ["B"] = BuildBars("B", 24, index => 30m + index),
            ["C"] = BuildBars("C", 24, index => 40m + index),
            ["D"] = BuildBars("D", 24, index => 50m + index)
        };
        var costs = new MomentumExecutionCostAssumptions(
            CommissionPerSideBps: 1m,
            RegulatoryExitBps: 0.2m,
            FullSpreadBps: 4m,
            SlippagePerSideBps: 5m);

        var report = new CrossSectionalMomentumResearchAnalyzer().Analyze(
            bars,
            Definition(),
            executionCosts: costs);

        Assert.NotNull(report.ExecutionCosts);
        Assert.DoesNotContain(
            "momentum_forward_returns_are_gross_before_execution_costs",
            report.PromotionBlockers);
        Assert.All(
            report.RankObservations,
            observation => Assert.Equal(
                costs.RoundTripCostPct,
                observation.GrossForwardReturnPct - observation.ForwardReturnPct));
    }

    [Fact]
    public void Analyze_HoldoutIsChronologicalAndNeverPromotesStaticUniverse()
    {
        var bars = new Dictionary<string, IReadOnlyList<OhlcvBar>>(StringComparer.OrdinalIgnoreCase)
        {
            ["SPY"] = BuildBars("SPY", 30, index => 100m + index),
            ["A"] = BuildBars("A", 30, index => 20m + index),
            ["B"] = BuildBars("B", 30, index => 30m + index),
            ["C"] = BuildBars("C", 30, index => 40m + index),
            ["D"] = BuildBars("D", 30, index => 50m + index),
        };

        var report = new CrossSectionalMomentumResearchAnalyzer().Analyze(bars, Definition());

        Assert.True(report.DecisionDateCount > 1);
        Assert.True(report.DevelopmentDecisionDateCount > 0);
        Assert.True(report.ValidationDecisionDateCount > 0);
        Assert.True(report.HoldoutDecisionDateCount > 0);
        Assert.NotNull(report.HoldoutStartDate);
        Assert.All(
            report.RankObservations.Where(observation => observation.Segment == "holdout"),
            observation => Assert.True(observation.DecisionDate >= report.HoldoutStartDate));
        Assert.False(report.PromotionEligible);
    }

    [Fact]
    public void Analyze_UsesNextBarOpenAsForwardReturnEntry()
    {
        var bars = new Dictionary<string, IReadOnlyList<OhlcvBar>>(StringComparer.OrdinalIgnoreCase)
        {
            ["SPY"] = BuildBars("SPY", 24, index => 100m + index),
            ["A"] = BuildBars("A", 24, index => 20m + index, index => index == 7 ? 100m : 20m + index),
            ["B"] = BuildBars("B", 24, index => 30m + index),
            ["C"] = BuildBars("C", 24, index => 40m + index),
            ["D"] = BuildBars("D", 24, index => 50m + index),
        };
        var definition = Definition() with
        {
            Options = Definition().Options with
            {
                DecisionCadenceBars = 20,
                Cells = [MomentumResearchCell.MomentumOnly]
            }
        };

        var report = new CrossSectionalMomentumResearchAnalyzer().Analyze(bars, definition);
        var observation = report.RankObservations.First(item =>
            item.Ticker == "A" && item.ForwardHorizonBars == 1);

        Assert.Equal(-73m, observation.ForwardReturnPct);
    }

    [Fact]
    public void Analyze_MonthEndSchedule_UsesCompletedMonthEndBarOnly()
    {
        var bars = new Dictionary<string, IReadOnlyList<OhlcvBar>>(StringComparer.OrdinalIgnoreCase)
        {
            ["SPY"] = BuildBusinessDayBars("SPY", new DateOnly(2025, 1, 1), 100, index => 100m + index),
            ["A"] = BuildBusinessDayBars("A", new DateOnly(2025, 1, 1), 100, index => 20m + index),
            ["B"] = BuildBusinessDayBars("B", new DateOnly(2025, 1, 1), 100, index => 30m + index),
            ["C"] = BuildBusinessDayBars("C", new DateOnly(2025, 1, 1), 100, index => 40m + index),
            ["D"] = BuildBusinessDayBars("D", new DateOnly(2025, 1, 1), 100, index => 50m + index)
        };
        var definition = Definition() with
        {
            AdjustedPricesConfirmed = true,
            PointInTimeUniverseEvidence = true,
            Options = Definition().Options with
            {
                FormationSchedule = MomentumFormationSchedule.MonthEnd,
                Cells = [MomentumResearchCell.MomentumOnly]
            }
        };

        var report = new CrossSectionalMomentumResearchAnalyzer().Analyze(bars, definition);

        Assert.NotEmpty(report.RankObservations);
        Assert.All(
            report.RankObservations,
            observation =>
            {
                var nextSession = NextBusinessDay(observation.DecisionDate);
                Assert.NotEqual(observation.DecisionDate.Month, nextSession.Month);
            });
        Assert.False(report.PromotionEligible);
        Assert.Contains(
            "research_report_requires_shared_brain_validation_before_promotion",
            report.PromotionBlockers);
    }

    [Fact]
    public void Analyze_RanksTickerAtFormationEvenWhenItsFutureOutcomeIsMissing()
    {
        var bars = new Dictionary<string, IReadOnlyList<OhlcvBar>>(StringComparer.OrdinalIgnoreCase)
        {
            ["SPY"] = BuildBars("SPY", 24, index => 100m + index),
            ["ENDS_HERE"] = BuildBars("ENDS_HERE", 12, index => 20m + (index * 3m)),
            ["B"] = BuildBars("B", 24, index => 30m + index),
            ["C"] = BuildBars("C", 24, index => 40m + index),
            ["D"] = BuildBars("D", 24, index => 50m + index),
        };
        var baseDefinition = Definition();
        var definition = baseDefinition with
        {
            Options = baseDefinition.Options with
            {
                DecisionCadenceBars = 1,
                Cells = [MomentumResearchCell.MomentumOnly]
            }
        };

        var report = new CrossSectionalMomentumResearchAnalyzer().Analyze(bars, definition);
        var finalAvailableDate = DateOnly.FromDateTime(new DateTime(2026, 1, 13));

        Assert.Contains(
            report.RankObservations,
            observation => observation.DecisionDate == finalAvailableDate);
        Assert.DoesNotContain(
            report.RankObservations,
            observation =>
                observation.DecisionDate == finalAvailableDate &&
                observation.Ticker == "ENDS_HERE");
    }

    [Fact]
    public void Analyze_AppliesPointInTimeMembershipAtEachFormationDate()
    {
        var bars = new Dictionary<string, IReadOnlyList<OhlcvBar>>(StringComparer.OrdinalIgnoreCase)
        {
            ["SPY"] = BuildBars("SPY", 30, index => 100m + index),
            ["EXCLUDED"] = BuildBars("EXCLUDED", 30, index => 20m + (index * 4m)),
            ["A"] = BuildBars("A", 30, index => 30m + index),
            ["B"] = BuildBars("B", 30, index => 40m + index),
            ["C"] = BuildBars("C", 30, index => 50m + index),
            ["D"] = BuildBars("D", 30, index => 60m + index),
            ["E"] = BuildBars("E", 30, index => 70m + index)
        };
        var eligible = Enumerable.Range(0, 35)
            .ToDictionary(
                index => new DateOnly(2026, 1, 1).AddDays(index),
                _ => (IReadOnlySet<string>)new HashSet<string>(
                    ["A", "B", "C", "D", "E"],
                    StringComparer.OrdinalIgnoreCase));
        var definition = Definition() with
        {
            PointInTimeUniverseEvidence = true,
            AdjustedPricesConfirmed = true
        };

        var report = new CrossSectionalMomentumResearchAnalyzer().Analyze(
            bars,
            definition,
            eligible);

        Assert.NotEmpty(report.RankObservations);
        Assert.DoesNotContain(
            report.RankObservations,
            observation => observation.Ticker == "EXCLUDED");
        Assert.DoesNotContain(
            "static_current_universe_without_historical_membership_evidence",
            report.PromotionBlockers);
    }

    [Fact]
    public void Analyze_UsesAsTradedBarsForDollarVolumeEligibility()
    {
        var adjusted = new Dictionary<string, IReadOnlyList<OhlcvBar>>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["SPY"] = BuildBars("SPY", 30, index => 100m + index),
            ["A"] = BuildBars("A", 30, index => 1_000m + index),
            ["B"] = BuildBars("B", 30, index => 1_100m + index),
            ["C"] = BuildBars("C", 30, index => 1_200m + index),
            ["D"] = BuildBars("D", 30, index => 1_300m + index)
        };
        var asTraded = adjusted.ToDictionary(
            item => item.Key,
            item => (IReadOnlyList<OhlcvBar>)item.Value
                .Select(bar => bar with
                {
                    Open = 1m,
                    High = 1m,
                    Low = 1m,
                    Close = 1m,
                    Volume = 1m
                })
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);
        var baseline = Definition();
        var definition = baseline with
        {
            Options = baseline.Options with
            {
                MinimumAverageDollarVolume = 100m,
                Cells = [MomentumResearchCell.MomentumOnly]
            }
        };

        var report = new CrossSectionalMomentumResearchAnalyzer().Analyze(
            adjusted,
            definition,
            eligibleSymbolsByDate: null,
            asTradedDailyBarsByTicker: asTraded);

        Assert.Empty(report.RankObservations);
        Assert.True(report.SkippedForCandidateCount > 0);
    }

    [Fact]
    public void Analyze_FrozenPartitionBoundariesDoNotMoveWhenDataIsAppended()
    {
        var baseline = Definition();
        var validationStart = new DateOnly(2026, 1, 15);
        var holdoutStart = new DateOnly(2026, 1, 23);
        var definition = baseline with
        {
            Options = baseline.Options with
            {
                ValidationStartDate = validationStart,
                HoldoutStartDate = holdoutStart
            }
        };
        var shortBars = new Dictionary<string, IReadOnlyList<OhlcvBar>>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["SPY"] = BuildBars("SPY", 30, index => 100m + index),
            ["A"] = BuildBars("A", 30, index => 20m + index),
            ["B"] = BuildBars("B", 30, index => 30m + index),
            ["C"] = BuildBars("C", 30, index => 40m + index),
            ["D"] = BuildBars("D", 30, index => 50m + index)
        };
        var longBars = shortBars.ToDictionary(
            item => item.Key,
            item => (IReadOnlyList<OhlcvBar>)BuildBars(
                item.Key,
                45,
                index => item.Value[0].Close + index),
            StringComparer.OrdinalIgnoreCase);

        var shortReport = new CrossSectionalMomentumResearchAnalyzer().Analyze(
            shortBars,
            definition);
        var longReport = new CrossSectionalMomentumResearchAnalyzer().Analyze(
            longBars,
            definition);

        Assert.Equal(validationStart, shortReport.ValidationStartDate);
        Assert.Equal(holdoutStart, shortReport.HoldoutStartDate);
        Assert.Equal(shortReport.ValidationStartDate, longReport.ValidationStartDate);
        Assert.Equal(shortReport.HoldoutStartDate, longReport.HoldoutStartDate);
        Assert.DoesNotContain("study_partitions_not_frozen", longReport.PromotionBlockers);
    }

    [Fact]
    public void Analyze_AdditiveCellsKeepFrozenSlotsAndUseConfiguredCash()
    {
        var bars = new Dictionary<string, IReadOnlyList<OhlcvBar>>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["SPY"] = BuildBars("SPY", 16, index => 100m + index),
            ["FAILS_GATE"] = BuildBars(
                "FAILS_GATE",
                16,
                index => index switch
                {
                    0 => 10m,
                    1 => 20m,
                    2 => 30m,
                    3 => 60m,
                    4 => 100m,
                    _ => 2m + (index / 10m)
                }),
            ["SECOND"] = BuildBars("SECOND", 16, index => 10m + (3m * index)),
            ["THIRD"] = BuildBars("THIRD", 16, index => 10m + (2m * index)),
            ["FOURTH"] = BuildBars("FOURTH", 16, index => 10m + index),
            ["FIFTH"] = BuildBars("FIFTH", 16, index => 10m + (0.5m * index))
        };
        var baseline = Definition();
        var definition = baseline with
        {
            Options = baseline.Options with
            {
                DecisionCadenceBars = 100,
                ForwardHorizons = [1],
                QuantileCount = 5,
                MinimumCandidatesPerDate = 5,
                PrimarySelectionFraction = 0.30m,
                Cells =
                [
                    MomentumResearchCell.MomentumOnly,
                    MomentumResearchCell.MomentumStockTrend
                ],
                AnnualCashReturnPct = 12m
            }
        };

        var report = new CrossSectionalMomentumResearchAnalyzer().Analyze(
            bars,
            definition);
        var decisionDate = report.RankObservations.Min(value => value.DecisionDate);
        var benchmarkSlots = report.RankObservations
            .Where(value =>
                value.Cell == MomentumResearchCell.MomentumOnly &&
                value.DecisionDate == decisionDate &&
                value.ForwardHorizonBars == 1 &&
                value.IsPrimarySelection)
            .OrderBy(value => value.Rank)
            .ToArray();
        var trendSlots = report.RankObservations
            .Where(value =>
                value.Cell == MomentumResearchCell.MomentumStockTrend &&
                value.DecisionDate == decisionDate &&
                value.ForwardHorizonBars == 1 &&
                value.IsPrimarySelection)
            .OrderBy(value => value.Rank)
            .ToArray();

        Assert.Equal(["FAILS_GATE", "SECOND"], benchmarkSlots.Select(value => value.Ticker));
        Assert.Equal(
            benchmarkSlots.Select(value => new { value.Ticker, value.Rank }),
            trendSlots.Select(value => new { value.Ticker, value.Rank }));
        var rejected = Assert.Single(
            trendSlots,
            value => value.Ticker == "FAILS_GATE");
        Assert.False(rejected.GatePassed);
        Assert.Equal(MomentumSlotReturnSource.Cash, rejected.SlotReturnSource);
        Assert.True(rejected.ForwardReturnPct > 0m);
        Assert.NotEqual(
            rejected.AssetGrossForwardReturnPct,
            rejected.GrossForwardReturnPct);
        Assert.DoesNotContain(
            report.RankObservations,
            value =>
                value.Cell == MomentumResearchCell.MomentumStockTrend &&
                value.DecisionDate == decisionDate &&
                value.Ticker == "THIRD" &&
                value.IsPrimarySelection);
        Assert.Equal(
            trendSlots.Length,
            report.FormationLedger.Count(value =>
                value.Cell == MomentumResearchCell.MomentumStockTrend &&
                value.DecisionDate == decisionDate &&
                value.ForwardHorizonBars == 1 &&
                value.IsPrimarySelection));
    }

    [Fact]
    public void Analyze_AdditiveComparisonReportsIncrementAgainstAcceptedParent()
    {
        var bars = new Dictionary<string, IReadOnlyList<OhlcvBar>>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["SPY"] = BuildBars("SPY", 24, index => 100m + index),
            ["A"] = BuildBars("A", 24, index => 20m + (3m * index)),
            ["B"] = BuildBars("B", 24, index => 30m + (2m * index)),
            ["C"] = BuildBars("C", 24, index => 40m + index),
            ["D"] = BuildBars("D", 24, index => 50m + (0.5m * index))
        };
        var baseline = Definition();
        var definition = baseline with
        {
            Options = baseline.Options with
            {
                Cells =
                [
                    MomentumResearchCell.MomentumOnly,
                    MomentumResearchCell.MomentumStockTrend
                ],
                PrimarySelectionFraction = 0.30m
            }
        };

        var report = new CrossSectionalMomentumResearchAnalyzer().Analyze(
            bars,
            definition);
        var comparison = Assert.Single(
            report.Comparisons,
            value =>
                value.Cell == MomentumResearchCell.MomentumStockTrend &&
                value.Segment == MomentumStudySegment.Full &&
                value.ForwardHorizonBars == 1);

        Assert.Equal(MomentumResearchCell.MomentumOnly, comparison.ParentCell);
        Assert.NotNull(comparison.ParentPrimaryMeanReturnPct);
        Assert.Equal(
            comparison.PrimaryMeanReturnPct -
            comparison.ParentPrimaryMeanReturnPct,
            comparison.IncrementalReturnVsParentPct);
        Assert.True(comparison.EligibleUniverseMeanTurnoverPct >= 0m);
        Assert.Contains(
            "point_in_time_sector_evidence_unavailable",
            comparison.BenchmarkBlockers);
    }

    [Fact]
    public void CanonicalArtifacts_AreNamedDeterministicAndDefensivelyCopied()
    {
        var bars = new Dictionary<string, IReadOnlyList<OhlcvBar>>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["SPY"] = BuildBars("SPY", 24, index => 100m + index),
            ["A"] = BuildBars("A", 24, index => 20m + index),
            ["B"] = BuildBars("B", 24, index => 30m + index),
            ["C"] = BuildBars("C", 24, index => 40m + index),
            ["D"] = BuildBars("D", 24, index => 50m + index)
        };
        var report = new CrossSectionalMomentumResearchAnalyzer().Analyze(
            bars,
            Definition());
        var audit = new MomentumResearchAuditAnalyzer().Analyze(
            report.RankObservations,
            report.Options.DecisionCadenceBars);

        var first = MomentumCanonicalArtifactBuilder.Build(report, audit);
        var replay = MomentumCanonicalArtifactBuilder.Build(report, audit);

        Assert.Equal(
            [
                MomentumCanonicalArtifactName.Report,
                MomentumCanonicalArtifactName.FormationLedger,
                MomentumCanonicalArtifactName.AuditReport
            ],
            first.Select(value => value.Name));
        Assert.Equal(first.Select(value => value.Sha256), replay.Select(value => value.Sha256));
        var ledger = Assert.Single(
            first,
            value => value.Name == MomentumCanonicalArtifactName.FormationLedger);
        var original = ledger.GetUtf8Json();
        var mutated = ledger.GetUtf8Json();
        mutated[0] = mutated[0] == (byte)'{' ? (byte)'[' : (byte)'{';
        Assert.Equal(original, ledger.GetUtf8Json());
        Assert.NotEmpty(original);
    }

    [Fact]
    public void Analyze_SectorNeutralBenchmarkRequiresConfirmedPointInTimeEvidence()
    {
        var bars = new Dictionary<string, IReadOnlyList<OhlcvBar>>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["SPY"] = BuildBars("SPY", 24, index => 100m + index),
            ["A"] = BuildBars("A", 24, index => 20m + (4m * index)),
            ["B"] = BuildBars("B", 24, index => 30m + (3m * index)),
            ["C"] = BuildBars("C", 24, index => 40m + (2m * index)),
            ["D"] = BuildBars("D", 24, index => 50m + index)
        };
        var definition = Definition();
        var diagnostic = new CrossSectionalMomentumResearchAnalyzer().Analyze(
            bars,
            definition);
        var sectors = diagnostic.RankObservations
            .Select(value => value.DecisionDate)
            .Distinct()
            .ToDictionary(
                date => date,
                _ => (IReadOnlyDictionary<string, string>)
                    new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["A"] = "technology",
                        ["D"] = "technology",
                        ["B"] = "industrials",
                        ["C"] = "industrials"
                    });
        var evidence = new MomentumAdditiveResearchEvidence(
            new Dictionary<DateOnly, IReadOnlySet<string>>(),
            sectors)
        {
            PointInTimeSectorCoverageConfirmed = true
        };

        var report = new CrossSectionalMomentumResearchAnalyzer().Analyze(
            bars,
            definition,
            additiveEvidence: evidence);
        var comparison = Assert.Single(
            report.Comparisons,
            value =>
                value.Cell == MomentumResearchCell.MomentumOnly &&
                value.Segment == MomentumStudySegment.Full &&
                value.ForwardHorizonBars == 1);

        Assert.NotNull(comparison.SectorNeutralTopMinusBottomPct);
        Assert.DoesNotContain(
            "point_in_time_sector_evidence_unavailable",
            comparison.BenchmarkBlockers);
        Assert.DoesNotContain(
            "point_in_time_sector_evidence_unavailable",
            report.PromotionBlockers);
    }

    [Fact]
    public void FrozenV7VcpContract_MatchesAuditedDefinition()
    {
        var options = MomentumFrozenV7Vcp.Definition;

        Assert.Equal(60, options.LookbackBars);
        Assert.Equal(2, options.PivotStrengthBars);
        Assert.Equal(2, options.MinimumContractions);
        Assert.Equal(4, options.MaximumContractions);
        Assert.Equal(0.90m, options.MaximumDepthRatioToPrevious);
        Assert.Equal(0.85m, options.MaximumContractionToAdvanceVolumeRatio);
        Assert.False(options.RequireProgressiveContractionVolume);
        Assert.Equal(3, options.MinimumVolumeReferenceBars);
        Assert.Equal(1m, MomentumFrozenV7Vcp.MinimumBreakoutVolumeRatio);
    }

    [Fact]
    public void Analyze_ClassifiedCatalystCellFailsClosedWithoutEvidence()
    {
        var bars = new Dictionary<string, IReadOnlyList<OhlcvBar>>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["SPY"] = BuildBars("SPY", 80, index => 100m + index),
            ["A"] = BuildBars("A", 80, index => 20m + index),
            ["B"] = BuildBars("B", 80, index => 30m + index),
            ["C"] = BuildBars("C", 80, index => 40m + index),
            ["D"] = BuildBars("D", 80, index => 50m + index)
        };
        var baseline = Definition();
        var definition = baseline with
        {
            Options = baseline.Options with
            {
                Cells =
                [
                    MomentumResearchCell.MomentumOnly,
                    MomentumResearchCell.MomentumStockTrend,
                    MomentumResearchCell.MomentumStockTrendVcpV7,
                    MomentumResearchCell.MomentumStockTrendClassifiedCatalyst
                ]
            }
        };

        var report = new CrossSectionalMomentumResearchAnalyzer().Analyze(
            bars,
            definition);

        Assert.Contains(
            "classified_catalyst_evidence_unavailable",
            report.PromotionBlockers);
        Assert.All(
            report.RankObservations.Where(value =>
                value.Cell ==
                    MomentumResearchCell.MomentumStockTrendClassifiedCatalyst &&
                value.IsPrimarySelection),
            value => Assert.Equal(
                MomentumSlotReturnSource.Cash,
                value.SlotReturnSource));
        Assert.Equal(
            MomentumResearchCell.MomentumStockTrend,
            MomentumResearchCell.ParentOf(
                MomentumResearchCell.MomentumStockTrendClassifiedCatalyst));
        Assert.Equal(
            MomentumResearchCell.MomentumOnly,
            MomentumResearchCell.ParentOf(
                MomentumResearchCell.MomentumClassifiedCatalyst));
        Assert.False(MomentumResearchCell.RequiresVcp(
            MomentumResearchCell.MomentumStockTrendClassifiedCatalyst));
    }

    [Fact]
    public void Analyze_PrimaryComparisonEqualWeightsFormationPortfoliosAndReportsAbsolutePnlConcentration()
    {
        var bars = new Dictionary<string, IReadOnlyList<OhlcvBar>>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["SPY"] = BuildBars("SPY", 40, index => 100m + index),
            ["A"] = BuildBars(
                "A",
                40,
                index => 20m + (index * 4m),
                index => (20m + (index * 4m)) / 2m),
            ["B"] = BuildBars("B", 40, index => 40m + (index * 2m)),
            ["C"] = BuildBars("C", 40, index => 50m + index),
            ["D"] = BuildBars("D", 40, index => 60m + (index * 0.8m)),
            ["E"] = BuildBars("E", 40, index => 70m + (index * 0.7m)),
            ["F"] = BuildBars("F", 40, index => 80m + (index * 0.6m)),
            ["G"] = BuildBars("G", 40, index => 90m + (index * 0.5m)),
            ["H"] = BuildBars("H", 40, index => 100m + (index * 0.4m))
        };
        var eligible = Enumerable.Range(0, 40)
            .ToDictionary(
                index => new DateOnly(2026, 1, 2).AddDays(index),
                index => (IReadOnlySet<string>)new HashSet<string>(
                    index < 18
                        ? ["A", "B", "C", "D"]
                        : ["A", "B", "C", "D", "E", "F", "G", "H"],
                    StringComparer.OrdinalIgnoreCase));
        var baseline = Definition();
        var definition = baseline with
        {
            PointInTimeUniverseEvidence = true,
            Options = baseline.Options with
            {
                ForwardHorizons = [1],
                Cells = [MomentumResearchCell.MomentumOnly]
            }
        };

        var report = new CrossSectionalMomentumResearchAnalyzer().Analyze(
            bars,
            definition,
            eligible);
        var primary = report.RankObservations
            .Where(value =>
                value.Cell == MomentumResearchCell.MomentumOnly &&
                value.ForwardHorizonBars == 1 &&
                value.IsPrimarySelection)
            .ToArray();
        var slotCounts = primary
            .GroupBy(value => value.DecisionDate)
            .Select(group => group.Count())
            .Distinct()
            .Order()
            .ToArray();
        var expectedFormationMean = decimal.Round(
            primary.GroupBy(value => value.DecisionDate)
                .Average(group => group.Average(value => value.ForwardReturnPct)),
            4);
        var rawObservationMean = decimal.Round(
            primary.Average(value => value.ForwardReturnPct),
            4);
        var comparison = Assert.Single(
            report.Comparisons,
            value =>
                value.Cell == MomentumResearchCell.MomentumOnly &&
                value.Segment == MomentumStudySegment.Full &&
                value.ForwardHorizonBars == 1);

        Assert.Equal([2, 3], slotCounts);
        Assert.Equal(expectedFormationMean, comparison.PrimaryMeanReturnPct);
        Assert.NotEqual(rawObservationMean, comparison.PrimaryMeanReturnPct);
        Assert.InRange(
            comparison.LargestTickerAbsolutePnlContributionPct,
            0.0001m,
            100m);
        Assert.Equal(
            75m,
            comparison.LargestMonthAbsolutePnlContributionPct);
        Assert.InRange(
            comparison.LargestFormationAbsolutePnlContributionPct,
            0.0001m,
            100m);
    }

    [Fact]
    public void Analyze_ClassifiedCatalystCellsArePairedDirectlyWithFrozenA1OrA2Parents()
    {
        Assert.Equal(
            MomentumResearchCell.MomentumOnly,
            MomentumResearchCell.ParentOf(
                MomentumResearchCell.MomentumClassifiedCatalyst));
        Assert.Equal(
            MomentumResearchCell.MomentumStockTrend,
            MomentumResearchCell.ParentOf(
                MomentumResearchCell.MomentumStockTrendClassifiedCatalyst));
        Assert.NotEqual(
            MomentumResearchCell.MomentumStockTrendVcpV7,
            MomentumResearchCell.ParentOf(
                MomentumResearchCell.MomentumStockTrendClassifiedCatalyst));
        Assert.False(MomentumResearchCell.RequiresVcp(
            MomentumResearchCell.MomentumClassifiedCatalyst));
        Assert.False(MomentumResearchCell.RequiresVcp(
            MomentumResearchCell.MomentumStockTrendClassifiedCatalyst));
    }

    private static CrossSectionalMomentumStudyDefinition Definition() =>
        new(
            "test",
            "SPY",
            "static test universe",
            false,
            false,
            new CrossSectionalMomentumResearchOptions(
                MomentumLookbackBars: 6,
                SkipRecentBars: 2,
                FastTrendSmaBars: 2,
                SlowTrendSmaBars: 4,
                AverageDollarVolumeBars: 2,
                MinimumAverageDollarVolume: 0m,
                FormationSchedule: MomentumFormationSchedule.EveryNBars,
                DecisionCadenceBars: 2,
                ForwardHorizons: [1, 2],
                QuantileCount: 2,
                PrimarySelectionFraction: 0.30m,
                MinimumCandidatesPerDate: 4,
                DevelopmentFraction: 0.50m,
                ValidationFraction: 0.25m,
                HoldoutFraction: 0.25m,
                MinimumFormationDates: 1,
                MinimumHoldoutFormationDates: 1,
                MinimumDistinctSelectedTickers: 1,
                Cells: [MomentumResearchCell.MomentumOnly],
                ExchangeTimezone: "America/New_York"));

    private static IReadOnlyList<OhlcvBar> BuildBars(
        string ticker,
        int count,
        Func<int, decimal> close,
        Func<int, decimal>? open = null)
    {
        var start = new DateTimeOffset(2026, 1, 2, 5, 0, 0, TimeSpan.Zero);
        return Enumerable.Range(0, count)
            .Select(index =>
            {
                var closeValue = close(index);
                var openValue = open?.Invoke(index) ?? closeValue;
                return new OhlcvBar(
                    ticker,
                    start.AddDays(index),
                    "1d",
                    openValue,
                    Math.Max(openValue, closeValue) + 1m,
                    Math.Min(openValue, closeValue) - 1m,
                    closeValue,
                    1_000_000m);
            })
            .ToArray();
    }

    private static IReadOnlyList<OhlcvBar> BuildBusinessDayBars(
        string ticker,
        DateOnly start,
        int count,
        Func<int, decimal> close)
    {
        var dates = new List<DateOnly>();
        for (var date = start; dates.Count < count; date = date.AddDays(1))
        {
            if (date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
            {
                dates.Add(date);
            }
        }

        return dates.Select((date, index) =>
        {
            var value = close(index);
            return new OhlcvBar(
                ticker,
                new DateTimeOffset(date.Year, date.Month, date.Day, 21, 0, 0, TimeSpan.Zero),
                "1d",
                value,
                value + 1m,
                value - 1m,
                value,
                1_000_000m);
        }).ToArray();
    }

    private static DateOnly NextBusinessDay(DateOnly date)
    {
        do
        {
            date = date.AddDays(1);
        }
        while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
        return date;
    }

    private static DateTimeOffset BarTimestampFor(DateOnly date) =>
        new(date.Year, date.Month, date.Day, 5, 0, 0, TimeSpan.Zero);
}
