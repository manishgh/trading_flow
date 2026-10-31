using TradingFlow.Application.Jobs;
using TradingFlow.Contracts.V1;

namespace TradingFlow.Web;

public static class TradingLimitEndpoints
{
    /// <summary>Reads/saves account-wide mode limits independently of run creation, using existing operator authentication.</summary>
    public static RouteGroupBuilder MapTradingLimitEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/trading-limits/{mode}", (string mode, TradingLimitsService service, CancellationToken token) =>
            Execute(() => service.GetAsync(mode, token)));
        group.MapPost("/trading-limits", (SaveTradingLimitProfileRequest request, TradingLimitsService service, CancellationToken token) =>
            Execute(() => service.SaveAsync(request, token)));
        return group;
    }

    /// <summary>Returns explicit input/revision errors so stale editors reload instead of overwriting another saved choice.</summary>
    private static async Task<IResult> Execute<T>(Func<Task<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (InvalidOperationException error)
        { return Results.BadRequest(new ApiErrorResponse(ContractVersions.VersionOne, "trading_limits_rejected", error.Message)); }
        catch (ArgumentException error)
        { return Results.BadRequest(new ApiErrorResponse(ContractVersions.VersionOne, "trading_limits_rejected", error.Message)); }
    }
}
