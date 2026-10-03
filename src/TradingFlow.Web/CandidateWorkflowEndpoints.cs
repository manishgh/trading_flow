using TradingFlow.Application.Candidates;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Web;

/// <summary>Application workflow routes; the calling group supplies operator authorization.</summary>
public static class CandidateWorkflowEndpoints
{
    public static RouteGroupBuilder MapCandidateWorkflowEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/universes/preview", async (
            UniversePreviewRequest request,
            IUniverseDiscoveryService workflow,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await workflow.PreviewAsync(request, cancellationToken));
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(new ApiErrorResponse(
                    ContractVersions.VersionOne,
                    "universe_preview_rejected",
                    exception.Message));
            }
        });

        group.MapPost("/candidate-runs", async (
            CreateCandidateRunRequest request,
            ICandidateWorkflow workflow,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await workflow.StartAsync(request, cancellationToken));
            }
            catch (IdempotencyKeyConflictException exception)
            {
                return Results.Conflict(new ApiErrorResponse(ContractVersions.VersionOne, "idempotency_key_conflict", exception.Message));
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(new ApiErrorResponse(
                    ContractVersions.VersionOne,
                    "candidate_run_rejected",
                    exception.Message));
            }
        });

        group.MapGet("/candidate-runs/{runId:guid}", async (
            Guid runId,
            ICandidateQueryService queries,
            CancellationToken cancellationToken) =>
        {
            var run = await queries.GetRunAsync(runId, cancellationToken);
            return run is null ? Results.NotFound() : Results.Ok(run);
        });

        group.MapGet("/candidates/{candidateId:guid}/audit", async (
            Guid candidateId,
            IDecisionAuditQuery queries,
            CancellationToken cancellationToken) =>
        {
            var audit = await queries.GetAsync(candidateId, cancellationToken);
            return audit is null ? Results.NotFound() : Results.Ok(audit);
        });

        return group;
    }
}
