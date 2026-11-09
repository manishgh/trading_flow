using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using TradingFlow.Alpaca;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Backtesting;

/// <summary>One consolidated-tape trade: its time, tape (A/B = CTA, C = UTP) and sale condition codes.</summary>
public sealed record SipTradePrint(DateTimeOffset TimestampUtc, string Tape, IReadOnlyList<string> Conditions);

/// <summary>
/// Every trade the provider reported for one stock and time range, with the archive that proves it; or, when the trades
/// could not be obtained completely, the reason (and no trades).
/// </summary>
public sealed record IntervalTradePrints(IReadOnlyList<SipTradePrint> Trades, string EvidenceId, string? UnavailableReason = null)
{
    /// <summary>Marks a range whose trades could not be obtained; its gaps stay unresolved with this reason.</summary>
    public static IntervalTradePrints Unavailable(string reason) => new([], "", reason);
}

/// <summary>
/// Supplies the consolidated-tape trades for a stock over [start, end). A result with an unavailable reason (or null) means
/// the trades could not be obtained completely, so the gaps stay unresolved.
/// </summary>
public interface IIntervalTradePrintSource
{
    Task<IntervalTradePrints?> GetTradesAsync(string ticker, DateTimeOffset startUtc, DateTimeOffset endUtc,
        DateOnly sessionDate, CancellationToken cancellationToken);
}

/// <summary>
/// Sale conditions under which a trade sets no consolidated price (no last, high/low or open update), so a candle
/// provider correctly builds no candle from it. Frozen from the official tape specifications:
/// CTA tapes A and B: CTS Pillar Multicast Output Binary Specification v2.11b (29 January 2026), Sale Condition table,
/// p. 83 (consolidated Last, High/Low and Open all NO). UTP tape C: UTP Data Feed Services Specification (September 2026),
/// section 3.14.2 UTP Sale Condition Matrix, p. 48 (consolidated Update High/Low and Update Last both No).
/// Reserved, TBD and official-open codes are deliberately absent, so they count as possibly price-setting.
/// </summary>
public static class SipNonPriceSettingConditions
{
    public const string Version = "sip-non-price-setting-conditions-v1";

    private static readonly HashSet<string> Cta = ["B", "C", "H", "I", "M", "R", "T", "U", "V", "7"];
    private static readonly HashSet<string> Utp = ["C", "H", "I", "M", "R", "T", "U", "V", "W", "7"];

    /// <summary>
    /// True when the trade cannot set a candle price: on a known tape, at least one of its codes is a no-update code
    /// (the specifications give any "no" precedence over the trade's other codes). Unknown tapes count as price-setting.
    /// </summary>
    public static bool IsNonPriceSetting(SipTradePrint trade)
    {
        var table = trade.Tape switch { "A" or "B" => Cta, "C" => Utp, _ => null };
        return table is not null && trade.Conditions.Any(code => table.Contains(code.Trim()));
    }
}

/// <summary>
/// Confirmed quiet intervals for one stock, the gaps that stay unresolved with the reason for each, and the archive
/// references used.
/// </summary>
public sealed record QuietIntervalResolution(IReadOnlySet<DateTimeOffset> QuietIntervals,
    IReadOnlyList<DateTimeOffset> UnresolvedGaps, IReadOnlyList<string> EvidenceIds,
    IReadOnlyDictionary<DateTimeOffset, string> UnresolvedReasons);

