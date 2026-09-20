using System.Text.Json;
using TradingFlow.Domain.Market;

namespace TradingFlow.Web.Services.Wishlists;

public sealed record WishlistMarketSnapshot(
    string Ticker,
    IndicatorSnapshot Current,
    IndicatorSnapshot? Previous,
    decimal? RecentHigh);

public sealed record WishlistSwingWatchEvaluation(
    string Ticker,
    DateTimeOffset SourceBarTimestampUtc,
    bool ShouldAlert,
    string SignalType,
    string Severity,
    decimal Price,
    string Reason,
    decimal Score,
    decimal? DailyRelativeVolume,
    decimal? DistanceFromRecentHighPct,
    decimal? Ema20ExtensionAtr,
    string SnapshotJson,
    string? NewsHeadline,
    string? NewsUrl,
    string? NewsProvider);

public sealed record WishlistSwingWatchEvaluatorOptions(
    decimal MinDailyRelativeVolume = 1.2m,
    decimal MaxDistanceBelowRecentHighPct = 2m,
    decimal MaxEma20ExtensionAtr = 3.5m);

/// <summary>
/// Produces a generic daily swing watch alert. This is not a trading strategy
/// and cannot authorize an order; strategy eligibility remains in the shared
/// decision kernel used by backtest, paper, and live execution.
/// </summary>
public sealed class WishlistSwingWatchEvaluator
{
    private readonly WishlistSwingWatchEvaluatorOptions options;

    public WishlistSwingWatchEvaluator() : this(new WishlistSwingWatchEvaluatorOptions())
    {
    }

    public WishlistSwingWatchEvaluator(WishlistSwingWatchEvaluatorOptions options)
    {
        this.options = options;
    }

    public WishlistSwingWatchEvaluation Evaluate(WishlistMarketSnapshot input)
    {
        var snapshot = input.Current;
        var ticker = NormalizeTicker(input.Ticker.Length == 0 ? snapshot.Ticker : input.Ticker);
        var isDailyEvidence = snapshot.Timeframe.Equals("1d", StringComparison.OrdinalIgnoreCase);
        var distanceFromRecentHighPct = CalculateDistanceBelow(input.RecentHigh, snapshot.CurrentPrice);
        var ema20ExtensionAtr = CalculateEma20ExtensionAtr(snapshot);
        var macdImproving = snapshot.MacdHistogram is > 0m &&
            (input.Previous?.MacdHistogram is null || snapshot.MacdHistogram >= input.Previous.MacdHistogram.Value);
        var emaAligned = snapshot.Ema10 is { } ema10 && snapshot.Ema20 is { } ema20 && ema10 >= ema20;
        var priceAboveTrend = snapshot.Ema20 is { } trend && snapshot.CurrentPrice >= trend;
        var participationConfirmed = snapshot.RelativeVolume is { } dailyRvol &&
            dailyRvol >= options.MinDailyRelativeVolume;
        var nearRecentHigh = distanceFromRecentHighPct is { } distance &&
            distance <= options.MaxDistanceBelowRecentHighPct;
        var notTooExtended = ema20ExtensionAtr is null ||
            ema20ExtensionAtr <= options.MaxEma20ExtensionAtr;

        var score = Score(
            isDailyEvidence,
            priceAboveTrend,
            emaAligned,
            macdImproving,
            participationConfirmed,
            nearRecentHigh,
            notTooExtended);
        var shouldAlert = isDailyEvidence && priceAboveTrend && emaAligned && macdImproving &&
            participationConfirmed && nearRecentHigh && notTooExtended;
        var reason = BuildReason(
            shouldAlert,
            isDailyEvidence,
            priceAboveTrend,
            emaAligned,
            macdImproving,
            participationConfirmed,
            nearRecentHigh,
            notTooExtended,
            snapshot.RelativeVolume,
            distanceFromRecentHighPct,
            ema20ExtensionAtr,
            snapshot.Catalyst);

        return new WishlistSwingWatchEvaluation(
            ticker,
            snapshot.Timestamp.ToUniversalTime(),
            shouldAlert,
            shouldAlert ? "swing_breakout_watch" : "swing_watch_not_ready",
            shouldAlert ? ResolveSeverity(score) : "info",
            snapshot.CurrentPrice,
            reason,
            score,
            snapshot.RelativeVolume,
            distanceFromRecentHighPct,
            ema20ExtensionAtr,
            JsonSerializer.Serialize(new
            {
                timestampUtc = snapshot.Timestamp.ToUniversalTime(),
                snapshot.Timeframe,
                snapshot.CurrentPrice,
                snapshot.CurrentVolume,
                snapshot.Atr,
                snapshot.Ema10,
                snapshot.Ema20,
                snapshot.MacdHistogram,
                dailyRelativeVolume = snapshot.RelativeVolume,
                recentHigh = input.RecentHigh,
                distanceFromRecentHighPct,
                ema20ExtensionAtr,
                catalyst = snapshot.Catalyst is null ? null : new
                {
                    snapshot.Catalyst.Headline,
                    snapshot.Catalyst.Provider,
                    snapshot.Catalyst.Source,
                    snapshot.Catalyst.Url,
                    snapshot.Catalyst.SentimentScore,
                    timestampUtc = snapshot.Catalyst.Timestamp.ToUniversalTime()
                }
            }),
            snapshot.Catalyst?.Headline,
            snapshot.Catalyst?.Url,
            snapshot.Catalyst?.Provider);
    }

