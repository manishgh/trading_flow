using Microsoft.Extensions.Configuration;
using Moq;
using TradingFlow.Domain.Jobs;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Execution;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class PaperJobRecoveryServiceTests
{
    [Fact]
    public async Task RecoverAsync_WaitsForDispatchRecoveryBeforeBrokerReconciliation()
    {
        var events = new List<string>();
        var health = new Mock<IOrderDispatchRecoveryHealth>();
        health.Setup(value => value.WaitUntilReadyAsync(
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback(() => events.Add("dispatch-ready"))
            .Returns(Task.CompletedTask);
        var synchronization = new Mock<IOrderSynchronizationCoordinator>();
        synchronization.Setup(value => value.CrossCheckAsync(
                It.IsAny<IAccountScopedBrokerOrderReader>(), It.IsAny<CancellationToken>()))
            .Callback(() => events.Add("order-sync"))
            .ReturnsAsync([]);
        var reconciliation = new Mock<IAccountReconciliationService>();
        reconciliation.Setup(value => value.ReconcileAsync(
                It.IsAny<IBrokerClient>(),
                It.IsAny<IReadOnlyList<TradingFlow.Domain.Orders.ActiveBrokerOrder>>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => events.Add("account-reconcile"))
            .ReturnsAsync(new AccountReconciliationResult(Guid.NewGuid(), "clean", [], false));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Alpaca:KeyId"] = "test-key",
                ["Alpaca:SecretKey"] = "test-secret"
            })
            .Build();
        var repositoryRoot = TestRepository.FindRoot();
        var service = new PaperJobRecoveryService(
            new SimpleYamlReader(),
            new PaperRuntimeFactory(
                new AlpacaCredentialProvider(configuration),
                new ProjectPaths(repositoryRoot)),
            health.Object,
            synchronization.Object,
            reconciliation.Object,
            PaperJobServiceOptions.Default);

        await service.RecoverAsync(
            new PersistedJob
            {
                Id = Guid.NewGuid(),
                JobType = "paper",
                RunName = "recovery-order",
                ConfigPath = Path.Combine(repositoryRoot, "configs", "paper", "alpaca-paper.yaml")
            },
            CancellationToken.None);

        Assert.Equal(new[] { "dispatch-ready", "order-sync", "account-reconcile" }, events);
    }

    [Fact]
    public async Task RecoverAsync_RejectsReconciliationWithFailedProtectionRepair()
    {
        var health = new Mock<IOrderDispatchRecoveryHealth>();
        health.Setup(value => value.WaitUntilReadyAsync(
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var synchronization = new Mock<IOrderSynchronizationCoordinator>();
        synchronization.Setup(value => value.CrossCheckAsync(
                It.IsAny<IAccountScopedBrokerOrderReader>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var reconciliationId = Guid.NewGuid();
        var reconciliation = new Mock<IAccountReconciliationService>();
        reconciliation.Setup(value => value.ReconcileAsync(
                It.IsAny<IBrokerClient>(),
                It.IsAny<IReadOnlyList<TradingFlow.Domain.Orders.ActiveBrokerOrder>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountReconciliationResult(
                reconciliationId,
                "clean",
                [],
                RequiresAcknowledgement: false,
                ProtectionRepairFailureCount: 1));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Alpaca:KeyId"] = "test-key",
                ["Alpaca:SecretKey"] = "test-secret"
            })
            .Build();
        var repositoryRoot = TestRepository.FindRoot();
        var service = new PaperJobRecoveryService(
            new SimpleYamlReader(),
            new PaperRuntimeFactory(
                new AlpacaCredentialProvider(configuration),
                new ProjectPaths(repositoryRoot)),
            health.Object,
            synchronization.Object,
            reconciliation.Object,
            PaperJobServiceOptions.Default);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecoverAsync(
            new PersistedJob
            {
                Id = Guid.NewGuid(),
                JobType = "paper",
                RunName = "failed-protection-recovery",
                ConfigPath = Path.Combine(repositoryRoot, "configs", "paper", "alpaca-paper.yaml")
            },
            CancellationToken.None));

        Assert.Contains(reconciliationId.ToString("N"), error.Message, StringComparison.Ordinal);
        Assert.Contains("could not restore protection", error.Message, StringComparison.Ordinal);
    }
}
