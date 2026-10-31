using System.Collections.Immutable;
using System.Text.Json;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Tests;

public sealed class DrawdownQualificationTests
{
    // More than six months of fixed native daily observations; no provider/network data.
    private static OhlcvBar[] Bars()
    {
        var start = new DateTimeOffset(2025, 1, 2, 5, 0, 0, TimeSpan.Zero);
        return Enumerable.Range(0, 260).Select(i => start.AddDays(i))
            .Where(day => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)).Take(180)
            .Select(day => new OhlcvBar("TEST", day, "1d", 100, 101, 99, 100, 1000, "sip", "all")).ToArray();
    }

    private static IndicatorSnapshot[] Snapshots(OhlcvBar[] bars) => bars.Select((bar, i) =>
        new IndicatorSnapshot(bar.Ticker, bar.Timestamp, bar.Timeframe, bar.Close, bar.Volume,
            null, 50, i + 1, null, null, null, null, null, null, null, null, null, null,
            DataFeed: "sip", AdjustmentPolicy: "all", MarketEvidenceReliability: "verified_same_feed")).ToArray();

    private static DrawdownQualificationAudit Analyze(OhlcvBar[] bars, int index,
        DrawdownQualificationRules? rules = null, DrawdownHistoryContract? history = null,
        IndicatorSnapshot[]? snapshots = null) => DrawdownQualificationAnalyzer.Analyze(bars,
            snapshots ?? Snapshots(bars), index, bars[index].Timestamp.AddDays(1), rules, history);

    private static DrawdownHistoryContract History(OhlcvBar[] bars, int index = 49)
    {
        var prefix = bars.Take(index + 1).ToArray();
        var binding = new DrawdownPricePrefixBinding("security-test", "all-v1", prefix[^1].Timestamp,
            prefix[^1].Timestamp.AddDays(1), prefix.Length,
            DrawdownPriceBasisBinding.ComputeSha256("security-test", "all-v1", prefix), new string('c', 64));
        return new("security-test", "all-v1", bars.Select(bar => bar.Timestamp).ToArray(),
            new("security-test", "TEST", "sip", "all", "all-v1", bars[0].Timestamp,
                bars[^1].Timestamp.AddDays(1), bars[0].Timestamp, new string('a', 64), binding),
            bars[0].Timestamp, new string('b', 64));
    }

    [Fact]
    public void PrefixIsInvariantToFutureBarsAndSnapshots()
    {
        var bars = Bars();
        var first = Analyze(bars.Take(50).ToArray(), 49);
        bars[50] = bars[50] with { High = 100000, Low = 1, Close = 500, Open = 500 };
        var snapshots = Snapshots(bars);
        snapshots[50] = snapshots[50] with { Atr = 1000000 };
        var second = Analyze(bars, 49, snapshots: snapshots);
        Assert.Equal(EvidenceCanonicalJson.ComputeSha256(first), EvidenceCanonicalJson.ComputeSha256(second));
    }

    [Fact]
    public void PeakUsesLatestPriorTieAndOnlySubsequentLows()
    {
        var bars = Bars();
        bars[31] = bars[31] with { High = 120, Low = 50 };
        bars[47] = bars[47] with { High = 120, Low = 60 };
        bars[48] = bars[48] with { Low = 90 };
        bars[49] = bars[49] with { High = 200, Low = 95 };
        var observed = Analyze(bars, 49).Observation;
        Assert.Equal(bars[47].Timestamp, observed.PeakTimestamp);
        Assert.Equal(90m, observed.LowSincePeak);
        Assert.Equal(2, observed.SessionsSincePeak);
        Assert.Equal(1m / 6m, observed.Metrics["close_drawdown_fraction"].Value);
        Assert.Equal(.25m, observed.Metrics["low_drawdown_fraction"].Value);
        Assert.Equal(47m, observed.PredeclineAtr);
        Assert.Equal(49m, observed.LaggedAtr);
        Assert.Equal(20m / 47m, observed.Metrics["predecline_atr_depth"].Value);
    }

    [Fact]
    public void MovesAndTrueRangeUseTheirPrespecifiedLaggedDenominators()
    {
        var bars = Bars();
        bars[49] = bars[49] with { Open = 105, High = 108, Low = 102, Close = 106, Volume = 2000 };
        var evidence = Analyze(bars, 49).Observation;
        Assert.Equal(6m / 49m, evidence.Metrics["move_1_atr"].Value);
        Assert.Equal(6m / 47m, evidence.Metrics["move_3_atr"].Value);
        Assert.Equal(8m / 49m, evidence.Metrics["true_range_atr"].Value);
        Assert.Equal(2m, evidence.Metrics["volume_median_ratio"].Value);
        Assert.Null(evidence.Metrics["predecline_atr_depth"].Value);
        Assert.Equal("not_below_prior_peak", evidence.Metrics["predecline_atr_depth"].UnknownReason);
    }

    [Fact]
    public void ZeroRangeVolumeAndAtrAreUnknownInsteadOfFabricatedZeros()
    {
        var bars = Bars().Select(bar => bar with { Volume = 0 }).ToArray();
        bars[49] = bars[49] with { Open = 100, High = 100, Low = 100, Close = 100 };
        var snapshots = Snapshots(bars).Select(snapshot => snapshot with { Atr = 0 }).ToArray();
        var observed = Analyze(bars, 49, snapshots: snapshots).Observation;
        Assert.Equal("zero_range_bar", observed.Metrics["close_location"].UnknownReason);
        Assert.Equal("positive_prior_volume_median_unavailable", observed.Metrics["volume_median_ratio"].UnknownReason);
        Assert.Equal("prepeak_atr14_unavailable", observed.Metrics["predecline_atr_depth"].UnknownReason);
        Assert.Null(observed.Metrics["move_1_atr"].Value);
    }

    [Fact]
    public void MissingWarmupIsExplicitAndRequiredGateFailsClosed()
    {
        var evidence = Analyze(Bars(), 19, new("gate", "close_drawdown_fraction", MinValue: 0));
        Assert.False(evidence.Observation.FeatureEligible);
        Assert.Equal("unknown", evidence.Verdict);
        Assert.Equal("twenty_prior_sessions_unavailable", evidence.Reason);
    }

    [Theory]
    [InlineData("malformed", "malformed_ohlcv")]
    [InlineData("unordered", "unordered_or_duplicate_bars")]
    [InlineData("future", "bar_not_completed_or_available")]
    [InlineData("identity", "unaligned_or_changed_market_identity")]
    [InlineData("adjustment", "unaligned_or_changed_market_identity")]
    public void InvalidPrefixRetainsAnUnknownEvidenceReason(string mutation, string reason)
    {
        var bars = Bars();
        bars[30] = mutation switch {
            "malformed" => bars[30] with { Low = 102 },
            "unordered" => bars[30] with { Timestamp = bars[29].Timestamp },
            "future" => bars[30] with { KnownAtUtc = bars[51].Timestamp },
            "identity" => bars[30] with { Ticker = "OTHER" },
            _ => bars[30] with { AdjustmentPolicy = "raw" }
        };
        var result = Analyze(bars, 49, new("gate", "close_drawdown_fraction", MinValue: 0));
        Assert.Equal("unknown", result.Verdict);
        Assert.Equal(reason, result.Reason);
        Assert.Empty(result.Episodes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SnapshotAtrMustShareTheBarFeedAndAdjustmentIdentity(bool mismatchFeed)
    {
        var bars = Bars();
        var snapshots = Snapshots(bars);
        snapshots[48] = mismatchFeed ? snapshots[48] with { DataFeed = "iex" }
            : snapshots[48] with { AdjustmentPolicy = "raw" };
        var audit = Analyze(bars, 49, new("gate", "predecline_atr_depth", MinValue: 0), snapshots: snapshots);
        Assert.Equal("unknown", audit.Verdict);
        Assert.Equal("unaligned_or_changed_market_identity", audit.Reason);
        Assert.Empty(audit.Episodes);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("[observe]")]
    public void QualificationYamlSectionRejectsScalarAndListShapes(string invalidValue)
    {
        var strategy = StrategyDecisionKernelTests.CreateRequest().Strategy;
        var yaml = TradingFlow.Web.Services.RunConfigWriter.SerializeStrategyYaml(strategy)
            .Replace("entry_rules:", "entry_rules:\n  drawdown_qualification: " + invalidValue);
        var path = Path.Combine(Path.GetTempPath(), "drawdown-invalid-shape-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            File.WriteAllText(path, yaml);
            var error = Assert.Throws<InvalidOperationException>(() => new TradingFlow.Engine.Configuration.SimpleYamlReader().ReadStrategy(path));
            Assert.Contains("entry_rules.drawdown_qualification", error.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EntireFixedOutcomeWindowMustMatureAndLosingEpisodeIsRetained()
    {
        var bars = Bars();
        bars[25] = bars[25] with { Open = 95, Low = 94, Close = 95 };
        bars[26] = bars[26] with { Open = 105, High = 106, Close = 105 };
        var before = Analyze(bars, 24, history: History(bars, 24));
        var mature = Analyze(bars, 25, history: History(bars, 25));
        var after = Analyze(bars, 27, history: History(bars, 27));
        Assert.Equal("right_censored", before.Episodes.Single().OutcomeStatus);
        Assert.Empty(before.Episodes.Single().Outcomes);
        Assert.Null(mature.Episodes.Single().RecoveredAtTimestamp);
        Assert.Equal(-.05m, mature.Episodes.Single().Outcomes.Single(label => label.HorizonSessions == 5).CloseReturnFraction);
        Assert.All(mature.Episodes.Single().Outcomes, label => Assert.Equal(bars[25].Timestamp.AddDays(1), label.AvailableAtUtc));
        Assert.Equal(2, after.Episodes.Count);
        Assert.Equal(1, after.MaturedPriorEpisodeCount);
        Assert.Equal(before.Episodes[0].EpisodeId, after.Episodes[0].EpisodeId);
    }

    [Fact]
    public void RecoveryCannotCreateOverlappingOutcomeWindows()
    {
        var bars = Bars();
        bars[21] = bars[21] with { Open = 105, High = 106, Close = 105 };
        var audit = Analyze(bars, 27, history: History(bars, 27));
        Assert.Equal(bars[20].Timestamp, audit.Episodes[0].OnsetTimestamp);
        Assert.Equal(bars[26].Timestamp, audit.Episodes[1].OnsetTimestamp);
    }

    [Fact]
    public void MissingSessionCalendarOrIdentityNeverInventsMatureHistory()
    {
        var bars = Bars();
        var gate = new DrawdownQualificationRules("gate", MinimumMaturedEpisodes: 1);
        var missingContract = Analyze(bars, 49, gate);
        Assert.Equal("unknown", missingContract.Verdict);
        Assert.Null(missingContract.MaturedPriorEpisodeCount);
        Assert.All(missingContract.Episodes, episode => Assert.Empty(episode.Outcomes));
        var history = History(bars) with { ExpectedSessionTimestamps = bars.Select(bar => bar.Timestamp)
            .Append(bars[30].Timestamp.AddHours(1)).Order().ToArray() };
        var gap = Analyze(bars, 49, gate, history);
        Assert.Equal("expected_session_interval_missing", gap.HistoryUnknownReason);
        Assert.Equal("unknown", gap.Verdict);
    }

    [Theory]
    [InlineData(true, false, "historical_sector_context_unavailable")]
    [InlineData(false, true, "evaluated_asof_event_context_unavailable")]
    public void EvidenceDependentRequirementsAreUnknown(bool sector, bool events, string reason)
    {
        var audit = Analyze(Bars(), 49, new("gate", RequireSectorContext: sector, RequireEventContext: events));
        Assert.Equal("unknown", audit.Verdict);
        Assert.Equal(reason, audit.Reason);
    }

    [Fact]
    public void BoundsAreInclusiveAndObserveNeverFilters()
    {
        var bars = Bars();
        var value = Analyze(bars, 49).Observation.Metrics["close_drawdown_fraction"].Value!.Value;
        Assert.Equal("qualifies", Analyze(bars, 49, new("gate", "close_drawdown_fraction", value, value)).Verdict);
        Assert.Equal("rejected", Analyze(bars, 49, new("gate", "close_drawdown_fraction", MaxValue: 0)).Verdict);
        Assert.Equal("observe", Analyze(bars, 49, new("observe", "close_drawdown_fraction", MaxValue: 0)).Verdict);
    }

    // The frozen 1-3 band uses decline remaining at the rebound close, independently of a deeper prior low.
    [Theory]
    [InlineData(116, "qualifies", 1)]
    [InlineData(108, "qualifies", 3)]
    [InlineData(116.01, "rejected", .9975)]
    [InlineData(107.99, "rejected", 3.0025)]
    public void RecoveryBand_UsesInclusivePrepeakAtrAtTheReboundClose(decimal close, string verdict, decimal depth)
    {
        var bars = Bars();
        bars[40] = bars[40] with { High = 120 };
        bars[41] = bars[41] with { Low = 80 };
        bars[49] = bars[49] with { Open = close, Low = close - 1, High = close + 1, Close = close };
        var snapshots = Snapshots(bars);
        snapshots[39] = snapshots[39] with { Atr = 4 };
        var audit = Analyze(bars, 49, new("gate", "predecline_atr_depth", 1m, 3m), snapshots: snapshots);
        Assert.Equal(verdict, audit.Verdict);
        Assert.Equal(depth, audit.Observation.Metrics["predecline_atr_depth"].Value);
        Assert.Equal(80m, audit.Observation.LowSincePeak);
        Assert.Equal((120m - close) / 120m, audit.Observation.Metrics["close_drawdown_fraction"].Value);
        Assert.Equal(1m / 3m, audit.Observation.Metrics["low_drawdown_fraction"].Value);
    }

    // A valid rebound close cannot supply its own ATR when the observation before the prior peak is unavailable.
    [Fact]
    public void RecoveryBand_RejectsUnknownPrepeakAtrWithoutInventingADepth()
    {
        var bars = Bars();
        bars[40] = bars[40] with { High = 108 };
        var snapshots = Snapshots(bars);
        snapshots[39] = snapshots[39] with { Atr = null };
        var audit = Analyze(bars, 49, new("gate", "predecline_atr_depth", 1m, 3m), snapshots: snapshots);
        Assert.Equal("unknown", audit.Verdict);
        Assert.Equal("prepeak_atr14_unavailable", audit.Reason);
        Assert.Null(audit.Observation.Metrics["predecline_atr_depth"].Value);
    }

    [Fact]
    public void DefaultAuditDoesNotEnterLegacySemanticDecisionOrStrategyIdentity()
    {
        var request = StrategyDecisionKernelTests.CreateRequest();
        var decision = new StrategyDecisionKernel().Evaluate(request);
        Assert.True(decision.IsTriggered, decision.NoEntryReason);
        Assert.Equal("observe", decision.DrawdownQualification!.Verdict);
        using var json = JsonDocument.Parse(decision.CanonicalDecisionJson);
        var semantic = json.RootElement.GetProperty("semantic");
        Assert.False(semantic.TryGetProperty("drawdownQualification", out _));
        // Re-serializing JsonElement re-escapes the DateTimeOffset '+' character.
        // Hash the exact embedded bytes to verify the original semantic contract.
        var semanticHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(semantic.GetRawText()))).ToLowerInvariant();
        Assert.Equal(decision.SemanticDecisionSha256, semanticHash);
        Assert.DoesNotContain("DrawdownQualification", JsonSerializer.Serialize(request.Strategy));
        Assert.Equal(request.StrategyIdentity.ContentSha256, EvidenceCanonicalJson.ComputeSha256(new CanonicalStrategyDocument(
            request.StrategyIdentity.StrategyId, request.StrategyIdentity.SemanticVersion, request.Strategy, request.AdmissionProfile)));
    }

    [Fact]
    public void SharedKernelRetainsBaselineTriggerWhenOptionalGateIsUnknown()
    {
        var request = StrategyDecisionKernelTests.CreateRequest();
        var baseline = new StrategyDecisionKernel().Evaluate(request);
        var strategy = request.Strategy with { EntryRules = request.Strategy.EntryRules with {
            DrawdownQualification = new("gate", RequireEventContext: true) } };
        var hash = EvidenceCanonicalJson.ComputeSha256(new CanonicalStrategyDocument(request.StrategyIdentity.StrategyId,
            request.StrategyIdentity.SemanticVersion, strategy, request.AdmissionProfile));
        request = request with { Strategy = strategy, StrategyIdentity = new(request.StrategyIdentity.StrategyId,
                request.StrategyIdentity.SemanticVersion, hash),
            Candidate = request.Candidate with { Identity = request.Candidate.Identity with { StrategyContentSha256 = hash } } };
        var gated = new StrategyDecisionKernel().Evaluate(request);
        Assert.True(baseline.IsTriggered);
        Assert.False(gated.IsTriggered);
        Assert.Equal(StrategyCandidateState.Rejected, gated.State);
        Assert.Null(gated.OrderPlan);
        Assert.Equal(baseline.Signal, gated.Signal);
        Assert.Contains(gated.Rules, rule => rule.Code == "execution_trigger_satisfied" && rule.Passed);
        Assert.StartsWith("drawdown_unknown:", gated.NoEntryReason);
        Assert.Contains(gated.Rules, rule => rule.Stage == "qualifier" && !rule.Passed);
    }

    [Fact]
    public async Task RejectedOpportunityCannotRetryOnLaterEvidenceButNewCandidateCanQualify()
    {
        var original = StrategyDecisionKernelTests.CreateRequest();
        var strategy = original.Strategy with { EntryRules = original.Strategy.EntryRules with {
            DrawdownQualification = new("gate", "close_location", MinValue: 0, MaxValue: 1) } };
        var identity = new StrategyArtifactIdentity(original.StrategyIdentity.StrategyId, original.StrategyIdentity.SemanticVersion,
            EvidenceCanonicalJson.ComputeSha256(new CanonicalStrategyDocument(original.StrategyIdentity.StrategyId,
                original.StrategyIdentity.SemanticVersion, strategy, original.AdmissionProfile)));
        var request = original with { Strategy = strategy, StrategyIdentity = identity,
            Candidate = original.Candidate with { Identity = original.Candidate.Identity with { StrategyContentSha256 = identity.ContentSha256 } } };
        using var journal = new BacktestCandidateJournal();
        var run = BacktestCandidateJournal.CreateRun(new { Test = "drawdown-terminal" }, "drawdown-terminal", original.AsOfUtc.AddMinutes(-15));
        var orchestrator = new StrategyCandidateDecisionOrchestrator(new StrategyDecisionKernel(), journal);
        var rejected = await orchestrator.EvaluateAsync(run, request);
        Assert.Equal(StrategyCandidateState.Rejected, rejected.Candidate.State);

        var firstBar = request.BarsByTimeframe["1d"][0];
        var firstSnapshot = request.SnapshotsByTimeframe["1d"][0];
        var bars = Enumerable.Range(-30, 30).Select(offset => firstBar with { Timestamp = firstBar.Timestamp.AddDays(offset) })
            .Concat(request.BarsByTimeframe["1d"]).ToImmutableArray();
        var snapshots = Enumerable.Range(-30, 30).Select(offset => firstSnapshot with { Timestamp = firstSnapshot.Timestamp.AddDays(offset) })
            .Concat(request.SnapshotsByTimeframe["1d"]).ToImmutableArray();
        var later = request with { AsOfUtc = request.AsOfUtc.AddMinutes(1),
            BarsByTimeframe = request.BarsByTimeframe.SetItem("1d", bars),
            SnapshotsByTimeframe = request.SnapshotsByTimeframe.SetItem("1d", snapshots) };
        var otherwiseQualified = new StrategyDecisionKernel().Evaluate(later);
        Assert.True(otherwiseQualified.IsTriggered, otherwiseQualified.NoEntryReason);
        var retry = await orchestrator.EvaluateAsync(run, later);
        Assert.True(retry.WasAlreadyTerminal);
        Assert.Null(retry.Decision);
        Assert.False(retry.IsNewTrigger);

        var separate = later with { Candidate = later.Candidate with { CandidateId = Guid.NewGuid(),
            Identity = later.Candidate.Identity with { SetupKey = "separate-opportunity" } } };
        var newCandidate = await orchestrator.EvaluateAsync(run, separate);
        Assert.True(newCandidate.IsNewTrigger, newCandidate.Decision?.NoEntryReason);
    }
}
