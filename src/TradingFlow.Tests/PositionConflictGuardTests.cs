using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TradingFlow.Domain.Execution;
using TradingFlow.Domain.Locking;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Tests;

public sealed class PositionConflictGuardTests
{
    [Fact]
    public async Task ExecuteEntryAsync_DifferentPositionOwner_RejectsBeforeSubmission()
    {
        var locks = CreateAvailableLock();
        var positions = new Mock<IPositionLedgerRepository>(MockBehavior.Strict);
        positions.Setup(repository => repository.GetCurrentAsync("MSFT", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreatePosition("MSFT", 10m, "SWGA"));
        var intents = CreateEmptyIntentRepository();
        var submitted = false;
        var guard = CreateGuard(locks.Object, positions.Object, intents.Object);

        var error = await Assert.ThrowsAsync<PositionConflictException>(() =>
            guard.ExecuteEntryAsync(
                "msft",
                "OTHER",
                _ =>
                {
                    submitted = true;
                    return Task.FromResult("submitted");
                }));

        Assert.Equal(RejectCode.REJECT_SETUP_INVALID, error.RejectCode);
        Assert.False(submitted);
        locks.Verify(service => service.ReleaseLockAsync(
            "exe10:MSFT",
            It.Is<string>(owner => owner.StartsWith("entry:", StringComparison.Ordinal)),
            CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task ExecuteEntryAsync_SamePositionOwner_AllowsSubmission()
    {
        var locks = CreateAvailableLock();
        var positions = new Mock<IPositionLedgerRepository>(MockBehavior.Strict);
        positions.Setup(repository => repository.GetCurrentAsync("MSFT", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreatePosition("MSFT", 10m, "SWGA"));
        var intents = CreateEmptyIntentRepository();
        var guard = CreateGuard(locks.Object, positions.Object, intents.Object);

        var result = await guard.ExecuteEntryAsync("MSFT", "SWGA", _ => Task.FromResult("submitted"));

        Assert.Equal("submitted", result);
    }

    [Fact]
    public async Task ExecuteEntryAsync_DifferentActiveIntentOwner_RejectsBeforeSubmission()
    {
        var locks = CreateAvailableLock();
        var positions = new Mock<IPositionLedgerRepository>(MockBehavior.Strict);
        positions.Setup(repository => repository.GetCurrentAsync("RGTI", It.IsAny<CancellationToken>()))
            .ReturnsAsync((PositionLedgerSnapshot?)null);
        var intents = new Mock<IOrderIntentRepository>(MockBehavior.Strict);
        intents.Setup(repository => repository.ListActiveForSymbolAsync("RGTI", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ActiveOrderIntent("owner-order", "SWGA", "RGTI", "buy", OrderState.Acked)
            ]);
        var submitted = false;
        var guard = CreateGuard(locks.Object, positions.Object, intents.Object);

        var error = await Assert.ThrowsAsync<PositionConflictException>(() =>
            guard.ExecuteEntryAsync(
                "RGTI",
                "OTHER",
                _ =>
                {
                    submitted = true;
                    return Task.FromResult("submitted");
                }));

        Assert.Equal(RejectCode.REJECT_SETUP_INVALID, error.RejectCode);
        Assert.False(submitted);
    }

    [Fact]
    public async Task ExecuteEntryAsync_UnavailableLease_FailsClosed()
    {
        var locks = new Mock<ITickerLockService>(MockBehavior.Strict);
        locks.Setup(service => service.TryAcquireLockAsync(
                "exe10:NVDA",
                It.IsAny<string>(),
                TimeSpan.FromMinutes(2),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var guard = CreateGuard(
            locks.Object,
            Mock.Of<IPositionLedgerRepository>(),
            Mock.Of<IOrderIntentRepository>());

        var error = await Assert.ThrowsAsync<PositionConflictException>(() =>
            guard.ExecuteEntryAsync("NVDA", "SWGA", _ => Task.FromResult("submitted")));

        Assert.Equal(RejectCode.REJECT_RECONCILE_LOCK, error.RejectCode);
        locks.Verify(service => service.ReleaseLockAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteEntryAsync_SubmissionFailure_StillReleasesLease()
    {
        var locks = CreateAvailableLock();
        var positions = new Mock<IPositionLedgerRepository>(MockBehavior.Strict);
        positions.Setup(repository => repository.GetCurrentAsync("MSFT", It.IsAny<CancellationToken>()))
            .ReturnsAsync((PositionLedgerSnapshot?)null);
        var intents = CreateEmptyIntentRepository();
        var guard = CreateGuard(locks.Object, positions.Object, intents.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            guard.ExecuteEntryAsync<string>(
                "MSFT",
                "SWGA",
                _ => throw new InvalidOperationException("broker failure")));

        locks.Verify(service => service.ReleaseLockAsync(
            "exe10:MSFT",
            It.Is<string>(owner => owner.StartsWith("entry:", StringComparison.Ordinal)),
            CancellationToken.None), Times.Once);
    }

    private static PositionConflictGuard CreateGuard(
        ITickerLockService locks,
        IPositionLedgerRepository positions,
        IOrderIntentRepository intents) =>
        new(
            locks,
            positions,
            intents,
            new PositionConflictOptions(allowMultiStrategySameSymbol: false),
            NullLogger<PositionConflictGuard>.Instance);

    private static Mock<ITickerLockService> CreateAvailableLock()
    {
        var locks = new Mock<ITickerLockService>(MockBehavior.Strict);
        locks.Setup(service => service.TryAcquireLockAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                TimeSpan.FromMinutes(2),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        locks.Setup(service => service.ReleaseLockAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                CancellationToken.None))
            .Returns(Task.CompletedTask);
        return locks;
    }

    private static Mock<IOrderIntentRepository> CreateEmptyIntentRepository()
    {
        var intents = new Mock<IOrderIntentRepository>(MockBehavior.Strict);
        intents.Setup(repository => repository.ListActiveForSymbolAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        return intents;
    }

    private static PositionLedgerSnapshot CreatePosition(
        string symbol,
        decimal quantity,
        string strategyId) =>
        new(
            symbol,
            quantity,
            strategyId,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            1,
            "owner-order",
            100m,
            "buy");
}
