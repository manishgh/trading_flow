using System.Text;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;

namespace TradingFlow.Tests;

/// <summary>
/// Pins the catalog's opening-range breakout versions, and the test-owned copies of the strategies retired on 13 November
/// 2026 (RetiredStrategyFixtures), to the identities recorded in docs/research/retired-strategy-artifacts-2026-11-07.md and
/// -2026-11-13.md, so removing strategies or their code can never silently change what a kept or retired version was.
/// </summary>
public sealed class KeptStrategyIdentityTests
{
    // Content hashes recorded in the retirement ledger (7 Nov) and for the published versions (8 Nov); any change needs a new semantic version.
    [Theory]
    [InlineData("technical.opening-range-breakout", "1.2.0", "bc73189a1c26ae2741402c2ad84a3a3e88aec709d43253590f7c7fb3c6fdbcc4", null)]
    [InlineData("swing.connors-rsi2", "7.0.0", "a16f4e27ae3e85d19f537d57ac0189a5058d8de0b0cffa39ce993c0220a70e8c", null)]
    [InlineData("technical.recovery-sma5-news-avoidance", "1.0.0", "47dd423636627dfd8a482291b3e905f56c6f278ac80a354afa33fc8acf6ff69d",
        "technical.recovery-sma5-exit")]
    [InlineData("swing.connors-rsi2", "9.0.0", "882e2a32d00ccdcc17737aa05d9ff466ed714b0be86f5da04b9b570aa86884c5", "swing.connors-rsi2")]
    [InlineData("swing.snapback", "2.0.0", "7bd1719285bdbb0e50cb1b94397a0353eb2f221e554b64b183a429096f7fd983", null)]
    [InlineData("technical.opening-range-breakout", "2.0.0", "83b58ddd3b6b2017c717066e98ce92b513537b9aa467283b32f72375ded14429", "technical.opening-range-breakout")]
    [InlineData("technical.opening-range-breakout", "2.1.0", "b71cf7b8a017004937efb28d6d25c57d931061c522a7444067d2463e6ac33750", "technical.opening-range-breakout")]
    [InlineData("swing.connors-rsi2-short", "2.0.0", "323989e84141d4338ca45d2acb7fb6f75a236aba4b2c53b94187d1bd27ba85ea", null)]
    [InlineData("swing.snapback-short", "2.0.0", "7deb6a30ddaaaa1844e0b9b2e2762eef9eadb8e5cf04839e5bd8cd412e11ed8e", null)]
    // As-traded copies of the Connors rules for whole-market research screens (11 Nov 2026).
    [InlineData("swing.connors-rsi2", "9.1.0", "0d052799b949ee9c76f0de44b1825087edc4eda4edb288c83ec8a0492674b258", "swing.connors-rsi2")]
    [InlineData("swing.connors-rsi2", "7.1.0", "f3dc47e30b4eee880b9d615a4c8c02a1db3fecede713912f2d482c3303e6fd11", "swing.connors-rsi2")]
    [InlineData("swing.connors-rsi2-short", "2.1.0", "f3e7c6c7c4f25e2dec7aa5135b211628dff767b0067d8d49c54dcca4dac51d32", "swing.connors-rsi2-short")]
    // Decision-hour copies for the one-year hourly-entry test (12 Nov 2026).
    [InlineData("swing.connors-rsi2", "9.2.0", "5938933c5800110d3574b4d5d0a067e70ba0ede4e86cb620d09ccb64c4568534", "swing.connors-rsi2")]
    [InlineData("swing.connors-rsi2", "7.2.0", "87bfbc48a88cf563b04c22300a4f9283b5635daac3e16979d7414243505cb067", "swing.connors-rsi2")]
    [InlineData("swing.connors-rsi2-short", "2.2.0", "34a4438d39a56020ba11ac08b6364cd003885ca681888025c1f4cbc26aa27a3e", "swing.connors-rsi2-short")]
    // ETF trend core and its buy-and-hold comparison control (13 Nov 2026).
    [InlineData("etf.trend-10m", "1.0.0", "e998f9ccd93d4630c21123e8d89826c5a02c1e93e601793d3f96333e8ec60b08", null)]
    [InlineData("etf.buy-and-hold-control", "1.0.0", "b39db129ba80d31c5dc9b03ae20b52453dbee90f8e6b12390436699147781ec5", null)]
    public void KeptArtifacts_KeepTheirRecordedCanonicalHashes(string id, string version, string hash, string? parent)
    {
        var artifact = Assert.Single(AllArtifacts(),
            artifact => artifact.Identity.StrategyId == id && artifact.Identity.SemanticVersion == version);
        Assert.Equal(new StrategyArtifactIdentity(id, version, hash), artifact.Identity);
        Assert.Equal(parent, artifact.DerivedFromIdentity?.StrategyId);
    }

