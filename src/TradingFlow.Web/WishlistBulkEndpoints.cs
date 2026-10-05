using System.Security.Claims;
using TradingFlow.Domain.Wishlists;

namespace TradingFlow.Web;

public static class WishlistBulkEndpoints
{
    public static RouteGroupBuilder MapWishlistBulkEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/wishlists/mutable", async (IWishlistRepository repository, CancellationToken token) => {
            var groups = new List<object>();
            foreach (var wishlist in await repository.ListAsync(token))
                if (!await repository.IsPreparationRevisionAsync(wishlist.Id, token))
                    groups.Add(new { wishlist.Id, wishlist.Name });
            return Results.Ok(groups);
        });
        group.MapPost("/wishlists/bulk", async (WishlistBulkRequest request, ClaimsPrincipal user,
            IWishlistRepository repository, CancellationToken token) => {
            try { return Results.Ok(await repository.ApplyBulkAsync(OperatorId(user), request, token)); }
            catch (InvalidOperationException error) { return Results.Conflict(new { error = error.Message }); }
        });
        group.MapPost("/wishlists/bulk/{id:guid}/undo", async (Guid id, ClaimsPrincipal user,
            IWishlistRepository repository, CancellationToken token) => {
            try { return Results.Ok(await repository.UndoBulkAsync(OperatorId(user), id, token)); }
            catch (InvalidOperationException error) { return Results.Conflict(new { error = error.Message }); }
        });
        return group;
    }
    private static string OperatorId(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new UnauthorizedAccessException();
}
