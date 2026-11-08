using Microsoft.EntityFrameworkCore;
using Moq;
using TradingFlow.Application.Candidates;
using TradingFlow.Application.Jobs;
using TradingFlow.Contracts.V1;
using TradingFlow.Data.Jobs;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Tests;

public sealed class RunWorkflowIntegrityTests
{
    [Fact]
    public async Task Enqueue_BindsImmutableMembershipAndExactDatesAndReplaysAfterExpiry()
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var clock = new CandidateWorkflowTestClock(CandidateAuditIntegrityTests.Now);
        var preview = await UniverseWorkflowIntegrityTests.Preview(database, clock);
        var identity = UniverseWorkflowIntegrityTests.Request(preview).Strategies[0];
        var configuration = new Mock<IWorkflowRunConfiguration>(MockBehavior.Strict);
        var strategies = new Mock<IStrategyIdentityResolver>();
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "immutable config fixture");
            var request = new StartBacktestRequest(ContractVersions.VersionOne, "fixture", preview.UniverseSnapshotId, [identity],
                clock.Now.AddMonths(-6), clock.Now.AddDays(-1), 10000, 1, 25, 4, "use_cache", "full", "job-key");
            configuration.Setup(item => item.WriteAsync(request, It.Is<UniversePreviewResponse>(value => value.ContentSha256 == preview.ContentSha256),
                It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([path]);
            var repository = new SqliteJobRepository(database);
            var service = new RunWorkflowService(repository, database.Workflows, strategies.Object, configuration.Object, clock);
            var first = await service.StartAsync(request);
            Assert.Equal("queued", first.Status);
            Assert.Equal(preview.UniverseSnapshotId, first.Provenance!.UniverseSnapshotId);
            Assert.Equal(identity, Assert.Single(first.Provenance.Strategies));
            clock.Now = clock.Now.AddHours(1);
            Assert.Equal(first.JobId, (await service.StartAsync(request)).JobId);
            configuration.Verify(item => item.WriteAsync(request, It.IsAny<UniversePreviewResponse>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
            await Assert.ThrowsAsync<IdempotencyKeyConflictException>(() => service.StartAsync(request with { StartingCapital = 20000 }));
            var cancelled = await service.CancelAsync(first.JobId, new(ContractVersions.VersionOne, "operator"));
            Assert.NotNull(cancelled!.CancellationRequestedAtUtc);
            Assert.Equal(cancelled.Status, (await service.GetAsync(first.JobId))!.Status);
            await using var context = database.CreateDbContext();
            Assert.Single(await context.Jobs.ToArrayAsync());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ExpiredUniverseOrUnauthorizedPaperMode_CannotWriteOrEnqueue()
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var clock = new CandidateWorkflowTestClock(CandidateAuditIntegrityTests.Now);
        var preview = await UniverseWorkflowIntegrityTests.Preview(database, clock);
        var configuration = new Mock<IWorkflowRunConfiguration>(MockBehavior.Strict);
        var service = new RunWorkflowService(new SqliteJobRepository(database), database.Workflows,
            Mock.Of<IStrategyIdentityResolver>(), configuration.Object, clock);
        var request = new StartPaperRunRequest(ContractVersions.VersionOne, "paper", preview.UniverseSnapshotId,
            UniverseWorkflowIntegrityTests.Request(preview).Strategies[0], false, false, "limit", "day", "strategy_validated", "paper-key", "live");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(request));
        clock.Now = clock.Now.AddHours(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(request with { Mode = "paper_shadow" }));
        configuration.VerifyNoOtherCalls();
    }
}
