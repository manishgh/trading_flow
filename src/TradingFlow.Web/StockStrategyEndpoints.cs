using TradingFlow.Contracts.V1;
using TradingFlow.Web.Services.Desk;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace TradingFlow.Web;

public static class StockStrategyEndpoints
{
    /// <summary>Maps recorded checks and durable current observations; the caller supplies the authenticated API group.</summary>
    public static RouteGroupBuilder MapStockStrategyEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/strategy-choices", async (DeskStrategyAssessmentService assessments, CancellationToken token) =>
            Results.Ok(await assessments.ChoicesAsync(token)));
        group.MapGet("/strategy-assessments", ReadAsync);
        group.MapPost("/strategy-assessments", async (StartStockStrategyAssessmentRequest request,
            ClaimsPrincipal user, [FromServices] CurrentStockStrategyAssessmentService current, CancellationToken token) =>
            await RespondAsync(() => current.StartAsync(Owner(user), request, token)));
        group.MapGet("/strategy-assessments/{id:guid}", async (Guid id, ClaimsPrincipal user,
            [FromServices] CurrentStockStrategyAssessmentService current, CancellationToken token) =>
            await RespondAsync(() => current.ReadAsync(Owner(user), id, token)));
        group.MapPost("/strategy-assessments/{id:guid}/cancel", async (Guid id, ClaimsPrincipal user,
            [FromServices] CurrentStockStrategyAssessmentService current, CancellationToken token) =>
            await RespondAsync(() => current.CancelAsync(Owner(user), id, token)));
        return group;
    }

    /// <summary>Uses the authenticated operator ID; clients cannot submit another job owner.</summary>
    private static string Owner(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new UnauthorizedAccessException("Sign in before requesting strategy checks.");

    /// <summary>Returns specific API failures while leaving unexpected server faults available to normal logging.</summary>
    private static async Task<IResult> RespondAsync(Func<Task<StockStrategyAssessmentJob>> action)
    {
        try { return Results.Ok(await action()); }
        catch (UnauthorizedAccessException exception) { return Results.Json(Error(exception.Message), statusCode: StatusCodes.Status403Forbidden); }
        catch (KeyNotFoundException exception) { return Results.NotFound(Error(exception.Message)); }
        catch (ArgumentException exception) { return Results.BadRequest(Error(exception.Message)); }
        catch (InvalidOperationException exception) { return Results.Conflict(Error(exception.Message)); }
    }
    /// <summary>Shares the established versioned API error shape with both clients.</summary>
    private static ApiErrorResponse Error(string message) => new(ContractVersions.VersionOne, "strategy_assessment_invalid", message);

    /// <summary>Validates a bounded stock selection and returns original checks without evaluating a new candidate.</summary>
    private static async Task<IResult> ReadAsync(Guid? runId, string strategyId, string semanticVersion,
        string contentSha256, string symbols, DeskStrategyAssessmentService assessments, CancellationToken token)
    {
        try
        {
            return Results.Ok(await assessments.ReadAsync(runId, new(strategyId, semanticVersion, contentSha256),
                symbols.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), token));
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new ApiErrorResponse(ContractVersions.VersionOne, "strategy_assessment_invalid", exception.Message));
        }
    }
}
