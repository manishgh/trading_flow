using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TradingFlow.Data.Evidence;
using TradingFlow.Data.Evidence.Parquet;
using TradingFlow.Data.Evidence.Research;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Research;
using TradingFlow.Engine.Storage;
using TradingFlow.Research;
using TradingFlow.Research.Momentum;
using TradingFlow.Research.Workflows;

namespace TradingFlow.Tests;

/// <summary>
/// Exercises the persisted, provider-free research path with synthetic fixture rows.
/// The fixture mimics evidence contracts; it makes no claim about real market performance.
/// </summary>
public sealed class ResearchPipelineEndToEndTests : IAsyncLifetime
{
    private const string CodeVersion = "test-fixture-commit";
    private const string NormalizerVersion = "test-normalizer-v1";
    private const string ResearchRunId = "research-pipeline-e2e-momentum-v1";
    private static readonly DateTimeOffset PlanCreatedAtUtc =
        new(2025, 12, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset DatasetCreatedAtUtc =
        new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] MarketSymbols = ["A", "B", "C", "D", "E", "SPY"];
    private static readonly string[] UniverseSymbols = ["A", "B", "C", "D", "E"];

    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "trading-flow-research-pipeline-e2e",
        Guid.NewGuid().ToString("N"));
    private string artifactRoot = null!;
    private string catalogPath = null!;
    private FileSystemImmutableArtifactStore artifactStore = null!;
    private SqliteEvidenceCatalog catalog = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        artifactRoot = Path.Combine(root, "objects");
        catalogPath = Path.Combine(root, "evidence.db");
        artifactStore = new FileSystemImmutableArtifactStore(
            new ImmutableArtifactStoreOptions(artifactRoot));
        catalog = new SqliteEvidenceCatalog(
            new EvidenceCatalogOptions(
                catalogPath,
                EvidenceCatalogOpenMode.BootstrapNew),
            artifactStore);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task FrozenEvidence_ToCatalogMomentumManifest_ReplaysByteIdenticallyAfterRestart()
    {
        var frozen = await PublishAndCommitFrozenEvidenceAsync();
        var workflow = CreateWorkflow(catalog);
        var request = BuildWorkflowRequest(frozen);

        var first = await workflow.RunMomentumAsync(request);
        var replay = await workflow.RunMomentumAsync(request);

        Assert.False(first.AlreadyRegistered);
        Assert.True(replay.AlreadyRegistered);
        Assert.Equal(3, first.Manifest.InputDatasets.Count);
        Assert.Equal(
            frozen.Manifests
                .Select(manifest => manifest.DatasetId)
                .OrderBy(value => value, StringComparer.Ordinal),
            first.Manifest.InputDatasets
                .Select(dataset => dataset.DatasetId)
                .OrderBy(value => value, StringComparer.Ordinal));
        AssertCanonicalBytesEqual(first.Manifest, replay.Manifest);

        var datasetReplay = await ((IAtomicEvidenceDatasetCatalog)catalog)
            .CommitDatasetsAtomicallyAsync(frozen.Manifests);
        Assert.All(datasetReplay, commit => Assert.True(commit.AlreadyCommitted));

        var stored = await catalog.GetResearchRunAsync(ResearchRunId);
        Assert.NotNull(stored);
        AssertCanonicalBytesEqual(first.Manifest, stored!);

        var manifestArtifact = ResearchManifestArtifact(first.Manifest);
        var manifestVerification = await artifactStore.VerifyAsync(manifestArtifact);
        Assert.True(
            manifestVerification.IsValid,
            manifestVerification.FailureReason);
        await using (var stream = await artifactStore.OpenReadAsync(manifestArtifact))
        {
            var persistedBytes = await ReadAllBytesAsync(stream);
            Assert.True(
                persistedBytes.AsSpan().SequenceEqual(
                    EvidenceCanonicalJson.SerializeToUtf8Bytes(first.Manifest)));
        }

        foreach (var artifact in ResearchArtifacts(first.Manifest))
        {
            var verification = await artifactStore.VerifyAsync(artifact);
            Assert.True(
                verification.IsValid,
                $"{artifact.ObjectNamespace}/{artifact.Content.Sha256}: " +
                verification.FailureReason);
        }

        AssertProviderFreeWorkflowConstructor();

        SqliteConnection.ClearAllPools();
        var reopenedCatalog = new SqliteEvidenceCatalog(
            new EvidenceCatalogOptions(
                catalogPath,
                EvidenceCatalogOpenMode.OpenExisting),
            artifactStore);
        var reopenedManifest = await reopenedCatalog.GetResearchRunAsync(ResearchRunId);
        Assert.NotNull(reopenedManifest);
        AssertCanonicalBytesEqual(first.Manifest, reopenedManifest!);

        var restartedReplay = await CreateWorkflow(reopenedCatalog)
            .RunMomentumAsync(request);
        Assert.True(restartedReplay.AlreadyRegistered);
        AssertCanonicalBytesEqual(first.Manifest, restartedReplay.Manifest);
    }

    [Fact]
    public async Task TamperedParquetArtifact_FailsClosedWithoutRegisteringResearchRun()
    {
        var frozen = await PublishAndCommitFrozenEvidenceAsync();
        var adjustedArtifact = frozen.AdjustedBars.Partitions[0].Artifact;
        var contentPath = ArtifactContentPath(adjustedArtifact);

        await File.WriteAllBytesAsync(
            contentPath,
            Encoding.UTF8.GetBytes("synthetic fixture tamper"));

        var verification = await artifactStore.VerifyAsync(adjustedArtifact);
        Assert.False(verification.IsValid);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateWorkflow(catalog).RunMomentumAsync(BuildWorkflowRequest(frozen)));

        Assert.Contains(
            "artifact",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Null(await catalog.GetResearchRunAsync(ResearchRunId));
    }

    private CatalogResearchWorkflow CreateWorkflow(IEvidenceCatalog evidenceCatalog) =>
        new(
            evidenceCatalog,
            new ParquetEvidencePartitionDataReader(
                new EvidenceParquetCodec(),
                artifactStore),
            new EvidenceResearchRunArtifactPackager(artifactStore));

    private async Task<FrozenEvidence> PublishAndCommitFrozenEvidenceAsync()
    {
        var sessionDates = TradingDates(
            new DateOnly(2026, 1, 2),
            count: 30);
        var rangeStartUtc = AtUtc(sessionDates[0]);
        var rangeEndUtc = AtUtc(sessionDates[^1].AddDays(1));
        var requests = new List<EvidenceCollectionRequest>
        {
            new(
                "adjusted-bars",
                "synthetic-fixture",
                "/synthetic/adjusted-bars",
                MarketSymbols,
                rangeStartUtc,
                rangeEndUtc,
                "sip",
                "all",
                "USD",
                sessionDates[^1]),
            new(
                "as-traded-bars",
                "synthetic-fixture",
                "/synthetic/as-traded-bars",
                MarketSymbols,
                rangeStartUtc,
                rangeEndUtc,
                "sip",
                "raw",
                "USD",
                sessionDates[^1])
        };
        requests.AddRange(sessionDates.Select(date =>
            new EvidenceCollectionRequest(
                UniverseRequestId(date),
                "synthetic-fixture",
                "/synthetic/universe",
                UniverseSymbols,
                AtUtc(date),
                AtUtc(date.AddDays(1)),
                "finviz",
                "none",
                "USD",
                date)));

        var plan = new EvidenceCollectionPlan(
            PlanCreatedAtUtc,
            "synthetic-research-pipeline-fixture",
            Hash("synthetic-research-pipeline-config"),
            CodeVersion,
            "fixture-collection-v1",
            "fixture-daily-partition-v1",
            requests);
        await catalog.RegisterCollectionPlanAsync(plan);

        var observations = new Dictionary<string, EvidenceSourceObservation>(
            StringComparer.Ordinal);
        foreach (var request in plan.Requests)
        {
            var rawBytes = EvidenceCanonicalJson.SerializeToUtf8Bytes(new
            {
                fixture = true,
                request.RequestId,
                request.Symbols,
                request.AsOfDate
            });
            var rawArtifact = Artifact(
                rawBytes,
                "raw/research-pipeline-test",
                "application/json");
            await artifactStore.PutIfAbsentAsync(
                new ImmutableArtifactWriteRequest(rawArtifact),
                rawBytes);
            var receivedAtUtc = request.AsOfDate is { } date &&
                                request.RequestId.StartsWith(
                                    "universe-",
                                    StringComparison.Ordinal)
                ? AtUtc(date).AddMinutes(2)
                : DatasetCreatedAtUtc.AddMinutes(-30);
            var observation = new EvidenceSourceObservation(
                $"observation-{request.RequestId}",
                plan.JobId,
                plan.LogicalPlanHash,
                plan.CollectionAttemptHash,
                request.RequestId,
                "page-1",
                request.Provider,
                request.Endpoint,
                EvidenceTransportKind.FileImport,
                null,
                request.Symbols,
                request.RequestedStartUtc,
                request.RequestedEndUtc,
                request.DataFeed,
                request.Adjustment,
                request.Currency,
                request.AsOfDate,
                null,
                null,
                null,
                receivedAtUtc,
                rawArtifact,
                plan.RunId,
                plan.ConfigHash,
                plan.CodeVersion);
            await catalog.RegisterSourceObservationAsync(observation);
            observations.Add(request.RequestId, observation);
        }

        var publisher = new EvidenceParquetPartitionPublisher(
            new EvidenceParquetCodec(),
            artifactStore);
        var adjustedObservation = observations["adjusted-bars"];
        var asTradedObservation = observations["as-traded-bars"];
        var adjustedRows = BuildBarRows(
            plan,
            sessionDates,
            "all",
            adjustedObservation);
        var asTradedRows = BuildBarRows(
            plan,
            sessionDates,
            "raw",
            asTradedObservation);
        var holdoutStartDate = sessionDates[21];
        var holdoutStartUtc = AtUtc(holdoutStartDate);
        var adjustedPartitions = new[]
        {
            await publisher.PublishResearchAdjustedMarketBarsAsync(
                MarketPartitionRequest(
                    plan,
                    adjustedObservation,
                    EvidenceDatasetKind.MarketBarsResearchAdjusted,
                    "all",
                    "market-bars-all-development-validation",
                    rangeStartUtc,
                    holdoutStartUtc),
                adjustedRows.Where(row => row.BarEndUtc < holdoutStartUtc).ToArray()),
            await publisher.PublishResearchAdjustedMarketBarsAsync(
                MarketPartitionRequest(
                    plan,
                    adjustedObservation,
                    EvidenceDatasetKind.MarketBarsResearchAdjusted,
                    "all",
                    "market-bars-all-holdout",
                    holdoutStartUtc,
                    rangeEndUtc),
                adjustedRows.Where(row => row.BarEndUtc >= holdoutStartUtc).ToArray())
        };
        var asTradedPartitions = new[]
        {
            await publisher.PublishMarketBarsAsTradedAsync(
                MarketPartitionRequest(
                    plan,
                    asTradedObservation,
                    EvidenceDatasetKind.MarketBarsAsTraded,
                    "raw",
                    "market-bars-raw-development-validation",
                    rangeStartUtc,
                    holdoutStartUtc),
                asTradedRows.Where(row => row.BarEndUtc < holdoutStartUtc).ToArray()),
            await publisher.PublishMarketBarsAsTradedAsync(
                MarketPartitionRequest(
                    plan,
                    asTradedObservation,
                    EvidenceDatasetKind.MarketBarsAsTraded,
                    "raw",
                    "market-bars-raw-holdout",
                    holdoutStartUtc,
                    rangeEndUtc),
                asTradedRows.Where(row => row.BarEndUtc >= holdoutStartUtc).ToArray())
        };

        var universePartitions = new List<EvidenceDatasetPartitionManifest>(
            sessionDates.Count);
        foreach (var date in sessionDates)
        {
            var observation = observations[UniverseRequestId(date)];
            var rows = BuildUniverseRows(plan, date, observation);
            universePartitions.Add(
                await publisher.PublishUniverseMembershipAsync(
                    UniversePartitionRequest(plan, date, observation),
                    rows));
        }

        var adjustedManifest = BuildManifest(
            plan,
            EvidenceDatasetKind.MarketBarsResearchAdjusted,
            "sip",
            adjustedPartitions);
        var asTradedManifest = BuildManifest(
            plan,
            EvidenceDatasetKind.MarketBarsAsTraded,
            "sip",
            asTradedPartitions);
        var universeManifest = BuildManifest(
            plan,
            EvidenceDatasetKind.UniverseMembership,
            "finviz",
            universePartitions);
        EvidenceDatasetManifest[] manifests =
        [
            adjustedManifest,
            asTradedManifest,
            universeManifest
        ];

        var commits = await ((IAtomicEvidenceDatasetCatalog)catalog)
            .CommitDatasetsAtomicallyAsync(manifests);
        Assert.All(commits, commit => Assert.False(commit.AlreadyCommitted));
        foreach (var manifest in manifests)
        {
            var committed = await catalog.GetDatasetAsync(manifest.DatasetId);
            Assert.NotNull(committed);
            AssertCanonicalBytesEqual(manifest, committed!);
        }

        return new FrozenEvidence(
            sessionDates,
            adjustedManifest,
            asTradedManifest,
            universeManifest,
            manifests);
    }

    private static EvidenceParquetPartitionRequest MarketPartitionRequest(
        EvidenceCollectionPlan plan,
        EvidenceSourceObservation observation,
        EvidenceDatasetKind kind,
        string adjustment,
        string partitionId,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc) =>
        new(
            partitionId,
            kind,
            1,
            new EvidencePartitionProvenance(
                observation.Provider,
                observation.Endpoint,
                "sip",
                adjustment,
                "USD",
                DateOnly.FromDateTime(rangeEndUtc.AddDays(-1).UtcDateTime),
                MarketSymbols.Select(SecurityId).ToArray(),
                MarketSymbols.Select(IssuerId).ToArray(),
                MarketSymbols,
                "1d",
                rangeStartUtc,
                rangeEndUtc),
            [observation.ToReference()],
            plan.RunId,
            plan.ConfigHash,
            NormalizerVersion,
            plan.CodeVersion,
            new EvidenceQualityReport(),
            new EvidenceObjectNamespace("normalized/research-pipeline-test"));

    private static EvidenceParquetPartitionRequest UniversePartitionRequest(
        EvidenceCollectionPlan plan,
        DateOnly date,
        EvidenceSourceObservation observation) =>
        new(
            $"universe-{date:yyyyMMdd}",
            EvidenceDatasetKind.UniverseMembership,
            1,
            new EvidencePartitionProvenance(
                observation.Provider,
                observation.Endpoint,
                "finviz",
                "none",
                "USD",
                date,
                UniverseSymbols.Select(SecurityId).ToArray(),
                UniverseSymbols.Select(IssuerId).ToArray(),
                UniverseSymbols,
                "1d",
                AtUtc(date),
                AtUtc(date.AddDays(1))),
            [observation.ToReference()],
            plan.RunId,
            plan.ConfigHash,
            NormalizerVersion,
            plan.CodeVersion,
            new EvidenceQualityReport(),
            new EvidenceObjectNamespace("normalized/research-pipeline-test"));

    private static IReadOnlyList<MarketBarEvidenceRow> BuildBarRows(
        EvidenceCollectionPlan plan,
        IReadOnlyList<DateOnly> dates,
        string adjustment,
        EvidenceSourceObservation observation) =>
        MarketSymbols
            .SelectMany((symbol, symbolIndex) =>
                dates.Select((date, dateIndex) =>
                {
                    var close = 50m +
                                (symbolIndex * 10m) +
                                (dateIndex * (0.35m + symbolIndex * 0.08m));
                    var barStartUtc = AtUtc(date).AddHours(5);
                    var barEndUtc = AtUtc(date).AddHours(21);
                    return new MarketBarEvidenceRow(
                        1,
                        plan.RunId,
                        plan.ConfigHash,
                        plan.CodeVersion,
                        "sip",
                        SecurityId(symbol),
                        symbol,
                        barStartUtc,
                        barEndUtc,
                        "1d",
                        EvidenceFixedDecimal.ToPriceUnits(close - 0.20m),
                        EvidenceFixedDecimal.ToPriceUnits(close + 0.60m),
                        EvidenceFixedDecimal.ToPriceUnits(close - 0.60m),
                        EvidenceFixedDecimal.ToPriceUnits(close),
                        null,
                        1_000_000 + symbolIndex * 100_000 + dateIndex * 1_000,
                        null,
                        adjustment,
                        "USD",
                        barEndUtc,
                        observation.ReceivedAtUtc,
                        [
                            new EvidenceRowSourceAddress(
                                observation.ObservationId,
                                observation.Artifact.Content.Sha256)
                        ]);
                }))
            .ToArray();

    private static IReadOnlyList<UniverseMembershipEvidenceRow> BuildUniverseRows(
        EvidenceCollectionPlan plan,
        DateOnly date,
        EvidenceSourceObservation observation)
    {
        var providerTimestampUtc = AtUtc(date).AddMinutes(1);
        var knownAtUtc = AtUtc(date).AddMinutes(2);
        return UniverseSymbols
            .Select((symbol, index) =>
                new UniverseMembershipEvidenceRow(
                    1,
                    plan.RunId,
                    plan.ConfigHash,
                    plan.CodeVersion,
                    "finviz",
                    observation.Provider,
                    "synthetic-point-in-time-universe",
                    $"snapshot-{date:yyyyMMdd}",
                    "synthetic-fixture=true",
                    observation.Artifact.Content.Sha256,
                    date,
                    SecurityId(symbol),
                    IssuerId(symbol),
                    symbol,
                    knownAtUtc,
                    true,
                    index + 1,
                    "synthetic_test_fixture_member",
                    providerTimestampUtc,
                    knownAtUtc,
                    [
                        new EvidenceRowSourceAddress(
                            observation.ObservationId,
                            observation.Artifact.Content.Sha256)
                    ]))
            .ToArray();
    }

    private static EvidenceDatasetManifest BuildManifest(
        EvidenceCollectionPlan plan,
        EvidenceDatasetKind kind,
        string dataFeed,
        IReadOnlyList<EvidenceDatasetPartitionManifest> partitions) =>
        EvidenceParquetPartitionPublisher.BuildDatasetManifest(
            kind,
            1,
            DatasetCreatedAtUtc,
            plan.JobId,
            plan.LogicalPlanHash,
            plan.ConfigHash,
            plan.CodeVersion,
            NormalizerVersion,
            dataFeed,
            partitions,
            new EvidenceQualityReport());

    private static CatalogMomentumWorkflowRequest BuildWorkflowRequest(
        FrozenEvidence frozen)
    {
        var validationStart = frozen.SessionDates[14];
        var holdoutStart = frozen.SessionDates[21];
        var end = frozen.SessionDates[^1].AddDays(1);
        var definition = new CrossSectionalMomentumStudyDefinition(
            "synthetic-catalog-momentum-e2e-v1",
            "SPY",
            "synthetic point-in-time integration fixture",
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
                PrimarySelectionFraction: 0.40m,
                MinimumCandidatesPerDate: 4,
                DevelopmentFraction: 0.50m,
                ValidationFraction: 0.25m,
                HoldoutFraction: 0.25m,
                MinimumFormationDates: 100,
                MinimumHoldoutFormationDates: 100,
                MinimumDistinctSelectedTickers: 100,
                Cells: [MomentumResearchCell.MomentumOnly],
                ExchangeTimezone: "America/New_York")
            {
                ValidationStartDate = validationStart,
                HoldoutStartDate = holdoutStart
            });

        return new CatalogMomentumWorkflowRequest(
            ResearchRunId,
            DatasetCreatedAtUtc.AddHours(1),
            CodeVersion,
            new CatalogMomentumStudyRequest(
                frozen.AdjustedBars.DatasetId,
                frozen.AsTradedBars.DatasetId,
                frozen.Universe.DatasetId,
                definition),
            new EvidenceStudyPartitions(
                new EvidenceStudyWindow(
                    "development",
                    AtUtc(frozen.SessionDates[0]),
                    AtUtc(validationStart)),
                new EvidenceStudyWindow(
                    "validation",
                    AtUtc(validationStart),
                    AtUtc(holdoutStart)),
                new EvidenceStudyWindow(
                    "holdout",
                    AtUtc(holdoutStart),
                    AtUtc(end))),
            new CatalogResearchAssumptionSet(
                new CatalogCostAssumptions(1m, 0.2m),
                new CatalogSpreadAssumptions(
                    "synthetic_fixture_no_execution_claim",
                    4m),
                new CatalogSlippageAssumptions(
                    "synthetic_fixture_volume_participation",
                    10m),
                new CatalogBorrowAssumptions(true, 300m),
                new CatalogBenchmarkAssumptions("SPY", "total_return")));
    }

    private static void AssertProviderFreeWorkflowConstructor()
    {
        var constructor = Assert.Single(
            typeof(CatalogResearchWorkflow).GetConstructors());
        Assert.Equal(
            [
                typeof(IEvidenceCatalog),
                typeof(IEvidencePartitionDataReader),
                typeof(EvidenceResearchRunArtifactPackager),
                typeof(TradingFlow.Data.Evidence.Governance.SqliteResearchTrialRegistry)
            ],
            constructor
                .GetParameters()
                .Select(parameter => parameter.ParameterType)
                .ToArray());
    }

    private static IReadOnlyList<EvidenceArtifactReference> ResearchArtifacts(
        EvidenceResearchRunManifest manifest) =>
    [
        manifest.UniverseLedgerArtifact,
        manifest.StudyConfigArtifact,
        manifest.Assumptions.CostModel,
        manifest.Assumptions.SpreadModel,
        manifest.Assumptions.SlippageModel,
        manifest.Assumptions.BorrowModel,
        manifest.Assumptions.BenchmarkDefinition,
        .. manifest.Holdout is null
            ? Array.Empty<EvidenceArtifactReference>()
            : [manifest.Holdout.PartitionDefinitionArtifact],
        .. manifest.Outputs
    ];

    private static EvidenceArtifactReference ResearchManifestArtifact(
        EvidenceResearchRunManifest manifest)
    {
        var bytes = EvidenceCanonicalJson.SerializeToUtf8Bytes(manifest);
        return Artifact(
            bytes,
            EvidenceObjectNamespaces.ResearchRunManifests.Value,
            "application/vnd.tradingflow.evidence-research-run+json");
    }

    private string ArtifactContentPath(EvidenceArtifactReference artifact) =>
        Path.Combine(
            artifactRoot,
            artifact.ObjectNamespace.Value.Replace(
                '/',
                Path.DirectorySeparatorChar),
            artifact.Content.Sha256[..2],
            artifact.Content.Sha256,
            "content.bin");

    private static EvidenceArtifactReference Artifact(
        ReadOnlySpan<byte> bytes,
        string objectNamespace,
        string mediaType) =>
        new(
            new EvidenceContentAddress(
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                bytes.Length,
                mediaType),
            new EvidenceObjectNamespace(objectNamespace));

    private static IReadOnlyList<DateOnly> TradingDates(
        DateOnly start,
        int count)
    {
        var dates = new List<DateOnly>(count);
        for (var date = start; dates.Count < count; date = date.AddDays(1))
        {
            if (date.DayOfWeek is not DayOfWeek.Saturday and
                not DayOfWeek.Sunday)
            {
                dates.Add(date);
            }
        }

        return dates;
    }

    private static DateTimeOffset AtUtc(DateOnly date) =>
        new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    private static string UniverseRequestId(DateOnly date) =>
        $"universe-{date:yyyyMMdd}";

    private static string SecurityId(string symbol) =>
        $"security-{symbol.ToLowerInvariant()}";

    private static string IssuerId(string symbol) =>
        $"issuer-{symbol.ToLowerInvariant()}";

    private static string Hash(string value) =>
        Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
    {
        await using var destination = new MemoryStream();
        await stream.CopyToAsync(destination);
        return destination.ToArray();
    }

    private static void AssertCanonicalBytesEqual<T>(T expected, T actual) =>
        Assert.True(
            EvidenceCanonicalJson.SerializeToUtf8Bytes(expected)
                .AsSpan()
                .SequenceEqual(EvidenceCanonicalJson.SerializeToUtf8Bytes(actual)));

    private sealed record FrozenEvidence(
        IReadOnlyList<DateOnly> SessionDates,
        EvidenceDatasetManifest AdjustedBars,
        EvidenceDatasetManifest AsTradedBars,
        EvidenceDatasetManifest Universe,
        IReadOnlyList<EvidenceDatasetManifest> Manifests);
}
