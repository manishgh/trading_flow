using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Research;

namespace TradingFlow.Data.Evidence.Governance;

public sealed record ResearchRegistryCommitResult(string SubjectId, bool AlreadyCommitted);

/// <summary>
/// Durable append-only registry for frozen research trials and canonical results.
/// Existing identifiers are idempotent only when their canonical content is identical.
/// </summary>
public sealed class SqliteResearchTrialRegistry
{
    private const int SchemaVersion = 1;
    private readonly string connectionString;
    private readonly ResearchTrialRegistryOpenMode openMode;
    private readonly SemaphoreSlim initialization = new(1, 1);
    private volatile bool initialized;

    public SqliteResearchTrialRegistry(ResearchTrialRegistryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        openMode = options.OpenMode;
        var directory = Path.GetDirectoryName(options.DatabasePath);
        if (!String.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (openMode == ResearchTrialRegistryOpenMode.BootstrapNew &&
            File.Exists(options.DatabasePath))
        {
            throw new IOException(
                $"A research trial registry already exists at '{options.DatabasePath}'.");
        }

        if (openMode == ResearchTrialRegistryOpenMode.OpenExisting &&
            !File.Exists(options.DatabasePath))
        {
            throw new FileNotFoundException(
                "The research trial registry was not found.",
                options.DatabasePath);
        }

        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = openMode == ResearchTrialRegistryOpenMode.BootstrapNew
                ? SqliteOpenMode.ReadWriteCreate
                : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public async Task<ResearchRegistryCommitResult> RegisterTrialAsync(
        ResearchTrialDefinition trial,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trial);
        if (trial.HoldoutState != ResearchHoldoutState.Unopened)
        {
            throw new InvalidOperationException(
                "A newly registered trial must have an unopened holdout.");
        }

        var bytes = EvidenceCanonicalJson.SerializeToUtf8Bytes(trial);
        var canonicalJson = Encoding.UTF8.GetString(bytes);
        var sha256 = EvidenceCanonicalJson.ComputeSha256(trial);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);

        var existing = await ReadRowAsync(
            connection,
            transaction,
            "SELECT canonical_json, sha256 FROM research_trials WHERE experiment_id = $id;",
            cancellationToken,
            ("$id", trial.ExperimentId));
        if (existing is not null)
        {
            RequireCanonicalMatch(existing.Value.Json, existing.Value.Hash, canonicalJson, sha256, "trial");
            transaction.Commit();
            return new ResearchRegistryCommitResult(trial.ExperimentId, true);
        }

        var expectedTrialCount = await ReadCountAsync(
            connection,
            transaction,
            "SELECT COUNT(*) FROM research_trials WHERE family_id = $family;",
            cancellationToken,
            ("$family", trial.FamilyId)) + 1;
        if (trial.FamilyTrialCount != expectedTrialCount)
        {
            throw new InvalidOperationException(
                $"Research family '{trial.FamilyId}' expected trial count " +
                $"{expectedTrialCount}, received {trial.FamilyTrialCount}.");
        }

        if (trial.ParentExperimentId is not null)
        {
            var parentFamily = await ReadScalarAsync(
                connection,
                transaction,
                "SELECT family_id FROM research_trials WHERE experiment_id = $id;",
                cancellationToken,
                ("$id", trial.ParentExperimentId));
            if (!String.Equals(parentFamily, trial.FamilyId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "A parent trial must already exist in the same research family.");
            }
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO research_trials(
                experiment_id, family_id, parent_experiment_id, family_trial_count,
                registered_at_utc, sha256, canonical_json)
            VALUES($id, $family, $parent, $count, $registered, $hash, $json);
            """,
            cancellationToken,
            ("$id", trial.ExperimentId),
            ("$family", trial.FamilyId),
            ("$parent", trial.ParentExperimentId),
            ("$count", trial.FamilyTrialCount),
            ("$registered", Utc(trial.RegisteredAtUtc)),
            ("$hash", sha256),
            ("$json", canonicalJson));
        transaction.Commit();
        return new ResearchRegistryCommitResult(trial.ExperimentId, false);
    }

    public async Task<ResearchTrialDefinition?> GetTrialAsync(
        string experimentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentId);
        await using var connection = await OpenAsync(cancellationToken);
        var row = await ReadRowAsync(
            connection,
            null,
            "SELECT canonical_json, sha256 FROM research_trials WHERE experiment_id = $id;",
            cancellationToken,
            ("$id", experimentId.Trim()));
        if (row is null)
        {
            return null;
        }

        var trial = EvidenceCanonicalJson.Deserialize<ResearchTrialDefinition>(
            Encoding.UTF8.GetBytes(row.Value.Json));
        RequireCanonicalMatch(
            row.Value.Json,
            row.Value.Hash,
            Encoding.UTF8.GetString(EvidenceCanonicalJson.SerializeToUtf8Bytes(trial)),
            EvidenceCanonicalJson.ComputeSha256(trial),
            "trial");
        return trial;
    }

    public async Task<ResearchRegistryCommitResult> RegisterResultAsync(
        CanonicalResearchResultManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var bytes = EvidenceCanonicalJson.SerializeToUtf8Bytes(manifest);
        var canonicalJson = Encoding.UTF8.GetString(bytes);
        var sha256 = EvidenceCanonicalJson.ComputeSha256(manifest);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);

        var trialRow = await ReadRowAsync(
            connection,
            transaction,
            "SELECT canonical_json, sha256 FROM research_trials WHERE experiment_id = $id;",
            cancellationToken,
            ("$id", manifest.ExperimentId));
        if (trialRow is null)
        {
            throw new InvalidOperationException(
                $"Research trial '{manifest.ExperimentId}' does not exist.");
        }

        var trial = EvidenceCanonicalJson.Deserialize<ResearchTrialDefinition>(
            Encoding.UTF8.GetBytes(trialRow.Value.Json));
        RequireCanonicalMatch(
            trialRow.Value.Json,
            trialRow.Value.Hash,
            Encoding.UTF8.GetString(EvidenceCanonicalJson.SerializeToUtf8Bytes(trial)),
            EvidenceCanonicalJson.ComputeSha256(trial),
            "trial");
        if (!manifest.TrialDefinitionSha256.Equals(trialRow.Value.Hash, StringComparison.Ordinal) ||
            !manifest.PrimaryMetric.Equals(trial.PrimaryMetric, StringComparison.Ordinal) ||
            !trial.Partitions.Any(partition =>
                partition.Name.Equals(manifest.Partition, StringComparison.Ordinal)) ||
            !manifest.Metrics.ContainsKey(manifest.PrimaryMetric))
        {
            throw new InvalidDataException(
                "The result manifest does not match its frozen trial definition.");
        }

        var existing = await ReadRowAsync(
            connection,
            transaction,
            "SELECT canonical_json, sha256 FROM research_results WHERE result_id = $id;",
            cancellationToken,
            ("$id", manifest.ResultId));
        if (existing is not null)
        {
            RequireCanonicalMatch(existing.Value.Json, existing.Value.Hash, canonicalJson, sha256, "result");
            transaction.Commit();
            return new ResearchRegistryCommitResult(manifest.ResultId, true);
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO research_results(
                result_id, experiment_id, created_at_utc, sha256, canonical_json)
            VALUES($id, $experiment, $created, $hash, $json);
            """,
            cancellationToken,
            ("$id", manifest.ResultId),
            ("$experiment", manifest.ExperimentId),
            ("$created", Utc(manifest.CreatedAtUtc)),
            ("$hash", sha256),
            ("$json", canonicalJson));
        transaction.Commit();
        return new ResearchRegistryCommitResult(manifest.ResultId, false);
    }

