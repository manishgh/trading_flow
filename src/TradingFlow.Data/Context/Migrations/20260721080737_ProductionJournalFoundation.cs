using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingFlow.Data.Context.Migrations
{
    /// <inheritdoc />
    public partial class ProductionJournalFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "runs",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    profile = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    started_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    finished_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    schema_version = table.Column<int>(type: "INTEGER", nullable: false),
                    config_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    code_version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runs", x => x.run_id);
                });

            migrationBuilder.CreateTable(
                name: "candidates",
                columns: table => new
                {
                    candidate_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    symbol = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    discovered_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    discovery_source = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    finviz_preset = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    horizon = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    previous_close = table.Column<decimal>(type: "TEXT", nullable: true),
                    last_price = table.Column<decimal>(type: "TEXT", nullable: true),
                    gap_pct = table.Column<decimal>(type: "TEXT", nullable: true),
                    gap_atr = table.Column<decimal>(type: "TEXT", nullable: true),
                    premarket_volume = table.Column<long>(type: "INTEGER", nullable: true),
                    premarket_dollar_volume = table.Column<decimal>(type: "TEXT", nullable: true),
                    same_time_rvol = table.Column<decimal>(type: "TEXT", nullable: true),
                    spread_bps = table.Column<decimal>(type: "TEXT", nullable: true),
                    quote_age_ms = table.Column<int>(type: "INTEGER", nullable: true),
                    benchmark_return = table.Column<decimal>(type: "TEXT", nullable: true),
                    sector_return = table.Column<decimal>(type: "TEXT", nullable: true),
                    market_excess_return = table.Column<decimal>(type: "TEXT", nullable: true),
                    sector_excess_return = table.Column<decimal>(type: "TEXT", nullable: true),
                    catalyst_result_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    market_confirmation_score = table.Column<decimal>(type: "TEXT", nullable: true),
                    setup_scores_json = table.Column<string>(type: "TEXT", nullable: false),
                    selected_strategy = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    state = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    reject_reasons_json = table.Column<string>(type: "TEXT", nullable: false),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    schema_version = table.Column<int>(type: "INTEGER", nullable: false),
                    config_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    code_version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_candidates", x => x.candidate_id);
                    table.ForeignKey(
                        name: "FK_candidates_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "runs",
                        principalColumn: "run_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "catalyst_results",
                columns: table => new
                {
                    catalyst_result_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    candidate_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    provider_article_id = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    symbol = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    category = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    direction = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    composite_score = table.Column<decimal>(type: "TEXT", nullable: false),
                    raw_components_json = table.Column<string>(type: "TEXT", nullable: false),
                    penalties_json = table.Column<string>(type: "TEXT", nullable: false),
                    dedup_evidence_json = table.Column<string>(type: "TEXT", nullable: false),
                    stage1_provenance_json = table.Column<string>(type: "TEXT", nullable: false),
                    stage2_provenance_json = table.Column<string>(type: "TEXT", nullable: true),
                    model_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    prompt_version = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    evaluated_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    schema_version = table.Column<int>(type: "INTEGER", nullable: false),
                    config_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    code_version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalyst_results", x => x.catalyst_result_id);
                    table.ForeignKey(
                        name: "FK_catalyst_results_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "runs",
                        principalColumn: "run_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "gate_evaluations",
                columns: table => new
                {
                    evaluation_id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    candidate_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    client_order_id = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    gate_order = table.Column<int>(type: "INTEGER", nullable: false),
                    gate_name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    passed = table.Column<bool>(type: "INTEGER", nullable: false),
                    reject_code = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    evaluated_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    inputs_json = table.Column<string>(type: "TEXT", nullable: false),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    schema_version = table.Column<int>(type: "INTEGER", nullable: false),
                    config_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    code_version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gate_evaluations", x => x.evaluation_id);
                    table.ForeignKey(
                        name: "FK_gate_evaluations_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "runs",
                        principalColumn: "run_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "kill_switch_events",
                columns: table => new
                {
                    kill_switch_event_id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    switch_type = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    transition = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    actor = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    reason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    flatten_requested = table.Column<bool>(type: "INTEGER", nullable: false),
                    details_json = table.Column<string>(type: "TEXT", nullable: false),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    schema_version = table.Column<int>(type: "INTEGER", nullable: false),
                    config_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    code_version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kill_switch_events", x => x.kill_switch_event_id);
                    table.ForeignKey(
                        name: "FK_kill_switch_events_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "runs",
                        principalColumn: "run_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_events",
                columns: table => new
                {
                    event_id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    client_order_id = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    broker_order_id = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    previous_state = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    new_state = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    source = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    broker_timestamp_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    local_timestamp_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    filled_quantity = table.Column<decimal>(type: "TEXT", nullable: true),
                    fill_price = table.Column<decimal>(type: "TEXT", nullable: true),
                    payload_json = table.Column<string>(type: "TEXT", nullable: false),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    schema_version = table.Column<int>(type: "INTEGER", nullable: false),
                    config_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    code_version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_events", x => x.event_id);
                    table.ForeignKey(
                        name: "FK_order_events_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "runs",
                        principalColumn: "run_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_intents",
                columns: table => new
                {
                    intent_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    candidate_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    client_order_id = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    strategy_id = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    symbol = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    side = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    order_type = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    time_in_force = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    requested_quantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    limit_price = table.Column<decimal>(type: "TEXT", nullable: true),
                    stop_price = table.Column<decimal>(type: "TEXT", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    request_json = table.Column<string>(type: "TEXT", nullable: false),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    schema_version = table.Column<int>(type: "INTEGER", nullable: false),
                    config_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    code_version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_intents", x => x.intent_id);
                    table.ForeignKey(
                        name: "FK_order_intents_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "runs",
                        principalColumn: "run_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reconciliations",
                columns: table => new
                {
                    reconciliation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    started_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    broker_snapshot_json = table.Column<string>(type: "TEXT", nullable: false),
                    local_snapshot_json = table.Column<string>(type: "TEXT", nullable: false),
                    diff_json = table.Column<string>(type: "TEXT", nullable: false),
                    requires_acknowledgement = table.Column<bool>(type: "INTEGER", nullable: false),
                    acknowledged_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    acknowledged_by = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    schema_version = table.Column<int>(type: "INTEGER", nullable: false),
                    config_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    code_version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reconciliations", x => x.reconciliation_id);
                    table.ForeignKey(
                        name: "FK_reconciliations_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "runs",
                        principalColumn: "run_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "risk_events",
                columns: table => new
                {
                    risk_event_id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    event_type = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    severity = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    symbol = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    observed_value = table.Column<decimal>(type: "TEXT", nullable: true),
                    limit_value = table.Column<decimal>(type: "TEXT", nullable: true),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    details_json = table.Column<string>(type: "TEXT", nullable: false),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    schema_version = table.Column<int>(type: "INTEGER", nullable: false),
                    config_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    code_version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_risk_events", x => x.risk_event_id);
                    table.ForeignKey(
                        name: "FK_risk_events_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "runs",
                        principalColumn: "run_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_candidates_catalyst_result_id",
                table: "candidates",
                column: "catalyst_result_id");

            migrationBuilder.CreateIndex(
                name: "IX_candidates_horizon_state",
                table: "candidates",
                columns: new[] { "horizon", "state" });

            migrationBuilder.CreateIndex(
                name: "IX_candidates_run_id",
                table: "candidates",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "IX_candidates_symbol_discovered_at_utc",
                table: "candidates",
                columns: new[] { "symbol", "discovered_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_catalyst_results_candidate_id",
                table: "catalyst_results",
                column: "candidate_id");

            migrationBuilder.CreateIndex(
                name: "IX_catalyst_results_evaluated_at_utc",
                table: "catalyst_results",
                column: "evaluated_at_utc");

            migrationBuilder.CreateIndex(
                name: "IX_catalyst_results_provider_article_id_symbol",
                table: "catalyst_results",
                columns: new[] { "provider_article_id", "symbol" });

            migrationBuilder.CreateIndex(
                name: "IX_catalyst_results_run_id",
                table: "catalyst_results",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "IX_gate_evaluations_candidate_id_gate_order",
                table: "gate_evaluations",
                columns: new[] { "candidate_id", "gate_order" });

            migrationBuilder.CreateIndex(
                name: "IX_gate_evaluations_reject_code",
                table: "gate_evaluations",
                column: "reject_code");

            migrationBuilder.CreateIndex(
                name: "IX_gate_evaluations_run_id",
                table: "gate_evaluations",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "IX_kill_switch_events_run_id",
                table: "kill_switch_events",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "IX_kill_switch_events_switch_type_occurred_at_utc",
                table: "kill_switch_events",
                columns: new[] { "switch_type", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_order_events_broker_order_id",
                table: "order_events",
                column: "broker_order_id");

            migrationBuilder.CreateIndex(
                name: "IX_order_events_client_order_id_local_timestamp_utc",
                table: "order_events",
                columns: new[] { "client_order_id", "local_timestamp_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_order_events_run_id",
                table: "order_events",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "IX_order_intents_candidate_id",
                table: "order_intents",
                column: "candidate_id");

            migrationBuilder.CreateIndex(
                name: "IX_order_intents_client_order_id",
                table: "order_intents",
                column: "client_order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_order_intents_run_id",
                table: "order_intents",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "IX_order_intents_symbol_created_at_utc",
                table: "order_intents",
                columns: new[] { "symbol", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_reconciliations_run_id",
                table: "reconciliations",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "IX_reconciliations_started_at_utc",
                table: "reconciliations",
                column: "started_at_utc");

            migrationBuilder.CreateIndex(
                name: "IX_reconciliations_status",
                table: "reconciliations",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_risk_events_event_type_severity",
                table: "risk_events",
                columns: new[] { "event_type", "severity" });

            migrationBuilder.CreateIndex(
                name: "IX_risk_events_occurred_at_utc",
                table: "risk_events",
                column: "occurred_at_utc");

            migrationBuilder.CreateIndex(
                name: "IX_risk_events_run_id",
                table: "risk_events",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "IX_runs_started_at_utc",
                table: "runs",
                column: "started_at_utc");

            migrationBuilder.CreateIndex(
                name: "IX_runs_status",
                table: "runs",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "candidates");

            migrationBuilder.DropTable(
                name: "catalyst_results");

            migrationBuilder.DropTable(
                name: "gate_evaluations");

            migrationBuilder.DropTable(
                name: "kill_switch_events");

            migrationBuilder.DropTable(
                name: "order_events");

            migrationBuilder.DropTable(
                name: "order_intents");

            migrationBuilder.DropTable(
                name: "reconciliations");

            migrationBuilder.DropTable(
                name: "risk_events");

            migrationBuilder.DropTable(
                name: "runs");
        }
    }
}
