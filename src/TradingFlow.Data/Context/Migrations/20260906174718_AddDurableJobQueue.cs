using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingFlow.Data.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableJobQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Jobs_RunName",
                table: "Jobs");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_Status",
                table: "Jobs");

            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CancellationRequestedAtUtc",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CreatedAtUtcTicks",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "HeartbeatAtUtc",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "JobType",
                table: "Jobs",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeaseExpiresAtUtc",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LeaseExpiresAtUtcTicks",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeaseOwner",
                table: "Jobs",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LeaseToken",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestJson",
                table: "Jobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ResultReference",
                table: "Jobs",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SnapshotJson",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_JobType_RunName",
                table: "Jobs",
                columns: new[] { "JobType", "RunName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_JobType_Status_LeaseExpiresAtUtcTicks_CreatedAtUtcTicks",
                table: "Jobs",
                columns: new[] { "JobType", "Status", "LeaseExpiresAtUtcTicks", "CreatedAtUtcTicks" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Jobs_JobType_RunName",
                table: "Jobs");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_JobType_Status_LeaseExpiresAtUtcTicks_CreatedAtUtcTicks",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "AttemptCount",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "CancellationRequestedAtUtc",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "CreatedAtUtcTicks",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "HeartbeatAtUtc",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "JobType",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAtUtc",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAtUtcTicks",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "LeaseOwner",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "LeaseToken",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "RequestJson",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ResultReference",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "SnapshotJson",
                table: "Jobs");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_RunName",
                table: "Jobs",
                column: "RunName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Status",
                table: "Jobs",
                column: "Status");
        }
    }
}
