using System.Globalization;
using System.Text.RegularExpressions;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Regime;

namespace TradingFlow.Engine.Market;

/// <summary>
/// Parsed fields of a standard OCC option contract symbol (for example <c>SPY261218C00600000</c>).
/// </summary>
public readonly record struct ParsedOccOptionSymbol(
    string UnderlyingSymbol,
    DateOnly ExpirationDate,
    bool IsCall,
    decimal Strike);

/// <summary>
/// Pure deterministic calculator converting raw OPRA option quotes into equity implied move, 25-delta put-call skew,
/// IV term structure, call/put volume ratio, and a plain-English trader regime summary.
/// </summary>
public static partial class OptionsSentimentCalculator
{
    public const decimal MaxBidAskSpreadToMidRatio = 0.35m;
    public const decimal MinStrikeMoneyness = 0.80m;
    public const decimal MaxStrikeMoneyness = 1.20m;
    public const int MinCalendarDte = 2;
    public const int MaxCalendarDte = 60;
    public const decimal PutSkewHedgingThreshold = 0.06m;
    public const decimal CallSkewSqueezeThreshold = -0.04m;

    private const decimal TargetAbsDelta = 0.25m;
    private const decimal MaxDeltaDistance = 0.12m;

    [GeneratedRegex(@"^([A-Z]+(?:[.-][A-Z]+)?)(\d{6})([CP])(\d{8})$", RegexOptions.CultureInvariant)]
    private static partial Regex OccSymbolRegex();

    /// <summary>
    /// Normalizes a stock or OCC underlying root by trimming, uppercasing, and removing share-class punctuation
    /// (<c>.</c> and <c>-</c>) so <c>BRK.B</c>/<c>BF-B</c> match <c>BRKB</c>/<c>BFB</c> deterministically.
    /// </summary>
    public static string NormalizeOccRoot(string? symbol) =>
        string.IsNullOrWhiteSpace(symbol)
            ? string.Empty
            : symbol.Trim().ToUpperInvariant().Replace(".", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// Parses a standard OCC option symbol into its underlying ticker, expiration date, call/put flag, and strike price.
    /// Returns <see langword="false"/> for malformed symbols, invalid dates, or non-positive strikes.
    /// </summary>
    public static bool TryParseOccSymbol(string? contractSymbol, out ParsedOccOptionSymbol parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(contractSymbol))
        {
            return false;
        }

        var match = OccSymbolRegex().Match(contractSymbol.Trim().ToUpperInvariant());
        if (!match.Success)
        {
            return false;
        }

        var dateDigits = match.Groups[2].Value;
        var year = 2000 + int.Parse(dateDigits[..2], CultureInfo.InvariantCulture);
        var month = int.Parse(dateDigits[2..4], CultureInfo.InvariantCulture);
        var day = int.Parse(dateDigits[4..6], CultureInfo.InvariantCulture);
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        var strikeTicks = long.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
        if (strikeTicks <= 0)
        {
            return false;
        }

        parsed = new ParsedOccOptionSymbol(
            UnderlyingSymbol: match.Groups[1].Value,
            ExpirationDate: new DateOnly(year, month, day),
            IsCall: match.Groups[3].Value[0] == 'C',
            Strike: strikeTicks / 1000m);
        return true;
    }

