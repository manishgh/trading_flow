using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using TradingFlow.Data.Identity;

namespace TradingFlow.Web;

public static class AuthApiEndpoints
{
    public static IEndpointRouteBuilder MapTradingFlowAuthApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/auth");

        group.MapPost("/login", [AllowAnonymous] async (
            LoginRequest request,
            SignInManager<TradingFlowUser> signInManager) =>
        {
            if (String.IsNullOrWhiteSpace(request.UserName) || String.IsNullOrEmpty(request.Password))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["credentials"] = ["Username and password are required."]
                });
            }

            var result = await signInManager.PasswordSignInAsync(
                request.UserName.Trim(),
                request.Password,
                request.RememberMe,
                lockoutOnFailure: true);
            return result.Succeeded
                ? Results.Ok(new { userName = request.UserName.Trim() })
                : Results.Unauthorized();
        });

        group.MapGet("/me", (HttpContext context) => Results.Ok(new
        {
            userName = context.User.Identity?.Name,
            isAdministrator = context.User.IsInRole(TradingFlowRoles.Administrator)
        }));

        group.MapPost("/logout", async (SignInManager<TradingFlowUser> signInManager) =>
        {
            await signInManager.SignOutAsync();
            return Results.NoContent();
        });

        return endpoints;
    }

    public sealed record LoginRequest(string UserName, string Password, bool RememberMe);
}
