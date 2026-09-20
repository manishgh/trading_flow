using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingFlow.Data.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddCandidateApiProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "decision_run_id",
                table: "runs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "universe_snapshot_id",
                table: "runs",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "strategy_semantic_version",
                table: "candidates",
                type: "TEXT",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_runs_universe_snapshot_id",
                table: "runs",
                column: "universe_snapshot_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_runs_universe_snapshot_id",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "decision_run_id",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "universe_snapshot_id",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "strategy_semantic_version",
                table: "candidates");
        }
    }
}
