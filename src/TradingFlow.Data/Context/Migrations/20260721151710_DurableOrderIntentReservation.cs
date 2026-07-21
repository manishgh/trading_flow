using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingFlow.Data.Context.Migrations
{
    /// <inheritdoc />
    public partial class DurableOrderIntentReservation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "sequence_number",
                table: "order_intents",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateOnly>(
                name: "session_date",
                table: "order_intents",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1));

            migrationBuilder.Sql(
                """
                WITH ranked AS
                (
                    SELECT
                        intent_id,
                        substr(created_at_utc, 1, 10) AS migrated_session_date,
                        row_number() OVER
                        (
                            PARTITION BY strategy_id, side, symbol, substr(created_at_utc, 1, 10)
                            ORDER BY created_at_utc, intent_id
                        ) AS migrated_sequence_number
                    FROM order_intents
                )
                UPDATE order_intents
                SET
                    session_date =
                    (
                        SELECT migrated_session_date
                        FROM ranked
                        WHERE ranked.intent_id = order_intents.intent_id
                    ),
                    sequence_number =
                    (
                        SELECT migrated_sequence_number
                        FROM ranked
                        WHERE ranked.intent_id = order_intents.intent_id
                    );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_order_intents_strategy_id_side_symbol_session_date_sequence_number",
                table: "order_intents",
                columns: new[] { "strategy_id", "side", "symbol", "session_date", "sequence_number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_order_intents_strategy_id_side_symbol_session_date_sequence_number",
                table: "order_intents");

            migrationBuilder.DropColumn(
                name: "sequence_number",
                table: "order_intents");

            migrationBuilder.DropColumn(
                name: "session_date",
                table: "order_intents");
        }
    }
}