/// <summary>
/// Finds regular-session five-minute intervals that have no candle and confirms, from the provider's trade records, the
/// ones in which trades happened but none set a price. A confirmed interval is not a candle: nothing can fill or trigger
/// in it. An interval with a price-setting or unknown code, with no trade records at all (indistinguishable from a
/// provider outage), or whose trades cannot be fetched, stays a gap.
/// </summary>
public static class IntradayQuietIntervals
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>Lists every missing regular-session interval in sessions that have at least one candle, in time order.</summary>
    public static IReadOnlyList<DateTimeOffset> ListGaps(IReadOnlyDictionary<DateOnly, OhlcvBar[]> sessionBars,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule> schedules, TimeZoneInfo zone)
    {
        var gaps = new List<DateTimeOffset>();
        foreach (var (date, bars) in sessionBars.OrderBy(pair => pair.Key))
        {
            if (bars.Length == 0 || !schedules.TryGetValue(date, out var session) || !session.IsTradingDay) continue;
            var starts = bars.Select(bar => bar.Timestamp).ToHashSet();
            var close = AtUtc(date, session.RegularClose, zone);
            for (var time = AtUtc(date, session.RegularOpen, zone); time < close; time = time.Add(Interval))
                if (!starts.Contains(time)) gaps.Add(time);
        }
        return gaps;
    }

    /// <summary>
    /// Fetches trades once per run of consecutive gaps (at most <paramref name="maximumGaps"/> gaps per stock, oldest
    /// first) and confirms a gap only when it holds at least one trade and every trade in it is non-price-setting.
    /// Without a source, nothing is confirmed.
    /// </summary>
    public static async Task<QuietIntervalResolution> ResolveAsync(string ticker, IReadOnlyList<DateTimeOffset> gaps,
        TimeZoneInfo zone, IIntervalTradePrintSource? source, int maximumGaps, CancellationToken cancellationToken)
    {
        var quiet = new HashSet<DateTimeOffset>();
        var reasons = new Dictionary<DateTimeOffset, string>();
        var evidence = new List<string>();
        foreach (var gap in gaps.Skip(maximumGaps)) reasons[gap] = "beyond_per_stock_trade_request_cap";
        if (source is null)
        {
            foreach (var gap in gaps) reasons[gap] = "no_trade_record_source_for_this_provider_or_feed";
            return new(quiet, gaps, evidence, reasons);
        }
        foreach (var run in ConsecutiveRuns(gaps.Take(maximumGaps).ToArray()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prints = await source.GetTradesAsync(ticker, run[0], run[^1].Add(Interval),
                DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(run[0], zone).DateTime), cancellationToken)
                ?? IntervalTradePrints.Unavailable("trade_records_unavailable");
            if (prints.UnavailableReason is { } unavailable)
            {
                foreach (var slot in run) reasons[slot] = unavailable;
                continue;
            }
            evidence.Add(prints.EvidenceId);
            var bySlot = prints.Trades.ToLookup(trade => SlotStart(trade.TimestampUtc, run[0]));
            foreach (var slot in run)
            {
                var trades = bySlot[slot].ToArray();
                if (trades.Length == 0) reasons[slot] = "no_trade_records_in_interval";
                else if (trades.Any(trade => !SipNonPriceSettingConditions.IsNonPriceSetting(trade))) reasons[slot] = "price_setting_trade_in_interval";
                else quiet.Add(slot);
            }
        }
        return new(quiet, gaps.Where(gap => !quiet.Contains(gap)).ToArray(), evidence, reasons);
    }

    /// <summary>The quiet intervals a decision at <paramref name="asOfUtc"/> may see: from <paramref name="fromUtc"/> up to the decision.</summary>
    public static IReadOnlySet<DateTimeOffset>? Window(IReadOnlySet<DateTimeOffset>? quiet, DateTimeOffset fromUtc, DateTimeOffset asOfUtc)
    {
        if (quiet is null) return null;
        var visible = quiet.Where(time => time >= fromUtc && time < asOfUtc).ToHashSet();
        return visible.Count > 0 ? visible : null;
    }

    // Groups sorted gap starts into runs of back-to-back intervals so each run needs one trade request.
    private static IEnumerable<DateTimeOffset[]> ConsecutiveRuns(IReadOnlyList<DateTimeOffset> gaps)
    {
        var current = new List<DateTimeOffset>();
        foreach (var gap in gaps)
        {
            if (current.Count > 0 && gap != current[^1].Add(Interval)) { yield return current.ToArray(); current.Clear(); }
            current.Add(gap);
        }
        if (current.Count > 0) yield return current.ToArray();
    }

    // The five-minute interval (aligned to the run start) that contains a trade time.
    private static DateTimeOffset SlotStart(DateTimeOffset time, DateTimeOffset runStart) =>
        runStart.AddTicks((time - runStart).Ticks / Interval.Ticks * Interval.Ticks);

    // Converts an exchange wall-clock time on a session date to UTC.
    private static DateTimeOffset AtUtc(DateOnly date, TimeOnly time, TimeZoneInfo zone) =>
        new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(time, DateTimeKind.Unspecified), zone), TimeSpan.Zero);
}

