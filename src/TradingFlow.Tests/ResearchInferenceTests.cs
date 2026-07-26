using TradingFlow.Research.Statistics;

namespace TradingFlow.Tests;

public sealed class ResearchInferenceTests
{
    [Fact]
    public void BlockBootstrap_IsDeterministicAndContainsObservedMean()
    {
        double[] observations = [0.01, 0.02, -0.01, 0.03, 0.00, 0.02, 0.01, -0.005];

        var first = ResearchInference.BlockBootstrapMean(
            observations,
            blockLength: 3,
            replicates: 2_000,
            confidenceLevel: 0.95,
            seed: 1729);
        var second = ResearchInference.BlockBootstrapMean(
            observations,
            blockLength: 3,
            replicates: 2_000,
            confidenceLevel: 0.95,
            seed: 1729);

        Assert.Equal(first, second);
        Assert.InRange(first.Estimate, first.LowerBound, first.UpperBound);
        Assert.Equal("circular-moving-block-bootstrap-mean", first.Method);
    }

    [Fact]
    public void NeweyWestMean_ReportsFiniteHacInference()
    {
        double[] observations =
        [
            0.01, 0.012, 0.011, 0.009, 0.013, 0.012, 0.010, 0.014
        ];

        var result = ResearchInference.NeweyWestMean(observations, lag: 2);

        Assert.Equal(observations.Average(), result.Estimate, 12);
        Assert.True(result.StandardError > 0);
        Assert.InRange(result.TwoSidedPValue, 0, 1);
        Assert.Equal(2, result.Lag);
    }

    [Fact]
    public void HolmAdjustment_ControlsFamilyWiseErrorInStableOrder()
    {
        var adjusted = ResearchInference.HolmAdjust(
            new Dictionary<string, double>
            {
                ["b"] = 0.03,
                ["a"] = 0.01,
                ["c"] = 0.20
            },
            familyWiseAlpha: 0.05);

        Assert.Equal(["a", "b", "c"], adjusted.Select(item => item.HypothesisId));
        Assert.Equal(0.03, adjusted[0].AdjustedPValue, 12);
        Assert.Equal(0.06, adjusted[1].AdjustedPValue, 12);
        Assert.Equal(0.20, adjusted[2].AdjustedPValue, 12);
        Assert.True(adjusted[0].Rejected);
        Assert.False(adjusted[1].Rejected);
    }

    [Fact]
    public void SelectionBiasAudit_PenalizesMoreTrialsAndDeclaresItsLimitations()
    {
        var oneTrial = ResearchInference.DeflatedSharpeCompatibleAudit(
            observedSharpe: 1.0,
            observationCount: 120,
            independentTrialCount: 1,
            skewness: 0,
            kurtosis: 3);
        var fiftyTrials = ResearchInference.DeflatedSharpeCompatibleAudit(
            observedSharpe: 1.0,
            observationCount: 120,
            independentTrialCount: 50,
            skewness: 0,
            kurtosis: 3);

        Assert.True(fiftyTrials.ExpectedMaximumSharpe > oneTrial.ExpectedMaximumSharpe);
        Assert.True(
            fiftyTrials.DeflatedSharpeProbability <
            oneTrial.DeflatedSharpeProbability);
        Assert.Contains("not independent proof", fiftyTrials.Limitation);
        Assert.Contains("compatible", fiftyTrials.Method);
    }
}
