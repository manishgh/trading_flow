using TradingFlow.Application.Jobs;
using TradingFlow.Backtesting;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Storage;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed partial class DecisionContextProviderTests
{
    /// <summary>The durable graph contains manifest, contexts and independently typed price bundles, and survives serialization.</summary>
    [Fact]
    public void CompleteContextGraph_IsCapturedAndRejectsTamperedSourceAfterRestart()
    {
        using var fixture = new EvidenceFixture();
        var provider = fixture.Provider();
        var config = Path.Combine(fixture.Root, "run.yaml");
        File.WriteAllText(config, "synthetic input capture only");
        var run = new SimpleYamlReader().ReadBacktestRun(Path.Combine(TestRepository.FindRoot(), "configs", "backtest", "swing-backtest-profile.yaml"));
        run = run with { Strategies = [], Engine = run.Engine with { EvidenceResearchRoot = fixture.Root,
            EvidenceManifestPath = "manifest.json", EvidenceManifestSha256 = provider.ManifestSha256,
            EvidenceDependencyGraphSha256 = provider.DependencyGraphSha256 } };
        var inputs = BacktestRunInputFiles.Resolve(config, run);
        Assert.Equal(1 + 1 + fixture.Manifest.Sources.Length + fixture.Manifest.Contexts.Length, inputs.Count);
        Assert.Contains(Path.Combine(fixture.Root, "synthetic-test-security.json"), inputs);
        var durable = DurableFileJobRequest.Deserialize(DurableFileJobRequest.Capture(inputs).Serialize());
        durable.Verify();
        File.AppendAllText(Path.Combine(fixture.Root, fixture.Manifest.Sources[0].Path), " ");
        Assert.Throws<InvalidDataException>(() => durable.Verify());
        Assert.Throws<InvalidDataException>(() => BacktestRunInputFiles.Resolve(config, run));
    }

    /// <summary>Configured manifest and complete graph identities are independently checked before any decision resolution.</summary>
    [Fact]
    public void ConfiguredManifestOrDependencyHashMismatch_IsRejected()
    {
        using var fixture = new EvidenceFixture();
        var provider = fixture.Provider();
        var config = new EngineConfig("channels", 1, 10, 30, 60, false) {
            EvidenceResearchRoot = fixture.Root, EvidenceManifestPath = "manifest.json",
            EvidenceManifestSha256 = provider.ManifestSha256, EvidenceDependencyGraphSha256 = provider.DependencyGraphSha256 };
        Assert.NotNull(FrozenManifestDecisionContextProvider.FromConfig(config));
        Assert.Throws<InvalidDataException>(() => FrozenManifestDecisionContextProvider.FromConfig(config with { EvidenceManifestSha256 = new('a', 64) }));
        Assert.Throws<InvalidDataException>(() => FrozenManifestDecisionContextProvider.FromConfig(config with { EvidenceDependencyGraphSha256 = new('b', 64) }));
        Assert.Equal(provider.ManifestSha256, provider.Provenance.ManifestSha256);
        Assert.Equal(provider.DependencyGraphSha256, provider.Provenance.DependencyGraphSha256);
    }

    /// <summary>Server profile identities are unique across nested directories, and evidence cannot override execution settings.</summary>
    [Fact]
    public async Task CatalogBinding_UsesRelativeIdentityAndCopiesOnlyEvidenceFields()
    {
        using var fixture = new EvidenceFixture();
        fixture.Provider();
        var catalog = CreateEvidenceCatalog(fixture, bindProfiles: true);
        var choices = catalog.GetDecisionContextEvidenceChoices();
        Assert.Equal(new[] { "first/profile.yaml", "second/profile.yaml" }, choices.Select(item => item.Reference!.Id).Order());
        Assert.All(choices, item => Assert.True(item.Available));
        var option = (await catalog.GetStrategiesAsync(StrategySelectionMode.Backtest)).First();
        var request = EvidenceRequest(option, choices[0].Reference);
        var execution = new EngineConfig("channels", 7, 99, 260, 120, true);
        var binding = catalog.BindDecisionContextEvidence(request, execution, [option]);
        Assert.Equal(execution.WorkerCount, binding.Engine.WorkerCount);
        Assert.Equal(execution.IndicatorWarmupBars, binding.Engine.IndicatorWarmupBars);
        Assert.Equal(choices[0].Reference!.ManifestSha256, binding.Engine.EvidenceManifestSha256);
        Assert.Equal(binding.Summary!.DependencyGraphSha256, binding.Engine.EvidenceDependencyGraphSha256);
        Assert.Throws<InvalidDataException>(() => catalog.BindDecisionContextEvidence(request with {
            DecisionContextEvidence = request.DecisionContextEvidence! with { ManifestSha256 = new('a', 64) } }, execution, [option]));
        Assert.Throws<InvalidOperationException>(() => catalog.BindDecisionContextEvidence(request with {
            DecisionContextEvidence = request.DecisionContextEvidence! with { Id = "../profile.yaml" } }, execution, [option]));
    }

    /// <summary>Missing, empty and failed-evaluation choices never become a passing event gate, while unbound price recipes retain their engine.</summary>
    [Fact]
    public async Task RequiredEvidence_RejectsAbsentAndEmptyContextsWithoutChangingPricePlans()
    {
        using var fixture = new EvidenceFixture();
        var catalog = CreateEvidenceCatalog(fixture, bindProfiles: false);
        Assert.False(Assert.Single(catalog.GetDecisionContextEvidenceChoices()).Available);
        var price = (await catalog.GetStrategiesAsync(StrategySelectionMode.Backtest)).First();
        var required = price with { Definition = price.Definition with { EntryRules = price.Definition.EntryRules with {
            DrawdownQualification = new("gate", RequireEventContext: true) } } };
        var engine = new EngineConfig("channels", 1, 10, 30, 60, false);
        var request = EvidenceRequest(price, null);
        Assert.Equal(engine, catalog.BindDecisionContextEvidence(request, engine, [price]).Engine);
        Assert.Throws<InvalidOperationException>(() => catalog.BindDecisionContextEvidence(request, engine, [required]));
        fixture.Manifest = fixture.Manifest with { Contexts = [] };
        fixture.Provider();
        catalog = CreateEvidenceCatalog(fixture, bindProfiles: true);
        var choice = catalog.GetDecisionContextEvidenceChoices()[0];
        Assert.False(choice.Available);
        Assert.Contains("no actual decision contexts", choice.UnavailableReason);
        Assert.Throws<InvalidOperationException>(() => catalog.BindDecisionContextEvidence(request with {
            DecisionContextEvidence = choice.Reference }, engine, [required]));
        fixture.ReplaceContext(fixture.Context with { Events = fixture.Context.Events! with {
            Evaluation = fixture.Context.Events.Evaluation with { Passed = false } } });
        fixture.Provider();
        var failed = catalog.GetDecisionContextEvidenceChoices()[0];
        Assert.False(failed.Available);
        Assert.Contains("has not passed", failed.UnavailableReason);
    }

    /// <summary>Creates isolated profile discovery around explicitly synthetic contexts without writing any operator source.</summary>
    private static ConfigCatalogService CreateEvidenceCatalog(EvidenceFixture fixture, bool bindProfiles)
    {
        var repo = TestRepository.FindRoot();
        var reader = new SimpleYamlReader();
        var artifacts = new StrategyArtifactCatalog(repo, Path.Combine(repo, "configs", "strategy-catalog.json"), reader);
        var store = new StrategyExperimentArtifactStore(Path.Combine(fixture.Root, "experiments"), artifacts, reader, AtomicFileArtifactWriter.Instance);
        if (bindProfiles)
        {
            var yaml = File.ReadAllText(Path.Combine(repo, "configs", "backtest", "swing-backtest-profile.yaml"));
            yaml = yaml.Replace("engine:", $"engine:\n  evidence_manifest_path: manifest.json\n  evidence_research_root: {fixture.Root.Replace('\\', '/')}", StringComparison.Ordinal);
            foreach (var directory in new[] { "first", "second" })
            {
                var root = Path.Combine(fixture.Root, "configs", "backtest", directory);
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, "profile.yaml"), yaml);
            }
        }
        return new(new ProjectPaths(fixture.Root), reader, artifacts, store, new StrategyAuthorizationTestRegistry());
    }

    /// <summary>Constructs a wire request whose only evidence input is the reviewed server ID and manifest hash.</summary>
    private static StartBacktestRequest EvidenceRequest(StrategyOption option, DecisionContextEvidenceReference? reference) =>
        new("1.0", "synthetic-evidence-test", Guid.NewGuid(), [new(option.Identity.StrategyId, option.Identity.SemanticVersion,
            option.Identity.ContentSha256)], DateTimeOffset.UtcNow.AddMonths(-1), DateTimeOffset.UtcNow.AddDays(-1),
            10000, 1, 25, 4, "use_cache", "full", "synthetic-evidence-test", DecisionContextEvidence: reference);
}
