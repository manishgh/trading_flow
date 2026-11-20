using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using TradingFlow.Domain.Discovery;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Backtesting;

/// <summary>
/// A session's provisional daily bar and the indicator snapshot computed with it, known at the decision time
/// (DecisionHourDailyBars): the setup evidence of a decision made before the session's final close exists.
/// </summary>
public sealed record ProvisionalSetupObservation(OhlcvBar Bar, IndicatorSnapshot Snapshot);

/// <summary>
/// Converts adapter-owned state into the immutable evidence contract consumed by
/// the strategy kernel. It truncates every series at the decision time so callers
/// cannot accidentally expose future bars to a backtest decision.
/// </summary>
public static class StrategyDecisionRequestAssembler
{
    // Freeze completed adapter evidence and preserve the runtime selection mode for isolated Research diagnostics.
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
        Guid runId,
        IReadOnlyList<OhlcvBar>? referenceBars = null,
        IReadOnlyList<DateOnly>? expectedReferenceSessions = null,
        DrawdownDecisionContext? drawdownContext = null,
        HistoricalNewsPresenceContext? historicalNewsPresence = null,
        IntradayBreakoutContext? previousSessionContext = null,
        ProvisionalSetupObservation? provisionalSetup = null,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? sessionSchedules = null,
        decimal? optionsSkew25Delta = null)
    {
        ArgumentNullException.ThrowIfNull(runtimeStrategy);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(setupKey);
        if (runId == Guid.Empty)
        {
            throw new ArgumentException("Candidate identity requires its owning run ID.", nameof(runId));
        }
        requiredWarmupBars = Math.Max(requiredWarmupBars, runtimeStrategy.Definition.EntryRules.TechnicalFactor?.RequiredCompletedBars ?? 0);
        // The squeeze ranks the previous bar's 20-session Bollinger width among its look-back widths: the earliest of those
        // widths needs 20 completed bars of its own, so look-back + 20 bars must exist before the setup can be judged.
        if (runtimeStrategy.Definition.EntryRules.BollingerSqueezeLookbackBars > 0)
            requiredWarmupBars = Math.Max(requiredWarmupBars, runtimeStrategy.Definition.EntryRules.BollingerSqueezeLookbackBars + 20);
        // Rising/falling SMA-50 requires both the current SMA-50 and the value N bars earlier (50 + N completed bars).
        if (runtimeStrategy.Definition.EntryRules.Sma50RisingLookbackBars > 0)
            requiredWarmupBars = Math.Max(requiredWarmupBars, 50 + runtimeStrategy.Definition.EntryRules.Sma50RisingLookbackBars);
        if (runtimeStrategy.Definition.EntryRules.Sma50FallingLookbackBars > 0)
            requiredWarmupBars = Math.Max(requiredWarmupBars, 50 + runtimeStrategy.Definition.EntryRules.Sma50FallingLookbackBars);
        if (runtimeStrategy.Definition.EntryRules.RequirePriceBelowSma50ForShort || runtimeStrategy.Definition.EntryRules.RequireBounceHighBelowSma50)
            requiredWarmupBars = Math.Max(requiredWarmupBars, 50 + Math.Max(0, runtimeStrategy.Definition.EntryRules.PullbackLookbackBars));
        if (runtimeStrategy.Definition.EntryRules.MinParabolicAtrAboveEma21 is not null)
            requiredWarmupBars = Math.Max(requiredWarmupBars, 22);
        // Strategy-owned ADR% and dollar-volume filters measure a 20-session rolling window matching DailyScreen.
        if (runtimeStrategy.Definition.EntryRules.MinAverageRangePct is not null ||
            runtimeStrategy.Definition.EntryRules.MinAverageDollarVolume is not null)
            requiredWarmupBars = Math.Max(requiredWarmupBars, 20);
        // Daily opening-range volume baseline needs 20 prior sessions plus the signal session (matching LongestIndicatorLookback).
        if (runtimeStrategy.Definition.EntryRules.OpeningRangeMinutes > 0)
            requiredWarmupBars = Math.Max(requiredWarmupBars, 21);
        // ORB needs its three completed five-minute range bars; its twenty-session volume qualification remains in the shared evaluator.
        if (runtimeStrategy.Definition.EntryRules.OpeningRangeBreakout is not null)
            requiredWarmupBars = Math.Max(requiredWarmupBars, 3);
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
            // Auxiliary 5m bars on a daily strategy supply sub-session opening-range confirmation; when absent on early
            // backtest days (e.g. a later-eligible screen stock), treat as empty so counts["5m"] = 0 < warmups["5m"]
            // fails closed cleanly via market_state_not_warm instead of aborting the stock's backtest work item.
            var isDailyAuxiliaryFiveMinute =
                runtimeStrategy.Definition.EntryRules.OpeningRangeMinutes > 0 &&
                runtimeStrategy.Definition.Timeframe.Equals("1d", StringComparison.OrdinalIgnoreCase) &&
                timeframe.Equals("5m", StringComparison.OrdinalIgnoreCase);
            var hasBars = barsByTimeframe.TryGetValue(timeframe, out var sourceBars);
            var hasSnapshots = snapshotsByTimeframe.TryGetValue(timeframe, out var sourceSnapshots);
            if (!hasBars || !hasSnapshots)
            {
                if (!isDailyAuxiliaryFiveMinute)
                {
                    throw new InvalidOperationException($"Required timeframe {timeframe} is unavailable for {symbol}.");
                }

                sourceBars = Array.Empty<OhlcvBar>();
                sourceSnapshots = Array.Empty<IndicatorSnapshot>();
            }

            var duration = TimeframeParser.Parse(timeframe);
            var (completedBars, completedSnapshots) = SliceCompletedSeriesFastOrFallback(
                symbol,
                timeframe,
                sourceBars!,
                sourceSnapshots!,
                duration,
                asOfUtc);
            if (provisionalSetup is not null && timeframe.Equals(runtimeStrategy.Definition.Timeframe, StringComparison.OrdinalIgnoreCase))
                (completedBars, completedSnapshots) = AppendProvisional(completedBars, completedSnapshots, provisionalSetup, asOfUtc);
            bars[timeframe] = completedBars;
            snapshots[timeframe] = completedSnapshots;
            counts[timeframe] = Math.Min(completedBars.Length, completedSnapshots.Length);
            warmups[timeframe] = isDailyAuxiliaryFiveMinute
                ? Math.Max(2, runtimeStrategy.Definition.EntryRules.OpeningRangeMinutes / 5)
                : requiredWarmupBars;
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
            asOfUtc,
            runtimeStrategy.Definition.EntryRules.TechnicalFactor?.Metric == TechnicalFactorRules.RelativeRoc
                ? CompletedReferenceBars(referenceBars ?? Array.Empty<OhlcvBar>(), asOfUtc) : null,
            runtimeStrategy.Definition.EntryRules.TechnicalFactor?.Metric == TechnicalFactorRules.RelativeRoc
                ? expectedReferenceSessions?.ToImmutableArray() : null,
            drawdownContext,
            historicalNewsPresence,
            runtimeStrategy.SelectionMode,
            previousSessionContext,
            SessionSchedules: runtimeStrategy.Definition.EntryRules.OpeningRangeMinutes > 0 && sessionSchedules is not null
                ? sessionSchedules.ToImmutableDictionary()
                : null,
            OptionsSkew25Delta: runtimeStrategy.Definition.EntryRules.MaxLongPutSkew is not null ||
                                runtimeStrategy.Definition.EntryRules.MinShortPutCallSkew is not null
                ? optionsSkew25Delta
                : null);
    }

    // Fast path for already-ordered, timestamp-aligned bar/snapshot series (avoiding O(M log M) LINQ sorting and
    // tuple allocations on every daily evaluation when 5m bars are attached). Falls back to full LINQ sort +
    // RequireCausalSnapshotAlignment if the input is unordered, misaligned, or contains a receipt-delayed bar.
    private static (ImmutableArray<OhlcvBar> Bars, ImmutableArray<IndicatorSnapshot> Snapshots) SliceCompletedSeriesFastOrFallback(
        string symbol,
        string timeframe,
        IReadOnlyList<OhlcvBar> sourceBars,
        IReadOnlyList<IndicatorSnapshot> sourceSnapshots,
        TimeSpan duration,
        DateTimeOffset asOfUtc)
    {
        if (TrySliceAlignedPrefixFast(sourceBars, sourceSnapshots, duration, asOfUtc, out var fastBars, out var fastSnapshots))
        {
            return (fastBars, fastSnapshots);
        }

        var completedBars = sourceBars
            .Where(bar => bar.Timestamp.Add(duration) <= asOfUtc &&
                (bar.KnownAtUtc is null || bar.KnownAtUtc <= asOfUtc))
            .OrderBy(bar => bar.Timestamp)
            .ToImmutableArray();
        var completedSnapshots = sourceSnapshots
            .Where(snapshot => snapshot.Timestamp.Add(duration) <= asOfUtc)
            .OrderBy(snapshot => snapshot.Timestamp)
            .ToImmutableArray();
        RequireCausalSnapshotAlignment(symbol, timeframe, sourceBars, completedBars, completedSnapshots, duration, asOfUtc);
        return (completedBars, completedSnapshots);
    }

    // Attempts a single-pass O(K) prefix slice when sourceBars and sourceSnapshots have matching counts, strictly
    // ascending aligned timestamps, no later bar completes at or before asOfUtc, and no completed bar has KnownAtUtc > asOfUtc.
    private static bool TrySliceAlignedPrefixFast(
        IReadOnlyList<OhlcvBar> sourceBars,
        IReadOnlyList<IndicatorSnapshot> sourceSnapshots,
        TimeSpan duration,
        DateTimeOffset asOfUtc,
        out ImmutableArray<OhlcvBar> completedBars,
        out ImmutableArray<IndicatorSnapshot> completedSnapshots)
    {
        completedBars = default;
        completedSnapshots = default;
        var count = sourceBars.Count;
        if (count != sourceSnapshots.Count)
        {
            return false;
        }

        if (count == 0)
        {
            completedBars = ImmutableArray<OhlcvBar>.Empty;
            completedSnapshots = ImmutableArray<IndicatorSnapshot>.Empty;
            return true;
        }

        // Binary-search the end of the completed prefix (assuming ascending order; verified right below).
        var lo = 0;
        var hi = count - 1;
        var prefixLength = 0;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (sourceBars[mid].Timestamp.Add(duration) <= asOfUtc)
            {
                prefixLength = mid + 1;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        // Confirm the suffix is also strictly ascending and never completes at or before asOfUtc so an out-of-order
        // bar in the tail always falls back to the full sort + alignment check.
        var suffixPrev = prefixLength > 0 ? sourceBars[prefixLength - 1].Timestamp : DateTimeOffset.MinValue;
        for (var i = prefixLength; i < count; i++)
        {
            var barTs = sourceBars[i].Timestamp;
            if (barTs <= suffixPrev ||
                sourceSnapshots[i].Timestamp != barTs ||
                barTs.Add(duration) <= asOfUtc)
            {
                return false;
            }

            suffixPrev = barTs;
        }

        var barBuilder = ImmutableArray.CreateBuilder<OhlcvBar>(prefixLength);
        var snapshotBuilder = ImmutableArray.CreateBuilder<IndicatorSnapshot>(prefixLength);
        var previousTimestamp = DateTimeOffset.MinValue;
        for (var i = 0; i < prefixLength; i++)
        {
            var bar = sourceBars[i];
            var snap = sourceSnapshots[i];
            if (bar.Timestamp <= previousTimestamp ||
                snap.Timestamp != bar.Timestamp ||
                bar.Timestamp.Add(duration) > asOfUtc ||
                (bar.KnownAtUtc is { } knownAt && knownAt > asOfUtc))
            {
                return false;
            }

            barBuilder.Add(bar);
            snapshotBuilder.Add(snap);
            previousTimestamp = bar.Timestamp;
        }

        completedBars = barBuilder.MoveToImmutable();
        completedSnapshots = snapshotBuilder.MoveToImmutable();
        return true;
    }

    // A snapshot can depend on an earlier unavailable bar; timestamp filtering cannot reconstruct those indicator dependencies.
    private static void RequireCausalSnapshotAlignment(string symbol, string timeframe, IReadOnlyList<OhlcvBar> sourceBars,
        ImmutableArray<OhlcvBar> completedBars, ImmutableArray<IndicatorSnapshot> completedSnapshots,
        TimeSpan duration, DateTimeOffset asOfUtc)
    {
        var lastSnapshot = completedSnapshots.LastOrDefault()?.Timestamp;
        var hiddenDependency = sourceBars.FirstOrDefault(bar => bar.Timestamp.Add(duration) <= asOfUtc &&
            bar.KnownAtUtc is { } receipt && receipt > asOfUtc && lastSnapshot is { } latest && bar.Timestamp <= latest);
        if (hiddenDependency is null && completedBars.Length == completedSnapshots.Length &&
            completedBars.Zip(completedSnapshots).All(pair => pair.First.Timestamp == pair.Second.Timestamp)) return;
        var detail = hiddenDependency is null
            ? "completed bars and indicator snapshots do not contain the same ordered source observations"
            : $"source {hiddenDependency.Timestamp:O} is not known until {hiddenDependency.KnownAtUtc:O}, so its dependent indicator snapshots cannot be used";
        throw new InvalidOperationException($"point_in_time_indicator_snapshots_unavailable: {symbol} {timeframe} at {asOfUtc:O}: {detail}.");
    }

    /// <summary>
    /// Appends a session's provisional setup bar and its snapshot (decision-hour timing) after the completed series. The bar
    /// must be known exactly at the decision time and open after the last completed bar; nothing later is ever appended.
    /// </summary>
    private static (ImmutableArray<OhlcvBar> Bars, ImmutableArray<IndicatorSnapshot> Snapshots) AppendProvisional(
        ImmutableArray<OhlcvBar> completedBars, ImmutableArray<IndicatorSnapshot> completedSnapshots,
        ProvisionalSetupObservation provisional, DateTimeOffset asOfUtc)
    {
        if (provisional.Bar.KnownAtUtc != asOfUtc || provisional.Snapshot.Timestamp != provisional.Bar.Timestamp ||
            provisional.Bar.Timestamp >= asOfUtc || completedBars.LastOrDefault() is { } last && last.Timestamp >= provisional.Bar.Timestamp)
            throw new InvalidOperationException("A provisional setup bar must be known at the decision time and follow the completed bars.");
        return (completedBars.Add(provisional.Bar), completedSnapshots.Add(provisional.Snapshot));
    }

    public static async ValueTask<DrawdownDecisionContext?> ResolveDrawdownContextAsync(
        IStrategyDecisionContextProvider? provider, string symbol, DateTimeOffset asOfUtc,
        IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> barsByTimeframe, string mode,
        CancellationToken cancellationToken = default)
    {
        if (provider is null) return null;
        var daily = barsByTimeframe.TryGetValue("1d", out var source) ? source : Array.Empty<OhlcvBar>();
        var completed = daily.Where(bar => bar.Timestamp.AddDays(1) <= asOfUtc &&
            (bar.KnownAtUtc is null || bar.KnownAtUtc <= asOfUtc)).OrderBy(bar => bar.Timestamp).ToImmutableArray();
        return await provider.ResolveAsync(new(symbol, RequireUtc(asOfUtc, nameof(asOfUtc)), completed, mode), cancellationToken);
    }

    private static ImmutableArray<OhlcvBar> CompletedReferenceBars(IReadOnlyList<OhlcvBar> source, DateTimeOffset asOfUtc) =>
        source.Where(bar => bar.Timestamp.AddDays(1) <= asOfUtc && (bar.KnownAtUtc is null || bar.KnownAtUtc <= asOfUtc))
            .GroupBy(bar => bar.Timestamp).OrderBy(group => group.Key).Select(group =>
            {
                var latestKnown = group.Max(bar => bar.KnownAtUtc ?? bar.Timestamp.AddDays(1));
                var versions = group.Where(bar => (bar.KnownAtUtc ?? bar.Timestamp.AddDays(1)) == latestKnown).Distinct().ToArray();
                if (versions.Length != 1) throw new InvalidDataException("Benchmark revision order is ambiguous.");
                return versions[0];
            }).ToImmutableArray();

    public static AuthorizedRuntimeStrategy CreateBacktestRuntimeStrategy(
        StrategyDefinition strategy,
        StrategyArtifactCatalog strategyArtifacts)
    {
        var exact = strategyArtifacts.GetSnapshot().ExecutableArtifacts
            .SingleOrDefault(artifact => artifact.ResolvedStrategy == strategy);
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

    // Projects a completed setup signal onto the planner's validated execution stop-context snapshot, preserving
    // volatility units (ATR and, when enabled, MTR-14) and binding the resolved structural stop price.
    public static TradeSignal CreateExecutionSignal(
        TradeSignal signal,
        StrategyOrderPlan orderPlan,
        IReadOnlyList<IndicatorSnapshot> executionSnapshots)
    {
        var duration = TimeframeParser.Parse(orderPlan.ExecutionTimeframe);
        // The actionable decision can be later than its completed stop context.
        // Use the planner's context instead of inventing a bar from trigger time.
        var index = orderPlan.StopContextIndex;
        if (index < 0 || index >= executionSnapshots.Count)
            throw new InvalidOperationException("Triggered execution context is unavailable.");
        var executionSnapshot = executionSnapshots[index];
        if (executionSnapshot.Timestamp != orderPlan.StopContextTimestampUtc ||
            !executionSnapshot.Timeframe.Equals(orderPlan.ExecutionTimeframe, StringComparison.OrdinalIgnoreCase) ||
            executionSnapshot.Timestamp.Add(duration) > orderPlan.TriggeredAtUtc ||
            executionSnapshot.CurrentPrice != orderPlan.TriggerReferencePrice)
            throw new InvalidOperationException("Triggered execution context is mismatched or not completed before the decision.");
        return signal with
        {
            Timestamp = executionSnapshot.Timestamp,
            Timeframe = executionSnapshot.Timeframe,
            CurrentPrice = orderPlan.TriggerReferencePrice,
            CurrentVolume = executionSnapshot.CurrentVolume,
            CurrentRsi = executionSnapshot.Rsi ?? signal.CurrentRsi,
            CurrentAtr = executionSnapshot.Atr ?? signal.CurrentAtr,
            CurrentMtr14 = signal.CurrentMtr14 is not null ? executionSnapshot.Mtr14 ?? signal.CurrentMtr14 : null,
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

    /// <summary>Returns exactly the setup, execution, enabled confluence, and daily sub-session confirmation timeframes consumed by the shared decision kernel.</summary>
    public static string[] ResolveRequiredTimeframes(StrategyDefinition strategy)
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

        if (strategy.EntryRules.OpeningRangeMinutes > 0)
        {
            timeframes.Add("5m");
        }

        return timeframes.OrderBy(TimeframeParser.Parse).ToArray();
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value, string name) =>
        value != default && value.Offset == TimeSpan.Zero
            ? value
            : throw new InvalidOperationException($"{name} must be UTC.");
}
