using System.Security.Claims;
using TradingFlow.Application.Preparation;
using TradingFlow.Contracts.Preparation;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Persistence;
namespace TradingFlow.Web;
public static class PreparationEndpoints
{
    public static void MapPreparationEndpoints(this RouteGroupBuilder group)
    {
        var preparation=group.MapGroup("/preparation");
        preparation.MapGet("/editor",()=>Results.Ok(new { defaults=new PreparationSettings(), fields=PreparationEditor.Fields }));
        preparation.MapGet("",(ClaimsPrincipal user,PreparationService service,CancellationToken ct)=>Execute(()=>service.ListAsync(Owner(user),ct)));
        preparation.MapPost("",(ClaimsPrincipal user,SavePreparationRequest request,PreparationService service,CancellationToken ct)=>Execute(()=>service.SaveAsync(Owner(user),request,ct)));
        preparation.MapGet("/{id:guid}",(Guid id,ClaimsPrincipal user,PreparationService service,CancellationToken ct)=>Execute(()=>service.DetailAsync(Owner(user),id,ct)));
        preparation.MapPost("/{id:guid}/run",(Guid id,ClaimsPrincipal user,PreparationRunRequest request,PreparationService service,CancellationToken ct)=>Execute(()=>service.RunAsync(Owner(user),id,request,ct)));
        preparation.MapPost("/{id:guid}/occurrences/{occurrence:guid}/cancel",(Guid id,Guid occurrence,ClaimsPrincipal user,PreparationService service,CancellationToken ct)=>Execute(()=>service.CancelAsync(Owner(user),id,occurrence,ct)));
        preparation.MapPost("/{id:guid}/occurrences/{occurrence:guid}/handoff",(Guid id,Guid occurrence,ClaimsPrincipal user,PreparationHandoffRequest request,PreparationService service,CancellationToken ct)=>Execute(()=>service.HandoffAsync(Owner(user),id,occurrence,request,ct)));
        preparation.MapGet("/{id:guid}/occurrences/{occurrence:guid}/evidence/{symbol}",(Guid id,Guid occurrence,string symbol,ClaimsPrincipal user,PreparationService service,CancellationToken ct)=>Execute(()=>service.EvidenceAsync(Owner(user),id,occurrence,symbol,ct)));
        preparation.MapPost("/{id:guid}/occurrences/{occurrence:guid}/curate",(Guid id,Guid occurrence,ClaimsPrincipal user,PreparationCurationRequest request,PreparationService service,CancellationToken ct)=>Execute(()=>service.CurateAsync(Owner(user),id,occurrence,request,ct)));
        preparation.MapGet("/accepted",(string key,ClaimsPrincipal user,PreparationService service,CancellationToken ct)=>Execute(()=>service.ReadHandoffAsync(Owner(user),key,ct)));
        preparation.MapGet("/events",(long? after,ClaimsPrincipal user,IApplicationEventRepository events,CancellationToken ct)=>Execute(()=>events.ListAsync("preparation:"+Owner(user),Math.Max(0,after??0),100,ct)));
    }
    private static string Owner(ClaimsPrincipal user)=>user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Sign in again.");
    private static async Task<IResult> Execute<T>(Func<Task<T>> action)
    {
        try {return Results.Ok(await action());}
        catch(KeyNotFoundException) {return Results.NotFound();}
        catch(InvalidOperationException ex) {return Results.BadRequest(new ApiErrorResponse(ContractVersions.VersionOne,"preparation_rejected",ex.Message));}
    }
}