    private static decimal? CalculateDistanceBelow(decimal? high, decimal price) =>
        high is > 0m ? Decimal.Round(((high.Value - price) / high.Value) * 100m, 4) : null;

    private static decimal? CalculateEma20ExtensionAtr(IndicatorSnapshot snapshot)
    {
        if (snapshot.Ema20 is not { } ema20 || snapshot.Atr is not { } atr || atr <= 0m)
        {
            return null;
        }

        return Decimal.Round((snapshot.CurrentPrice - ema20) / atr, 4);
    }

    private static decimal Score(params bool[] checks) => checks.Length == 0
        ? 0m
        : Decimal.Round(checks.Count(check => check) / (decimal)checks.Length, 4);

    private static string ResolveSeverity(decimal score) => score >= 0.95m ? "high" : "medium";

    private static string BuildReason(
        bool shouldAlert,
        bool isDailyEvidence,
        bool priceAboveTrend,
        bool emaAligned,
        bool macdImproving,
        bool participationConfirmed,
        bool nearRecentHigh,
        bool notTooExtended,
        decimal? dailyRelativeVolume,
        decimal? distanceFromRecentHighPct,
        decimal? ema20ExtensionAtr,
        CatalystEvent? catalyst)
    {
        if (shouldAlert)
        {
            var catalystText = catalyst is null
                ? " No matched news catalyst."
                : $" News match: {catalyst.Headline} ({catalyst.Provider ?? "news"}).";
            return $"Swing watch: daily trend, MACD, participation, and recent-high proximity are aligned. " +
                $"Daily RVOL={Format(dailyRelativeVolume)}, distance from 20-session high={Format(distanceFromRecentHighPct)}%, " +
                $"EMA20 extension ATR={Format(ema20ExtensionAtr)}.{catalystText}";
        }

        var failures = new List<string>();
        if (!isDailyEvidence) failures.Add("daily evidence unavailable");
        if (!priceAboveTrend) failures.Add("price below daily EMA20");
        if (!emaAligned) failures.Add("daily EMA10 below EMA20");
        if (!macdImproving) failures.Add("daily MACD histogram not bullish/improving");
        if (!participationConfirmed) failures.Add("daily relative volume below minimum or unavailable");
        if (!nearRecentHigh) failures.Add("price is not near the prior 20-session high");
        if (!notTooExtended) failures.Add("price is too extended above EMA20");
        return $"No swing watch: {String.Join(", ", failures)}. Daily RVOL={Format(dailyRelativeVolume)}, " +
            $"distance from high={Format(distanceFromRecentHighPct)}%, EMA20 extension ATR={Format(ema20ExtensionAtr)}.";
    }

    private static string Format(decimal? value) => value.HasValue
        ? value.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
        : "n/a";

    private static string NormalizeTicker(string ticker) => ticker.Trim().ToUpperInvariant();
}
