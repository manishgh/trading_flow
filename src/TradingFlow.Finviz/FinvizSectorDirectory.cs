namespace TradingFlow.Finviz;

/// <summary>
/// Today's Finviz sector for every listed stock, read from one all-stocks screener export and reused
/// for a day. It labels and filters screens; it is current classification, not point-in-time evidence.
/// </summary>
public sealed class FinvizSectorDirectory(FinvizClient finviz, TimeProvider clock)
{
    // The default overview export lists every stock with its sector.
    internal const string AllStocksQuery = "v=111";
    private static readonly TimeSpan MaximumAge = TimeSpan.FromDays(1);
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private IReadOnlyDictionary<string, string> sectors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? refreshedAtUtc;

    public string? Find(string ticker) => Volatile.Read(ref sectors).GetValueOrDefault(ticker);

    /// <summary>Re-reads the export once the copy is a day old; a failed read keeps the previous copy.</summary>
    public async Task RefreshIfStaleAsync(CancellationToken cancellationToken)
    {
        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (refreshedAtUtc is { } last && clock.GetUtcNow() - last < MaximumAge) return;
            var rows = await finviz.GetScreenerRowsAsync(AllStocksQuery, cancellationToken);
            var current = rows.Where(row => row.Sector is not null)
                .GroupBy(row => row.Ticker, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Sector!, StringComparer.OrdinalIgnoreCase);
            if (current.Count == 0) throw new InvalidDataException("The Finviz all-stocks export returned no sectors.");
            Volatile.Write(ref sectors, current);
            refreshedAtUtc = clock.GetUtcNow();
        }
        finally
        {
            refreshLock.Release();
        }
    }
}
