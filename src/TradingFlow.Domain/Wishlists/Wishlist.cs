namespace TradingFlow.Domain.Wishlists;

/// <summary>
/// A named, user-managed universe of tickers that can be monitored and traded.
/// Runtime configs reference wishlists instead of owning mutable ticker lists.
/// </summary>
public sealed class Wishlist
{
    public Guid Id { get; set; }

    public string Name { get; set; } = String.Empty;

    public string? Description { get; set; }

    public bool IsDefault { get; set; }

    public bool IncludeExtendedHours { get; set; } = true;

    public bool IsObserved { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public ICollection<WishlistItem> Items { get; set; } = new List<WishlistItem>();
}
