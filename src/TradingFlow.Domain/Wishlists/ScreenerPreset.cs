namespace TradingFlow.Domain.Wishlists;

/// <summary>
/// A user-managed Finviz screener preset that has been verified against ML baseline performance.
/// </summary>
public sealed class ScreenerPreset
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string FilterQuery { get; set; } = string.Empty;

    public bool IsVerified { get; set; }

    public decimal? AverageMlScore { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
