using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TradingFlow.Application.Candidates;
using TradingFlow.Application.Jobs;
using TradingFlow.Contracts.V1;
using TradingFlow.Data.Jobs;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Discovery;
using TradingFlow.Domain.Earnings;
using TradingFlow.Domain.News;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Engine.Configuration;
using TradingFlow.Web.Services;
using TradingFlow.Web.Services.Discovery;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Tests;

public sealed class PaperMembershipWorkflowTests
{
    private const string Query = "f=sh_relvol_o2";
    private const string Alias = "Saved relative volume";
    private static readonly DateTimeOffset Now = CandidateAuditIntegrityTests.Now;

    // Preview separately records each selected source and resolves overlapping stocks only once.
    [Theory]
    [InlineData("group", false, 2, 1)]
    [InlineData("screen", false, 1, 1)]
    [InlineData("both", false, 2, 2)]
    [InlineData("both", true, 1, 2)]
    public async Task PaperPreview_RetainsCanonicalSelectionsAndEmptyGroup(string selection, bool emptyGroup, int memberCount, int sourceCount)
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var clock = new CandidateWorkflowTestClock(Now);
        var group = Guid.NewGuid();
        var groups = GroupSource(group, () => emptyGroup ? [] : ["MSFT", "AAPL"], clock);
        var screen = ScreenSource(clock);
        var request = ReviewRequest(selection, group);
        var preview = await PreviewService(database, clock, groups.Object, screen.Object).PreviewAsync(request);

