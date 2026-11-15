using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using TradingFlow.Web.Services.Charts;

namespace TradingFlow.Web;

/// <summary>Page-issued chart capabilities and a separate, authenticated, read-only evidence endpoint.</summary>
internal static class PrivatePaperChartEndpoints
{
    private const string ReadPath = "/api/v1/charts/paper-evidence";
    private const string Unavailable = "Paper chart evidence is unavailable.";

    // Bind reloadable, empty-by-default grants without adding account credentials or a mutation service.
    public static void AddPrivatePaperChart(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PrivatePaperChartOptions>().Bind(configuration.GetSection(PrivatePaperChartOptions.Section));
        services.AddSingleton<PrivatePaperChartAccess>();
        services.AddSingleton<PrivatePaperChartTokens>();
        services.AddSingleton<PrivatePaperChartJournal>();
    }

    // Cover challenges and validation failures as well as successful private responses.
    public static void UsePrivatePaperChartNoStore(this WebApplication app) => app.Use(async (context, next) =>
    {
        if (context.Request.Path.Equals(ReadPath, StringComparison.OrdinalIgnoreCase))
            context.Response.Headers.CacheControl = "no-store";
        await next(context);
    });

    // Keep the token out of the URL. The standard API antiforgery middleware protects this read-only POST.
    public static void MapPrivatePaperChart(this WebApplication app) =>
        app.MapPost(ReadPath, ReadAsync).RequireAuthorization();

    // Show selectors only to a current administrator with a grant for this page's account and optional job.
    public static Task<bool> ShowSelectorsAsync(HttpContext http, Guid? jobId, CancellationToken token) =>
        http.RequestServices.GetRequiredService<PrivatePaperChartAccess>().CanSelectAsync(http.User, jobId, token);

    // Only a Paper or Orders Razor handler calls this resolver, independently of its ordinary full-page data loads.
    public static async Task<IActionResult> IssueAsync(HttpContext http, Guid? jobId, string? clientOrderId,
        string? symbol, bool paper, CancellationToken token)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!paper || symbol is null || http.Request.Headers["X-TradingFlow-Chart"] != "1" ||
            (http.Request.Headers.TryGetValue("Sec-Fetch-Site", out var site) && site != "same-origin") ||
            (http.Request.Headers.ContainsKey("Origin") && !ChartEndpoints.HasSameOrigin(http.Request)))
            return new NotFoundObjectResult(new { message = Unavailable });

        var services = http.RequestServices;
        var user = PrivatePaperChartAccess.OperatorId(http.User);
        if (user == Guid.Empty) return new NotFoundObjectResult(new { message = Unavailable });
        if (!services.GetRequiredService<ChartTransportLimits>().Request(user.ToString("D")))
            return new StatusCodeResult(StatusCodes.Status429TooManyRequests);
        var access = services.GetRequiredService<PrivatePaperChartAccess>();
        var resolved = await access.SelectAsync(http.User, jobId, clientOrderId, symbol, token);
        if (resolved is null || !await access.CanReadAsync(resolved.Scope, token))
            return new NotFoundObjectResult(new { message = Unavailable });

        services.GetRequiredService<ChartAccessPolicy>().RecordDisplayed(http.User, [resolved.Scope.Symbol]);
        var selection = services.GetRequiredService<PrivatePaperChartTokens>().Issue(resolved);
        return new JsonResult(selection);
    }

    private sealed record ReadRequest(string? Token, string? Symbol, DateTimeOffset FromUtc, DateTimeOffset ToUtc,
        string? Continuation);

    // Check identity and bounds before SQLite, then recheck the exact grant after projecting the read snapshot.
    private static async Task<IResult> ReadAsync(HttpContext http, ReadRequest request, PrivatePaperChartAccess access,
        PrivatePaperChartTokens tokens, PrivatePaperChartJournal journal, ChartTransportLimits limits, TimeProvider clock,
        ILogger<PrivatePaperChartJournal> logger, CancellationToken token)
    {
        if (!ChartEndpoints.HasSameOrigin(http.Request)) return Denied();
        var user = PrivatePaperChartAccess.OperatorId(http.User);
        var symbol = PrivatePaperChartAccess.CanonicalSymbol(request.Symbol);
        if (user == Guid.Empty || symbol is null || symbol != request.Symbol) return Denied();
        if (!limits.Request(user.ToString("D"))) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        var now = clock.GetUtcNow();
        if (request.FromUtc.Offset != TimeSpan.Zero || request.ToUtc.Offset != TimeSpan.Zero ||
            request.ToUtc > now || request.ToUtc <= request.FromUtc || request.ToUtc - request.FromUtc > TimeSpan.FromDays(31))
            return Results.BadRequest(new { message = "Choose a UTC chart window of at most 31 days ending at or before the current time." });

        var context = tokens.Read(request.Token, user, symbol);
        if (context is null) return Denied();
        var window = new PaperChartWindow(request.FromUtc, request.ToUtc);
        var cursor = request.Continuation is { } continuation ? tokens.ReadPage(continuation, context, window) : null;
        if (request.Continuation is not null && cursor is null) return Denied();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            if (!await access.CanReadAsync(context.Scope, deadline.Token)) return Denied();
            var facts = await journal.ReadAsync(context.Scope, deadline.Token);
            if (facts is null) return Denied();
            var page = PrivatePaperChartProjection.Build(facts, window, cursor, context, tokens, clock.GetUtcNow());
            if (context.ExpiresAtUtc <= clock.GetUtcNow() || !await access.CanReadAsync(context.Scope, deadline.Token))
                return Denied();
            return Results.Json(page);
        }
        catch (Exception error) when (error is SqliteException or InvalidDataException ||
            error is OperationCanceledException && !token.IsCancellationRequested)
        {
            logger.LogWarning("Local paper chart evidence unavailable ({FailureType}).", error.GetType().Name);
            return Results.Problem(Unavailable, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    // All missing and mismatched contexts produce the same response.
    private static IResult Denied() => Results.NotFound(new { message = Unavailable });
}
