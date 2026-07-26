using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
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

public sealed class CatalogResearchWorkflowTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "trading-flow-research-workflow-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task MomentumWorkflow_PackagesAndRegistersByteIdenticalReplay()
    {
        var adjusted = Manifest(
            EvidenceDatasetKind.MarketBarsResearchAdjusted,
            "sip",
            "adjusted");
        var asTraded = Manifest(
            EvidenceDatasetKind.MarketBarsAsTraded,
            "sip",
            "as-traded");
        var universe = Manifest(
            EvidenceDatasetKind.UniverseMembership,
            "finviz",
            "universe");
        var bars = BuildBars();
        var adjustedRows = BuildMarketRows(bars, "all");
        var asTradedRows = BuildMarketRows(bars, "raw");
        var memberships = BuildMembershipRows(
            bars["SPY"].Select(bar =>
                DateOnly.FromDateTime(bar.Timestamp.UtcDateTime)),
            ["A", "B", "C", "D", "E"]);
        var request = Request(adjusted, asTraded, universe);
        var requestHoldoutStart = request.StudyPartitions.Holdout.StartUtc;

        EvidenceResearchRunManifest? registered = null;
        var catalog = new Mock<IEvidenceCatalog>(MockBehavior.Strict);
        catalog
            .Setup(value => value.GetDatasetAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) =>
                new[] { adjusted, asTraded, universe }
                    .SingleOrDefault(manifest =>
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
                "workflow-run",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => registered);
        var partitionReader = new Mock<IEvidencePartitionDataReader>(
            MockBehavior.Strict);
        partitionReader
            .Setup(value => value.ReadMarketBarsAsync(
                It.IsAny<EvidenceDatasetManifest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                EvidenceDatasetManifest manifest,
                CancellationToken _) =>
            {
                Assert.All(
                    manifest.Partitions,
                    partition => Assert.True(
                        partition.MaximumSourceTimestampUtc <
                        requestHoldoutStart));
                return manifest.Kind == EvidenceDatasetKind.MarketBarsResearchAdjusted
                    ? adjustedRows
                    : asTradedRows;
            });
        partitionReader
            .Setup(value => value.ReadUniverseMembershipAsync(
                It.Is<EvidenceDatasetManifest>(manifest =>
                    manifest.Kind == EvidenceDatasetKind.UniverseMembership),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                EvidenceDatasetManifest manifest,
                CancellationToken _) =>
            {
                Assert.All(
                    manifest.Partitions,
                    partition => Assert.True(
                        partition.MaximumSourceTimestampUtc <
                        requestHoldoutStart));
                return memberships;
            });

        var artifactStore = new FileSystemImmutableArtifactStore(
            new ImmutableArtifactStoreOptions(Path.Combine(root, "objects")));
        var workflow = new CatalogResearchWorkflow(
            catalog.Object,
            partitionReader.Object,
            new EvidenceResearchRunArtifactPackager(artifactStore));

        var first = await workflow.RunMomentumAsync(request);
        var replay = await workflow.RunMomentumAsync(request);

        Assert.False(first.AlreadyRegistered);
        Assert.True(replay.AlreadyRegistered);
        Assert.Equal(
            EvidenceCanonicalJson.SerializeToUtf8Bytes(first.Manifest),
            EvidenceCanonicalJson.SerializeToUtf8Bytes(replay.Manifest));
        Assert.False(first.Manifest.EvidenceReady);
        Assert.NotEmpty(first.Manifest.ReadinessFailures);
        Assert.Equal(
            CatalogResearchPhase.DevelopmentValidation,
            first.PhaseState.Phase);
        Assert.Equal(ResearchHoldoutState.Unopened, first.PhaseState.HoldoutState);
        Assert.False(first.PhaseState.HoldoutReserved);
        catalog.Verify(
            value => value.ReserveHoldoutAsync(
                It.IsAny<EvidenceHoldoutConsumption>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        catalog.VerifyAll();
        partitionReader.VerifyAll();
    }

    [Fact]
    public async Task MomentumWorkflow_RejectsPartitionDatesThatDifferFromFrozenRequest()
    {
        var catalog = new Mock<IEvidenceCatalog>(MockBehavior.Strict);
        var reader = new Mock<IEvidencePartitionDataReader>(MockBehavior.Strict);
        var artifactStore = new FileSystemImmutableArtifactStore(
            new ImmutableArtifactStoreOptions(Path.Combine(root, "objects")));
        var workflow = new CatalogResearchWorkflow(
            catalog.Object,
            reader.Object,
            new EvidenceResearchRunArtifactPackager(artifactStore));
        var adjusted = Manifest(
            EvidenceDatasetKind.MarketBarsResearchAdjusted,
            "sip",
            "adjusted-mismatch");
        var asTraded = Manifest(
            EvidenceDatasetKind.MarketBarsAsTraded,
            "sip",
            "as-traded-mismatch");
        var universe = Manifest(
            EvidenceDatasetKind.UniverseMembership,
            "finviz",
            "universe-mismatch");
        var request = Request(adjusted, asTraded, universe);
        request = request with
        {
            StudyPartitions = new EvidenceStudyPartitions(
                new EvidenceStudyWindow(
                    "development",
                    Utc(2026, 1, 1),
                    Utc(2026, 1, 15)),
                new EvidenceStudyWindow(
                    "validation",
                    Utc(2026, 1, 15),
                    Utc(2026, 1, 23)),
                new EvidenceStudyWindow(
                    "holdout",
                    Utc(2026, 1, 23),
                    Utc(2026, 2, 1)))
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            workflow.RunMomentumAsync(request));

        catalog.VerifyNoOtherCalls();
        reader.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CatalystWorkflow_RejectsGroundTruthFromDifferentNewsDataset()
    {
        var bars = Manifest(
            EvidenceDatasetKind.MarketBarsAsTraded,
            "sip",
            "catalyst-bars");
        var news = Manifest(
            EvidenceDatasetKind.NewsRevisions,
            "alpaca",
            "catalyst-news");
        var sentiment = Manifest(
            EvidenceDatasetKind.SentimentAssessments,
            "finbert",
            "catalyst-sentiment");
        var universe = Manifest(
            EvidenceDatasetKind.UniverseMembership,
            "finviz",
            "catalyst-universe");
        var groundTruth = Manifest(
            EvidenceDatasetKind.ClassifierGroundTruth,
            "human",
            "catalyst-ground-truth-mismatch",
            new Dictionary<string, string>
            {
                ["source_news_dataset_id"] = "different-news-dataset",
                ["nws11_ready"] = "false"
            });
        var sessions = Manifest(
            EvidenceDatasetKind.ExchangeSessions,
            "alpaca",
            "catalyst-sessions");
        var manifests = new[]
        {
            bars,
            news,
            sentiment,
            universe,
            groundTruth,
            sessions
        };
        var catalog = new Mock<IEvidenceCatalog>(MockBehavior.Strict);
        catalog
            .Setup(value => value.GetDatasetAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) =>
                manifests.SingleOrDefault(manifest =>
                    manifest.DatasetId.Equals(id, StringComparison.Ordinal)));
        var reader = new Mock<IEvidencePartitionDataReader>(MockBehavior.Strict);
        var artifactStore = new FileSystemImmutableArtifactStore(
            new ImmutableArtifactStoreOptions(Path.Combine(root, "objects-mismatch")));
        var workflow = new CatalogResearchWorkflow(
            catalog.Object,
            reader.Object,
            new EvidenceResearchRunArtifactPackager(artifactStore));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            workflow.RunCatalystAsync(CatalystRequest(
                bars,
                news,
                sentiment,
                universe,
                groundTruth,
                sessions)));

        Assert.Contains("exact catalyst news dataset", exception.Message);
        reader.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CatalystWorkflow_RegistersDiagnosticRunWhenGroundTruthIsNotReady()
    {
        var bars = Manifest(
            EvidenceDatasetKind.MarketBarsAsTraded,
            "sip",
            "diagnostic-bars");
        var news = Manifest(
            EvidenceDatasetKind.NewsRevisions,
            "alpaca",
            "diagnostic-news");
        var sentiment = Manifest(
            EvidenceDatasetKind.SentimentAssessments,
            "finbert",
            "diagnostic-sentiment");
        var universe = Manifest(
            EvidenceDatasetKind.UniverseMembership,
            "finviz",
            "diagnostic-universe");
        var groundTruth = Manifest(
            EvidenceDatasetKind.ClassifierGroundTruth,
            "human",
            "diagnostic-ground-truth",
            new Dictionary<string, string>
            {
                ["source_news_dataset_id"] = news.DatasetId,
                ["nws11_ready"] = "false",
                ["nws11_failure_00"] = "valid_labels_below_minimum:12/500"
            });
        var sessions = Manifest(
            EvidenceDatasetKind.ExchangeSessions,
            "alpaca-trading",
            "diagnostic-sessions");
        var manifests = new[]
        {
            bars,
            news,
            sentiment,
            universe,
            groundTruth,
            sessions
        };
        EvidenceResearchRunManifest? registered = null;
        var catalog = new Mock<IEvidenceCatalog>(MockBehavior.Strict);
        catalog
            .Setup(value => value.GetDatasetAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) =>
                manifests.SingleOrDefault(manifest =>
                    manifest.DatasetId.Equals(id, StringComparison.Ordinal)));
        catalog
            .Setup(value => value.RegisterResearchRunAsync(
                It.IsAny<EvidenceResearchRunManifest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                EvidenceResearchRunManifest manifest,
                CancellationToken _) =>
            {
                registered = manifest;
                return new EvidenceCatalogCommitResult(
                    manifest.ResearchRunId,
                    false);
            });
        catalog
            .Setup(value => value.GetResearchRunAsync(
                "catalyst-workflow-run",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => registered);
        var reader = new Mock<IEvidencePartitionDataReader>(MockBehavior.Strict);
        reader
            .Setup(value => value.ReadExchangeSessionsAsync(
                sessions,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([ExchangeSession()]);
        reader
            .Setup(value => value.ReadClassifierGroundTruthAsync(
                It.Is<EvidenceDatasetManifest>(manifest =>
                    manifest.Kind == EvidenceDatasetKind.ClassifierGroundTruth),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        reader
            .Setup(value => value.ReadMarketBarsAsync(
                It.Is<EvidenceDatasetManifest>(manifest =>
                    manifest.Kind == EvidenceDatasetKind.MarketBarsAsTraded),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        reader
            .Setup(value => value.ReadNewsRevisionsAsync(
                It.Is<EvidenceDatasetManifest>(manifest =>
                    manifest.Kind == EvidenceDatasetKind.NewsRevisions),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        reader
            .Setup(value => value.ReadSentimentAssessmentsAsync(
                It.Is<EvidenceDatasetManifest>(manifest =>
                    manifest.Kind == EvidenceDatasetKind.SentimentAssessments),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        reader
            .Setup(value => value.ReadUniverseMembershipAsync(
                It.Is<EvidenceDatasetManifest>(manifest =>
                    manifest.Kind == EvidenceDatasetKind.UniverseMembership),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var artifactStore = new FileSystemImmutableArtifactStore(
            new ImmutableArtifactStoreOptions(Path.Combine(root, "objects-diagnostic")));
        var workflow = new CatalogResearchWorkflow(
            catalog.Object,
            reader.Object,
            new EvidenceResearchRunArtifactPackager(artifactStore));

        var result = await workflow.RunCatalystAsync(CatalystRequest(
            bars,
            news,
            sentiment,
            universe,
            groundTruth,
            sessions));

        Assert.False(result.ClassifierGroundTruthReady);
        Assert.False(result.Manifest.EvidenceReady);
        Assert.Contains(
            result.Manifest.ReadinessFailures,
            failure => failure.Equals(
                "valid_labels_below_minimum:0/500",
                StringComparison.Ordinal));
        Assert.Contains(
            "historical_sip_nbbo_dataset_missing",
            result.Manifest.ReadinessFailures);
        Assert.Equal(
            CatalogResearchPhase.DevelopmentValidation,
            result.PhaseState.Phase);
        Assert.False(result.PhaseState.HoldoutConsumed);
        Assert.Empty(result.Study.Report.Observations);
        catalog.VerifyAll();
        reader.VerifyAll();
    }

    [Fact]
    public async Task MomentumHoldout_ConsumesFrozenTrialExactlyOnce()
    {
        var fixture = await CreateHoldoutFixtureAsync("single-use");

        var first = await fixture.Workflow.RunMomentumAsync(fixture.Request);
        var readsAfterFirst = fixture.ObservationReadCount;
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Workflow.RunMomentumAsync(fixture.Request));

        Assert.Equal(CatalogResearchPhase.Holdout, first.PhaseState.Phase);
        Assert.Equal(ResearchHoldoutState.Completed, first.PhaseState.HoldoutState);
        Assert.True(first.PhaseState.HoldoutReserved);
        Assert.True(first.PhaseState.HoldoutConsumed);
        Assert.Contains("already been consumed", exception.Message);
        Assert.Equal(1, fixture.HoldoutReservationCount);
        Assert.Equal(readsAfterFirst, fixture.ObservationReadCount);
        Assert.Equal(
            ResearchHoldoutState.Completed,
            await fixture.Registry.GetHoldoutStateAsync(
                fixture.Trial.ExperimentId));
    }

    [Fact]
    public async Task MomentumHoldout_RejectsConfigurationMutationBeforeObservationRead()
    {
        var fixture = await CreateHoldoutFixtureAsync("mutation");
        var mutated = fixture.Request with
        {
            CodeVersion = "git:mutated-after-freeze"
        };

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Workflow.RunMomentumAsync(mutated));

        Assert.Contains("configuration differs", exception.Message);
        Assert.Equal(0, fixture.HoldoutReservationCount);
        Assert.Equal(0, fixture.ObservationReadCount);
        Assert.Equal(
            ResearchHoldoutState.Unopened,
            await fixture.Registry.GetHoldoutStateAsync(
                fixture.Trial.ExperimentId));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private async Task<HoldoutFixture> CreateHoldoutFixtureAsync(string seed)
    {
        var adjusted = Manifest(
            EvidenceDatasetKind.MarketBarsResearchAdjusted,
            "sip",
            $"holdout-adjusted-{seed}");
        var asTraded = Manifest(
            EvidenceDatasetKind.MarketBarsAsTraded,
            "sip",
            $"holdout-as-traded-{seed}");
        var universe = Manifest(
            EvidenceDatasetKind.UniverseMembership,
            "finviz",
            $"holdout-universe-{seed}");
        var bars = BuildBars();
        var adjustedRows = BuildMarketRows(bars, "all");
        var asTradedRows = BuildMarketRows(bars, "raw");
        var memberships = BuildMembershipRows(
            bars["SPY"].Select(bar =>
                DateOnly.FromDateTime(bar.Timestamp.UtcDateTime)),
            ["A", "B", "C", "D", "E"]);
        var baseRequest = Request(adjusted, asTraded, universe) with
        {
            ResearchRunId = $"holdout-run-{seed}"
        };
        var registry = new SqliteResearchTrialRegistry(
            new ResearchTrialRegistryOptions(
                Path.Combine(root, $"trial-registry-{seed}.db"),
                ResearchTrialRegistryOpenMode.BootstrapNew));
        var trial = TrialFor(baseRequest, $"trial-{seed}");
        await registry.RegisterTrialAsync(trial);
        var request = baseRequest with
        {
            Phase = CatalogResearchPhase.Holdout,
            FrozenTrial = new CatalogFrozenTrialIdentity(
                trial.ExperimentId,
                EvidenceCanonicalJson.ComputeSha256(trial))
        };

        EvidenceResearchRunManifest? registered = null;
        var fixture = new HoldoutFixture(registry, trial, request);
        var catalog = new Mock<IEvidenceCatalog>(MockBehavior.Strict);
        catalog
            .Setup(value => value.GetDatasetAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) =>
                new[] { adjusted, asTraded, universe }
                    .SingleOrDefault(manifest =>
                        manifest.DatasetId.Equals(id, StringComparison.Ordinal)));
        catalog
            .Setup(value => value.ReserveHoldoutAsync(
                It.IsAny<EvidenceHoldoutConsumption>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                EvidenceHoldoutConsumption consumption,
                CancellationToken _) =>
            {
                fixture.HoldoutReservationCount++;
                return new EvidenceCatalogCommitResult(consumption.HoldoutId, false);
            });
        catalog
            .Setup(value => value.RegisterResearchRunAsync(
                It.IsAny<EvidenceResearchRunManifest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                EvidenceResearchRunManifest manifest,
                CancellationToken _) =>
            {
                registered = manifest;
                return new EvidenceCatalogCommitResult(
                    manifest.ResearchRunId,
                    false);
            });
        catalog
            .Setup(value => value.GetResearchRunAsync(
                request.ResearchRunId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => registered);

        var reader = new Mock<IEvidencePartitionDataReader>(MockBehavior.Strict);
        reader
            .Setup(value => value.ReadMarketBarsAsync(
                It.IsAny<EvidenceDatasetManifest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                EvidenceDatasetManifest manifest,
                CancellationToken _) =>
            {
                fixture.ObservationReadCount++;
                return manifest.Kind == EvidenceDatasetKind.MarketBarsResearchAdjusted
                    ? adjustedRows
                    : asTradedRows;
            });
        reader
            .Setup(value => value.ReadUniverseMembershipAsync(
                It.IsAny<EvidenceDatasetManifest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                EvidenceDatasetManifest _,
                CancellationToken _) =>
            {
                fixture.ObservationReadCount++;
                return memberships;
            });

        var artifactStore = new FileSystemImmutableArtifactStore(
            new ImmutableArtifactStoreOptions(
                Path.Combine(root, $"holdout-objects-{seed}")));
        fixture.Workflow = new CatalogResearchWorkflow(
            catalog.Object,
            reader.Object,
            new EvidenceResearchRunArtifactPackager(artifactStore),
            registry);
        return fixture;
    }

    private static ResearchTrialDefinition TrialFor(
        CatalogMomentumWorkflowRequest request,
        string experimentId) =>
        new(
            experimentId,
            "momentum-holdout-discipline",
            null,
            1,
            "Frozen cross-sectional momentum candidate.",
            [
                request.Study.ResearchAdjustedBarsDatasetId,
                request.Study.AsTradedBarsDatasetId,
                request.Study.UniverseMembershipDatasetId
            ],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["decision"] = "completed_daily_bar"
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [CatalogResearchTrialBinding.RequestBindingFormulaKey] =
                    CatalogResearchTrialBinding.ComputeMomentumRequestSha256(request)
            },
            new Dictionary<string, decimal>(StringComparer.Ordinal)
            {
                ["primary_selection_fraction"] =
                    request.Study.Definition.Options.PrimarySelectionFraction
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["commission_bps"] =
                    request.Assumptions.Cost.CommissionBps.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)
            },
            [
                new ResearchPartitionDefinition(
                    request.StudyPartitions.Development.Name,
                    request.StudyPartitions.Development.StartUtc,
                    request.StudyPartitions.Development.EndUtc,
                    TimeSpan.Zero),
                new ResearchPartitionDefinition(
                    request.StudyPartitions.Validation.Name,
                    request.StudyPartitions.Validation.StartUtc,
                    request.StudyPartitions.Validation.EndUtc,
                    TimeSpan.Zero),
                new ResearchPartitionDefinition(
                    request.StudyPartitions.Holdout.Name,
                    request.StudyPartitions.Holdout.StartUtc,
                    request.StudyPartitions.Holdout.EndUtc,
                    TimeSpan.Zero)
            ],
            "net_return_pct",
            ["non_positive_holdout_return"],
            ResearchHoldoutState.Unopened,
            Now.AddMinutes(-5));

    private sealed class HoldoutFixture(
        SqliteResearchTrialRegistry registry,
        ResearchTrialDefinition trial,
        CatalogMomentumWorkflowRequest request)
    {
        public SqliteResearchTrialRegistry Registry { get; } = registry;

        public ResearchTrialDefinition Trial { get; } = trial;

        public CatalogMomentumWorkflowRequest Request { get; } = request;

        public CatalogResearchWorkflow Workflow { get; set; } = null!;

        public int HoldoutReservationCount { get; set; }

        public int ObservationReadCount { get; set; }
    }

    private static CatalogMomentumWorkflowRequest Request(
        EvidenceDatasetManifest adjusted,
        EvidenceDatasetManifest asTraded,
        EvidenceDatasetManifest universe)
    {
        var validationStart = new DateOnly(2026, 1, 15);
        var holdoutStart = new DateOnly(2026, 1, 22);
        var definition = new CrossSectionalMomentumStudyDefinition(
            "catalog-momentum-workflow-v1",
            "SPY",
            "point-in-time test universe",
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
                PrimarySelectionFraction: 0.25m,
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
        return new CatalogMomentumWorkflowRequest(
            "workflow-run",
            Now,
            "git:test-commit",
            new CatalogMomentumStudyRequest(
                adjusted.DatasetId,
                asTraded.DatasetId,
                universe.DatasetId,
                definition),
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

    private static CatalogCatalystWorkflowRequest CatalystRequest(
        EvidenceDatasetManifest bars,
        EvidenceDatasetManifest news,
        EvidenceDatasetManifest sentiment,
        EvidenceDatasetManifest universe,
        EvidenceDatasetManifest groundTruth,
        EvidenceDatasetManifest sessions) =>
        new(
            "catalyst-workflow-run",
            Now,
            "git:test-commit",
            new CatalogCatalystStudyRequest(
                bars.DatasetId,
                news.DatasetId,
                sentiment.DatasetId,
                universe.DatasetId,
                null,
                Utc(2026, 1, 2),
                Utc(2026, 1, 3),
                "5m"),
            groundTruth.DatasetId,
            sessions.DatasetId,
            new EvidenceStudyPartitions(
                new EvidenceStudyWindow(
                    "development",
                    Utc(2026, 1, 2),
                    Utc(2026, 1, 2).AddHours(8)),
                new EvidenceStudyWindow(
                    "validation",
                    Utc(2026, 1, 2).AddHours(8),
                    Utc(2026, 1, 2).AddHours(16)),
                new EvidenceStudyWindow(
                    "holdout",
                    Utc(2026, 1, 2).AddHours(16),
                    Utc(2026, 1, 3))),
            new CatalogResearchAssumptionSet(
                new CatalogCostAssumptions(1m, 0.2m),
                new CatalogSpreadAssumptions("conservative_fixed", 4m),
                new CatalogSlippageAssumptions("volume_participation", 10m),
                new CatalogBorrowAssumptions(true, 300m),
                new CatalogBenchmarkAssumptions("SPY", "total_return")));

    private static ExchangeSessionEvidenceRow ExchangeSession() =>
        new(
            1,
            "session-run",
            Hash("session-config"),
            "code-v1",
            "alpaca",
            "alpaca",
            "XNYS",
            new DateOnly(2026, 1, 2),
            new DateTimeOffset(2026, 1, 2, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 2, 14, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 2, 21, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 3, 1, 0, 0, TimeSpan.Zero),
            false,
            "test-calendar",
            Utc(2026, 1, 1),
            [
                new EvidenceRowSourceAddress(
                    "session-observation",
                    Hash("session-source"))
            ]);

    private static IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> BuildBars() =>
        new Dictionary<string, IReadOnlyList<OhlcvBar>>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["SPY"] = Bars("SPY", index => 100m + index),
            ["A"] = Bars("A", index => 30m + index),
            ["B"] = Bars("B", index => 40m + index),
            ["C"] = Bars("C", index => 50m + index),
            ["D"] = Bars("D", index => 60m + index),
            ["E"] = Bars("E", index => 70m + index)
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
                "workflow-source-run",
                Hash("workflow-config"),
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
                        $"observation-{adjustment}-{entry.Key}",
                        Hash($"source-{adjustment}-{entry.Key}"))
                ]);
        })).ToArray();

    private static IReadOnlyList<UniverseMembershipEvidenceRow> BuildMembershipRows(
        IEnumerable<DateOnly> dates,
        IReadOnlyList<string> symbols)
    {
        var rows = new List<UniverseMembershipEvidenceRow>();
        foreach (var date in dates.Distinct())
        {
            var observedAt = new DateTimeOffset(
                date.ToDateTime(new TimeOnly(12, 0)),
                TimeSpan.Zero);
            var snapshotHash = Hash($"snapshot-{date:O}");
            for (var index = 0; index < symbols.Count; index++)
            {
                var symbol = symbols[index];
                rows.Add(new UniverseMembershipEvidenceRow(
                    1,
                    "workflow-source-run",
                    Hash("workflow-config"),
                    "code-v1",
                    "finviz",
                    "finviz",
                    "workflow-universe",
                    $"snapshot-{date:O}",
                    "test",
                    snapshotHash,
                    date,
                    $"security-{symbol}",
                    $"issuer-{symbol}",
                    symbol,
                    observedAt,
                    true,
                    index + 1,
                    "eligible",
                    observedAt,
                    observedAt,
                    [
                        new EvidenceRowSourceAddress(
                            $"observation-universe-{date:O}",
                            snapshotHash)
                    ]));
            }
        }

        return rows;
    }

    private static EvidenceDatasetManifest Manifest(
        EvidenceDatasetKind kind,
        string feed,
        string seed,
        IReadOnlyDictionary<string, string>? attributes = null)
    {
        var sourceArtifact = Artifact(
            $"raw-{seed}",
            "raw/test",
            "application/json");
        (DateTimeOffset StartUtc, DateTimeOffset EndUtc)[] windows =
            kind == EvidenceDatasetKind.ExchangeSessions
            ?
            [
                (Utc(2026, 1, 2), Utc(2026, 2, 1))
            ]
            :
            [
                (Utc(2026, 1, 2), Utc(2026, 1, 2).AddHours(8)),
                (Utc(2026, 1, 2).AddHours(8), Utc(2026, 1, 2).AddHours(16)),
                (Utc(2026, 1, 2).AddHours(16), Utc(2026, 1, 3)),
                (Utc(2026, 1, 3), Utc(2026, 1, 15)),
                (Utc(2026, 1, 15), Utc(2026, 1, 22)),
                (Utc(2026, 1, 22), Utc(2026, 2, 1))
            ];
        var partitions = windows
            .Select((window, index) =>
                new EvidenceDatasetPartitionManifest(
                    $"partition-{seed}-{index:D2}",
                    kind,
                    1,
                    new EvidencePartitionProvenance(
                        kind == EvidenceDatasetKind.ExchangeSessions
                            ? "alpaca"
                            : "test",
                        kind == EvidenceDatasetKind.ExchangeSessions
                            ? "/v2/calendar"
                            : "/test",
                        feed,
                        kind == EvidenceDatasetKind.MarketBarsResearchAdjusted
                            ? "all"
                            : kind == EvidenceDatasetKind.MarketBarsAsTraded
                                ? "raw"
                                : kind == EvidenceDatasetKind.ExchangeSessions
                                    ? "raw"
                                    : "none",
                        "USD",
                        new DateOnly(2026, 1, 2),
                        ["security-a"],
                        kind == EvidenceDatasetKind.UniverseMembership
                            ? ["issuer-a"]
                            : [],
                        kind == EvidenceDatasetKind.ExchangeSessions
                            ? ["US_EQUITIES"]
                            : ["A"],
                        kind == EvidenceDatasetKind.ExchangeSessions
                            ? "session"
                            : "1d",
                        window.Item1,
                        window.Item2),
                    window.Item1,
                    window.Item2.AddTicks(-1),
                    1,
                    Artifact(
                        $"partition-{seed}-{index:D2}",
                        "normalized/test",
                        "application/vnd.apache.parquet"),
                    [
                        new EvidenceSourceReference(
                            $"observation-{seed}-{index:D2}",
                            sourceArtifact,
                            Now.AddMinutes(-1))
                    ],
                    "normalizer-v1",
                    "code-v1",
                    new EvidenceQualityReport(),
                    dimensions: kind == EvidenceDatasetKind.ExchangeSessions
                        ? new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["calendar_coverage_start_date"] = "2026-01-02",
                            ["calendar_coverage_end_date_exclusive"] = "2026-02-01",
                            ["calendar_source"] = "alpaca_official_market_calendar",
                            ["calendar_source_complete"] = "true",
                            ["authoritative_page_count"] = "1"
                        }
                        : null))
            .ToArray();
        return new EvidenceDatasetManifest(
            kind,
            1,
            Now,
            $"job-{seed}",
            Hash($"plan-{seed}"),
            Hash($"config-{seed}"),
            "code-v1",
            "normalizer-v1",
            feed,
            partitions,
            new EvidenceQualityReport(),
            attributes ??
                (kind is EvidenceDatasetKind.MarketBarsAsTraded or
                    EvidenceDatasetKind.MarketBarsResearchAdjusted
                    ? new Dictionary<string, string>
                    {
                        ["adjustment"] = kind == EvidenceDatasetKind.MarketBarsResearchAdjusted
                            ? "all"
                            : "raw"
                    }
                    : null));
    }

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

    private static DateTimeOffset Utc(
        int year,
        int month,
        int day) =>
        new(year, month, day, 0, 0, 0, TimeSpan.Zero);
}
