using TradingFlow.Contracts.V1;
using TradingFlow.Web.Services;

namespace TradingFlow.Web;

/// <summary>
/// Authenticated REST endpoints for building point-in-time research screens and planning/collecting 5-minute bars
/// from the Backtests workspace. Mapped inside <c>/api/v1</c> (which enforces operator authorization).
/// </summary>
public static class ResearchScreenEndpoints
{
    /// <summary>Maps research-screen discovery, build, two-step 5-minute bar plan/collect, and job-status routes.</summary>
    public static RouteGroupBuilder MapResearchScreenEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/backtests/research-screens/options", (
            ResearchEvidenceJobService service,
            CancellationToken cancellationToken) =>
            service.GetOptionsAsync(cancellationToken));

        group.MapPost("/backtests/research-screens/build", (
            BuildResearchScreenHttpRequest request,
            ResearchEvidenceJobService service,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.StartBuildScreenAsync(request, cancellationToken)));

        group.MapPost("/backtests/research-screens/minute-bars/plan", (
            PlanMinuteBarsHttpRequest request,
            ResearchEvidenceJobService service,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.PlanFiveMinuteBarsAsync(request, cancellationToken)));

        group.MapPost("/backtests/research-screens/minute-bars/collect", (
            CollectMinuteBarsHttpRequest request,
            ResearchEvidenceJobService service,
            CancellationToken cancellationToken) =>
            ExecuteAsync(() => service.StartCollectFiveMinuteBarsAsync(request, cancellationToken)));

        group.MapGet("/backtests/research-screens/jobs/{id:guid}", async (
            Guid id,
            ResearchEvidenceJobService service,
            CancellationToken cancellationToken) =>
            await service.GetJobAsync(id, cancellationToken) is { } job
                ? Results.Ok(job)
                : Results.NotFound(new ApiErrorResponse(ContractVersions.VersionOne, "research_evidence_job_not_found", $"Research evidence job '{id}' was not found.")));

        return group;
    }

    // Translates validation, missing-data, and credential failures into structured 400 ApiErrorResponse payloads.
    private static async Task<IResult> ExecuteAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (Exception exception) when (exception is InvalidDataException or FileNotFoundException or DirectoryNotFoundException)
        {
            return Results.BadRequest(new ApiErrorResponse(ContractVersions.VersionOne, "research_evidence_unavailable", exception.Message));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or FormatException)
        {
            return Results.BadRequest(new ApiErrorResponse(ContractVersions.VersionOne, "research_evidence_rejected", exception.Message));
        }
    }
}
