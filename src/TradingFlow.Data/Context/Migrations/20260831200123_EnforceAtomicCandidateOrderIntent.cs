using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingFlow.Data.Context.Migrations
{
    /// <inheritdoc />
    public partial class EnforceAtomicCandidateOrderIntent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "candidate_consumed_version",
                table: "order_intents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "candidate_consumption_evidence_sha256",
                table: "order_intents",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "candidate_semantic_decision_sha256",
                table: "order_intents",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "candidate_triggered_evidence_sha256",
                table: "order_intents",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "candidate_triggered_version",
                table: "order_intents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "order_intents",
                type: "TEXT",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql(
                """
                UPDATE order_intents
                SET kind = CASE
                    WHEN candidate_id IS NOT NULL THEN 'StrategyEntry'
                    WHEN strategy_id = 'BACKSTOP' THEN 'ProtectiveStop'
                    ELSE 'OperatorEntry'
                END;
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_order_intents_candidates_candidate_id",
                table: "order_intents",
                column: "candidate_id",
                principalTable: "candidates",
                principalColumn: "candidate_id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_order_intents_candidates_candidate_id",
                table: "order_intents");

            migrationBuilder.DropColumn(
                name: "candidate_consumed_version",
                table: "order_intents");

            migrationBuilder.DropColumn(
                name: "candidate_consumption_evidence_sha256",
                table: "order_intents");

            migrationBuilder.DropColumn(
                name: "candidate_semantic_decision_sha256",
                table: "order_intents");

            migrationBuilder.DropColumn(
                name: "candidate_triggered_evidence_sha256",
                table: "order_intents");

            migrationBuilder.DropColumn(
                name: "candidate_triggered_version",
                table: "order_intents");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "order_intents");
        }
    }
}
