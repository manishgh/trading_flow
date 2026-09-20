using Microsoft.Data.Sqlite;
using System.Text.Json;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Data.Backtesting;

/// <summary>Run-local indexed state; bounded SQLite page caching replaces retained candidate dictionaries.</summary>
public sealed class SqliteBacktestCandidateStateStore : IDisposable
{
    public const int MaximumRecordCharacters = 4 * 1024 * 1024;
    private readonly SqliteConnection connection;

    public SqliteBacktestCandidateStateStore(string path, bool readOnly = false)
    {
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate
        }.ToString());
        try
        {
            connection.Open();
            connection.CreateCollation("SYMBOL_ORDER", StringComparer.OrdinalIgnoreCase.Compare);
            connection.CreateCollation("GUID_ORDER", (left, right) => Guid.Parse(left!).CompareTo(Guid.Parse(right!)));
            Execute(readOnly ? "PRAGMA cache_size=-64; PRAGMA temp_store=FILE;" : "PRAGMA cache_size=-2048; PRAGMA temp_store=FILE;");
            if (!readOnly)
            {
                Execute("""
                    PRAGMA journal_mode=DELETE;
                    PRAGMA synchronous=FULL;
                    CREATE TABLE candidates(id TEXT PRIMARY KEY, at INTEGER NOT NULL, symbol TEXT NOT NULL, payload TEXT NOT NULL);
                    CREATE INDEX candidates_time ON candidates(at, symbol COLLATE SYMBOL_ORDER, id COLLATE GUID_ORDER);
                    CREATE TABLE transitions(candidate_id TEXT NOT NULL, sequence INTEGER NOT NULL, payload TEXT NOT NULL,
                        PRIMARY KEY(candidate_id, sequence));
                    """);
            }
        }
        catch { connection.Dispose(); throw; }
    }

    public CandidateRecord? Get(Guid id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM candidates WHERE id=$id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        return command.ExecuteScalar() is string json
            ? JsonSerializer.Deserialize<CandidateRecord>(json) ?? throw new InvalidDataException("Null candidate in audit index.")
            : null;
    }

    public IReadOnlyList<CandidateTransitionRecord> GetTransitions(Guid id)
        => ReadTransitions(id).ToArray();

    public IEnumerable<CandidateTransitionRecord> ReadTransitions(Guid id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM transitions WHERE candidate_id=$id ORDER BY sequence";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        using var reader = command.ExecuteReader();
        while (reader.Read()) yield return JsonSerializer.Deserialize<CandidateTransitionRecord>(reader.GetString(0))
            ?? throw new InvalidDataException("Null candidate transition in audit index.");
    }

    public void Save(CandidateRecord candidate, IReadOnlyList<CandidateTransitionRecord> appended)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO candidates(id,at,symbol,payload) VALUES($id,$at,$symbol,$payload)
            ON CONFLICT(id) DO UPDATE SET payload=excluded.payload;
            """;
        command.Parameters.AddWithValue("$id", candidate.CandidateId.ToString("D"));
        command.Parameters.AddWithValue("$at", candidate.DiscoveredAtUtc.UtcTicks);
        command.Parameters.AddWithValue("$symbol", candidate.Symbol);
        command.Parameters.AddWithValue("$payload", SerializeBounded(candidate));
        command.ExecuteNonQuery();
        foreach (var item in appended)
        {
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO transitions(candidate_id,sequence,payload) VALUES($id,$sequence,$payload)";
            command.Parameters.AddWithValue("$id", candidate.CandidateId.ToString("D"));
            command.Parameters.AddWithValue("$sequence", item.Sequence);
            command.Parameters.AddWithValue("$payload", SerializeBounded(item));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>Reads one candidate at a time using the chronology index, without sorting history in memory.</summary>
    public IEnumerable<CandidateRecord> ReadCandidates(CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM candidates ORDER BY at,symbol COLLATE SYMBOL_ORDER,id COLLATE GUID_ORDER";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = JsonSerializer.Deserialize<CandidateRecord>(reader.GetString(0))
                ?? throw new InvalidDataException("Null candidate in audit index.");
            yield return candidate;
        }
    }

    private void Execute(string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string SerializeBounded<T>(T value)
    {
        var json = JsonSerializer.Serialize(value);
        if (json.Length > MaximumRecordCharacters)
            throw new InvalidDataException("Candidate audit record exceeds the supported size; evidence was not truncated.");
        return json;
    }

    public void Dispose() => connection.Dispose();
}
