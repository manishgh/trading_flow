using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingFlow.Data.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableDiscovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "discovery_aggregates",
                columns: table => new
                {
                    AggregateId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScopeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Horizon = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    FirstDiscoveredAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastObservedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discovery_aggregates", x => x.AggregateId);
                });

            migrationBuilder.CreateTable(
                name: "discovery_snapshots",
                columns: table => new
                {
                    SnapshotId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScopeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ObservationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceKind = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    SourceKey = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Horizon = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    SourceVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ProviderTimestampUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    IsDiagnostic = table.Column<bool>(type: "INTEGER", nullable: false),
                    RawReference = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    ContentSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SymbolsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discovery_snapshots", x => x.SnapshotId);
                });

            migrationBuilder.CreateTable(
                name: "discovery_source_memberships",
                columns: table => new
                {
                    MembershipId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AggregateId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceKind = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    SourceKey = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    LatestSnapshotId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FirstObservedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastObservedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    MetadataJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discovery_source_memberships", x => x.MembershipId);
                    table.ForeignKey(
                        name: "FK_discovery_source_memberships_discovery_aggregates_AggregateId",
                        column: x => x.AggregateId,
                        principalTable: "discovery_aggregates",
                        principalColumn: "AggregateId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_discovery_source_memberships_discovery_snapshots_LatestSnapshotId",
                        column: x => x.LatestSnapshotId,
                        principalTable: "discovery_snapshots",
                        principalColumn: "SnapshotId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_discovery_aggregates_ScopeId_IsActive_ExpiresAtUtc",
                table: "discovery_aggregates",
                columns: new[] { "ScopeId", "IsActive", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_discovery_aggregates_ScopeId_Symbol",
                table: "discovery_aggregates",
                columns: new[] { "ScopeId", "Symbol" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_discovery_snapshots_ExpiresAtUtc",
                table: "discovery_snapshots",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_discovery_snapshots_ScopeId_SourceKind_SourceKey_ObservationId",
                table: "discovery_snapshots",
                columns: new[] { "ScopeId", "SourceKind", "SourceKey", "ObservationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_discovery_snapshots_ScopeId_SourceKind_SourceKey_SourceVersion",
                table: "discovery_snapshots",
                columns: new[] { "ScopeId", "SourceKind", "SourceKey", "SourceVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_discovery_source_memberships_AggregateId_SourceKind_SourceKey",
                table: "discovery_source_memberships",
                columns: new[] { "AggregateId", "SourceKind", "SourceKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_discovery_source_memberships_ExpiresAtUtc",
                table: "discovery_source_memberships",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_discovery_source_memberships_LatestSnapshotId",
                table: "discovery_source_memberships",
                column: "LatestSnapshotId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "discovery_source_memberships");

            migrationBuilder.DropTable(
                name: "discovery_snapshots");

            migrationBuilder.DropTable(
                name: "discovery_aggregates");
        }
    }
}
