using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;

namespace TradingFlow.Tests;

public sealed class StrategyArtifactCatalogTests
{
    [Fact]
    // The bootstrap exposes only the exact reviewed Research recipes; registration never grants execution authority.
    public void Bootstrap_ClassifiesEveryStrategyExactlyOnceAndStartsFailClosed()
    {
        var catalog = CreateCatalog();

        var snapshot = catalog.GetSnapshot();

        Assert.Equal(
            new[]
            {
                // The single-stock swing artifacts were retired on 13 November 2026 (test-owned copies: RetiredStrategyFixtures);
                // the ETF trend core and its buy-and-hold control were added the same day.
                "etf.buy-and-hold-control@1.0.0",
                "etf.rotation-12-1@1.0.0",
                "etf.trend-10m@1.0.0",
                "technical.opening-range-breakout@1.2.0",
                "technical.opening-range-breakout@2.0.0",
                "technical.opening-range-breakout@2.1.0"
            },
            snapshot.ExecutableArtifacts
                .Select(artifact => $"{artifact.Identity.StrategyId}@{artifact.Identity.SemanticVersion}")
                .Order(StringComparer.Ordinal));
        Assert.Empty(snapshot.ArchivedArtifacts);
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

        Assert.DoesNotContain(artifacts, item =>
            item.Identity.StrategyId == "swing.minervini-vcp" &&
            item.Identity.SemanticVersion == "10.0.0");
        Assert.Contains(artifacts, item =>
            item.Identity.StrategyId == "technical.opening-range-breakout" &&
            item.Identity.SemanticVersion == "1.2.0");
        Assert.DoesNotContain(artifacts, item => item.Identity.StrategyId.StartsWith("swing.", StringComparison.Ordinal));
        Assert.DoesNotContain(artifacts, item =>
            item.Identity.StrategyId == "swing.catalyst-drift");
    }

    [Fact]
    public void ModeFiltering_RequiresExactAuthorizedIdentityAndHonorsSuspension()
    {
        // Paper selection needs a swing artifact; the retired test-owned copies supply one.
        var catalog = RetiredStrategyFixtures.Catalog();
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
    public void CanonicalHash_IsStableAcrossIndependentLoads()
    {
        var first = CreateCatalog().GetSelectable(StrategySelectionMode.Backtest);
        var second = CreateCatalog().GetSelectable(StrategySelectionMode.Backtest);

        Assert.Equal(
            first.Select(item => item.Identity),
            second.Select(item => item.Identity));
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
    public void AuthorizationSelection_PreservesExactArtifactIdentity()
    {
        // Paper selection needs a swing artifact; the retired test-owned copies supply one.
        var catalog = RetiredStrategyFixtures.Catalog();
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
