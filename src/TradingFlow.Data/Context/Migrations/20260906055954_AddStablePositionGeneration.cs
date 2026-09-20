using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingFlow.Data.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddStablePositionGeneration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_protective_stop_replacements_account_id_broker_order_id_stop_price",
                table: "protective_stop_replacements");

            migrationBuilder.AddColumn<string>(
                name: "replacement_client_order_id",
                table: "protective_stop_replacements",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "root_broker_order_id",
                table: "protective_stop_replacements",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "position_generation_client_order_id",
                table: "position_events",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "position_generation_event_id",
                table: "position_events",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.Sql(
                """
                UPDATE protective_stop_replacements
                SET root_broker_order_id = broker_order_id,
                    replacement_client_order_id = 'TFR-legacy-' || replace(command_id, '-', '');

                CREATE TEMP TABLE __tradingflow_position_generations
                (
                    position_event_id INTEGER PRIMARY KEY,
                    generation_event_id INTEGER NOT NULL,
                    generation_client_order_id TEXT NOT NULL
                );

                INSERT INTO __tradingflow_position_generations
                    (position_event_id, generation_event_id, generation_client_order_id)
                WITH ordered AS
                (
                    SELECT
                        position_event_id,
                        account_id,
                        symbol,
                        client_order_id,
                        CAST(quantity_after AS NUMERIC) AS quantity_after,
                        LAG(CAST(quantity_after AS NUMERIC)) OVER
                            (PARTITION BY account_id, symbol ORDER BY position_event_id) AS previous_quantity
                    FROM position_events
                ),
                anchored AS
                (
                    SELECT
                        *,
                        CASE
                            WHEN quantity_after <> 0 AND
                                 (previous_quantity IS NULL OR previous_quantity = 0 OR
                                  (previous_quantity > 0 AND quantity_after < 0) OR
                                  (previous_quantity < 0 AND quantity_after > 0))
                            THEN position_event_id
                            ELSE NULL
                        END AS generation_anchor
                    FROM ordered
                ),
                resolved AS
                (
                    SELECT
                        position_event_id,
                        MAX(generation_anchor) OVER
                            (PARTITION BY account_id, symbol ORDER BY position_event_id
                             ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS generation_event_id
                    FROM anchored
                )
                SELECT
                    resolved.position_event_id,
                    COALESCE(resolved.generation_event_id, 0),
                    COALESCE(generation.client_order_id, '')
                FROM resolved
                LEFT JOIN position_events AS generation
                    ON generation.position_event_id = resolved.generation_event_id;

                UPDATE position_events
                SET position_generation_event_id =
                        (SELECT generation_event_id
                         FROM __tradingflow_position_generations AS generation
                         WHERE generation.position_event_id = position_events.position_event_id),
                    position_generation_client_order_id =
                        (SELECT generation_client_order_id
                         FROM __tradingflow_position_generations AS generation
                         WHERE generation.position_event_id = position_events.position_event_id);

                DROP TABLE __tradingflow_position_generations;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_protective_stop_replacements_account_id_root_broker_order_id_stop_price",
                table: "protective_stop_replacements",
                columns: new[] { "account_id", "root_broker_order_id", "stop_price" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_protective_stop_replacements_account_id_verified_broker_order_id",
                table: "protective_stop_replacements",
                columns: new[] { "account_id", "verified_broker_order_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_protective_stop_replacements_replacement_client_order_id",
                table: "protective_stop_replacements",
                column: "replacement_client_order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_position_events_account_id_symbol_position_generation_event_id",
                table: "position_events",
                columns: new[] { "account_id", "symbol", "position_generation_event_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_protective_stop_replacements_account_id_root_broker_order_id_stop_price",
                table: "protective_stop_replacements");

            migrationBuilder.DropIndex(
                name: "IX_protective_stop_replacements_account_id_verified_broker_order_id",
                table: "protective_stop_replacements");

            migrationBuilder.DropIndex(
                name: "IX_protective_stop_replacements_replacement_client_order_id",
                table: "protective_stop_replacements");

            migrationBuilder.DropIndex(
                name: "IX_position_events_account_id_symbol_position_generation_event_id",
                table: "position_events");

            migrationBuilder.DropColumn(
                name: "replacement_client_order_id",
                table: "protective_stop_replacements");

            migrationBuilder.DropColumn(
                name: "root_broker_order_id",
                table: "protective_stop_replacements");

            migrationBuilder.DropColumn(
                name: "position_generation_client_order_id",
                table: "position_events");

            migrationBuilder.DropColumn(
                name: "position_generation_event_id",
                table: "position_events");

            migrationBuilder.CreateIndex(
                name: "IX_protective_stop_replacements_account_id_broker_order_id_stop_price",
                table: "protective_stop_replacements",
                columns: new[] { "account_id", "broker_order_id", "stop_price" },
                unique: true);
        }
    }
}
