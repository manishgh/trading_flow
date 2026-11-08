using System.Globalization;
using System.Text.Json;
using TradingFlow.Application.Candidates;
using TradingFlow.Contracts.V1;

namespace TradingFlow.Web;

public sealed record CandidateStreamOptions(TimeSpan PollInterval, TimeSpan HeartbeatInterval, TimeSpan WriteTimeout)
{
    public static CandidateStreamOptions Default { get; } = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30));
}

public static class CandidateStreamEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static RouteGroupBuilder MapCandidateStreamEndpoints(this RouteGroupBuilder group, CandidateStreamOptions? settings = null)
    {
        var options = settings ?? CandidateStreamOptions.Default;
        group.MapGet("/candidate-runs/{runId:guid}/events/batch", async (Guid runId, long? after,
            ICandidateQueryService queries, ICandidateEventStreamService stream, CancellationToken cancellationToken) =>
        {
            if (after < 0) return Results.BadRequest(Error("event_cursor_invalid", "Cursor must be non-negative."));
            if (await queries.GetRunAsync(runId, cancellationToken) is null) return Results.NotFound();
            var batch = await stream.ReadAsync(runId, after ?? 0, 100, cancellationToken);
            return batch.RequiresResync ? Results.Conflict(Error("event_cursor_expired", "Reload the candidate run snapshot.")) : Results.Ok(batch);
        });
        group.MapGet("/candidate-runs/{runId:guid}/events", async (Guid runId, long? after, HttpContext context,
            ICandidateQueryService queries, ICandidateEventStreamService stream, CancellationToken cancellationToken) =>
        {
            var cursor = after ?? 0;
            if ((after is null && context.Request.Headers.TryGetValue("Last-Event-ID", out var resume) &&
                 !Int64.TryParse(resume.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out cursor)) || cursor < 0)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(Error("event_cursor_invalid", "Cursor must be non-negative."), cancellationToken);
                return;
            }
            if (await queries.GetRunAsync(runId, cancellationToken) is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            var pending = await stream.ReadAsync(runId, cursor, 100, cancellationToken);
            if (pending.RequiresResync)
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await context.Response.WriteAsJsonAsync(Error("event_cursor_expired", "Reload the candidate run snapshot."), cancellationToken);
                return;
            }
            context.Response.Headers.CacheControl = "no-cache, no-transform";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            context.Response.ContentType = "text/event-stream";
            var lastWrite = DateTimeOffset.UtcNow;
            try
            {
                await WriteAsync("retry: 2000\n: connected\n\n");
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (pending.RequiresResync)
                    {
                        await WriteAsync("event: resync\ndata: " + JsonSerializer.Serialize(Error("event_cursor_expired", "Reload the candidate run snapshot."), Json) + "\n\n");
                        break;
                    }
                    foreach (var item in pending.Events)
                    {
                        await WriteAsync($"id: {item.EventSequence}\nevent: candidate.changed\ndata: {JsonSerializer.Serialize(item, Json)}\n\n");
                        cursor = item.EventSequence;
                        lastWrite = DateTimeOffset.UtcNow;
                    }
                    if (DateTimeOffset.UtcNow - lastWrite >= options.HeartbeatInterval)
                    {
                        var heartbeat = new StreamHeartbeatResponse(ContractVersions.VersionOne, $"candidate-runs/{runId:N}", pending.LatestSequence, DateTimeOffset.UtcNow);
                        await WriteAsync("event: heartbeat\ndata: " + JsonSerializer.Serialize(heartbeat, Json) + "\n\n");
                        lastWrite = DateTimeOffset.UtcNow;
                    }
                    await Task.Delay(options.PollInterval, cancellationToken);
                    pending = await stream.ReadAsync(runId, cursor, 100, cancellationToken);
                }
            }
            catch (OperationCanceledException) { if (!cancellationToken.IsCancellationRequested) context.Abort(); }
            catch (IOException) { context.Abort(); }

            async Task WriteAsync(string data)
            {
                using var write = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                write.CancelAfter(options.WriteTimeout);
                await context.Response.WriteAsync(data, write.Token);
                await context.Response.Body.FlushAsync(write.Token);
            }
        });
        return group;
    }
    private static ApiErrorResponse Error(string code, string message) => new(ContractVersions.VersionOne, code, message);
}
