using Moq;
using TradingFlow.Application.Jobs;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Jobs;

namespace TradingFlow.Tests;

public sealed class PreparedRequestStagingCleanupTests
{
    /// <summary>A losing concurrent attempt can remove only its own request while the admitted winner and other generated files survive.</summary>
    [Fact]
    public async Task RejectedAttempt_DeletesOnlyExactOwnedRequestAndPreservesWinner()
    {
        using var fixture = new Fixture();
        var loser = fixture.Write("loser");
        var winner = fixture.Write("winner");
        var other = Path.Combine(Path.GetDirectoryName(loser.Path)!, "unclassified.json");
        await File.WriteAllTextAsync(other, "preserve");
        var jobs = Repository([Accepted(winner)]);
        await PreparedRequestStagingCleanup.DiscardAsync(loser, loser.Path, jobs.Object, Guid.NewGuid());
        Assert.False(File.Exists(loser.Path));
        Assert.True(File.Exists(winner.Path));
        Assert.True(File.Exists(other));
        Assert.True(Directory.Exists(Path.GetDirectoryName(loser.Path)));
    }

    /// <summary>An admitted sibling protects the shared request even when the preparing child's own job identity was rejected.</summary>
    [Fact]
    public async Task AcceptedSibling_PreservesSharedComparisonRequest()
    {
        using var fixture = new Fixture();
        var reference = fixture.Write("shared");
        var jobs = Repository([Accepted(reference)]);
        await PreparedRequestStagingCleanup.DiscardAsync(reference, reference.Path, jobs.Object, Guid.NewGuid());
        Assert.True(File.Exists(reference.Path));
    }

    /// <summary>Cleanup cannot claim another attempt's path or delete parameter bytes that no longer match its captured hash.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PathOrHashMismatch_PreservesFile(bool changedPath)
    {
        using var fixture = new Fixture();
        var reference = fixture.Write("owned");
        var expectedPath = reference.Path;
        if (changedPath) expectedPath = fixture.Write("other").Path;
        else await File.AppendAllTextAsync(reference.Path, "changed");
        await PreparedRequestStagingCleanup.DiscardAsync(reference, expectedPath, Repository([]).Object, Guid.NewGuid());
        Assert.True(File.Exists(reference.Path));
    }

    /// <summary>A proven unaccepted request may release its empty attempt directory without touching any parent comparison directory.</summary>
    [Fact]
    public async Task UnacceptedRequest_RemovesOnlyEmptyAttemptDirectory()
    {
        using var fixture = new Fixture();
        var reference = fixture.Write("empty");
        var attemptDirectory = Path.GetDirectoryName(reference.Path)!;
        var comparisonDirectory = Path.GetDirectoryName(attemptDirectory)!;
        await PreparedRequestStagingCleanup.DiscardAsync(reference, reference.Path, Repository([]).Object, Guid.NewGuid());
        Assert.False(Directory.Exists(attemptDirectory));
        Assert.True(Directory.Exists(comparisonDirectory));
    }

    /// <summary>An unreadable durable ownership record cannot be treated as proof that a captured request was never accepted.</summary>
    [Fact]
    public async Task UnreadableOwnership_PreservesRequest()
    {
        using var fixture = new Fixture();
        var reference = fixture.Write("unproved");
        await PreparedRequestStagingCleanup.DiscardAsync(reference, reference.Path,
            Repository([new PersistedJob { Id = Guid.NewGuid(), RequestJson = "{" }]).Object, Guid.NewGuid());
        Assert.True(File.Exists(reference.Path));
    }

    /// <summary>Models durable ownership without accepting or mutating any application data.</summary>
    private static Mock<IDurableJobRepository> Repository(IReadOnlyList<PersistedJob> accepted)
    {
        var repository = new Mock<IDurableJobRepository>();
        repository.Setup(x => x.GetJobAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => accepted.FirstOrDefault(job => job.Id == id));
        repository.Setup(x => x.GetAllJobsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(accepted);
        return repository;
    }

    /// <summary>Captures a genuine request-file hash under a distinct accepted sibling identity.</summary>
    private static PersistedJob Accepted(PreparedMarketDataRequestReference reference) => new() {
        Id = Guid.NewGuid(), RequestJson = (DurableFileJobRequest.Capture([reference.Path]) with { PreparedMarketData = reference }).Serialize() };

    private sealed class Fixture : IDisposable
    {
        private readonly string id = Guid.NewGuid().ToString("N");
        private readonly string parent = Path.GetFullPath(Path.GetTempPath());
        public string Root { get; }
        /// <summary>Allocates an exclusively owned synthetic fixture for generated parameter cleanup checks.</summary>
        public Fixture() { Root = Path.Combine(parent, "tf-request-cleanup-" + id); Directory.CreateDirectory(Root); }
        /// <summary>Creates a distinct attempt's immutable synthetic request and its actual captured content hash.</summary>
        public PreparedMarketDataRequestReference Write(string attempt)
        {
            var path = Path.Combine(Root, "prepared-comparisons", "comparison-synthetic", attempt, "request.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{\"synthetic\":true}");
            var evidence = Assert.Single(DurableFileJobRequest.Capture([path]).Files);
            return new(path, evidence.Sha256);
        }
        /// <summary>Removes only the fixture's resolved temporary root after verifying its original owner identity.</summary>
        public void Dispose()
        {
            var resolved = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(resolved) != Path.TrimEndingDirectorySeparator(parent) ||
                Path.GetFileName(resolved) != "tf-request-cleanup-" + id) throw new InvalidOperationException("Synthetic request cleanup escaped its owned root.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }
}
