using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingFlow.Data.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddPositionExitQuantityReservation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "position_generation_event_id",
                table: "order_intents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_order_intents_account_id_symbol_position_generation_event_id_kind",
                table: "order_intents",
                columns: new[] { "account_id", "symbol", "position_generation_event_id", "kind" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_order_intents_account_id_symbol_position_generation_event_id_kind",
                table: "order_intents");

            migrationBuilder.DropColumn(
                name: "position_generation_event_id",
                table: "order_intents");
        }
    }
}
