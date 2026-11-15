using TradingFlow.Alpaca;

namespace TradingFlow.Web.Services.Charts;

internal sealed record SipMinutePrices(decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);

/// <summary>Only stores identity and price contributions for one unclosed SIP minute.</summary>
internal sealed class SipFormingMinute
{
    private readonly record struct TradeIdentity(char Tape, char Exchange, long Id);
    private sealed record Trade(TradeIdentity Identity, AlpacaSipTradePrint Print, AlpacaSipEventClock Clock,
        AlpacaSipMinuteFields Fields);

    private readonly HashSet<AlpacaSipTradeFrame> seen = [];
    private readonly HashSet<TradeIdentity> usedIds = [];
    private readonly Dictionary<TradeIdentity, Trade> active = [];
    private Trade? open;
    private Trade? close;
    private decimal? high;
    private decimal? low;
    private decimal volume;
    private bool dirty;

    public int Events => seen.Count;

    // An exact duplicate is ignored even when its original print was subsequently corrected or cancelled.
    public bool Seen(AlpacaSipTradeFrame frame) => seen.Contains(frame);

    // Apply one new event only if its identity and all relevant field rules are known.
    public bool TryApply(AlpacaSipTradeFrame frame)
    {
        var id = new TradeIdentity(frame.Tape, frame.Exchange, frame.Original.Id);
        var applied = frame.Kind == AlpacaSipTradeKind.Print
            ? TryAddPrint(frame, id) : TryApplyMutation(frame, id);
        if (applied) seen.Add(frame);
        return applied;
    }

    // Each ordinary print updates the running aggregate without looking through earlier prints.
    private bool TryAddPrint(AlpacaSipTradeFrame frame, TradeIdentity id)
    {
        if (!AlpacaMarketDataMessageParser.TryResolveSipMinuteFields(frame.Tape, frame.Original.Conditions, out var fields) ||
            !usedIds.Add(id)) return false;
        var trade = new Trade(id, frame.Original, frame.Clock, fields);
        active.Add(id, trade);
        if (!dirty) Include(trade);
        return true;
    }

    // A matched mutation edits the original time position, since its own event time is not a new execution.
    private bool TryApplyMutation(AlpacaSipTradeFrame frame, TradeIdentity id)
    {
        if (!active.TryGetValue(id, out var original) || original.Print.Price != frame.Original.Price ||
            original.Print.Size != frame.Original.Size || frame.Clock.CompareTo(original.Clock) < 0) return false;

        if (frame.Kind == AlpacaSipTradeKind.Correction)
        {
            if (original.Print != frame.Original || frame.Corrected is not { } corrected ||
                !AlpacaMarketDataMessageParser.TryResolveSipMinuteFields(frame.Tape, corrected.Conditions, out var fields))
                return false;
            var correctedId = id with { Id = corrected.Id };
            if (correctedId != id && !usedIds.Add(correctedId)) return false;
            active.Remove(id);
            active.Add(correctedId, original with { Print = corrected, Fields = fields });
        }
        else active.Remove(id);

        dirty = true;
        return true;
    }

    // Defer recalculation after mutations and share the resulting subtotal across all chart watches.
    public SipMinutePrices? ReadPrices()
    {
        if (dirty)
        {
            open = close = null;
            high = low = null;
            volume = 0m;
            foreach (var trade in active.Values) Include(trade);
            dirty = false;
        }
        return open is not null && close is not null && high is { } h && low is { } l && volume > 0m
            ? new(open.Print.Price, h, l, close.Print.Price, volume) : null;
    }

    // Apply each field independently. A volume-only trade never establishes OHLC.
    private void Include(Trade trade)
    {
        if ((trade.Fields & AlpacaSipMinuteFields.OpenClose) != 0)
        {
            if (open is null || Compare(trade, open) < 0) open = trade;
            if (close is null || Compare(trade, close) > 0) close = trade;
        }
        if ((trade.Fields & AlpacaSipMinuteFields.HighLow) != 0)
        {
            high = high is { } h ? Math.Max(h, trade.Print.Price) : trade.Print.Price;
            low = low is { } l ? Math.Min(l, trade.Print.Price) : trade.Print.Price;
        }
        if ((trade.Fields & AlpacaSipMinuteFields.Volume) != 0) volume += trade.Print.Size;
    }

    // Break exact event-time ties deterministically using provider tape, exchange and original trade ID.
    private static int Compare(Trade left, Trade right)
    {
        var clock = left.Clock.CompareTo(right.Clock);
        if (clock != 0) return clock;
        var tape = left.Identity.Tape.CompareTo(right.Identity.Tape);
        if (tape != 0) return tape;
        var exchange = left.Identity.Exchange.CompareTo(right.Identity.Exchange);
        if (exchange != 0) return exchange;
        return left.Identity.Id.CompareTo(right.Identity.Id);
    }
}
