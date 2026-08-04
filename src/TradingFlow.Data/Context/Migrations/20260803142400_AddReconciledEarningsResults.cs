using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingFlow.Data.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddReconciledEarningsResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "EffectiveEpsActual",
                table: "EarningsAnalysisSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "EffectiveEpsEstimate",
                table: "EarningsAnalysisSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "EffectiveEpsSurprisePercent",
                table: "EarningsAnalysisSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "EffectiveRevenueActualMillions",
                table: "EarningsAnalysisSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "EffectiveRevenueEstimateMillions",
                table: "EarningsAnalysisSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "EffectiveRevenueSurprisePercent",
                table: "EarningsAnalysisSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResultDataSource",
                table: "EarningsAnalysisSnapshots",
                type: "TEXT",
                maxLength: 120,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EffectiveEpsActual",
                table: "EarningsAnalysisSnapshots");

            migrationBuilder.DropColumn(
                name: "EffectiveEpsEstimate",
                table: "EarningsAnalysisSnapshots");

            migrationBuilder.DropColumn(
                name: "EffectiveEpsSurprisePercent",
                table: "EarningsAnalysisSnapshots");

            migrationBuilder.DropColumn(
                name: "EffectiveRevenueActualMillions",
                table: "EarningsAnalysisSnapshots");

            migrationBuilder.DropColumn(
                name: "EffectiveRevenueEstimateMillions",
                table: "EarningsAnalysisSnapshots");

            migrationBuilder.DropColumn(
                name: "EffectiveRevenueSurprisePercent",
                table: "EarningsAnalysisSnapshots");

            migrationBuilder.DropColumn(
                name: "ResultDataSource",
                table: "EarningsAnalysisSnapshots");
        }
    }
}