    /// <summary>
    /// Computes read-only options sentiment metrics from <paramref name="quotes"/> as of <paramref name="observedAtUtc"/>
    /// using New York calendar dates for days-to-expiry calculations.
    /// </summary>
    public static OptionsSentimentSnapshot Calculate(
        string symbol,
        DateTimeOffset observedAtUtc,
        decimal? spotPrice,
        IReadOnlyList<OptionContractQuote> quotes,
        decimal? adr20Pct = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentNullException.ThrowIfNull(quotes);

        var normalizedSymbol = symbol.Trim().ToUpperInvariant();
        var expectedRoot = NormalizeOccRoot(normalizedSymbol);
        var matchingQuotes = quotes
            .Where(q => string.Equals(NormalizeOccRoot(q.UnderlyingSymbol), expectedRoot, StringComparison.Ordinal))
            .ToArray();
        var utcObserved = observedAtUtc.ToUniversalTime();
        var observedDateNy = ToNewYorkDate(utcObserved);
        var resolvedSpot = spotPrice is > 0m
            ? spotPrice.Value
            : TryInferSpotFromPutCallParity(matchingQuotes, observedDateNy);

        if (resolvedSpot is not { } spot || spot <= 0m)
        {
            return new OptionsSentimentSnapshot(
                Symbol: normalizedSymbol,
                ObservedAtUtc: utcObserved,
                SpotPrice: null,
                FrontExpiry: null,
                TradingDaysToFrontExpiry: null,
                FrontImpliedMovePct: null,
                AtmImpliedVolPct: null,
                Skew25Delta: null,
                TermStructureSpread: null,
                CallVolume: 0m,
                PutVolume: 0m,
                CallPutVolumeRatio: null,
                ImpliedToRealizedAdrRatio: null,
                LiquidContractCount: 0,
                RegimeLabel: OptionsSentimentRegime.NoLiquidOptions,
                SummaryNote: "Spot price and liquid option pairs are unavailable; no options sentiment computed.");
        }

        var liquid = FilterLiquidQuotes(matchingQuotes, observedDateNy, spot);
        if (liquid.Count == 0)
        {
            return new OptionsSentimentSnapshot(
                Symbol: normalizedSymbol,
                ObservedAtUtc: utcObserved,
                SpotPrice: spot,
                FrontExpiry: null,
                TradingDaysToFrontExpiry: null,
                FrontImpliedMovePct: null,
                AtmImpliedVolPct: null,
                Skew25Delta: null,
                TermStructureSpread: null,
                CallVolume: 0m,
                PutVolume: 0m,
                CallPutVolumeRatio: null,
                ImpliedToRealizedAdrRatio: null,
                LiquidContractCount: 0,
                RegimeLabel: OptionsSentimentRegime.NoLiquidOptions,
                SummaryNote: "No liquid OPRA contracts within 20% of spot and 2-60 DTE passed the 35% bid-ask spread filter.");
        }

        var frontStraddle = ComputeFrontStraddle(liquid, observedDateNy, spot);
        var skew25Delta = ComputeSkew25Delta(liquid, observedDateNy);
        var termStructureSpread = ComputeTermStructureSpread(liquid, observedDateNy, spot);
        var callVolume = liquid.Where(q => q.IsCall).Sum(q => Math.Max(0m, q.DailyVolume));
        var putVolume = liquid.Where(q => !q.IsCall).Sum(q => Math.Max(0m, q.DailyVolume));
        decimal? callPutVolumeRatio = putVolume > 0m ? Math.Round(callVolume / putVolume, 4) : null;
        var impliedToAdrRatio = ComputeImpliedToRealizedAdrRatio(
            frontStraddle?.ImpliedMovePct,
            frontStraddle?.TradingDays,
            adr20Pct);

        var regimeLabel = ClassifyRegime(skew25Delta, termStructureSpread);
        var summaryNote = BuildSummaryNote(
            regimeLabel,
            frontStraddle?.ImpliedMovePct,
            frontStraddle?.TradingDays,
            skew25Delta,
            termStructureSpread,
            callPutVolumeRatio,
            impliedToAdrRatio,
            liquid.Count);

        return new OptionsSentimentSnapshot(
            Symbol: normalizedSymbol,
            ObservedAtUtc: utcObserved,
            SpotPrice: spot,
            FrontExpiry: frontStraddle?.Expiry,
            TradingDaysToFrontExpiry: frontStraddle?.TradingDays,
            FrontImpliedMovePct: frontStraddle?.ImpliedMovePct,
            AtmImpliedVolPct: frontStraddle?.AtmImpliedVolPct,
            Skew25Delta: skew25Delta,
            TermStructureSpread: termStructureSpread,
            CallVolume: callVolume,
            PutVolume: putVolume,
            CallPutVolumeRatio: callPutVolumeRatio,
            ImpliedToRealizedAdrRatio: impliedToAdrRatio,
            LiquidContractCount: liquid.Count,
            RegimeLabel: regimeLabel,
            SummaryNote: summaryNote);
    }

