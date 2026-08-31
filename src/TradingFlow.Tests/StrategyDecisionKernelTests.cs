using System.Collections.Immutable;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Tests;

public sealed class StrategyDecisionKernelTests
{
    [Fact]
    public void Evaluate_WhenCalledWithIdenticalEvidence_ProducesSameSemanticDecision()
    {
        var kernel = new StrategyDecisionKernel();
        var request = CreateRequest();

        var first = kernel.Evaluate(request);
        var second = kernel.Evaluate(request);

        Assert.Equal(StrategyCandidateState.Triggered, first.State);
        Assert.Equal(first.SemanticDecisionSha256, second.SemanticDecisionSha256);
        Assert.Equal(first.CanonicalDecisionJson, second.CanonicalDecisionJson);
        Assert.Contains(first.Transitions, transition => transition.To == StrategyCandidateState.Qualified);
        Assert.Contains(first.Transitions, transition => transition.To == StrategyCandidateState.Armed);
        Assert.Contains(first.Transitions, transition => transition.To == StrategyCandidateState.Triggered);
    }

    [Fact]
    public void CreateSetupKey_NormalizesTimeframeAndTimestampAcrossAdapters()
    {
        var instant = new DateTimeOffset(2026, 6, 11, 15, 30, 0, TimeSpan.FromHours(2));

        var local = StrategyDecisionRequestAssembler.CreateSetupKey("strategy.v1", "5M", instant);
        var utc = StrategyDecisionRequestAssembler.CreateSetupKey(
            "strategy.v1",
            "5m",
            instant.ToUniversalTime());

        Assert.Equal(utc, local);
        Assert.Equal("strategy.v1:5m:2026-06-11T13:30:00.0000000+00:00", local);
    }

    [Fact]
    public void Evaluate_AdapterProvenanceDoesNotChangeSemanticDecisionHash()
    {
        var kernel = new StrategyDecisionKernel();
        var backtest = CreateRequest();
        var paper = WithPaperAdapterProvenance(backtest);

        var backtestDecision = kernel.Evaluate(backtest);
        var paperDecision = kernel.Evaluate(paper);

        Assert.Equal(backtestDecision.SemanticDecisionSha256, paperDecision.SemanticDecisionSha256);
        Assert.Equal(
            backtestDecision.Rules.Select(rule => (rule.Stage, rule.Code, rule.Passed)),
            paperDecision.Rules.Select(rule => (rule.Stage, rule.Code, rule.Passed)));
        Assert.NotEqual(backtestDecision.CanonicalDecisionJson, paperDecision.CanonicalDecisionJson);
    }

    [Theory]
    [InlineData("long_regular_no_catalyst")]
    [InlineData("long_regular_catalyst")]
    [InlineData("short_research")]
    [InlineData("long_extended")]
    public void Evaluate_GoldenAdapterMatrixProducesIdenticalSemanticDecision(string scenario)
    {
        var kernel = new StrategyDecisionKernel();
        var backtest = CreateParityScenario(scenario);
        var paper = WithPaperAdapterProvenance(backtest);

        var backtestDecision = kernel.Evaluate(backtest);
        var paperDecision = kernel.Evaluate(paper);

        Assert.Equal(backtestDecision.SemanticDecisionSha256, paperDecision.SemanticDecisionSha256);
        Assert.Equal(backtestDecision.State, paperDecision.State);
        Assert.Equal(backtestDecision.Direction, paperDecision.Direction);
        Assert.Equal(backtestDecision.NoEntryReason, paperDecision.NoEntryReason);
        Assert.Equal(
            backtestDecision.Rules.Select(rule => (rule.Stage, rule.Code, rule.Passed)),
            paperDecision.Rules.Select(rule => (rule.Stage, rule.Code, rule.Passed)));
    }

    [Fact]
    public void Evaluate_WhenDiscoveryWasNotPersisted_FailsBeforeTechnicalEvaluation()
    {
        var kernel = new StrategyDecisionKernel();
        var request = CreateRequest();
        request = request with
        {
            Candidate = request.Candidate with
            {
                Discovery = request.Candidate.Discovery with { IsPersisted = false }
            }
        };

        var result = kernel.Evaluate(request);

        Assert.Equal(StrategyCandidateState.Rejected, result.State);
        Assert.Null(result.Signal);
        Assert.Null(result.OrderPlan);
        Assert.Equal("candidate_not_persisted", result.NoEntryReason);
        Assert.False(result.Rules.Single(rule => rule.Code == "candidate_not_persisted").Passed);
    }

