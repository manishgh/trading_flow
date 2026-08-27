using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TradingFlow.Backtesting.Discovery;
using TradingFlow.Data.Context;
using TradingFlow.Data.Discovery;
using TradingFlow.Domain.Discovery;

namespace TradingFlow.Tests;

public sealed class DiscoveryRepositoryTests
{
    [Fact]
    public async Task ConcurrentIdenticalSnapshot_CreatesOneAggregateAndOneSourceMembership()
    {
        await using var database = await DiscoveryTestDatabase.CreateAsync();
        var repository = database.CreateRepository();
        var scopeId = Guid.NewGuid();
        var observationId = Guid.NewGuid();
        var observedAt = new DateTimeOffset(2026, 8, 27, 14, 0, 0, TimeSpan.Zero);
        var request = new DiscoverySnapshotRequest(
            scopeId,
            observationId,
            DiscoverySourceKinds.Wishlist,
            "momentum",
            "intraday",
            observedAt,
            observedAt.AddMinutes(3),
            0,
            [new DiscoverySymbolObservation(" rgti ")]);

        var commits = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => CommitWithBusyRetryAsync(repository, request)));

        Assert.Single(commits.Select(commit => commit.SnapshotId).Distinct());
        await using var context = database.CreateContext();
        Assert.Equal(1, await context.DiscoverySnapshots.CountAsync());
        Assert.Equal(1, await context.DiscoveryAggregates.CountAsync());
        Assert.Equal(1, await context.DiscoverySourceMemberships.CountAsync());
        Assert.Equal("RGTI", (await repository.GetActiveAsync(scopeId, observedAt, default)).Single().Symbol);
    }

    [Fact]
    public async Task MultipleSources_KeepOneAggregateAndDropOnlyAfterLastSourceDrops()
    {
        await using var database = await DiscoveryTestDatabase.CreateAsync();
        var repository = database.CreateRepository();
        var scopeId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 27, 14, 0, 0, TimeSpan.Zero);

        await repository.CommitSnapshotAsync(Request(
            scopeId, DiscoverySourceKinds.Wishlist, "momentum", now, ["POET"]));
        await repository.CommitSnapshotAsync(Request(
            scopeId, DiscoverySourceKinds.Finviz, "f=sh_relvol_o2", now, ["POET"]));

        var both = Assert.Single(await repository.GetActiveAsync(scopeId, now, default));
        Assert.Equal(2, both.Sources.Count);

        await repository.CommitSnapshotAsync(Request(
            scopeId, DiscoverySourceKinds.Finviz, "f=sh_relvol_o2", now.AddMinutes(1), [], expectedVersion: 1));
        var wishlistOnly = Assert.Single(await repository.GetActiveAsync(scopeId, now.AddMinutes(1), default));
        Assert.Single(wishlistOnly.Sources);
        Assert.Equal(DiscoverySourceKinds.Wishlist, wishlistOnly.Sources[0].SourceKind);

        var final = await repository.CommitSnapshotAsync(Request(
            scopeId, DiscoverySourceKinds.Wishlist, "momentum", now.AddMinutes(2), [], expectedVersion: 1));
        Assert.Equal(["POET"], final.Dropped);
        Assert.Empty(await repository.GetActiveAsync(scopeId, now.AddMinutes(2), default));
    }

    [Fact]
    public async Task DurableSession_FailedRefreshRetainsMembershipUntilTtlAndPublishesIncrementalDelta()
    {
        await using var database = await DiscoveryTestDatabase.CreateAsync();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 27, 14, 0, 0, TimeSpan.Zero));
        var source = new ScriptedDiscoverySource(
            [
                ["AAPL", "MSFT"],
                ["MSFT", "NVDA"]
            ],
            clock);
        var sink = new RecordingSubscriptionSink();
        await using var session = new DurableDiscoverySession(
            Guid.NewGuid(),
            database.CreateRepository(),
            [source],
            sink,
            clock,
            NullLogger<DurableDiscoverySession>.Instance);

        var first = await session.RefreshAsync(default);
        Assert.Equal(["AAPL", "MSFT"], first.Symbols);

        clock.Advance(TimeSpan.FromMinutes(1));
        var second = await session.RefreshAsync(default);
        Assert.Equal(["NVDA"], second.Added);
        Assert.Equal(["AAPL"], second.Dropped);
        Assert.Equal(["MSFT", "NVDA"], second.Symbols);

        source.Fail = true;
        clock.Advance(TimeSpan.FromMinutes(1));
        var retainedAfterFailure = await session.RefreshAsync(default);
        Assert.Equal(["MSFT", "NVDA"], retainedAfterFailure.Symbols);

        clock.Advance(TimeSpan.FromMinutes(3));
        var expired = await session.RefreshAsync(default);
        Assert.Empty(expired.Symbols);
        Assert.Equal(["MSFT", "NVDA"], expired.Dropped);
        Assert.Equal(4, sink.Snapshots.Count);
    }

    [Fact]
    public async Task DurableSession_DroppedDiscoveryKeepsConfirmedExposureSubscribed()
    {
        await using var database = await DiscoveryTestDatabase.CreateAsync();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 27, 14, 0, 0, TimeSpan.Zero));
        var source = new ScriptedDiscoverySource([["AAPL"], []], clock);
        var sink = new RecordingSubscriptionSink();
        await using var session = new DurableDiscoverySession(
            Guid.NewGuid(),
            database.CreateRepository(),
            [source],
            sink,
            clock,
            NullLogger<DurableDiscoverySession>.Instance);

        await session.RefreshAsync(default);
        await session.RetainExposureSymbolsAsync(["AAPL"], default);
        clock.Advance(TimeSpan.FromMinutes(1));
        var dropped = await session.RefreshAsync(default);

        Assert.Empty(dropped.Symbols);
        Assert.Equal(["AAPL"], dropped.Dropped);
        Assert.Equal(["AAPL"], sink.Snapshots[^1]);
    }

    [Fact]
    public async Task DurableSession_RestartReconstructsMembershipWithoutFalseDeltaUntilTtlExpires()
    {
        await using var database = await DiscoveryTestDatabase.CreateAsync();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 27, 14, 0, 0, TimeSpan.Zero));
        var scopeId = Guid.NewGuid();
        var initialSource = new ScriptedDiscoverySource([["AAPL"]], clock);
        await using (var initial = new DurableDiscoverySession(
                         scopeId,
                         database.CreateRepository(),
                         [initialSource],
                         new RecordingSubscriptionSink(),
                         clock,
                         NullLogger<DurableDiscoverySession>.Instance))
        {
            var first = await initial.RefreshAsync(default);
            Assert.Equal(["AAPL"], first.Added);
        }

        var restartedSource = new ScriptedDiscoverySource([], clock) { Fail = true };
        var restartedSink = new RecordingSubscriptionSink();
        await using var restarted = new DurableDiscoverySession(
            scopeId,
            database.CreateRepository(),
            [restartedSource],
            restartedSink,
            clock,
            NullLogger<DurableDiscoverySession>.Instance);

        clock.Advance(TimeSpan.FromMinutes(1));
        var recovered = await restarted.RefreshAsync(default);
        Assert.Equal(["AAPL"], recovered.Symbols);
        Assert.Empty(recovered.Added);
        Assert.Empty(recovered.Dropped);
        var sourceEvidence = Assert.Single(Assert.Single(recovered.Members).Sources);
        Assert.Equal(DiscoverySourceKinds.Finviz, sourceEvidence.SourceKind);
        Assert.Equal("test-screen", sourceEvidence.SourceKey);

        clock.Advance(TimeSpan.FromMinutes(3));
        var expired = await restarted.RefreshAsync(default);
        Assert.Empty(expired.Symbols);
        Assert.Equal(["AAPL"], expired.Dropped);
    }

    [Fact]
    public async Task ConcurrentExpiryAndFreshSnapshot_LeavesFreshMembershipActive()
    {
        await using var database = await DiscoveryTestDatabase.CreateAsync();
        var repository = database.CreateRepository();
        var scopeId = Guid.NewGuid();
        var firstSeen = new DateTimeOffset(2026, 8, 27, 14, 0, 0, TimeSpan.Zero);
        await repository.CommitSnapshotAsync(new DiscoverySnapshotRequest(
            scopeId,
            Guid.NewGuid(),
            DiscoverySourceKinds.Finviz,
            "screen",
            "intraday",
            firstSeen,
            firstSeen.AddMinutes(1),
            0,
            [new DiscoverySymbolObservation("AAPL")]));

        var refreshedAt = firstSeen.AddMinutes(2);
        var refresh = new DiscoverySnapshotRequest(
            scopeId,
            Guid.NewGuid(),
            DiscoverySourceKinds.Finviz,
            "screen",
            "intraday",
            refreshedAt,
            refreshedAt.AddMinutes(3),
            1,
            [new DiscoverySymbolObservation("AAPL")]);

        await Task.WhenAll(
            repository.ExpireAsync(scopeId, refreshedAt),
            CommitWithBusyRetryAsync(repository, refresh));

        var active = Assert.Single(await repository.GetActiveAsync(scopeId, refreshedAt, default));
        Assert.Equal("AAPL", active.Symbol);
        Assert.Equal(refreshedAt.AddMinutes(3), active.ExpiresAtUtc);
    }

    private static DiscoverySnapshotRequest Request(
        Guid scopeId,
        string sourceKind,
        string sourceKey,
        DateTimeOffset observedAt,
        IReadOnlyCollection<string> symbols,
        long expectedVersion = 0) =>
        new(
            scopeId,
            Guid.NewGuid(),
            sourceKind,
            sourceKey,
            "intraday",
            observedAt,
            observedAt.AddMinutes(3),
            expectedVersion,
            symbols.Select(symbol => new DiscoverySymbolObservation(symbol)).ToArray());

    private static async Task<DiscoverySnapshotCommit> CommitWithBusyRetryAsync(
        SqliteDiscoveryRepository repository,
        DiscoverySnapshotRequest request)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await repository.CommitSnapshotAsync(request);
            }
            catch (Microsoft.Data.Sqlite.SqliteException exception) when (
                attempt < 10 && exception.SqliteErrorCode is 5 or 6)
            {
                await Task.Delay(10 * (attempt + 1));
            }
        }
    }

    private sealed class ScriptedDiscoverySource(
        Queue<IReadOnlyCollection<string>> captures,
        TimeProvider clock) : IDiscoverySource
    {
        public ScriptedDiscoverySource(
            IEnumerable<IReadOnlyCollection<string>> captures,
            TimeProvider clock) : this(new Queue<IReadOnlyCollection<string>>(captures), clock)
        {
        }

        public bool Fail { get; set; }
        public string SourceKind => DiscoverySourceKinds.Finviz;
        public string SourceKey => "test-screen";
        public string Horizon => "intraday";
        public TimeSpan RefreshInterval => TimeSpan.FromMinutes(1);
        public TimeSpan TimeToLive => TimeSpan.FromMinutes(3);
        public bool IsDiagnostic => false;

        public Task<DiscoveryCapture> CaptureAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Fail)
            {
                throw new TimeoutException("Finviz timed out.");
            }

            var symbols = captures.Count > 0 ? captures.Dequeue() : Array.Empty<string>();
            return Task.FromResult(new DiscoveryCapture(
                Guid.NewGuid(),
                clock.GetUtcNow(),
                symbols.Select(symbol => new DiscoverySymbolObservation(symbol)).ToArray()));
        }
    }

    private sealed class RecordingSubscriptionSink : IDiscoverySubscriptionSink
    {
        public List<IReadOnlyList<string>> Snapshots { get; } = [];

        public Task ReplaceScopeAsync(Guid scopeId, IReadOnlyCollection<string> symbols, CancellationToken cancellationToken)
        {
            Snapshots.Add(symbols.OrderBy(symbol => symbol, StringComparer.Ordinal).ToArray());
            return Task.CompletedTask;
        }

        public Task RemoveScopeAsync(Guid scopeId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current = current.Add(duration);
    }

    private sealed class DiscoveryTestDatabase : IAsyncDisposable
    {
        private readonly string path;
        private readonly DbContextOptions<TradingFlowDbContext> options;

        private DiscoveryTestDatabase(string path, DbContextOptions<TradingFlowDbContext> options)
        {
            this.path = path;
            this.options = options;
        }

        public static async Task<DiscoveryTestDatabase> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), "tradingflow-discovery-tests", $"{Guid.NewGuid():N}.db");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var options = new DbContextOptionsBuilder<TradingFlowDbContext>()
                .UseSqlite($"Data Source={path};Default Timeout=5")
                .Options;
            await using var context = new TradingFlowDbContext(options);
            await context.Database.EnsureCreatedAsync();
            return new DiscoveryTestDatabase(path, options);
        }

        public SqliteDiscoveryRepository CreateRepository() => new(new TestDbContextFactory(options));
        public TradingFlowDbContext CreateContext() => new(options);

        public ValueTask DisposeAsync()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<TradingFlowDbContext> options)
        : IDbContextFactory<TradingFlowDbContext>
    {
        public TradingFlowDbContext CreateDbContext() => new(options);
    }
}