    /// <summary>
    /// Recomputes <see cref="OptionsSentimentSnapshot.ImpliedToRealizedAdrRatio"/> when a cached snapshot is reused
    /// with a caller-supplied 20-day ADR% without repeating an OPRA request.
    /// </summary>
    public static OptionsSentimentSnapshot WithAdr20Pct(OptionsSentimentSnapshot snapshot, decimal? adr20Pct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var ratio = ComputeImpliedToRealizedAdrRatio(
            snapshot.FrontImpliedMovePct,
            snapshot.TradingDaysToFrontExpiry,
            adr20Pct);
        return ratio == snapshot.ImpliedToRealizedAdrRatio
            ? snapshot
            : snapshot with { ImpliedToRealizedAdrRatio = ratio };
    }

    // Converts a UTC observation timestamp into its New York exchange calendar date.
    private static DateOnly ToNewYorkDate(DateTimeOffset observedAtUtc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(observedAtUtc, VixRegime.NewYorkTimeZone).DateTime);

    // Filters quotes to positive two-sided markets with <= 35% bid-ask spread, 2-60 calendar DTE, and 80%-120% strike moneyness.
    private static List<OptionContractQuote> FilterLiquidQuotes(
        IReadOnlyList<OptionContractQuote> quotes,
        DateOnly observedDateNy,
        decimal spot)
    {
        var minStrike = spot * MinStrikeMoneyness;
        var maxStrike = spot * MaxStrikeMoneyness;
        var liquid = new List<OptionContractQuote>(quotes.Count);
        foreach (var quote in quotes)
        {
            if (!PassesBidAskLiquidity(quote))
            {
                continue;
            }

            var dte = quote.ExpirationDate.DayNumber - observedDateNy.DayNumber;
            if (dte < MinCalendarDte || dte > MaxCalendarDte)
            {
                continue;
            }

            if (quote.Strike < minStrike || quote.Strike > maxStrike)
            {
                continue;
            }

            liquid.Add(quote);
        }

        return liquid;
    }

    // Checks basic quote validity (Bid > 0, Ask >= Bid, positive mid) and the 35% maximum bid-ask spread-to-mid ratio.
    private static bool PassesBidAskLiquidity(OptionContractQuote quote)
    {
        if (quote.Bid <= 0m || quote.Ask < quote.Bid)
        {
            return false;
        }

        var mid = (quote.Bid + quote.Ask) / 2m;
        return mid > 0m && (quote.Ask - quote.Bid) / mid <= MaxBidAskSpreadToMidRatio;
    }

    // Estimates spot via put-call parity (Strike + CallMid - PutMid) on the closest-to-ATM liquid pair when no spot hint exists.
    private static decimal? TryInferSpotFromPutCallParity(IReadOnlyList<OptionContractQuote> quotes, DateOnly observedDateNy)
    {
        var candidates = quotes
            .Where(q => PassesBidAskLiquidity(q) &&
                        q.ExpirationDate.DayNumber - observedDateNy.DayNumber is >= MinCalendarDte and <= MaxCalendarDte)
            .GroupBy(q => q.ExpirationDate)
            .OrderBy(g => g.Key);

        foreach (var expiryGroup in candidates)
        {
            var bestPair = expiryGroup
                .GroupBy(q => q.Strike)
                .Select(g => (Call: g.FirstOrDefault(x => x.IsCall), Put: g.FirstOrDefault(x => !x.IsCall)))
                .Where(pair => pair.Call is not null && pair.Put is not null)
                .OrderBy(pair => Math.Abs(MidPrice(pair.Call!) - MidPrice(pair.Put!)))
                .ThenBy(pair => pair.Call!.Strike)
                .FirstOrDefault();

            if (bestPair.Call is not null && bestPair.Put is not null)
            {
                var inferred = bestPair.Call.Strike + MidPrice(bestPair.Call) - MidPrice(bestPair.Put);
                if (inferred > 0m)
                {
                    return Math.Round(inferred, 4);
                }
            }
        }

        return null;
    }

