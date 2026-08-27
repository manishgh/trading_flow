using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingFlow.Data.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableMarketState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "market_stream_leases",
                columns: table => new
                {
                    resource_key = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    owner_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    fencing_token = table.Column<long>(type: "INTEGER", nullable: false),
                    acquired_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    renewed_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    expires_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_market_stream_leases", x => x.resource_key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_market_stream_leases_expires_at_utc",
                table: "market_stream_leases",
                column: "expires_at_utc");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "market_stream_leases");
        }
    }
}
