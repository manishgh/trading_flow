using System.Globalization;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Engine.Regime;

/// <summary>
/// Daily VIX closes from FRED (series VIXCLS), keyed by observation date. FRED also lists values on some exchange holidays
/// (for example 7 Sep 2026); callers only ever look up the date of an exchange session (see <see cref="VixRegime"/>), so a
/// holiday value is never used. Missing observations ("." or an empty value in FRED's file) are left out, never filled in.
/// </summary>
public sealed class VixCloseSeries
{
    private readonly IReadOnlyDictionary<DateOnly, decimal> closes;

    private VixCloseSeries(IReadOnlyDictionary<DateOnly, decimal> closes) => this.closes = closes;

    /// <summary>Number of dates with a published close.</summary>
    public int Count => closes.Count;

    /// <summary>Earliest observation date with a published close, or null when the series is empty.</summary>
    public DateOnly? FirstDate => closes.Count == 0 ? null : closes.Keys.Min();

    /// <summary>Latest observation date with a published close, or null when the series is empty.</summary>
    public DateOnly? LastDate => closes.Count == 0 ? null : closes.Keys.Max();

    /// <summary>The published close for <paramref name="date"/>, when FRED lists one.</summary>
    public bool TryGetClose(DateOnly date, out decimal close) => closes.TryGetValue(date, out close);

    /// <summary>A copy holding only observations dated before <paramref name="cutoff"/> (protected research windows).</summary>
    public VixCloseSeries Before(DateOnly cutoff) =>
        new(closes.Where(pair => pair.Key < cutoff).ToDictionary(pair => pair.Key, pair => pair.Value));

    /// <summary>Builds a series from (date, close) pairs, for sources other than FRED's file and for tests.</summary>
    public static VixCloseSeries From(IEnumerable<KeyValuePair<DateOnly, decimal>> observations) =>
        new(observations.ToDictionary(pair => pair.Key, pair => pair.Value));

