using TradingFlow.Web.Services;

namespace TradingFlow.Web;

public static class WarmupQueueEndpoints
{
    /// <summary>Uses the shared signed-in API boundary for queue state, cancellation and archive retry.</summary>
    public static RouteGroupBuilder MapWarmupQueueEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/warmup/jobs", (WarmupServiceClient client, CancellationToken token) => Forward(() => client.GetJobsAsync(token)));
        group.MapPost("/warmup/jobs/{id}/cancel", async (string id, WarmupServiceClient client, CancellationToken token) =>
            await Forward(() => client.CancelJobAsync(id, token)));
        group.MapPost("/warmup/runs/{id}/archive/retry", async (string id, WarmupServiceClient client, CancellationToken token) =>
            await Forward(() => client.RetryArchivesAsync(id, token)));
        return group;
    }

    /// <summary>Distinguishes service connection failure from an empty queue or a rejected command.</summary>
    private static async Task<IResult> Forward<T>(Func<Task<T>> request)
    {
        try { return Results.Ok(await request()); }
        catch (HttpRequestException) { return Results.Json(new { message = "The warmup service could not be reached. Its job status is unavailable." }, statusCode: 503); }
        catch (InvalidOperationException error) { return Results.BadRequest(new { message = error.Message }); }
    }
}
