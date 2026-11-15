using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradingFlow.Backtesting;
using TradingFlow.Backtesting.Artifacts;
using TradingFlow.Data.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Research;
using Xunit.Abstractions;

namespace TradingFlow.Tests;

public sealed class CandidateEvidenceReferenceTests(ITestOutputHelper output)
{
    /// <summary>A corrupt self-reference is rejected after one load and one bounded observation buffer.</summary>
    [Fact]
    public void CyclicLargeFragment_DoesNotAllocateOneBufferPerDepth()
    {
        var run = Guid.NewGuid();
        var hash = new string('a', 64);
        var reference = JsonSerializer.Serialize(new CandidateEvidenceCodec.Reference(CandidateEvidenceCodec.EncodingId, run, hash));
        var recipe = JsonSerializer.Serialize(new CandidateEvidenceCodec.Fragment(CandidateEvidenceCodec.MaximumBytes,
            [new(Sha256: hash)]));
        var reads = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => CandidateEvidenceCodec.Hydrate(run, reference, (_, _) => { reads++; return recipe; }));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(1, reads);
        Assert.True(allocated < CandidateEvidenceCodec.MaximumBytes * 2L, $"Allocated {allocated} bytes for a cyclic reference.");
    }

    /// <summary>Evidence limits poison the audit rather than becoming an ordinary non-fail-fast ticker result.</summary>
    [Fact]
    public async Task OversizedEvidence_IsFatalAuditPersistence_AndPoisonsSubsequentReads()
    {
        var root = Path.Combine(Path.GetTempPath(), $"evidence-fatal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var journal = new BacktestCandidateJournal(Path.Combine(root, "worker.ndjson"));
            var run = BacktestCandidateJournal.CreateRun(new { fixture = "oversized" }, "fixture", DateTimeOffset.UnixEpoch);
            var candidate = new CandidateRecord { CandidateId = Guid.NewGuid(), RunId = run.RunId, State = TradingFlow.Domain.Strategies.StrategyCandidateState.Discovered };
            await journal.UpsertDiscoveryAsync(run, candidate);
            var error = await Assert.ThrowsAsync<TradingFlow.Engine.Execution.AuditPersistenceException>(() => journal.ApplyDecisionAsync(run,
                candidate.CandidateId, 0, "hash", DateTimeOffset.UnixEpoch.AddDays(1),
                [new(candidate.State, TradingFlow.Domain.Strategies.StrategyCandidateState.DataWarming, DateTimeOffset.UnixEpoch,
                    "fixture", "fixture", new string('x', SqliteBacktestCandidateStateStore.MaximumRecordCharacters + 1))]));
            Assert.IsType<InvalidDataException>(error.InnerException);
            await Assert.ThrowsAsync<TradingFlow.Engine.Execution.AuditPersistenceException>(() => journal.GetAsync(candidate.CandidateId));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>A valid content hash is not a substitute for the mandatory kernel semantic decision hash.</summary>
    [Fact]
    public void KernelEvidence_CannotRemoveItsSemanticObject()
    {
        var path = Path.Combine(Path.GetTempPath(), $"evidence-kernel-{Guid.NewGuid():N}.sqlite");
        try
        {
            using var index = new CandidateEvidenceIndex(path, Guid.NewGuid().ToString("D"), create: true);
            var candidate = new CandidateRecord { CandidateId = Guid.NewGuid(), RunId = Guid.NewGuid() };
            var reference = CandidateEvidenceCodec.Encode(candidate.RunId, "{\"other\":true}", (run, hash, recipe) => index.Put(run, hash, recipe));
            var transition = new CandidateTransitionRecord { CandidateId = candidate.CandidateId, RunId = candidate.RunId,
                Source = "strategy_decision_kernel", EvidenceJson = reference, SemanticDecisionSha256 = new string('a', 64) };
            Assert.Throws<InvalidDataException>(() => CandidateAuditReader.Hydrate(candidate, transition, index));
        }
        finally { File.Delete(path); }
    }

    /// <summary>Original inline publications remain read-only evidence with no synthesized sidecar or rewrite.</summary>
    [Fact]
    public async Task OriginalInlinePublication_ReadsWithoutChangingItsBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"evidence-inline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "original.json");
        try
        {
            var candidate = new CandidateRecord { CandidateId = Guid.NewGuid(), RunId = Guid.NewGuid() };
            var transition = new CandidateTransitionRecord { CandidateId = candidate.CandidateId, RunId = candidate.RunId, EvidenceJson = "{\"original\":1.2300}" };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new[] { new BacktestCandidateDecisionAudit(candidate, [transition]) });
            await File.WriteAllBytesAsync(path, bytes);
            var reference = await BacktestArtifactIntegrity.CreateReferenceAsync(root, "candidate_decisions", path, 1);
            var manifest = new BacktestArtifactManifest(1, Guid.NewGuid().ToString("D"), "original", DateTimeOffset.UnixEpoch, true, new(1,1,0,[]), [reference]);
            var count = 0;
            await foreach (var observation in CandidateAuditReader.ReadAsync(root, manifest))
            {
                Assert.Equal(transition.EvidenceJson, observation.Transition!.EvidenceJson);
                count++;
            }
            Assert.Equal(1, count);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            Assert.Single(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Checks original lexical bytes, distinct as-of versions and fail-closed reference errors.</summary>
    [Fact]
    public void Codec_PreservesExactBytesAndAsOfVersions_RejectsMissingTamperedAndCrossRun()
    {
        var fragments = new Dictionary<(Guid, string), string>();
        var run = Guid.NewGuid();
        var history = JsonSerializer.Serialize(Enumerable.Range(0, 40).Select(i => new { i, description = new string('x', 90) }));
        var original = " {\"price\":1.2300, \"history\":" + history + ",\"recovered\":false}\r\n";
        var later = original.Replace("false", "true");
        void Save(Guid owner, string hash, string recipe) => fragments.TryAdd((owner, hash), recipe);
        string Load(Guid owner, string hash) => fragments.TryGetValue((owner, hash), out var value) ? value : throw new InvalidDataException("Missing test fragment.");
        var first = CandidateEvidenceCodec.Encode(run, original, Save);
        var count = fragments.Count;
        var second = CandidateEvidenceCodec.Encode(run, later, Save);
        Assert.NotEqual(first, second);
        Assert.True(fragments.Count < 2 * count);
        Assert.Equal(original, CandidateEvidenceCodec.Hydrate(run, first, Load));
        Assert.Equal(later, CandidateEvidenceCodec.Hydrate(run, second, Load));
        Assert.Throws<InvalidDataException>(() => CandidateEvidenceCodec.Hydrate(Guid.NewGuid(), first, Load));
        var reference = JsonSerializer.Deserialize<CandidateEvidenceCodec.Reference>(first)!;
        var recipe = fragments[(run, reference.Sha256)];
        fragments.Remove((run, reference.Sha256));
        Assert.Throws<InvalidDataException>(() => CandidateEvidenceCodec.Hydrate(run, first, Load));
        var broken = JsonSerializer.Deserialize<CandidateEvidenceCodec.Fragment>(recipe)!;
        fragments[(run, reference.Sha256)] = JsonSerializer.Serialize(broken with { ByteLength = broken.ByteLength + 1 });
        Assert.Throws<InvalidDataException>(() => CandidateEvidenceCodec.Hydrate(run, first, Load));
    }

    /// <summary>Unfinished sidecar transactions stay incomplete, and an index cannot be adopted by another publication.</summary>
    [Fact]
    public void Publication_RollsBackIncompleteRecipes_AndRequiresExactOwner()
    {
        var path = Path.Combine(Path.GetTempPath(), $"evidence-publication-{Guid.NewGuid():N}.sqlite");
        var owner = Guid.NewGuid().ToString("D");
        try
        {
            using (var index = new CandidateEvidenceIndex(path, owner, create: true))
            {
                _ = CandidateEvidenceCodec.Encode(Guid.NewGuid(), "{}", (run, hash, recipe) => index.Put(run, hash, recipe));
                Assert.Equal(1, index.Count());
            }
            using (var index = new CandidateEvidenceIndex(path, owner, create: false)) Assert.Equal(0, index.Count());
            Assert.Throws<IOException>(() => new CandidateEvidenceIndex(path, owner, create: true));
            Assert.Throws<InvalidDataException>(() => new CandidateEvidenceIndex(path, Guid.NewGuid().ToString("D"), create: false));
        }
        finally { File.Delete(path); }
    }

    /// <summary>Measures real SQLite plus journal/export bytes on repeated histories while checking every hydrated semantic decision.</summary>
    [Fact]
    public async Task WorkerAndPublication_DeduplicateRepeatedHistory_WithExactHydrationAndStrictManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), $"evidence-storage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "worker.ndjson");
        var baselinePath = Path.Combine(root, "baseline.sqlite");
        var owner = Guid.NewGuid().ToString("D");
        var run = Guid.NewGuid();
        var expected = new List<BacktestCandidateDecisionAudit>();
        long baselineJournal = 0;
        try
        {
            using (var store = new SqliteBacktestCandidateStateStore(BacktestCandidateJournal.StatePath(journal)))
            using (var baseline = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = baselinePath, Pooling = false }.ToString()))
            using (var journalWriter = new StreamWriter(journal, false, new UTF8Encoding(false)))
            {
                baseline.Open();
                using var command = baseline.CreateCommand();
                command.CommandText = "CREATE TABLE transitions(candidate_id TEXT, sequence INTEGER, payload TEXT, PRIMARY KEY(candidate_id,sequence)); CREATE TABLE candidates(id TEXT PRIMARY KEY, at INTEGER, symbol TEXT, payload TEXT); CREATE INDEX candidates_time ON candidates(at,symbol,id);";
                command.ExecuteNonQuery();
                var history = Enumerable.Range(0, 120).Select(i => new { episode = i, observedAtUtc = "2026-01-01T00:00:00Z", inputs = new string('x', 150) }).ToArray();
                for (var i = 0; i < 96; i++)
                {
                    var candidate = new CandidateRecord { CandidateId = Guid.NewGuid(), RunId = run, Symbol = "TEST", Version = 1, DiscoveredAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(i) };
                    var semantic = new { asOf = i, at = DateTimeOffset.UnixEpoch.AddMinutes(i), episodes = history, recovered = i >= 48 };
                    var evidence = Encoding.UTF8.GetString(EvidenceCanonicalJson.SerializeToUtf8Bytes(new { semantic,
                        drawdownQualification = new { episodes = history, recovered = i >= 48 },
                        rules = new[] { new { evidenceJson = JsonSerializer.Serialize(semantic) } } }));
                    var transition = new CandidateTransitionRecord { CandidateId = candidate.CandidateId, RunId = run, Sequence = 1, EvidenceJson = evidence, SemanticDecisionSha256 = EvidenceCanonicalJson.ComputeSha256(semantic) };
                    expected.Add(new(candidate, [transition]));
                    var encoded = store.Save(candidate, [transition]);
                    Assert.Equal(evidence, Assert.Single(store.GetTransitions(candidate.CandidateId)).EvidenceJson);
                    journalWriter.WriteLine(JsonSerializer.Serialize(new { Candidate = candidate, Transitions = encoded }));
                    baselineJournal += Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(expected[^1]) + Environment.NewLine);
                    command.Parameters.Clear();
                    command.CommandText = "INSERT INTO candidates VALUES($id,$at,$symbol,$candidate); INSERT INTO transitions VALUES($id,1,$transition);";
                    command.Parameters.AddWithValue("$id", candidate.CandidateId.ToString("D"));
                    command.Parameters.AddWithValue("$at", candidate.DiscoveredAtUtc.UtcTicks);
                    command.Parameters.AddWithValue("$symbol", candidate.Symbol);
                    command.Parameters.AddWithValue("$candidate", JsonSerializer.Serialize(candidate));
                    command.Parameters.AddWithValue("$transition", JsonSerializer.Serialize(transition));
                    command.ExecuteNonQuery();
                }
            }
            var decisions = Path.Combine(root, "decisions.json");
            var sidecar = Path.Combine(root, "evidence.sqlite");
            long fragments;
            using (var evidence = new CandidateEvidenceIndex(sidecar, owner, create: true))
            {
                await using var destination = File.Create(decisions);
                Assert.Equal(expected.Count, await CandidateAuditExporter.WriteAsync(destination, [journal], evidence, CancellationToken.None));
                fragments = evidence.Count();
                evidence.Complete();
            }
            var references = new[] {
                await BacktestArtifactIntegrity.CreateReferenceAsync(root, "candidate_decisions", decisions, expected.Count),
                await BacktestArtifactIntegrity.CreateReferenceAsync(root, "candidate_evidence", sidecar, fragments) };
            var manifest = new BacktestArtifactManifest(1, owner, "fixture", DateTimeOffset.UnixEpoch, true, new(1,1,0,[]), references, CandidateEvidenceCodec.EncodingId);
            var actual = new List<BacktestCandidateDecisionAudit>();
            await foreach (var item in CandidateAuditReader.ReadAsync(root, manifest))
                actual.Add(new(item.Candidate, item.Transition is { } transition ? [transition] : []));
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
            var workerBytes = new FileInfo(journal).Length + new FileInfo(BacktestCandidateJournal.StatePath(journal)).Length;
            var baselineWorker = baselineJournal + new FileInfo(baselinePath).Length;
            var publishedBytes = new FileInfo(decisions).Length + new FileInfo(sidecar).Length;
            var baselinePublished = JsonSerializer.SerializeToUtf8Bytes(expected).LongLength;
            output.WriteLine($"Worker: {baselineWorker} -> {workerBytes} bytes; published: {baselinePublished} -> {publishedBytes} bytes; fragments={fragments}; retained run cache=0.");
            Assert.True(workerBytes < baselineWorker / 2);
            Assert.True(publishedBytes < baselinePublished / 2);
            Assert.Throws<InvalidDataException>(() => CandidateAuditReader.ValidateEncoding(manifest with { Artifacts = [references[0]] }));
            Assert.Throws<InvalidDataException>(() => CandidateAuditReader.ValidateEncoding(manifest with { CandidateAuditEncoding = "unknown" }));
            using (var bad = new FileStream(sidecar, FileMode.Open, FileAccess.Write)) { bad.Position = bad.Length - 1; bad.WriteByte(0xFF); }
            await Assert.ThrowsAsync<InvalidDataException>(() => CandidateAuditReader.OpenEvidenceAsync(root, manifest, CancellationToken.None));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
