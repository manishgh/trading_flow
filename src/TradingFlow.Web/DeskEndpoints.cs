using System.Text.Json;
using TradingFlow.Domain.Discovery;
using TradingFlow.Web.Services.Desk;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Web;

public static class DeskEndpoints
{
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
        group.MapGet("/stream", StreamAsync);
    }
    public sealed record ImportScreen(string Name, string Url);
    public sealed record FollowStock(string Source, string Symbol, Guid WishlistId);

    private static async Task StreamAsync(HttpContext context, DeskSources sources, DeskReadService reader,
        ILoggerFactory logs)
    {
        var key = context.Request.Query["source"].FirstOrDefault() ?? "all";
        var search = context.Request.Query["search"].ToString();
        if (search.Length > 80) search = search[..80];
        var page = Int32.TryParse(context.Request.Query["page"], out var requestedPage) ? Math.Max(0, requestedPage) : 0;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        var ct = context.RequestAborted;
        try
        {
            // Read-only REST observations do not subscribe the execution candle stream.
            // Reconnect bounds lifetime and refreshes authentication.
            for (var i = 0; i < 60 && !ct.IsCancellationRequested; i++)
            {
                var selection = await sources.ResolveAsync(key, ct, page, search);
                var snapshot = await reader.ReadAsync(selection, ct);
                await context.Response.WriteAsync("event: snapshot\ndata: " + JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + "\n\n", ct);
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

    }
}
