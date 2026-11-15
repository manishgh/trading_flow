using System.Security.Claims;
using TradingFlow.Data.Identity;
using TradingFlow.Domain.Execution;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Web;

public static class AccountLossReviewEndpoints
{
    // Reuse authenticated API antiforgery; administrator identity comes from claims, never JSON.
    public static void MapAccountLossReviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/operations/account-loss")
            .RequireAuthorization(policy => policy.RequireRole(TradingFlowRoles.Administrator));
        group.MapGet("/", async (HttpContext context, IAccountLossReviewService service, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            return await ExecuteAsync(() => service.GetAsync(token));
        });
        group.MapPost("/review", async (AccountLossReviewRequest request, HttpContext context,
            IAccountLossReviewService service, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            return string.IsNullOrWhiteSpace(actor) ? Results.Forbid() :
                await ExecuteAsync(() => service.ReviewAsync(request, actor, token));
        });
    }

    // Refused reviews leave the durable gate intact and require the operator to inspect newer facts.
    private static async Task<IResult> ExecuteAsync(Func<Task<AccountLossReviewSnapshot>> action)
    {
        try { return Results.Ok(await action()); }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
        { return Results.Conflict(new { error = "account_loss_review_refused", message = exception.Message }); }
    }
}
