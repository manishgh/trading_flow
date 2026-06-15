using Microsoft.EntityFrameworkCore;
using TradingFlow.Domain.Locking;
using TradingFlow.Domain.Orders;

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
    }
}
