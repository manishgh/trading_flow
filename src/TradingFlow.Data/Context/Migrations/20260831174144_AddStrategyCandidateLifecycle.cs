using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingFlow.Data.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddStrategyCandidateLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "admission_profile_id",
                table: "candidates",
                type: "TEXT",
                maxLength: 120,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "admission_profile_version",
                table: "candidates",
                type: "TEXT",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "discovery_window_end_utc",
                table: "candidates",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "discovery_window_start_utc",
                table: "candidates",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "expires_at_utc",
                table: "candidates",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<string>(
                name: "semantic_decision_sha256",
                table: "candidates",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "setup_key",
                table: "candidates",
                type: "TEXT",
                maxLength: 300,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "strategy_content_sha256",
                table: "candidates",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "version",
                table: "candidates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // Historical candidates predate the typed lifecycle and must never be
            // mistaken for newly authorized setup evidence after migration.
            migrationBuilder.Sql(
                """
                UPDATE candidates
                SET state = 'Expired',
                    admission_profile_id = 'legacy-expired-history',
                    admission_profile_version = '0.0.0',
                    discovery_window_start_utc = discovered_at_utc,
                    discovery_window_end_utc = revalidated_at_utc,
                    expires_at_utc = revalidated_at_utc,
                    semantic_decision_sha256 = '0000000000000000000000000000000000000000000000000000000000000000',
                    setup_key = 'legacy:' || candidate_id,
                    strategy_content_sha256 = '0000000000000000000000000000000000000000000000000000000000000000',
                    version = 0;
                """);

            migrationBuilder.CreateTable(
                name: "candidate_transitions",
                columns: table => new
                {
                    transition_id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    candidate_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    previous_state = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    new_state = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    reason_code = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    source = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    semantic_decision_sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    evidence_json = table.Column<string>(type: "TEXT", nullable: false),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    schema_version = table.Column<int>(type: "INTEGER", nullable: false),
                    config_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    code_version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_candidate_transitions", x => x.transition_id);
                    table.ForeignKey(
                        name: "FK_candidate_transitions_candidates_candidate_id",
                        column: x => x.candidate_id,
                        principalTable: "candidates",
                        principalColumn: "candidate_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_candidate_transitions_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "runs",
                        principalColumn: "run_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_candidate_transitions_candidate_id_sequence",
                table: "candidate_transitions",
                columns: new[] { "candidate_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_candidate_transitions_run_id",
                table: "candidate_transitions",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "IX_candidate_transitions_semantic_decision_sha256",
                table: "candidate_transitions",
                column: "semantic_decision_sha256");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "candidate_transitions");

            migrationBuilder.DropColumn(
                name: "admission_profile_id",
                table: "candidates");

            migrationBuilder.DropColumn(
                name: "admission_profile_version",
                table: "candidates");

            migrationBuilder.DropColumn(
                name: "discovery_window_end_utc",
                table: "candidates");

            migrationBuilder.DropColumn(
                name: "discovery_window_start_utc",
                table: "candidates");

            migrationBuilder.DropColumn(
                name: "expires_at_utc",
                table: "candidates");

            migrationBuilder.DropColumn(
                name: "semantic_decision_sha256",
                table: "candidates");

            migrationBuilder.DropColumn(
                name: "setup_key",
                table: "candidates");

            migrationBuilder.DropColumn(
                name: "strategy_content_sha256",
                table: "candidates");

            migrationBuilder.DropColumn(
                name: "version",
                table: "candidates");
        }
    }
}
