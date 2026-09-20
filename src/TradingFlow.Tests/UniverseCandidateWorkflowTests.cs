using Moq;
using TradingFlow.Application.Candidates;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Discovery;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Tests;

public sealed class UniverseCandidateWorkflowTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-16T12:00:00Z");

    [Fact]
    public async Task Preview_RejectsFutureSourceObservationBeforePersistence()
    {
        var source = new Mock<IUniverseSourceResolver>();
        source.Setup(item => item.ResolveAsync(It.IsAny<UniverseSourceSelectionRequest>(), "swing", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UniverseSourceCapture(
                DiscoverySourceKinds.Wishlist,
                "watch",
                Now.AddSeconds(1),
                Now.AddMinutes(3),
                [new DiscoverySymbolObservation("MSFT")]));
        var repository = new Mock<IDiscoveryRepository>(MockBehavior.Strict);
        var service = new UniverseDiscoveryService(
            source.Object,
            repository.Object,
            Mock.Of<IUniversePreviewRepository>(),
            Mock.Of<IApplicationEventRepository>(),
            new FixedTimeProvider(Now));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PreviewAsync(
            new UniversePreviewRequest(
                ContractVersions.VersionOne,
                "swing",
                Now,
                [new UniverseSourceSelectionRequest("wishlist", "watch")] )));

        Assert.Contains("future", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CandidateStart_IsIdempotentAndPersistsExactStrategyIdentity()
    {
        var snapshotId = Guid.NewGuid();
        var strategy = new StrategyReference("swing.momentum", "2.0.0", new string('a', 64));
        var aggregates = new[]
        {
            new ActiveDiscoveryAggregate(
                Guid.NewGuid(),
                snapshotId,
                "MSFT",
                "swing",
                Now.AddSeconds(-5),
                Now.AddSeconds(-5),
                Now.AddMinutes(2),
                1,
                [new DiscoverySourceEvidence(
                    DiscoverySourceKinds.Wishlist,
                    "watch",
                    Now.AddSeconds(-5),
                    Now.AddMinutes(2),
                    "{}",
                    Guid.NewGuid(),
                    1,
                    Now.AddSeconds(-5),
                    false,
                    "wishlist:watch",
                    new string('b', 64))])
        };
        var preview = ToPreview(snapshotId, aggregates);
        var workflowStore = new WorkflowStore(preview);
        var store = new CandidateStore();
        var strategyResolver = new Mock<IStrategyIdentityResolver>();
        var service = new CandidateWorkflowService(
            workflowStore,
            store,
            store,
            strategyResolver.Object,
            new FixedTimeProvider(Now));
        var request = new CreateCandidateRunRequest(
            ContractVersions.VersionOne,
            "candidate-start-1",
            "backtest",
            snapshotId,
            [strategy],
            Now);

        var first = await service.StartAsync(request);
        var replay = await service.StartAsync(request);

        Assert.Equal(first.CandidateRunId, replay.CandidateRunId);
        var candidate = Assert.Single(first.Candidates);
        Assert.Equal(strategy, candidate.Strategy);
        Assert.Equal("swing", store.Records.Single().Horizon);
        Assert.Single(store.Records);
        strategyResolver.Verify(item => item.RequireSwingStrategyAsync(strategy, "backtest", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task CandidateStart_RejectsTamperedStrategyIdentity()
    {
        var snapshotId = Guid.NewGuid();
        var strategies = new Mock<IStrategyIdentityResolver>();
        strategies.Setup(item => item.RequireSwingStrategyAsync(It.IsAny<StrategyReference>(), "backtest", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("content hash mismatch"));
        var store = new CandidateStore();
        var workflowStore = new WorkflowStore(null);
        var service = new CandidateWorkflowService(workflowStore, store, store, strategies.Object, new FixedTimeProvider(Now));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(
            new CreateCandidateRunRequest(
                ContractVersions.VersionOne,
                "candidate-start-tamper",
                "backtest",
                snapshotId,
                [new StrategyReference("swing.momentum", "2.0.0", new string('c', 64))],
                Now)));

        Assert.Contains("hash", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CandidateStart_RejectsIdempotencyKeyReuseWithDifferentContent()
    {
        var snapshotId = Guid.NewGuid();
        var aggregates = new[]
        {
            new ActiveDiscoveryAggregate(Guid.NewGuid(), snapshotId, "MSFT", "swing", Now, Now, Now.AddMinutes(2), 1,
                [new DiscoverySourceEvidence("wishlist", "watch", Now, Now.AddMinutes(2), "{}", Guid.NewGuid(), 1, Now, false, "wishlist:watch", new string('d', 64))])
        };
        var workflowStore = new WorkflowStore(ToPreview(snapshotId, aggregates));
        var candidates = new CandidateStore();
        var resolver = Mock.Of<IStrategyIdentityResolver>();
        var service = new CandidateWorkflowService(workflowStore, candidates, candidates, resolver, new FixedTimeProvider(Now));
        var first = new CreateCandidateRunRequest(ContractVersions.VersionOne, "same-key", "backtest", snapshotId,
            [new StrategyReference("swing.one", "1.0.0", new string('1', 64))], Now);
        var changed = first with
        {
            Strategies = [new StrategyReference("swing.two", "1.0.0", new string('2', 64))]
        };

        await service.StartAsync(first);
        await Assert.ThrowsAsync<IdempotencyKeyConflictException>(() => service.StartAsync(changed));
    }

    private static UniversePreviewRecord ToPreview(
        Guid snapshotId,
        IReadOnlyList<ActiveDiscoveryAggregate> aggregates)
    {
        var response = UniverseDiscoveryService.ToResponse(snapshotId, Now, aggregates);
        return new UniversePreviewRecord
        {
            UniverseSnapshotId = snapshotId,
            Horizon = "swing",
            RequestSha256 = new string('e', 64),
            ContentSha256 = response.ContentSha256,
            ResolvedAtUtc = response.ResolvedAtUtc,
            ExpiresAtUtc = response.ExpiresAtUtc,
            PreviewJson = System.Text.Json.JsonSerializer.Serialize(response)
        };
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class CandidateStore : ICandidateRepository, ICandidateQueryService
    {
        private ProductionRun? run;
        public List<CandidateRecord> Records { get; } = [];

        public Task<CandidateRecord> UpsertDiscoveryAsync(
            ProductionRun requestedRun,
            CandidateRecord candidate,
            CancellationToken cancellationToken = default)
        {
            run ??= requestedRun;
            var existing = Records.SingleOrDefault(item => item.CandidateId == candidate.CandidateId);
            if (existing is null)
            {
                Records.Add(candidate);
            }
            return Task.FromResult(existing ?? candidate);
        }

        public Task<CandidateRunResponse?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
        {
            if (run?.RunId != runId)
            {
                return Task.FromResult<CandidateRunResponse?>(null);
            }
            var states = Records.Select(item => new CandidateStateResponse(
                item.CandidateId,
                item.Version,
                item.Symbol,
                new StrategyReference(item.SelectedStrategy!, item.StrategySemanticVersion, item.StrategyContentSha256),
                item.State.ToString(),
                "watching",
                null,
                item.DiscoveredAtUtc,
                item.RevalidatedAtUtc,
                item.ExpiresAtUtc,
                null,
                null,
                null,
                [])).ToArray();
            return Task.FromResult<CandidateRunResponse?>(new CandidateRunResponse(
                ContractVersions.VersionOne,
                runId,
                new RunProvenance(run.Profile, run.UniverseSnapshotId, run.DecisionRunId, states.Select(item => item.Strategy).Distinct().ToArray()),
                run.Status,
                run.StartedAtUtc,
                run.StartedAtUtc,
                null,
                0,
                states));
        }

        public Task<CandidateStateResponse?> GetCandidateAsync(Guid candidateId, CancellationToken cancellationToken = default) =>
            Task.FromResult<CandidateStateResponse?>(null);

        public Task<CandidateRecord> ApplyDecisionAsync(ProductionRun run, Guid candidateId, int expectedVersion, string semanticDecisionSha256, DateTimeOffset expiresAtUtc, IReadOnlyList<CandidateTransitionAppendRequest> transitions, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CandidateRecord?> GetAsync(Guid candidateId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Records.SingleOrDefault(item => item.CandidateId == candidateId));

        public Task<IReadOnlyList<CandidateRecord>> ListByRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CandidateRecord>>(Records.Where(item => item.RunId == runId).ToArray());

        public Task<IReadOnlyList<CandidateTransitionRecord>> GetTransitionsAsync(Guid candidateId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CandidateTransitionRecord>>([]);
    }

    private sealed class WorkflowStore(UniversePreviewRecord? preview) : IUniversePreviewRepository, IApplicationEventRepository
    {
        private readonly Dictionary<string, CandidateRunRequestRecord> requests = new(StringComparer.Ordinal);
        private readonly List<ApplicationEventRecord> events = [];

        public Task SaveAsync(UniversePreviewRecord value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<UniversePreviewRecord?> GetAsync(Guid universeSnapshotId, CancellationToken cancellationToken = default) =>
            Task.FromResult(preview?.UniverseSnapshotId == universeSnapshotId ? preview : null);

        public Task<CandidateRunReservation> ReserveCandidateRunAsync(CandidateRunRequestRecord request, CancellationToken cancellationToken = default)
        {
            if (requests.TryGetValue(request.IdempotencyKey, out var existing))
            {
                if (existing.RequestSha256 != request.RequestSha256)
                {
                    throw new IdempotencyKeyConflictException(request.IdempotencyKey);
                }
                return Task.FromResult(new CandidateRunReservation(existing.CandidateRunId, false));
            }
            requests.Add(request.IdempotencyKey, request);
            return Task.FromResult(new CandidateRunReservation(request.CandidateRunId, true));
        }

        public Task<long> AppendAsync(string streamName, string eventType, DateTimeOffset occurredAtUtc, string payloadJson, CancellationToken cancellationToken = default)
        {
            var sequence = events.Count + 1L;
            events.Add(new ApplicationEventRecord { EventSequence = sequence, StreamName = streamName, EventType = eventType, OccurredAtUtc = occurredAtUtc, PayloadJson = payloadJson });
            return Task.FromResult(sequence);
        }

        public Task<IReadOnlyList<ApplicationEventRecord>> ListAsync(string streamName, long afterSequence, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ApplicationEventRecord>>(events.Where(item => item.StreamName == streamName && item.EventSequence > afterSequence).Take(limit).ToArray());

        public Task<(long Floor, long Latest)> GetBoundsAsync(string streamName, CancellationToken cancellationToken = default)
        {
            var values = events.Where(item => item.StreamName == streamName).Select(item => item.EventSequence).ToArray();
            return Task.FromResult(values.Length == 0 ? (0L, 0L) : (values.Min(), values.Max()));
        }
    }
}
