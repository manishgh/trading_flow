using TradingFlow.Contracts.Evidence;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class PredictorEvidencePresentationTests
{
    [Theory]
    [InlineData("live_inputs_incomplete", "inputs incomplete at the decision")]
    [InlineData("sector_peer_floor", "too few eligible sector peers for ranking")]
    [InlineData("out_of_universe", "not in the point-in-time universe")]
    public void Abstention_PreservesAvailableEvidenceAndDistinctReason(string reason, string label)
    {
        var evidence = Evidence("abstain", [reason]);
        var mobile = SymbolIntelligenceService.MapEvidence(evidence);

        Assert.Equal(label, evidence.ErrorSummary);
        Assert.Equal("available", mobile.AvailabilityStatus);
        Assert.Equal("abstain", mobile.FinalSignal);
        Assert.Equal("invalid", mobile.ReadinessStatus);
        Assert.False(mobile.IsValidPromotedEvidence);
        Assert.NotNull(mobile.Swing);
        Assert.Null(mobile.Swing.Probability);
        Assert.Null(mobile.Swing.DecisionScore);
        Assert.Null(mobile.Swing.Rank);
        Assert.Equal(reason, Assert.Single(mobile.ReadinessReasons));
        Assert.Equal(label, PredictorEvidenceDisplay.FormatReason(mobile.ReadinessReasons[0]));
        Assert.Null(mobile.ModelTrainingDataEnd);
    }

    [Fact]
    public void Reasons_PreserveCombinedAndUnknownDetails()
    {
        var evidence = Evidence("abstain", ["live_inputs_incomplete", "out_of_universe", "upstream detail"]);
        var mobile = SymbolIntelligenceService.MapEvidence(evidence);

        Assert.Equal("inputs incomplete at the decision; not in the point-in-time universe; upstream detail", evidence.ErrorSummary);
        Assert.Equal(evidence.Errors, mobile.ReadinessReasons);
        Assert.Equal("upstream detail", PredictorEvidenceDisplay.FormatReason(mobile.ReadinessReasons[2]));
    }

    [Fact]
    public void UnavailableEvidence_RemainsExplicitAndHasNoSwingReading()
    {
        var evidence = MarketPredictorResult.Unavailable("MU", "swing", "auto", "unavailable", "Service is unavailable.");
        var mobile = SymbolIntelligenceService.MapEvidence(evidence);

        Assert.Equal("unavailable", mobile.AvailabilityStatus);
        Assert.Equal("Service is unavailable.", mobile.AvailabilityReason);
        Assert.Null(mobile.Swing);
        Assert.False(mobile.IsValidPromotedEvidence);
        Assert.Equal("Service is unavailable.", evidence.ErrorSummary);
    }

    [Fact]
    public void ScoredEvidence_RetainsProbabilityWithoutInventingReasons()
    {
        var evidence = Evidence("positive_setup", []);
        var mobile = SymbolIntelligenceService.MapEvidence(evidence);

        Assert.True(mobile.IsValidPromotedEvidence);
        Assert.NotNull(mobile.Swing);
        Assert.Equal(0.72m, mobile.Swing.Probability);
        Assert.Equal(0.72m, mobile.Swing.DecisionScore);
        Assert.Equal(1, mobile.Swing.Rank);
        Assert.Empty(mobile.ReadinessReasons);
        Assert.Empty(evidence.ErrorSummary);
    }

    [Theory]
    [InlineData("positive_setup", ModelDirection.Supportive)]
    [InlineData("low_probability", ModelDirection.Opposed)]
    [InlineData("ranked_candidate", ModelDirection.Neutral)]
    [InlineData("neutral", ModelDirection.Neutral)]
    [InlineData("abstain", ModelDirection.Neutral)]
    [InlineData("watch_for_entry", ModelDirection.Neutral)]
    [InlineData("bullish_watch", ModelDirection.Neutral)]
    public void Direction_UsesFinalSignalAndCannotBeOverriddenByTheSwingLeg(string signal, ModelDirection expected)
    {
        var evidence = Evidence(signal, []);
        evidence = evidence with { Swing = evidence.Swing! with { Signal = "positive_setup" } };
        Assert.Equal(expected, UniverseRankService.ResolveDirection(evidence));
        Assert.Equal(ModelDirection.Neutral, UniverseRankService.ResolveDirection(evidence with { AvailabilityStatus = "unavailable" }));
    }

    private static MarketPredictorResult Evidence(string signal, IReadOnlyList<string> reasons)
    {
        var abstained = signal == "abstain";
        var readiness = abstained ? "invalid" : "valid";
        return new MarketPredictorResult(
            "local-test-evidence", "MU", "swing", "auto", "10b", "request", null,
            new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero), signal, readiness, reasons,
            new PredictorModelInfo("promoted", "classifier", "schema", "target", "hash", null),
            new PredictorSwingPrediction(
                abstained ? null : 0.72m, abstained ? null : 0.72m, signal, abstained ? null : 1,
                null, null, new PredictorGlobalContext(0m, []),
                new PredictorCatalyst("absent", "none", 0m, 0, 0m, null, []),
                new PredictorReadiness(readiness, reasons, null, "sip", "unknown", "unknown", "promoted", "unknown")),
            "available", null);
    }
}
