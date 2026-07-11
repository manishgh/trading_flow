using TradingFlow.Domain.Market;

namespace TradingFlow.Engine.Catalysts;

/// <summary>
/// Shared catalyst one-shot lifecycle (docs/edge-recovery-master-plan.md Phase 1, doctrine §6B).
/// Turns a catalyst from a state that is re-tradable every bar (the V3 churn anti-pattern) into an
/// EVENT that gets exactly one bounded attempt:
///  - eligibility starts at the RECEIVED time (when the system could actually act), not the published
///    time; when received time is unknown it falls back to published time with an audit flag;
///  - confirmation may begin only on the first candle STRICTLY AFTER the eligibility time (never the
///    still-forming bar that already contains the news move — that would be lookahead);
///  - confirmation is evaluated within a bounded window of N candles; and
///  - each catalyst, deduped by story, is CONSUMED after a single attempt, so one story can never
///    produce more than one trade.
/// This service holds only the lifecycle rules + consumption set; it is used identically by the
/// backtest and live runners (one brain). Persistence for paper/live is layered on top separately.
/// </summary>
public sealed class CatalystEligibilityService
{
    private readonly int confirmationWindowBars;
    private readonly HashSet<CatalystKey> attempted = new();

    public CatalystEligibilityService(int confirmationWindowBars = 6)
    {
        this.confirmationWindowBars = Math.Max(1, confirmationWindowBars);
    }

    /// <summary>Dedupe key: the provider's ExternalId when present, else the normalized headline,
    /// scoped to the ticker. The same story from two providers maps to one catalyst.</summary>
    public static CatalystKey KeyFor(CatalystEvent catalyst)
    {
        var story = !string.IsNullOrWhiteSpace(catalyst.ExternalId)
            ? catalyst.ExternalId!.Trim()
            : NormalizeHeadline(catalyst.Headline);
        return new CatalystKey(catalyst.Ticker.Trim().ToUpperInvariant(), story);
    }

    /// <summary>Eligibility clock: the received time when known (that is when the system could act),
    /// else the published time with UsedPublishedFallback = true (treat as research-only / audit).</summary>
    public static CatalystEligibility Eligibility(CatalystEvent catalyst) =>
        catalyst.ReceivedAt is { } received
            ? new CatalystEligibility(received, false)
            : new CatalystEligibility(catalyst.Timestamp, true);

    /// <summary>The bounded confirmation window: the first candle strictly after the eligibility time,
    /// spanning up to confirmationWindowBars candles. Null when no candle falls after eligibility.</summary>
    public ConfirmationWindow? ResolveWindow(CatalystEvent catalyst, IReadOnlyList<DateTimeOffset> orderedCandleTimestamps) =>
        ResolveWindow(catalyst, orderedCandleTimestamps, confirmationWindowBars);

    /// <summary>Static window resolution for the stateless signal path (same no-lookahead rule): first
    /// candle strictly after the eligibility time, spanning up to windowBars candles.</summary>
    public static ConfirmationWindow? ResolveWindow(
        CatalystEvent catalyst,
        IReadOnlyList<DateTimeOffset> orderedCandleTimestamps,
        int windowBars)
    {
        var bars = Math.Max(1, windowBars);
        var eligibleAt = Eligibility(catalyst).EligibleAt;
        for (var i = 0; i < orderedCandleTimestamps.Count; i++)
        {
            if (orderedCandleTimestamps[i] > eligibleAt)
            {
                var lastIndex = Math.Min(orderedCandleTimestamps.Count - 1, i + bars - 1);
                return new ConfirmationWindow(orderedCandleTimestamps[i], i, lastIndex);
            }
        }

        return null;
    }

    public bool IsWithinWindow(ConfirmationWindow window, int candleIndex) =>
        candleIndex >= window.FirstIndex && candleIndex <= window.LastIndexInclusive;

    /// <summary>One trade attempt per catalyst per ticker, ever. Returns true exactly once (the first
    /// time a story is attempted) and false forever after — this is the structural anti-churn guarantee.</summary>
    public bool TryBeginAttempt(CatalystEvent catalyst) => attempted.Add(KeyFor(catalyst));

    public bool IsConsumed(CatalystEvent catalyst) => attempted.Contains(KeyFor(catalyst));

    private static string NormalizeHeadline(string headline) =>
        string.Join(' ', (headline ?? string.Empty)
            .ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

public readonly record struct CatalystKey(string Ticker, string StoryHash);

public sealed record CatalystEligibility(DateTimeOffset EligibleAt, bool UsedPublishedFallback);

public sealed record ConfirmationWindow(DateTimeOffset FirstConfirmable, int FirstIndex, int LastIndexInclusive);
