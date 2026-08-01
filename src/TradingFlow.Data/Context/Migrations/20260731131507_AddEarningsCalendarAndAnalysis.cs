using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingFlow.Data.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddEarningsCalendarAndAnalysis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EarningsCalendarEvents",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Ticker = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CompanyName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    ReportDateExchange = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    ScheduledAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ReleaseWindow = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    IsScheduleEstimate = table.Column<bool>(type: "INTEGER", nullable: false),
                    MarketCapMillions = table.Column<decimal>(type: "TEXT", nullable: true),
                    EpsEstimate = table.Column<decimal>(type: "TEXT", nullable: true),
                    EpsActual = table.Column<decimal>(type: "TEXT", nullable: true),
                    EpsSurprisePercent = table.Column<decimal>(type: "TEXT", nullable: true),
                    ReportedEpsEstimate = table.Column<decimal>(type: "TEXT", nullable: true),
                    ReportedEpsActual = table.Column<decimal>(type: "TEXT", nullable: true),
                    ReportedEpsSurprisePercent = table.Column<decimal>(type: "TEXT", nullable: true),
                    RevenueEstimateMillions = table.Column<decimal>(type: "TEXT", nullable: true),
                    RevenueActualMillions = table.Column<decimal>(type: "TEXT", nullable: true),
                    RevenueSurprisePercent = table.Column<decimal>(type: "TEXT", nullable: true),
                    OneDayPriceReactionPercent = table.Column<decimal>(type: "TEXT", nullable: true),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    SourceUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    SourceArtifactSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProviderReceivedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ResultFirstSeenAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FirstSeenAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastSeenAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EarningsCalendarEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EarningsAnalysisSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EarningsEventId = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Ticker = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    AnalyzedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ResultAssessment = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    BreakoutAssessment = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1500, nullable: false),
                    ResultNewsPublishedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    NewsHeadline = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    NewsUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    NewsProvider = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    NewsSentiment = table.Column<decimal>(type: "TEXT", nullable: true),
                    LatestCompletedBarAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    PreReleaseReferenceHigh = table.Column<decimal>(type: "TEXT", nullable: true),
                    PreReleaseReferenceClose = table.Column<decimal>(type: "TEXT", nullable: true),
                    LatestClose = table.Column<decimal>(type: "TEXT", nullable: true),
                    EventReturnPercent = table.Column<decimal>(type: "TEXT", nullable: true),
                    Ema10 = table.Column<decimal>(type: "TEXT", nullable: true),
                    Ema20 = table.Column<decimal>(type: "TEXT", nullable: true),
                    MacdHistogram = table.Column<decimal>(type: "TEXT", nullable: true),
                    SlotRelativeVolume = table.Column<decimal>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EarningsAnalysisSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EarningsAnalysisSnapshots_EarningsCalendarEvents_EarningsEventId",
                        column: x => x.EarningsEventId,
                        principalTable: "EarningsCalendarEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EarningsAnalysisSnapshots_EarningsEventId_AnalyzedAtUtc",
                table: "EarningsAnalysisSnapshots",
                columns: new[] { "EarningsEventId", "AnalyzedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_EarningsAnalysisSnapshots_Ticker_AnalyzedAtUtc",
                table: "EarningsAnalysisSnapshots",
                columns: new[] { "Ticker", "AnalyzedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_EarningsCalendarEvents_Provider",
                table: "EarningsCalendarEvents",
                column: "Provider");

            migrationBuilder.CreateIndex(
                name: "IX_EarningsCalendarEvents_ReportDateExchange_ScheduledAtUtc",
                table: "EarningsCalendarEvents",
                columns: new[] { "ReportDateExchange", "ScheduledAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_EarningsCalendarEvents_Ticker",
                table: "EarningsCalendarEvents",
                column: "Ticker");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EarningsAnalysisSnapshots");

            migrationBuilder.DropTable(
                name: "EarningsCalendarEvents");
        }
    }
}
