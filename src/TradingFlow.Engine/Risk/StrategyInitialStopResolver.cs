using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Engine.Risk;

public sealed record StrategyInitialStopRequest(
    StrategyDefinition Strategy,
    TradeSignal Signal,
    PlannedOrderSide Side,
    decimal EntryPrice,
    int StopContextIndex,
    IReadOnlyList<OhlcvBar> ExecutionBars,
    IReadOnlyList<IndicatorSnapshot> ExecutionSnapshots);

public sealed record StrategyInitialStopResult(
    bool IsResolved,
    PlannedStopRequest? Stop,
    decimal? StopPrice,
    decimal? StopDistance,
    string? RejectionReason)
{
    public static StrategyInitialStopResult Resolved(
        PlannedStopRequest stop,
        decimal stopPrice,
        decimal stopDistance) =>
        new(true, stop, stopPrice, stopDistance, null);

    public static StrategyInitialStopResult Rejected(string reason) =>
        new(false, null, null, null, reason);
}

/// <summary>
/// Resolves the strategy-defined initial stop without applying portfolio risk
/// limits. Position sizing and loss caps belong to <see cref="SharedOrderRiskPlanner"/>.
/// </summary>
public sealed class StrategyInitialStopResolver
{
    public StrategyInitialStopResult Resolve(StrategyInitialStopRequest request)
    {
        if (request.EntryPrice <= 0m ||
            request.StopContextIndex < 0 ||
            request.StopContextIndex >= request.ExecutionBars.Count ||
            request.StopContextIndex >= request.ExecutionSnapshots.Count)
        {
            return StrategyInitialStopResult.Rejected("invalid_stop_context");
        }

        var mode = NormalizeRuleName(request.Strategy.ExitRules.InitialStopMode);
        var isShort = request.Side == PlannedOrderSide.Short;
        decimal stopPrice;
        PlannedStopRequest stop;

        switch (mode)
        {
            case "vwap_minus_atr" when !isShort:
                if (!TryGetVwapAtr(request, out var longVwap, out var longAtr))
                {
                    return StrategyInitialStopResult.Rejected("vwap_or_atr_unavailable_for_initial_stop");
                }

                stopPrice = longVwap - (request.Strategy.ExitRules.StopAtrMultiple * longAtr);
                stop = PlannedStopRequest.FromStructuralPrice(stopPrice);
                break;

            case "vwap_plus_atr" when isShort:
                if (!TryGetVwapAtr(request, out var shortVwap, out var shortAtr))
                {
                    return StrategyInitialStopResult.Rejected("vwap_or_atr_unavailable_for_initial_stop");
                }

                stopPrice = shortVwap + (request.Strategy.ExitRules.StopAtrMultiple * shortAtr);
                stop = PlannedStopRequest.FromStructuralPrice(stopPrice);
                break;

            case "opening_range_opposite":
                var openingRange = GetOpeningRange(request);
                if (openingRange is null)
                {
                    return StrategyInitialStopResult.Rejected("opening_range_unavailable_for_initial_stop");
                }

                stopPrice = isShort ? openingRange.Value.High : openingRange.Value.Low;
                stop = PlannedStopRequest.FromStructuralPrice(stopPrice);
                break;

            case "extreme_shadow":
                var sessionExtreme = GetSessionExtremeThroughEntry(request);
                if (sessionExtreme is null)
                {
                    return StrategyInitialStopResult.Rejected("session_extreme_unavailable_for_initial_stop");
                }

                stopPrice = isShort
                    ? sessionExtreme.Value.High + request.Strategy.ExitRules.StopTickBuffer
                    : sessionExtreme.Value.Low - request.Strategy.ExitRules.StopTickBuffer;
                stop = PlannedStopRequest.FromStructuralPrice(stopPrice);
                break;

            case "swing_low" when !isShort:
                var swingLow = request.Signal.SetupStructuralStopPrice ??
                    request.Signal.ReversionStretchLow ??
                    LowestLowThroughStopContext(
                        request.ExecutionBars,
                        request.StopContextIndex,
                        Math.Max(1, request.Strategy.EntryRules.ReversionStretchLookbackBars));
                if (swingLow is not > 0m)
                {
                    return StrategyInitialStopResult.Rejected("swing_low_unavailable_for_initial_stop");
                }

                stopPrice = swingLow.Value - request.Strategy.ExitRules.StopTickBuffer;
                stop = PlannedStopRequest.FromStructuralPrice(stopPrice);
                break;

            case "atr":
                if (request.Signal.CurrentAtr <= 0m ||
                    request.Strategy.ExitRules.StopAtrMultiple <= 0m)
                {
                    return StrategyInitialStopResult.Rejected("atr_unavailable_for_initial_stop");
                }

                var atrDistance = request.Signal.CurrentAtr * request.Strategy.ExitRules.StopAtrMultiple;
                stopPrice = isShort
                    ? request.EntryPrice + atrDistance
                    : request.EntryPrice - atrDistance;
                stop = PlannedStopRequest.FromAtr(
                    request.Signal.CurrentAtr,
                    request.Strategy.ExitRules.StopAtrMultiple);
                break;

            default:
                return StrategyInitialStopResult.Rejected(
                    $"unsupported_initial_stop_mode ({request.Strategy.ExitRules.InitialStopMode})");
        }

        var stopDistance = isShort
            ? stopPrice - request.EntryPrice
            : request.EntryPrice - stopPrice;
        return stopPrice <= 0m || stopDistance <= 0m
            ? StrategyInitialStopResult.Rejected("initial_stop_on_wrong_side_of_entry")
            : StrategyInitialStopResult.Resolved(
                stop,
                Decimal.Round(stopPrice, 4),
                stopDistance);
    }

