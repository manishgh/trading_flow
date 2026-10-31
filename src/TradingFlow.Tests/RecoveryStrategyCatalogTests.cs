using System.Security.Cryptography;
using System.Text;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using Xunit.Abstractions;

namespace TradingFlow.Tests;

public sealed class RecoveryStrategyCatalogTests(ITestOutputHelper output)
{
    private const string ParentHash = "c151cb4becae9f19b0aeeab7517c884401f14d716fbc404e78bf8dfa4b8396bb";
    private static readonly StrategyArtifactIdentity ParentIdentity = new(
        "technical.family03.mr_a0b1", "1.0.0", ParentHash);

    [Theory]
    [InlineData("swing.connors-rsi2", "6.0.0", "f56ef8e2505d7e6118bfb7c534c5c71ad03936da4cbf13b5e13e5566e6876df0")]
    // Freeze the pre-registration catalog identities retained in the October 21 catalog response.
    public void OriginalResearchControls_KeepTheirPublishedCanonicalHashes(string id, string version, string hash)
    {
        var artifact = Assert.Single(CreateCatalog().GetSnapshot().ExecutableArtifacts,
            artifact => artifact.Identity.StrategyId == id && artifact.Identity.SemanticVersion == version);
        Assert.Equal(new StrategyArtifactIdentity(id, version, hash), artifact.Identity);
        Assert.Null(artifact.DerivedFromIdentity);
        Assert.Null(artifact.DerivedFromContentSha256);
    }

    [Fact]
    // Compare every resolved field after removing descriptive identity fields and the one approved treatment.
    public void PricePair_DiffersOnlyBySma5ExitAndDescriptiveIdentity()
    {
        var (control, treatment) = LoadPair();
        Assert.False(control.ResolvedStrategy.ExitRules.ExitOnCloseAboveSma5);
        Assert.True(treatment.ResolvedStrategy.ExitRules.ExitOnCloseAboveSma5);
        Assert.Equal(control.ResolvedStrategy, treatment.ResolvedStrategy with
        {
            StrategyId = control.ResolvedStrategy.StrategyId,
            StrategyName = control.ResolvedStrategy.StrategyName,
            ExitRules = treatment.ResolvedStrategy.ExitRules with { ExitOnCloseAboveSma5 = false }
        });
        Assert.Equal(control.AdmissionProfile, treatment.AdmissionProfile);
        Assert.NotEqual(control.Identity.ContentSha256, treatment.Identity.ContentSha256);
        output.WriteLine($"{control.Identity.StrategyId}@{control.Identity.SemanticVersion}: {control.Identity.ContentSha256}");
        output.WriteLine($"{treatment.Identity.StrategyId}@{treatment.Identity.SemanticVersion}: {treatment.Identity.ContentSha256}");
    }