    /// <summary>
    /// Parses FRED's graph CSV ("observation_date,VIXCLS" or the older "DATE,VIXCLS" header, then one "yyyy-MM-dd,value" row
    /// per date). Strips a leading UTF-8 BOM if present. A row whose value is "." or empty is a missing observation and is
    /// skipped. Any other malformed row, a duplicate date, an unexpected header or a non-positive close refuses the whole file.
    /// </summary>
    public static VixCloseSeries ParseFredCsv(string text, string seriesId = RegimeRules.FredVixSeries)
    {
        var normalized = text.TrimStart('\uFEFF').Replace("\r", "", StringComparison.Ordinal);
        var lines = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) throw new InvalidDataException("The FRED file is empty.");
        var header = lines[0].Split(',');
        var dateHeader = header.Length == 2 ? header[0].Trim() : string.Empty;
        var validDateHeader = dateHeader.Equals("observation_date", StringComparison.OrdinalIgnoreCase) ||
            dateHeader.Equals("DATE", StringComparison.OrdinalIgnoreCase);
        if (header.Length != 2 || !validDateHeader || !header[1].Trim().Equals(seriesId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The FRED file header '{lines[0]}' is not 'observation_date,{seriesId}' or 'DATE,{seriesId}'.");
        var closes = new Dictionary<DateOnly, decimal>();
        for (var index = 1; index < lines.Length; index++)
        {
            var cells = lines[index].Split(',');
            if (cells.Length != 2 || !DateOnly.TryParseExact(cells[0].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                throw new InvalidDataException($"FRED file line {index + 1} ('{lines[index]}') is not 'yyyy-MM-dd,value'.");
            var value = cells[1].Trim();
            if (value.Length == 0 || value == ".") continue;
            if (!Decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var close) || close <= 0m)
                throw new InvalidDataException($"FRED file line {index + 1} has an invalid close '{value}'.");
            if (!closes.TryAdd(date, close))
                throw new InvalidDataException($"FRED file lists {date:yyyy-MM-dd} twice.");
        }
        return new(closes);
    }
}

/// <summary>What the VIX rule decided for one entry: a refusal reason, or the per-trade risk scale (1 = full risk).</summary>
/// <param name="Session">The exchange session whose VIX close was read (the session before the entry), when known.</param>
/// <param name="Close">The VIX close of that session, when published.</param>
public sealed record VixEntryDecision(string? Rejection, decimal RiskScale, DateOnly? Session, decimal? Close)
{
    /// <summary>The decision of a strategy without VIX sizing: allowed at full risk.</summary>
    public static VixEntryDecision NotRequired { get; } = new(null, 1m, null, null);
}

/// <summary>
/// The one VIX sizing rule shared by backtests, paper trading and the Desk. For an entry decided at time t, the VIX close of
/// the previous exchange session is read: the session of the latest completed benchmark (SPY) daily bar known before t, which
/// is the same close the market-regime rule reads. FRED's latest published close is therefore never used on its own: a value
/// FRED lists for a holiday is skipped, and a session FRED has not published yet refuses the entry instead of falling back to
/// an older close. Below the full-risk threshold the trade takes its normal risk; from that threshold to the no-entry
/// threshold (both included) the reduced scale; above it the entry is refused.
/// </summary>
public static class VixRegime
{
    /// <summary>Rejection when the previous session's VIX close is above the long no-entry threshold (22).</summary>
    public const string OffReason = "vix_regime_off_on_entry_day";
    /// <summary>Rejection when the previous session's VIX close is below the short no-entry threshold (18).</summary>
    public const string ShortOffReason = "vix_short_regime_off_on_entry_day";
    /// <summary>Rejection when FRED lists no VIX close for the previous session (not yet published, or missing), or the session is unknown.</summary>
    public const string CloseUnknownReason = "vix_close_unknown_for_previous_session";
    /// <summary>Rejection when the VIX source could not be read (request failed, or no source is configured).</summary>
    public const string LookupFailedReason = "vix_lookup_failed";

    /// <summary>Cross-platform US Eastern exchange timezone ("America/New_York" with Windows "Eastern Standard Time" fallback).</summary>
    public static TimeZoneInfo NewYorkTimeZone { get; } = ResolveNewYorkTimeZone();

    /// <summary>
    /// Applies the long or short VIX sizing thresholds of <paramref name="rules"/> to the close of
    /// <paramref name="previousSession"/> in <paramref name="series"/>. Long rules take full risk below
    /// <c>VixFullRiskBelow</c>, reduced risk in <c>[VixFullRiskBelow, VixNoLongsAbove]</c>, and refuse above
    /// <c>VixNoLongsAbove</c>; short rules refuse below <c>VixNoShortsBelow</c>, take reduced risk in
    /// <c>[VixNoShortsBelow, VixShortFullRiskAbove]</c>, and take full risk above <c>VixShortFullRiskAbove</c>.
    /// A strategy without VIX sizing is always allowed at full risk; every missing piece fails closed.
    /// </summary>
    public static VixEntryDecision Decide(RegimeRules rules, VixCloseSeries? series, DateOnly? previousSession)
    {
        if (!rules.UsesVix) return VixEntryDecision.NotRequired;
        rules.Validate();
        if (series is null) return new(LookupFailedReason, 0m, previousSession, null);
        if (previousSession is not { } session || !series.TryGetClose(session, out var close))
            return new(CloseUnknownReason, 0m, previousSession, null);
        if (rules.UsesShortVix)
        {
            if (close < rules.VixNoShortsBelow!.Value) return new(ShortOffReason, 0m, session, close);
            var shortScale = close > rules.VixShortFullRiskAbove!.Value ? 1m : rules.VixReducedRiskScale!.Value;
            return new(null, shortScale, session, close);
        }
        if (close > rules.VixNoLongsAbove!.Value) return new(OffReason, 0m, session, close);
        var scale = close < rules.VixFullRiskBelow!.Value ? 1m : rules.VixReducedRiskScale!.Value;
        return new(null, scale, session, close);
    }

    /// <summary>
    /// Loads the latest published VIX closes from <paramref name="source"/> for live/Desk paths. Returns null when no
    /// source is configured or when a non-caller-cancelled failure occurs (refusing entries as <see cref="LookupFailedReason"/>
    /// instead of aborting the ticker refresh; structured failure diagnostics are logged by the source implementation).
    /// </summary>
    public static async Task<VixCloseSeries?> TryLoadLatestAsync(IVixCloseSource? source, CancellationToken cancellationToken)
    {
        if (source is null) return null;
        try
        {
            return await source.LoadLatestAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Drops benchmark daily bars dated on known non-trading days (such as Sunday overnight or holiday prints resampled from
    /// sub-daily bars) when an authoritative exchange schedule is supplied. Bars outside the schedule map are kept so recent
    /// live windows do not discard older SMA warm-up bars.
    /// </summary>
    public static IReadOnlyList<OhlcvBar> FilterTradingDayBars(
        IReadOnlyList<OhlcvBar> benchmarkDailyBars,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? schedules)
    {
        if (schedules is null || schedules.Count == 0 || benchmarkDailyBars.Count == 0)
            return benchmarkDailyBars;
        var filtered = new List<OhlcvBar>(benchmarkDailyBars.Count);
        foreach (var bar in benchmarkDailyBars)
        {
            var barDate = DateOnly.FromDateTime(bar.Timestamp.UtcDateTime);
            if (schedules.TryGetValue(barDate, out var schedule) && !schedule.IsTradingDay)
                continue;
            filtered.Add(bar);
        }
        return filtered;
    }

    /// <summary>
    /// Verifies that <paramref name="benchmarkSession"/> is not older than the latest completed trading session required by
    /// the authoritative <paramref name="schedules"/> at <paramref name="decisionTime"/>. Returns true when no authoritative
    /// schedule is supplied (synthetic providers), and false (fail closed) when the schedule is incomplete or the benchmark
    /// ended on an earlier session (such as D-2 instead of D-1).
    /// </summary>
    public static bool HasFreshBenchmarkSession(
        DateOnly? benchmarkSession,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? schedules,
        DateTimeOffset decisionTime)
    {
        if (schedules is null || schedules.Count == 0) return true;
        if (benchmarkSession is not { } actual) return false;
        return TryExpectedPreviousSession(schedules, decisionTime, out var expected) && actual >= expected;
    }

    /// <summary>
    /// Resolves the latest completed exchange trading day at <paramref name="decisionTime"/> using the authoritative session
    /// schedule (same completion boundary and calendar continuity check as
    /// <see cref="TechnicalFactorCalculator.ExpectedCompletedSessions"/>). Returns false when the schedule has a gap or no
    /// completed trading day before <paramref name="decisionTime"/>.
    /// </summary>
    public static bool TryExpectedPreviousSession(
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule> schedules,
        DateTimeOffset decisionTime,
        out DateOnly expectedSession)
    {
        try
        {
            var sessions = TechnicalFactorCalculator.ExpectedCompletedSessions(schedules, decisionTime, 1);
            expectedSession = sessions[0];
            return true;
        }
        catch (InvalidDataException)
        {
            expectedSession = default;
            return false;
        }
    }

    /// <summary>
    /// The previous exchange session for a decision at <paramref name="decisionTime"/>: the date of the latest benchmark daily
    /// bar that had ended (and, when stamped, was received) by then. Uses the same completion test as
    /// <see cref="RegimeCalendarBuilder.IsRegimeOnAt"/>, so a still-forming bar for today is never counted. Null without bars.
    /// </summary>
    public static DateOnly? PreviousSession(IReadOnlyList<OhlcvBar> benchmarkDailyBars, DateTimeOffset decisionTime)
    {
        DateTimeOffset? latest = null;
        foreach (var bar in benchmarkDailyBars)
        {
            if (bar.Timestamp.Add(TimeframeParser.Parse(bar.Timeframe)) > decisionTime) continue;
            if (bar.KnownAtUtc is { } known && known > decisionTime) continue;
            if (latest is null || bar.Timestamp > latest) latest = bar.Timestamp;
        }
        return latest is { } timestamp ? DateOnly.FromDateTime(timestamp.UtcDateTime) : null;
    }

    /// <summary>Resolves New York time across Linux/macOS (IANA) and Windows (registry fallback).</summary>
    private static TimeZoneInfo ResolveNewYorkTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
    }
}
