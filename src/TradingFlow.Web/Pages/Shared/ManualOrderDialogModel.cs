namespace TradingFlow.Web.Pages.Shared;

public sealed record ManualOrderDialogModel(
    Guid? WishlistId,
    string? Source,
    string? StrategyPath);
