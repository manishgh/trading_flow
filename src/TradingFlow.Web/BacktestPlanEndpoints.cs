using TradingFlow.Application.Jobs;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Web;

public static class BacktestPlanEndpoints
{
    /// <summary>Exposes the same saved-plan workflow to Web and Android; saving never starts jobs.</summary>
    public static RouteGroupBuilder MapBacktestPlanEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/backtests/sources", ([Microsoft.AspNetCore.Mvc.FromServices] TradingFlow.Web.Services.Desk.DeskSources sources, CancellationToken token) =>
            sources.BacktestChoicesAsync(token));
        group.MapPost("/backtests/plans/preview", (SaveBacktestPlanRequest request, BacktestPlanService service, CancellationToken token) =>
            Execute(() => service.PreviewAsync(request, token)));
        group.MapPost("/backtests/plans", (SaveBacktestPlanRequest request, BacktestPlanService service, CancellationToken token) =>
            Execute(() => service.SaveAsync(request, token)));
        group.MapGet("/backtests/plans", (BacktestPlanService service, CancellationToken token) => service.ListAsync(token));
        group.MapGet("/backtests/plans/{id:long}", async (long id, BacktestPlanService service, CancellationToken token) =>
            await service.GetAsync(id, token) is { } plan ? Results.Ok(plan) : Results.NotFound());
        group.MapPost("/backtests/plans/{id:long}/start", (long id, StartBacktestPlanRequest request, BacktestPlanService service, CancellationToken token) =>
            Execute(() => service.StartAsync(id, request, token)));
        return group;
    }

    /// <summary>Returns explicit invalid-choice and conflicting-retry errors without accepting partial work.</summary>
    private static async Task<IResult> Execute<T>(Func<Task<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (IdempotencyKeyConflictException exception)
        { return Results.Conflict(new ApiErrorResponse(ContractVersions.VersionOne, "idempotency_key_conflict", exception.Message)); }
        catch (Exception exception) when (exception is InvalidDataException or FileNotFoundException or DirectoryNotFoundException)
        { return Results.BadRequest(new ApiErrorResponse(ContractVersions.VersionOne, "backtest_source_unavailable", exception.Message)); }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or OverflowException)
        { return Results.BadRequest(new ApiErrorResponse(ContractVersions.VersionOne, "backtest_plan_rejected", exception.Message)); }
    }
}
