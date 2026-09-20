using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingFlow.Data.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddApplicationWorkflowPreviewsAndEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "application_events",
                columns: table => new
                {
                    EventSequence = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StreamName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_application_events", x => x.EventSequence);
                });

            migrationBuilder.CreateTable(
                name: "candidate_run_requests",
                columns: table => new
                {
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    RequestSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CandidateRunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UniverseSnapshotId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Mode = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    StrategyIdentitiesJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_candidate_run_requests", x => x.IdempotencyKey);
                });

            migrationBuilder.CreateTable(
                name: "universe_previews",
                columns: table => new
                {
                    UniverseSnapshotId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Horizon = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    RequestSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ContentSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ResolvedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    PreviewJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_universe_previews", x => x.UniverseSnapshotId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_application_events_StreamName_EventSequence",
                table: "application_events",
                columns: new[] { "StreamName", "EventSequence" });

            migrationBuilder.CreateIndex(
                name: "IX_candidate_run_requests_CandidateRunId",
                table: "candidate_run_requests",
                column: "CandidateRunId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_candidate_run_requests_UniverseSnapshotId",
                table: "candidate_run_requests",
                column: "UniverseSnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_universe_previews_ExpiresAtUtc",
                table: "universe_previews",
                column: "ExpiresAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "application_events");

            migrationBuilder.DropTable(
                name: "candidate_run_requests");

            migrationBuilder.DropTable(
                name: "universe_previews");
        }
    }
}
