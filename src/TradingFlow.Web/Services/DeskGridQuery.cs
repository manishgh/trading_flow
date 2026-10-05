namespace TradingFlow.Web.Services;

public sealed record DeskGridQuery(string? Search, decimal? MinPrice, decimal? MaxPrice,
    decimal? MaxSpreadBps, decimal? MinChange, decimal? MaxChange,
    decimal? MinVolumeZ20, decimal? MaxVolumeZ20, string? Tracked);

public sealed record DeskGridValues(string Ticker, string Name, decimal? Price, decimal? SpreadBps,
    decimal? ChangePct, decimal? VolumeZ20, bool HasTrackedTrade);

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
    }

    public static bool Matches(DeskGridValues row, DeskGridQuery query) =>
        (String.IsNullOrWhiteSpace(query.Search) || row.Ticker.Contains(query.Search.Trim(), StringComparison.OrdinalIgnoreCase) ||
            row.Name.Contains(query.Search.Trim(), StringComparison.OrdinalIgnoreCase)) &&
        Range(row.Price, query.MinPrice, query.MaxPrice) && Range(row.SpreadBps, null, query.MaxSpreadBps) &&
        Range(row.ChangePct, query.MinChange, query.MaxChange) && Range(row.VolumeZ20, query.MinVolumeZ20, query.MaxVolumeZ20) &&
        (query.Tracked != "tracked" || row.HasTrackedTrade) && (query.Tracked != "untracked" || !row.HasTrackedTrade);

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
