using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TradingFlow.Data.Context.Migrations;

[DbContext(typeof(TradingFlowDbContext))]
[Migration("20261122080000_AddNewsSentimentAnalyzer")]
public sealed class AddNewsSentimentAnalyzer : Migration
{
    // Adds the scorer name; existing rows keep null ("scorer not recorded") and are never re-scored automatically.
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>(name: "SentimentAnalyzer", table: "NewsItems", type: "TEXT", maxLength: 200, nullable: true);

    // Remove only this migration's column when explicitly rolling back a verification database.
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "SentimentAnalyzer", table: "NewsItems");
}
