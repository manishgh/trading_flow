using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingFlow.Data.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddAtomicRiskAccountScopedExecutionAndDispatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TEMP TABLE __tradingflow_account_scope_guard
                (
                    valid INTEGER NOT NULL CHECK (valid = 1)
                );
                INSERT INTO __tradingflow_account_scope_guard (valid)
                SELECT CASE
                    WHEN (SELECT COUNT(*) FROM order_intents) = 0
                     AND (SELECT COUNT(*) FROM position_events) = 0
                    THEN 1
                    ELSE 0
                END;
                DROP TABLE __tradingflow_account_scope_guard;
                """);

            migrationBuilder.DropIndex(
                name: "IX_position_events_broker_order_id",
                table: "position_events");

            migrationBuilder.DropIndex(
                name: "IX_position_events_execution_id",
                table: "position_events");

            migrationBuilder.DropIndex(
                name: "IX_position_events_symbol_position_event_id",
                table: "position_events");

            migrationBuilder.DropIndex(
                name: "IX_order_intents_symbol_created_at_utc",
                table: "order_intents");

            migrationBuilder.AddColumn<string>(
                name: "account_id",
                table: "position_events",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "account_id",
                table: "order_intents",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "dispatch_attempt_count",
                table: "order_intents",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "dispatch_expires_at_utc",
                table: "order_intents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "dispatch_lease_expires_at_utc",
                table: "order_intents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "dispatch_lease_owner",
                table: "order_intents",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "dispatch_lease_token",
                table: "order_intents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_dispatch_attempt_at_utc",
                table: "order_intents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "portfolio_risk_reservations",
                columns: table => new
                {
                    reservation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    intent_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    client_order_id = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    account_id = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    symbol = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    horizon = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    requested_quantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    pending_quantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    cumulative_filled_quantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    open_position_quantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    entry_price = table.Column<decimal>(type: "TEXT", nullable: false),
                    risk_per_share = table.Column<decimal>(type: "TEXT", nullable: false),
                    reserved_buying_power = table.Column<decimal>(type: "TEXT", nullable: false),
                    reserved_gross_exposure = table.Column<decimal>(type: "TEXT", nullable: false),
                    reserved_net_exposure = table.Column<decimal>(type: "TEXT", nullable: false),
                    reserved_portfolio_risk = table.Column<decimal>(type: "TEXT", nullable: false),
                    reserved_position_slots = table.Column<int>(type: "INTEGER", nullable: false),
                    account_equity_at_reservation = table.Column<decimal>(type: "TEXT", nullable: false),
                    broker_buying_power_at_reservation = table.Column<decimal>(type: "TEXT", nullable: false),
                    broker_gross_exposure_at_reservation = table.Column<decimal>(type: "TEXT", nullable: false),
                    broker_net_exposure_at_reservation = table.Column<decimal>(type: "TEXT", nullable: false),
                    broker_position_count_at_reservation = table.Column<int>(type: "INTEGER", nullable: false),
                    max_gross_exposure = table.Column<decimal>(type: "TEXT", nullable: false),
                    max_portfolio_risk = table.Column<decimal>(type: "TEXT", nullable: false),
                    max_positions = table.Column<int>(type: "INTEGER", nullable: false),
                    account_snapshot_requested_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    account_snapshot_observed_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    reserved_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    state_changed_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    broker_accepted_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    latest_fill_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    released_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    release_reason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    version = table.Column<int>(type: "INTEGER", nullable: false),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    schema_version = table.Column<int>(type: "INTEGER", nullable: false),
                    config_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    code_version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_portfolio_risk_reservations", x => x.reservation_id);
                    table.ForeignKey(
                        name: "FK_portfolio_risk_reservations_order_intents_intent_id",
                        column: x => x.intent_id,
                        principalTable: "order_intents",
                        principalColumn: "intent_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_portfolio_risk_reservations_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "runs",
                        principalColumn: "run_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_position_events_account_id_broker_order_id",
                table: "position_events",
                columns: new[] { "account_id", "broker_order_id" });

            migrationBuilder.CreateIndex(
                name: "IX_position_events_account_id_execution_id",
                table: "position_events",
                columns: new[] { "account_id", "execution_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_position_events_account_id_symbol_position_event_id",
                table: "position_events",
                columns: new[] { "account_id", "symbol", "position_event_id" });

            migrationBuilder.CreateIndex(
                name: "IX_order_intents_account_id_dispatch_lease_expires_at_utc",
                table: "order_intents",
                columns: new[] { "account_id", "dispatch_lease_expires_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_order_intents_account_id_symbol_created_at_utc",
                table: "order_intents",
                columns: new[] { "account_id", "symbol", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_portfolio_risk_reservations_account_id_state",
                table: "portfolio_risk_reservations",
                columns: new[] { "account_id", "state" });

            migrationBuilder.CreateIndex(
                name: "IX_portfolio_risk_reservations_account_id_symbol",
                table: "portfolio_risk_reservations",
                columns: new[] { "account_id", "symbol" },
                unique: true,
                filter: "state <> 'Released'");

            migrationBuilder.CreateIndex(
                name: "IX_portfolio_risk_reservations_client_order_id",
                table: "portfolio_risk_reservations",
                column: "client_order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_portfolio_risk_reservations_intent_id",
                table: "portfolio_risk_reservations",
                column: "intent_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_portfolio_risk_reservations_run_id",
                table: "portfolio_risk_reservations",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "IX_portfolio_risk_reservations_symbol_state",
                table: "portfolio_risk_reservations",
                columns: new[] { "symbol", "state" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "portfolio_risk_reservations");

            migrationBuilder.DropIndex(
                name: "IX_position_events_account_id_broker_order_id",
                table: "position_events");

            migrationBuilder.DropIndex(
                name: "IX_position_events_account_id_execution_id",
                table: "position_events");

            migrationBuilder.DropIndex(
                name: "IX_position_events_account_id_symbol_position_event_id",
                table: "position_events");

            migrationBuilder.DropIndex(
                name: "IX_order_intents_account_id_dispatch_lease_expires_at_utc",
                table: "order_intents");

            migrationBuilder.DropIndex(
                name: "IX_order_intents_account_id_symbol_created_at_utc",
                table: "order_intents");

            migrationBuilder.DropColumn(
                name: "account_id",
                table: "position_events");

            migrationBuilder.DropColumn(
                name: "account_id",
                table: "order_intents");

            migrationBuilder.DropColumn(
                name: "dispatch_attempt_count",
                table: "order_intents");

            migrationBuilder.DropColumn(
                name: "dispatch_expires_at_utc",
                table: "order_intents");

            migrationBuilder.DropColumn(
                name: "dispatch_lease_expires_at_utc",
                table: "order_intents");

            migrationBuilder.DropColumn(
                name: "dispatch_lease_owner",
                table: "order_intents");

            migrationBuilder.DropColumn(
                name: "dispatch_lease_token",
                table: "order_intents");

            migrationBuilder.DropColumn(
                name: "last_dispatch_attempt_at_utc",
                table: "order_intents");

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
                name: "IX_position_events_symbol_position_event_id",
                table: "position_events",
                columns: new[] { "symbol", "position_event_id" });

            migrationBuilder.CreateIndex(
                name: "IX_order_intents_symbol_created_at_utc",
                table: "order_intents",
                columns: new[] { "symbol", "created_at_utc" });
        }
    }
}
