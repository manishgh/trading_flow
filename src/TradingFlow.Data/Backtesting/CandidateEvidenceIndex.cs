using Microsoft.Data.Sqlite;
using System.IO.Compression;
using System.Text;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Data.Backtesting;

/// <summary>Disk-backed exact evidence recipes, scoped to their original decision run; no whole-run memory cache.</summary>
public sealed class CandidateEvidenceIndex : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly bool ownsConnection;
    private SqliteTransaction? publicationTransaction;

    /// <summary>Uses the worker's existing transaction and SQLite page budget for both references and fragments.</summary>
    internal CandidateEvidenceIndex(SqliteConnection connection, bool create)
    {
        this.connection = connection;
        if (create) CreateTables();
    }

    /// <summary>Creates a new publication index or opens an existing immutable one and verifies its artifact-set owner.</summary>
    public CandidateEvidenceIndex(string path, string artifactSetId, bool create)
    {
        if (create && File.Exists(path)) throw new IOException("Candidate evidence publication already exists.");
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false, Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadOnly
        }.ToString());
        ownsConnection = true;
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA cache_size=-256; PRAGMA temp_store=FILE;";
            command.ExecuteNonQuery();
            if (create)
            {
                CreateTables();
                command.CommandText = "CREATE TABLE evidence_owner(encoding TEXT NOT NULL, artifact_set_id TEXT NOT NULL); INSERT INTO evidence_owner VALUES($encoding,$owner);";
                command.Parameters.AddWithValue("$encoding", CandidateEvidenceCodec.EncodingId);
                command.Parameters.AddWithValue("$owner", artifactSetId);
                command.ExecuteNonQuery();
                publicationTransaction = connection.BeginTransaction();
            }
            else
            {
                command.CommandText = "SELECT encoding, artifact_set_id FROM evidence_owner";
                using var reader = command.ExecuteReader();
                if (!reader.Read() || reader.GetString(0) != CandidateEvidenceCodec.EncodingId || reader.GetString(1) != artifactSetId || reader.Read())
                    throw new InvalidDataException("Candidate evidence sidecar belongs to another encoding or artifact set.");
            }
        }
        catch { connection.Dispose(); throw; }
    }

    /// <summary>Creates a unique immutable fragment key; conflict checks below refuse a different recipe for the same bytes.</summary>
    private void CreateTables()
    {
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE candidate_evidence(run_id TEXT NOT NULL, sha256 TEXT NOT NULL, recipe BLOB NOT NULL, PRIMARY KEY(run_id,sha256));";
        command.ExecuteNonQuery();
    }

    /// <summary>Persists a recipe in the caller's transaction before any reference can become durable.</summary>
    public void Put(Guid runId, string hash, string recipe, SqliteTransaction? transaction = null)
        => PutEncoded(runId, hash, Compress(recipe), transaction);

    /// <summary>Copies the compressed immutable representation without reparsing historical observations.</summary>
    private void PutEncoded(Guid runId, string hash, byte[] recipe, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction ?? publicationTransaction;
        command.CommandText = "INSERT INTO candidate_evidence VALUES($run,$hash,$recipe) ON CONFLICT(run_id,sha256) DO NOTHING;";
        command.Parameters.AddWithValue("$run", runId.ToString("D"));
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$recipe", recipe);
        if (command.ExecuteNonQuery() != 0) return;
        command.CommandText = "SELECT CASE WHEN length(recipe) <= $limit THEN recipe END FROM candidate_evidence WHERE run_id=$run AND sha256=$hash";
        command.Parameters.AddWithValue("$limit", CandidateEvidenceCodec.MaximumBytes * 6L + 1024);
        if (command.ExecuteScalar() is not byte[] existing || !existing.AsSpan().SequenceEqual(recipe))
            throw new InvalidDataException("Conflicting candidate evidence recipes share a content hash.");
    }

    /// <summary>Reads the exact owner's immutable fragment; a matching hash under another run is never substituted.</summary>
    private string Load(Guid runId, string hash)
    {
        using var command = connection.CreateCommand();
        command.Transaction = publicationTransaction;
        command.CommandText = "SELECT CASE WHEN length(recipe) <= $limit THEN recipe END FROM candidate_evidence WHERE run_id=$run AND sha256=$hash";
        command.Parameters.AddWithValue("$limit", CandidateEvidenceCodec.MaximumBytes * 6L + 1024);
        command.Parameters.AddWithValue("$run", runId.ToString("D"));
        command.Parameters.AddWithValue("$hash", hash);
        return command.ExecuteScalar() is byte[] bytes ? Expand(bytes)
            : throw new InvalidDataException("Candidate evidence fragment is missing or oversized for this run.");
    }

    /// <summary>Copies a transition and replaces only its evidence representation; decision and transition identities are untouched.</summary>
    public CandidateTransitionRecord Encode(CandidateTransitionRecord transition, SqliteTransaction transaction)
    {
        var copy = Copy(transition);
        copy.EvidenceJson = CandidateEvidenceCodec.Encode(copy.RunId, copy.EvidenceJson, (run, hash, recipe) => Put(run, hash, recipe, transaction));
        return copy;
    }

    /// <summary>Hydrates exact bytes for ordinary repository consumers without changing their inline evidence contract.</summary>
    public CandidateTransitionRecord Hydrate(CandidateTransitionRecord transition)
    {
        var copy = Copy(transition);
        copy.EvidenceJson = CandidateEvidenceCodec.Hydrate(copy.RunId, copy.EvidenceJson, Load);
        return copy;
    }

    /// <summary>Streams immutable recipes from one worker into the single publication index with bounded page caches.</summary>
    public void CopyTo(CandidateEvidenceIndex destination, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.Transaction = publicationTransaction;
        command.CommandText = "SELECT run_id,sha256,CASE WHEN length(recipe) <= $limit THEN recipe END FROM candidate_evidence ORDER BY run_id,sha256";
        command.Parameters.AddWithValue("$limit", CandidateEvidenceCodec.MaximumBytes * 6L + 1024);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.IsDBNull(2)) throw new InvalidDataException("Candidate evidence fragment exceeds the publication bound.");
            destination.PutEncoded(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetFieldValue<byte[]>(2));
        }
    }

    /// <summary>Returns the disk index count without loading its contents.</summary>
    public long Count()
    {
        using var command = connection.CreateCommand();
        command.Transaction = publicationTransaction;
        command.CommandText = "SELECT COUNT(*) FROM candidate_evidence";
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>Commits only a complete publication; disposal before this point rolls back its fragment transaction.</summary>
    public void Complete()
    {
        publicationTransaction?.Commit();
        publicationTransaction?.Dispose();
        publicationTransaction = null;
    }

    /// <summary>Compresses immutable recipes, including repeated JSON embedded as strings, without altering their exact-byte hashes.</summary>
    private static byte[] Compress(string recipe)
    {
        using var output = new MemoryStream();
        using (var compressor = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
            compressor.Write(Encoding.UTF8.GetBytes(recipe));
        return output.ToArray();
    }

    /// <summary>Bounds decompression before returning a recipe; malformed data cannot expand beyond one supported evidence record.</summary>
    private static string Expand(byte[] bytes)
    {
        using var input = new MemoryStream(bytes, writable: false);
        using var decompressor = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = decompressor.Read(buffer)) != 0)
        {
            if (output.Length + read > CandidateEvidenceCodec.MaximumBytes * 6L)
                throw new InvalidDataException("Candidate evidence recipe exceeds its decompression bound.");
            output.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

    /// <summary>Copies only one bounded transition, preventing persisted representation changes from mutating caller-owned objects.</summary>
    private static CandidateTransitionRecord Copy(CandidateTransitionRecord value) => new()
    {
        TransitionId = value.TransitionId, CandidateId = value.CandidateId, Sequence = value.Sequence,
        PreviousState = value.PreviousState, NewState = value.NewState, OccurredAtUtc = value.OccurredAtUtc,
        ReasonCode = value.ReasonCode, Source = value.Source, SemanticDecisionSha256 = value.SemanticDecisionSha256,
        EvidenceJson = value.EvidenceJson, RunId = value.RunId, SchemaVersion = value.SchemaVersion,
        ConfigHash = value.ConfigHash, CodeVersion = value.CodeVersion
    };

    /// <summary>Rolls back unfinished publication writes and closes only connections owned by this index.</summary>
    public void Dispose()
    {
        publicationTransaction?.Dispose();
        if (ownsConnection) connection.Dispose();
    }
}
