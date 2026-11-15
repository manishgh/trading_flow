using Microsoft.Data.Sqlite;
using TradingFlow.Domain.Research;

namespace TradingFlow.Data.Evidence.Governance;

public sealed partial class SqliteResearchTrialRegistry
{
    /// <summary>Reads verified registry metadata through a SQLite read-only connection without opening market datasets or creating a registry.</summary>
    public async Task<ResearchLedger> ReadLedgerAsync(CancellationToken token)
    {
        if (!File.Exists(databasePath)) return new([], []);
        var readOnly = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        await using var connection = new SqliteConnection(readOnly.ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var version = await ReadSchemaVersionAsync(connection, transaction, token);
        if (version is null) return new([], []);
        if (version is not ("2" or "3"))
            throw new InvalidDataException($"Research trial registry schema '{version}' is unsupported.");
        var trials = await ReadVerifiedRowsAsync<ResearchTrialDefinition>(connection, transaction,
            "SELECT canonical_json, sha256 FROM research_trials ORDER BY family_id, family_trial_count;", token);
        var results = await ReadVerifiedRowsAsync<CanonicalResearchResultManifest>(connection, transaction,
            "SELECT canonical_json, sha256 FROM research_results ORDER BY created_at_utc;", token);
        var holdouts = await ReadVerifiedRowsAsync<ResearchHoldoutConsumptionRecord>(connection, transaction,
            "SELECT canonical_json, sha256 FROM holdout_consumptions;", token);
        var byTrial = results.ToLookup(result => result.ExperimentId);
        var byHoldout = holdouts.ToDictionary(holdout => holdout.ExperimentId, StringComparer.Ordinal);
        var knownTrials = trials.Select(trial => ProjectLedgerTrial(trial, byTrial[trial.ExperimentId].ToArray(),
            byHoldout.GetValueOrDefault(trial.ExperimentId))).ToArray();
        if (version == "2") return new(knownTrials, []);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT start_id FROM research_starts ORDER BY registered_at_utc DESC, start_id;";
        var ids = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) ids.Add(reader.GetString(0));

        var verifiedStarts = new List<ResearchLedgerStart>(ids.Count);
        foreach (var id in ids)
        {
            var start = await ReadStartAsync(connection, transaction, id, token)
                ?? throw new InvalidDataException("A research start disappeared from the ledger.");
            await RequireBoundTrialsAsync(connection, transaction, start.Binding, token);
            var executions = await ReadVerifiedRowsAsync<ResearchExecutionResult>(connection, transaction,
                "SELECT canonical_json, sha256 FROM research_execution_results WHERE start_id=$id ORDER BY sequence;", token, id);
            if (executions.Any(result => !start.Binding.Request.Executions.ContainsKey(result.ExecutionId)))
                throw new InvalidDataException("A ledger outcome belongs to an execution outside its frozen start.");
            verifiedStarts.Add(new(start, executions));
        }
        return new(knownTrials, verifiedStarts);
    }

    /// <summary>Reports holdout state only from exact frozen partitions and verified one-use registration records.</summary>
    private static ResearchLedgerTrial ProjectLedgerTrial(ResearchTrialDefinition trial,
        IReadOnlyList<CanonicalResearchResultManifest> results, ResearchHoldoutConsumptionRecord? consumption)
    {
        var hash = EvidenceCanonicalJson.ComputeSha256(trial);
        if (results.Any(result => result.TrialDefinitionSha256 != hash || result.PrimaryMetric != trial.PrimaryMetric ||
            !trial.Partitions.Any(partition => partition.Name == result.Partition)))
            throw new InvalidDataException("A canonical research result does not match its frozen trial.");
        var holdout = results.SingleOrDefault(result => result.Partition.Equals("holdout", StringComparison.OrdinalIgnoreCase));
        if (holdout is not null && consumption?.ResearchRunId != holdout.ResearchRunId)
            throw new InvalidDataException("A holdout result does not match its one-use reservation.");
        ResearchHoldoutState? state = null;
        if (trial.Partitions.Any(partition => partition.Name.Equals("holdout", StringComparison.OrdinalIgnoreCase)))
            state = holdout is not null ? ResearchHoldoutState.Completed :
                consumption is not null ? ResearchHoldoutState.Consumed : ResearchHoldoutState.Unopened;
        return new(trial, hash, state, results);
    }
}
