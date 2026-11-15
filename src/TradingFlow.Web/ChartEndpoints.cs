using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using TradingFlow.Contracts.V1;
using TradingFlow.Web.Services.Charts;

namespace TradingFlow.Web;

public static class ChartEndpoints
{
    // Browser WebSocket authentication also needs an exact origin check; CORS does not protect upgraded requests.
    public static bool HasSameOrigin(HttpRequest request) =>
        Uri.TryCreate(request.Headers.Origin.ToString(), UriKind.Absolute, out var origin)
        && origin.Scheme.Equals(request.Scheme, StringComparison.OrdinalIgnoreCase)
        && origin.Authority.Equals(request.Host.Value, StringComparison.OrdinalIgnoreCase)
        && origin.AbsolutePath == "/" && String.IsNullOrEmpty(origin.Query)
        && String.IsNullOrEmpty(origin.Fragment) && String.IsNullOrEmpty(origin.UserInfo);

    // Apply origin and credential-query checks before the SignalR upgrade, without weakening other API CSRF checks.
    public static void UseChartOriginGuard(this WebApplication app) => app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/api/v1/charts/hub") &&
            (!HasSameOrigin(context.Request) || context.Request.Query.ContainsKey("access_token")))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        await next(context);
    });

    // Only authenticated reads are exposed. The provider feed and trading paths are unaffected.
    public static void MapChartEndpoints(this WebApplication app)
    {
        app.MapHub<StockChartHub>("/api/v1/charts/hub", options =>
        {
            options.Transports = HttpTransportType.WebSockets;
            options.CloseOnAuthenticationExpiration = true;
            options.ApplicationMaxBufferSize = 64 * 1024;
            options.TransportMaxBufferSize = 64 * 1024;
        }).RequireAuthorization();
        app.MapGet("/api/v1/charts/history", async (HttpContext context, string symbol, string interval,
            string sessionView, string? chartContext, string? cursor, IChartSeriesReader series, ChartAccessPolicy access,
            ChartTransportLimits limits, ChartHistoryCursor cursors, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            try
            {
                var user = ChartAccessPolicy.UserId(context.User);
                if (!limits.Request(user)) return Results.StatusCode(429);
                var key = ChartSeriesService.Normalize(new ChartSeriesKey(symbol, interval, sessionView, chartContext ?? "auto"));
                if (!await access.CanReadAsync(context.User, key.Symbol, token)) return Results.Forbid();
                var before = cursor is null ? (DateTimeOffset?)null : cursors.Read(cursor, user, key);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(25));
                var page = await series.ReadAsync(key, before, deadline.Token);
                if (!await access.CanReadAsync(context.User, key.Symbol, deadline.Token)) return Results.Forbid();
                var snapshot = page.Snapshot with { HistoryCursor = cursors.Create(user, key, page.NextBeforeUtc) };
                var json = JsonSerializer.SerializeToUtf8Bytes(snapshot, StockChartHub.Json);
                return json.Length <= StockChartHub.MaximumSnapshotBytes
                    ? Results.Bytes(json, "application/json")
                    : Results.Problem("Chart response exceeds the bounded view. Choose a smaller interval.", statusCode: 413);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { message = ex.Message }); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { return Results.Problem("Chart history timed out. Retry the selected stock.", statusCode: 503); }
        }).RequireAuthorization();
    }
}
