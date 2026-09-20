using TradingFlow.Domain.Jobs;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Web.Services;

public interface IPaperJobRecoveryService
{
    Task RecoverAsync(PersistedJob job, CancellationToken cancellationToken);
}

/// <summary>
/// Re-establishes broker truth before a previously claimed paper run may continue.
/// It reuses the shared synchronization and reconciliation brain; it never adopts or
/// submits an order through a recovery-specific path.
/// </summary>
public sealed class PaperJobRecoveryService(
    SimpleYamlReader yamlReader,
    PaperRuntimeFactory runtimeFactory,
    IOrderDispatchRecoveryHealth dispatchRecoveryHealth,
    IOrderSynchronizationCoordinator orderSynchronization,
    IAccountReconciliationService accountReconciliation,
    PaperJobServiceOptions options) : IPaperJobRecoveryService
{
    public async Task RecoverAsync(PersistedJob job, CancellationToken cancellationToken)
    {
        await dispatchRecoveryHealth.WaitUntilReadyAsync(
            options.RecoveryReadinessTimeout,
            cancellationToken);
        var runConfig = runtimeFactory.ResolveRunPaths(yamlReader.ReadBacktestRun(job.ConfigPath));
        var brokerClient = runtimeFactory.CreateBrokerClient(runConfig)
            ?? throw new InvalidOperationException("Paper recovery requires a configured broker client.");
        try
        {
            var openOrders = await orderSynchronization.CrossCheckAsync(brokerClient, cancellationToken);
            var reconciliation = await accountReconciliation.ReconcileAsync(
                brokerClient,
                openOrders,
                cancellationToken);
            if (reconciliation.RequiresAcknowledgement)
            {
                throw new InvalidOperationException(
                    $"Broker reconciliation {reconciliation.ReconciliationId:N} requires operator acknowledgement.");
            }

            if (!reconciliation.BrokerProtectionReady)
            {
                throw new InvalidOperationException(
                    $"Broker reconciliation {reconciliation.ReconciliationId:N} could not restore protection for " +
                    $"{reconciliation.ProtectionRepairFailureCount} position(s).");
            }
        }
        finally
        {
            if (brokerClient is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
