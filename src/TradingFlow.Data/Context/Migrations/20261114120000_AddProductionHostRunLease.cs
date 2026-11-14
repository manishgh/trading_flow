using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TradingFlow.Data.Context.Migrations;

[DbContext(typeof(TradingFlowDbContext))]
[Migration("20261114120000_AddProductionHostRunLease")]
public sealed class AddProductionHostRunLease : Migration
{
    // Existing run rows are unclassified until exact ownership can be established.
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "host_lease_expires_at_utc_ticks",
            table: "runs",
            type: "INTEGER",
            nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "paper_entries_stopped_at_utc",
            table: "runs",
            type: "TEXT",
            nullable: true);
        migrationBuilder.AddColumn<Guid>(
            name: "paper_job_lease_token",
            table: "runs",
            type: "TEXT",
            nullable: true);
    }

    // SQLite directly drops the nullable, unindexed lifecycle columns.
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER TABLE runs DROP COLUMN paper_job_lease_token;");
        migrationBuilder.Sql("ALTER TABLE runs DROP COLUMN paper_entries_stopped_at_utc;");
        migrationBuilder.Sql("ALTER TABLE runs DROP COLUMN host_lease_expires_at_utc_ticks;");
    }
}
