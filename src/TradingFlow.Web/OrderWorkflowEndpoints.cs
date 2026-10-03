using Microsoft.AspNetCore.Mvc;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Persistence;
using TradingFlow.Web.Services;

namespace TradingFlow.Web;

public static class OrderWorkflowEndpoints
{
    public static RouteGroupBuilder MapOrderWorkflowEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/portfolio", async ([FromServices] TradingFlow.Web.Services.Desk.IDeskAccountReader reader, CancellationToken ct) =>
        {
            var account = await reader.ReadAsync(ct);
            return Results.Ok(new PortfolioResponse(ContractVersions.VersionOne, account.Available, account.Detail,
                account.ReadAtUtc, account.Holdings.Select(x => new PortfolioPositionResponse(x.Symbol, x.Quantity,
                    x.AverageEntry, x.CostBasis, x.MarketValue, x.UnrealizedPl)).ToArray()));
        });
        group.MapGet("/orders/journal", async ([FromServices] IOrderActivityQuery journal, [FromServices] IOrderIntentRepository intents,
            [FromServices] TradingFlow.Engine.Execution.BrokerAccountBindingOptions binding, CancellationToken ct) =>
        {
            var result = new List<OrderJournalEntry>();
            foreach (var row in await journal.ListRecentAsync(limit: 200, cancellationToken: ct))
            {
                var intent = await intents.GetByClientOrderIdAsync(row.ClientOrderId, ct);
                if (!binding.EnforcementEnabled || intent?.AccountId != binding.ExpectedAccountId) continue;
                result.Add(new(intent.IntentId, intent.AccountId, row.RunId, intent.CandidateId, row.ClientOrderId,
                    row.BrokerOrderId, row.Symbol, row.Side, row.OrderType, row.TimeInForce, row.RequestedQuantity,
                    row.LimitPrice, row.State.ToString(), row.FilledQuantity, row.FillPrice, row.UpdatedAtUtc));
            }
            return Results.Ok(result);
        });
        group.MapPost("/orders/plan", (OrderTicketPlanRequest request, [FromServices] OrderTicketPlanService planner, CancellationToken cancellationToken) =>
            ExecuteAsync(() => planner.PlanAsync(request, cancellationToken)));
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
