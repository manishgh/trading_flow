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
                // Active catalog contains only the 10 approved swing research artifacts (5 long + 5 short); retired/set-aside artifacts live under RetiredStrategyFixtures.
                "technical.bollinger-squeeze-breakdown@1.0.0",
                "technical.bollinger-squeeze-breakout@1.0.0",
                "technical.ema21-rejection-short@1.0.0",
                "technical.ema21-rejection-short@1.1.0",
                "technical.ema21-trend-pullback@1.0.0",
                "technical.ema21-trend-pullback@1.1.0",
                "technical.news-orb-breakdown@1.0.0",
                "technical.news-orb-swing@1.0.0",
                "technical.parabolic-exhaustion-short@1.0.0",
                "technical.rubber-band-reclaim@1.0.0"
            },
            snapshot.ExecutableArtifacts
                .Where(artifact => !IsTemporaryVariant(artifact.Identity))
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
        // The same-day opening-range versions stay set aside; all ten approved swing research artifacts (5 long + 5 short)
        // are enabled for research backtests (still research-only, never authorized for paper/live without explicit promotion).
        Assert.DoesNotContain(artifacts, item => item.Identity.StrategyId == "technical.opening-range-breakout");
        Assert.Equal(
            new[]
            {
                "technical.bollinger-squeeze-breakdown@1.0.0",
                "technical.bollinger-squeeze-breakout@1.0.0",
                "technical.ema21-rejection-short@1.0.0",
                "technical.ema21-rejection-short@1.1.0",
                "technical.ema21-trend-pullback@1.0.0",
                "technical.ema21-trend-pullback@1.1.0",
                "technical.news-orb-breakdown@1.0.0",
                "technical.news-orb-swing@1.0.0",
                "technical.parabolic-exhaustion-short@1.0.0",
                "technical.rubber-band-reclaim@1.0.0"
            },
            artifacts
                .Where(item => !IsTemporaryVariant(item.Identity))
                .Select(item => $"{item.Identity.StrategyId}@{item.Identity.SemanticVersion}")
                .Order(StringComparer.Ordinal));
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

    // A temporary strategy variant (pre-release version such as 1.0.0-gate-off.1, user rule of 23 November 2026) is a
    // research-only copy that names the exact catalog version it was copied from; it is never offered to paper or live.
    [Fact]
    public void TemporaryVariants_AreResearchOnlyCopiesOfAnExactCatalogVersion()
    {
        var catalog = CreateCatalog();
        var artifacts = catalog.GetSnapshot().ExecutableArtifacts;
        var released = artifacts.Where(artifact => !IsTemporaryVariant(artifact.Identity)).Select(artifact => artifact.Identity).ToHashSet();

        Assert.All(artifacts.Where(artifact => IsTemporaryVariant(artifact.Identity)), variant =>
        {
            Assert.Equal(StrategyLifecycleState.Research, variant.EffectiveLifecycle);
            Assert.NotNull(variant.DerivedFromIdentity);
            Assert.Contains(variant.DerivedFromIdentity, released);
            Assert.Equal(variant.DerivedFromIdentity!.StrategyId, variant.Identity.StrategyId);
            Assert.StartsWith(variant.DerivedFromIdentity.SemanticVersion + "-", variant.Identity.SemanticVersion, StringComparison.Ordinal);
        });
        Assert.Empty(catalog.GetSelectable(StrategySelectionMode.RunPaperShadow));
        Assert.Empty(catalog.GetSelectable(StrategySelectionMode.RunLive));
    }

    /// <summary>True for a pre-release semantic version, which marks a temporary strategy variant under comparison.</summary>
    internal static bool IsTemporaryVariant(StrategyArtifactIdentity identity) =>
        identity.SemanticVersion.Split('+')[0].Contains('-', StringComparison.Ordinal);

    private static StrategyArtifactCatalog CreateCatalog()
    {
        var root = TestRepository.FindRoot();
        return new StrategyArtifactCatalog(
            root,
            Path.Combine(root, "configs", "strategy-catalog.json"),
            new SimpleYamlReader());
    }
}
