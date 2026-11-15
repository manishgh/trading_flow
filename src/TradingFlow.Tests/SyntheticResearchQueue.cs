using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Moq;
using TradingFlow.Application.Jobs;
using TradingFlow.Domain.Jobs;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Research;

namespace TradingFlow.Tests;

/// <summary>Gives existing synthetic queue fixtures deterministic starts without configuring real research approvals.</summary>
internal static class SyntheticResearchQueue
{
    private static readonly ConditionalWeakTable<IDurableJobRepository, ResearchQueue> Queues = new();

    /// <summary>Shares the synthetic registry among submitters and recovered workers of one fixture.</summary>
    internal static ResearchQueue For(IDurableJobRepository jobs) => Queues.GetValue(jobs, Create);

    /// <summary>Freezes intended jobs for fixture retry tests and never invokes a provider or operator registry.</summary>
    private static ResearchQueue Create(IDurableJobRepository jobs)
    {
        var starts = new ConcurrentDictionary<string, ResearchStart>();
        var prepared = new ConcurrentDictionary<string, IReadOnlyList<PersistedJob>>();
        var registry = new Mock<IResearchStartRegistry>();
        registry.Setup(item => item.ReserveStartAsync(It.IsAny<FrozenResearchStart>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FrozenResearchStart request, CancellationToken _) =>
            {
                var stored = starts.GetOrAdd(request.StartId, _ => new(new(request, [], "synthetic fixture", false),
                    ResearchStartState.Pending, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
                if (EvidenceCanonicalJson.ComputeSha256(stored.Binding.Request) != EvidenceCanonicalJson.ComputeSha256(request))
                    throw new IdempotencyKeyConflictException(request.StartId);
                return stored;
            });
        registry.Setup(item => item.FreezeQueueJobsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<PersistedJob>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, IReadOnlyList<PersistedJob> books, CancellationToken _) => prepared.GetOrAdd(id, books));
        registry.Setup(item => item.ReadQueueJobsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => prepared.TryGetValue(id, out var books) ? books : []);
        registry.Setup(item => item.OwnsQueueInputsAsync(It.IsAny<PersistedJob>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PersistedJob job, CancellationToken _) => prepared.Values.SelectMany(books => books)
                .Any(book => book.Id == job.Id && book.ConfigPath == job.ConfigPath && book.RequestJson == job.RequestJson));
        return new(registry.Object, jobs, new string('a', 64));
    }
}