    private sealed record FrontStraddleMetrics(
        DateOnly Expiry,
        int TradingDays,
        decimal ImpliedMovePct,
        decimal? AtmImpliedVolPct);

    // Computes ATM straddle implied move % on the nearest liquid 5-14 DTE expiry, falling back to 2-30 DTE when no weekly exists.
    private static FrontStraddleMetrics? ComputeFrontStraddle(
        IReadOnlyList<OptionContractQuote> liquid,
        DateOnly observedDateNy,
        decimal spot)
    {
        var preferred = FindStraddleInWindow(liquid, observedDateNy, spot, minDte: 5, maxDte: 14);
        return preferred ?? FindStraddleInWindow(liquid, observedDateNy, spot, minDte: 2, maxDte: 30);
    }

    // Finds the nearest expiry inside [minDte, maxDte] that has a strike with both a liquid call and a liquid put.
    private static FrontStraddleMetrics? FindStraddleInWindow(
        IReadOnlyList<OptionContractQuote> liquid,
        DateOnly observedDateNy,
        decimal spot,
        int minDte,
        int maxDte)
    {
        var expiries = liquid
            .Where(q =>
            {
                var dte = q.ExpirationDate.DayNumber - observedDateNy.DayNumber;
                return dte >= minDte && dte <= maxDte;
            })
            .GroupBy(q => q.ExpirationDate)
            .OrderBy(g => g.Key);

        foreach (var expiryGroup in expiries)
        {
            var atmPair = expiryGroup
                .GroupBy(q => q.Strike)
                .Select(g => (
                    Strike: g.Key,
                    Call: SelectTightestQuote(g.Where(x => x.IsCall)),
                    Put: SelectTightestQuote(g.Where(x => !x.IsCall))))
                .Where(pair => pair.Call is not null && pair.Put is not null)
                .OrderBy(pair => Math.Abs(pair.Strike - spot))
                .ThenBy(pair => pair.Strike)
                .FirstOrDefault();

            if (atmPair.Call is null || atmPair.Put is null)
            {
                continue;
            }

            var straddleMid = MidPrice(atmPair.Call) + MidPrice(atmPair.Put);
            var impliedMovePct = Math.Round(straddleMid / spot * 100m, 4);
            var tradingDays = CountWeekdaysExclusiveInclusive(observedDateNy, expiryGroup.Key);
            var atmIv = AveragePositiveIv(atmPair.Call.ImpliedVolatility, atmPair.Put.ImpliedVolatility);
            decimal? atmIvPct = atmIv is { } iv ? Math.Round(iv * 100m, 4) : null;
            return new FrontStraddleMetrics(expiryGroup.Key, tradingDays, impliedMovePct, atmIvPct);
        }

        return null;
    }

    // Computes 25-delta put IV minus 25-delta call IV on the nearest 20-45 DTE expiry with both wings inside |abs(delta)-0.25| <= 0.12.
    private static decimal? ComputeSkew25Delta(IReadOnlyList<OptionContractQuote> liquid, DateOnly observedDateNy)
    {
        var minAbs = TargetAbsDelta - MaxDeltaDistance; // 0.13
        var maxAbs = TargetAbsDelta + MaxDeltaDistance; // 0.37

        var expiries = liquid
            .Where(q =>
            {
                var dte = q.ExpirationDate.DayNumber - observedDateNy.DayNumber;
                return dte is >= 20 and <= 45 && q.ImpliedVolatility is > 0m && q.Delta is not null;
            })
            .GroupBy(q => q.ExpirationDate)
            .OrderBy(g => g.Key);

        foreach (var expiryGroup in expiries)
        {
            var put25 = expiryGroup
                .Where(q => !q.IsCall && q.Delta!.Value <= -minAbs && q.Delta.Value >= -maxAbs)
                .OrderBy(q => Math.Abs(Math.Abs(q.Delta!.Value) - TargetAbsDelta))
                .ThenBy(q => SpreadRatio(q))
                .ThenBy(q => q.Strike)
                .FirstOrDefault();

            var call25 = expiryGroup
                .Where(q => q.IsCall && q.Delta!.Value >= minAbs && q.Delta.Value <= maxAbs)
                .OrderBy(q => Math.Abs(q.Delta!.Value - TargetAbsDelta))
                .ThenBy(q => SpreadRatio(q))
                .ThenBy(q => q.Strike)
                .FirstOrDefault();

            if (put25 is not null && call25 is not null)
            {
                return Math.Round(put25.ImpliedVolatility!.Value - call25.ImpliedVolatility!.Value, 6);
            }
        }

        return null;
    }

