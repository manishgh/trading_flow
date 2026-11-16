using System.Text.Json;
using TradingFlow.Alpaca;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Market;

namespace TradingFlow.Web.Services.Charts;

/// <summary>Holds ephemeral chart observations only for already tracked symbols on one leased SIP connection.</summary>
internal sealed class SipFormingBook(
    long fence, Func<DateTimeOffset, bool> hasLease, SipFormingCalendar calendar,
    int symbolLimit = 10_000, int connectionLimit = 100_000)
{
    private readonly object gate = new();
    private readonly Dictionary<string, Coverage> tracking = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SipFormingMinute> trades = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset minute;
    private BarResampler.BucketWindow? largestWindow;
    private int events;
    private bool retired;

    private readonly record struct MutationBound(DateTimeOffset LastPossibleChild, DateTimeOffset CanonicalAfter);

    private sealed class Coverage(long generation)
    {
        public long Generation { get; } = generation;
        public DateTimeOffset? AcknowledgedAt { get; set; }
        public DateTimeOffset? WarmAt { get; set; }
        public Dictionary<DateTimeOffset, DateTimeOffset> LateChildren { get; } = [];
        public MutationBound? UnresolvedMutation { get; set; }
        public AlpacaSipEventClock? LastEvent { get; set; }
        public DateTimeOffset? LastObservation { get; set; }

        // Both stream acknowledgement and successful reconciliation must precede a full observed minute.
        public bool Covers(DateTimeOffset start) => AcknowledgedAt is { } ack && WarmAt is { } warm &&
            start >= MinuteOf(ack > warm ? ack : warm).AddMinutes(1);
    }

    // Register or replace only the symbol generation that the existing stream subscribed.
    public void Track(string symbol, long generation)
    {
        lock (gate)
        {
            if (retired) return;
            RemoveTrades(symbol);
            tracking[symbol] = new(generation);
        }
    }

    // Retire observations before a tracked generation is unsubscribed from the provider.
    public void Untrack(string symbol)
    {
        lock (gate)
        {
            tracking.Remove(symbol);
            RemoveTrades(symbol);
        }
    }

    // Retire the entire preview before this connection hands off its ownership.
    public void Retire()
    {
        lock (gate)
        {
            retired = true;
            trades.Clear();
            tracking.Clear();
            events = 0;
        }
    }

    // The existing lease heartbeat also expires minute IDs on a completely silent socket.
    public void ExpireClosedMinute(DateTimeOffset now)
    {
        lock (gate) Advance(now);
    }

    // Invalidate observed coverage if the authoritative current session changed on refresh.
    public bool UpdateCalendar(SipFormingCalendar replacement, DateTimeOffset now)
    {
        lock (gate)
        {
            var same = calendar.SameSession(replacement, now);
            calendar = replacement;
            if (same) return false;
            calendar.TryReadWindow(now, "15m", out largestWindow);
            foreach (var (symbol, state) in tracking)
            {
                Reset(symbol, state);
                if (state.WarmAt is not null) state.WarmAt = now;
            }
            return true;
        }
    }

    // Let dedicated acceptance end immediately when the normal forming path invalidates any covered child.
    public bool HasUnreconciledObservation(string symbol)
    {
        lock (gate)
            return tracking.TryGetValue(symbol, out var state) &&
                (state.LateChildren.Count > 0 || state.UnresolvedMutation is not null);
    }

    // The existing subscription response must cover trades, corrections and cancel/errors for preview admission.
    public bool ApplyAcknowledgement(JsonElement message, DateTimeOffset observed)
    {
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("T", out var type) ||
            type.ValueKind != JsonValueKind.String || type.GetString() != "subscription") return false;
        var prints = ReadSymbols(message, "trades");
        var corrections = ReadSymbols(message, "corrections");
        var cancellations = ReadSymbols(message, "cancelErrors");
        lock (gate)
        {
            if (retired || !hasLease(observed)) return true;
            foreach (var (symbol, state) in tracking)
            {
                var covered = prints.Contains(symbol) && corrections.Contains(symbol) && cancellations.Contains(symbol);
                if (covered) state.AcknowledgedAt ??= observed;
                else if (state.AcknowledgedAt is not null)
                {
                    state.AcknowledgedAt = null;
                    Reset(symbol, state);
                }
            }
        }
        return true;
    }

    // A repair clears trade identity immediately. Successful completion starts a new full-minute window.
    public void MarkWarm(string symbol, long generation, DateTimeOffset? observed)
    {
        lock (gate)
        {
            if (!tracking.TryGetValue(symbol, out var state) || state.Generation != generation) return;
            Reset(symbol, state);
            state.WarmAt = observed;
        }
    }

    // Bind each message to its active scope, unclosed minute, and verified exchange segment.
    public void Observe(AlpacaSipTradeFrame frame, long generation, DateTimeOffset observed, DateTimeOffset now)
    {
        lock (gate)
        {
            if (!Advance(now) || largestWindow is null || !tracking.TryGetValue(frame.Symbol, out var state) ||
                state.Generation != generation || !state.Covers(MinuteOf(observed))) return;

            if (frame.Clock.Utc > observed)
            {
                var receiptMinute = MinuteOf(observed);
                if (receiptMinute >= largestWindow!.StartUtc)
                    Suppress(state, receiptMinute, observed, now, frame.Clock, true);
                return;
            }
            if (frame.Kind == AlpacaSipTradeKind.Print && MinuteOf(frame.Clock.Utc) != minute)
            {
                ObserveOtherMinute(frame, state, observed, now);
                return;
            }
            // A mutation timestamp is not its original execution timestamp. The retained ID decides membership.
            if (frame.Kind != AlpacaSipTradeKind.Print && MinuteOf(observed) < minute)
            {
                if (MinuteOf(observed) >= largestWindow!.StartUtc)
                    Suppress(state, MinuteOf(observed), observed, now, frame.Clock, true);
                return;
            }
            ApplyCurrent(frame, state, observed, now);
        }
    }

    // A known trade timestamp localizes a late report. An unidentified mutation may affect the entire bucket.
    private void ObserveOtherMinute(AlpacaSipTradeFrame frame, Coverage state, DateTimeOffset observed, DateTimeOffset now)
    {
        var target = MinuteOf(frame.Clock.Utc);
        if (target > minute)
        {
            Suppress(state, minute, observed, now, frame.Clock, true);
            return;
        }
        if (target < largestWindow!.StartUtc ||
            (AlpacaMarketDataMessageParser.TryResolveSipMinuteFields(frame.Tape, frame.Original.Conditions, out var fields) &&
             fields == AlpacaSipMinuteFields.None)) return;
        Suppress(state, target, observed, now, frame.Clock, false);
    }

    // A malformed relevant frame cannot silently become a missing print or correction.
    public void ObserveFailure(AlpacaSipTradeFailure failure, DateTimeOffset observed, DateTimeOffset now)
    {
        lock (gate)
        {
            if (!Advance(now) || largestWindow is null) return;
            if (failure.Symbol is { } symbol)
            {
                if (tracking.TryGetValue(symbol, out var state)) SuppressFailure(failure, state, observed, now);
                return;
            }
            foreach (var state in tracking.Values) SuppressFailure(failure, state, observed, now);
        }
    }

    // Without a usable trade clock or mutation identity, no single preceding child can be selected safely.
    private void SuppressFailure(AlpacaSipTradeFailure failure, Coverage state, DateTimeOffset observed, DateTimeOffset now)
    {
        var receiptMinute = MinuteOf(observed);
        if (!state.Covers(receiptMinute)) return;
        var target = failure.Clock is { } time ? MinuteOf(time.Utc) : minute;
        var uncertain = failure.CouldBeMutation || failure.Clock is null || target > minute;
        if (uncertain) target = receiptMinute;
        if (target >= largestWindow!.StartUtc) Suppress(state, target, observed, now, failure.Clock, uncertain);
    }

    // Bound distinct incoming events before retaining IDs or updating a subtotal.
    private void ApplyCurrent(AlpacaSipTradeFrame frame, Coverage state, DateTimeOffset observed, DateTimeOffset now)
    {
        trades.TryGetValue(frame.Symbol, out var tape);
        if (tape?.Seen(frame) == true) return;
        if (state.LateChildren.ContainsKey(minute) || state.UnresolvedMutation?.LastPossibleChild == minute)
        {
            Suppress(state, minute, observed, now, frame.Clock, frame.Kind != AlpacaSipTradeKind.Print);
            return;
        }
        if (tape is null) trades.Add(frame.Symbol, tape = new());
        if (tape.Events >= symbolLimit || events >= connectionLimit)
        {
            Suppress(state, minute, observed, now, frame.Clock, frame.Kind != AlpacaSipTradeKind.Print);
            return;
        }
        if (!tape.TryApply(frame))
        {
            Suppress(state, minute, observed, now, frame.Clock, frame.Kind != AlpacaSipTradeKind.Print);
            return;
        }
        events++;
        ObserveClocks(state, frame.Clock, observed);
    }

    // Preserve a bounded obstruction for every earlier child affected by late or unresolved evidence.
    private void Suppress(Coverage state, DateTimeOffset target, DateTimeOffset observed, DateTimeOffset now,
        AlpacaSipEventClock? clock, bool ambiguous)
    {
        var added = !state.LateChildren.TryGetValue(target, out var prior);
        if (added)
        {
            foreach (var expired in state.LateChildren.Keys.Where(stamp => stamp < largestWindow!.StartUtc).ToArray())
                state.LateChildren.Remove(expired);
        }
        // Canonical KnownAt is its dispatch clock. A bar dispatched earlier in this batch cannot prove reconciliation.
        if (added || now > prior) state.LateChildren[target] = now;
        if (ambiguous)
        {
            var previous = state.UnresolvedMutation;
            added |= previous is null || target > previous.Value.LastPossibleChild;
            state.UnresolvedMutation = previous is { } mutation
                ? new(target > mutation.LastPossibleChild ? target : mutation.LastPossibleChild,
                    now > mutation.CanonicalAfter ? now : mutation.CanonicalAfter)
                : new(target, now);
        }
        if (added) ObserveClocks(state, clock, observed);
    }

    // Last provider event and last receipt are separate clocks, including when event order differs from arrival.
    private static void ObserveClocks(Coverage state, AlpacaSipEventClock? clock, DateTimeOffset observed)
    {
        if (clock is { } value && (state.LastEvent is null || value.CompareTo(state.LastEvent.Value) > 0))
            state.LastEvent = value;
        if (state.LastObservation is null || observed > state.LastObservation) state.LastObservation = observed;
    }

    // Produce one watch response from the independent preview and the completed read already made for this tick.
    public ChartFormingCandle Read(ChartSeriesKey key, ChartCanonicalSession? canonical, DateTimeOffset now)
    {
        lock (gate)
        {
            if (retired || !hasLease(now) || !tracking.TryGetValue(key.Symbol, out var state)) return new("not_streaming");
            if (key.Interval is not ("1m" or "5m" or "15m")) return new("unsupported");
            if (!Advance(now) || !calendar.TryReadWindow(now, key.Interval, out var bucket))
                return Result("coverage_unverified", state);
            if (bucket is null || (key.SessionView == "regular" && bucket.SessionSegment != "regular"))
                return Result("closed", state);
            if (canonical is null || canonical.NeedsRepair || canonical.OwnershipFence != fence ||
                canonical.SubscriptionGeneration != state.Generation || !state.Covers(minute))
                return Result("coverage_unverified", state, bucket);
            if (RequiredReceipt(state, minute, bucket.StartUtc) is not null)
                return Result("suppressed", state, bucket);
            if (!trades.TryGetValue(key.Symbol, out var tape) || tape.ReadPrices() is not { } subtotal)
                return Result("waiting_for_trade", state, bucket);
            return ReadBucket(key.Symbol, canonical, state, bucket, subtotal, now);
        }
    }

    // Earlier minute trade IDs are gone. Only a usable completed canonical child or explicit quiet proof can replace them.
    private ChartFormingCandle ReadBucket(string symbol, ChartCanonicalSession canonical, Coverage state,
        BarResampler.BucketWindow bucket, SipMinutePrices subtotal, DateTimeOffset now)
    {
        var open = subtotal.Open;
        var high = subtotal.High;
        var low = subtotal.Low;
        var volume = subtotal.Volume;
        var first = true;
        for (var child = bucket.StartUtc; child < minute; child = child.AddMinutes(1))
        {
            var after = RequiredReceipt(state, child, bucket.StartUtc);
            var bar = FindBar(canonical.Bars, child);
            var childEnd = child.AddMinutes(1);
            if (bar is not null && bar.Volume > 0m && ChartHistorySource.Compatible(bar, symbol, "1m") &&
                bar.KnownAtUtc is { } receipt && receipt >= childEnd && receipt <= now && (after is null || receipt > after))
            {
                if (first) open = bar.Open;
                first = false;
                high = Math.Max(high, bar.High);
                low = Math.Min(low, bar.Low);
                if (decimal.MaxValue - volume < bar.Volume) return Result("suppressed", state, bucket);
                volume += bar.Volume;
            }
            else if (bar is not null || after is not null || canonical.NoTradeProofAtUtc is not { } proof ||
                     proof < childEnd || proof > now || !canonical.NoTradeMinutes.Contains(child))
                return Result("coverage_unverified", state, bucket);
        }
        return Result("forming", state, bucket) with
        {
            Open = open, High = high, Low = low, Close = subtotal.Close, EligibleVolume = volume
        };
    }

    // An unresolved mutation has no trustworthy original minute, so require new canonical receipts for affected children.
    private static DateTimeOffset? RequiredReceipt(Coverage state, DateTimeOffset child, DateTimeOffset bucketStart)
    {
        var after = state.LateChildren.GetValueOrDefault(child);
        if (state.UnresolvedMutation is { } mutation && mutation.LastPossibleChild >= child &&
            mutation.LastPossibleChild >= bucketStart && mutation.CanonicalAfter > after) after = mutation.CanonicalAfter;
        return after == default ? null : after;
    }

    // The existing canonical copy is ordered by native minute; seek within it without scanning its full session.
    private static OhlcvBar? FindBar(IReadOnlyList<OhlcvBar> bars, DateTimeOffset target)
    {
        var left = 0;
        var right = bars.Count - 1;
        while (left <= right)
        {
            var middle = left + (right - left) / 2;
            var bar = bars[middle];
            if (bar.Timestamp == target) return bar;
            if (bar.Timestamp < target) left = middle + 1;
            else right = middle - 1;
        }
        return null;
    }

    // Status frames can carry the last real observation but never synthesize one from this read.
    private static ChartFormingCandle Result(string status, Coverage state, BarResampler.BucketWindow? bucket = null) =>
        new(status, StartUtc: bucket?.StartUtc, EndUtc: bucket?.EndUtc, Segment: bucket?.SessionSegment,
            LastEventAtUtc: state.LastEvent?.ToUtcText(), LastObservedAtUtc: state.LastObservation);

    // Roll the connection-wide retention window before receiving or reading any minute subtotal.
    private bool Advance(DateTimeOffset now)
    {
        if (retired || !hasLease(now)) return false;
        var current = MinuteOf(now);
        if (current < minute) return false;
        if (current > minute)
        {
            trades.Clear();
            events = 0;
            minute = current;
            calendar.TryReadWindow(now, "15m", out largestWindow);
        }
        return true;
    }

    // Coverage resets are generation-local and cannot leave a stale quote or print summary behind.
    private void Reset(string symbol, Coverage state)
    {
        RemoveTrades(symbol);
        state.LateChildren.Clear();
        state.UnresolvedMutation = null;
        state.LastEvent = null;
        state.LastObservation = null;
    }

    // Release the symbol's events from the connection-wide memory budget.
    private void RemoveTrades(string symbol)
    {
        if (trades.Remove(symbol, out var tape)) events -= tape.Events;
    }

    // Alpaca native 1m candles use UTC minute starts for the source event clock.
    private static DateTimeOffset MinuteOf(DateTimeOffset time) =>
        new(time.UtcTicks - time.UtcTicks % TimeSpan.TicksPerMinute, TimeSpan.Zero);

    // Invalid or missing channels acknowledge no preview symbols.
    private static HashSet<string> ReadSymbols(JsonElement message, string field)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!message.TryGetProperty(field, out var array) || array.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 and <= 16 } symbol ||
                String.IsNullOrWhiteSpace(symbol)) return [];
            result.Add(AlpacaSymbolMapper.ToCanonicalSymbol(symbol));
        }
        return result;
    }
}
