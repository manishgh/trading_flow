using TradingFlow.Backtesting;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Strategies;
using TradingFlow.Backtesting.Artifacts;
using TradingFlow.Domain.Backtesting;
using System.Text.Json;
using TradingFlow.Data.Backtesting;
using Microsoft.Data.Sqlite;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Tests;

public sealed class BacktestCandidateJournalTests
{
    [Fact]
    public void InitializationFailure_IsAuditFatal_AndDoesNotOverwriteEvidence()
    {
        var path = Path.Combine(Path.GetTempPath(), $"candidate-existing-{Guid.NewGuid():N}.ndjson");
        try
        {
            File.WriteAllText(path, "existing evidence");
            Assert.Throws<AuditPersistenceException>(() => new BacktestCandidateJournal(path));
            Assert.Equal("existing evidence", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void IndexFailure_RollsBackCandidateAndTransitionsTogether()
    {
        var path = Path.Combine(Path.GetTempPath(), $"candidate-index-{Guid.NewGuid():N}.sqlite");
        try
        {
            using var store = new SqliteBacktestCandidateStateStore(path);
            var candidate = new CandidateRecord { CandidateId = Guid.NewGuid(), Version = 1 };
            var transition = new CandidateTransitionRecord { CandidateId = candidate.CandidateId, Sequence = 1 };
            store.Save(candidate, [transition]);
            candidate.Version = 2;
            Assert.Throws<SqliteException>(() => store.Save(candidate, [transition]));
            Assert.Equal(1, store.Get(candidate.CandidateId)!.Version);
            Assert.Single(store.GetTransitions(candidate.CandidateId));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Export_UsesInstantSymbolAndGuidOrdering_AndCancellationClosesReaders()
    {
        var root = Path.Combine(Path.GetTempPath(), $"candidate-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "journal");
        try
        {
            var candidates = new[]
            {
                new CandidateRecord { CandidateId = Guid.Parse("ffffffff-0000-0000-0000-000000000000"), Symbol = "AAA", DiscoveredAtUtc = DateTimeOffset.UnixEpoch },
                new CandidateRecord { CandidateId = Guid.Parse("00000001-0000-0000-0000-000000000000"), Symbol = "aaa", DiscoveredAtUtc = DateTimeOffset.UnixEpoch.ToOffset(TimeSpan.FromHours(2)) },
                new CandidateRecord { CandidateId = Guid.NewGuid(), Symbol = "BBB", DiscoveredAtUtc = DateTimeOffset.UnixEpoch }
            };
            using (var store = new SqliteBacktestCandidateStateStore(BacktestCandidateJournal.StatePath(path)))
                foreach (var candidate in candidates) store.Save(candidate, []);
            using var output = new MemoryStream();
            await CandidateAuditExporter.WriteAsync(output, [path], CancellationToken.None);
            output.Position = 0;
            var actual = await JsonSerializer.DeserializeAsync<BacktestCandidateDecisionAudit[]>(output);
            Assert.NotNull(actual);
            Assert.Equal(candidates.OrderBy(item => item.DiscoveredAtUtc)
                .ThenBy(item => item.Symbol, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.CandidateId)
                .Select(item => item.CandidateId), actual.Select(item => item.Candidate.CandidateId));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CandidateAuditExporter.WriteAsync(output, [path], cancelled.Token));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task DurableHistory_RetainsOldVersions_AndExportsInterleavedChronology()
    {
        var root = Path.Combine(Path.GetTempPath(), $"candidate-history-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var paths = new[] { Path.Combine(root, "a.ndjson"), Path.Combine(root, "b.ndjson") };
        var expected = new List<Guid>();
        try
        {
            var run = BacktestCandidateJournal.CreateRun(new { Test = "history" }, "history", DateTimeOffset.UnixEpoch);
            using (var first = new BacktestCandidateJournal(paths[0]))
            using (var second = new BacktestCandidateJournal(paths[1]))
            {
                for (var index = 0; index < 100; index++)
                {
                    var journal = index % 2 == 0 ? first : second;
                    var candidate = new CandidateRecord
                    {
                        CandidateId = Guid.NewGuid(), RunId = run.RunId, Symbol = "TEST",
                        State = StrategyCandidateState.Discovered,
                        DiscoveredAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(index)
                    };
                    expected.Add(candidate.CandidateId);
                    await journal.UpsertDiscoveryAsync(run, candidate);
                    await journal.ApplyDecisionAsync(run, candidate.CandidateId, 0, "hash",
                        candidate.DiscoveredAtUtc.AddMinutes(1),
                        [new(StrategyCandidateState.Discovered, StrategyCandidateState.DataWarming,
                            candidate.DiscoveredAtUtc, "warm", "test", "{}")]);
                }
                var old = await first.GetAsync(expected[0]);
                Assert.Equal(1, old!.Version);
                Assert.Equal(StrategyCandidateState.DataWarming, old.State);
                Assert.Single(await first.GetTransitionsAsync(old.CandidateId));
                Assert.Equal(0, first.RetainedCandidateCount);
                Assert.Equal(0, second.RetainedCandidateCount);
                Assert.Throws<InvalidOperationException>(() => first.Snapshot());
            }
            using var output = new MemoryStream();
            await CandidateAuditExporter.WriteAsync(output, paths, CancellationToken.None);
            output.Position = 0;
            var audits = await JsonSerializer.DeserializeAsync<BacktestCandidateDecisionAudit[]>(output);
            Assert.NotNull(audits);
            Assert.Equal(expected, audits.Select(audit => audit.Candidate.CandidateId));
            Assert.All(audits, audit => Assert.Single(audit.Transitions));
            Assert.All(paths, path => Assert.True(File.Exists(path)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task InvalidSecondTransition_DoesNotPublishFirstTransition()
    {
        using var journal = new BacktestCandidateJournal();
        var run = BacktestCandidateJournal.CreateRun(new { Test = "atomic-batch" }, "test", DateTimeOffset.UnixEpoch);
        var candidate = new CandidateRecord
        {
            CandidateId = Guid.NewGuid(), RunId = run.RunId,
            State = StrategyCandidateState.Discovered
        };
        await journal.UpsertDiscoveryAsync(run, candidate);
        CandidateTransitionAppendRequest Transition(StrategyCandidateState from, StrategyCandidateState to) =>
            new(from, to, DateTimeOffset.UnixEpoch, "test", "test", "{}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.ApplyDecisionAsync(
            run, candidate.CandidateId, 0, "hash", DateTimeOffset.UnixEpoch.AddHours(1),
            [Transition(StrategyCandidateState.Discovered, StrategyCandidateState.DataWarming),
             Transition(StrategyCandidateState.Armed, StrategyCandidateState.Triggered)]));
        Assert.Empty(await journal.GetTransitionsAsync(candidate.CandidateId));
        var persisted = await journal.GetAsync(candidate.CandidateId);
        Assert.Equal(0, persisted!.Version);
        Assert.Equal(StrategyCandidateState.Discovered, persisted.State);
    }
}
