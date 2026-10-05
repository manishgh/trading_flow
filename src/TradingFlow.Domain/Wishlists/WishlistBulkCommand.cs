namespace TradingFlow.Domain.Wishlists;

public sealed record WishlistBulkRequest(Guid CommandId, string Operation, Guid? SourceId,
    Guid? TargetId, IReadOnlyList<string> Tickers, string? Tag = null);
public sealed record WishlistBulkResult(Guid CommandId, int Affected, bool Undone);
public sealed record WishlistCreateRequest(Guid CommandId, string Name);
public sealed record WishlistCreateResult(Guid Id, string Name, WishlistBulkResult Receipt);

/// <summary>Durable receipt and exact before/after state for one operator's reversible curation command.</summary>
public sealed class WishlistBulkCommand
{
    public Guid Id { get; set; }
    public string OperatorId { get; set; } = "";
    public string RequestJson { get; set; } = "";
    public string BeforeJson { get; set; } = "";
    public string AfterJson { get; set; } = "";
    public int Affected { get; set; }
    public bool Undone { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
