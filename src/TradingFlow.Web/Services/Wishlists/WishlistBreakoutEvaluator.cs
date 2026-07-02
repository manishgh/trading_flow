using System.Text.Json;
using TradingFlow.Domain.Market;

namespace TradingFlow.Web.Services.Wishlists;

public sealed record WishlistMarketSnapshot(
    string Ticker,
    IndicatorSnapshot Current,
    IndicatorSnapshot? Previous,
    decimal? RecentHigh,
    decimal? SessionOpen);

public sealed record WishlistBreakoutEvaluation(
    string Ticker,
    bool ShouldAlert,
    string SignalType,
    string Severity,
    decimal Price,
    string Reason,
    decimal Score,
    decimal? SessionGainPct,
    decimal? SessionRelativeVolume,
    decimal? VwapExtensionAtr,
    string SnapshotJson,
    string? NewsHeadline,
    string? NewsUrl,
    string? NewsProvider);

public sealed record WishlistBreakoutEvaluatorOptions(
    decimal MinSessionGainPct = 1.5m,
    decimal MinSessionRelativeVolume = 1.2m,
    decimal MinBreakoutPct = 0.15m,
    decimal MaxVwapExtensionAtr = 3.5m);

/// <summary>
/// Turns warmed indicator snapshots into a human-actionable wishlist alert.
/// The rule follows the current trading thesis: trend and momentum first,
/// volume as confirmation, and no chasing bars already too far from VWAP.
/// </summary>
public sealed class WishlistBreakoutEvaluator
{
    private readonly WishlistBreakoutEvaluatorOptions options;

    public WishlistBreakoutEvaluator() : this(new WishlistBreakoutEvaluatorOptions())
    {
    }

    public WishlistBreakoutEvaluator(WishlistBreakoutEvaluatorOptions options)
    {
        this.options = options;
    }

    public WishlistBreakoutEvaluation Evaluate(WishlistMarketSnapshot input)
    {
        var snapshot = input.Current;
        var ticker = NormalizeTicker(input.Ticker.Length == 0 ? snapshot.Ticker : input.Ticker);
        var sessionGainPct = CalculatePercentGain(input.SessionOpen, snapshot.CurrentPrice);
        var breakoutPct = CalculatePercentGain(input.RecentHigh, snapshot.CurrentPrice);
        var vwapExtensionAtr = CalculateVwapExtensionAtr(snapshot);
        var macdImproving = snapshot.MacdHistogram is > 0m &&
            (input.Previous?.MacdHistogram is null || snapshot.MacdHistogram >= input.Previous.MacdHistogram.Value);
        var emaAligned = snapshot.Ema10 is { } ema10 && snapshot.Ema20 is { } ema20 && ema10 >= ema20;
        var priceAboveSupport = snapshot.Vwap is { } vwap && snapshot.CurrentPrice >= vwap &&
            (snapshot.Ema10 is null || snapshot.CurrentPrice >= snapshot.Ema10.Value);
        var participationConfirmed = snapshot.SessionRelativeVolume is { } sessionRvol && sessionRvol >= options.MinSessionRelativeVolume ||
            input.Previous is { } previous && snapshot.CurrentVolume > previous.CurrentVolume;
        var breakoutConfirmed = breakoutPct is { } breakout && breakout >= options.MinBreakoutPct ||
            sessionGainPct is { } sessionGain && sessionGain >= options.MinSessionGainPct;
        var notTooExtended = vwapExtensionAtr is null || vwapExtensionAtr <= options.MaxVwapExtensionAtr;

        var score = Score(priceAboveSupport, emaAligned, macdImproving, participationConfirmed, breakoutConfirmed, notTooExtended);
        var shouldAlert = priceAboveSupport && emaAligned && macdImproving && participationConfirmed && breakoutConfirmed && notTooExtended;
        var reason = BuildReason(
            shouldAlert,
            priceAboveSupport,
            emaAligned,
            macdImproving,
            participationConfirmed,
            breakoutConfirmed,
            notTooExtended,
            sessionGainPct,
            snapshot.SessionRelativeVolume,
            vwapExtensionAtr,
            snapshot.Catalyst);

        return new WishlistBreakoutEvaluation(
            ticker,
            shouldAlert,
            shouldAlert ? "wishlist_breakout" : "wishlist_watch_rejected",
            shouldAlert ? ResolveSeverity(score) : "info",
            snapshot.CurrentPrice,
            reason,
            score,
            sessionGainPct,
            snapshot.SessionRelativeVolume,
            vwapExtensionAtr,
            JsonSerializer.Serialize(new
            {
                snapshot.Timestamp,
                snapshot.Timeframe,
                snapshot.CurrentPrice,
                snapshot.CurrentVolume,
                snapshot.Vwap,
                snapshot.Ema10,
                snapshot.Ema20,
                snapshot.MacdHistogram,
                snapshot.SessionRelativeVolume,
                catalyst = snapshot.Catalyst is null ? null : new
                {
                    snapshot.Catalyst.Headline,
                    snapshot.Catalyst.Provider,
                    snapshot.Catalyst.Source,
                    snapshot.Catalyst.Url,
                    snapshot.Catalyst.SentimentScore,
                    snapshot.Catalyst.Timestamp
                },
                breakoutPct,
                sessionGainPct,
                vwapExtensionAtr
            }),
            snapshot.Catalyst?.Headline,
            snapshot.Catalyst?.Url,
            snapshot.Catalyst?.Provider);
    }

