using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingFlow.Data.Context.Migrations
{
    /// <inheritdoc />
    public partial class LegacyOperationalBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DecisionAudits",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Ticker = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    StrategyName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Decision = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    RejectionReason = table.Column<string>(type: "TEXT", nullable: true),
                    SignalJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DecisionAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ConfigPath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NewsItems",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Ticker = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Headline = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    SentimentScore = table.Column<decimal>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Url = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Summary = table.Column<string>(type: "TEXT", nullable: true),
                    IngestedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Orders",
                columns: table => new
                {
                    OrderId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Ticker = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    RunName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ClientOrderId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    StrategyName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Broker = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    EntryPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    StopLossPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    TakeProfitPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    ShareQuantity = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Orders", x => x.OrderId);
                });

            migrationBuilder.CreateTable(
                name: "TickerLocks",
                columns: table => new
                {
                    Ticker = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    PodId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    AcquiredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TickerLocks", x => x.Ticker);
                });

            migrationBuilder.CreateTable(
                name: "Wishlists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    IncludeExtendedHours = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsObserved = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Wishlists", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WishlistItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WishlistId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Ticker = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    AddedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WishlistItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WishlistItems_Wishlists_WishlistId",
                        column: x => x.WishlistId,
                        principalTable: "Wishlists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WishlistSignals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WishlistId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Ticker = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    SignalType = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Severity = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    DetectedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Price = table.Column<decimal>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    SnapshotJson = table.Column<string>(type: "TEXT", nullable: false),
                    NewsHeadline = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    NewsUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    NewsProvider = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Acknowledged = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WishlistSignals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WishlistSignals_Wishlists_WishlistId",
                        column: x => x.WishlistId,
                        principalTable: "Wishlists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DecisionAudits_RunName",
                table: "DecisionAudits",
                column: "RunName");

            migrationBuilder.CreateIndex(
                name: "IX_DecisionAudits_Ticker",
                table: "DecisionAudits",
                column: "Ticker");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_RunName",
                table: "Jobs",
                column: "RunName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Status",
                table: "Jobs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_NewsItems_Provider",
                table: "NewsItems",
                column: "Provider");

            migrationBuilder.CreateIndex(
                name: "IX_NewsItems_Ticker",
                table: "NewsItems",
                column: "Ticker");

            migrationBuilder.CreateIndex(
                name: "IX_NewsItems_Timestamp",
                table: "NewsItems",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_ClientOrderId",
                table: "Orders",
                column: "ClientOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_RunName",
                table: "Orders",
                column: "RunName");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Status",
                table: "Orders",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Ticker",
                table: "Orders",
                column: "Ticker");

            migrationBuilder.CreateIndex(
                name: "IX_TickerLocks_ExpiresAt",
                table: "TickerLocks",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_WishlistItems_Ticker",
                table: "WishlistItems",
                column: "Ticker");

            migrationBuilder.CreateIndex(
                name: "IX_WishlistItems_WishlistId_Ticker",
                table: "WishlistItems",
                columns: new[] { "WishlistId", "Ticker" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Wishlists_IsDefault",
                table: "Wishlists",
                column: "IsDefault");

            migrationBuilder.CreateIndex(
                name: "IX_Wishlists_IsObserved",
                table: "Wishlists",
                column: "IsObserved");

            migrationBuilder.CreateIndex(
                name: "IX_Wishlists_Name",
                table: "Wishlists",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WishlistSignals_DetectedAtUtc",
                table: "WishlistSignals",
                column: "DetectedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_WishlistSignals_Ticker",
                table: "WishlistSignals",
                column: "Ticker");

            migrationBuilder.CreateIndex(
                name: "IX_WishlistSignals_WishlistId",
                table: "WishlistSignals",
                column: "WishlistId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DecisionAudits");

            migrationBuilder.DropTable(
                name: "Jobs");

            migrationBuilder.DropTable(
                name: "NewsItems");

            migrationBuilder.DropTable(
                name: "Orders");

            migrationBuilder.DropTable(
                name: "TickerLocks");

            migrationBuilder.DropTable(
                name: "WishlistItems");

            migrationBuilder.DropTable(
                name: "WishlistSignals");

            migrationBuilder.DropTable(
                name: "Wishlists");
        }
    }
}
