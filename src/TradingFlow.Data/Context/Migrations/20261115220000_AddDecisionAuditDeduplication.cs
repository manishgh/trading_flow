using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TradingFlow.Data.Context.Migrations;

[DbContext(typeof(TradingFlowDbContext))]
[Migration("20261115220000_AddDecisionAuditDeduplication")]
public sealed class AddDecisionAuditDeduplication : Migration
{
    // Preserve all existing rows while allowing new keyed observations to survive polling/restart retries once.
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "DeduplicationKey", table: "DecisionAudits",
            type: "TEXT", maxLength: 64, nullable: true);
        migrationBuilder.CreateIndex(name: "IX_DecisionAudits_DeduplicationKey", table: "DecisionAudits",
            column: "DeduplicationKey", unique: true);
        migrationBuilder.AddColumn<string>(name: "EntryWindowJson", table: "order_admission_telemetry",
            type: "TEXT", nullable: true);
    }

    // Remove only this migration's schema when explicitly rolling back a verification database.
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_DecisionAudits_DeduplicationKey", table: "DecisionAudits");
        migrationBuilder.DropColumn(name: "DeduplicationKey", table: "DecisionAudits");
        migrationBuilder.DropColumn(name: "EntryWindowJson", table: "order_admission_telemetry");
    }
}
