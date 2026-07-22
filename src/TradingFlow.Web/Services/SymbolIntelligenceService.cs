using TradingFlow.Web.Models;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Web.Services;

/// <summary>
/// Builds one symbol-detail contract for Razor and mobile clients. TradingFlow
/// decision state remains authoritative; predictor output is read-only evidence.
/// </summary>
public sealed class SymbolIntelligenceService
{
    private readonly MarketPredictorHttpClient predictor;
    private readonly TimeProvider timeProvider;

    public SymbolIntelligenceService(MarketPredictorHttpClient predictor, TimeProvider timeProvider)
    {
        this.predictor = predictor;
        this.timeProvider = timeProvider;
    }

    public async Task<MobileSymbolIntelligenceResponse> BuildAsync(
        WishlistDeskRow row,
        string mode,
        string horizon,
        CancellationToken cancellationToken)
    {
        var evidence = await predictor.GetAsync(row.Ticker, mode, horizon, cancellationToken);
        return new MobileSymbolIntelligenceResponse(
            row.Ticker,
            timeProvider.GetUtcNow(),
            new MobileTradingFlowDecisionResponse(
                row.EligibilityLabel,
                row.EligibilityReason,
                row.HasTrade,
                row.Trade,
                row.LatestSignal is null ? null : MobileApiModelMapper.ToMobileWishlistSignal(row.LatestSignal),
                row.Quote.BidPrice,
                row.Quote.AskPrice,
                row.Quote.MidPrice,
                row.Quote.Timestamp),
            MapEvidence(evidence));
    }

    internal static MobileModelIntelligenceResponse MapEvidence(MarketPredictorResult evidence)
    {
        var readinessReasons = evidence.Errors
            .Concat(evidence.Swing?.Readiness.Reasons ?? [])
            .Concat(evidence.Intraday?.Readiness.Reasons ?? [])
            .Where(reason => !String.IsNullOrWhiteSpace(reason))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new MobileModelIntelligenceResponse(
            evidence.ContractVersion,
            evidence.AvailabilityStatus,
            evidence.AvailabilityReason,
            evidence.IsValidPromotedEvidence,
            evidence.Mode,
            evidence.RequestedHorizon,
            evidence.ResolvedHorizon,
            evidence.FinalSignal,
            evidence.ReadinessStatus,
            readinessReasons,
            evidence.RequestId,
            evidence.SnapshotId,
            evidence.GeneratedAtUtc,
            evidence.Model?.Status,
            evidence.Model?.ModelType,
            evidence.Model?.SchemaVersion,
            evidence.Model?.Target,
            evidence.Model?.ArtifactSha256,
            evidence.Model?.TrainingDataEnd,
            evidence.Swing is null ? null : MapSwing(evidence.Swing),
            evidence.Intraday is null ? null : MapIntraday(evidence.Intraday));
    }

    private static MobileSwingIntelligenceResponse MapSwing(PredictorSwingPrediction swing) => new(
        swing.Probability,
        swing.DecisionScore,
        swing.Signal,
        swing.Rank,
        swing.Return1D,
        swing.VolumeZ20,
        MapCatalyst(swing.Catalyst),
        swing.GlobalContext.NetImpact,
        swing.GlobalContext.ActiveFlashpoints,
        swing.Readiness.Status,
        swing.Readiness.Reasons,
        swing.Readiness.LatestPriceDate,
        swing.Readiness.PriceFeed);

    private static MobileIntradayIntelligenceResponse MapIntraday(PredictorIntradayPrediction intraday) => new(
        intraday.OpportunityProbability,
        intraday.DownsideProbability,
        intraday.DecisionScore,
        intraday.Signal,
        intraday.Rank,
        intraday.RelativeVolume,
        intraday.Rsi14,
        intraday.MacdSignalDiff,
        intraday.EntryStopPct,
        intraday.EntryTargetPct,
        MapCatalyst(intraday.Catalyst),
        intraday.Readiness.Status,
        intraday.Readiness.Reasons,
        intraday.Readiness.LatestPriceDate,
        intraday.Readiness.PriceFeed);

    private static MobileCatalystIntelligenceResponse MapCatalyst(PredictorCatalyst catalyst) => new(
        catalyst.Status,
        catalyst.Direction,
        catalyst.Score,
        catalyst.EventCount,
        catalyst.Relevance,
        catalyst.MinutesSinceLatest,
        catalyst.Reasons);
}

internal static class MobileApiModelMapper
{
    public static MobileWishlistSignalResponse ToMobileWishlistSignal(TradingFlow.Domain.Wishlists.WishlistSignal signal) => new(
        signal.Id,
        signal.WishlistId,
        signal.Ticker,
        signal.SignalType,
        signal.Severity,
        signal.DetectedAtUtc,
        signal.Price,
        signal.Reason,
        signal.SnapshotJson,
        signal.NewsHeadline,
        signal.NewsUrl,
        signal.NewsProvider,
        signal.Acknowledged);
}