    private static bool TryGetVwapAtr(
        StrategyInitialStopRequest request,
        out decimal vwap,
        out decimal atr)
    {
        var snapshot = request.ExecutionSnapshots[request.StopContextIndex];
        vwap = snapshot.Vwap ?? 0m;
        atr = snapshot.Atr ?? 0m;
        return vwap > 0m && atr > 0m;
    }

    private static (decimal High, decimal Low)? GetOpeningRange(
        StrategyInitialStopRequest request)
    {
        if (request.Strategy.EntryRules.OpeningRangeMinutes <= 0)
        {
            return null;
        }

        var contextTimestamp = request.ExecutionBars[request.StopContextIndex].Timestamp;
        var exchangeTime = ToExchangeTime(
            contextTimestamp,
            request.Strategy.Session.ExchangeTimezone);
        var sessionOpen = exchangeTime.Date.Add(new TimeSpan(9, 30, 0));
        var openingRangeEnd = sessionOpen.AddMinutes(
            request.Strategy.EntryRules.OpeningRangeMinutes);
        var rangeBars = request.ExecutionBars
            .Take(request.StopContextIndex + 1)
            .Where(bar =>
            {
                var barExchangeTime = ToExchangeTime(
                    bar.Timestamp,
                    request.Strategy.Session.ExchangeTimezone);
                return barExchangeTime.Date == exchangeTime.Date &&
                    barExchangeTime >= sessionOpen &&
                    barExchangeTime < openingRangeEnd;
            })
            .ToArray();

        return rangeBars.Length == 0
            ? null
            : (rangeBars.Max(bar => bar.High), rangeBars.Min(bar => bar.Low));
    }

    private static (decimal High, decimal Low)? GetSessionExtremeThroughEntry(
        StrategyInitialStopRequest request)
    {
        var contextTimestamp = request.ExecutionBars[request.StopContextIndex].Timestamp;
        var exchangeDate = ToExchangeTime(
            contextTimestamp,
            request.Strategy.Session.ExchangeTimezone).Date;
        var sessionBars = request.ExecutionBars
            .Take(request.StopContextIndex + 1)
            .Where(bar =>
                ToExchangeTime(
                    bar.Timestamp,
                    request.Strategy.Session.ExchangeTimezone).Date == exchangeDate)
            .ToArray();

        return sessionBars.Length == 0
            ? null
            : (sessionBars.Max(bar => bar.High), sessionBars.Min(bar => bar.Low));
    }

    private static decimal? LowestLowThroughStopContext(
        IReadOnlyList<OhlcvBar> bars,
        int stopContextIndex,
        int lookback)
    {
        var start = Math.Max(0, stopContextIndex - lookback);
        decimal? low = null;
        for (var i = start; i <= stopContextIndex && i < bars.Count; i++)
        {
            low = low is { } current ? Math.Min(current, bars[i].Low) : bars[i].Low;
        }

        return low;
    }

    private static DateTime ToExchangeTime(DateTimeOffset timestamp, string timezoneId)
    {
        var timezone = ResolveTimeZone(timezoneId);
        return TimeZoneInfo.ConvertTime(timestamp, timezone).DateTime;
    }

    private static TimeZoneInfo ResolveTimeZone(string timezoneId)
    {
        foreach (var candidate in new[] { timezoneId, "America/New_York", "Eastern Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(candidate);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.Utc;
    }

    private static string NormalizeRuleName(string? value) =>
        String.IsNullOrWhiteSpace(value)
            ? String.Empty
            : value.Trim().Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant();
}
