using Microsoft.EntityFrameworkCore;
using TradingFlow.Domain.Strategies;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Web.Services;
using TradingFlow.Web.Services.Wishlists;
using System.Text.Json;
using Moq;
using TradingFlow.Application.Candidates;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Discovery;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Tests;

public sealed class UniverseWorkflowIntegrityTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-27T12:00:00Z");
    private static readonly StrategyReference Strategy = new("swing.test", "test", new string('a', 64));

    /// <summary>Day previews preserve their horizon and source evidence while operational candidate creation remains disabled.</summary>
    [Fact]
    public async Task DayPreview_PersistsHorizonAndCannotStartOperationalCandidates()
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var clock = new CandidateWorkflowTestClock(Now);
        var id = Guid.NewGuid();
        var wishlists = new Mock<IWishlistRepository>();
        wishlists.Setup(item => item.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(
            new Wishlist { Id = id, UpdatedAtUtc = Now, Items = [new WishlistItem { Ticker = "MSFT", Active = true }] });
        var resolver = new UniverseSourceResolver(wishlists.Object, Mock.Of<IScreenerSnapshotSource>(), clock);
        var service = new UniverseDiscoveryService(resolver, database.Discovery, database.Workflows, database.Workflows, clock);
        var preview = await service.PreviewAsync(new("1.0", "day", null, [new("wishlist", id.ToString(), id)]));
        var stored = await database.Workflows.GetAsync(preview.UniverseSnapshotId, default);
        var restored = UniverseDiscoveryService.ReadPreview(stored!, preview.UniverseSnapshotId);
        Assert.Equal("day", restored.Horizon);
        Assert.Equal(preview.ContentSha256, restored.ContentSha256);
        Assert.Equal("MSFT", Assert.Single(restored.Members).Symbol);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Workflow(database, clock).StartAsync(Request(restored)));
        await using var context = database.CreateDbContext();
        Assert.Empty(await context.Candidates.ToArrayAsync());
    }

    [Fact]
    public async Task Preview_RejectsObservationAfterExplicitCutoff()
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var clock = new CandidateWorkflowTestClock(Now);
        var service = Discovery(database, clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PreviewAsync(
            new UniversePreviewRequest(ContractVersions.VersionOne, "swing", Now.AddSeconds(-1),
                [new UniverseSourceSelectionRequest("wishlist", "watch")])));
    }

    [Fact]
    public async Task Start_PersistsSourceEvidenceThroughTheActualProjection()
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var clock = new CandidateWorkflowTestClock(Now);
        var preview = await Preview(database, clock);
        var result = await Workflow(database, clock).StartAsync(Request(preview));
        var candidate = Assert.Single(result.Candidates);
        var source = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<UniverseSourceEvidenceResponse>>(candidate.Sources));
        Assert.Equal("wishlist", source.SourceKind);
        Assert.Equal("watch", source.SourceKey);
        Assert.Equal(preview.Members[0].Sources[0].ContentSha256, source.ContentSha256);
        Assert.Equal(preview.UniverseSnapshotId, result.Provenance.UniverseSnapshotId);
    }

    [Fact]
    public async Task Start_ExactRetryAfterExpiryReturnsTheDurableRun()
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var clock = new CandidateWorkflowTestClock(Now);
        var preview = await Preview(database, clock);
        var request = Request(preview);
        var first = await Workflow(database, clock).StartAsync(request);
        clock.Now = Now.AddMinutes(10);
        var retry = await Workflow(database, clock).StartAsync(request);
        Assert.Equal(first.CandidateRunId, retry.CandidateRunId);
        Assert.Equal(first.Candidates.Select(item => item.CandidateId), retry.Candidates.Select(item => item.CandidateId));
    }

    [Fact]
    public async Task Start_RejectsSnapshotIdentitySubstitution()
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var clock = new CandidateWorkflowTestClock(Now);
        var preview = await Preview(database, clock);
        await using (var context = database.CreateDbContext())
        {
            var stored = await context.UniversePreviews.FindAsync(preview.UniverseSnapshotId);
            stored!.PreviewJson = JsonSerializer.Serialize(preview with { UniverseSnapshotId = Guid.NewGuid() });
            await context.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => Workflow(database, clock).StartAsync(Request(preview)));
    }

    [Fact]
    public async Task Preview_CurrentWishlistUsesResolutionCutoffWithoutBackdating()
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var clock = new CandidateWorkflowTestClock(Now);
        var id = Guid.NewGuid();
        var wishlists = new Mock<IWishlistRepository>();
        wishlists.Setup(item => item.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(() =>
        {
            clock.Now = Now.AddSeconds(1);
            return new Wishlist { Id = id, UpdatedAtUtc = Now, Items = [new WishlistItem { Ticker = "MSFT", Active = true }] };
        });
        var resolver = new UniverseSourceResolver(wishlists.Object, Mock.Of<IScreenerSnapshotSource>(), clock);
        var service = new UniverseDiscoveryService(resolver, database.Discovery, database.Workflows, database.Workflows, clock);
        var preview = await service.PreviewAsync(new UniversePreviewRequest(ContractVersions.VersionOne, "swing", null,
            [new UniverseSourceSelectionRequest("wishlist", id.ToString(), id)]));
        Assert.Equal(clock.Now, preview.DecisionCutoffUtc);
        Assert.Equal(clock.Now, preview.ResolvedAtUtc);
        Assert.Equal(clock.Now, Assert.Single(Assert.Single(preview.Members).Sources).ObservedAtUtc);
    }

    [Theory]
    [InlineData(StrategyCandidateState.Triggered)]
    [InlineData(StrategyCandidateState.Consumed)]
    [InlineData(StrategyCandidateState.Rejected)]
    public async Task Start_RetryAfterStateProgressDoesNotRevalidateOrRewrite(StrategyCandidateState state)
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var clock = new CandidateWorkflowTestClock(Now);
        var preview = await Preview(database, clock);
        var request = Request(preview);
        var first = await Workflow(database, clock).StartAsync(request);
        await using (var context = database.CreateDbContext())
        {
            var candidate = await context.Candidates.SingleAsync();
            candidate.State = state;
            candidate.Version = 4;
            await context.SaveChangesAsync();
        }
        var strictResolver = new Mock<IStrategyIdentityResolver>(MockBehavior.Strict);
        var restarted = new CandidateWorkflowService(database.Workflows, database.Queries(clock), strictResolver.Object, clock);
        var retry = await restarted.StartAsync(request);
        Assert.Equal(first.CandidateRunId, retry.CandidateRunId);
        Assert.Equal(state.ToString(), Assert.Single(retry.Candidates).State);
        Assert.Equal(4, retry.Candidates[0].Version);
        await using var check = database.CreateDbContext();
        Assert.Equal(1, await check.CandidateRunRequests.CountAsync());
        Assert.Equal(1, await check.ApplicationEvents.CountAsync(item => item.StreamName.StartsWith("candidate-runs/")));
    }

    [Fact]
    public async Task Start_ConcurrentNormalizedRetriesCreateOneCompleteRun()
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var clock = new CandidateWorkflowTestClock(Now);
        var preview = await Preview(database, clock);
        var request = Request(preview);
        var tasks = Enumerable.Range(0, 4).Select(index => Task.Run(() => Workflow(database, clock).StartAsync(request with
        {
            IdempotencyKey = index % 2 == 0 ? " start-test " : "start-test",
            Strategies = [Strategy with { ContentSha256 = index % 2 == 0 ? Strategy.ContentSha256.ToUpperInvariant() : Strategy.ContentSha256 }]
        })));
        var results = await Task.WhenAll(tasks);
        Assert.Single(results.Select(item => item.CandidateRunId).Distinct());
        Assert.All(results, item => Assert.Single(item.Candidates));
        await using var check = database.CreateDbContext();
        Assert.Equal(1, await check.CandidateRunRequests.CountAsync());
        Assert.Equal(1, await check.ProductionRuns.CountAsync());
        Assert.Equal(1, await check.Candidates.CountAsync());
    }

    [Fact]
    public async Task Start_WriteFailureRollsBackTheWholeStartAndRetryCanRecover()
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var clock = new CandidateWorkflowTestClock(Now);
        var preview = await Preview(database, clock);
        await using (var context = database.CreateDbContext())
            await context.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_candidate_start BEFORE INSERT ON candidates BEGIN SELECT RAISE(ABORT, 'injected candidate write failure'); END;");
        await Assert.ThrowsAnyAsync<Exception>(() => Workflow(database, clock).StartAsync(Request(preview)));
        await using (var context = database.CreateDbContext())
        {
            Assert.Empty(await context.CandidateRunRequests.ToArrayAsync());
            Assert.Empty(await context.ProductionRuns.ToArrayAsync());
            Assert.Empty(await context.Candidates.ToArrayAsync());
            Assert.False(await context.ApplicationEvents.AnyAsync(item => item.StreamName.StartsWith("candidate-runs/")));
            await context.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_candidate_start;");
        }
        var recovered = await Workflow(database, clock).StartAsync(Request(preview));
        Assert.Single(recovered.Candidates);
    }

    [Theory]
    [InlineData("expiry")]
    [InlineData("future")]
    [InlineData("members")]
    [InlineData("metadata")]
    public async Task Start_RejectsStaleFutureOrTamperedPreview(string defect)
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var clock = new CandidateWorkflowTestClock(Now);
        var preview = await Preview(database, clock);
        var request = Request(preview);
        if (defect == "expiry") clock.Now = Now.AddMinutes(4);
        if (defect == "future") request = request with { AsOfUtc = Now.AddSeconds(-1) };
        if (defect is "members" or "metadata")
        {
            await using var context = database.CreateDbContext();
            var record = await context.UniversePreviews.SingleAsync();
            if (defect == "metadata") record.ExpiresAtUtc = record.ExpiresAtUtc.AddMinutes(1);
            else record.PreviewJson = JsonSerializer.Serialize(preview with { Members = [preview.Members[0] with { Symbol = "OTHER" }] });
            await context.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => Workflow(database, clock).StartAsync(request));
        await using var check = database.CreateDbContext();
        Assert.Empty(await check.CandidateRunRequests.ToArrayAsync());
        Assert.Empty(await check.Candidates.ToArrayAsync());
    }

    [Fact]
    public async Task PreviewRepository_RejectsMetadataReplacementWithUnchangedHash()
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var clock = new CandidateWorkflowTestClock(Now);
        var preview = await Preview(database, clock);
        var persisted = await database.Workflows.GetAsync(preview.UniverseSnapshotId);
        persisted!.ExpiresAtUtc = persisted.ExpiresAtUtc.AddMinutes(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Workflows.SaveAsync(persisted));
    }

    [Fact]
    public async Task CandidateRepository_RejectsRunSnapshotRebinding()
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var clock = new CandidateWorkflowTestClock(Now);
        var preview = await Preview(database, clock);
        await Workflow(database, clock).StartAsync(Request(preview));
        await using var context = database.CreateDbContext();
        var run = await context.ProductionRuns.AsNoTracking().SingleAsync();
        var candidate = await context.Candidates.AsNoTracking().SingleAsync();
        run.UniverseSnapshotId = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Candidates.UpsertDiscoveryAsync(run, candidate));
    }

    internal static UniverseDiscoveryService Discovery(CandidateWorkflowTestDatabase database, CandidateWorkflowTestClock clock)
    {
        var source = new Mock<IUniverseSourceResolver>();
        source.Setup(item => item.ResolveAsync(It.IsAny<UniverseSourceSelectionRequest>(), "swing", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UniverseSourceCapture("wishlist", "watch", Now, Now.AddMinutes(3), [new DiscoverySymbolObservation("MSFT")]));
        return new UniverseDiscoveryService(source.Object, database.Discovery, database.Workflows, database.Workflows, clock);
    }
    internal static Task<UniversePreviewResponse> Preview(CandidateWorkflowTestDatabase database, CandidateWorkflowTestClock clock) =>
        Discovery(database, clock).PreviewAsync(new UniversePreviewRequest(ContractVersions.VersionOne, "swing", Now,
            [new UniverseSourceSelectionRequest("wishlist", "watch")]));
    internal static CandidateWorkflowService Workflow(CandidateWorkflowTestDatabase database, TimeProvider clock) =>
        new(database.Workflows, database.Queries(clock), Mock.Of<IStrategyIdentityResolver>(), clock);
    internal static CreateCandidateRunRequest Request(UniversePreviewResponse preview) =>
        new(ContractVersions.VersionOne, "start-test", "backtest", preview.UniverseSnapshotId, [Strategy], Now);
}
