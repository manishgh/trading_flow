using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class QualifiedRecoveryStrategyCatalogTests
{
    // All four recipes bind the approved band, news treatment and 3R target without other rule drift.
    [Theory]
    [InlineData("technical", null)]
    [InlineData("coverage", HistoricalCompanyNewsDiagnosticPolicy.CoverageOnly)]
    [InlineData("negative-veto", HistoricalCompanyNewsDiagnosticPolicy.NegativeVeto)]
    [InlineData("positive-required", HistoricalCompanyNewsDiagnosticPolicy.PositiveRequired)]
    public void QualifiedVariants_DifferOnlyByTheApprovedBandNewsTreatmentAndTarget(string suffix, string? treatment)
    {
        var root = TestRepository.FindRoot();
        var reader = new SimpleYamlReader();
        var catalog = new StrategyArtifactCatalog(root, Path.Combine(root, "configs", "strategy-catalog.json"), reader);
        var source = catalog.RequireExact(new("technical.recovery-sma5-exit", "2.0.0",
            "ad27d9ed0855760bf17fb4fec8ed9b97938dc3a1f52710097b6c63dc4a64c4a8"));
        var artifact = Assert.Single(catalog.GetSelectable(StrategySelectionMode.Backtest),
            item => item.Identity.StrategyId == "technical.recovery-sma5-qualified-" + suffix);
        Assert.Equal(StrategyArtifactDisposition.Research, artifact.Disposition);
        Assert.Equal(StrategyLifecycleState.Research, artifact.EffectiveLifecycle);
        Assert.Equal(source.Identity, artifact.DerivedFromIdentity);
        Assert.Equal(new DrawdownQualificationRules("gate", "predecline_atr_depth", 1m, 3m),
            artifact.ResolvedStrategy.EntryRules.DrawdownQualification);
        Assert.Equal(treatment, artifact.ResolvedStrategy.EntryRules.HistoricalNewsDiagnostic);
        Assert.Equal("rsi2_ascending", artifact.ResolvedStrategy.EntryRules.PortfolioRankMode);
        Assert.Equal("1.1.0", artifact.Identity.SemanticVersion);
        Assert.Equal(2.5m, source.ResolvedStrategy.ExitRules.TargetRMultiple);
        Assert.Equal(3.0m, artifact.ResolvedStrategy.ExitRules.TargetRMultiple);
        var restored = artifact.ResolvedStrategy with { StrategyId = source.ResolvedStrategy.StrategyId,
            StrategyName = source.ResolvedStrategy.StrategyName, Version = source.ResolvedStrategy.Version,
            EntryRules = artifact.ResolvedStrategy.EntryRules with { DrawdownQualification = null, HistoricalNewsDiagnostic = null },
            ExitRules = artifact.ResolvedStrategy.ExitRules with { TargetRMultiple = source.ResolvedStrategy.ExitRules.TargetRMultiple } };
        Assert.Equal(source.ResolvedStrategy, restored);
        Assert.Equal(source.AdmissionProfile, artifact.AdmissionProfile with {
            ProfileId = source.AdmissionProfile.ProfileId, HistoricalNewsDiagnosticPolicyId = null });
        if (treatment is null) Assert.Null(artifact.AdmissionProfile.HistoricalNewsDiagnosticPolicyId);
        else
        {
            Assert.Equal(HistoricalCompanyNewsDiagnosticPolicy.AdmissionPolicyId, artifact.AdmissionProfile.HistoricalNewsDiagnosticPolicyId);
            Assert.DoesNotContain(artifact, catalog.GetSelectable(StrategySelectionMode.DiagnosticReplay));
            Assert.DoesNotContain(artifact, catalog.GetSelectable(StrategySelectionMode.RunPaperShadow, authorizedPaperShadow: [artifact.Identity]));
            Assert.DoesNotContain(artifact, catalog.GetSelectable(StrategySelectionMode.RunLive, authorizedValidated: [artifact.Identity]));
        }
    }

    // Durable saved plans serialize the same gate/treatment through the existing writer/parser, without custom run configuration.
    [Theory]
    [InlineData("technical")]
    [InlineData("coverage")]
    [InlineData("negative-veto")]
    [InlineData("positive-required")]
    public void NormalStrategySnapshot_RoundTripsEveryApprovedTreatment(string suffix)
    {
        var reader = new SimpleYamlReader();
        var original = reader.ReadStrategy(Path.Combine(TestRepository.FindRoot(), "configs", "backtest", "strategies",
            "recovery-sma5-qualified-" + suffix + ".bt-v1.yaml"));
        var path = Path.Combine(Path.GetTempPath(), "tradingflow-qualified-recovery-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            File.WriteAllText(path, RunConfigWriter.SerializeStrategyYaml(original));
            Assert.Equal(original, reader.ReadStrategy(path));
        }
        finally
        {
            // This exact generated test-owned YAML contains no provider or research evidence.
            File.Delete(path);
        }
    }
}
