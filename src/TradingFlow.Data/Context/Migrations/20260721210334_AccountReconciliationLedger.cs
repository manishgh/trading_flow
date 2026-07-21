using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingFlow.Data.Context.Migrations
{
    /// <inheritdoc />
    public partial class AccountReconciliationLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "acknowledgement_reason",
                table: "reconciliations",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "diff_hash",
                table: "reconciliations",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "position_events",
                columns: table => new
                {
                    position_event_id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    symbol = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    quantity_after = table.Column<decimal>(type: "TEXT", nullable: false),
                    fill_quantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    fill_price = table.Column<decimal>(type: "TEXT", nullable: false),
                    side = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    broker_order_id = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    client_order_id = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    execution_id = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    source = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    broker_timestamp_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    local_timestamp_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    payload_json = table.Column<string>(type: "TEXT", nullable: false),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    schema_version = table.Column<int>(type: "INTEGER", nullable: false),
                    config_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    code_version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_position_events", x => x.position_event_id);
                    table.ForeignKey(
                        name: "FK_position_events_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "runs",
                        principalColumn: "run_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_reconciliations_requires_acknowledgement",
                table: "reconciliations",
                column: "requires_acknowledgement",
                unique: true,
                filter: "requires_acknowledgement = 1");

            migrationBuilder.CreateIndex(
                name: "IX_position_events_broker_order_id",
                table: "position_events",
                column: "broker_order_id");

            migrationBuilder.CreateIndex(
                name: "IX_position_events_execution_id",
                table: "position_events",
                column: "execution_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_position_events_run_id",
                table: "position_events",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "IX_position_events_symbol_position_event_id",
                table: "position_events",
                columns: new[] { "symbol", "position_event_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "position_events");

            migrationBuilder.DropIndex(
                name: "IX_reconciliations_requires_acknowledgement",
                table: "reconciliations");

            migrationBuilder.DropColumn(
                name: "acknowledgement_reason",
                table: "reconciliations");

            migrationBuilder.DropColumn(
                name: "diff_hash",
                table: "reconciliations");
        }
    }
}
