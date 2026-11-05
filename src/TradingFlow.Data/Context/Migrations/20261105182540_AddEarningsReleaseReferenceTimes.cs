using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingFlow.Data.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddEarningsReleaseReferenceTimes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "PostReleaseHigh",
                table: "EarningsAnalysisSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PostReleaseHighAtUtc",
                table: "EarningsAnalysisSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PreReleaseReferenceAtUtc",
                table: "EarningsAnalysisSnapshots",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PostReleaseHigh",
                table: "EarningsAnalysisSnapshots");

            migrationBuilder.DropColumn(
                name: "PostReleaseHighAtUtc",
                table: "EarningsAnalysisSnapshots");

            migrationBuilder.DropColumn(
                name: "PreReleaseReferenceAtUtc",
                table: "EarningsAnalysisSnapshots");
        }
    }
}