/// <summary>
/// Alpaca SIP trades for one gap run, archived page by page with the existing raw collector under the durable raw root.
/// A repeat run reads the same verified archive instead of requesting again. Every failure returns its reason.
/// </summary>
public sealed class AlpacaSipIntervalTradePrintSource(HttpClient client, string archiveRoot) : IIntervalTradePrintSource
{
    // A gap run with more trades than this many 10,000-row pages is too active to be quiet; stop instead of downloading it.
    private const int MaximumPages = 20;
    private static readonly DateTimeOffset ShareSizeArchiveStart = new(2025, 11, 3, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Collects (or re-reads) the archive for [start, end) and returns each trade's time, tape and conditions.</summary>
    public async Task<IntervalTradePrints?> GetTradesAsync(string ticker, DateTimeOffset startUtc, DateTimeOffset endUtc,
        DateOnly sessionDate, CancellationToken cancellationToken)
    {
        if (startUtc < ShareSizeArchiveStart) return IntervalTradePrints.Unavailable("before_2025_11_03_share_size_archive_contract");
        var symbol = ticker.Trim().ToUpperInvariant();
        var directory = Path.Combine(archiveRoot, symbol,
            startUtc.UtcDateTime.ToString("yyyyMMdd'T'HHmm", CultureInfo.InvariantCulture) + "-" +
            endUtc.UtcDateTime.ToString("yyyyMMdd'T'HHmm", CultureInfo.InvariantCulture));
        try
        {
            var request = new SipArchiveRequest(symbol, "trades", Rfc3339(startUtc), Rfc3339(endUtc),
                sessionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            var manifest = await new SipExecutionArchiveCollector(client).CollectAsync(request, directory,
                minimumFreeBytes: 1_000_000_000, maximumPages: MaximumPages, cancellationToken: cancellationToken);
            if (!manifest.PaginationComplete) return IntervalTradePrints.Unavailable("trade_pagination_incomplete");
            var trades = new List<SipTradePrint>();
            foreach (var page in manifest.Pages)
                trades.AddRange(ReadTrades(await SipExecutionArchiveCollector.ReadVerifiedPageAsync(directory, page, cancellationToken), symbol));
            var manifestSha = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(directory, "manifest.json"), cancellationToken)));
            return new(trades, $"{Path.GetRelativePath(archiveRoot, directory).Replace('\\', '/')}#manifest-sha256={manifestSha}");
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is HttpRequestException or
            InvalidDataException or ArgumentException or IOException or JsonException or TaskCanceledException or
            KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // The reason is recorded with the gap; request credentials travel in headers and never appear in it.
            return IntervalTradePrints.Unavailable($"trade_records_failed:{exception.GetType().Name}:{exception.Message}");
        }
    }

    // Reads {"trades":{"SYM":[{"t":..,"c":[..],"z":..}]}} from one verified raw page.
    private static IEnumerable<SipTradePrint> ReadTrades(byte[] payload, string symbol)
    {
        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("trades", out var bySymbol) || bySymbol.ValueKind != JsonValueKind.Object ||
            !bySymbol.TryGetProperty(symbol, out var rows) || rows.ValueKind != JsonValueKind.Array) return [];
        // Provider times carry nanoseconds; the shared parser keeps them exact before truncating to .NET's 100 ns ticks.
        return rows.EnumerateArray().Select(row => new SipTradePrint(
            DateTimeOffset.UnixEpoch.AddTicks(SipNanosecondTime.Parse(row.GetProperty("t").GetString()!) / 100),
            row.TryGetProperty("z", out var tape) && tape.ValueKind == JsonValueKind.String ? tape.GetString()! : "",
            row.TryGetProperty("c", out var codes) && codes.ValueKind == JsonValueKind.Array
                ? codes.EnumerateArray().Select(code => code.ValueKind == JsonValueKind.String ? code.GetString()! : "").ToArray() : [])).ToArray();
    }

    // Formats a UTC instant as the collector's RFC 3339 form.
    private static string Rfc3339(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
