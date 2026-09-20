using TradingFlow.Domain.Strategies;
using TradingFlow.Data.Evidence;
using TradingFlow.Data.Evidence.Governance;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Storage;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class StrategyExperimentArtifactStoreTests
{
    private static readonly DateTimeOffset RegistrationTime =
        new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PersistedAuthorizationRegistry_DrivesCatalogVisibilityAcrossRestart()
    {
        var repositoryRoot = TestRepository.FindRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "trading-flow-persisted-authorization-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var reader = new SimpleYamlReader();
            var sourceCatalog = new StrategyArtifactCatalog(
                repositoryRoot,
                Path.Combine(repositoryRoot, "configs", "strategy-catalog.json"),
                reader);
            var experimentStore = new StrategyExperimentArtifactStore(
                Path.Combine(tempRoot, "registered-experiments"),
                sourceCatalog,
                reader,
                AtomicFileArtifactWriter.Instance);
            var immutableStore = new FileSystemImmutableArtifactStore(
                new ImmutableArtifactStoreOptions(Path.Combine(tempRoot, "objects")));
            var evidenceCatalog = new SqliteEvidenceCatalog(
                new EvidenceCatalogOptions(
                    Path.Combine(tempRoot, "evidence.db"),
                    EvidenceCatalogOpenMode.BootstrapNew),
                immutableStore);
            var registryPath = Path.Combine(tempRoot, "strategy-authorizations.db");
            var registry = new SqliteStrategyPromotionRegistry(
                new StrategyPromotionRegistryOptions(registryPath, StrategyPromotionRegistryOpenMode.BootstrapNew),
                evidenceCatalog,
                evidenceCatalog,
                immutableStore,
                new DenyAllPromotionPrincipalAuthorizer(),
                experimentStore);
            var writer = new RunConfigWriter(
                new ProjectPaths(tempRoot),
                reader,
                AtomicFileArtifactWriter.Instance,
                sourceCatalog,
                experimentStore,
                registry);
            var operationId = Guid.NewGuid();
            var registered = writer.RegisterPaperExperiment(
                Path.Combine(repositoryRoot, "configs", "strategies", "minervini-trend-template-vcp.v4-trend-rider.yaml"),
                "4.0.0",
                "unit-test-operator",
                operationId,
                RegistrationTime);
            var replay = writer.RegisterPaperExperiment(
                Path.Combine(repositoryRoot, "configs", "strategies", "minervini-trend-template-vcp.v4-trend-rider.yaml"),
                "4.0.0",
                "unit-test-operator",
                operationId,
                RegistrationTime);
            Assert.Equal(registered.Identity, replay.Identity);
            var duplicate = Assert.Throws<InvalidOperationException>(() =>
                writer.RegisterPaperExperiment(
                    Path.Combine(repositoryRoot, "configs", "strategies", "minervini-trend-template-vcp.v4-trend-rider.yaml"),
                    "4.0.0",
                    "unit-test-operator",
                    Guid.NewGuid(),
                    RegistrationTime));
            Assert.Contains("reuse its original decision ID", duplicate.Message, StringComparison.OrdinalIgnoreCase);

            var firstCatalog = new ConfigCatalogService(
                new ProjectPaths(repositoryRoot),
                reader,
                sourceCatalog,
                experimentStore,
                registry);
            Assert.Equal(
                registered.Identity,
                Assert.Single(await firstCatalog.GetStrategiesAsync(StrategySelectionMode.RunPaperExperiment)).Identity);

            var reopenedRegistry = new SqliteStrategyPromotionRegistry(
                new StrategyPromotionRegistryOptions(registryPath, StrategyPromotionRegistryOpenMode.OpenExisting),
                evidenceCatalog,
                evidenceCatalog,
                immutableStore,
                new DenyAllPromotionPrincipalAuthorizer(),
                experimentStore);
            var reopenedCatalog = new ConfigCatalogService(
                new ProjectPaths(repositoryRoot),
                reader,
                sourceCatalog,
                experimentStore,
                reopenedRegistry);
            Assert.Equal(
                registered.Identity,
                Assert.Single(await reopenedCatalog.GetStrategiesAsync(StrategySelectionMode.RunPaperExperiment)).Identity);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void EveryResearchArtifact_SerializesToTheSameCanonicalDefinition()
    {
        var repositoryRoot = TestRepository.FindRoot();
        var reader = new SimpleYamlReader();
        var sourceCatalog = new StrategyArtifactCatalog(
            repositoryRoot,
            Path.Combine(repositoryRoot, "configs", "strategy-catalog.json"),
            reader);
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "trading-flow-strategy-roundtrip-test",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            foreach (var source in sourceCatalog.GetSelectable(StrategySelectionMode.CreatePaperExperiment))
            {
                var path = Path.Combine(tempRoot, source.Identity.ContentSha256 + ".yaml");
                AtomicFileArtifactWriter.Instance.WriteText(
                    path,
                    RunConfigWriter.SerializeStrategyYaml(source.ResolvedStrategy));
                var parsed = reader.ReadStrategy(path);
                var expected = EvidenceCanonicalJson.ComputeSha256(new CanonicalStrategyDocument(
                    source.Identity.StrategyId,
                    source.Identity.SemanticVersion,
                    source.ResolvedStrategy,
                    source.AdmissionProfile));
                var actual = EvidenceCanonicalJson.ComputeSha256(new CanonicalStrategyDocument(
                    source.Identity.StrategyId,
                    source.Identity.SemanticVersion,
                    parsed,
                    source.AdmissionProfile));

                Assert.True(
                    expected.Equals(actual, StringComparison.Ordinal),
                    $"Strategy '{source.Identity.StrategyId}@{source.Identity.SemanticVersion}' did not round-trip. " +
                    $"Expected {expected}, actual {actual}. Differences: " +
                    String.Join("; ", FindDifferences(source.ResolvedStrategy, parsed, "strategy")));
            }
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentStoreInstances_RegisterOneExactArtifact()
    {
        var repositoryRoot = TestRepository.FindRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "trading-flow-experiment-concurrency-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var reader = new SimpleYamlReader();
            var sourceCatalog = new StrategyArtifactCatalog(
                repositoryRoot,
                Path.Combine(repositoryRoot, "configs", "strategy-catalog.json"),
                reader);
            var storeRoot = Path.Combine(tempRoot, "registered-experiments");
            var authorizations = new StrategyAuthorizationTestRegistry();
            RunConfigWriter Writer() => new(
                new ProjectPaths(tempRoot),
                reader,
                AtomicFileArtifactWriter.Instance,
                sourceCatalog,
                new StrategyExperimentArtifactStore(
                    storeRoot,
                    sourceCatalog,
                    reader,
                    AtomicFileArtifactWriter.Instance),
                authorizations);
            var sourcePath = Path.Combine(
                repositoryRoot,
                "configs",
                "strategies",
                "minervini-trend-template-vcp.v4-trend-rider.yaml");

            var operationId = Guid.NewGuid();
            var registered = await Task.WhenAll(
                Task.Run(() => Writer().RegisterPaperExperiment(
                    sourcePath,
                    "1.0.0",
                    "operator-a",
                    operationId,
                    RegistrationTime)),
                Task.Run(() => Writer().RegisterPaperExperiment(
                    sourcePath,
                    "1.0.0",
                    "operator-a",
                    operationId,
                    RegistrationTime)));

            Assert.Equal(registered[0].Identity, registered[1].Identity);
            Assert.Single(Directory.EnumerateFiles(storeRoot, "artifact.json", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RegistrationRetry_ReusesArtifactAndStableOperationAfterGrantFailure()
    {
        var repositoryRoot = TestRepository.FindRoot();
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "trading-flow-experiment-saga-retry-test",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var reader = new SimpleYamlReader();
            var sourceCatalog = new StrategyArtifactCatalog(
                repositoryRoot,
                Path.Combine(repositoryRoot, "configs", "strategy-catalog.json"),
                reader);
            var store = new StrategyExperimentArtifactStore(
                Path.Combine(tempRoot, "registered-experiments"),
                sourceCatalog,
                reader,
                AtomicFileArtifactWriter.Instance);
            var durableAuthorizations = new StrategyAuthorizationTestRegistry();
            var failFirst = new FailFirstExperimentGrantCommands(durableAuthorizations);
            var writer = new RunConfigWriter(
                new ProjectPaths(tempRoot),
                reader,
                AtomicFileArtifactWriter.Instance,
                sourceCatalog,
                store,
                failFirst);
            var sourcePath = Path.Combine(
                repositoryRoot,
                "configs",
                "strategies",
                "minervini-trend-template-vcp.v4-trend-rider.yaml");
            var operationId = Guid.NewGuid();

            Assert.Throws<IOException>(() => writer.RegisterPaperExperiment(
                sourcePath,
                "1.0.0",
                "unit-test-operator",
                operationId,
                RegistrationTime));
            Assert.Single(store.List());
            Assert.Empty(await durableAuthorizations.GetActiveGrantsAsync());

            var registered = writer.RegisterPaperExperiment(
                sourcePath,
                "1.0.0",
                "unit-test-operator",
                operationId,
                RegistrationTime);
            var replay = writer.RegisterPaperExperiment(
                sourcePath,
                "1.0.0",
                "unit-test-operator",
                operationId,
                RegistrationTime);

            Assert.Equal(registered.Identity, replay.Identity);
            Assert.Single(store.List());
            Assert.Single(await durableAuthorizations.GetActiveGrantsAsync());
            Assert.Equal(3, failFirst.Attempts);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RegisteredExperiment_IsDurableSelectableAndSeparateFromRunSnapshot()
    {
        var repositoryRoot = TestRepository.FindRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "trading-flow-experiment-store-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var reader = new SimpleYamlReader();
            var sourceCatalog = new StrategyArtifactCatalog(
                repositoryRoot,
                Path.Combine(repositoryRoot, "configs", "strategy-catalog.json"),
                reader);
            var store = new StrategyExperimentArtifactStore(
                Path.Combine(tempRoot, "registered-experiments"),
                sourceCatalog,
                reader,
                AtomicFileArtifactWriter.Instance);
            var authorizations = new StrategyAuthorizationTestRegistry();
            var writer = new RunConfigWriter(
                new ProjectPaths(tempRoot),
                reader,
                AtomicFileArtifactWriter.Instance,
                sourceCatalog,
                store,
                authorizations);
            var sourcePath = Path.Combine(
                repositoryRoot,
                "configs",
                "strategies",
                "minervini-trend-template-vcp.v4-trend-rider.yaml");

            var operationId = Guid.NewGuid();
            var registered = writer.RegisterPaperExperiment(
                sourcePath,
                "1.0.0",
                "unit-test-operator",
                operationId,
                RegistrationTime);
            var replay = writer.RegisterPaperExperiment(
                sourcePath,
                "1.0.0",
                "unit-test-operator",
                operationId,
                RegistrationTime);

            Assert.Equal(StrategyArtifactDisposition.Research, registered.Disposition);
            Assert.Equal(StrategyLifecycleState.PaperExperiment, registered.EffectiveLifecycle);
            Assert.Equal(registered.Identity, replay.Identity);
            Assert.True(File.Exists(registered.SourcePath));
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(registered.SourcePath)!, "artifact.json")));

            var catalogService = new ConfigCatalogService(
                new ProjectPaths(repositoryRoot),
                reader,
                sourceCatalog,
                store,
                authorizations);
            var selectable = await catalogService.GetStrategiesAsync(StrategySelectionMode.RunPaperExperiment);
            var option = Assert.Single(selectable);
            Assert.Equal(registered.Identity, option.Identity);
            Assert.Equal(StrategyLifecycleState.PaperExperiment, option.Lifecycle);

            authorizations.Seed(registered.Identity, StrategyExecutionAuthorization.PaperShadow, "shadow-grant");
            var shadow = Assert.Single(await catalogService.GetStrategiesAsync(StrategySelectionMode.RunPaperShadow));
            Assert.Equal(registered.Identity, shadow.Identity);
            Assert.Equal(Path.GetFullPath(registered.SourcePath), shadow.Path);
            authorizations.Seed(registered.Identity, StrategyExecutionAuthorization.Validated, "validated-grant");
            var live = Assert.Single(await catalogService.GetStrategiesAsync(StrategySelectionMode.RunLive));
            Assert.Equal(registered.Identity, live.Identity);
            Assert.Equal(Path.GetFullPath(registered.SourcePath), live.Path);

            var runConfig = writer.SaveTempConfig(
                Path.Combine(repositoryRoot, "configs", "paper", "alpaca-paper.yaml"),
                ["MU"],
                registered,
                StrategySelectionMode.RunPaperExperiment,
                orderExpiration: "day",
                entryOrderType: "limit",
                runName: "registered-experiment-run");
            var run = reader.ReadBacktestRun(runConfig);
            var runStrategyPath = Assert.Single(run.Strategies);
            Assert.NotEqual(registered.SourcePath, runStrategyPath);
            _ = new StrategyRunArtifactValidator(reader, sourceCatalog).ReadAndValidate(
                runStrategyPath,
                StrategySelectionMode.RunPaperExperiment);

            await authorizations.RevokePaperExperimentAsync(
                registered.Identity,
                "revoke-experiment",
                "test-grant",
                "unit-test-operator",
                DateTimeOffset.UtcNow,
                "Experiment was withdrawn from execution.");
            Assert.Single(store.List());
            Assert.Empty(await catalogService.GetStrategiesAsync(StrategySelectionMode.RunPaperExperiment));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void ChangedExperiment_RequiresNewVersionAndRecordsOverride()
    {
        var repositoryRoot = TestRepository.FindRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "trading-flow-experiment-version-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var reader = new SimpleYamlReader();
            var sourceCatalog = new StrategyArtifactCatalog(
                repositoryRoot,
                Path.Combine(repositoryRoot, "configs", "strategy-catalog.json"),
                reader);
            var store = new StrategyExperimentArtifactStore(
                Path.Combine(tempRoot, "registered-experiments"),
                sourceCatalog,
                reader,
                AtomicFileArtifactWriter.Instance);
            var authorizations = new StrategyAuthorizationTestRegistry();
            var writer = new RunConfigWriter(
                new ProjectPaths(tempRoot),
                reader,
                AtomicFileArtifactWriter.Instance,
                sourceCatalog,
                store,
                authorizations);
            var sourcePath = Path.Combine(
                repositoryRoot,
                "configs",
                "strategies",
                "minervini-trend-template-vcp.v4-trend-rider.yaml");
            var strategyOverride = new StrategyParameterOverride(
                sourcePath,
                2.5m,
                0m,
                100m,
                null,
                false,
                "1h",
                50,
                1m,
                3m,
                6m,
                false,
                1m,
                1m,
                1,
                "1h",
                25m,
                Actor: "experiment-author");

            Assert.Throws<InvalidOperationException>(() => writer.RegisterPaperExperiment(
                sourcePath,
                "4.0.0",
                "experiment-author",
                Guid.NewGuid(),
                RegistrationTime,
                strategyOverride));

            var registered = writer.RegisterPaperExperiment(
                sourcePath,
                "4.1.0-experiment",
                "experiment-author",
                Guid.NewGuid(),
                RegistrationTime,
                strategyOverride);
            var manifest = File.ReadAllText(Path.Combine(Path.GetDirectoryName(registered.SourcePath)!, "artifact.json"));

            Assert.Equal("4.1.0-experiment", registered.Identity.SemanticVersion);
            Assert.NotNull(registered.DerivedFromContentSha256);
            Assert.Contains("\"registeredBy\":\"experiment-author\"", manifest, StringComparison.Ordinal);
            Assert.Contains("entry_rules.min_volume_spike", manifest, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private sealed class FailFirstExperimentGrantCommands(
        IStrategyExperimentAuthorizationCommands inner) : IStrategyExperimentAuthorizationCommands
    {
        private int attempts;

        public int Attempts => Volatile.Read(ref attempts);

        public Task GrantPaperExperimentAsync(
            StrategyArtifactIdentity identity,
            string decisionId,
            string actor,
            DateTimeOffset decidedAtUtc,
            string reason,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new IOException("Injected failure between artifact publication and authorization commit.");
            }

            return inner.GrantPaperExperimentAsync(
                identity,
                decisionId,
                actor,
                decidedAtUtc,
                reason,
                cancellationToken);
        }

        public Task RevokePaperExperimentAsync(
            StrategyArtifactIdentity identity,
            string decisionId,
            string targetDecisionId,
            string actor,
            DateTimeOffset decidedAtUtc,
            string reason,
            CancellationToken cancellationToken = default) =>
            inner.RevokePaperExperimentAsync(
                identity,
                decisionId,
                targetDecisionId,
                actor,
                decidedAtUtc,
                reason,
                cancellationToken);

        public Task SuspendPaperExperimentAsync(
            StrategyArtifactIdentity identity,
            string decisionId,
            string targetDecisionId,
            string actor,
            DateTimeOffset decidedAtUtc,
            string reason,
            CancellationToken cancellationToken = default) =>
            inner.SuspendPaperExperimentAsync(
                identity,
                decisionId,
                targetDecisionId,
                actor,
                decidedAtUtc,
                reason,
                cancellationToken);

        public Task ResumePaperExperimentAsync(
            StrategyArtifactIdentity identity,
            string decisionId,
            string targetDecisionId,
            string actor,
            DateTimeOffset decidedAtUtc,
            string reason,
            CancellationToken cancellationToken = default) =>
            inner.ResumePaperExperimentAsync(
                identity,
                decisionId,
                targetDecisionId,
                actor,
                decidedAtUtc,
                reason,
                cancellationToken);

        public Task SupersedePaperExperimentAsync(
            StrategyArtifactIdentity existingIdentity,
            string supersessionDecisionId,
            string targetDecisionId,
            StrategyArtifactIdentity replacementIdentity,
            string replacementDecisionId,
            string actor,
            DateTimeOffset decidedAtUtc,
            string reason,
            CancellationToken cancellationToken = default) =>
            inner.SupersedePaperExperimentAsync(
                existingIdentity,
                supersessionDecisionId,
                targetDecisionId,
                replacementIdentity,
                replacementDecisionId,
                actor,
                decidedAtUtc,
                reason,
                cancellationToken);
    }

    private static IReadOnlyList<string> FindDifferences(object? expected, object? actual, string path)
    {
        if (ReferenceEquals(expected, actual))
        {
            return [];
        }

        if (expected is null || actual is null)
        {
            return [$"{path}: expected={expected ?? "<null>"}, actual={actual ?? "<null>"}"];
        }

        var type = expected.GetType();
        if (type != actual.GetType())
        {
            return [$"{path}: expected type={type.Name}, actual type={actual.GetType().Name}"];
        }

        if (type.IsPrimitive || type.IsEnum || expected is string or decimal or DateTimeOffset or TimeSpan)
        {
            return Equals(expected, actual)
                ? []
                : [$"{path}: expected={expected}, actual={actual}"];
        }

        if (expected is System.Collections.IEnumerable expectedItems &&
            actual is System.Collections.IEnumerable actualItems)
        {
            var expectedArray = expectedItems.Cast<object?>().ToArray();
            var actualArray = actualItems.Cast<object?>().ToArray();
            var differences = new List<string>();
            if (expectedArray.Length != actualArray.Length)
            {
                differences.Add($"{path}.Count: expected={expectedArray.Length}, actual={actualArray.Length}");
            }

            for (var index = 0; index < Math.Min(expectedArray.Length, actualArray.Length); index++)
            {
                differences.AddRange(FindDifferences(expectedArray[index], actualArray[index], $"{path}[{index}]"));
            }

            return differences;
        }

        return type.GetProperties()
            .SelectMany(property => FindDifferences(
                property.GetValue(expected),
                property.GetValue(actual),
                $"{path}.{property.Name}"))
            .ToArray();
    }
}
