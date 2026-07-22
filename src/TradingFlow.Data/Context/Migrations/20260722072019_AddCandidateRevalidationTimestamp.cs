using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingFlow.Data.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddCandidateRevalidationTimestamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "revalidated_at_utc",
                table: "candidates",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.Sql(
                "UPDATE candidates SET revalidated_at_utc = discovered_at_utc;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "revalidated_at_utc",
                table: "candidates");
        }
    }
}
