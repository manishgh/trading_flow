using System.Text;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;

namespace TradingFlow.Tests;

/// <summary>
/// Pins the three research artifacts kept on 7 November 2026 to the identities recorded in
/// docs/research/retired-strategy-artifacts-2026-11-07.md, so removing other strategies or their code
/// can never silently change what a kept strategy is.
/// </summary>
public sealed class KeptStrategyIdentityTests
{
    // Content hashes recorded in the retirement ledger (7 Nov) and for the published versions (8 Nov); any change needs a new semantic version.
    [Theory]
    [InlineData("technical.opening-range-breakout", "1.2.0", "bc73189a1c26ae2741402c2ad84a3a3e88aec709d43253590f7c7fb3c6fdbcc4", null)]
    [InlineData("swing.connors-rsi2", "7.0.0", "a16f4e27ae3e85d19f537d57ac0189a5058d8de0b0cffa39ce993c0220a70e8c", null)]
    [InlineData("technical.recovery-sma5-news-avoidance", "1.0.0", "47dd423636627dfd8a482291b3e905f56c6f278ac80a354afa33fc8acf6ff69d",
        "technical.recovery-sma5-exit")]
    [InlineData("swing.connors-rsi2", "8.0.0", "f5ee0ef58473b0e749edcb4894d9aa31ac0786e42ffb313ca369f57b80dff5a3", "swing.connors-rsi2")]
    [InlineData("swing.snapback", "1.0.0", "cda6eb1c9031d1b9e9845afa243a12179d410518a21ebb4631e73cdf1b69713e", null)]
    [InlineData("technical.opening-range-breakout", "2.0.0", "83b58ddd3b6b2017c717066e98ce92b513537b9aa467283b32f72375ded14429", "technical.opening-range-breakout")]
    public void KeptArtifacts_KeepTheirRecordedCanonicalHashes(string id, string version, string hash, string? parent)
    {
        var artifact = Assert.Single(CreateCatalog().GetSnapshot().ExecutableArtifacts,
            artifact => artifact.Identity.StrategyId == id && artifact.Identity.SemanticVersion == version);
        Assert.Equal(new StrategyArtifactIdentity(id, version, hash), artifact.Identity);
        Assert.Equal(parent, artifact.DerivedFromIdentity?.StrategyId);
    }

    // Optional lineage survives ordinary artifact serialization, and further derivation binds to its immediate source.
    [Fact]
    public void Lineage_RoundTripsAndFurtherDerivationUsesExactSourceIdentity()
    {
        var catalog = CreateCatalog();
        var artifacts = catalog.GetSelectable(StrategySelectionMode.Backtest);
        var derivedSource = Assert.Single(artifacts, artifact => artifact.Identity.StrategyId == "technical.recovery-sma5-news-avoidance");
        var replay = EvidenceCanonicalJson.Deserialize<StrategyArtifact>(EvidenceCanonicalJson.SerializeToUtf8Bytes(derivedSource));
        Assert.Equal(derivedSource, replay);
        var derived = catalog.CreateDerivedArtifact(derivedSource, derivedSource.ResolvedStrategy, "1.0.1-research");
        Assert.Equal(derivedSource.Identity, derived.DerivedFromIdentity);
        Assert.Equal(derivedSource.Identity.ContentSha256, derived.DerivedFromContentSha256);
        var original = Assert.Single(artifacts, artifact => artifact.Identity.StrategyId == "swing.connors-rsi2" && artifact.Identity.SemanticVersion == "7.0.0");
        Assert.DoesNotContain("derivedFromIdentity", Encoding.UTF8.GetString(
            EvidenceCanonicalJson.SerializeToUtf8Bytes(original)), StringComparison.Ordinal);
    }

    // Resolve only repository-owned configuration; tests never query providers or start a portfolio run.
    private static StrategyArtifactCatalog CreateCatalog()
    {
        var root = TestRepository.FindRoot();
        return new StrategyArtifactCatalog(root, Path.Combine(root, "configs", "strategy-catalog.json"), new SimpleYamlReader());
    }
}
