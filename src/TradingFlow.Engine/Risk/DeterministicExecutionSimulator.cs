namespace TradingFlow.Engine.Risk;

public enum SimulatedOrderSide
{
    Buy,
    Sell
}

public enum SimulatedOrderType
{
    Market,
    Limit,
    Stop,
    StopLimit
}

public enum SimulatedFillStatus
{
    NoFill,
    PartialFill,
    Filled
}

/// <summary>
/// Completed, point-in-time market evidence available to the execution simulator.
/// Quotes are optional because historical bar archives do not always contain them;
/// callers must then provide a conservative synthetic spread in the policy.
/// </summary>
public sealed record ExecutionMarketSnapshot(
    DateTimeOffset TimestampUtc,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    decimal? Bid = null,
    decimal? Ask = null);

/// <summary>
/// Immutable execution assumptions. Every value is explicit so replay is repeatable
/// and no random fill probability can silently turn research results into luck.
/// </summary>
public sealed record ExecutionSimulationPolicy(
    decimal MaxParticipationPct,
    decimal BaseSlippageBps,
    decimal SyntheticSpreadBps,
    decimal ImpactBpsAtMaxParticipation,
    decimal FixedBuyFee,
    decimal FixedSellFee,
    decimal SecFeeRate,
    decimal FinraTafPerShare,
    decimal FinraTafCap,
    bool InputPriceIncludesExecutionFriction = false,
    int MaximumTrackedOrderContracts = 1_000_000);

public sealed record ExecutionSimulationRequest(
    SimulatedOrderSide Side,
    SimulatedOrderType OrderType,
    int RequestedQuantity,
    ExecutionMarketSnapshot Market,
    ExecutionSimulationPolicy Policy,
    decimal? LimitPrice = null,
    decimal? StopPrice = null);

public sealed record ExecutionSimulationResult(
    SimulatedFillStatus Status,
    int RequestedQuantity,
    int FilledQuantity,
    int RemainingQuantity,
    decimal? AverageFillPrice,
    decimal Fees,
    decimal SpreadBps,
    decimal SlippageBps,
    decimal ImpactBps,
    string Reason);

/// <summary>
/// Pure execution model shared by deterministic research and broker simulations.
/// It consumes one completed executable market snapshot, never reads future bars,
/// and resolves ambiguous stop-limit ordering by refusing to invent a fill.
/// </summary>
public sealed partial class DeterministicExecutionSimulator : IDeterministicExecutionSimulator
{
    private const decimal BasisPoints = 10_000m;

    public ExecutionSimulationResult Simulate(ExecutionSimulationRequest request)
        => SimulateCore(request, null);

    private ExecutionSimulationResult SimulateCore(
        ExecutionSimulationRequest request,
        int? maximumFillQuantity)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        var spreadBps = ResolveSpreadBps(request.Market, request.Policy);
        var trigger = ResolveTrigger(request);
        if (!trigger.IsTriggered)
        {
            return NoFill(request, spreadBps, trigger.Reason);
        }

        var fillableQuantity = maximumFillQuantity is { } maximum
            ? Math.Min(request.RequestedQuantity, Math.Max(maximum, 0))
            : CalculateFillableQuantity(
                request.RequestedQuantity,
                request.Market.Volume,
                request.Policy.MaxParticipationPct,
                missingVolumeFailsClosed: true);
        if (fillableQuantity <= 0)
        {
            return NoFill(request, spreadBps, "insufficient_executable_volume");
        }

