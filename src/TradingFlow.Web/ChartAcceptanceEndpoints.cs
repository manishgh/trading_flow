using System.Net;
using TradingFlow.Data.Identity;
using TradingFlow.Web.Services.Charts;

namespace TradingFlow.Web;

internal static class ChartAcceptanceEndpoints
{
    private const string Route = "/api/operations/chart-acceptance";

    // Reuse Identity and the existing global /api antiforgery middleware for the administrator mutations.
    public static void MapChartAcceptance(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<ChartAcceptanceOptions>().Enabled) return;
        var routes = app.MapGroup(Route).RequireAuthorization(policy => policy.RequireRole(TradingFlowRoles.Administrator));
        // Keep responses and the one-use capability local and outside caches.
        routes.AddEndpointFilter(async (invocation, next) =>
        {
            var context = invocation.HttpContext;
            context.Response.Headers.CacheControl = "private, no-store";
            return IsLocalRequest(context) ? await next(invocation) : Results.NotFound();
        });
        routes.MapGet("", Status);
        routes.MapPost("/start", StartAsync);
        routes.MapPost("/stop", Stop);
    }

    // Reject remote, forwarded-host and explicitly cross-origin browser traffic.
    internal static bool IsLocalRequest(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote) ||
            !Uri.TryCreate($"{context.Request.Scheme}://{context.Request.Host}", UriKind.Absolute, out var request) ||
            !request.IsLoopback || context.Request.Headers.Keys.Any(name =>
                name.Equals("Forwarded", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("X-Original-", StringComparison.OrdinalIgnoreCase))) return false;
        if (context.Request.Headers.Origin.Count > 0 && !ChartEndpoints.HasSameOrigin(context.Request)) return false;
        var fetchSite = context.Request.Headers["Sec-Fetch-Site"];
        return fetchSite.Count == 0 || (fetchSite.Count == 1 && fetchSite[0] is "same-origin" or "none");
    }

    // Return only the invoking administrator's own session and no correlation capability.
    private static IResult Status(HttpContext context, ChartAcceptanceCoordinator coordinator) =>
        Results.Ok(coordinator.GetStatus(ChartAccessPolicy.UserId(context.User)));

    // Start accepts no request-controlled symbol, duration, account, path or source.
    private static async Task<IResult> StartAsync(HttpContext context, ChartAcceptanceCoordinator coordinator,
        ChartAccessPolicy access, CancellationToken token)
    {
        var result = await coordinator.StartAsync(ChartAccessPolicy.UserId(context.User), token);
        if (result.Start is null)
            return Results.Problem(result.Denial, statusCode: StatusCodes.Status409Conflict);
        access.RecordDisplayed(context.User, [ChartAcceptanceSession.Symbol], ChartAcceptanceSession.Lifetime);
        return Results.Ok(result.Start);
    }

    // Cancel only the caller's chart check and return its current state even after repeated stops.
    private static IResult Stop(HttpContext context, ChartAcceptanceCoordinator coordinator) =>
        Results.Ok(coordinator.Stop(ChartAccessPolicy.UserId(context.User)));
}
