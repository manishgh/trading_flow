using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TradingFlow.Data.Context.Migrations;

[DbContext(typeof(TradingFlowDbContext))]
[Migration("20261115190000_AddOrderExecutionTelemetry")]
public sealed class AddOrderExecutionTelemetry : Migration
{
    // Add immutable observations alongside durable order intents and their existing dispatch fence.
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "order_admission_telemetry",
            columns: table => new
            {
                IntentId = table.Column<Guid>(type: "TEXT", nullable: false),
                Bid = table.Column<decimal>(type: "TEXT", nullable: true),
                Ask = table.Column<decimal>(type: "TEXT", nullable: true),
                QuoteFeed = table.Column<string>(type: "TEXT", nullable: true),
                QuoteProviderTimestampUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                QuoteObservedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                QuoteFreshnessLimitMilliseconds = table.Column<int>(type: "INTEGER", nullable: true),
                QuoteReceiptId = table.Column<string>(type: "TEXT", nullable: true),
                QuoteReceiptSha256 = table.Column<string>(type: "TEXT", nullable: true),
                QuoteReceiptAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                DecisionPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                DecisionAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                ModeledExecutablePrice = table.Column<decimal>(type: "TEXT", nullable: true),
                StrategyArtifactId = table.Column<string>(type: "TEXT", nullable: true),
                StrategyVersion = table.Column<string>(type: "TEXT", nullable: true),
                StrategyContentSha256 = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_order_admission_telemetry", row => row.IntentId);
                table.ForeignKey("FK_order_admission_telemetry_order_intents_IntentId", row => row.IntentId,
                    "order_intents", "intent_id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "order_dispatch_attempt_telemetry",
            columns: table => new
            {
                IntentId = table.Column<Guid>(type: "TEXT", nullable: false),
                AttemptNumber = table.Column<int>(type: "INTEGER", nullable: false),
                PrePostAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_order_dispatch_attempt_telemetry", row => new { row.IntentId, row.AttemptNumber });
                table.ForeignKey("FK_order_dispatch_attempt_telemetry_order_intents_IntentId", row => row.IntentId,
                    "order_intents", "intent_id", onDelete: ReferentialAction.Restrict);
            });
    }

    // Keep the original dispatch and quote evidence retained with the order journal.
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Order execution evidence requires dataset-specific approval before removal.");
}