        var impactBps = CalculateImpactBps(
            fillableQuantity,
            request.Market.Volume,
            request.Policy.MaxParticipationPct,
            request.Policy.ImpactBpsAtMaxParticipation);
        // A real quote-side reference already contains the spread. Historical bars use
        // the synthetic half-spread because only a midpoint-like open is available.
        var syntheticHalfSpreadBps = request.Market.Bid is null ? spreadBps / 2m : 0m;
        var slippageBps = request.Policy.BaseSlippageBps + syntheticHalfSpreadBps + impactBps;
        var fillPrice = ApplyAdversePrice(trigger.ReferencePrice, request.Side, slippageBps);
        if (request.OrderType is SimulatedOrderType.Limit or SimulatedOrderType.StopLimit)
        {
            fillPrice = request.Side == SimulatedOrderSide.Buy
                ? Math.Min(fillPrice, request.LimitPrice!.Value)
                : Math.Max(fillPrice, request.LimitPrice!.Value);
        }

        if (fillPrice <= 0m)
        {
            return NoFill(request, spreadBps, "non_positive_fill_price");
        }

        var sellProceeds = request.Side == SimulatedOrderSide.Sell
            ? fillPrice * fillableQuantity
            : 0m;
        var fixedFee = request.Side == SimulatedOrderSide.Buy
            ? request.Policy.FixedBuyFee
            : request.Policy.FixedSellFee;
        var fees = fixedFee + CalculateRegulatoryFees(
            sellProceeds,
            request.Side == SimulatedOrderSide.Sell ? fillableQuantity : 0,
            request.Policy.SecFeeRate,
            request.Policy.FinraTafPerShare,
            request.Policy.FinraTafCap);
        var remaining = request.RequestedQuantity - fillableQuantity;
        return new ExecutionSimulationResult(
            remaining == 0 ? SimulatedFillStatus.Filled : SimulatedFillStatus.PartialFill,
            request.RequestedQuantity,
            fillableQuantity,
            remaining,
            Decimal.Round(fillPrice, 6),
            Decimal.Round(fees, 6),
            Decimal.Round(spreadBps, 6),
            Decimal.Round(slippageBps, 6),
            Decimal.Round(impactBps, 6),
            remaining == 0 ? "filled" : "participation_limited_partial_fill");
    }

    public static int CalculateFillableQuantity(
        int requestedQuantity,
        decimal marketVolume,
        decimal maxParticipationPct,
        bool missingVolumeFailsClosed)
    {
        if (requestedQuantity <= 0)
        {
            return 0;
        }

        if (maxParticipationPct <= 0m)
        {
            return requestedQuantity;
        }

        if (marketVolume <= 0m)
        {
            return missingVolumeFailsClosed ? 0 : requestedQuantity;
        }

        var capacity = Decimal.Floor(marketVolume * (maxParticipationPct / 100m));
        return Math.Min(requestedQuantity, capacity > Int32.MaxValue ? Int32.MaxValue : (int)capacity);
    }

    public static decimal CalculateRegulatoryFees(
        decimal sellProceeds,
        int shares,
        decimal secFeeRate,
        decimal finraTafPerShare,
        decimal finraTafCap)
    {
        var fees = secFeeRate > 0m && sellProceeds > 0m
            ? sellProceeds * secFeeRate
            : 0m;
        if (finraTafPerShare <= 0m || shares <= 0)
        {
            return fees;
        }

        var taf = shares * finraTafPerShare;
        return fees + (finraTafCap > 0m ? Math.Min(taf, finraTafCap) : taf);
    }

    public static decimal CalculateLegacyDynamicSlippageBps(
        decimal price,
        decimal relativeVolume,
        decimal baseSlippageBps,
        decimal? tradeAmount)
    {
        var bps = baseSlippageBps;
        if (price < 2m)
        {
            bps *= 3m;
        }
        else if (price < 5m)
        {
            bps *= 2m;
        }
        else if (price < 10m)
        {
            bps *= 1.5m;
        }

        if (relativeVolume < 0.5m)
        {
            bps *= 2.5m;
        }
        else if (relativeVolume < 0.8m)
        {
            bps *= 1.5m;
        }
        else if (relativeVolume > 2m)
        {
            bps *= 0.8m;
        }

        if (tradeAmount > 100_000m)
        {
            bps *= 1.5m;
        }
        else if (tradeAmount > 50_000m)
        {
            bps *= 1.2m;
        }

        return Math.Min(bps, 100m);
    }

    private static TriggerResolution ResolveTrigger(ExecutionSimulationRequest request)
    {
        var market = request.Market;
        return request.OrderType switch
        {
            SimulatedOrderType.Market => TriggerResolution.Fill(ResolveQuoteSideOrOpen(request)),
            SimulatedOrderType.Limit => ResolveLimitTrigger(request),
            SimulatedOrderType.Stop => ResolveStopTrigger(request),
            SimulatedOrderType.StopLimit => ResolveStopLimitTrigger(request),
            _ => TriggerResolution.NoFill("unsupported_order_type")
        };
    }

    private static TriggerResolution ResolveLimitTrigger(ExecutionSimulationRequest request)
    {
        var limit = request.LimitPrice!.Value;
        if (request.Side == SimulatedOrderSide.Buy)
        {
            return request.Market.Low <= limit
                ? TriggerResolution.Fill(request.Market.Open <= limit ? request.Market.Open : limit)
                : TriggerResolution.NoFill("limit_not_reached");
        }

        return request.Market.High >= limit
            ? TriggerResolution.Fill(request.Market.Open >= limit ? request.Market.Open : limit)
            : TriggerResolution.NoFill("limit_not_reached");
    }

    private static TriggerResolution ResolveStopTrigger(ExecutionSimulationRequest request)
    {
        var stop = request.StopPrice!.Value;
        if (request.Side == SimulatedOrderSide.Buy)
        {
            return request.Market.High >= stop
                ? TriggerResolution.Fill(Math.Max(request.Market.Open, stop))
                : TriggerResolution.NoFill("stop_not_reached");
        }

        return request.Market.Low <= stop
            ? TriggerResolution.Fill(Math.Min(request.Market.Open, stop))
            : TriggerResolution.NoFill("stop_not_reached");
    }

    private static TriggerResolution ResolveStopLimitTrigger(ExecutionSimulationRequest request)
    {
        var stop = request.StopPrice!.Value;
        var limit = request.LimitPrice!.Value;
        var openSatisfies = request.Side == SimulatedOrderSide.Buy
            ? request.Market.Open >= stop && request.Market.Open <= limit
            : request.Market.Open <= stop && request.Market.Open >= limit;
        if (openSatisfies)
        {
            return TriggerResolution.Fill(request.Market.Open);
        }

        var stopTouched = request.Side == SimulatedOrderSide.Buy
            ? request.Market.High >= stop
            : request.Market.Low <= stop;
        return stopTouched
            ? TriggerResolution.NoFill("ambiguous_intrabar_stop_limit_sequence")
            : TriggerResolution.NoFill("stop_not_reached");
    }

    private static decimal ResolveQuoteSideOrOpen(ExecutionSimulationRequest request)
    {
        if (request.Market.Bid is > 0m && request.Market.Ask is > 0m)
        {
            return request.Side == SimulatedOrderSide.Buy
                ? request.Market.Ask.Value
                : request.Market.Bid.Value;
        }

        return request.Market.Open;
    }

    private static decimal ResolveSpreadBps(
        ExecutionMarketSnapshot market,
        ExecutionSimulationPolicy policy)
    {
        if (market.Bid is > 0m && market.Ask is > 0m)
        {
            var midpoint = (market.Bid.Value + market.Ask.Value) / 2m;
            return midpoint > 0m ? (market.Ask.Value - market.Bid.Value) / midpoint * BasisPoints : 0m;
        }

        return policy.SyntheticSpreadBps;
    }

    private static decimal CalculateImpactBps(
        int filledQuantity,
        decimal marketVolume,
        decimal maxParticipationPct,
        decimal impactBpsAtMaxParticipation)
    {
        if (filledQuantity <= 0 || marketVolume <= 0m || impactBpsAtMaxParticipation <= 0m)
        {
            return 0m;
        }

        var actualParticipationPct = filledQuantity / marketVolume * 100m;
        var denominator = maxParticipationPct > 0m ? maxParticipationPct : 100m;
        return impactBpsAtMaxParticipation * Math.Min(actualParticipationPct / denominator, 1m);
    }

    private static decimal ApplyAdversePrice(
        decimal referencePrice,
        SimulatedOrderSide side,
        decimal adverseBps) =>
        side == SimulatedOrderSide.Buy
            ? referencePrice * (1m + (adverseBps / BasisPoints))
            : referencePrice * (1m - (adverseBps / BasisPoints));

    private static ExecutionSimulationResult NoFill(
        ExecutionSimulationRequest request,
        decimal spreadBps,
        string reason) =>
        new(
            SimulatedFillStatus.NoFill,
            request.RequestedQuantity,
            0,
            request.RequestedQuantity,
            null,
            0m,
            Decimal.Round(spreadBps, 6),
            0m,
            0m,
            reason);

    private static void Validate(ExecutionSimulationRequest request)
    {
        if (request.RequestedQuantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Requested quantity must be positive.");
        }

        var market = request.Market;
        if (market.TimestampUtc == default || market.TimestampUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Market timestamp must be UTC.", nameof(request));
        }

        if (market.Open <= 0m || market.High <= 0m || market.Low <= 0m || market.Close <= 0m ||
            market.High < Math.Max(market.Open, market.Close) ||
            market.Low > Math.Min(market.Open, market.Close) ||
            market.High < market.Low || market.Volume < 0m)
        {
            throw new ArgumentException("Market OHLCV evidence is invalid.", nameof(request));
        }

        var hasBid = market.Bid is not null;
        var hasAsk = market.Ask is not null;
        if (hasBid != hasAsk ||
            market.Bid is <= 0m || market.Ask is <= 0m ||
            (hasBid && market.Ask < market.Bid))
        {
            throw new ArgumentException("Bid and ask must be supplied together as a valid quote.", nameof(request));
        }

        var policy = request.Policy;
        if (policy.MaxParticipationPct is < 0m or > 100m ||
            policy.BaseSlippageBps < 0m || policy.SyntheticSpreadBps < 0m ||
            policy.ImpactBpsAtMaxParticipation < 0m ||
            policy.FixedBuyFee < 0m || policy.FixedSellFee < 0m ||
            policy.SecFeeRate < 0m || policy.FinraTafPerShare < 0m || policy.FinraTafCap < 0m)
        {
            throw new ArgumentException("Execution policy values are outside their valid range.", nameof(request));
        }

        if (!hasBid &&
            policy.SyntheticSpreadBps == 0m &&
            policy.BaseSlippageBps == 0m &&
            !policy.InputPriceIncludesExecutionFriction)
        {
            throw new ArgumentException(
                "Bar-only execution requires synthetic friction or an explicit assertion that input prices already include it.",
                nameof(request));
        }

        if (request.OrderType is SimulatedOrderType.Limit or SimulatedOrderType.StopLimit &&
            request.LimitPrice is not > 0m)
        {
            throw new ArgumentException("Limit and stop-limit orders require a positive limit price.", nameof(request));
        }

        if (request.OrderType is SimulatedOrderType.Stop or SimulatedOrderType.StopLimit &&
            request.StopPrice is not > 0m)
        {
            throw new ArgumentException("Stop and stop-limit orders require a positive stop price.", nameof(request));
        }
    }

    private sealed record TriggerResolution(bool IsTriggered, decimal ReferencePrice, string Reason)
    {
        public static TriggerResolution Fill(decimal referencePrice) => new(true, referencePrice, "triggered");

        public static TriggerResolution NoFill(string reason) => new(false, 0m, reason);
    }
}
