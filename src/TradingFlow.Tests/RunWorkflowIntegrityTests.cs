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
    /// <summary>Freezes membership, exact run dates and one comparison request across command replay and cancellation.</summary>
    [Fact]
    public async Task Enqueue_BindsImmutableMembershipAndExactDatesAndReplaysAfterExpiry()
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var clock = new CandidateWorkflowTestClock(CandidateAuditIntegrityTests.Now);
        var preview = await UniverseWorkflowIntegrityTests.Preview(database, clock);
        var identity = UniverseWorkflowIntegrityTests.Request(preview).Strategies[0];
        var configuration = new Mock<IWorkflowRunConfiguration>(MockBehavior.Strict);
        configuration.Setup(item => item.OpenEvidenceVerificationScopeAsync(It.IsAny<StartBacktestRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IWorkflowEvidenceVerificationScope?)null);
        configuration.Setup(item => item.GetDecisionContextEvidenceAsync(It.IsAny<StartBacktestRequest>(), It.IsAny<CancellationToken>(), It.IsAny<IWorkflowEvidenceVerificationScope?>()))
            .ReturnsAsync((DecisionContextEvidenceSummary?)null);
        configuration.Setup(item => item.ValidateCapturedBacktestInputsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>(), It.IsAny<IWorkflowEvidenceVerificationScope?>()))
            .Returns(Task.CompletedTask);
        var strategies = new Mock<IStrategyIdentityResolver>();
        var root = Path.Combine(Path.GetTempPath(), "workflow-prepared-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "configuration.json");
        try
        {
            var request = new StartBacktestRequest(ContractVersions.VersionOne, "fixture", preview.UniverseSnapshotId, [identity],
                clock.Now.AddMonths(-6), clock.Now.AddDays(-1), 10000, 1, 25, 4, "use_cache", "full", "job-key");
            await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(request));
            configuration.Setup(item => item.WritePreparedComparisonRequestAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<TradingFlow.Domain.Jobs.PersistedJob>>(),
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .Returns<string, IReadOnlyList<TradingFlow.Domain.Jobs.PersistedJob>, DateTimeOffset, CancellationToken>((comparison, books, cutoff, token) =>
                    PreparedWorkflowTestRequest.WriteAsync(root, comparison, books, cutoff, preview, token));
            configuration.Setup(item => item.WriteAsync(request, It.Is<UniversePreviewResponse>(value => value.ContentSha256 == preview.ContentSha256),
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IWorkflowEvidenceVerificationScope?>())).ReturnsAsync([path]);
            var repository = new SqliteJobRepository(database);
            var service = new RunWorkflowService(repository, database.Workflows, strategies.Object, configuration.Object, clock, new(32, 16));
            var first = await service.StartAsync(request);
            Assert.Equal("queued", first.Status);
            Assert.Equal(preview.UniverseSnapshotId, first.Provenance!.UniverseSnapshotId);
            Assert.Equal(identity, Assert.Single(first.Provenance.Strategies));
            clock.Now = clock.Now.AddHours(1);
            Assert.Equal(first.JobId, (await service.StartAsync(request)).JobId);
            configuration.Verify(item => item.WriteAsync(request, It.IsAny<UniversePreviewResponse>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IWorkflowEvidenceVerificationScope?>()), Times.Once);
            configuration.Verify(item => item.WritePreparedComparisonRequestAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<TradingFlow.Domain.Jobs.PersistedJob>>(),
                request.TestWindowEndUtc, It.IsAny<CancellationToken>()), Times.Once);
            var captured = TradingFlow.Application.Jobs.DurableFileJobRequest.Deserialize((await repository.GetJobAsync(first.JobId, default))!.RequestJson);
            Assert.NotNull(captured.PreparedMarketData);
            using var lease = captured.AcquireVerifiedReadLease();
            await Assert.ThrowsAsync<IdempotencyKeyConflictException>(() => service.StartAsync(request with { StartingCapital = 20000 }));
            var cancelled = await service.CancelAsync(first.JobId, new(ContractVersions.VersionOne, "operator"));
            Assert.NotNull(cancelled!.CancellationRequestedAtUtc);
            Assert.Equal(cancelled.Status, (await service.GetAsync(first.JobId))!.Status);
            await using var context = database.CreateDbContext();
            Assert.Single(await context.Jobs.ToArrayAsync());
        }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar))
                throw new InvalidOperationException("Workflow fixture cleanup escaped its owned temporary directory.");
            Directory.Delete(root, true);
        }
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
            Mock.Of<IStrategyIdentityResolver>(), configuration.Object, clock, new(32, 16));
        var request = new StartPaperRunRequest(ContractVersions.VersionOne, "paper", preview.UniverseSnapshotId,
            UniverseWorkflowIntegrityTests.Request(preview).Strategies[0], false, false, "limit", "day", "strategy_validated", "paper-key", "live");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(request));
        clock.Now = clock.Now.AddHours(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(request with { Mode = "paper_shadow" }));
        configuration.VerifyNoOtherCalls();
    }
}
