using TradingFlow.Backtesting.Artifacts;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Storage;
using TradingFlow.Domain.Backtesting;
using System.Text.Json;
using Xunit.Abstractions;

namespace TradingFlow.Tests;

[CollectionDefinition("Audit resource measurements", DisableParallelization = true)]
public sealed class AuditResourceMeasurementCollection;

[Collection("Audit resource measurements")]
public sealed class ExecutionEventJournalTests(ITestOutputHelper output)
{
    [Fact]
    public void LargeAppendVolume_RetainsOnlyConfiguredPreviewWindow()
    {
        var path = Path.Combine(Path.GetTempPath(), $"execution-journal-{Guid.NewGuid():N}.ndjson");
        try
        {
            using var journal = new ExecutionEventJournal(path);
            var auditor = new ExecutionAuditor(1, journal, "candidate_hypothesis");
            var managedBefore = GC.GetTotalMemory(forceFullCollection: true);
            for (var index = 0; index < 10_000; index++)
                auditor.LogEvent("TEST", "strategy", DateTimeOffset.UnixEpoch.AddSeconds(index),
                    ExecutionState.SignalGenerated, $"event-{index}");
            var managedAfter = GC.GetTotalMemory(forceFullCollection: true);
            Assert.Equal(10_000, journal.Count);
            Assert.Single(auditor.GetEvents());
            var diskBytes = new FileInfo(path).Length;
            output.WriteLine(
                $"records={journal.Count}; disk_bytes={diskBytes}; managed_delta_bytes={managedAfter - managedBefore}; retained_preview={auditor.GetEvents().Count}");
            Assert.InRange(diskBytes, 1_000_001, 16L * 1024 * 1024);
            Assert.InRange(managedAfter - managedBefore, Int64.MinValue, 8L * 1024 * 1024);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CancelledExport_RetainsAcceptedSpool_AndAllowsCompleteRetry()
    {
        var path = Path.Combine(Path.GetTempPath(), $"execution-journal-{Guid.NewGuid():N}.ndjson");
        var destination = path + ".json";
        try
        {
            using var journal = new ExecutionEventJournal(path);
            journal.Append(new ExecutionEvent("TEST", "research", DateTimeOffset.UnixEpoch,
                ExecutionState.SignalGenerated, "retained", "candidate_hypothesis", "candidate-1"));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                AtomicFileArtifactWriter.Instance.WriteStreamExclusiveAsync(destination,
                    (stream, token) => JsonSerializer.SerializeAsync(stream, journal.ReadAsync(token),
                        cancellationToken: token), cancellation.Token));
            Assert.False(File.Exists(destination));
            Assert.True(new FileInfo(path).Length > 0);
            await AtomicFileArtifactWriter.Instance.WriteStreamExclusiveAsync(destination,
                (stream, token) => JsonSerializer.SerializeAsync(stream, journal.ReadAsync(token),
                    cancellationToken: token));
            await using var input = File.OpenRead(destination);
            var records = await JsonSerializer.DeserializeAsync<BacktestExecutionAuditEvent[]>(input);
            var record = Assert.Single(records!);
            Assert.Equal("retained", record.Message);
            Assert.Equal("candidate_hypothesis", record.EvidenceScope);
            Assert.Equal("candidate-1", record.ReferenceId);
        }
        finally
        {
            File.Delete(path);
            File.Delete(destination);
        }
    }

    [Fact]
    public async Task ConcurrentProducers_RetainAllEvents_WithBoundedPreview()
    {
        var path = Path.Combine(Path.GetTempPath(), $"execution-journal-{Guid.NewGuid():N}.ndjson");
        try
        {
            using var journal = new ExecutionEventJournal(path);
            var auditor = new ExecutionAuditor(8, journal);
            await Parallel.ForEachAsync(Enumerable.Range(0, 200), (index, _) =>
            {
                auditor.LogEvent("TEST", "research", DateTimeOffset.UnixEpoch,
                    ExecutionState.SignalGenerated, index.ToString());
                return ValueTask.CompletedTask;
            });
            Assert.True(auditor.GetEvents().Count <= 8);
            Assert.Equal(200, journal.Count);
            var messages = new HashSet<string>();
            await foreach (var record in journal.ReadAsync()) Assert.True(messages.Add(record.Message));
            Assert.Equal(200, messages.Count);
            Assert.Throws<InvalidOperationException>(() => auditor.LogEvent("TEST", "research",
                DateTimeOffset.UnixEpoch, ExecutionState.SignalGenerated, "late"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OversizedRecord_FailsBeforeAcceptance_AndPreservesSpool()
    {
        var path = Path.Combine(Path.GetTempPath(), $"execution-journal-{Guid.NewGuid():N}.ndjson");
        try
        {
            using (var journal = new ExecutionEventJournal(path))
            {
                var auditor = new ExecutionAuditor(8, journal);
                Assert.Throws<AuditPersistenceException>(() => auditor.LogEvent("TEST", "research",
                    DateTimeOffset.UnixEpoch, ExecutionState.SignalGenerated,
                    new string('x', ExecutionEventJournal.MaximumRecordBytes + 1)));
                Assert.Equal(0, journal.Count);
                Assert.Empty(auditor.GetEvents());
                Assert.Throws<AuditPersistenceException>(() => auditor.LogEvent("TEST", "research",
                    DateTimeOffset.UnixEpoch, ExecutionState.SignalGenerated, "cannot resume failed evidence"));
            }
            Assert.True(File.Exists(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task OversizedMultibyteEvidence_FailsBeforeWritingAnyPartOfTheRecord()
    {
        var path = Path.Combine(Path.GetTempPath(), $"execution-journal-{Guid.NewGuid():N}.ndjson");
        try
        {
            using (var journal = new ExecutionEventJournal(path))
            {
                journal.Append(new ExecutionEvent("TEST", "research", DateTimeOffset.UnixEpoch,
                    ExecutionState.SignalGenerated, "retained", "candidate_hypothesis", "candidate-1"));

                Assert.Throws<AuditPersistenceException>(() => journal.Append(new ExecutionEvent(
                    "TEST",
                    "research",
                    DateTimeOffset.UnixEpoch,
                    ExecutionState.SignalGenerated,
                    "oversized",
                    "candidate_hypothesis",
                    "candidate-2",
                    new string('\u20ac', ExecutionEventJournal.MaximumRecordBytes))));
            }

            var lines = await File.ReadAllLinesAsync(path);
            Assert.Single(lines);
            Assert.Contains("retained", lines[0], StringComparison.Ordinal);
            Assert.DoesNotContain("candidate-2", lines[0], StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }
}
