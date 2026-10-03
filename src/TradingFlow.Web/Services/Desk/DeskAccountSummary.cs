using TradingFlow.Web.Services.Wishlists;
namespace TradingFlow.Web.Services.Desk;
public static class DeskAccountSummary
{
    public static DeskPortfolioSummary? Summarize(DeskAccount account)
    {
        var held = account.Holdings.Where(x => x.Quantity != 0m).ToArray();
        return account.Available && held.All(x => x.CostBasis is not null && x.MarketValue is not null && x.UnrealizedPl is not null)
            ? new(held.Length, held.Sum(x => x.CostBasis!.Value), held.Sum(x => x.MarketValue!.Value), held.Sum(x => x.UnrealizedPl!.Value)) : null;
    }
}