    public async Task<CanonicalResearchResultManifest?> GetResultAsync(
        string resultId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resultId);
        await using var connection = await OpenAsync(cancellationToken);
        var row = await ReadRowAsync(
            connection,
            null,
            "SELECT canonical_json, sha256 FROM research_results WHERE result_id = $id;",
            cancellationToken,
            ("$id", resultId.Trim()));
        if (row is null)
        {
            return null;
        }

        var manifest = EvidenceCanonicalJson.Deserialize<CanonicalResearchResultManifest>(
            Encoding.UTF8.GetBytes(row.Value.Json));
        RequireCanonicalMatch(
            row.Value.Json,
            row.Value.Hash,
            Encoding.UTF8.GetString(EvidenceCanonicalJson.SerializeToUtf8Bytes(manifest)),
            EvidenceCanonicalJson.ComputeSha256(manifest),
            "result");
        return manifest;
    }

    public async Task<ResearchRegistryCommitResult> ConsumeHoldoutAsync(
        ResearchHoldoutConsumptionRecord consumption,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(consumption);
        var canonicalJson = Encoding.UTF8.GetString(
            EvidenceCanonicalJson.SerializeToUtf8Bytes(consumption));
        var sha256 = EvidenceCanonicalJson.ComputeSha256(consumption);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        var trialExists = await ReadScalarAsync(
            connection,
            transaction,
            "SELECT experiment_id FROM research_trials WHERE experiment_id = $id;",
            cancellationToken,
            ("$id", consumption.ExperimentId));
        if (trialExists is null)
        {
            throw new InvalidOperationException(
                $"Research trial '{consumption.ExperimentId}' does not exist.");
        }

        var existing = await ReadRowAsync(
            connection,
            transaction,
            """
            SELECT canonical_json, sha256
            FROM holdout_consumptions
            WHERE experiment_id = $id;
            """,
            cancellationToken,
            ("$id", consumption.ExperimentId));
        if (existing is not null)
        {
            RequireCanonicalMatch(
                existing.Value.Json,
                existing.Value.Hash,
                canonicalJson,
                sha256,
                "holdout consumption");
            transaction.Commit();
            return new ResearchRegistryCommitResult(consumption.ExperimentId, true);
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO holdout_consumptions(
                experiment_id, research_run_id, consumed_at_utc, sha256, canonical_json)
            VALUES($id, $run, $consumed, $hash, $json);
            """,
            cancellationToken,
            ("$id", consumption.ExperimentId),
            ("$run", consumption.ResearchRunId),
            ("$consumed", Utc(consumption.ConsumedAtUtc)),
            ("$hash", sha256),
            ("$json", canonicalJson));
        transaction.Commit();
        return new ResearchRegistryCommitResult(consumption.ExperimentId, false);
    }

    public async Task<ResearchHoldoutState> GetHoldoutStateAsync(
        string experimentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentId);
        await using var connection = await OpenAsync(cancellationToken);
        var trial = await ReadScalarAsync(
            connection,
            null,
            "SELECT experiment_id FROM research_trials WHERE experiment_id = $id;",
            cancellationToken,
            ("$id", experimentId.Trim()));
        if (trial is null)
        {
            throw new KeyNotFoundException($"Research trial '{experimentId}' does not exist.");
        }

        var consumption = await ReadRowAsync(
            connection,
            null,
            """
            SELECT canonical_json, sha256
            FROM holdout_consumptions
            WHERE experiment_id = $id;
            """,
            cancellationToken,
            ("$id", experimentId.Trim()));
        if (consumption is null)
        {
            return ResearchHoldoutState.Unopened;
        }

        var value = EvidenceCanonicalJson.Deserialize<ResearchHoldoutConsumptionRecord>(
            Encoding.UTF8.GetBytes(consumption.Value.Json));
        RequireCanonicalMatch(
            consumption.Value.Json,
            consumption.Value.Hash,
            Encoding.UTF8.GetString(EvidenceCanonicalJson.SerializeToUtf8Bytes(value)),
            EvidenceCanonicalJson.ComputeSha256(value),
            "holdout consumption");
        return ResearchHoldoutState.Consumed;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await SqliteDurability.ConfigureDatabaseAsync(connection, cancellationToken);
        return connection;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (initialized)
        {
            return;
        }

        await initialization.WaitAsync(cancellationToken);
        try
        {
            if (initialized)
            {
                return;
            }

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await SqliteDurability.ConfigureDatabaseAsync(connection, cancellationToken);
            if (openMode == ResearchTrialRegistryOpenMode.BootstrapNew)
            {
                await ExecuteAsync(connection, null, SchemaSql, cancellationToken);
                await ExecuteAsync(
                    connection,
                    null,
                    "INSERT INTO research_registry_metadata(singleton, schema_version) VALUES(1, $version);",
                    cancellationToken,
                    ("$version", SchemaVersion));
            }
            else
            {
                var version = await ReadScalarAsync(
                    connection,
                    null,
                    "SELECT CAST(schema_version AS TEXT) FROM research_registry_metadata WHERE singleton = 1;",
                    cancellationToken);
                if (!Int32.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
                    parsed != SchemaVersion)
                {
                    throw new InvalidDataException(
                        $"Research trial registry schema '{version ?? "missing"}' is unsupported.");
                }

                var integrity = await ReadScalarAsync(
                    connection,
                    null,
                    "PRAGMA quick_check;",
                    cancellationToken);
                if (!String.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Research trial registry integrity check failed: {integrity ?? "missing"}.");
                }
            }

            initialized = true;
        }
        finally
        {
            initialization.Release();
        }
    }

    private static void RequireCanonicalMatch(
        string storedJson,
        string storedHash,
        string candidateJson,
        string candidateHash,
        string subject)
    {
        if (!storedJson.Equals(candidateJson, StringComparison.Ordinal) ||
            !storedHash.Equals(candidateHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The {subject} identifier was reused with different or corrupted content.");
        }
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string?> ReadScalarAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull
            ? null
            : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static async Task<long> ReadCountAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        var value = await ReadScalarAsync(connection, transaction, sql, cancellationToken, parameters);
        return Int64.Parse(value ?? "0", CultureInfo.InvariantCulture);
    }

    private static async Task<(string Json, string Hash)?> ReadRowAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetString(0), reader.GetString(1))
            : null;
    }

    private static string Utc(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private const string SchemaSql =
        """
        CREATE TABLE research_registry_metadata (
            singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton = 1),
            schema_version INTEGER NOT NULL
        );

        CREATE TABLE research_trials (
            experiment_id TEXT NOT NULL PRIMARY KEY,
            family_id TEXT NOT NULL,
            parent_experiment_id TEXT NULL,
            family_trial_count INTEGER NOT NULL CHECK(family_trial_count > 0),
            registered_at_utc TEXT NOT NULL,
            sha256 TEXT NOT NULL,
            canonical_json TEXT NOT NULL,
            FOREIGN KEY(parent_experiment_id) REFERENCES research_trials(experiment_id)
        );
        CREATE UNIQUE INDEX ux_research_trials_family_count
            ON research_trials(family_id, family_trial_count);

        CREATE TABLE research_results (
            result_id TEXT NOT NULL PRIMARY KEY,
            experiment_id TEXT NOT NULL,
            created_at_utc TEXT NOT NULL,
            sha256 TEXT NOT NULL,
            canonical_json TEXT NOT NULL,
            FOREIGN KEY(experiment_id) REFERENCES research_trials(experiment_id)
        );

        CREATE TABLE holdout_consumptions (
            experiment_id TEXT NOT NULL PRIMARY KEY,
            research_run_id TEXT NOT NULL,
            consumed_at_utc TEXT NOT NULL,
            sha256 TEXT NOT NULL,
            canonical_json TEXT NOT NULL,
            FOREIGN KEY(experiment_id) REFERENCES research_trials(experiment_id)
        );
        """;
}
