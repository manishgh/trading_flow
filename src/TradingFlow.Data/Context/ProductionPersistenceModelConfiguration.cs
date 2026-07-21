using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Data.Context;

internal static class ProductionPersistenceModelConfiguration
{
    public static void ConfigureProductionPersistence(this ModelBuilder modelBuilder)
    {
        ConfigureRuns(modelBuilder.Entity<ProductionRun>());
        ConfigureOrderIntents(modelBuilder.Entity<OrderIntentRecord>());
        ConfigureOrderEvents(modelBuilder.Entity<OrderEventRecord>());
        ConfigureGateEvaluations(modelBuilder.Entity<GateEvaluationRecord>());
        ConfigureRiskEvents(modelBuilder.Entity<RiskEventRecord>());
        ConfigureKillSwitchEvents(modelBuilder.Entity<KillSwitchEventRecord>());
        ConfigureReconciliations(modelBuilder.Entity<ReconciliationRecord>());
        ConfigurePositionEvents(modelBuilder.Entity<PositionEventRecord>());
        ConfigureCandidates(modelBuilder.Entity<CandidateRecord>());
        ConfigureCatalystResults(modelBuilder.Entity<CatalystResultRecord>());

        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(entityType => typeof(OperationalRecord).IsAssignableFrom(entityType.ClrType)))
        {
            foreach (var property in entityType.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }
        }
    }

    private static void ConfigureRuns(EntityTypeBuilder<ProductionRun> entity)
    {
        entity.ToTable("runs");
        entity.HasKey(record => record.RunId);
        ConfigureProvenance(entity, includeRunForeignKey: false);
        entity.Property(record => record.Profile).HasMaxLength(20).IsRequired();
        entity.Property(record => record.Status).HasMaxLength(30).IsRequired();
        entity.HasIndex(record => record.StartedAtUtc);
        entity.HasIndex(record => record.Status);
    }

    private static void ConfigureOrderIntents(EntityTypeBuilder<OrderIntentRecord> entity)
    {
        entity.ToTable("order_intents");
        entity.HasKey(record => record.IntentId);
        ConfigureProvenance(entity);
        entity.Property(record => record.ClientOrderId).HasMaxLength(100).IsRequired();
        entity.Property(record => record.StrategyId).HasMaxLength(120).IsRequired();
        entity.Property(record => record.Symbol).HasMaxLength(20).IsRequired();
        entity.Property(record => record.Side).HasMaxLength(10).IsRequired();
        entity.Property(record => record.OrderType).HasMaxLength(30).IsRequired();
        entity.Property(record => record.TimeInForce).HasMaxLength(10).IsRequired();
        entity.Property(record => record.RequestJson).IsRequired();
        entity.HasIndex(record => record.ClientOrderId).IsUnique();
        entity.HasIndex(record => new
        {
            record.StrategyId,
            record.Side,
            record.Symbol,
            record.SessionDate,
            record.SequenceNumber
        }).IsUnique();
        entity.HasIndex(record => record.CandidateId);
        entity.HasIndex(record => new { record.Symbol, record.CreatedAtUtc });
    }

    private static void ConfigureOrderEvents(EntityTypeBuilder<OrderEventRecord> entity)
    {
        entity.ToTable("order_events");
        entity.HasKey(record => record.EventId);
        ConfigureProvenance(entity);
        entity.Property(record => record.ClientOrderId).HasMaxLength(100).IsRequired();
        entity.Property(record => record.BrokerOrderId).HasMaxLength(100);
        entity.Property(record => record.PreviousState).HasMaxLength(30);
        entity.Property(record => record.NewState).HasMaxLength(30).IsRequired();
        entity.Property(record => record.Source).HasMaxLength(30).IsRequired();
        entity.Property(record => record.PayloadJson).IsRequired();
        entity.HasIndex(record => new { record.ClientOrderId, record.LocalTimestampUtc });
        entity.HasIndex(record => record.BrokerOrderId);
    }

    private static void ConfigureGateEvaluations(EntityTypeBuilder<GateEvaluationRecord> entity)
    {
        entity.ToTable("gate_evaluations");
        entity.HasKey(record => record.EvaluationId);
        ConfigureProvenance(entity);
        entity.Property(record => record.ClientOrderId).HasMaxLength(100);
        entity.Property(record => record.GateName).HasMaxLength(80).IsRequired();
        entity.Property(record => record.RejectCode).HasConversion<string>().HasMaxLength(80);
        entity.Property(record => record.InputsJson).IsRequired();
        entity.HasIndex(record => new { record.CandidateId, record.GateOrder });
        entity.HasIndex(record => record.RejectCode);
    }

    private static void ConfigureRiskEvents(EntityTypeBuilder<RiskEventRecord> entity)
    {
        entity.ToTable("risk_events");
        entity.HasKey(record => record.RiskEventId);
        ConfigureProvenance(entity);
        entity.Property(record => record.EventType).HasMaxLength(80).IsRequired();
        entity.Property(record => record.Severity).HasMaxLength(20).IsRequired();
        entity.Property(record => record.Symbol).HasMaxLength(20);
        entity.Property(record => record.DetailsJson).IsRequired();
        entity.HasIndex(record => record.OccurredAtUtc);
        entity.HasIndex(record => new { record.EventType, record.Severity });
    }

    private static void ConfigureKillSwitchEvents(EntityTypeBuilder<KillSwitchEventRecord> entity)
    {
        entity.ToTable("kill_switch_events");
        entity.HasKey(record => record.KillSwitchEventId);
        ConfigureProvenance(entity);
        entity.Property(record => record.SwitchType).HasMaxLength(80).IsRequired();
        entity.Property(record => record.Transition).HasMaxLength(30).IsRequired();
        entity.Property(record => record.Actor).HasMaxLength(120);
        entity.Property(record => record.Reason).HasMaxLength(1000).IsRequired();
        entity.Property(record => record.DetailsJson).IsRequired();
        entity.HasIndex(record => new { record.SwitchType, record.OccurredAtUtc });
    }

    private static void ConfigureReconciliations(EntityTypeBuilder<ReconciliationRecord> entity)
    {
        entity.ToTable("reconciliations");
        entity.HasKey(record => record.ReconciliationId);
        ConfigureProvenance(entity);
        entity.Property(record => record.Status).HasMaxLength(40).IsRequired();
        entity.Property(record => record.BrokerSnapshotJson).IsRequired();
        entity.Property(record => record.LocalSnapshotJson).IsRequired();
        entity.Property(record => record.DiffJson).IsRequired();
        entity.Property(record => record.DiffHash).HasMaxLength(64).IsRequired();
        entity.Property(record => record.AcknowledgedBy).HasMaxLength(120);
        entity.Property(record => record.AcknowledgementReason).HasMaxLength(1000);
        entity.HasIndex(record => record.StartedAtUtc);
        entity.HasIndex(record => record.Status);
        entity.HasIndex(record => record.RequiresAcknowledgement)
            .HasFilter("requires_acknowledgement = 1")
            .IsUnique();
    }

    private static void ConfigurePositionEvents(EntityTypeBuilder<PositionEventRecord> entity)
    {
        entity.ToTable("position_events");
        entity.HasKey(record => record.PositionEventId);
        ConfigureProvenance(entity);
        entity.Property(record => record.Symbol).HasMaxLength(20).IsRequired();
        entity.Property(record => record.Side).HasMaxLength(10).IsRequired();
        entity.Property(record => record.BrokerOrderId).HasMaxLength(100).IsRequired();
        entity.Property(record => record.ClientOrderId).HasMaxLength(100).IsRequired();
        entity.Property(record => record.ExecutionId).HasMaxLength(160).IsRequired();
        entity.Property(record => record.Source).HasMaxLength(30).IsRequired();
        entity.Property(record => record.PayloadJson).IsRequired();
        entity.HasIndex(record => record.ExecutionId).IsUnique();
        entity.HasIndex(record => new { record.Symbol, record.PositionEventId });
        entity.HasIndex(record => record.BrokerOrderId);
    }

    private static void ConfigureCandidates(EntityTypeBuilder<CandidateRecord> entity)
    {
        entity.ToTable("candidates");
        entity.HasKey(record => record.CandidateId);
        ConfigureProvenance(entity);
        entity.Property(record => record.Symbol).HasMaxLength(20).IsRequired();
        entity.Property(record => record.DiscoverySource).HasMaxLength(80).IsRequired();
        entity.Property(record => record.FinvizPreset).HasMaxLength(120).IsRequired();
        entity.Property(record => record.Horizon).HasMaxLength(20).IsRequired();
        entity.Property(record => record.SelectedStrategy).HasMaxLength(120);
        entity.Property(record => record.State).HasMaxLength(40).IsRequired();
        entity.Property(record => record.SetupScoresJson).IsRequired();
        entity.Property(record => record.RejectReasonsJson).IsRequired();
        entity.HasIndex(record => new { record.Symbol, record.DiscoveredAtUtc });
        entity.HasIndex(record => new { record.Horizon, record.State });
        entity.HasIndex(record => record.CatalystResultId);
    }

    private static void ConfigureCatalystResults(EntityTypeBuilder<CatalystResultRecord> entity)
    {
        entity.ToTable("catalyst_results");
        entity.HasKey(record => record.CatalystResultId);
        ConfigureProvenance(entity);
        entity.Property(record => record.ProviderArticleId).HasMaxLength(500).IsRequired();
        entity.Property(record => record.Symbol).HasMaxLength(20).IsRequired();
        entity.Property(record => record.Category).HasMaxLength(80).IsRequired();
        entity.Property(record => record.Direction).HasMaxLength(20).IsRequired();
        entity.Property(record => record.RawComponentsJson).IsRequired();
        entity.Property(record => record.PenaltiesJson).IsRequired();
        entity.Property(record => record.DedupEvidenceJson).IsRequired();
        entity.Property(record => record.Stage1ProvenanceJson).IsRequired();
        entity.Property(record => record.ModelId).HasMaxLength(200);
        entity.Property(record => record.PromptVersion).HasMaxLength(100);
        entity.HasIndex(record => new { record.ProviderArticleId, record.Symbol });
        entity.HasIndex(record => record.CandidateId);
        entity.HasIndex(record => record.EvaluatedAtUtc);
    }

    private static void ConfigureProvenance<T>(
        EntityTypeBuilder<T> entity,
        bool includeRunForeignKey = true)
        where T : OperationalRecord
    {
        entity.Property(record => record.ConfigHash).HasMaxLength(64).IsRequired();
        entity.Property(record => record.CodeVersion).HasMaxLength(64).IsRequired();
        entity.Property(record => record.SchemaVersion).IsRequired();
        if (includeRunForeignKey)
        {
            entity.HasOne<ProductionRun>()
                .WithMany()
                .HasForeignKey(record => record.RunId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(record => record.RunId);
        }
    }

    private static string ToSnakeCase(string value)
    {
        var output = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsUpper(character) && index > 0)
            {
                output.Append('_');
            }

            output.Append(char.ToLowerInvariant(character));
        }

        return output.ToString();
    }
}
