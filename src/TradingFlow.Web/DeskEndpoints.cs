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
        group.MapGet("/stream", StreamAsync);
        app.MapGroup("/api/v1/desk").RequireAuthorization().MapStockStrategyEndpoints();
    }
    public sealed record ImportScreen(string Name, string Url);
    public sealed record FollowStock(string Source, string Symbol, Guid WishlistId);

    /// <summary>Streams bounded observation pages and the selected run's recorded strategy checks; never starts an evaluation.</summary>
    private static async Task StreamAsync(HttpContext context, DeskSources sources, DeskReadService reader,
        DeskStrategyAssessmentService assessments, TradingFlow.Web.Services.IDisplayQuoteSubscriptions subscriptions, TradingFlow.Web.Services.LiveQuoteBook quoteBook,
        ILoggerFactory logs)
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
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        var ct = context.RequestAborted;
        // The page's stocks join the one shared Alpaca SIP connection while this view is open.
        var quoteScope = Guid.NewGuid();
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        try
        {
            // A full snapshot every 5 seconds; streamed quotes in between, once a second.
            // Reconnect bounds lifetime and refreshes authentication.
            for (var i = 0; i < 60 && !ct.IsCancellationRequested; i++)
            {
                var selection = await sources.ResolveAsync(key, ct, page, search);
                var snapshot = await reader.ReadAsync(selection, ct);
                if (strategy is not null && runId is not null && snapshot.Rows.Count > 0)
                {
                    var recorded = await assessments.ReadAsync(runId, strategy,
                        snapshot.Rows.Select(item => item.Membership.Symbol).ToArray(), ct);
                    var bySymbol = recorded.Stocks.ToDictionary(item => item.Symbol, StringComparer.Ordinal);
                    snapshot = snapshot with { Rows = snapshot.Rows.Select(row => row with {
                        StrategyAssessment = bySymbol.GetValueOrDefault(row.Membership.Symbol) }).ToArray() };
                }
                var symbols = snapshot.Rows.Select(row => row.Membership.Symbol).ToArray();
                await subscriptions.ReplaceDisplayQuotesAsync(quoteScope, symbols, ct);
                snapshot = snapshot with { Rows = snapshot.Rows.Select(row => row with {
                    Quote = quoteBook.Newest(row.Membership.Symbol, row.Quote) }).ToArray() };
                await context.Response.WriteAsync("event: snapshot\ndata: " + JsonSerializer.Serialize(snapshot, json) + "\n\n", ct);
                await context.Response.Body.FlushAsync(ct);
                var sent = snapshot.Rows.ToDictionary(row => row.Membership.Symbol, row => row.Quote?.Timestamp, StringComparer.OrdinalIgnoreCase);
                for (var tick = 0; tick < 5; tick++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    var changed = symbols.Select(quoteBook.Find)
                        .Where(quote => quote is not null && (sent.GetValueOrDefault(quote.Ticker) is not { } last || quote.Timestamp > last))
                        .Select(quote => quote!).ToArray();
                    if (changed.Length == 0) continue;
                    foreach (var quote in changed) sent[quote.Ticker] = quote.Timestamp;
                    await context.Response.WriteAsync("event: quotes\ndata: " + JsonSerializer.Serialize(changed.Select(quote => new
                    {
                        symbol = quote.Ticker, quote.BidPrice, quote.AskPrice, quote.MidPrice, quote.BidSize, quote.AskSize, quote.Timestamp
                    }), json) + "\n\n", ct);
                    await context.Response.Body.FlushAsync(ct);
                }
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
            await subscriptions.RemoveDisplayQuotesAsync(quoteScope, CancellationToken.None);
        }
    }
}