    // Computes near-term (7-21 DTE) ATM IV minus back-month (30-60 DTE) ATM IV; positive values indicate term backwardation.
    private static decimal? ComputeTermStructureSpread(
        IReadOnlyList<OptionContractQuote> liquid,
        DateOnly observedDateNy,
        decimal spot)
    {
        var nearIv = FindAtmIvInWindow(liquid, observedDateNy, spot, minDte: 7, maxDte: 21);
        var farIv = FindAtmIvInWindow(liquid, observedDateNy, spot, minDte: 30, maxDte: 60);
        return nearIv is { } near && farIv is { } far
            ? Math.Round(near - far, 6)
            : null;
    }

    // Resolves the ATM implied volatility (decimal) of the nearest expiry inside [minDte, maxDte].
    private static decimal? FindAtmIvInWindow(
        IReadOnlyList<OptionContractQuote> liquid,
        DateOnly observedDateNy,
        decimal spot,
        int minDte,
        int maxDte)
    {
        var expiries = liquid
            .Where(q =>
            {
                var dte = q.ExpirationDate.DayNumber - observedDateNy.DayNumber;
                return dte >= minDte && dte <= maxDte && q.ImpliedVolatility is > 0m;
            })
            .GroupBy(q => q.ExpirationDate)
            .OrderBy(g => g.Key);

        foreach (var expiryGroup in expiries)
        {
            var matchedPair = expiryGroup
                .GroupBy(q => q.Strike)
                .Select(g => (
                    Strike: g.Key,
                    Call: SelectTightestQuote(g.Where(x => x.IsCall)),
                    Put: SelectTightestQuote(g.Where(x => !x.IsCall))))
                .Where(pair => pair.Call is not null && pair.Put is not null)
                .OrderBy(pair => Math.Abs(pair.Strike - spot))
                .ThenBy(pair => pair.Strike)
                .FirstOrDefault();

            if (matchedPair.Call is not null && matchedPair.Put is not null)
            {
                return AveragePositiveIv(matchedPair.Call.ImpliedVolatility, matchedPair.Put.ImpliedVolatility);
            }

            var closestSingle = expiryGroup
                .OrderBy(q => Math.Abs(q.Strike - spot))
                .ThenBy(q => SpreadRatio(q))
                .ThenBy(q => q.Strike)
                .FirstOrDefault();
            if (closestSingle?.ImpliedVolatility is > 0m)
            {
                return closestSingle.ImpliedVolatility.Value;
            }
        }

        return null;
    }

    // Scales the front-expiry straddle move by sqrt(trading days) and compares against 20-day average daily range %.
    private static decimal? ComputeImpliedToRealizedAdrRatio(
        decimal? frontImpliedMovePct,
        int? tradingDaysToFrontExpiry,
        decimal? adr20Pct)
    {
        if (frontImpliedMovePct is not > 0m || tradingDaysToFrontExpiry is not > 0 || adr20Pct is not > 0m)
        {
            return null;
        }

        var dailyImpliedPct = frontImpliedMovePct.Value / (decimal)Math.Sqrt(Math.Max(1, tradingDaysToFrontExpiry.Value));
        return Math.Round(dailyImpliedPct / adr20Pct.Value, 4);
    }

