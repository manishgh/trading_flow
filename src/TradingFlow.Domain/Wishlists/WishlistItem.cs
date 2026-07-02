namespace TradingFlow.Domain.Wishlists;

/// <summary>
/// One active or archived ticker membership inside a wishlist.
/// </summary>
public sealed class WishlistItem
{
    public Guid Id { get; set; }

    public Guid WishlistId { get; set; }

    public string Ticker { get; set; } = String.Empty;

    public string? DisplayName { get; set; }

    public string? Notes { get; set; }

    public bool Active { get; set; } = true;

    public DateTimeOffset AddedAtUtc { get; set; }

    public Wishlist? Wishlist { get; set; }
}
