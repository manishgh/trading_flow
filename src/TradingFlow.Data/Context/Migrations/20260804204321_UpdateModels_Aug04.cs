using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingFlow.Data.Context.Migrations
{
    /// <inheritdoc />
    public partial class UpdateModels_Aug04 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdvisoryPortfolios",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdvisoryPortfolios", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AdvisoryPositions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PortfolioId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Ticker = table.Column<string>(type: "TEXT", nullable: false),
                    Shares = table.Column<decimal>(type: "TEXT", nullable: false),
                    PurchasePrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    AcquiredAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastMlScore = table.Column<decimal>(type: "TEXT", nullable: true),
                    LastScoredAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastRecommendation = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdvisoryPositions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdvisoryPositions_AdvisoryPortfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "AdvisoryPortfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdvisoryPositions_PortfolioId",
                table: "AdvisoryPositions",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_AdvisoryPositions_Ticker",
                table: "AdvisoryPositions",
                column: "Ticker");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdvisoryPositions");

            migrationBuilder.DropTable(
                name: "AdvisoryPortfolios");
        }
    }
}
