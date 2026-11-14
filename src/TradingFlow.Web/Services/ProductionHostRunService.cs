using TradingFlow.Data.Orders;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Web.Services;

/// <summary>Retains the host’s reconciliation run through the final broker-protection drain.</summary>
public sealed class ProductionHostRunService(
    SqliteProductionHostRunRepository repository,
    ReconciliationRunContext reconciliation,
    IHostApplicationLifetime lifetime,
    ILogger<ProductionHostRunService> logger) : BackgroundService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);

    // Commit ownership before any service can use this host’s reconciliation identity.
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await repository.StartAsync(reconciliation.Run, LeaseDuration, cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    // A lost or uncertain lease requests the normal host drain, including broker protection.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (!await repository.RenewAsync(reconciliation.Run.RunId, LeaseDuration, stoppingToken))
                {
                    logger.LogError("Reconciliation run {RunId} lost its host lease. Draining the host.", reconciliation.Run.RunId);
                    lifetime.StopApplication();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not renew the reconciliation run lease. Draining the host.");
            lifetime.StopApplication();
        }
    }

    // Called after the other services stop. Give the exact-row close a bounded chance
    // even if the outer shutdown token expired during the broker drain.
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await base.StopAsync(cancellationToken);
        }
        finally
        {
            using var closeBudget = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await repository.StopAsync(reconciliation.Run.RunId, closeBudget.Token);
        }
    }
}