    // The as-traded copies keep their parents' trading rules (only the file identity and name differ), accept raw Alpaca SIP
    // daily bars through their admission profile, and take same-day candidates lowest RSI(2) first (shorts: highest).
    [Theory]
    [InlineData("swing.connors-rsi2", "9.1.0", "9.0.0", "rsi2_ascending")]
    [InlineData("swing.connors-rsi2", "7.1.0", "7.0.0", "rsi2_ascending")]
    [InlineData("swing.connors-rsi2-short", "2.1.0", "2.0.0", "rsi2_descending")]
    public void AsTradedCopies_KeepTheRulesAndAcceptRawDailyBars(string id, string version, string parentVersion, string rankMode)
    {
        var artifacts = RetiredStrategyFixtures.Catalog().GetSnapshot().ExecutableArtifacts;
        var copy = Assert.Single(artifacts, artifact => artifact.Identity.StrategyId == id && artifact.Identity.SemanticVersion == version);
        var parent = Assert.Single(artifacts, artifact => artifact.Identity.StrategyId == id && artifact.Identity.SemanticVersion == parentVersion);

        Assert.Equal(parent.Identity, copy.DerivedFromIdentity);
        Assert.Equivalent(parent.ResolvedStrategy with { StrategyId = "", StrategyName = "" },
            copy.ResolvedStrategy with { StrategyId = "", StrategyName = "" }, strict: true);
        Assert.Equal(rankMode, copy.ResolvedStrategy.EntryRules.PortfolioRankMode);
        Assert.Equal(ExecutionRules.KeptGapAsTradedTimingPolicy, copy.AdmissionProfile.ExecutionTimingPolicy);
        Assert.Equal("raw", TradingFlow.Engine.Indicators.MarketEvidenceOperationalRules.ForExecutionTimingPolicy(
            copy.AdmissionProfile.ExecutionTimingPolicy).RequiredAdjustmentPolicy);
        Assert.Equal("all", TradingFlow.Engine.Indicators.MarketEvidenceOperationalRules.ForExecutionTimingPolicy(
            parent.AdmissionProfile.ExecutionTimingPolicy).RequiredAdjustmentPolicy);
    }

    // The decision-hour copies keep their as-traded parents' trading rules and bind the decision-hour timing policy, which
    // reads raw (as-traded) bars like their parents and counts as an as-traded research-screen timing.
    [Theory]
    [InlineData("swing.connors-rsi2", "9.2.0", "9.1.0")]
    [InlineData("swing.connors-rsi2", "7.2.0", "7.1.0")]
    [InlineData("swing.connors-rsi2-short", "2.2.0", "2.1.0")]
    public void DecisionHourCopies_KeepTheRulesAndBindTheDecisionHourPolicy(string id, string version, string parentVersion)
    {
        var artifacts = RetiredStrategyFixtures.Catalog().GetSnapshot().ExecutableArtifacts;
        var copy = Assert.Single(artifacts, artifact => artifact.Identity.StrategyId == id && artifact.Identity.SemanticVersion == version);
        var parent = Assert.Single(artifacts, artifact => artifact.Identity.StrategyId == id && artifact.Identity.SemanticVersion == parentVersion);

        Assert.Equal(parent.Identity, copy.DerivedFromIdentity);
        Assert.Equivalent(parent.ResolvedStrategy with { StrategyId = "", StrategyName = "" },
            copy.ResolvedStrategy with { StrategyId = "", StrategyName = "" }, strict: true);
        Assert.Equal(ExecutionRules.DecisionHourAsTradedTimingPolicy, copy.AdmissionProfile.ExecutionTimingPolicy);
        Assert.Equal("raw", TradingFlow.Engine.Indicators.MarketEvidenceOperationalRules.ForExecutionTimingPolicy(
            copy.AdmissionProfile.ExecutionTimingPolicy).RequiredAdjustmentPolicy);
        Assert.True(ExecutionRules.IsAsTradedResearchTiming(copy.AdmissionProfile.ExecutionTimingPolicy));
    }

    // Optional lineage survives ordinary artifact serialization, and further derivation binds to its immediate source.
    [Fact]
    public void Lineage_RoundTripsAndFurtherDerivationUsesExactSourceIdentity()
    {
        var catalog = RetiredStrategyFixtures.Catalog();
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

    // Every run writes a snapshot of each strategy and checks it against the catalog identity before starting, so the
    // snapshot writer must keep every setting of every catalog strategy (ORB 2.1.0 runs failed at start on 11 November
    // 2026 because two settings were dropped).
    [Fact]
    public void EveryCatalogStrategy_SurvivesTheRunSnapshotRoundTrip()
    {
        var reader = new SimpleYamlReader();
        foreach (var artifact in AllArtifacts())
        {
            var path = Path.Combine(Path.GetTempPath(), "tf-snapshot-roundtrip-" + Guid.NewGuid().ToString("N") + ".yaml");
            try
            {
                File.WriteAllText(path, TradingFlow.Web.Services.RunConfigWriter.SerializeStrategyYaml(artifact.ResolvedStrategy));
                Assert.Equivalent(artifact.ResolvedStrategy, reader.ReadStrategy(path), strict: true);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    // Resolve only repository-owned configuration; tests never query providers or start a portfolio run.
    private static StrategyArtifactCatalog CreateCatalog()
    {
        var root = TestRepository.FindRoot();
        return new StrategyArtifactCatalog(root, Path.Combine(root, "configs", "strategy-catalog.json"), new SimpleYamlReader());
    }

    // The real catalog's artifacts followed by the retired test-owned copies.
    private static IReadOnlyList<StrategyArtifact> AllArtifacts() =>
        [.. CreateCatalog().GetSnapshot().ExecutableArtifacts, .. RetiredStrategyFixtures.Catalog().GetSnapshot().ExecutableArtifacts];
}
