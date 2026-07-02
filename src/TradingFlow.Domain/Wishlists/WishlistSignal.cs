namespace TradingFlow.Domain.Wishlists;

/// <summary>
/// A persisted monitor signal for a wishlist ticker. The snapshot is JSON so
/// evaluator details can evolve without requiring a migration for every field.
/// </summary>
public sealed class WishlistSignal
{
    public Guid Id { get; set; }

    public Guid WishlistId { get; set; }

    public string Ticker { get; set; } = String.Empty;

    public string SignalType { get; set; } = String.Empty;

    public string Severity { get; set; } = "info";

    public DateTimeOffset DetectedAtUtc { get; set; }

    public decimal Price { get; set; }

    public string Reason { get; set; } = String.Empty;

    public string SnapshotJson { get; set; } = "{}";

    public string? NewsHeadline { get; set; }

    public string? NewsUrl { get; set; }

    public string? NewsProvider { get; set; }

    public bool Acknowledged { get; set; }

    public Wishlist? Wishlist { get; set; }
}
