using TradingFlow.Engine.Execution;

namespace TradingFlow.Research.Catalysts;

public sealed record ExchangeSessionResolution(
    DateOnly TradeDate,
    EquityTradingSession Session,
    DateTimeOffset SessionStartUtc,
    DateTimeOffset SessionEndUtc)
{
    public string Label => Session switch
    {
        EquityTradingSession.Premarket => "premarket",
        EquityTradingSession.Regular => "regular",
        EquityTradingSession.AfterHours => "postmarket",
        EquityTradingSession.Overnight => "overnight",
        _ => "closed"
    };
}

/// <summary>
/// Resolves a timestamp against an immutable, point-in-time exchange calendar.
/// Implementations must distinguish a date omitted by a complete official calendar
/// from an arbitrary missing date. They may resolve the former as closed, but must
/// fail closed when coverage or source completeness is not proven.
/// </summary>
public interface IExchangeSessionResolver
{
    ExchangeSessionResolution Resolve(DateTimeOffset timestampUtc);
}

/// <summary>
/// Adapts the engine's trading-session snapshots to deterministic research sessions.
/// Extended-session bounds remain explicit constructor inputs because provider calendars
/// normally supply only the authoritative regular open and close.
/// </summary>
public sealed class HistoricalExchangeSessionResolver : IExchangeSessionResolver
{
    private readonly IReadOnlyDictionary<DateOnly, TradingSessionSnapshot> _calendarByDate;
    private readonly TimeZoneInfo _exchangeTimeZone;
    private readonly TimeOnly _premarketOpen;
    private readonly TimeOnly _postmarketClose;

    public HistoricalExchangeSessionResolver(
        IEnumerable<TradingSessionSnapshot> calendar,
        TimeZoneInfo exchangeTimeZone,
        TimeOnly premarketOpen,
        TimeOnly postmarketClose)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        _exchangeTimeZone = exchangeTimeZone
            ?? throw new ArgumentNullException(nameof(exchangeTimeZone));
        if (premarketOpen >= postmarketClose)
        {
            throw new ArgumentException("Premarket open must precede postmarket close.");
        }

        _premarketOpen = premarketOpen;
        _postmarketClose = postmarketClose;
        _calendarByDate = calendar
            .Select(ValidateCalendarSnapshot)
            .ToDictionary(snapshot => snapshot.TradeDate);
    }

    public ExchangeSessionResolution Resolve(DateTimeOffset timestampUtc)
    {
        EnsureUtc(timestampUtc, nameof(timestampUtc));
        var exchangeLocal = TimeZoneInfo.ConvertTime(timestampUtc, _exchangeTimeZone);
        var tradeDate = DateOnly.FromDateTime(exchangeLocal.DateTime);
        var dayStartUtc = ToUtc(tradeDate, TimeOnly.MinValue);
        var nextDayStartUtc = ToUtc(tradeDate.AddDays(1), TimeOnly.MinValue);

        if (!_calendarByDate.TryGetValue(tradeDate, out var calendar) ||
            calendar.RegularOpenUtc is null ||
            calendar.RegularCloseUtc is null)
        {
            return new ExchangeSessionResolution(
                tradeDate,
                EquityTradingSession.Closed,
                dayStartUtc,
                nextDayStartUtc);
        }

        var premarketOpenUtc = ToUtc(tradeDate, _premarketOpen);
        var postmarketCloseUtc = ToUtc(tradeDate, _postmarketClose);
        var regularOpenUtc = calendar.RegularOpenUtc.Value;
        var regularCloseUtc = calendar.RegularCloseUtc.Value;

        if (timestampUtc >= premarketOpenUtc && timestampUtc < regularOpenUtc)
        {
            return new ExchangeSessionResolution(
                tradeDate,
                EquityTradingSession.Premarket,
                premarketOpenUtc,
                regularOpenUtc);
        }

        if (timestampUtc >= regularOpenUtc && timestampUtc < regularCloseUtc)
        {
            return new ExchangeSessionResolution(
                tradeDate,
                EquityTradingSession.Regular,
                regularOpenUtc,
                regularCloseUtc);
        }

        if (timestampUtc >= regularCloseUtc && timestampUtc < postmarketCloseUtc)
        {
            return new ExchangeSessionResolution(
                tradeDate,
                EquityTradingSession.AfterHours,
                regularCloseUtc,
                postmarketCloseUtc);
        }

        return timestampUtc < premarketOpenUtc
            ? new ExchangeSessionResolution(
                tradeDate,
                EquityTradingSession.Overnight,
                dayStartUtc,
                premarketOpenUtc)
            : new ExchangeSessionResolution(
                tradeDate,
                EquityTradingSession.Overnight,
                postmarketCloseUtc,
                nextDayStartUtc);
    }

    private TradingSessionSnapshot ValidateCalendarSnapshot(TradingSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        EnsureUtc(snapshot.ObservedAtUtc, nameof(snapshot.ObservedAtUtc));
        if (snapshot.Session == EquityTradingSession.Closed)
        {
            if (snapshot.RegularOpenUtc is not null || snapshot.RegularCloseUtc is not null)
            {
                throw new ArgumentException(
                    $"Closed calendar date {snapshot.TradeDate:yyyy-MM-dd} cannot have regular-session bounds.");
            }

            return snapshot;
        }

        if (snapshot.RegularOpenUtc is null || snapshot.RegularCloseUtc is null)
        {
            throw new ArgumentException(
                $"Open calendar date {snapshot.TradeDate:yyyy-MM-dd} requires regular-session bounds.");
        }

        EnsureUtc(snapshot.RegularOpenUtc.Value, nameof(snapshot.RegularOpenUtc));
        EnsureUtc(snapshot.RegularCloseUtc.Value, nameof(snapshot.RegularCloseUtc));
        if (snapshot.RegularCloseUtc <= snapshot.RegularOpenUtc)
        {
            throw new ArgumentException(
                $"Calendar date {snapshot.TradeDate:yyyy-MM-dd} has invalid regular-session bounds.");
        }

        var openDate = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(snapshot.RegularOpenUtc.Value, _exchangeTimeZone).DateTime);
        var closeDate = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(snapshot.RegularCloseUtc.Value, _exchangeTimeZone).DateTime);
        if (openDate != snapshot.TradeDate || closeDate != snapshot.TradeDate)
        {
            throw new ArgumentException(
                $"Calendar bounds must resolve to trade date {snapshot.TradeDate:yyyy-MM-dd}.");
        }

        return snapshot;
    }

    private DateTimeOffset ToUtc(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        if (_exchangeTimeZone.IsInvalidTime(local))
        {
            throw new InvalidOperationException(
                $"Exchange-local timestamp {local:O} is invalid in {_exchangeTimeZone.Id}.");
        }

        var offset = _exchangeTimeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be a non-default UTC value.", parameterName);
        }
    }
}