    [Fact]
    public void Evaluate_WhenCatalystRevisionWasNotKnownAsOfDecision_DoesNotLeakIt()
    {
        var kernel = new StrategyDecisionKernel();
        var request = CreateRequest(requirePositiveNews: true);
        var futureCatalyst = CreateCatalyst(request.AsOfUtc.AddMinutes(1));

        var unavailable = kernel.Evaluate(request with { Catalysts = [futureCatalyst] });
        var available = kernel.Evaluate(request with
        {
            Catalysts = [futureCatalyst with { DecisionKnownAtUtc = request.AsOfUtc.AddMinutes(-1) }]
        });

        Assert.NotEqual(StrategyCandidateState.Triggered, unavailable.State);
        Assert.Contains(unavailable.Rules, rule =>
            !rule.Passed && rule.Code.Contains("news", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(StrategyCandidateState.Triggered, available.State);
        Assert.NotNull(available.Signal?.Catalyst);
    }

    [Fact]
    public void Evaluate_WhenAdmissionProfileDoesNotMatchArtifactHash_RejectsRequest()
    {
        var request = CreateRequest();
        var changedProfile = request.AdmissionProfile with { ProfileVersion = "2.0.0" };

        var error = Assert.Throws<InvalidOperationException>(() =>
            new StrategyDecisionKernel().Evaluate(request with { AdmissionProfile = changedProfile }));

        Assert.Contains("immutable content hash", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BacktestAssembler_BindsCandidateIdentityToOwningRunAndStartsUnpersisted()
    {
        var original = CreateRequest();
        var runtime = new AuthorizedRuntimeStrategy(
            original.StrategyIdentity,
            StrategySelectionMode.Backtest,
            original.Strategy,
            original.AdmissionProfile);
        var discovery = StrategyDecisionRequestAssembler.CreateBacktestDiscovery(
            original.Candidate.Identity.Symbol,
            original.Candidate.Identity.SetupKey,
            original.Candidate.Identity.DiscoveryWindowStartUtc,
            original.Candidate.Identity.DiscoveryWindowEndUtc);

        var first = CreateForRun(Guid.NewGuid());
        var second = CreateForRun(Guid.NewGuid());

        Assert.False(discovery.IsPersisted);
        Assert.NotEqual(first.Candidate.CandidateId, second.Candidate.CandidateId);
        Assert.Equal(first.Candidate.Identity, second.Candidate.Identity);

        StrategyDecisionRequest CreateForRun(Guid runId) => StrategyDecisionRequestAssembler.Create(
            runtime,
            original.Candidate.Identity.Symbol,
            original.BarsByTimeframe.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<OhlcvBar>)pair.Value,
                StringComparer.OrdinalIgnoreCase),
            original.SnapshotsByTimeframe.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<IndicatorSnapshot>)pair.Value,
                StringComparer.OrdinalIgnoreCase),
            [],
            original.AsOfUtc,
            discovery,
            original.Candidate.Universe,
            original.Candidate.Regime,
            StrategyCandidateState.Discovered,
            0,
            original.Candidate.Identity.SetupKey,
            original.Candidate.Identity.DiscoveryWindowStartUtc,
            original.Candidate.Identity.DiscoveryWindowEndUtc,
            2,
            runId);
    }

