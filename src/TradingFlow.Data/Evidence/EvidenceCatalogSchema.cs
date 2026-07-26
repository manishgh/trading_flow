namespace TradingFlow.Data.Evidence;

internal static class EvidenceCatalogSchema
{
    public const int Version = 5;

    public const string CreateSql =
        """
        CREATE TABLE IF NOT EXISTS catalog_metadata (
            singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton = 1),
            schema_version INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS collection_plans (
            job_id TEXT NOT NULL PRIMARY KEY,
            logical_plan_hash TEXT NOT NULL,
            attempt_hash TEXT NOT NULL UNIQUE,
            canonical_json TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_collection_plans_logical
            ON collection_plans(logical_plan_hash);

        CREATE TABLE IF NOT EXISTS collection_checkpoints (
            job_id TEXT NOT NULL PRIMARY KEY,
            state INTEGER NOT NULL,
            updated_at_utc TEXT NOT NULL,
            canonical_json TEXT NOT NULL,
            FOREIGN KEY(job_id) REFERENCES collection_plans(job_id)
        );

        CREATE TABLE IF NOT EXISTS request_cursor_checkpoints (
            job_id TEXT NOT NULL,
            request_id TEXT NOT NULL,
            page_ordinal INTEGER NOT NULL,
            updated_at_utc TEXT NOT NULL,
            canonical_json TEXT NOT NULL,
            PRIMARY KEY(job_id, request_id),
            FOREIGN KEY(job_id) REFERENCES collection_plans(job_id)
        );

        CREATE TABLE IF NOT EXISTS source_observations (
            observation_id TEXT NOT NULL PRIMARY KEY,
            job_id TEXT NOT NULL,
            request_id TEXT NOT NULL,
            artifact_namespace TEXT NOT NULL,
            artifact_sha256 TEXT NOT NULL,
            received_at_utc TEXT NOT NULL,
            canonical_json TEXT NOT NULL,
            FOREIGN KEY(job_id) REFERENCES collection_plans(job_id)
        );
        CREATE INDEX IF NOT EXISTS ix_source_observations_job_request
            ON source_observations(job_id, request_id);

        CREATE TABLE IF NOT EXISTS request_page_ledger (
            job_id TEXT NOT NULL,
            request_id TEXT NOT NULL,
            page_ordinal INTEGER NOT NULL,
            observation_id TEXT NOT NULL UNIQUE,
            consumed_page_token_hash TEXT NULL,
            next_page_token_hash TEXT NULL,
            exhausted INTEGER NOT NULL,
            canonical_json TEXT NOT NULL,
            created_at_utc TEXT NOT NULL,
            PRIMARY KEY(job_id, request_id, page_ordinal),
            FOREIGN KEY(job_id) REFERENCES collection_plans(job_id),
            FOREIGN KEY(observation_id) REFERENCES source_observations(observation_id)
        );

        CREATE TABLE IF NOT EXISTS request_completions (
            job_id TEXT NOT NULL,
            request_id TEXT NOT NULL,
            terminal_page_ordinal INTEGER NOT NULL,
            completion_observation_id TEXT NOT NULL,
            canonical_json TEXT NOT NULL,
            completed_at_utc TEXT NOT NULL,
            PRIMARY KEY(job_id, request_id),
            FOREIGN KEY(job_id, request_id, terminal_page_ordinal)
                REFERENCES request_page_ledger(job_id, request_id, page_ordinal),
            FOREIGN KEY(completion_observation_id)
                REFERENCES source_observations(observation_id)
        );

        CREATE TABLE IF NOT EXISTS datasets (
            dataset_id TEXT NOT NULL PRIMARY KEY,
            logical_dataset_key TEXT NOT NULL UNIQUE,
            kind INTEGER NOT NULL,
            data_feed TEXT NOT NULL,
            created_at_utc TEXT NOT NULL,
            manifest_namespace TEXT NOT NULL,
            manifest_sha256 TEXT NOT NULL,
            canonical_json TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_datasets_kind_feed_created
            ON datasets(kind, data_feed, created_at_utc);

        CREATE TABLE IF NOT EXISTS research_runs (
            research_run_id TEXT NOT NULL PRIMARY KEY,
            manifest_namespace TEXT NOT NULL,
            manifest_sha256 TEXT NOT NULL,
            canonical_json TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS holdout_consumptions (
            holdout_id TEXT NOT NULL PRIMARY KEY,
            research_run_id TEXT NOT NULL,
            consumed_at_utc TEXT NOT NULL,
            canonical_json TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS quarantines (
            quarantine_id TEXT NOT NULL PRIMARY KEY,
            status INTEGER NOT NULL,
            canonical_json TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_quarantines_status
            ON quarantines(status);

        CREATE TABLE IF NOT EXISTS quarantine_decisions (
            quarantine_id TEXT NOT NULL,
            decision_ordinal INTEGER NOT NULL,
            status INTEGER NOT NULL,
            decided_at_utc TEXT NOT NULL,
            canonical_json TEXT NOT NULL,
            PRIMARY KEY(quarantine_id, decision_ordinal),
            FOREIGN KEY(quarantine_id) REFERENCES quarantines(quarantine_id)
        );

        CREATE TABLE IF NOT EXISTS external_reference_subjects (
            subject_kind INTEGER NOT NULL,
            subject_id TEXT NOT NULL,
            canonical_json TEXT NOT NULL,
            PRIMARY KEY(subject_kind, subject_id)
        );

        CREATE TABLE IF NOT EXISTS retention_pins (
            pin_id TEXT NOT NULL PRIMARY KEY,
            subject_kind INTEGER NOT NULL,
            subject_id TEXT NOT NULL,
            canonical_json TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS retention_pin_artifacts (
            pin_id TEXT NOT NULL,
            artifact_namespace TEXT NOT NULL,
            artifact_sha256 TEXT NOT NULL,
            PRIMARY KEY(pin_id, artifact_namespace, artifact_sha256),
            FOREIGN KEY(pin_id) REFERENCES retention_pins(pin_id)
        );

        CREATE TABLE IF NOT EXISTS artifact_publications (
            publication_id TEXT NOT NULL PRIMARY KEY,
            owner_kind INTEGER NOT NULL,
            owner_id TEXT NOT NULL,
            state INTEGER NOT NULL,
            artifact_namespace TEXT NOT NULL,
            artifact_sha256 TEXT NOT NULL,
            artifact_byte_length INTEGER NOT NULL,
            artifact_media_type TEXT NOT NULL,
            canonical_owner_json TEXT NOT NULL,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            attempt_count INTEGER NOT NULL,
            last_error TEXT NULL,
            UNIQUE(owner_kind, owner_id)
        );
        CREATE INDEX IF NOT EXISTS ix_artifact_publications_state
            ON artifact_publications(state, updated_at_utc);

        CREATE TABLE IF NOT EXISTS catalog_artifacts (
            artifact_namespace TEXT NOT NULL,
            artifact_sha256 TEXT NOT NULL,
            artifact_byte_length INTEGER NOT NULL,
            artifact_media_type TEXT NOT NULL,
            artifact_kind INTEGER NOT NULL,
            status INTEGER NOT NULL,
            cataloged_at_utc TEXT NOT NULL,
            PRIMARY KEY(artifact_namespace, artifact_sha256)
        );

        CREATE TABLE IF NOT EXISTS artifact_references (
            owner_kind INTEGER NOT NULL,
            owner_id TEXT NOT NULL,
            role TEXT NOT NULL,
            artifact_namespace TEXT NOT NULL,
            artifact_sha256 TEXT NOT NULL,
            reference_time_utc TEXT NOT NULL,
            PRIMARY KEY(owner_kind, owner_id, role, artifact_namespace, artifact_sha256),
            FOREIGN KEY(artifact_namespace, artifact_sha256)
                REFERENCES catalog_artifacts(artifact_namespace, artifact_sha256)
        );
        CREATE INDEX IF NOT EXISTS ix_artifact_references_artifact
            ON artifact_references(artifact_namespace, artifact_sha256);

        CREATE TABLE IF NOT EXISTS orphan_artifacts (
            artifact_namespace TEXT NOT NULL,
            artifact_sha256 TEXT NOT NULL,
            artifact_byte_length INTEGER NOT NULL,
            artifact_media_type TEXT NOT NULL,
            first_seen_at_utc TEXT NOT NULL,
            last_seen_at_utc TEXT NOT NULL,
            classification INTEGER NOT NULL,
            state INTEGER NOT NULL,
            attempt_count INTEGER NOT NULL,
            last_error TEXT NULL,
            PRIMARY KEY(artifact_namespace, artifact_sha256)
        );

        CREATE TABLE IF NOT EXISTS retention_tombstones (
            tombstone_id TEXT NOT NULL PRIMARY KEY,
            artifact_namespace TEXT NOT NULL,
            artifact_sha256 TEXT NOT NULL,
            artifact_byte_length INTEGER NOT NULL,
            artifact_media_type TEXT NOT NULL,
            state INTEGER NOT NULL,
            policy_cutoff_utc TEXT NOT NULL,
            evaluated_at_utc TEXT NOT NULL,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            deleted_at_utc TEXT NULL,
            attempt_count INTEGER NOT NULL,
            next_retry_at_utc TEXT NULL,
            last_error TEXT NULL,
            UNIQUE(artifact_namespace, artifact_sha256)
        );
        """;

    public static string Version4CreateSql =>
        CreateSql.Replace(
            """
            CREATE TABLE IF NOT EXISTS holdout_consumptions (
                holdout_id TEXT NOT NULL PRIMARY KEY,
                research_run_id TEXT NOT NULL,
                consumed_at_utc TEXT NOT NULL,
                canonical_json TEXT NOT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS holdout_consumptions (
                holdout_id TEXT NOT NULL PRIMARY KEY,
                research_run_id TEXT NOT NULL,
                consumed_at_utc TEXT NOT NULL,
                canonical_json TEXT NOT NULL,
                FOREIGN KEY(research_run_id) REFERENCES research_runs(research_run_id)
            );
            """,
            StringComparison.Ordinal);
}
