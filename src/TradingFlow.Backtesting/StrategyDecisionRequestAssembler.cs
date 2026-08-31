using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using TradingFlow.Domain.Discovery;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Backtesting;

/// <summary>
/// Converts adapter-owned state into the immutable evidence contract consumed by
/// the strategy kernel. It truncates every series at the decision time so callers
/// cannot accidentally expose future bars to a backtest decision.
/// </summary>
public static class StrategyDecisionRequestAssembler
{
    public static StrategyDecisionRequest Create(
        AuthorizedRuntimeStrategy runtimeStrategy,
        string symbol,
        IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> barsByTimeframe,
        IReadOnlyDictionary<string, IReadOnlyList<IndicatorSnapshot>> snapshotsByTimeframe,
        IReadOnlyCollection<CatalystEvent> catalysts,
        DateTimeOffset asOfUtc,
        StrategyDiscoveryEvidence discovery,
        StrategyEligibilityEvidence universe,
        StrategyEligibilityEvidence regime,
        StrategyCandidateState candidateState,
        int candidateVersion,
        string setupKey,
        DateTimeOffset discoveryWindowStartUtc,
        DateTimeOffset discoveryWindowEndUtc,
        int requiredWarmupBars,
        Guid runId)
    {
        ArgumentNullException.ThrowIfNull(runtimeStrategy);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(setupKey);
        if (runId == Guid.Empty)
        {
            throw new ArgumentException("Candidate identity requires its owning run ID.", nameof(runId));
        }
        if (requiredWarmupBars < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredWarmupBars));
        }

        asOfUtc = RequireUtc(asOfUtc, nameof(asOfUtc));
        discoveryWindowStartUtc = RequireUtc(discoveryWindowStartUtc, nameof(discoveryWindowStartUtc));
        discoveryWindowEndUtc = RequireUtc(discoveryWindowEndUtc, nameof(discoveryWindowEndUtc));
        var requiredTimeframes = ResolveRequiredTimeframes(runtimeStrategy.Definition);
        var bars = ImmutableDictionary.CreateBuilder<string, ImmutableArray<OhlcvBar>>(StringComparer.OrdinalIgnoreCase);
        var snapshots = ImmutableDictionary.CreateBuilder<string, ImmutableArray<IndicatorSnapshot>>(StringComparer.OrdinalIgnoreCase);
        var counts = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.OrdinalIgnoreCase);
        var warmups = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var timeframe in requiredTimeframes)
        {
            if (!barsByTimeframe.TryGetValue(timeframe, out var sourceBars) ||
                !snapshotsByTimeframe.TryGetValue(timeframe, out var sourceSnapshots))
            {
                throw new InvalidOperationException($"Required timeframe {timeframe} is unavailable for {symbol}.");
            }

            var duration = TimeframeParser.Parse(timeframe);
            var completedBars = sourceBars
                .Where(bar => bar.Timestamp.Add(duration) <= asOfUtc &&
                    (bar.KnownAtUtc is null || bar.KnownAtUtc <= asOfUtc))
                .OrderBy(bar => bar.Timestamp)
                .ToImmutableArray();
            var completedSnapshots = sourceSnapshots
                .Where(snapshot => snapshot.Timestamp.Add(duration) <= asOfUtc)
                .OrderBy(snapshot => snapshot.Timestamp)
                .ToImmutableArray();
            bars[timeframe] = completedBars;
            snapshots[timeframe] = completedSnapshots;
            counts[timeframe] = Math.Min(completedBars.Length, completedSnapshots.Length);
            warmups[timeframe] = requiredWarmupBars;
        }

        var setupBars = bars[runtimeStrategy.Definition.Timeframe];
        var setupSnapshots = snapshots[runtimeStrategy.Definition.Timeframe];
        if (setupBars.IsDefaultOrEmpty || setupSnapshots.IsDefaultOrEmpty)
        {
            throw new InvalidOperationException($"No completed setup evidence is available for {symbol} at {asOfUtc:O}.");
        }

        var latestBar = setupBars[^1];
        var latestSnapshot = setupSnapshots[^1];
        var marketState = new StrategyMarketStateEvidence(
            requiredTimeframes.All(timeframe => counts[timeframe] >= warmups[timeframe]),
            EvidenceCanonicalJson.ComputeSha256(new
            {
                symbol,
                asOfUtc,
                series = requiredTimeframes.Select(timeframe => new
                {
                    timeframe,
                    count = counts[timeframe],
                    lastBar = bars[timeframe].LastOrDefault()?.Timestamp,
                    lastSnapshot = snapshots[timeframe].LastOrDefault()?.Timestamp
                })
            }),
            "alpaca",
            latestBar.DataFeed,
            latestBar.AdjustmentPolicy,
            latestSnapshot.MarketEvidenceReliability ?? String.Empty,
            asOfUtc,
            counts.ToImmutable(),
            warmups.ToImmutable());
        var identity = new StrategyCandidateIdentity(
            symbol.Trim().ToUpperInvariant(),
            runtimeStrategy.Identity.ContentSha256,
            setupKey.Trim(),
            discoveryWindowStartUtc,
            discoveryWindowEndUtc);

        return new StrategyDecisionRequest(
            runtimeStrategy.Identity,
            runtimeStrategy.AdmissionProfile,
            runtimeStrategy.Definition,
            new StrategyCandidateSnapshot(
                CreateDeterministicId(new { runId, identity }),
                identity,
                discovery,
                marketState,
                universe,
                regime,
                candidateState,
                candidateVersion),
            bars.ToImmutable(),
            snapshots.ToImmutable(),
            catalysts.Select(ToCatalystSnapshot)
                .OfType<StrategyCatalystSnapshot>()
                .ToImmutableArray(),
            asOfUtc);
    }

    public static AuthorizedRuntimeStrategy CreateBacktestRuntimeStrategy(
        StrategyDefinition strategy,
        StrategyArtifactCatalog strategyArtifacts)
    {
        var exact = strategyArtifacts.GetSnapshot().ExecutableArtifacts
            .Where(artifact => artifact.Identity.StrategyId.Equals(strategy.StrategyId, StringComparison.Ordinal))
            .FirstOrDefault(artifact => artifact.ResolvedStrategy == strategy);
        var admissionProfile = exact?.AdmissionProfile ?? StrategyAdmissionProfiles.DeterministicV1;
        var semanticVersion = exact?.Identity.SemanticVersion ?? "0.0.0-backtest";
        var identity = exact?.Identity ?? new StrategyArtifactIdentity(
            strategy.StrategyId,
            semanticVersion,
            EvidenceCanonicalJson.ComputeSha256(new CanonicalStrategyDocument(
                strategy.StrategyId,
                semanticVersion,
                strategy,
                admissionProfile)));
        return new AuthorizedRuntimeStrategy(
            identity,
            StrategySelectionMode.Backtest,
            strategy,
            admissionProfile);
    }

    public static StrategyDiscoveryEvidence CreateBacktestDiscovery(
        string symbol,
        string setupKey,
        DateTimeOffset observedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        var evidence = new
        {
            source = "backtest_candidate_journal",
            symbol = symbol.Trim().ToUpperInvariant(),
            setupKey,
            observedAtUtc,
            expiresAtUtc
        };
        return new StrategyDiscoveryEvidence(
            CreateDeterministicId(evidence),
            1,
            false,
            true,
            observedAtUtc,
            expiresAtUtc,
            ["backtest_candidate_journal"],
            EvidenceCanonicalJson.ComputeSha256(evidence));
    }

    public static StrategyDiscoveryEvidence CreateLiveDiscovery(
        ActiveDiscoveryAggregate aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        return new StrategyDiscoveryEvidence(
            aggregate.AggregateId,
            aggregate.Version,
            true,
            true,
            aggregate.LastObservedAtUtc.ToUniversalTime(),
            aggregate.ExpiresAtUtc.ToUniversalTime(),
            aggregate.Sources
                .Select(source => $"{source.SourceKind}:{source.SourceKey}")
                .OrderBy(source => source, StringComparer.Ordinal)
                .ToImmutableArray(),
            EvidenceCanonicalJson.ComputeSha256(aggregate));
    }

    public static StrategyDiscoveryEvidence CreateConfiguredLiveDiscovery(
        Guid runId,
        string symbol,
        DateTimeOffset observedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        var evidence = new
        {
            source = "configured_run_universe",
            runId,
            symbol = symbol.Trim().ToUpperInvariant(),
            observedAtUtc,
            expiresAtUtc
        };
        return new StrategyDiscoveryEvidence(
            CreateDeterministicId(evidence),
            1,
            false,
            true,
            observedAtUtc,
            expiresAtUtc,
            ["configured_run_universe"],
            EvidenceCanonicalJson.ComputeSha256(evidence));
    }

    public static StrategyDiscoveryEvidence CreateAlertDiscovery(
        Guid runId,
        string symbol,
        string source,
        DateTimeOffset observedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        var evidence = new
        {
            source = source.Trim().ToLowerInvariant(),
            runId,
            symbol = symbol.Trim().ToUpperInvariant(),
            observedAtUtc,
            expiresAtUtc
        };
        return new StrategyDiscoveryEvidence(
            CreateDeterministicId(evidence),
            1,
            false,
            true,
            observedAtUtc,
            expiresAtUtc,
            [$"alert:{source.Trim().ToLowerInvariant()}"],
            EvidenceCanonicalJson.ComputeSha256(evidence));
    }

    public static TradeSignal CreateExecutionSignal(
        TradeSignal signal,
        StrategyOrderPlan orderPlan,
        IReadOnlyList<IndicatorSnapshot> executionSnapshots)
    {
        var duration = TimeframeParser.Parse(orderPlan.ExecutionTimeframe);
        var triggerBarTimestamp = orderPlan.TriggeredAtUtc.Subtract(duration);
        var executionSnapshot = executionSnapshots.LastOrDefault(snapshot =>
            snapshot.Timestamp == triggerBarTimestamp)
            ?? throw new InvalidOperationException(
                $"Triggered execution evidence {orderPlan.ExecutionTimeframe} at {triggerBarTimestamp:O} is unavailable.");
        return signal with
        {
            Timestamp = executionSnapshot.Timestamp,
            Timeframe = executionSnapshot.Timeframe,
            CurrentPrice = orderPlan.TriggerReferencePrice,
            CurrentVolume = executionSnapshot.CurrentVolume,
            CurrentRsi = executionSnapshot.Rsi ?? signal.CurrentRsi,
            CurrentAtr = executionSnapshot.Atr ?? signal.CurrentAtr,
            SetupStructuralStopPrice = orderPlan.InitialStopPrice
        };
    }

    public static Guid CreateDeterministicId(object value)
    {
        var hash = SHA256.HashData(EvidenceCanonicalJson.SerializeToUtf8Bytes(value));
        return new Guid(hash.AsSpan(0, 16));
    }

    public static string CreateSetupKey(
        string strategyId,
        string signalTimeframe,
        DateTimeOffset signalBarTimestamp) =>
        $"{strategyId.Trim()}:{signalTimeframe.Trim().ToLowerInvariant()}:" +
        $"{signalBarTimestamp.ToUniversalTime():O}";

    private static StrategyCatalystSnapshot? ToCatalystSnapshot(CatalystEvent catalyst)
    {
        if (catalyst.ReceivedAt is not { } receivedAt ||
            catalyst.DecisionAvailableAt is not { } decisionKnownAt ||
            String.IsNullOrWhiteSpace(catalyst.ClassificationVersion) ||
            String.IsNullOrWhiteSpace(
                catalyst.DecisionAvailabilityEvidence ?? catalyst.AvailabilityEvidence) ||
            decisionKnownAt < receivedAt)
        {
            return null;
        }

        var provider = catalyst.Provider?.Trim().ToLowerInvariant() ?? "unknown";
        var articleId = !String.IsNullOrWhiteSpace(catalyst.ExternalId)
            ? catalyst.ExternalId.Trim()
            : EvidenceCanonicalJson.ComputeSha256(new
            {
                provider,
                catalyst.Ticker,
                catalyst.Timestamp,
                catalyst.Headline,
                catalyst.Url
            });
        var revision = EvidenceCanonicalJson.ComputeSha256(catalyst);
        var deduplicationKey = EvidenceCanonicalJson.ComputeSha256(new
        {
            provider,
            ticker = catalyst.Ticker.Trim().ToUpperInvariant(),
            headline = catalyst.Headline.Trim().ToUpperInvariant(),
            catalyst.Url
        });
        return new StrategyCatalystSnapshot(
            provider,
            articleId,
            revision,
            catalyst.Timestamp.ToUniversalTime(),
            catalyst.UpdatedAt?.ToUniversalTime(),
            receivedAt.ToUniversalTime(),
            decisionKnownAt.ToUniversalTime(),
            catalyst.ClassificationVersion,
            catalyst.DecisionAvailabilityEvidence ?? catalyst.AvailabilityEvidence!,
            deduplicationKey,
            catalyst);
    }

    private static string[] ResolveRequiredTimeframes(StrategyDefinition strategy)
    {
        var timeframes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            strategy.Timeframe,
            strategy.Execution.Timeframe
        };
        if (strategy.Confluence.Enabled)
        {
            timeframes.Add(strategy.Confluence.Timeframe);
        }

        return timeframes.OrderBy(TimeframeParser.Parse).ToArray();
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value, string name) =>
        value != default && value.Offset == TimeSpan.Zero
            ? value
            : throw new InvalidOperationException($"{name} must be UTC.");
}
