namespace TradingFlow.Web.Services;

public sealed record DeskGridQuery(string? Search, decimal? MinPrice, decimal? MaxPrice,
    decimal? MaxSpreadBps, decimal? MinChange, decimal? MaxChange,
    decimal? MinVolumeZ20, decimal? MaxVolumeZ20, string? Tracked,
    string? Sector = null, decimal? MinMarketCap = null, decimal? MaxMarketCap = null,
    decimal? MinVolume = null, decimal? MinRvol = null, decimal? MaxRvol = null,
    decimal? MinAtrPercent = null, decimal? MaxAtrPercent = null,
    decimal? MinGap = null, decimal? MaxGap = null, decimal? MinVwapDistance = null, decimal? MaxVwapDistance = null,
    decimal? MinRsi = null, decimal? MaxRsi = null, int? MaxEarningsDays = null,
    bool? CatalystOnly = null, string? SignalState = null);

public sealed record DeskGridValues(string Ticker, string Name, decimal? Price, decimal? SpreadBps,
    decimal? ChangePct, decimal? VolumeZ20, bool HasTrackedTrade,
    DeskUniverseEvidence? Evidence = null, bool HasSignal = false);

/// <summary>Display filters only; never changes candidate admission or trading priority.</summary>
public static class DeskGridFilter
{
    public static void Validate(DeskGridQuery query)
    {
        if (query.MinPrice < 0 || query.MaxPrice < 0 || query.MaxSpreadBps < 0 ||
            query.MinPrice > query.MaxPrice || query.MinChange > query.MaxChange || query.MinVolumeZ20 > query.MaxVolumeZ20)
            throw new InvalidOperationException("Minimum filters must not exceed maximum filters; price and spread cannot be negative.");
        if (query.Tracked is not (null or "" or "all" or "tracked" or "untracked"))
            throw new InvalidOperationException("Choose all, tracked or untracked positions.");
        if (query.Search?.Length > 200) throw new InvalidOperationException("Search must be at most 200 characters.");
        foreach (var (min,max) in new[] { (query.MinMarketCap,query.MaxMarketCap),(query.MinRvol,query.MaxRvol),
            (query.MinAtrPercent,query.MaxAtrPercent),(query.MinGap,query.MaxGap),(query.MinVwapDistance,query.MaxVwapDistance),(query.MinRsi,query.MaxRsi) })
            if (min > max) throw new InvalidOperationException("Minimum filters must not exceed maximum filters.");
        if (query.MinMarketCap < 0 || query.MaxMarketCap < 0 || query.MinVolume < 0 || query.MinRvol < 0 || query.MaxRvol < 0 ||
            query.MinAtrPercent < 0 || query.MaxAtrPercent < 0 || query.MinRsi < 0 || query.MinRsi > 100 || query.MaxRsi < 0 || query.MaxRsi > 100 ||
            query.MaxEarningsDays < 0 || query.MaxEarningsDays > 35 || query.Sector?.Length > 120 || query.SignalState is not (null or "" or "all" or "signal" or "none") || query.CatalystOnly == false)
            throw new InvalidOperationException("Choose valid nonnegative volume/cap/RVOL/ATR values, RSI 0–100, earnings proximity 0–35 days and supported signal/catalyst filters.");
    }

    public static bool Matches(DeskGridValues row, DeskGridQuery query) =>
        (String.IsNullOrWhiteSpace(query.Search) || row.Ticker.Contains(query.Search.Trim(), StringComparison.OrdinalIgnoreCase) ||
            row.Name.Contains(query.Search.Trim(), StringComparison.OrdinalIgnoreCase)) &&
        Range(row.Price, query.MinPrice, query.MaxPrice) && Range(row.SpreadBps, null, query.MaxSpreadBps) &&
        Range(row.ChangePct, query.MinChange, query.MaxChange) && Range(row.VolumeZ20, query.MinVolumeZ20, query.MaxVolumeZ20) &&
        (query.Tracked != "tracked" || row.HasTrackedTrade) && (query.Tracked != "untracked" || !row.HasTrackedTrade) &&
        (String.IsNullOrWhiteSpace(query.Sector) || String.Equals(row.Evidence?.Sector,query.Sector.Trim(),StringComparison.OrdinalIgnoreCase)) &&
        Range(row.Evidence?.MarketCapMillions,query.MinMarketCap,query.MaxMarketCap) && Range(row.Evidence?.Volume,query.MinVolume,null) &&
        Range(row.Evidence?.DailyRvol,query.MinRvol,query.MaxRvol) && Range(row.Evidence?.AtrPercent,query.MinAtrPercent,query.MaxAtrPercent) &&
        Range(row.Evidence?.GapPercent,query.MinGap,query.MaxGap) && Range(row.Evidence?.VwapDistancePercent,query.MinVwapDistance,query.MaxVwapDistance) &&
        Range(row.Evidence?.Rsi,query.MinRsi,query.MaxRsi) && Range(row.Evidence?.EarningsInDays,null,query.MaxEarningsDays) &&
        (query.CatalystOnly != true || row.Evidence?.HasCatalyst == true) &&
        (query.SignalState != "signal" || row.HasSignal) && (query.SignalState != "none" || !row.HasSignal);

    private static bool Range(decimal? value, decimal? min, decimal? max) => min is null && max is null ||
        value is { } known && (min is null || known >= min) && (max is null || known <= max);
}

public sealed record DeskGridPage<T>(IReadOnlyList<T> Rows, int TotalMatching, int Number, int Size, int PageCount)
{
    // Caller supplies already filtered, deterministically sorted rows. At most 100 rows reach the DOM.
    public static DeskGridPage<T> Create(IEnumerable<T> sortedRows, int page, int size)
    {
        size = Math.Clamp(size, 10, 100);
        var rows = sortedRows.ToArray();
        var count = Math.Max(1, (int)Math.Ceiling(rows.Length / (double)size));
        page = Math.Clamp(page, 1, count);
        return new(rows.Skip((page - 1) * size).Take(size).ToArray(), rows.Length, page, size, count);
    }
}
