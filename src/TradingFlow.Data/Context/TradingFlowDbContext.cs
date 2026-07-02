using Microsoft.EntityFrameworkCore;
using TradingFlow.Domain.Locking;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Wishlists;

namespace TradingFlow.Data.Context;

public sealed class TradingFlowDbContext : DbContext
{
    public TradingFlowDbContext(DbContextOptions<TradingFlowDbContext> options) : base(options)
    {
    }

    public DbSet<TickerLockEntity> TickerLocks => Set<TickerLockEntity>();
    public DbSet<PersistedOrder> Orders => Set<PersistedOrder>();

    public DbSet<TradingFlow.Domain.Audit.DecisionAuditRecord> DecisionAudits => Set<TradingFlow.Domain.Audit.DecisionAuditRecord>();
    public DbSet<TradingFlow.Domain.Jobs.PersistedJob> Jobs => Set<TradingFlow.Domain.Jobs.PersistedJob>();
    public DbSet<TradingFlow.Domain.News.PersistedNewsItem> NewsItems => Set<TradingFlow.Domain.News.PersistedNewsItem>();
    public DbSet<Wishlist> Wishlists => Set<Wishlist>();
    public DbSet<WishlistItem> WishlistItems => Set<WishlistItem>();
    public DbSet<WishlistSignal> WishlistSignals => Set<WishlistSignal>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TickerLockEntity>(entity =>
        {
            entity.HasKey(e => e.Ticker);
            entity.Property(e => e.Ticker).HasMaxLength(50);
            entity.Property(e => e.PodId).HasMaxLength(100).IsRequired();
            entity.HasIndex(e => e.ExpiresAt);
        });

        modelBuilder.Entity<PersistedOrder>(entity =>
        {
            entity.HasKey(e => e.OrderId);
            entity.Property(e => e.OrderId).HasMaxLength(100);
            entity.Property(e => e.Ticker).HasMaxLength(50).IsRequired();
            entity.Property(e => e.RunName).HasMaxLength(100).IsRequired();
            entity.Property(e => e.ClientOrderId).HasMaxLength(100).IsRequired();
            entity.Property(e => e.StrategyName).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(50).IsRequired();

            entity.HasIndex(e => e.Ticker);
            entity.HasIndex(e => e.RunName);
            entity.HasIndex(e => e.ClientOrderId);
            entity.HasIndex(e => e.Status);
        });

        modelBuilder.Entity<TradingFlow.Domain.Audit.DecisionAuditRecord>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.RunName).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Ticker).HasMaxLength(50).IsRequired();
            entity.Property(e => e.StrategyName).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Decision).HasMaxLength(50).IsRequired();

            entity.HasIndex(e => e.RunName);
            entity.HasIndex(e => e.Ticker);
        });

        modelBuilder.Entity<TradingFlow.Domain.Jobs.PersistedJob>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.RunName).HasMaxLength(100).IsRequired();
            entity.Property(e => e.ConfigPath).HasMaxLength(500).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(50).IsRequired();

            entity.HasIndex(e => e.RunName).IsUnique();
            entity.HasIndex(e => e.Status);
        });

        modelBuilder.Entity<TradingFlow.Domain.News.PersistedNewsItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(500);
            entity.Property(e => e.Ticker).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Headline).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.Provider).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Source).HasMaxLength(200);
            entity.Property(e => e.Url).HasMaxLength(1000);

            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => e.Ticker);
            entity.HasIndex(e => e.Provider);
        });

        modelBuilder.Entity<Wishlist>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(120).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.HasIndex(e => e.Name).IsUnique();
            entity.HasIndex(e => e.IsDefault);
            entity.HasIndex(e => e.IsObserved);
        });

        modelBuilder.Entity<WishlistItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Ticker).HasMaxLength(50).IsRequired();
            entity.Property(e => e.DisplayName).HasMaxLength(200);
            entity.Property(e => e.Notes).HasMaxLength(1000);
            entity.HasIndex(e => new { e.WishlistId, e.Ticker }).IsUnique();
            entity.HasIndex(e => e.Ticker);
            entity.HasOne(e => e.Wishlist)
                .WithMany(e => e.Items)
                .HasForeignKey(e => e.WishlistId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WishlistSignal>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Ticker).HasMaxLength(50).IsRequired();
            entity.Property(e => e.SignalType).HasMaxLength(80).IsRequired();
            entity.Property(e => e.Severity).HasMaxLength(30).IsRequired();
            entity.Property(e => e.Reason).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.NewsHeadline).HasMaxLength(1000);
            entity.Property(e => e.NewsUrl).HasMaxLength(1000);
            entity.Property(e => e.NewsProvider).HasMaxLength(100);
            entity.HasIndex(e => e.WishlistId);
            entity.HasIndex(e => e.Ticker);
            entity.HasIndex(e => e.DetectedAtUtc);
            entity.HasOne(e => e.Wishlist)
                .WithMany()
                .HasForeignKey(e => e.WishlistId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