    [Fact]
    // Preserve the exact historical YAML and canonical parent while proving no other resolved rule changed.
    public void PricePair_PreservesEveryParentRuleUnderNewNativeCutoffProfile()
    {
        var fixturePath = Path.Combine(TestRepository.FindRoot(), "src", "TradingFlow.Tests", "Fixtures",
            "Strategies", "recovery-family03-mr-a0b1.parent.yaml");
        Assert.Equal("0d2efb9d292db0499ac1d05b636327c0c7d5bad29e0f33e6751d148b74ae48ce",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixturePath))).ToLowerInvariant());
        var parent = new SimpleYamlReader().ReadStrategy(fixturePath);
        var (control, treatment) = LoadPair();
        var historicalProfile = treatment.AdmissionProfile with
        {
            ProfileId = "swing-family-cycle03",
            ProfileVersion = "3.0.0",
            ExecutionTimingPolicy = "completed-bar-precommitted-terminal-owned-v5"
        };
        Assert.Equal(ParentHash, EvidenceCanonicalJson.ComputeSha256(new CanonicalStrategyDocument(
            ParentIdentity.StrategyId, ParentIdentity.SemanticVersion, parent, historicalProfile)));
        Assert.All(new[] { control, treatment }, artifact =>
        {
            var normalized = artifact.ResolvedStrategy with
            {
                StrategyId = parent.StrategyId,
                StrategyName = parent.StrategyName,
                Version = parent.Version,
                ExitRules = artifact.ResolvedStrategy.ExitRules with { ExitOnCloseAboveSma5 = true }
            };
            Assert.Equal(parent, normalized);
            Assert.Equal(ParentIdentity, artifact.DerivedFromIdentity);
            Assert.Equal(ParentHash, artifact.DerivedFromContentSha256);
            Assert.NotEqual(ParentHash, artifact.Identity.ContentSha256);
        });
    }

    [Fact]
    // Registration describes the existing native policy and remains Research without paper/live grants or news rules.
    public void PricePair_BindsExactSharedProfileAndRemainsResearchOnly()
    {
        var catalog = CreateCatalog();
        var (control, treatment) = LoadPair();
        var expected = new StrategyAdmissionProfile(
            "native-recovery-open-cutoff-v1", "1.0.0", "simple-yaml-strict-v2",
            new StrategyIndicatorProfile("Skender.Stock.Indicators", "2.7.1", "TradingFlow.DerivedIndicators", "2.3.0"),
            "account-risk-budget-v1", "completed-bar-next-slice-market-exits-v3");
        Assert.All(new[] { control, treatment }, artifact =>
        {
            Assert.Equal(expected, artifact.AdmissionProfile);
            Assert.Equal("2.0.0", artifact.Identity.SemanticVersion);
            Assert.Equal(StrategyArtifactDisposition.Research, artifact.Disposition);
            Assert.Equal(StrategyLifecycleState.Research, artifact.EffectiveLifecycle);
            Assert.False(artifact.ResolvedStrategy.EntryRules.RequirePositiveNews);
            Assert.Null(artifact.ResolvedStrategy.EntryRules.DrawdownQualification);
            Assert.Contains(artifact, catalog.GetSelectable(StrategySelectionMode.Backtest));
        });
        Assert.Empty(catalog.GetSelectable(StrategySelectionMode.RunPaperExperiment));
        Assert.Empty(catalog.GetSelectable(StrategySelectionMode.RunPaperShadow));
        Assert.Empty(catalog.GetSelectable(StrategySelectionMode.RunLive));
    }

    [Fact]
    // Preserve optional lineage across ordinary artifact serialization and bind further derivation to its immediate source.
    public void Lineage_RoundTripsAndFurtherDerivationUsesExactSourceIdentity()
    {
        var catalog = CreateCatalog();
        var (control, _) = LoadPair();
        var encoded = EvidenceCanonicalJson.SerializeToUtf8Bytes(control);
        var replay = EvidenceCanonicalJson.Deserialize<StrategyArtifact>(encoded);
        Assert.Equal(control, replay);
        var derived = catalog.CreateDerivedArtifact(control, control.ResolvedStrategy, "2.0.1-research");
        Assert.Equal(control.Identity, derived.DerivedFromIdentity);
        Assert.Equal(control.Identity.ContentSha256, derived.DerivedFromContentSha256);
        var originalControl = Assert.Single(catalog.GetSnapshot().ExecutableArtifacts,
            artifact => artifact.Identity.StrategyId == "swing.connors-rsi2" && artifact.Identity.SemanticVersion == "6.0.0");
        Assert.DoesNotContain("derivedFromIdentity", Encoding.UTF8.GetString(
            EvidenceCanonicalJson.SerializeToUtf8Bytes(originalControl)), StringComparison.Ordinal);
    }

    // Load both approved recipes through the normal catalog so completeness, parsing and content hashes are exercised together.
    private static (StrategyArtifact Control, StrategyArtifact Treatment) LoadPair()
    {
        var artifacts = CreateCatalog().GetSelectable(StrategySelectionMode.Backtest);
        return (
            Assert.Single(artifacts, artifact => artifact.Identity.StrategyId == "technical.recovery-exit-control"),
            Assert.Single(artifacts, artifact => artifact.Identity.StrategyId == "technical.recovery-sma5-exit"));
    }

    // Resolve only repository-owned configuration; tests never query providers or start a portfolio run.
    private static StrategyArtifactCatalog CreateCatalog()
    {
        var root = TestRepository.FindRoot();
        return new StrategyArtifactCatalog(root, Path.Combine(root, "configs", "strategy-catalog.json"),
            new SimpleYamlReader());
    }
}
