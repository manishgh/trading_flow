using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingFlow.Data.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableOrderPreviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "order_previews",
                columns: table => new
                {
                    PreviewId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TokenSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RequestSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    RequestJson = table.Column<string>(type: "TEXT", nullable: false),
                    PreviewJson = table.Column<string>(type: "TEXT", nullable: false),
                    OutcomeJson = table.Column<string>(type: "TEXT", nullable: true),
                    ConfirmationLeaseToken = table.Column<Guid>(type: "TEXT", nullable: true),
                    ConfirmationLeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_previews", x => x.PreviewId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_order_previews_ExpiresAtUtc",
                table: "order_previews",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_order_previews_IdempotencyKey",
                table: "order_previews",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_order_previews_TokenSha256",
                table: "order_previews",
                column: "TokenSha256",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_previews");
        }
    }
}
