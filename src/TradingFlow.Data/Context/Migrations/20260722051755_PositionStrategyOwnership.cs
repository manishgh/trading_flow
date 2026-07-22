using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingFlow.Data.Context.Migrations
{
    /// <inheritdoc />
    public partial class PositionStrategyOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "execution_strategy_id",
                table: "position_events",
                type: "TEXT",
                maxLength: 120,
                nullable: false,
                defaultValue: "UNKNOWN");

            migrationBuilder.AddColumn<string>(
                name: "strategy_id",
                table: "position_events",
                type: "TEXT",
                maxLength: 120,
                nullable: false,
                defaultValue: "UNKNOWN");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "execution_strategy_id",
                table: "position_events");

            migrationBuilder.DropColumn(
                name: "strategy_id",
                table: "position_events");
        }
    }
}
