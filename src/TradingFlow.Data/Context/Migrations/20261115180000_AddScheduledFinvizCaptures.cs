using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TradingFlow.Data.Context.Migrations;

[DbContext(typeof(TradingFlowDbContext))]
[Migration("20261115180000_AddScheduledFinvizCaptures")]
public sealed class AddScheduledFinvizCaptures : Migration
{
    // Add a separate observational catalog without rewriting existing provider or preset evidence.
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "finviz_capture_schedule",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false),
                NextUnclosedSessionDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                CalendarCheckedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                NextSlotAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                CalendarError = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_finviz_capture_schedule", row => row.Id);
            });

        migrationBuilder.CreateTable(
            name: "finviz_capture_slots",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PresetId = table.Column<Guid>(type: "TEXT", nullable: false),
                SessionDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                Kind = table.Column<int>(type: "INTEGER", nullable: false),
                PlannedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                PresetName = table.Column<string>(type: "TEXT", nullable: true),
                PresetUpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                ClaimedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                PlannedRevisionKnown = table.Column<bool>(type: "INTEGER", nullable: true),
                Query = table.Column<string>(type: "TEXT", nullable: true),
                QuerySha256 = table.Column<string>(type: "TEXT", nullable: true),
                ExportFormat = table.Column<string>(type: "TEXT", nullable: true),
                State = table.Column<int>(type: "INTEGER", nullable: false),
                Reason = table.Column<string>(type: "TEXT", nullable: true),
                AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                RetryNotBeforeUtcTicks = table.Column<long>(type: "INTEGER", nullable: true),
                EvidenceAttemptId = table.Column<Guid>(type: "TEXT", nullable: true),
                PublishedAttemptId = table.Column<Guid>(type: "TEXT", nullable: true),
                PublishedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_finviz_capture_slots", row => row.Id);
            });

        migrationBuilder.CreateTable(
            name: "finviz_capture_attempts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                SlotId = table.Column<Guid>(type: "TEXT", nullable: false),
                Number = table.Column<int>(type: "INTEGER", nullable: false),
                RequestedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                FinishedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                ObservationJson = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_finviz_capture_attempts", row => row.Id);
                table.ForeignKey("FK_finviz_capture_attempts_finviz_capture_slots_SlotId", row => row.SlotId,
                    "finviz_capture_slots", "Id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex("IX_finviz_capture_slots_PresetId_SessionDate_Kind",
            "finviz_capture_slots", ["PresetId", "SessionDate", "Kind"], unique: true);
        migrationBuilder.CreateIndex("IX_finviz_capture_slots_SessionDate_Kind",
            "finviz_capture_slots", ["SessionDate", "Kind"]);
        migrationBuilder.CreateIndex("IX_finviz_capture_attempts_SlotId_Number",
            "finviz_capture_attempts", ["SlotId", "Number"], unique: true);
    }

    // A downgrade would delete original provider receipt and slot evidence.
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Scheduled Finviz capture evidence requires dataset-specific approval before removal.");
}
