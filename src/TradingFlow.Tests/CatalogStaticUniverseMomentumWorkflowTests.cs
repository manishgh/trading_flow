using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Moq;
using TradingFlow.Data.Evidence;
using TradingFlow.Data.Evidence.Governance;
using TradingFlow.Data.Evidence.Research;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Research;
using TradingFlow.Research;
using TradingFlow.Research.Momentum;
using TradingFlow.Research.Workflows;

namespace TradingFlow.Tests;

public sealed class CatalogStaticUniverseMomentumWorkflowTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
    private const string BiasWarning =
        "Symbols were selected from a current snapshot; survivorship and selection bias are present.";
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "trading-flow-static-universe-workflow-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Diagnostic_IsImmutableNetOfCostsAndNeverPromotionEligible()
    {
        var adjusted = Manifest(
            EvidenceDatasetKind.MarketBarsResearchAdjusted,
            "static-adjusted");
        var asTraded = Manifest(
            EvidenceDatasetKind.MarketBarsAsTraded,
            "static-as-traded");
        var bars = BuildBars();
        var adjustedRows = BuildMarketRows(bars, "all");
        var asTradedRows = BuildMarketRows(bars, "raw");
        EvidenceResearchRunManifest? registered = null;
        var openedManifests = new List<EvidenceDatasetManifest>();

        var catalog = new Mock<IEvidenceCatalog>(MockBehavior.Strict);
        catalog
            .Setup(value => value.GetDatasetAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) =>
                new[] { adjusted, asTraded }.SingleOrDefault(manifest =>
                    manifest.DatasetId.Equals(id, StringComparison.Ordinal)));
        catalog
            .Setup(value => value.RegisterResearchRunAsync(
                It.IsAny<EvidenceResearchRunManifest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                EvidenceResearchRunManifest manifest,
                CancellationToken _) =>
            {
                if (registered is null)
                {
                    registered = manifest;
                    return new EvidenceCatalogCommitResult(
                        manifest.ResearchRunId,
                        false);
                }

                Assert.Equal(
                    EvidenceCanonicalJson.SerializeToUtf8Bytes(registered),
                    EvidenceCanonicalJson.SerializeToUtf8Bytes(manifest));
                return new EvidenceCatalogCommitResult(
                    manifest.ResearchRunId,
                    true);
            });
        catalog
            .Setup(value => value.GetResearchRunAsync(
                "static-diagnostic-run",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => registered);

        var reader = new Mock<IEvidencePartitionDataReader>(MockBehavior.Strict);
        reader
            .Setup(value => value.ReadMarketBarsAsync(
                It.Is<EvidenceDatasetManifest>(manifest =>
                    manifest.Kind == EvidenceDatasetKind.MarketBarsResearchAdjusted),
                It.IsAny<CancellationToken>()))
            .Callback((EvidenceDatasetManifest manifest, CancellationToken _) =>
                openedManifests.Add(manifest))
            .ReturnsAsync(adjustedRows.Where(row =>
                row.BarStartUtc < Utc(2026, 1, 22)).ToArray());
        reader
            .Setup(value => value.ReadMarketBarsAsync(
                It.Is<EvidenceDatasetManifest>(manifest =>
                    manifest.Kind == EvidenceDatasetKind.MarketBarsAsTraded),
                It.IsAny<CancellationToken>()))
            .Callback((EvidenceDatasetManifest manifest, CancellationToken _) =>
                openedManifests.Add(manifest))
            .ReturnsAsync(asTradedRows.Where(row =>
                row.BarStartUtc < Utc(2026, 1, 22)).ToArray());
        var workflow = Workflow(catalog.Object, reader.Object, "objects-replay");
        var request = Request(adjusted, asTraded);

        var first =
            await workflow.RunStaticUniverseMomentumDiagnosticAsync(request);
        var replay =
            await workflow.RunStaticUniverseMomentumDiagnosticAsync(request);

        Assert.False(first.AlreadyRegistered);
        Assert.True(replay.AlreadyRegistered);
        Assert.Equal(
            EvidenceCanonicalJson.SerializeToUtf8Bytes(first.Manifest),
            EvidenceCanonicalJson.SerializeToUtf8Bytes(replay.Manifest));
        Assert.Equal(["A", "B", "C", "D", "E"], first.FrozenSymbols);
        Assert.Equal(BiasWarning, first.SurvivorshipSelectionBiasWarning);
        Assert.False(first.Report.PointInTimeUniverseEvidence);
        Assert.False(first.Report.DataEvidenceReady);
        Assert.False(first.Report.PromotionEligible);
        Assert.False(first.Manifest.EvidenceReady);
        Assert.Contains(
            CatalogResearchWorkflow.StaticUniversePromotionBlocker,
            first.Report.PromotionBlockers);
        Assert.Contains(
            CatalogResearchWorkflow.StaticUniversePromotionBlocker,
            first.Manifest.ReadinessFailures);
        Assert.Contains(
            CatalogResearchWorkflow.StaticUniverseHoldoutUnopenedBlocker,
            first.Manifest.ReadinessFailures);
        Assert.Equal(2, first.Manifest.InputDatasets.Count);
        Assert.DoesNotContain(
            first.Manifest.InputDatasets,
            dataset => dataset.DatasetId.Contains(
                "universe",
                StringComparison.OrdinalIgnoreCase));

        var costs = Assert.IsType<MomentumExecutionCostAssumptions>(
            first.Report.ExecutionCosts);
        Assert.Equal(26.2m, costs.RoundTripCostBps);
        Assert.NotEmpty(first.Report.RankObservations);
        Assert.All(
            first.Report.RankObservations,
            observation => Assert.Equal(
                costs.RoundTripCostPct,
                observation.GrossForwardReturnPct - observation.ForwardReturnPct));
        Assert.Equal(0, first.Report.HoldoutDecisionDateCount);
        Assert.DoesNotContain(
            first.Report.RankObservations,
            observation => observation.Segment == MomentumStudySegment.Holdout);
        Assert.All(
            openedManifests,
            manifest => Assert.All(
                manifest.Partitions,
                partition => Assert.True(
                    partition.MaximumSourceTimestampUtc < Utc(2026, 1, 22))));
        catalog.Verify(value => value.ReserveHoldoutAsync(
            It.IsAny<EvidenceHoldoutConsumption>(),
            It.IsAny<CancellationToken>()), Times.Never);
        catalog.VerifyAll();
        reader.VerifyAll();
    }

    [Fact]
    public async Task Diagnostic_FailsWhenFrozenSymbolIsMissingFromCommittedBars()
    {
        var adjusted = Manifest(
            EvidenceDatasetKind.MarketBarsResearchAdjusted,
            "missing-adjusted");
        var asTraded = Manifest(
            EvidenceDatasetKind.MarketBarsAsTraded,
            "missing-as-traded");
        var bars = BuildBars();
        var catalog = DatasetCatalog(adjusted, asTraded);
        var reader = new Mock<IEvidencePartitionDataReader>(MockBehavior.Strict);
        reader
            .Setup(value => value.ReadMarketBarsAsync(
                It.Is<EvidenceDatasetManifest>(manifest =>
                    manifest.Kind == EvidenceDatasetKind.MarketBarsResearchAdjusted),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildMarketRows(bars, "all").Where(row =>
                row.BarStartUtc < Utc(2026, 1, 22)).ToArray());
        reader
            .Setup(value => value.ReadMarketBarsAsync(
                It.Is<EvidenceDatasetManifest>(manifest =>
                    manifest.Kind == EvidenceDatasetKind.MarketBarsAsTraded),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildMarketRows(bars, "raw").Where(row =>
                row.BarStartUtc < Utc(2026, 1, 22)).ToArray());
        var request = Request(adjusted, asTraded) with
        {
            FrozenSymbols = ["A", "B", "C", "D", "MISSING"]
        };

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Workflow(catalog.Object, reader.Object, "objects-missing")
                .RunStaticUniverseMomentumDiagnosticAsync(request));

        Assert.Contains("MISSING", exception.Message, StringComparison.Ordinal);
        catalog.Verify(value => value.ReserveHoldoutAsync(
            It.IsAny<EvidenceHoldoutConsumption>(),
            It.IsAny<CancellationToken>()), Times.Never);
        catalog.Verify(value => value.RegisterResearchRunAsync(
            It.IsAny<EvidenceResearchRunManifest>(),
            It.IsAny<CancellationToken>()), Times.Never);
        reader.VerifyAll();
    }

    [Fact]
    public async Task Diagnostic_NeverReservesHoldoutWhenAnalyzerFails()
    {
        var adjusted = Manifest(
            EvidenceDatasetKind.MarketBarsResearchAdjusted,
            "ordering-adjusted");
        var asTraded = Manifest(
            EvidenceDatasetKind.MarketBarsAsTraded,
            "ordering-as-traded");
        var bars = BuildBars();
        var catalog = new Mock<IEvidenceCatalog>(MockBehavior.Strict);
        catalog
            .Setup(value => value.GetDatasetAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) =>
                new[] { adjusted, asTraded }.SingleOrDefault(manifest =>
                    manifest.DatasetId.Equals(id, StringComparison.Ordinal)));
        var reader = new Mock<IEvidencePartitionDataReader>(MockBehavior.Strict);
        reader
            .Setup(value => value.ReadMarketBarsAsync(
                It.Is<EvidenceDatasetManifest>(manifest =>
                    manifest.Kind == EvidenceDatasetKind.MarketBarsResearchAdjusted),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildMarketRows(bars, "all").Where(row =>
                row.BarStartUtc < Utc(2026, 1, 22)).ToArray());
        reader
            .Setup(value => value.ReadMarketBarsAsync(
                It.Is<EvidenceDatasetManifest>(manifest =>
                    manifest.Kind == EvidenceDatasetKind.MarketBarsAsTraded),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildMarketRows(bars, "raw").Where(row =>
                row.BarStartUtc < Utc(2026, 1, 22)).ToArray());
        var valid = Request(adjusted, asTraded);
        var invalid = valid with
        {
            Definition = valid.Definition with
            {
                Options = valid.Definition.Options with
                {
                    MomentumLookbackBars = 1,
                    SkipRecentBars = 2
                }
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Workflow(catalog.Object, reader.Object, "objects-ordering")
                .RunStaticUniverseMomentumDiagnosticAsync(invalid));

        catalog.Verify(value => value.ReserveHoldoutAsync(
            It.IsAny<EvidenceHoldoutConsumption>(),
            It.IsAny<CancellationToken>()), Times.Never);
        catalog.Verify(value => value.RegisterResearchRunAsync(
            It.IsAny<EvidenceResearchRunManifest>(),
            It.IsAny<CancellationToken>()), Times.Never);
        catalog.VerifyAll();
        reader.VerifyAll();
    }

    [Fact]
    public void WorkflowComposition_IsCatalogOnlyAndProviderFree()
    {
        var constructor = Assert.Single(
            typeof(CatalogResearchWorkflow).GetConstructors(
                BindingFlags.Public | BindingFlags.Instance));
        Assert.Equal(
            [
                typeof(IEvidenceCatalog),
                typeof(IEvidencePartitionDataReader),
                typeof(EvidenceResearchRunArtifactPackager),
                typeof(SqliteResearchTrialRegistry)
            ],
            constructor.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.DoesNotContain(
            constructor.GetParameters(),
            parameter => parameter.ParameterType.Namespace?.Contains(
                "Alpaca",
                StringComparison.OrdinalIgnoreCase) == true);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private CatalogResearchWorkflow Workflow(
        IEvidenceCatalog catalog,
        IEvidencePartitionDataReader reader,
        string objectFolder) =>
        new(
            catalog,
            reader,
            new EvidenceResearchRunArtifactPackager(
                new FileSystemImmutableArtifactStore(
                    new ImmutableArtifactStoreOptions(
                        Path.Combine(root, objectFolder)))));

    private static Mock<IEvidenceCatalog> DatasetCatalog(
        params EvidenceDatasetManifest[] manifests)
    {
        var catalog = new Mock<IEvidenceCatalog>(MockBehavior.Strict);
        catalog
            .Setup(value => value.GetDatasetAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) =>
                manifests.SingleOrDefault(manifest =>
                    manifest.DatasetId.Equals(id, StringComparison.Ordinal)));
        return catalog;
    }

    private static CatalogStaticUniverseMomentumRequest Request(
        EvidenceDatasetManifest adjusted,
        EvidenceDatasetManifest asTraded)
    {
        var validationStart = new DateOnly(2026, 1, 15);
        var holdoutStart = new DateOnly(2026, 1, 22);
        var definition = new CrossSectionalMomentumStudyDefinition(
            "static-universe-momentum-diagnostic-v1",
            "SPY",
            "Frozen current-snapshot diagnostic universe.",
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
                ExchangeTimezone: "America/New_York")
            {
                ValidationStartDate = validationStart,
                HoldoutStartDate = holdoutStart
            });
        return new CatalogStaticUniverseMomentumRequest(
            "static-diagnostic-run",
            Now,
            "git:test-commit",
            adjusted.DatasetId,
            asTraded.DatasetId,
            ["E", "A", "D", "B", "C"],
            BiasWarning,
            definition,
            new EvidenceStudyPartitions(
                new EvidenceStudyWindow(
                    "development",
                    Utc(2026, 1, 1),
                    Utc(2026, 1, 15)),
                new EvidenceStudyWindow(
                    "validation",
                    Utc(2026, 1, 15),
                    Utc(2026, 1, 22)),
                new EvidenceStudyWindow(
                    "holdout",
                    Utc(2026, 1, 22),
                    Utc(2026, 2, 1))),
            new CatalogResearchAssumptionSet(
                new CatalogCostAssumptions(1m, 0.2m),
                new CatalogSpreadAssumptions("conservative_fixed", 4m),
                new CatalogSlippageAssumptions("volume_participation", 10m),
                new CatalogBorrowAssumptions(true, 300m),
                new CatalogBenchmarkAssumptions("SPY", "total_return")));
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> BuildBars() =>
        new Dictionary<string, IReadOnlyList<OhlcvBar>>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["SPY"] = Bars("SPY", index => 100m + index),
            ["A"] = Bars("A", index => 30m + index),
            ["B"] = Bars("B", index => 40m + (index * 1.1m)),
            ["C"] = Bars("C", index => 50m + (index * 1.2m)),
            ["D"] = Bars("D", index => 60m + (index * 1.3m)),
            ["E"] = Bars("E", index => 70m + (index * 1.4m))
        };

    private static IReadOnlyList<OhlcvBar> Bars(
        string ticker,
        Func<int, decimal> close) =>
        Enumerable.Range(0, 30)
            .Select(index =>
            {
                var value = close(index);
                return new OhlcvBar(
                    ticker,
                    new DateTimeOffset(
                        2026,
                        1,
                        2,
                        5,
                        0,
                        0,
                        TimeSpan.Zero).AddDays(index),
                    "1d",
                    value,
                    value + 1m,
                    value - 1m,
                    value,
                    1_000_000m);
            })
            .ToArray();

    private static IReadOnlyList<MarketBarEvidenceRow> BuildMarketRows(
        IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> bars,
        string adjustment) =>
        bars.SelectMany(entry => entry.Value.Select(bar =>
        {
            var end = bar.Timestamp.AddHours(16);
            return new MarketBarEvidenceRow(
                1,
                "static-workflow-source-run",
                Hash("static-workflow-config"),
                "code-v1",
                "sip",
                $"security-{entry.Key}",
                entry.Key,
                bar.Timestamp,
                end,
                "1d",
                EvidenceFixedDecimal.ToPriceUnits(bar.Open),
                EvidenceFixedDecimal.ToPriceUnits(bar.High),
                EvidenceFixedDecimal.ToPriceUnits(bar.Low),
                EvidenceFixedDecimal.ToPriceUnits(bar.Close),
                null,
                Decimal.ToInt64(bar.Volume),
                null,
                adjustment,
                "USD",
                end,
                Now,
                [
                    new EvidenceRowSourceAddress(
                        $"observation-{adjustment}-{entry.Key}-{bar.Timestamp:O}",
                        Hash($"source-{adjustment}-{entry.Key}-{bar.Timestamp:O}"))
                ]);
        })).ToArray();

    private static EvidenceDatasetManifest Manifest(
        EvidenceDatasetKind kind,
        string seed)
    {
        var adjustment =
            kind == EvidenceDatasetKind.MarketBarsResearchAdjusted ? "all" : "raw";
        var sourceArtifact = Artifact(
            $"raw-{seed}",
            "raw/test",
            "application/json");
        return new EvidenceDatasetManifest(
            kind,
            1,
            Now,
            $"job-{seed}",
            Hash($"plan-{seed}"),
            Hash($"config-{seed}"),
            "code-v1",
            "normalizer-v1",
            "sip",
            [
                Partition(
                    kind,
                    seed,
                    "development-validation",
                    adjustment,
                    Utc(2026, 1, 1),
                    Utc(2026, 1, 21).AddHours(23),
                    sourceArtifact),
                Partition(
                    kind,
                    seed,
                    "holdout",
                    adjustment,
                    Utc(2026, 1, 22),
                    Utc(2026, 2, 1),
                    sourceArtifact)
            ],
            new EvidenceQualityReport(),
            new Dictionary<string, string>
            {
                ["adjustment"] = adjustment,
                ["corporate_actions_reconciled"] = "true",
                ["terminal_outcomes_reconciled"] = "true",
                ["listed_bar_coverage_pct"] = "100",
                ["benchmark_coverage_confirmed"] = "true"
            });
    }

    private static EvidenceDatasetPartitionManifest Partition(
        EvidenceDatasetKind kind,
        string seed,
        string phase,
        string adjustment,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        EvidenceArtifactReference sourceArtifact) =>
        new(
            $"partition-{seed}-{phase}",
            kind,
            1,
            new EvidencePartitionProvenance(
                "alpaca",
                "/v2/stocks/bars",
                "sip",
                adjustment,
                "USD",
                DateOnly.FromDateTime(endUtc.UtcDateTime),
                ["security-SPY", "security-A", "security-B"],
                [],
                ["SPY", "A", "B", "C", "D", "E"],
                "1d",
                startUtc,
                endUtc),
            startUtc,
            endUtc,
            90,
            Artifact(
                $"partition-{seed}-{phase}",
                "normalized/test",
                "application/vnd.apache.parquet"),
            [
                new EvidenceSourceReference(
                    $"observation-{seed}-{phase}",
                    sourceArtifact,
                    Now.AddMinutes(-1))
            ],
            "normalizer-v1",
            "code-v1",
            new EvidenceQualityReport());

    private static EvidenceArtifactReference Artifact(
        string value,
        string objectNamespace,
        string mediaType) =>
        new(
            new EvidenceContentAddress(
                Hash(value),
                Encoding.UTF8.GetByteCount(value),
                mediaType),
            new EvidenceObjectNamespace(objectNamespace));

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static DateTimeOffset Utc(int year, int month, int day) =>
        new(year, month, day, 0, 0, 0, TimeSpan.Zero);
}
