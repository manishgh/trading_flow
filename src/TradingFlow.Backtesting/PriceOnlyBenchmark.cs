using System.Globalization;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Market;

namespace TradingFlow.Backtesting;

internal static class PriceOnlyBenchmark
{
    private const string Basis = "price_only_regular_session";
    private const string TestOpen = "first_evaluation_session_open";
    private const string PriorMonthClose = "prior_month_final_session_close";
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private readonly record struct PriceObservation(decimal Price, DateTimeOffset TimestampUtc);
    private sealed record PeriodPrices(PriceObservation? FirstOpen, PriceObservation? LastClose, string? UnavailableReason);

    // Selects regular-session prices within the declared window, independent of strategy trades and warm-up.
    internal static BenchmarkValidation Build(BenchmarkConfig config, IReadOnlyCollection<OhlcvBar> bars,
        DateTimeOffset start, DateTimeOffset end, IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? calendar,
        IReadOnlyCollection<StrategyBacktestResult> strategyResults, MarketDataProvenance? provenance = null)
    {
        if (!config.Enabled)
            return new(false, String.Empty, null, []);
        if (end <= start)
            return new(true, config.Ticker, null, [], Basis, "The benchmark requires a start before the end of the run.", []);
        var sourceStart = Utc(LocalDate(start), TimeOnly.MinValue);
        var symbolBars = bars.Where(bar => bar.Ticker.Equals(config.Ticker, StringComparison.OrdinalIgnoreCase) &&
            bar.Timestamp >= sourceStart && bar.Timestamp < end).ToArray();
        var timeframes = symbolBars.Select(bar => bar.Timeframe).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var timeframe = timeframes.Length == 1 ? timeframes[0] : null;
        var duration = timeframe is null ? (TimeSpan?)null : TimeframeParser.Parse(timeframe);
        OhlcvBar[] completedBars = duration is { } interval
            ? symbolBars.Where(bar => IsAvailable(bar, interval, end)).ToArray() : [];
        var adjustment = MarketPriceBasis.FromBars(completedBars, provenance);
        var unavailable = symbolBars.Length == 0 ? $"No {config.Ticker} benchmark bars cover the comparison interval."
            : completedBars.Length > 0 ? AdjustmentProblem(adjustment) : null;
        var byDate = completedBars.ToLookup(bar => LocalDate(bar.Timestamp));
        var fullPeriod = unavailable is null ? ReadPeriod(start, end, calendar, byDate, timeframe) :
            new PeriodPrices(null, null, unavailable);
        var total = Return(fullPeriod.FirstOpen?.Price, fullPeriod.LastClose?.Price);
        var comparisons = strategyResults.Select(strategy => new StrategyBenchmarkComparison(
            strategy.StrategyId, strategy.StrategyName, strategy.Source, strategy.TotalReturnPct,
            total is { } benchmark ? Decimal.Round(strategy.TotalReturnPct - benchmark, 4) : null)).ToArray();
        var months = BuildMonths(start, end, calendar, byDate, timeframe, unavailable);
        return new(true, config.Ticker, total, comparisons, Basis, fullPeriod.UnavailableReason, months,
            start.ToUniversalTime(), end.ToUniversalTime(), adjustment.Policy, fullPeriod.FirstOpen?.Price,
            fullPeriod.LastClose?.Price, fullPeriod.FirstOpen?.TimestampUtc, fullPeriod.LastClose?.TimestampUtc);
    }

    // A verified raw or split-only source can support the price-only comparison.
    private static string? AdjustmentProblem(MarketPriceBasis adjustment)
    {
        if (adjustment.HasDividendAdjustment)
            return "The supplied benchmark prices include dividend adjustments, which prevents a price-only comparison.";
        if (adjustment.Policy.StartsWith("mixed (", StringComparison.Ordinal))
            return "The benchmark bars use mixed price adjustment bases.";
        if (adjustment.Policy is not ("raw" or "split" or "split_adjusted"))
            return "The benchmark price adjustment basis does not verify raw or split-only prices.";
        return null;
    }

    // Carry the prior month's close as the opening value of a continuously held benchmark.
    private static IReadOnlyList<MonthlyBenchmarkValidation> BuildMonths(DateTimeOffset start, DateTimeOffset end,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? calendar, ILookup<DateOnly, OhlcvBar> bars, string? timeframe,
        string? unavailable)
    {
        var results = new List<MonthlyBenchmarkValidation>();
        var first = LocalDate(start);
        var last = LocalDate(end.AddTicks(-1));
        PriceObservation? priorClose = null;
        for (var month = new DateOnly(first.Year, first.Month, 1); month <= last; month = month.AddMonths(1))
        {
            var boundary = Utc(month.AddMonths(1), TimeOnly.MinValue);
            var firstPeriod = results.Count == 0;
            var periodStart = firstPeriod ? start : Utc(month, TimeOnly.MinValue);
            var periodEnd = boundary < end ? boundary : end;
            var prices = unavailable is null ? ReadPeriod(periodStart, periodEnd, calendar, bars, timeframe) :
                new PeriodPrices(null, null, unavailable);
            var opening = firstPeriod ? prices.FirstOpen : priorClose;
            var reason = prices.UnavailableReason ?? (opening is null ? "Prior month closing price is unavailable." : null);
            results.Add(new(month.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                reason is null ? Return(opening?.Price, prices.LastClose?.Price) : null,
                firstPeriod ? TestOpen : PriorMonthClose, reason, periodStart.ToUniversalTime(), periodEnd.ToUniversalTime(),
                opening?.Price, prices.LastClose?.Price, opening?.TimestampUtc, prices.LastClose?.TimestampUtc));
            priorClose = prices.LastClose;
        }
        return results;
    }

