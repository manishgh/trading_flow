using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Data.Market;

namespace TradingFlow.Tests;

public sealed class MarketStreamLeaseTests
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Acquire_ByCurrentOwner_IsIdempotentAndKeepsFencingToken()
    {
        await using var database = await LeaseTestDatabase.CreateAsync();
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 27, 16, 0, 0, TimeSpan.FromHours(2)));
        var repository = database.CreateRepository(clock);

        var first = await repository.TryAcquireAsync(
            "alpaca:sip", "pod-a", LeaseDuration);
        Assert.NotNull(first);
        clock.Advance(TimeSpan.FromSeconds(10));
        var reacquired = await repository.TryAcquireAsync(
            "alpaca:sip", "pod-a", LeaseDuration);
        Assert.NotNull(reacquired);

        Assert.Equal(1, first.FencingToken);
        Assert.Equal(first.FencingToken, reacquired.FencingToken);
        Assert.Equal(first.AcquiredAtUtc, reacquired.AcquiredAtUtc);
        Assert.Equal(clock.GetUtcNow().ToUniversalTime(), reacquired.RenewedAtUtc);
        Assert.Equal(TimeSpan.Zero, reacquired.ExpiresAtUtc.Offset);
        Assert.Equal(clock.GetUtcNow().ToUniversalTime().Add(LeaseDuration), reacquired.ExpiresAtUtc);
    }

    [Fact]
    public async Task Acquire_DifferentOwnerBeforeExpiry_IsRejected()
    {
        await using var database = await LeaseTestDatabase.CreateAsync();
        var clock = new ManualTimeProvider(UtcNow());
        var repository = database.CreateRepository(clock);

        var owner = await repository.TryAcquireAsync(
            "alpaca:sip", "pod-a", LeaseDuration);
        Assert.NotNull(owner);
        var contender = await repository.TryAcquireAsync(
            "alpaca:sip", "pod-b", LeaseDuration);

        Assert.Equal(1, owner.FencingToken);
        Assert.Null(contender);
    }

    [Fact]
    public async Task Renew_RequiresMatchingOwnerTokenAndUnexpiredLease()
    {
        await using var database = await LeaseTestDatabase.CreateAsync();
        var clock = new ManualTimeProvider(UtcNow());
        var repository = database.CreateRepository(clock);
        var lease = await repository.TryAcquireAsync(
            "alpaca:sip", "pod-a", LeaseDuration);
        Assert.NotNull(lease);

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Null(await repository.TryRenewAsync(
            "alpaca:sip", "pod-b", lease.FencingToken, LeaseDuration));
        Assert.Null(await repository.TryRenewAsync(
            "alpaca:sip", "pod-a", lease.FencingToken + 1, LeaseDuration));

        var renewed = await repository.TryRenewAsync(
            "alpaca:sip", "pod-a", lease.FencingToken, LeaseDuration);
        Assert.NotNull(renewed);
        Assert.Equal(clock.GetUtcNow(), renewed.RenewedAtUtc);
        Assert.Equal(clock.GetUtcNow().Add(LeaseDuration), renewed.ExpiresAtUtc);

        clock.Advance(LeaseDuration);
        Assert.Null(await repository.TryRenewAsync(
            "alpaca:sip", "pod-a", lease.FencingToken, LeaseDuration));
    }

    [Fact]
    public async Task TakeoverAfterExpiry_IncrementsTokenAndRejectsStaleOwnerActions()
    {
        await using var database = await LeaseTestDatabase.CreateAsync();
        var clock = new ManualTimeProvider(UtcNow());
        var repository = database.CreateRepository(clock);
        var first = await repository.TryAcquireAsync(
            "alpaca:sip", "pod-a", LeaseDuration);
        Assert.NotNull(first);

        clock.Advance(LeaseDuration);
        var successor = await repository.TryAcquireAsync(
            "alpaca:sip", "pod-b", LeaseDuration);
        Assert.NotNull(successor);

        Assert.Equal(first.FencingToken + 1, successor.FencingToken);
        Assert.Null(await repository.TryRenewAsync(
            "alpaca:sip", "pod-a", first.FencingToken, LeaseDuration));
        Assert.False(await repository.TryReleaseAsync(
            "alpaca:sip", "pod-a", first.FencingToken));

        Assert.True(await repository.TryReleaseAsync(
            "alpaca:sip", "pod-b", successor.FencingToken));
        var third = await repository.TryAcquireAsync(
            "alpaca:sip", "pod-c", LeaseDuration);
        Assert.NotNull(third);
        Assert.Equal(successor.FencingToken + 1, third.FencingToken);
    }

    [Fact]
    public async Task ConcurrentAcquire_AllowsExactlyOneOwner()
    {
        await using var database = await LeaseTestDatabase.CreateAsync();
        var clock = new ManualTimeProvider(UtcNow());
        var repositories = Enumerable.Range(0, 16)
            .Select(_ => database.CreateRepository(clock))
            .ToArray();

        var attempts = await Task.WhenAll(repositories.Select((repository, index) =>
            repository.TryAcquireAsync(
                "alpaca:sip",
                $"pod-{index}",
                LeaseDuration)));

        var winner = Assert.Single(attempts.OfType<TradingFlow.Domain.Market.MarketStreamLease>());
        Assert.Equal(1, winner.FencingToken);
        await using var context = database.CreateContext();
        var row = Assert.Single(await context.MarketStreamLeases.AsNoTracking().ToListAsync());
        Assert.Equal(winner.OwnerId, row.OwnerId);
        Assert.Equal(winner.FencingToken, row.FencingToken);
    }

    [Fact]
    public async Task ExpiredCurrentOwner_ReacquiresAsNewFencedTenure()
    {
        await using var database = await LeaseTestDatabase.CreateAsync();
        var clock = new ManualTimeProvider(UtcNow());
        var repository = database.CreateRepository(clock);
        var first = await repository.TryAcquireAsync(
            "alpaca:sip", "pod-a", LeaseDuration);
        Assert.NotNull(first);

        clock.Advance(LeaseDuration);
        var second = await repository.TryAcquireAsync(
            "alpaca:sip", "pod-a", LeaseDuration);
        Assert.NotNull(second);

        Assert.Equal(first.FencingToken + 1, second.FencingToken);
        Assert.Equal(clock.GetUtcNow(), second.AcquiredAtUtc);
    }

    private static DateTimeOffset UtcNow() =>
        new(2026, 8, 27, 14, 0, 0, TimeSpan.Zero);

    private sealed class ManualTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = initialUtcNow.ToUniversalTime();

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow = utcNow.Add(duration);
    }

    private sealed class LeaseTestDatabase : IAsyncDisposable
    {
        private readonly string path;
        private readonly DbContextOptions<TradingFlowDbContext> options;

        private LeaseTestDatabase(string path, DbContextOptions<TradingFlowDbContext> options)
        {
            this.path = path;
            this.options = options;
        }

        public static async Task<LeaseTestDatabase> CreateAsync()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "tradingflow-market-stream-lease-tests",
                $"{Guid.NewGuid():N}.db");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var options = new DbContextOptionsBuilder<TradingFlowDbContext>()
                .UseSqlite($"Data Source={path};Default Timeout=5")
                .Options;
            await using var context = new TradingFlowDbContext(options);
            await context.Database.EnsureCreatedAsync();
            return new LeaseTestDatabase(path, options);
        }

        public SqliteMarketStreamLeaseRepository CreateRepository(TimeProvider timeProvider) =>
            new(new TestDbContextFactory(options), timeProvider);

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
