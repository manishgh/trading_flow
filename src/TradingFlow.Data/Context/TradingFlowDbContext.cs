using Microsoft.EntityFrameworkCore;
using TradingFlow.Domain.Locking;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Domain.Earnings;

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
    public DbSet<ProductionRun> ProductionRuns => Set<ProductionRun>();
    public DbSet<OrderIntentRecord> OrderIntents => Set<OrderIntentRecord>();
    public DbSet<OrderEventRecord> OrderEvents => Set<OrderEventRecord>();
    public DbSet<GateEvaluationRecord> GateEvaluations => Set<GateEvaluationRecord>();
    public DbSet<RiskEventRecord> RiskEvents => Set<RiskEventRecord>();
    public DbSet<KillSwitchEventRecord> KillSwitchEvents => Set<KillSwitchEventRecord>();
    public DbSet<ReconciliationRecord> Reconciliations => Set<ReconciliationRecord>();
    public DbSet<PositionEventRecord> PositionEvents => Set<PositionEventRecord>();
    public DbSet<CandidateRecord> Candidates => Set<CandidateRecord>();
    public DbSet<CatalystResultRecord> CatalystResults => Set<CatalystResultRecord>();
    public DbSet<EarningsCalendarEvent> EarningsCalendarEvents => Set<EarningsCalendarEvent>();
    public DbSet<EarningsAnalysisSnapshot> EarningsAnalysisSnapshots => Set<EarningsAnalysisSnapshot>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ConfigureProductionPersistence();

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
            entity.Property(e => e.Timestamp).HasConversion(
                value => value.UtcDateTime,
                value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)));
            entity.Property(e => e.IngestedAt).HasConversion(
                value => value.UtcDateTime,
                value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)));

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

        modelBuilder.Entity<EarningsCalendarEvent>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasMaxLength(80);
            entity.Property(item => item.Ticker).HasMaxLength(20).IsRequired();
            entity.Property(item => item.CompanyName).HasMaxLength(300).IsRequired();
            entity.Property(item => item.ReleaseWindow).HasConversion<string>().HasMaxLength(40);
            entity.Property(item => item.Provider).HasMaxLength(40).IsRequired();
            entity.Property(item => item.SourceUrl).HasMaxLength(1000).IsRequired();
            entity.Property(item => item.SourceArtifactSha256).HasMaxLength(64).IsRequired();
            entity.HasIndex(item => new { item.ReportDateExchange, item.ScheduledAtUtc });
            entity.HasIndex(item => item.Ticker);
            entity.HasIndex(item => item.Provider);
        });

        modelBuilder.Entity<EarningsAnalysisSnapshot>(entity =>
        {
            entity.HasKey(snapshot => snapshot.Id);
            entity.Property(snapshot => snapshot.EarningsEventId).HasMaxLength(80).IsRequired();
            entity.Property(snapshot => snapshot.Ticker).HasMaxLength(20).IsRequired();
            entity.Property(snapshot => snapshot.ResultAssessment).HasConversion<string>().HasMaxLength(30);
            entity.Property(snapshot => snapshot.BreakoutAssessment).HasConversion<string>().HasMaxLength(30);
            entity.Property(snapshot => snapshot.Reason).HasMaxLength(1500).IsRequired();
            entity.Property(snapshot => snapshot.NewsHeadline).HasMaxLength(1000);
            entity.Property(snapshot => snapshot.NewsUrl).HasMaxLength(1000);
            entity.Property(snapshot => snapshot.NewsProvider).HasMaxLength(100);
            entity.HasIndex(snapshot => new { snapshot.EarningsEventId, snapshot.AnalyzedAtUtc });
            entity.HasIndex(snapshot => new { snapshot.Ticker, snapshot.AnalyzedAtUtc });
            entity.HasOne<EarningsCalendarEvent>()
                .WithMany()
                .HasForeignKey(snapshot => snapshot.EarningsEventId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