    [Fact]
    public void BacktestAssembler_DoesNotPromoteProviderTimestampIntoDecisionAvailability()
    {
        var original = CreateRequest(requirePositiveNews: true);
        var runtime = new AuthorizedRuntimeStrategy(
            original.StrategyIdentity,
            StrategySelectionMode.Backtest,
            original.Strategy,
            original.AdmissionProfile);
        var discovery = StrategyDecisionRequestAssembler.CreateBacktestDiscovery(
            original.Candidate.Identity.Symbol,
            original.Candidate.Identity.SetupKey,
            original.Candidate.Identity.DiscoveryWindowStartUtc,
            original.Candidate.Identity.DiscoveryWindowEndUtc);
        var providerTimestampOnly = new CatalystEvent(
            original.Candidate.Identity.Symbol,
            original.AsOfUtc.AddMinutes(-5),
            CatalystType.NewsReport,
            "Provider timestamp without observed receipt",
            0.9m,
            Provider: "alpaca",
            ExternalId: "incomplete-news");

        var request = StrategyDecisionRequestAssembler.Create(
            runtime,
            original.Candidate.Identity.Symbol,
            original.BarsByTimeframe.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<OhlcvBar>)pair.Value,
                StringComparer.OrdinalIgnoreCase),
            original.SnapshotsByTimeframe.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<IndicatorSnapshot>)pair.Value,
                StringComparer.OrdinalIgnoreCase),
            [providerTimestampOnly],
            original.AsOfUtc,
            discovery,
            original.Candidate.Universe,
            original.Candidate.Regime,
            StrategyCandidateState.Discovered,
            0,
            original.Candidate.Identity.SetupKey,
            original.Candidate.Identity.DiscoveryWindowStartUtc,
            original.Candidate.Identity.DiscoveryWindowEndUtc,
            2,
            Guid.NewGuid());

        Assert.Empty(request.Catalysts);
    }

    internal static StrategyDecisionRequest CreateRequest(bool requirePositiveNews = false)
    {
        var strategy = CreateStrategy(requirePositiveNews);
        var profile = CreateProfile();
        var identity = new StrategyArtifactIdentity(
            strategy.StrategyId,
            "1.0.0",
            EvidenceCanonicalJson.ComputeSha256(new CanonicalStrategyDocument(
                strategy.StrategyId,
                "1.0.0",
                strategy,
                profile)));
        var bars = CreateBars();
        var snapshots = CreateSnapshots(bars);
        var asOf = new DateTimeOffset(2026, 6, 11, 14, 4, 0, TimeSpan.Zero);
        var candidate = new StrategyCandidateSnapshot(
            Guid.Parse("b2903d73-7b3d-4ab1-9840-55cdf6d8f2fb"),
            new StrategyCandidateIdentity(
                "RGTI",
                identity.ContentSha256,
                "golden_fixture",
                asOf.AddMinutes(-10),
                asOf.AddMinutes(5)),
            new StrategyDiscoveryEvidence(
                Guid.Parse("c3903d73-7b3d-4ab1-9840-55cdf6d8f2fb"),
                1,
                true,
                true,
                asOf.AddMinutes(-10),
                asOf.AddMinutes(5),
                ["golden_fixture"],
                new string('e', 64)),
            new StrategyMarketStateEvidence(
                true,
                "fixture-state",
                "alpaca",
                "sip",
                "all",
                "verified_same_feed",
                asOf.AddSeconds(-1),
                ImmutableDictionary<string, int>.Empty
                    .WithComparers(StringComparer.OrdinalIgnoreCase)
                    .Add("1m", bars.Length),
                ImmutableDictionary<string, int>.Empty
                    .WithComparers(StringComparer.OrdinalIgnoreCase)
                    .Add("1m", 2)),
            new StrategyEligibilityEvidence(
                true,
                "fixture-universe",
                "universe-1",
                asOf.AddMinutes(-1),
                "{\"eligible\":true}"),
            new StrategyEligibilityEvidence(
                true,
                "fixture-regime",
                "regime-1",
                asOf.AddMinutes(-1),
                "{\"eligible\":true}"),
            StrategyCandidateState.DataWarming,
            1);

        return new StrategyDecisionRequest(
            identity,
            profile,
            strategy,
            candidate,
            ImmutableDictionary<string, ImmutableArray<OhlcvBar>>.Empty
                .WithComparers(StringComparer.OrdinalIgnoreCase)
                .Add("1m", bars),
            ImmutableDictionary<string, ImmutableArray<IndicatorSnapshot>>.Empty
                .WithComparers(StringComparer.OrdinalIgnoreCase)
                .Add("1m", snapshots),
            [],
            asOf);
    }

    private static StrategyDefinition CreateStrategy(bool requirePositiveNews) => new(
        "strategy.kernel-golden",
        "Kernel Golden Fixture",
        "unit-test",
        1,
        "1m",
        "long",
        new EntryRules(
            SetupType: "momentum",
            MinVolumeSpike: 1m,
            MinEntryRsi: 0m,
            MaxEntryRsi: 100m,
            TrendFilter: "none",
            MacdFilter: "none",
            RequirePriceAboveBollingerMiddle: false,
            RequireMacdHistogramPositive: false,
            RequirePriceAboveVwap: false,
            RequirePriceAboveEma20: false,
            RequirePriceAboveEma50: false,
            RequireEma20AboveEma50: false,
            MaxVwapExtensionAtr: null,
            OpeningRangeMinutes: 5,
            RecentHighLookbackBars: 8,
            VolatilityContractionLookbackBars: 10,
            RequirePositiveNews: requirePositiveNews),
        new ConfluenceRules(false, "1m", 50, "none"),
        new ExitRules(1m, 3m, 2m, false, 1m, 1m, false, false, false, 1),
        new ExecutionRules("1m", 5m),
        new SessionRules("America/New_York", 0, 0, 0));

    private static StrategyAdmissionProfile CreateProfile() => new(
        "default-deterministic-v1",
        "1.0.0",
        "simple-yaml-strict-v1",
        new StrategyIndicatorProfile(
            "Skender.Stock.Indicators",
            "2.7.1",
            "TradingFlow.DerivedIndicators",
            "1.0.0"),
        "account-risk-budget-v1",
        "completed-bar-next-executable-bar-v1");

    private static ImmutableArray<OhlcvBar> CreateBars()
    {
        var start = new DateTimeOffset(2026, 6, 11, 14, 0, 0, TimeSpan.Zero);
        return Enumerable.Range(0, 3)
            .Select(index => new OhlcvBar(
                "RGTI",
                start.AddMinutes(index),
                "1m",
                10m + index * 0.05m,
                10.20m + index * 0.05m,
                9.95m + index * 0.05m,
                10.10m + index * 0.05m,
                10_000m + index * 500m,
                "sip",
                "all"))
            .ToImmutableArray();
    }

    private static ImmutableArray<IndicatorSnapshot> CreateSnapshots(
        ImmutableArray<OhlcvBar> bars) => bars
        .Select(bar => new IndicatorSnapshot(
            bar.Ticker,
            bar.Timestamp,
            bar.Timeframe,
            bar.Close,
            bar.Volume,
            Vwap: 10m,
            Rsi: 60m,
            Atr: 0.25m,
            Ema20: 9.9m,
            Ema50: 9.8m,
            Ema200: null,
            BollingerMiddle: 10m,
            BollingerUpper: 10.5m,
            BollingerLower: 9.5m,
            RelativeVolume: 2m,
            MacdLine: 0.2m,
            MacdSignal: 0.1m,
            MacdHistogram: 0.1m,
            SlotRelativeVolume: 1.5m,
            Ema10: 10m,
            SlotMedianVolume: 7_000m,
            CumulativeSameTimeMedianVolume: 15_000m,
            RelativeVolumeSampleCount: 20,
            SlotRelativeVolumeSampleCount: 20,
            MarketEvidenceProfileVersion: "us_equities_same_time_rvol_v1",
            RelativeVolumeCohort: "regular",
            RelativeVolumeMinimumSamples: 20,
            DataFeed: "sip",
            AdjustmentPolicy: "all",
            MarketEvidenceReliability: "verified_same_feed"))
        .ToImmutableArray();

    private static StrategyCatalystSnapshot CreateCatalyst(DateTimeOffset decisionKnownAtUtc)
    {
        var published = new DateTimeOffset(2026, 6, 11, 13, 55, 0, TimeSpan.Zero);
        var received = published.AddSeconds(2);
        var item = new CatalystEvent(
            "RGTI",
            published,
            CatalystType.NewsReport,
            "RGTI receives a material contract award",
            0.8m,
            Provider: "alpaca",
            ExternalId: "news-123",
            ReceivedAt: received,
            DecisionAvailableAt: decisionKnownAtUtc,
            DecisionAvailabilityEvidence: "fixture-received-at",
            ClassificationVersion: "fixture-classifier-v1");
        return new StrategyCatalystSnapshot(
            "alpaca",
            "news-123",
            "revision-1",
            published,
            null,
            received,
            decisionKnownAtUtc,
            "fixture-classifier-v1",
            "fixture-received-at",
            new string('f', 64),
            item);
    }

    internal static StrategyDecisionRequest CreateParityScenario(string scenario)
    {
        var request = scenario == "long_regular_catalyst"
            ? CreateRequest(requirePositiveNews: true)
            : CreateRequest();
        if (scenario == "long_regular_catalyst")
        {
            return request with
            {
                Catalysts = [CreateCatalyst(request.AsOfUtc.AddMinutes(-1))]
            };
        }

        if (scenario == "short_research")
        {
            return RebindStrategy(request, request.Strategy with { Direction = "short" });
        }

        if (scenario != "long_extended")
        {
            return request;
        }

        var offset = TimeSpan.FromHours(-2);
        var shiftedBars = request.BarsByTimeframe.ToImmutableDictionary(
            pair => pair.Key,
            pair => pair.Value.Select(bar => bar with { Timestamp = bar.Timestamp.Add(offset) }).ToImmutableArray(),
            StringComparer.OrdinalIgnoreCase);
        var shiftedSnapshots = request.SnapshotsByTimeframe.ToImmutableDictionary(
            pair => pair.Key,
            pair => pair.Value.Select(snapshot => snapshot with { Timestamp = snapshot.Timestamp.Add(offset) }).ToImmutableArray(),
            StringComparer.OrdinalIgnoreCase);
        var shifted = request with
        {
            BarsByTimeframe = shiftedBars,
            SnapshotsByTimeframe = shiftedSnapshots,
            AsOfUtc = request.AsOfUtc.Add(offset),
            Candidate = request.Candidate with
            {
                Identity = request.Candidate.Identity with
                {
                    DiscoveryWindowStartUtc = request.Candidate.Identity.DiscoveryWindowStartUtc.Add(offset),
                    DiscoveryWindowEndUtc = request.Candidate.Identity.DiscoveryWindowEndUtc.Add(offset)
                },
                Discovery = request.Candidate.Discovery with
                {
                    ObservedAtUtc = request.Candidate.Discovery.ObservedAtUtc.Add(offset),
                    ExpiresAtUtc = request.Candidate.Discovery.ExpiresAtUtc.Add(offset)
                },
                MarketState = request.Candidate.MarketState with
                {
                    VerifiedAtUtc = request.Candidate.MarketState.VerifiedAtUtc.Add(offset)
                },
                Universe = request.Candidate.Universe with
                {
                    EvaluatedAtUtc = request.Candidate.Universe.EvaluatedAtUtc.Add(offset)
                },
                Regime = request.Candidate.Regime with
                {
                    EvaluatedAtUtc = request.Candidate.Regime.EvaluatedAtUtc.Add(offset)
                }
            }
        };
        return RebindStrategy(
            shifted,
            shifted.Strategy with
            {
                Session = shifted.Strategy.Session with { UseExtendedHours = true }
            });
    }

    private static StrategyDecisionRequest RebindStrategy(
        StrategyDecisionRequest request,
        StrategyDefinition strategy)
    {
        var identity = new StrategyArtifactIdentity(
            strategy.StrategyId,
            request.StrategyIdentity.SemanticVersion,
            EvidenceCanonicalJson.ComputeSha256(new CanonicalStrategyDocument(
                strategy.StrategyId,
                request.StrategyIdentity.SemanticVersion,
                strategy,
                request.AdmissionProfile)));
        return request with
        {
            Strategy = strategy,
            StrategyIdentity = identity,
            Candidate = request.Candidate with
            {
                Identity = request.Candidate.Identity with
                {
                    StrategyContentSha256 = identity.ContentSha256
                }
            }
        };
    }

    private static StrategyDecisionRequest WithPaperAdapterProvenance(StrategyDecisionRequest request) =>
        request with
        {
            Candidate = request.Candidate with
            {
                Discovery = request.Candidate.Discovery with
                {
                    AggregateId = Guid.NewGuid(),
                    AggregateVersion = 17,
                    Sources = ["durable_discovery:finviz"],
                    EvidenceSha256 = new string('a', 64)
                },
                MarketState = request.Candidate.MarketState with
                {
                    VerifiedAtUtc = request.Candidate.MarketState.VerifiedAtUtc.AddSeconds(1)
                },
                Universe = request.Candidate.Universe with
                {
                    Source = "durable_discovery_universe",
                    EvidenceId = "paper-universe",
                    EvaluatedAtUtc = request.Candidate.Universe.EvaluatedAtUtc.AddSeconds(1),
                    EvidenceJson = "{\"eligible\":true,\"adapter\":\"paper\"}"
                },
                Regime = request.Candidate.Regime with
                {
                    Source = "shared_regime_gate",
                    EvidenceId = "paper-regime",
                    EvaluatedAtUtc = request.Candidate.Regime.EvaluatedAtUtc.AddSeconds(1),
                    EvidenceJson = "{\"eligible\":true,\"adapter\":\"paper\"}"
                }
            }
        };
}