        Assert.Equal(memberCount, preview.Members.Count);
        var review = Assert.IsType<PaperMembershipReview>(preview.PaperMembership);
        Assert.Equal(request.PaperMembershipMode, review.Mode);
        Assert.Equal(sourceCount, review.Sources.Count);
        if (selection != "screen")
        {
            var source = Assert.Single(review.Sources, item => item.SourceKind == "wishlist");
            Assert.Equal(group, source.WishlistId);
            Assert.Equal(emptyGroup ? 0 : 2, source.MemberCount);
        }
        if (selection != "group")
        {
            var source = Assert.Single(review.Sources, item => item.SourceKind == "finviz");
            Assert.Equal(Query, source.NormalizedQuery);
            Assert.Equal(64, source.QuerySha256!.Length);
            Assert.True(source.IsBoundedObservation);
            Assert.Equal("raw-archive:strict-fixture", source.ProviderReference);
        }
        Assert.Equal(selection == "both" && !emptyGroup ? 2 : 1,
            Assert.Single(preview.Members, item => item.Symbol == "MSFT").Sources.Count);
        var retained = await database.Workflows.GetAsync(preview.UniverseSnapshotId);
        Assert.Equal(preview.ContentSha256, UniverseDiscoveryService.ReadPreview(retained!, preview.UniverseSnapshotId).ContentSha256);
    }

    // A truly empty group can contribute a receipt, but cannot start an empty polling job.
    [Fact]
    public async Task PaperPreview_RefusesEntirelyEmptySelection()
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var clock = new CandidateWorkflowTestClock(Now);
        var group = Guid.NewGuid();
        var groups = GroupSource(group, () => [], clock);
        var previews = PreviewService(database, clock, groups.Object, Mock.Of<IScreenerSnapshotSource>());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => previews.PreviewAsync(ReviewRequest("group", group)));
        Assert.Contains("no unexpired stocks", error.Message, StringComparison.Ordinal);
        await using var context = database.CreateDbContext();
        Assert.Empty(await context.UniversePreviews.ToListAsync());
    }

    // Confirmation compares the reviewed receipt and survives a new service and elapsed preview expiry.
    [Fact]
    public async Task PaperStart_RejectsChangedReviewAndReplaysThePersistedCommand()
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var clock = new CandidateWorkflowTestClock(Now);
        var group = Guid.NewGuid();
        var groups = GroupSource(group, () => [], clock);
        var screens = ScreenSource(clock);
        var preview = await PreviewService(database, clock, groups.Object, screens.Object).PreviewAsync(ReviewRequest("both", group));
        var repository = new SqliteJobRepository(database);
        var configuration = new Mock<IWorkflowRunConfiguration>(MockBehavior.Strict);
        var strategies = new Mock<IStrategyIdentityResolver>();
        var identity = new StrategyReference("fixture.swing", "1.0.0", new string('a', 64));
        var request = new StartPaperRunRequest(ContractVersions.VersionOne, "paper_fixture", preview.UniverseSnapshotId, identity,
            false, false, "limit", "day", "strategy_validated", "paper-stock-review", "paper_experiment",
            PaperMembershipMode: PaperMembershipModes.RefreshSelectedSources, UniverseContentSha256: preview.ContentSha256);
        var service = new RunWorkflowService(repository, database.Workflows, strategies.Object, configuration.Object, clock,
            new(16, 16), SyntheticResearchQueue.For(repository));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(request with { PaperMembershipMode = PaperMembershipModes.FixedAtLaunch }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(request with { UniverseContentSha256 = new string('b', 64) }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(request with { UniverseSnapshotId = Guid.NewGuid() }));

        var path = Path.GetTempFileName();
        try
        {
            configuration.Setup(config => config.WriteAsync(request,
                    It.Is<UniversePreviewResponse>(value => value.ContentSha256 == preview.ContentSha256),
                    It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([path]);
            var launch = await service.StartAsync(request);
            Assert.Equal(preview.ContentSha256, launch.Provenance!.UniverseContentSha256);
            Assert.Equal(Query, Assert.Single(launch.Provenance.PaperMembership!.Sources, source => source.SourceKind == "finviz").NormalizedQuery);
            Assert.Equal(group, Assert.Single(launch.Provenance.PaperMembership.Sources, source => source.SourceKind == "wishlist").WishlistId);

            clock.Now = Now.AddMinutes(4);
            var restarted = new RunWorkflowService(new SqliteJobRepository(database), database.Workflows,
                Mock.Of<IStrategyIdentityResolver>(MockBehavior.Strict), Mock.Of<IWorkflowRunConfiguration>(MockBehavior.Strict),
                clock, new(16, 16), SyntheticResearchQueue.For(repository));
            Assert.Equal(launch.JobId, (await restarted.StartAsync(request)).JobId);
            await Assert.ThrowsAsync<IdempotencyKeyConflictException>(() => restarted.StartAsync(request with { UniverseContentSha256 = new string('b', 64) }));
            screens.Verify(source => source.ReadMembershipAsync(Alias, true, It.IsAny<CancellationToken>()), Times.Once);
            strategies.Verify(source => source.RequireSwingStrategyAsync(identity, "paper_experiment", It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Runtime uses the frozen ID and query and reads both again on startup, even before an old interval elapses.
    [Fact]
    public async Task PaperSession_RefreshesBoundSourcesAtSixtySecondsAndStartsWithAReadAfterRestart()
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var clock = new CandidateWorkflowTestClock(Now);
        var group = Guid.NewGuid();
        string[] groupSymbols = ["MSFT"];
        var groups = GroupSource(group, () => groupSymbols, clock);
        var screen = ScreenSource(clock);
        var config = new SimpleYamlReader().ReadBacktestRun(Path.Combine(TestRepository.FindRoot(), "configs", "paper", "alpaca-paper.yaml"));
        config = config with
        {
            Tickers = ["FIXTURE"],
            Discovery = DiscoveryRuntimeConfig.Disabled with { Enabled = true, WishlistId = group, FinvizQuery = Query }
        };
        var factory = new PaperDiscoverySessionFactory(database.Discovery, groups.Object, screen.Object, Mock.Of<INewsFeedRepository>(),
            Mock.Of<IEarningsRepository>(), new StockPulseReceiverService(clock), NullDiscoverySubscriptionSink.Instance,
            clock, NullLoggerFactory.Instance);
        var id = Guid.NewGuid();
        await using (var session = factory.Create(id, config, "swing"))
        {
            var first = Assert.Single((await session.RefreshAsync(default)).Members);
            Assert.Equal("MSFT", first.Symbol);
            Assert.Equal(2, first.Sources.Count);
            Assert.All(first.Sources, item => Assert.Equal(Now.AddSeconds(180), item.ExpiresAtUtc));

            groupSymbols = ["NVDA"];
            clock.Now = Now.AddSeconds(59);
            Assert.Equal(["MSFT"], (await session.RefreshAsync(default)).Symbols);
            clock.Now = Now.AddSeconds(60);
            var changed = await session.RefreshAsync(default);
            Assert.Equal(["NVDA"], changed.Added);
            Assert.Single(Assert.Single(changed.Members, item => item.Symbol == "MSFT").Sources);
        }

        clock.Now = Now.AddSeconds(61);
        groupSymbols = [];
        await using var restarted = factory.Create(id, config, "swing");
        var recovered = await restarted.RefreshAsync(default);
        Assert.Equal(["NVDA"], recovered.Dropped);
        Assert.Equal(["MSFT"], recovered.Symbols);
        screen.Verify(item => item.ReadMembershipAsync(Query, false, It.IsAny<CancellationToken>()), Times.Exactly(3));
        groups.Verify(item => item.GetByIdAsync(group, It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    // Resolves synthetic stocks through the production preview and persistence adapters.
    private static UniverseDiscoveryService PreviewService(CandidateWorkflowTestDatabase database, TimeProvider clock,
        IWishlistRepository groups, IScreenerSnapshotSource screener) =>
        new(new UniverseSourceResolver(groups, screener, clock), database.Discovery, database.Workflows, database.Workflows, clock);

    // Creates a review request with exactly the sources the simulated operator selected.
    private static UniversePreviewRequest ReviewRequest(string selection, Guid group)
    {
        var sources = new List<UniverseSourceSelectionRequest>();
        if (selection != "screen")
            sources.Add(new("wishlist", group.ToString("D"), group));
        if (selection != "group")
            sources.Add(new("finviz", Alias, Query: Alias));
        return new(ContractVersions.VersionOne, "swing", null, sources, PaperMembershipModes.RefreshSelectedSources);
    }

    // Provides an editable source with mutable synthetic membership and a stable group ID.
    private static Mock<IWishlistRepository> GroupSource(Guid id, Func<string[]> symbols, TimeProvider clock)
    {
        var groups = new Mock<IWishlistRepository>();
        groups.Setup(item => item.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(() => new Wishlist
        {
            Id = id,
            UpdatedAtUtc = clock.GetUtcNow(),
            Items = symbols().Select(symbol => new WishlistItem { Ticker = symbol }).ToArray()
        });
        return groups;
    }

    // Allows alias resolution only at preview and verifies that runtime uses the resulting query directly.
    private static Mock<IScreenerSnapshotSource> ScreenSource(TimeProvider clock)
    {
        var screen = new Mock<IScreenerSnapshotSource>(MockBehavior.Strict);
        screen.Setup(item => item.ReadMembershipAsync(Alias, true, It.IsAny<CancellationToken>())).ReturnsAsync(Observe);
        screen.Setup(item => item.ReadMembershipAsync(Query, false, It.IsAny<CancellationToken>())).ReturnsAsync(Observe);
        return screen;

        // Stamp the provider fixture at the actual synthetic read.
        ScreenerSyncResult Observe() => new(ScreenerScope.Swing, Alias, Query, ["MSFT"], [],
            clock.GetUtcNow(), null, null) { RawReference = "raw-archive:strict-fixture" };
    }
}
