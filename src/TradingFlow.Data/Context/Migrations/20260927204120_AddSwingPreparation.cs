using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingFlow.Data.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddSwingPreparation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "preparation_definitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    OwnerId = table.Column<string>(type: "TEXT", nullable: false),
                    AuthorizationStamp = table.Column<string>(type: "TEXT", nullable: false),
                    EffectiveTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    ContentSha256 = table.Column<string>(type: "TEXT", nullable: false),
                    SettingsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_preparation_definitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "preparation_occurrences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DefinitionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SlotKey = table.Column<string>(type: "TEXT", nullable: false),
                    ScheduledTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    SlotJson = table.Column<string>(type: "TEXT", nullable: false),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_preparation_occurrences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_preparation_occurrences_preparation_definitions_DefinitionId",
                        column: x => x.DefinitionId,
                        principalTable: "preparation_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "preparation_reviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OccurrenceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WishlistId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<string>(type: "TEXT", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", nullable: false),
                    RequestSha256 = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_preparation_reviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_preparation_reviews_Wishlists_WishlistId",
                        column: x => x.WishlistId,
                        principalTable: "Wishlists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_preparation_reviews_preparation_occurrences_OccurrenceId",
                        column: x => x.OccurrenceId,
                        principalTable: "preparation_occurrences",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_preparation_definitions_OwnerId",
                table: "preparation_definitions",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_preparation_definitions_WorkflowId_Revision",
                table: "preparation_definitions",
                columns: new[] { "WorkflowId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_preparation_occurrences_DefinitionId",
                table: "preparation_occurrences",
                column: "DefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_preparation_occurrences_WorkflowId_SlotKey",
                table: "preparation_occurrences",
                columns: new[] { "WorkflowId", "SlotKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_preparation_reviews_OccurrenceId",
                table: "preparation_reviews",
                column: "OccurrenceId");

            migrationBuilder.CreateIndex(
                name: "IX_preparation_reviews_OwnerId_IdempotencyKey",
                table: "preparation_reviews",
                columns: new[] { "OwnerId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_preparation_reviews_WishlistId",
                table: "preparation_reviews",
                column: "WishlistId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "preparation_reviews");

            migrationBuilder.DropTable(
                name: "preparation_occurrences");

            migrationBuilder.DropTable(
                name: "preparation_definitions");
        }
    }
}
