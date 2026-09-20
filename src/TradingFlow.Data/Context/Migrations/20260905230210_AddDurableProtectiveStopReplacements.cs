using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingFlow.Data.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableProtectiveStopReplacements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_order_intents_client_order_id",
                table: "order_intents",
                column: "client_order_id");

            migrationBuilder.CreateTable(
                name: "protective_stop_replacements",
                columns: table => new
                {
                    command_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    account_id = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    owner_client_order_id = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    broker_order_id = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    symbol = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    stop_price = table.Column<decimal>(type: "TEXT", nullable: false),
                    reason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    requested_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    lease_owner = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    lease_token = table.Column<Guid>(type: "TEXT", nullable: true),
                    lease_expires_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    attempt_count = table.Column<int>(type: "INTEGER", nullable: false),
                    last_attempt_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    verified_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    verified_broker_order_id = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    last_error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    version = table.Column<int>(type: "INTEGER", nullable: false),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    schema_version = table.Column<int>(type: "INTEGER", nullable: false),
                    config_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    code_version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_protective_stop_replacements", x => x.command_id);
                    table.ForeignKey(
                        name: "FK_protective_stop_replacements_order_intents_owner_client_order_id",
                        column: x => x.owner_client_order_id,
                        principalTable: "order_intents",
                        principalColumn: "client_order_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_protective_stop_replacements_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "runs",
                        principalColumn: "run_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_protective_stop_replacements_account_id_broker_order_id_stop_price",
                table: "protective_stop_replacements",
                columns: new[] { "account_id", "broker_order_id", "stop_price" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_protective_stop_replacements_account_id_state_lease_expires_at_utc",
                table: "protective_stop_replacements",
                columns: new[] { "account_id", "state", "lease_expires_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_protective_stop_replacements_owner_client_order_id",
                table: "protective_stop_replacements",
                column: "owner_client_order_id");

            migrationBuilder.CreateIndex(
                name: "IX_protective_stop_replacements_run_id",
                table: "protective_stop_replacements",
                column: "run_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "protective_stop_replacements");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_order_intents_client_order_id",
                table: "order_intents");
        }
    }
}