    private static decimal? CalculatePercentGain(decimal? from, decimal to)
    {
        return from is > 0m ? Decimal.Round(((to - from.Value) / from.Value) * 100m, 4) : null;
    }

    private static decimal? CalculateVwapExtensionAtr(IndicatorSnapshot snapshot)
    {
        if (snapshot.Vwap is not { } vwap || snapshot.Atr is not { } atr || atr <= 0m)
        {
            return null;
        }

        return Decimal.Round((snapshot.CurrentPrice - vwap) / atr, 4);
    }

    private static decimal Score(params bool[] checks)
    {
        if (checks.Length == 0)
        {
            return 0m;
        }

        return Decimal.Round(checks.Count(check => check) / (decimal)checks.Length, 4);
    }

    private static string ResolveSeverity(decimal score) => score >= 0.95m ? "high" : "medium";

    private static string BuildReason(
        bool shouldAlert,
        bool priceAboveSupport,
        bool emaAligned,
        bool macdImproving,
        bool participationConfirmed,
        bool breakoutConfirmed,
        bool notTooExtended,
        decimal? sessionGainPct,
        decimal? sessionRelativeVolume,
        decimal? vwapExtensionAtr,
        CatalystEvent? catalyst)
    {
        if (shouldAlert)
        {
            var catalystText = catalyst is null
                ? " No matched news catalyst."
                : $" News match: {catalyst.Headline} ({catalyst.Provider ?? "news"}).";
            return $"Breakout watch: price holds VWAP/EMA10, EMA10>=EMA20, MACD histogram bullish/improving, participation confirmed. Session gain={Format(sessionGainPct)}%, RVOL={Format(sessionRelativeVolume)}, VWAP extension ATR={Format(vwapExtensionAtr)}.{catalystText}";
        }

        var failures = new List<string>();
        if (!priceAboveSupport) failures.Add("price below VWAP/EMA10 support");
        if (!emaAligned) failures.Add("EMA10 below EMA20");
        if (!macdImproving) failures.Add("MACD histogram not bullish/improving");
        if (!participationConfirmed) failures.Add("participation not confirmed");
        if (!breakoutConfirmed) failures.Add("no breakout/session gain confirmation");
        if (!notTooExtended) failures.Add("VWAP/ATR extension too high");
        return $"No alert: {String.Join(", ", failures)}. Session gain={Format(sessionGainPct)}%, RVOL={Format(sessionRelativeVolume)}, VWAP extension ATR={Format(vwapExtensionAtr)}.";
    }

    private static string Format(decimal? value) => value.HasValue ? value.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) : "n/a";

    private static string NormalizeTicker(string ticker) => ticker.Trim().ToUpperInvariant();
}
