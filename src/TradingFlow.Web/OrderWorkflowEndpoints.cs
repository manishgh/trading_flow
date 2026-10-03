using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Persistence;
using TradingFlow.Web.Services;

namespace TradingFlow.Web;

public static class OrderWorkflowEndpoints
{
    public static RouteGroupBuilder MapOrderWorkflowEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/orders/preview", (PreviewOrderRequest request, IOrderWorkflowService workflow, CancellationToken cancellationToken) =>
            ExecuteAsync(() => workflow.PreviewAsync(request, cancellationToken)));
        group.MapPost("/orders/confirm", (ConfirmOrderRequest request, IOrderWorkflowService workflow, CancellationToken cancellationToken) =>
            ExecuteAsync(() => workflow.ConfirmAsync(request, cancellationToken)));
        group.MapPost("/orders/cancel", (CancelOrderRequest request, IOrderWorkflowService workflow, CancellationToken cancellationToken) =>
            ExecuteAsync(() => workflow.CancelAsync(request, cancellationToken)));
        group.MapPost("/positions/close", (ClosePositionRequest request, IOrderWorkflowService workflow, CancellationToken cancellationToken) =>
            ExecuteAsync(() => workflow.CloseAsync(request, cancellationToken)));
        return group;
    }

    private static async Task<IResult> ExecuteAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            var result = await operation();
            return result is OrderCommandResponse { State: "confirmation_pending" }
                ? Results.Json(result, statusCode: StatusCodes.Status202Accepted)
                : Results.Ok(result);
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (IdempotencyKeyConflictException exception)
        { return Results.Conflict(new ApiErrorResponse(ContractVersions.VersionOne, "idempotency_key_conflict", exception.Message)); }
        catch (InvalidOperationException exception)
        { return Results.BadRequest(new ApiErrorResponse(ContractVersions.VersionOne, "order_workflow_rejected", exception.Message)); }
    }
}
