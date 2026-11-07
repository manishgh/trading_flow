using TradingFlow.Application.Jobs;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Jobs;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Web;

public static class RunWorkflowEndpoints
{
    public static RouteGroupBuilder MapRunWorkflowEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/backtests/runs", (StartBacktestRequest request, RunWorkflowService workflow, CancellationToken token) =>
            Execute(() => workflow.StartAsync(request, token)));
        group.MapPost("/paper/runs", (StartPaperRunRequest request, RunWorkflowService workflow, CancellationToken token) =>
            Execute(() => workflow.StartAsync(request, token)));
        group.MapGet("/workflow/jobs", async (string kind, RunWorkflowService workflow, CancellationToken token) =>
        {
            try { return Results.Ok(await workflow.ListAsync(kind, token)); }
            catch (InvalidOperationException exception) { return Error(exception); }
        });
        group.MapGet("/workflow/jobs/{id:guid}", async (Guid id, RunWorkflowService workflow, CancellationToken token) =>
            await workflow.GetAsync(id, token) is { } job ? Results.Ok(job) : Results.NotFound());
        group.MapGet("/workflow/jobs/{id:guid}/details", async (Guid id, RunWorkflowService workflow, CancellationToken token) =>
            await workflow.DetailsAsync(id, token) is { } detail ? Results.Ok(detail) : Results.NotFound());
        group.MapPost("/workflow/jobs/{id:guid}/cancel", async (Guid id, CancelJobRequest request, RunWorkflowService workflow, CancellationToken token) =>
        {
            try { return await workflow.CancelAsync(id, request, token) is { } job ? Results.Accepted($"/api/v1/workflow/jobs/{id}", job) : Results.NotFound(); }
            catch (InvalidOperationException exception) { return Error(exception); }
        });
        return group;
    }
    private static async Task<IResult> Execute(Func<Task<RunJobResponse>> action)
    {
        try { return Results.Ok(await action()); }
        catch (IdempotencyKeyConflictException exception) { return Results.Conflict(new ApiErrorResponse(ContractVersions.VersionOne, "idempotency_key_conflict", exception.Message)); }
        catch (InvalidOperationException exception) { return Error(exception); }
    }
    private static IResult Error(Exception exception) => Results.BadRequest(new ApiErrorResponse(ContractVersions.VersionOne, "run_rejected", exception.Message));
}