    // Assigns one primary regime label prioritizing acute call-squeeze skew, then term-structure stress, then heavy put hedging.
    private static string ClassifyRegime(decimal? skew25Delta, decimal? termStructureSpread)
    {
        if (skew25Delta is < CallSkewSqueezeThreshold)
        {
            return OptionsSentimentRegime.CallSkewSqueezeRisk;
        }

        if (termStructureSpread is > 0m)
        {
            return OptionsSentimentRegime.TermBackwardationStress;
        }

        if (skew25Delta is > PutSkewHedgingThreshold)
        {
            return OptionsSentimentRegime.PutSkewHedging;
        }

        return OptionsSentimentRegime.Neutral;
    }

    // Formats a concise plain-English trader note reporting implied move, skew vol points, term structure, and active cautions.
    private static string BuildSummaryNote(
        string regimeLabel,
        decimal? frontImpliedMovePct,
        int? tradingDays,
        decimal? skew25Delta,
        decimal? termStructureSpread,
        decimal? callPutVolumeRatio,
        decimal? impliedToAdrRatio,
        int liquidContractCount)
    {
        var invariant = CultureInfo.InvariantCulture;
        var headline = regimeLabel switch
        {
            OptionsSentimentRegime.CallSkewSqueezeRisk => "Aggressive call skew (short-squeeze risk)",
            OptionsSentimentRegime.TermBackwardationStress => "Near-term IV backwardation (event/macro stress)",
            OptionsSentimentRegime.PutSkewHedging => "Elevated put skew (heavy downside hedging)",
            _ => "Balanced options pricing"
        };

        var parts = new List<string> { headline };
        if (frontImpliedMovePct is { } move)
        {
            parts.Add(string.Create(invariant, $"±{move:F1}% implied move ({tradingDays ?? 1}d)"));
        }

        if (skew25Delta is { } skew)
        {
            var skewPts = skew * 100m;
            var sign = skewPts >= 0m ? "+" : string.Empty;
            parts.Add(string.Create(invariant, $"25Δ skew {sign}{skewPts:F1} vol pts"));
        }

        if (termStructureSpread is { } term)
        {
            var termPts = term * 100m;
            var sign = termPts >= 0m ? "+" : string.Empty;
            parts.Add(string.Create(invariant, $"term {sign}{termPts:F1} vol pts"));
        }

        if (callPutVolumeRatio is { } cpRatio)
        {
            parts.Add(string.Create(invariant, $"C/P vol {cpRatio:F2}x"));
        }

        if (impliedToAdrRatio is { } adrRatio)
        {
            parts.Add(string.Create(invariant, $"implied/ADR {adrRatio:F2}x"));
        }

        parts.Add(string.Create(invariant, $"{liquidContractCount} liquid contracts"));
        return string.Join(" · ", parts);
    }

    // Counts weekdays strictly after startDate through endDate (inclusive), clamped to at least 1.
    private static int CountWeekdaysExclusiveInclusive(DateOnly startDate, DateOnly endDate)
    {
        var count = 0;
        for (var day = startDate.AddDays(1); day <= endDate; day = day.AddDays(1))
        {
            if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                count++;
            }
        }

        return Math.Max(1, count);
    }

    // Picks the quote with the tightest relative bid-ask spread when duplicate quotes exist for the same strike/side.
    private static OptionContractQuote? SelectTightestQuote(IEnumerable<OptionContractQuote> quotes) =>
        quotes
            .OrderBy(SpreadRatio)
            .ThenByDescending(q => q.DailyVolume)
            .ThenBy(q => q.ContractSymbol, StringComparer.Ordinal)
            .FirstOrDefault();

    private static decimal MidPrice(OptionContractQuote quote) => (quote.Bid + quote.Ask) / 2m;

    private static decimal SpreadRatio(OptionContractQuote quote)
    {
        var mid = MidPrice(quote);
        return mid <= 0m ? decimal.MaxValue : (quote.Ask - quote.Bid) / mid;
    }

    private static decimal? AveragePositiveIv(decimal? left, decimal? right) => (left, right) switch
    {
        (> 0m, > 0m) => (left.Value + right.Value) / 2m,
        (> 0m, _) => left.Value,
        (_, > 0m) => right.Value,
        _ => null
    };
}
