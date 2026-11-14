using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TradingFlow.Backtesting;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Storage;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class StrategyAvailabilityTests
{
    private const string RotationId = "etf.rotation-12-1";
    private const string RotationPath = "configs/backtest/strategies/etf-rotation-12-1.bt-v1.yaml";
    private const string Reason = "Sector rotation is parked for later standalone research.";

    // Changing the operational flag in an isolated copy leaves all catalog identity and lifecycle fields intact.
    [Fact]
    public void ManifestToggle_PreservesIdentityAndRestoresResearchSelectionOnReload()
    {
        using var fixture = new Fixture();
        var parked = fixture.LoadCatalog();
        var held = parked.RequireSourcePath(fixture.Rotation);
        Assert.Contains(Reason, parked.GetDisabledReason(held), StringComparison.Ordinal);
        Assert.DoesNotContain(parked.GetSelectable(StrategySelectionMode.Backtest), item => item.Identity == held.Identity);
        Assert.Equal(held, parked.RequireExact(held.Identity));
        Assert.Equal(StrategyLifecycleState.Research, held.EffectiveLifecycle);
        Assert.Equal(StrategyArtifactDisposition.Research, held.Disposition);
        Assert.Contains(parked.GetSelectable(StrategySelectionMode.Backtest), item => item.Identity.StrategyId == "etf.trend-10m");
        Assert.Contains(parked.GetSelectable(StrategySelectionMode.Backtest), item => item.Identity.StrategyId == "etf.buy-and-hold-control");
        Assert.Equal(3, parked.GetSelectable(StrategySelectionMode.Backtest).Count(item => item.Identity.StrategyId == "technical.opening-range-breakout"));

        fixture.SetAvailability(RotationId, "1.0.0", enabled: null);
        var reloaded = fixture.LoadCatalog();
        Assert.Equal(held, reloaded.RequireExact(held.Identity));
        Assert.Contains(Reason, parked.GetDisabledReason(held), StringComparison.Ordinal);
        Assert.Null(reloaded.GetDisabledReason(held));
        Assert.Contains(reloaded.GetSelectable(StrategySelectionMode.Backtest), item => item.Identity == held.Identity);
        Assert.Empty(reloaded.GetSelectable(StrategySelectionMode.RunPaperShadow));
        Assert.Empty(reloaded.GetSelectable(StrategySelectionMode.RunLive));
    }

    // A disabled entry must explain the refusal an operator sees in Strategy Lab.
    [Fact]
    public void DisabledManifestEntry_RequiresReason()
    {
        using var fixture = new Fixture();
        fixture.SetAvailability(RotationId, "1.0.0", enabled: false, reason: " ");
        var error = Assert.Throws<InvalidDataException>(() => fixture.LoadCatalog().GetSnapshot());
        Assert.Contains("disabledReason", error.Message, StringComparison.Ordinal);
    }

    // Disabling a catalog ancestor applies transitively to exact descendants while their identities still resolve.
    [Fact]
    public void ManifestLineage_DisabledAncestorRefusesExistingDescendant()
    {
        using var fixture = new Fixture();
        var enabled = fixture.LoadCatalog();
        var latest = Assert.Single(enabled.GetSelectable(StrategySelectionMode.Backtest), item =>
            item.Identity.StrategyId == "technical.opening-range-breakout" && item.Identity.SemanticVersion == "2.1.0");
        fixture.SetAvailability("technical.opening-range-breakout", "1.2.0", enabled: false);
        var parked = fixture.LoadCatalog();

        Assert.Equal(latest, parked.RequireExact(latest.Identity));
        Assert.Contains("technical.opening-range-breakout@1.2.0", parked.GetDisabledReason(latest), StringComparison.Ordinal);
        Assert.DoesNotContain(parked.GetSelectable(StrategySelectionMode.Backtest), item => item.Identity == latest.Identity);
        Assert.Throws<InvalidOperationException>(() =>
            parked.CreateDerivedArtifact(latest, latest.ResolvedStrategy, "2.2.0"));
    }

    // Reproducing an exact canonical identity gives it the same availability even when its immediate source remains enabled.
    [Fact]
    public void SnapshotAndRegistration_WithDisabledExactIdentity_RefuseAnEnabledSource()
    {
        const string family = "technical.opening-range-breakout";
        const string disabledVersion = "2.1.0";
        using var fixture = new Fixture();
        var enabled = fixture.LoadCatalog();
        var artifacts = enabled.GetSelectable(StrategySelectionMode.Backtest);
        var source = Assert.Single(artifacts, item => item.Identity.StrategyId == family && item.Identity.SemanticVersion == "2.0.0");
        var target = Assert.Single(artifacts, item => item.Identity.StrategyId == family && item.Identity.SemanticVersion == disabledVersion);
        var persisted = enabled.CreateDerivedArtifact(source, target.ResolvedStrategy, disabledVersion);
        Assert.Equal(target.Identity, persisted.Identity);
        var snapshot = fixture.WriteSnapshot(persisted, source, "existing-snapshot", StrategySelectionMode.Backtest);
        var registration = fixture.Store(enabled).Register(source, target.ResolvedStrategy, disabledVersion, "fixture",
            new Dictionary<string, string>(), RunConfigWriter.SerializeStrategyYaml(target.ResolvedStrategy));

        fixture.SetAvailability(family, disabledVersion, enabled: false, reason: "Parking the distinct ORB test version.");
        var parked = fixture.LoadCatalog();
        var validator = new StrategyRunArtifactValidator(fixture.Reader, parked);
        Assert.Null(parked.GetDisabledReason(source));
        Assert.Equal(target.Identity, validator.ReadAndValidateRuntimeArtifact(snapshot, StrategySelectionMode.Backtest).Identity);
        Assert.Equal(registration, fixture.Store(parked).RequireExact(registration.Identity));
        Assert.False(fixture.Store(parked).IsAvailableForAuthorization(registration.Identity));

        var newDerivation = Assert.Throws<InvalidOperationException>(() =>
            parked.CreateDerivedArtifact(source, target.ResolvedStrategy, disabledVersion));
        var newRun = Assert.Throws<InvalidOperationException>(() =>
            validator.ReadAndAuthorizeRuntimeArtifact(snapshot, StrategySelectionMode.Backtest));
        var researchRead = Assert.Throws<InvalidOperationException>(() =>
            validator.ReadResearchDefinition(registration.SourcePath));
        Assert.All(new[] { newDerivation, newRun, researchRead }, error =>
            Assert.Contains(family + "@" + disabledVersion, error.Message, StringComparison.Ordinal));
    }

    // Previously created snapshots and canonical inputs fail new-work admission but still validate for historical reporting.
    [Fact]
    public async Task NewRunnerAndDiagnosticRefuseParkedSource_WhileHistoricalSnapshotAndMonthlyRowsRemainReadable()
    {
        using var fixture = new Fixture();
        fixture.SetAvailability(RotationId, "1.0.0", enabled: true);
        var enabled = fixture.LoadCatalog();
        var source = enabled.RequireSourcePath(fixture.Rotation);
        var derived = enabled.CreateDerivedArtifact(source, source.ResolvedStrategy, "1.0.1");
        var snapshot = fixture.WriteSnapshot(derived, source, "derived", StrategySelectionMode.Backtest);
        var diagnostic = fixture.WriteSnapshot(derived, source, "diagnostic", StrategySelectionMode.DiagnosticReplay);

        fixture.SetAvailability(RotationId, "1.0.0", enabled: false);
        var parked = fixture.LoadCatalog();
        var validator = new StrategyRunArtifactValidator(fixture.Reader, parked);
        Assert.Contains(Reason, Assert.Throws<InvalidOperationException>(() =>
            validator.ReadAndAuthorizeRuntimeArtifact(fixture.Rotation, StrategySelectionMode.Backtest)).Message, StringComparison.Ordinal);
        Assert.Contains(Reason, Assert.Throws<InvalidOperationException>(() =>
            validator.ReadAndAuthorizeRuntimeArtifact(diagnostic, StrategySelectionMode.DiagnosticReplay)).Message, StringComparison.Ordinal);
        Assert.Contains(Reason, Assert.Throws<InvalidOperationException>(() =>
            parked.CreateDerivedArtifact(source, source.ResolvedStrategy, "1.0.2")).Message, StringComparison.Ordinal);
        Assert.Equal(derived.Identity, validator.ReadAndValidateRuntimeArtifact(snapshot, StrategySelectionMode.Backtest).Identity);

        var runPath = Path.Combine(fixture.Root, "run.yaml");
        var template = File.ReadAllText(Path.Combine(TestRepository.FindRoot(), "configs", "backtest", "swing-backtest-profile.yaml"));
        File.WriteAllText(runPath, template.Replace("strategies:", $"strategies:\n  - {snapshot.Replace('\\', '/')}", StringComparison.Ordinal));
        var resultPath = Path.Combine(fixture.Root, "unstarted", "result.json");
        var runner = new BacktestRunner(fixture.Reader, parked);
        Assert.Contains(Reason, (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(runPath, resultPath, DateTimeOffset.UtcNow, CancellationToken.None))).Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.GetDirectoryName(resultPath)));

        var historical = MonthlyRunResultWriterTests.CreateFixture();
        var strategy = historical.Result.StrategyResults[0] with
        {
            StrategyId = derived.ResolvedStrategy.StrategyId,
            CompletedTrades = historical.Result.StrategyResults[0].CompletedTrades
                .Select(trade => trade with { StrategyId = derived.ResolvedStrategy.StrategyId }).ToArray()
        };
        var result = historical.Result with { StrategyResults = [strategy], CompletedTrades = strategy.CompletedTrades };
        var run = historical.Config with { Strategies = [snapshot] };
        var identity = new StrategyReference(derived.Identity.StrategyId, derived.Identity.SemanticVersion, derived.Identity.ContentSha256);
        var report = new MonthlyRunResultWriter(validator).Create(run, result, run.TimeWindow.End!.Value, "Retained", [identity]);

        Assert.Null(report.UnavailableReason);
        Assert.Equal(2, report.Results.Count);
        Assert.All(report.Results, item => Assert.Equal(identity, item.Strategy));
    }

    // The active experiment grant remains stored, but a fresh catalog refuses that experiment while its source is parked.
    [Fact]
    public async Task RegisteredExperiment_RetainsArtifactAndGrantWhileSelectionAndQueuedPaperStartRefuseIt()
    {
        using var fixture = new Fixture();
        fixture.SetAvailability(RotationId, "1.0.0", enabled: true);
        var enabled = fixture.LoadCatalog();
        var source = enabled.RequireSourcePath(fixture.Rotation);
        var overridden = source.ResolvedStrategy with
        {
            EntryRules = source.ResolvedStrategy.EntryRules with
            {
                MinVolumeSpike = source.ResolvedStrategy.EntryRules.MinVolumeSpike + 1m
            }
        };
        var existing = fixture.Store(enabled).Register(source, overridden, "1.0.1", "fixture",
            new Dictionary<string, string>(), RunConfigWriter.SerializeStrategyYaml(overridden));
        var grants = new StrategyAuthorizationTestRegistry();
        grants.Seed(existing.Identity, StrategyExecutionAuthorization.PaperExperiment, "existing-fixture-grant");
        var queued = fixture.WriteSnapshot(existing, existing, "paper", StrategySelectionMode.RunPaperExperiment);
        Assert.Single(await fixture.Service(enabled, grants).GetStrategiesAsync(StrategySelectionMode.RunPaperExperiment));

        fixture.SetAvailability(RotationId, "1.0.0", enabled: false);
        var parked = fixture.LoadCatalog();
        var store = fixture.Store(parked);
        var catalog = fixture.Service(parked, grants);
        Assert.Equal(existing, store.RequireExact(existing.Identity));
        Assert.Empty(await catalog.GetStrategiesAsync(StrategySelectionMode.RunPaperExperiment));
        Assert.True(store.IsRegistered(existing.Identity));
        Assert.False(store.IsAvailableForAuthorization(existing.Identity));
        var runner = new BacktestRunner(fixture.Reader, parked);
        Assert.Contains(Reason, Assert.Throws<InvalidOperationException>(() =>
            runner.ReadResearchStrategy(existing.SourcePath)).Message, StringComparison.Ordinal);
        Assert.Contains(Reason, Assert.Throws<InvalidOperationException>(() =>
            store.Register(source, source.ResolvedStrategy, "1.0.2", "fixture", new Dictionary<string, string>(),
                RunConfigWriter.SerializeStrategyYaml(source.ResolvedStrategy))).Message, StringComparison.Ordinal);
        Assert.Contains(Reason, (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.RequireAuthorizedPaperRunSnapshotAsync(queued))).Message, StringComparison.Ordinal);
        Assert.Equal(existing.Identity, catalog.ReadAndValidatePaperSnapshot(queued).Manifest.Identity);
        Assert.Equal(existing.Identity, Assert.Single(await grants.GetActiveGrantsAsync()).Identity);

        fixture.SetAvailability(RotationId, "1.0.0", enabled: true);
        await fixture.Service(fixture.LoadCatalog(), grants).RequireAuthorizedPaperRunSnapshotAsync(queued);
        Assert.Single(await grants.GetActiveGrantsAsync());
    }

    // Copies only the strategy catalog and its classified YAML into a test-owned folder; all experiments and snapshots stay there.
    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryFolder folder = new();
        public string Root => folder.Path;
        public string Rotation => Path.Combine(Root, RotationPath);
        public SimpleYamlReader Reader { get; } = new();
        private string Manifest => Path.Combine(Root, "configs", "strategy-catalog.json");

        // Mirror the exact real strategy set so the normal catalog completeness and hashing rules run in every test.
        public Fixture()
        {
            var repo = TestRepository.FindRoot();
            var sourceManifest = Path.Combine(repo, "configs", "strategy-catalog.json");
            Directory.CreateDirectory(Path.GetDirectoryName(Manifest)!);
            File.Copy(sourceManifest, Manifest);
            using var contents = JsonDocument.Parse(File.ReadAllText(Manifest));
            foreach (var item in contents.RootElement.GetProperty("strategies").EnumerateArray())
            {
                var relative = item.GetProperty("sourcePath").GetString()!;
                var target = Path.Combine(Root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(repo, relative), target);
            }
        }

        // Reloads as a new Web process or CLI invocation would.
        public StrategyArtifactCatalog LoadCatalog() => new(Root, Manifest, Reader);

        // A null value removes the optional field to exercise manifests created before strategy parking.
        public void SetAvailability(string strategyId, string semanticVersion, bool? enabled, string reason = Reason)
        {
            var document = JsonNode.Parse(File.ReadAllText(Manifest))!;
            var entry = document["strategies"]!.AsArray().Single(item =>
                item!["strategyId"]!.GetValue<string>() == strategyId &&
                item["semanticVersion"]!.GetValue<string>() == semanticVersion)!.AsObject();
            if (enabled.HasValue)
                entry["enabled"] = enabled.Value;
            else
                entry.Remove("enabled");
            if (enabled == false)
                entry["disabledReason"] = reason;
            File.WriteAllText(Manifest, document.ToJsonString());
        }

        // Open the same experiment artifacts against the requested catalog state.
        public StrategyExperimentArtifactStore Store(StrategyArtifactCatalog catalog) =>
            new(Path.Combine(Root, "registered"), catalog, Reader, AtomicFileArtifactWriter.Instance);

        // Exercise the actual Web selection and paper-snapshot checks with synthetic grants and storage.
        public ConfigCatalogService Service(StrategyArtifactCatalog catalog, StrategyAuthorizationTestRegistry grants) =>
            new(new ProjectPaths(Root), Reader, catalog, Store(catalog), grants);

        // Freezes strategy bytes and their exact source identity just as the run writer does.
        public string WriteSnapshot(StrategyArtifact artifact, StrategyArtifact source, string name, StrategySelectionMode mode)
        {
            var path = Path.Combine(Root, name + ".yaml");
            File.WriteAllText(path, RunConfigWriter.SerializeStrategyYaml(artifact.ResolvedStrategy));
            var manifest = new StrategyRunArtifactManifest(1, artifact.Identity, mode, source.Identity, source.SourcePath,
                artifact.AdmissionProfile, DateTimeOffset.UtcNow, "fixture", new Dictionary<string, string>());
            File.WriteAllText(path + ".artifact.json", Encoding.UTF8.GetString(EvidenceCanonicalJson.SerializeToUtf8Bytes(manifest)));
            return path;
        }

        // Releases only the isolated test copy.
        public void Dispose() => folder.Dispose();
    }
}
