using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Data.Wishlists;
using TradingFlow.Domain.Wishlists;

namespace TradingFlow.Tests;

public sealed class WishlistRepositoryTests
{
    [Fact]
    public async Task SaveAndListAsync_PreservesNamedWishlistAndDefaultOwnership()
    {
        await using var database = await WishlistTestDatabase.CreateAsync();
        var repository = database.CreateRepository();

        var primary = await repository.SaveAsync(new Wishlist
        {
            Name = "  Momentum Watch  ",
            Description = "Small-cap runners",
            IsDefault = true,
            IncludeExtendedHours = true
        }, CancellationToken.None);
        await repository.SaveAsync(new Wishlist
        {
            Name = "Swing Candidates",
            IsDefault = true,
            IncludeExtendedHours = true
        }, CancellationToken.None);

        var lists = await repository.ListAsync(CancellationToken.None);

        Assert.Equal(2, lists.Count);
        Assert.Single(lists, list => list.IsDefault);
        Assert.Equal("Swing Candidates", lists.Single(list => list.IsDefault).Name);
        Assert.Contains(lists, list => list.Id == primary.Id && list.Name == "Momentum Watch" && list.IncludeExtendedHours);
    }

    [Fact]
    public async Task AddRemoveAndReactivateItemAsync_NormalizesTickerWithoutDuplicatingRows()
    {
        await using var database = await WishlistTestDatabase.CreateAsync();
        var repository = database.CreateRepository();
        var wishlist = await repository.SaveAsync(new Wishlist { Name = "Day Trade", IncludeExtendedHours = true }, CancellationToken.None);

        await repository.AddOrUpdateItemAsync(wishlist.Id, " rgti ", "Rigetti", "volatile", CancellationToken.None);
        await repository.RemoveItemAsync(wishlist.Id, "RGTI", CancellationToken.None);
        var reactivated = await repository.AddOrUpdateItemAsync(wishlist.Id, "rgti", null, "back on watch", CancellationToken.None);
        var loaded = await repository.GetByIdAsync(wishlist.Id, CancellationToken.None);

        Assert.Equal("RGTI", reactivated.Ticker);
        Assert.NotNull(loaded);
        var item = Assert.Single(loaded!.Items);
        Assert.True(item.Active);
        Assert.Equal("Rigetti", item.DisplayName);
        Assert.Equal("back on watch", item.Notes);
    }

    [Fact]
    public async Task SignalsAsync_AreQueryableByWishlistTickerAndTime()
    {
        await using var database = await WishlistTestDatabase.CreateAsync();
        var repository = database.CreateRepository();
        var wishlist = await repository.SaveAsync(new Wishlist { Name = "Breakouts", IncludeExtendedHours = true }, CancellationToken.None);
        var otherWishlist = await repository.SaveAsync(new Wishlist { Name = "Other", IncludeExtendedHours = true }, CancellationToken.None);
        var now = DateTimeOffset.UtcNow;

        var signal = await repository.AddSignalAsync(new WishlistSignal
        {
            WishlistId = wishlist.Id,
            Ticker = " poet ",
            SignalType = "breakout",
            Severity = "Warning",
            DetectedAtUtc = now,
            Price = 14.25m,
            Reason = "Price reclaimed VWAP with rising cumulative volume.",
            SnapshotJson = "{\"rvol\":2.1}"
        }, CancellationToken.None);
        await repository.AddSignalAsync(new WishlistSignal
        {
            WishlistId = wishlist.Id,
            Ticker = "MXL",
            SignalType = "breakout",
            DetectedAtUtc = now,
            Price = 90m,
            Reason = "Different ticker",
            SnapshotJson = "{}"
        }, CancellationToken.None);
        await repository.AddSignalAsync(new WishlistSignal
        {
            WishlistId = otherWishlist.Id,
            Ticker = "POET",
            SignalType = "breakout",
            DetectedAtUtc = now,
            Price = 15m,
            Reason = "Different wishlist",
            SnapshotJson = "{}"
        }, CancellationToken.None);
        await repository.AddSignalAsync(new WishlistSignal
        {
            WishlistId = wishlist.Id,
            Ticker = "POET",
            SignalType = "breakout",
            DetectedAtUtc = now.AddHours(-2),
            Price = 13m,
            Reason = "Too old",
            SnapshotJson = "{}"
        }, CancellationToken.None);

        var signals = await repository.GetSignalsAsync(wishlist.Id, "POET", now.AddMinutes(-1), 10, CancellationToken.None);
        await repository.AcknowledgeSignalAsync(signal.Id, CancellationToken.None);
        var acknowledged = await repository.GetSignalsAsync(wishlist.Id, "POET", now.AddMinutes(-1), 10, CancellationToken.None);

        var loaded = Assert.Single(signals);
        Assert.Equal("POET", loaded.Ticker);
        Assert.Equal("warning", loaded.Severity);
        Assert.Equal(14.25m, loaded.Price);
        Assert.True(Assert.Single(acknowledged).Acknowledged);
    }

    private sealed class WishlistTestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly DbContextOptions<TradingFlowDbContext> options;

        private WishlistTestDatabase(SqliteConnection connection, DbContextOptions<TradingFlowDbContext> options)
        {
            this.connection = connection;
            this.options = options;
        }

        public static async Task<WishlistTestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<TradingFlowDbContext>()
                .UseSqlite(connection)
                .Options;
            await using var db = new TradingFlowDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new WishlistTestDatabase(connection, options);
        }

        public SqliteWishlistRepository CreateRepository() => new(new TestDbContextFactory(options));

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }

    private sealed class TestDbContextFactory : IDbContextFactory<TradingFlowDbContext>
    {
        private readonly DbContextOptions<TradingFlowDbContext> options;

        public TestDbContextFactory(DbContextOptions<TradingFlowDbContext> options)
        {
            this.options = options;
        }

        public TradingFlowDbContext CreateDbContext() => new(options);
    }
}

