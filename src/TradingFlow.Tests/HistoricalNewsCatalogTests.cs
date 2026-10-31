using System.Text.Json;
using TradingFlow.Contracts.Presentation;
using TradingFlow.Contracts.V1;
using TradingFlow.Data.Evidence.Historical;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Storage;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class HistoricalNewsCatalogTests
{
    [Fact]
    // Normal catalog references bind the full graph without changing sizing, execution settings or the original files.
    public async Task PublishedDiagnosticBindsThroughNormalCatalogAndRoundTripsClientSummary()
    {
        using var fixture = new HistoricalDiagnosticNewsTests.Archive();
        fixture.News("empty", []);
        var provider = fixture.Provider();
        Publish(fixture, provider.Manifest);
        var catalog = Catalog(fixture);
        var choice = Assert.Single(catalog.GetDecisionContextEvidenceChoices());
        Assert.True(choice.Available);
        var selected = (await catalog.GetStrategiesAsync(StrategySelectionMode.Backtest))
            .Single(item => item.Identity.StrategyId == "technical.recovery-sma5-news-evidence");
        var request = Request(selected.Identity, choice.Reference);
        Assert.True(TradingFlow.Web.TradingApiV1Endpoints.UsesNews(selected.Definition));
        var original = new EngineConfig("channels", 7, 99, 260, 120, true);
        var binding = catalog.BindDecisionContextEvidence(request, original, [selected]);
        Assert.Equal(7, binding.Engine.WorkerCount);
        Assert.Equal(260, binding.Engine.IndicatorWarmupBars);
        Assert.Equal(64, binding.Summary!.DependencyGraphSha256.Length);
        Assert.NotNull(binding.Summary.HistoricalNewsDiagnostic);
        Assert.Throws<InvalidOperationException>(() => catalog.BindDecisionContextEvidence(request with {
            DecisionContextEvidence = null }, original, [selected]));
        Assert.Throws<InvalidDataException>(() => catalog.BindDecisionContextEvidence(request with {
            DecisionContextEvidence = choice.Reference! with { ManifestSha256 = new string('0', 64) } }, original, [selected]));
        var restored = JsonSerializer.Deserialize<DecisionContextEvidenceSummary>(JsonSerializer.Serialize(binding.Summary))!;
        Assert.Equal(binding.Summary.Reference, restored.Reference);
        Assert.Equal(binding.Summary.DependencyGraphSha256, restored.DependencyGraphSha256);
        Assert.Equal(HistoricalDiagnosticNewsTests.Archive.Capture, restored.HistoricalNewsDiagnostic!.ActualReceivedFromUtc);
        Assert.Contains("Original historical availability and classifier accuracy are unproved. Research only.",
            DecisionContextEvidencePresentation.Lines(restored));
        Assert.Contains(DecisionContextEvidencePresentation.Lines(restored), line => line.Contains("provider update + 24h"));
    }

    [Fact]
    // Incomplete query evidence remains visible and the normal decision path will reject unknowns instead of claiming no news.
    public void UnknownQueryCoverageIsDisplayedWithoutInventingAClassifierPass()
    {
        using var fixture = new HistoricalDiagnosticNewsTests.Archive();
        fixture.News("empty", []);
        Publish(fixture, fixture.Provider(withSemantics: false).Manifest);
        var choice = Assert.Single(Catalog(fixture).GetDecisionContextEvidenceChoices());
        Assert.True(choice.Available);
        Assert.NotEmpty(choice.Summary!.UnknownReasons);
        Assert.Empty(choice.Summary.EvaluationSha256s);
        Assert.Contains(DecisionContextEvidencePresentation.Lines(choice.Summary), line => line.StartsWith("Unknown reasons:") && !line.EndsWith("None reported"));
    }

    [Fact]
    // Existing strict rules cannot silently lose their provider when a diagnostic package is selected.
    public async Task DiagnosticSelectionRejectsStrictNewsRecipe()
    {
        using var fixture = new HistoricalDiagnosticNewsTests.Archive();
        fixture.News("empty", []);
        Publish(fixture, fixture.Provider().Manifest);
        var catalog = Catalog(fixture);
        var choice = Assert.Single(catalog.GetDecisionContextEvidenceChoices());
        var price = (await catalog.GetStrategiesAsync(StrategySelectionMode.Backtest))
            .Single(item => item.Identity.StrategyId == "technical.recovery-sma5-exit");
        var strict = price with { Definition = price.Definition with { EntryRules = price.Definition.EntryRules with { RequirePositiveNews = true } } };
        Assert.False(TradingFlow.Web.TradingApiV1Endpoints.UsesNews(price.Definition));
        Assert.True(TradingFlow.Web.TradingApiV1Endpoints.UsesNews(strict.Definition));
        Assert.Throws<InvalidOperationException>(() => catalog.BindDecisionContextEvidence(Request(price.Identity, choice.Reference),
            new EngineConfig("channels", 1, 20, 260, 120, false), [strict]));
    }

    [Theory]
    [InlineData("recovery-sma5-news-evidence.bt-v1.yaml", "evidence_checked")]
    [InlineData("recovery-sma5-news-avoidance.bt-v1.yaml", "avoid_recent_company_news")]
    // Generated durable strategy snapshots retain the registered treatment through the normal YAML writer/parser.
    public void StrategySnapshotRoundTripRetainsDiagnosticTreatment(string name, string treatment)
    {
        using var fixture = new HistoricalDiagnosticNewsTests.Archive();
        var reader = new SimpleYamlReader();
        var original = reader.ReadStrategy(Path.Combine(TestRepository.FindRoot(), "configs", "backtest", "strategies", name));
        var snapshot = Path.Combine(fixture.Root, "strategy.yaml");
        File.WriteAllText(snapshot, RunConfigWriter.SerializeStrategyYaml(original));
        var restored = reader.ReadStrategy(snapshot);
        Assert.Equal(treatment, restored.EntryRules.HistoricalNewsDiagnostic);
        Assert.True(TradingFlow.Web.TradingApiV1Endpoints.UsesNews(restored));
        Assert.Equal(original.ExitRules, restored.ExitRules);
        Assert.Equal(original.Execution, restored.Execution);
    }

    // Publishes only synthetic immutable evidence into the standard server catalog location.
    private static void Publish(HistoricalDiagnosticNewsTests.Archive fixture, HistoricalDiagnosticNewsManifest manifest) =>
        HistoricalDiagnosticNewsManifestStore.PublishExclusive(Path.Combine(fixture.Root, "data", "research", "evidence", "historical-news-diagnostics"), manifest);

    // Shares real catalog and authorization components with an isolated operator data root.
    private static ConfigCatalogService Catalog(HistoricalDiagnosticNewsTests.Archive fixture)
    {
        var repo = TestRepository.FindRoot();
        var reader = new SimpleYamlReader();
        var artifacts = new StrategyArtifactCatalog(repo, Path.Combine(repo, "configs", "strategy-catalog.json"), reader);
        var store = new StrategyExperimentArtifactStore(Path.Combine(fixture.Root, "experiments"), artifacts, reader, AtomicFileArtifactWriter.Instance);
        return new(new ProjectPaths(fixture.Root), reader, artifacts, store, new StrategyAuthorizationTestRegistry());
    }

    // Sends server identities and hashes only, never a client-controlled physical evidence path.
    private static StartBacktestRequest Request(StrategyArtifactIdentity identity, DecisionContextEvidenceReference? reference) =>
        new("1.0", "synthetic-news-catalog", Guid.NewGuid(), [new(identity.StrategyId, identity.SemanticVersion, identity.ContentSha256)],
            DateTimeOffset.Parse("2026-08-17T00:00:00Z"), DateTimeOffset.Parse("2026-08-28T00:00:00Z"),
            10000, 1, 25, 4, "use_cache", "full", "synthetic-news-catalog", DecisionContextEvidence: reference);
}
