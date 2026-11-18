using System.Text.Json;
using TradingFlow.Domain.Discovery;
using TradingFlow.Contracts.V1;
using TradingFlow.Web.Services.Desk;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Web;

public static class DeskEndpoints
{
    /// <summary>Maps authenticated Desk observations and read-only recorded strategy evidence for Web and Android.</summary>
    public static void MapDeskEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/desk").RequireAuthorization();
        group.MapGet("/sources", async (DeskSources sources, CancellationToken ct) => Results.Ok(await sources.CatalogAsync(ct)));
        group.MapPost("/screens", async (ImportScreen request, DeskSources sources, ScreenerPresetService presets, CancellationToken ct) => {
            try
            {
                if (String.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 120) return Results.BadRequest(new { message = "Name must contain 1–120 characters." });
                var query = DeskSources.ImportFilter(request.Url ?? "");
                var saved = await presets.SaveAsync(request.Name, ScreenerScope.Swing, query, ct);
                return Results.Ok(new { key = "saved:" + saved.Id });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { message = ex.Message }); }
        });
        group.MapPost("/follow", async (FollowStock request, DeskSources sources, CancellationToken ct) => {
            try {
                await sources.FollowAsync(request.Source, request.Symbol, request.WishlistId, ct);
                return Results.Ok(new { followed = true });
            } catch (ArgumentException ex) { return Results.BadRequest(new { message = ex.Message }); }
        });
        // Adds any symbol to one of the operator's stock groups after Alpaca confirms an active, tradable US stock.
        group.MapPost("/stocks", async (AddStock request, DeskSources sources, CancellationToken ct) => {
            var result = await sources.AddStockAsync(request.Symbol, request.WishlistId, ct);
            var body = new { status = result.Status.ToString(), symbol = result.Symbol, message = result.Message };
            return result.Status switch
            {
                DeskStockAddStatus.Refused => Results.BadRequest(body),
                DeskStockAddStatus.Unavailable => Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable),
                _ => Results.Ok(body)
            };
        });
        group.MapGet("/stream", StreamAsync);
        app.MapGroup("/api/v1/desk").RequireAuthorization().MapStockStrategyEndpoints();
        // Display quotes for the visible stocks travel on their own socket, so a slow snapshot cannot delay them.
        app.MapHub<DeskQuoteHub>(DeskQuoteHub.Path, options =>
        {
            options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;
            options.CloseOnAuthenticationExpiration = true;
            options.ApplicationMaxBufferSize = 4 * 1024;
            options.TransportMaxBufferSize = 64 * 1024;
        }).RequireAuthorization();
    }
    public sealed record ImportScreen(string Name, string Url);
    public sealed record FollowStock(string Source, string Symbol, Guid WishlistId);
    public sealed record AddStock(string? Symbol, Guid WishlistId);

    /// <summary>
    /// Called by both sign-out paths before the cookie is cleared: ends the user's Desk live-quote views. Hosts that do
    /// not serve the Desk have no registry and nothing to revoke.
    /// </summary>
    public static Task RevokeQuoteViewsOnSignOutAsync(HttpContext context) =>
        context.User.Identity?.IsAuthenticated == true && context.RequestServices.GetService<DeskQuoteViews>() is { } views
            ? views.RevokeUserAsync(TradingFlow.Web.Services.Charts.ChartAccessPolicy.UserId(context.User))
            : Task.CompletedTask;

    /// <summary>
    /// Streams bounded observation pages and the selected run's recorded strategy checks; never starts an evaluation.
    /// Each cycle first renews this stream's quote view from the freshly resolved stocks, then reads the full snapshot.
    /// </summary>
    private static async Task StreamAsync(HttpContext context, DeskSources sources, DeskReadService reader,
        DeskStrategyAssessmentService assessments, DeskQuoteViews quoteViews,
        ILoggerFactory logs, TradingFlow.Web.Services.Charts.ChartAccessPolicy charts,
        TradingFlow.Application.Preparation.PreparationService preparation)
    {
        var key = context.Request.Query["source"].FirstOrDefault() ?? "all";
        var search = context.Request.Query["search"].ToString();
        if (search.Length > 80) search = search[..80];
        var page = Int32.TryParse(context.Request.Query["page"], out var requestedPage) ? Math.Max(0, requestedPage) : 0;
        var strategyId = context.Request.Query["strategyId"].ToString();
        var strategyVersion = context.Request.Query["semanticVersion"].ToString();
        var strategyHash = context.Request.Query["contentSha256"].ToString();
        var requestedRun = context.Request.Query["runId"].ToString();
        Guid? runId = null;
        if (!String.IsNullOrEmpty(requestedRun))
        {
            if (!Guid.TryParse(requestedRun, out var parsedRun))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            runId = parsedRun;
        }
        var strategy = String.IsNullOrEmpty(strategyId) ? null : new StrategyReference(strategyId, strategyVersion, strategyHash);
        var ct = context.RequestAborted;
        TradingFlow.Contracts.Preparation.PreparationHandoff? prepared = null;
        if (key == "prepared")
        {
            var reviewKey = context.Request.Query["preparationKey"].ToString();
            var owner = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (String.IsNullOrWhiteSpace(owner) || reviewKey.Length is < 1 or > 100)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            try { prepared = await preparation.ReadHandoffAsync(owner, reviewKey, ct); }
            catch (KeyNotFoundException)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
        }
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        // The page's stocks join the one shared Alpaca SIP connection while this view is open.
        var quoteView = Guid.NewGuid();
        var user = TradingFlow.Web.Services.Charts.ChartAccessPolicy.UserId(context.User);
        string? quoteReason = null;
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        try
        {
            // A full snapshot about every 5 seconds; live quotes travel separately on the Desk quote socket.
            // Reconnect bounds lifetime and refreshes authentication.
            for (var i = 0; i < 60 && !ct.IsCancellationRequested; i++)
            {
                var selection = prepared is null ? await sources.ResolveAsync(key, ct, page, search) :
                    await sources.ResolvePreparedAsync(prepared, ct, page, search);
                var offer = await quoteViews.RenewAsync(quoteView, user,
                    selection.Members.Select(member => member.Symbol), ct);
                // A signed-out view ends this stream; the browser's reconnect then needs a valid sign-in.
                if (offer.Closed) break;
                if (offer.Capability is not null || offer.Reason != quoteReason)
                {
                    // Sent only in this no-store event body, never in a URL or log; the socket consumes it once.
                    quoteReason = offer.Reason;
                    await context.Response.WriteAsync("event: view\ndata: " + JsonSerializer.Serialize(new
                    {
                        view = offer.ViewId, capability = offer.Capability, reason = offer.Reason, refused = offer.Refused
                    }, json) + "\n\n", ct);
                    await context.Response.Body.FlushAsync(ct);
                }
                var snapshot = await reader.ReadAsync(selection, ct);
                if (strategy is not null && runId is not null && snapshot.Rows.Count > 0)
                {
                    var recorded = await assessments.ReadAsync(runId, strategy,
                        snapshot.Rows.Select(item => item.Membership.Symbol).ToArray(), ct);
                    var bySymbol = recorded.Stocks.ToDictionary(item => item.Symbol, StringComparer.Ordinal);
                    snapshot = snapshot with { Rows = snapshot.Rows.Select(row => row with {
                        StrategyAssessment = bySymbol.GetValueOrDefault(row.Membership.Symbol) }).ToArray() };
                }
                charts.RecordDisplayed(context.User, snapshot.Rows.Select(row => row.Membership.Symbol).ToArray());
                // Snapshot quotes come from the shared quote service, which already prefers a newer streamed quote.
                await context.Response.WriteAsync("event: snapshot\ndata: " + JsonSerializer.Serialize(snapshot, json) + "\n\n", ct);
                await context.Response.Body.FlushAsync(ct);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logs.CreateLogger("DeskStream").LogWarning(ex, "Desk observation stream failed.");
            if (!ct.IsCancellationRequested)
            {
                await context.Response.WriteAsync("event: unavailable\ndata: {}\n\n", ct);
                await context.Response.Body.FlushAsync(ct);
            }
        }
        finally
        {
            // Source/page/search replacement, reconnect and tab close all end this stream, and with it the quote view.
            await quoteViews.RevokeAsync(quoteView);
        }
    }
}