    // Missing dates, incomplete boundary sessions and absent regular-session prices each make the comparison unavailable.
    private static PeriodPrices ReadPeriod(DateTimeOffset start, DateTimeOffset end,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? calendar, ILookup<DateOnly, OhlcvBar> bars, string? timeframe)
    {
        if (calendar is null || calendar.Count == 0)
            return new(null, null, "An authoritative New York session calendar is unavailable.");
        if (timeframe is null || (TimeframeParser.IsDailyOrHigher(timeframe) &&
            TimeframeParser.Parse(timeframe) != TimeSpan.FromDays(1)))
            return new(null, null, "A single daily or sub-daily benchmark bar series is required.");

        PriceObservation? firstOpen = null;
        PriceObservation? lastClose = null;
        var lastDate = LocalDate(end.AddTicks(-1));
        for (var date = LocalDate(start); date <= lastDate; date = date.AddDays(1))
        {
            if (!calendar.TryGetValue(date, out var session) || session.TradeDate != date)
                return new(null, null, $"New York calendar coverage is missing for {date:yyyy-MM-dd}.");
            if (!session.IsTradingDay) continue;
            var open = Utc(date, session.RegularOpen);
            var close = Utc(date, session.RegularClose);
            if (close <= open)
                return new(null, null, $"New York calendar has invalid regular session hours for {date:yyyy-MM-dd}.");
            if (close <= start || open >= end) continue;
            if (open < start || close > end)
                return new(null, null, $"The comparison interval lacks a complete regular session for {date:yyyy-MM-dd}.");
            var prices = ReadSession(bars[date], timeframe, open, close, end);
            if (prices is not { } observed)
                return new(null, null, $"Completed benchmark bars do not cover the regular session for {date:yyyy-MM-dd}.");
            firstOpen ??= new PriceObservation(observed.Open, open);
            lastClose = new PriceObservation(observed.Close, close);
        }
        return new(firstOpen, lastClose, firstOpen is null ? "There are no complete New York trading sessions in this interval." : null);
    }

    // A daily candle covers its calendar session. Sub-daily evidence must span the complete scheduled session.
    private static (decimal Open, decimal Close)? ReadSession(IEnumerable<OhlcvBar> bars, string timeframe,
        DateTimeOffset open, DateTimeOffset close, DateTimeOffset cutoff)
    {
        var duration = TimeframeParser.Parse(timeframe);
        if (duration <= TimeSpan.Zero) return null;
        var known = bars.Where(bar => IsAvailable(bar, duration, cutoff));
        if (duration == TimeSpan.FromDays(1))
        {
            var daily = known.ToArray();
            return daily is [{ Open: > 0, Close: > 0 } bar] ? (bar.Open, bar.Close) : null;
        }

        var session = known.Where(bar => bar.Timestamp >= open && bar.Timestamp.Add(duration) <= close)
            .OrderBy(bar => bar.Timestamp).ToArray();
        if (session.Length == 0 || session[^1].Timestamp.Add(duration) != close) return null;
        for (var index = 0; index < session.Length; index++)
        {
            if (session[index].Timestamp != open.AddTicks(duration.Ticks * index) ||
                session[index].Open <= 0m || session[index].Close <= 0m) return null;
        }
        return (session[0].Open, session[^1].Close);
    }

    // Use the candle completion clock and any later provider receipt, matching the prepared-data boundary.
    private static bool IsAvailable(OhlcvBar bar, TimeSpan duration, DateTimeOffset cutoff) =>
        bar.Timestamp.Add(duration) <= cutoff && (bar.KnownAtUtc is null || bar.KnownAtUtc <= cutoff);

    // Returns percentage points in the same precision as strategy validation.
    private static decimal? Return(decimal? opening, decimal? closing) =>
        opening is > 0m && closing is > 0m ? Decimal.Round((closing.Value / opening.Value - 1m) * 100m, 4) : null;

    // Maps the provider's source stamp into the New York session date.
    private static DateOnly LocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, NewYork).DateTime);

    // Converts a calendar schedule or month boundary using the offset effective on that date.
    private static DateTimeOffset Utc(DateOnly date, TimeOnly time) =>
        new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(time), NewYork), TimeSpan.Zero);
}
