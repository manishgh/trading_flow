using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;

namespace TradingFlow.Tests;

public sealed class StrategyArtifactCatalogTests
{
    [Fact]
    public void Bootstrap_ClassifiesEveryStrategyExactlyOnceAndStartsFailClosed()
    {
        var catalog = CreateCatalog();

        var snapshot = catalog.GetSnapshot();

        Assert.Equal(2, snapshot.ExecutableArtifacts.Count);
        Assert.Equal(13, snapshot.ArchivedArtifacts.Count);
        Assert.All(
            snapshot.ExecutableArtifacts,
            artifact =>
            {
                Assert.Equal(StrategyArtifactDisposition.Research, artifact.Disposition);
                Assert.Equal(StrategyLifecycleState.Research, artifact.EffectiveLifecycle);
            });
        Assert.Empty(catalog.GetSelectable(StrategySelectionMode.RunPaperExperiment));
        Assert.Empty(catalog.GetSelectable(StrategySelectionMode.RunPaperShadow));
        Assert.Empty(catalog.GetSelectable(StrategySelectionMode.RunLive));
    }

    [Fact]
    public void Bootstrap_UsesReviewedCanonicalResearchControls()
    {
        var artifacts = CreateCatalog().GetSelectable(StrategySelectionMode.Backtest);

        Assert.Contains(artifacts, item =>
            item.Identity.StrategyId == "swing.minervini-vcp" &&
            item.Identity.SemanticVersion == "4.0.0");
        Assert.Contains(artifacts, item =>
            item.Identity.StrategyId == "swing.connors-rsi2" &&
            item.Identity.SemanticVersion == "3.0.0");
        Assert.DoesNotContain(artifacts, item =>
            item.Identity.StrategyId == "swing.catalyst-drift");
    }

    [Fact]
    public void ModeFiltering_RequiresExactAuthorizedIdentityAndHonorsSuspension()
    {
        var catalog = CreateCatalog();
        var identity = catalog.GetSelectable(StrategySelectionMode.Backtest)[0].Identity;

        Assert.Single(catalog.GetSelectable(
            StrategySelectionMode.RunPaperShadow,
            authorizedPaperShadow: [identity]));
        Assert.Empty(catalog.GetSelectable(
            StrategySelectionMode.RunPaperShadow,
            authorizedPaperShadow: [identity],
            suspended: [identity]));
        Assert.Empty(catalog.GetSelectable(
            StrategySelectionMode.RunLive,
            authorizedPaperShadow: [identity]));
        Assert.Single(catalog.GetSelectable(
            StrategySelectionMode.RunLive,
            authorizedValidated: [identity]));
    }

    [Fact]
    public void ArchivedArtifacts_RemainStrictlyParseableDailySwingEvidence()
    {
        var root = TestRepository.FindRoot();
        var archived = CreateCatalog().GetSnapshot().ArchivedArtifacts;
        var reader = new SimpleYamlReader();

        Assert.NotEmpty(archived);
        Assert.All(archived, artifact =>
            Assert.Equal("1d", reader.ReadStrategy(Path.Combine(root, artifact.SourcePath)).Timeframe));
    }

    [Fact]
    public void CanonicalHash_IsStableAcrossIndependentLoads()
    {
        var first = CreateCatalog().GetSelectable(StrategySelectionMode.Backtest);
        var second = CreateCatalog().GetSelectable(StrategySelectionMode.Backtest);

        Assert.Equal(
            first.Select(item => item.Identity),
            second.Select(item => item.Identity));
    }

    [Fact]
    public void ArchivedArtifacts_HaveStableGloballyUniqueCanonicalIdentities()
    {
        var first = CreateCatalog().GetSnapshot().ArchivedArtifacts;
        var second = CreateCatalog().GetSnapshot().ArchivedArtifacts;

        Assert.All(first, artifact =>
        {
            Assert.False(String.IsNullOrWhiteSpace(artifact.Identity.StrategyId));
            Assert.Matches("^[0-9a-f]{64}$", artifact.Identity.ContentSha256);
        });
        Assert.Equal(first.Count, first.Select(artifact => artifact.Identity).Distinct().Count());
        Assert.Equal(
            first.Select(artifact => artifact.Identity),
            second.Select(artifact => artifact.Identity));
    }

    [Fact]
    public void ChangedResolvedContent_RequiresANewSemanticVersion()
    {
        var catalog = CreateCatalog();
        var source = catalog.GetSelectable(StrategySelectionMode.Backtest)[0];
        var changed = source.ResolvedStrategy with
        {
            EntryRules = source.ResolvedStrategy.EntryRules with
            {
                MinVolumeSpike = source.ResolvedStrategy.EntryRules.MinVolumeSpike + 0.25m
            }
        };

        Assert.Throws<InvalidOperationException>(() => catalog.CreateDerivedArtifact(
            source,
            changed,
            source.Identity.SemanticVersion));

        var derived = catalog.CreateDerivedArtifact(
            source,
            changed,
            "1.0.1-research");
        Assert.Equal(source.Identity.ContentSha256, derived.DerivedFromContentSha256);
        Assert.NotEqual(source.Identity.ContentSha256, derived.Identity.ContentSha256);
        Assert.Equal(StrategyLifecycleState.Research, derived.EffectiveLifecycle);
    }

    [Fact]
    public void DerivedContent_CannotReuseVersionOccupiedByAnotherBootstrapArtifact()
    {
        var catalog = CreateCatalog();
        var source = Assert.Single(
            catalog.GetSelectable(StrategySelectionMode.Backtest),
            artifact => artifact.Identity.StrategyId == "swing.minervini-vcp");
        var changed = source.ResolvedStrategy with
        {
            EntryRules = source.ResolvedStrategy.EntryRules with
            {
                MinVolumeSpike = source.ResolvedStrategy.EntryRules.MinVolumeSpike + 0.25m
            }
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            catalog.CreateDerivedArtifact(source, changed, "2.0.0"));

        Assert.Contains("bootstrap content", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AuthorizationSelection_PreservesExactArtifactIdentity()
    {
        var catalog = CreateCatalog();
        var source = catalog.GetSelectable(StrategySelectionMode.Backtest)[0];
        var authorized = Assert.Single(catalog.GetSelectable(
            StrategySelectionMode.RunPaperShadow,
            authorizedPaperShadow: [source.Identity]));

        Assert.Equal(source.Identity, authorized.Identity);
        Assert.Equal(StrategyArtifactDisposition.Research, authorized.Disposition);
    }

    private static StrategyArtifactCatalog CreateCatalog()
    {
        var root = TestRepository.FindRoot();
        return new StrategyArtifactCatalog(
            root,
            Path.Combine(root, "configs", "strategy-catalog.json"),
            new SimpleYamlReader());
    }
}
