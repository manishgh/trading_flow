using TradingFlow.Application.Jobs;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Web.Models;

namespace TradingFlow.Tests;

public sealed class BacktestResultAvailabilityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tradingflow-result-view-" + Guid.NewGuid().ToString("N"));

    // A completed row without a payload must explain absence rather than imply a valid result from its execution status.
    [Fact]
    public void CompletedWithoutPayload_IsUnavailableWithoutInventingSummary()
    {
        var observed = BacktestResultAvailability.Observe(Job(null));
        Assert.True(observed.RequiresAttention);
        Assert.False(observed.HasRetainedSummary);
        Assert.Contains("no retained result payload", observed.Detail);
    }

    // Missing original files do not erase retained monthly/trade evidence or mutate the durable job projection.
    [Fact]
    public void MissingFile_LeavesRecordedSummaryAndReferenceUnchanged()
    {
        var reference = Path.Combine(root, "absent.json");
        var job = Job(Result(reference));
        var observed = BacktestResultAvailability.Observe(job);
        Assert.Equal("Result unavailable", observed.Label);
        Assert.True(observed.HasRetainedSummary);
        Assert.Equal(reference, job.Result!.ResultPath);
        Assert.False(Directory.Exists(root));
    }

    // Relative references cannot accidentally resolve against a new host's working directory.
    [Fact]
    public void RelativeReference_IsUnavailable()
    {
        Assert.True(BacktestResultAvailability.Observe(Job(Result("results/result.json"))).RequiresAttention);
    }

    // A staged durable attempt is never presented as published; the view itself must not create a marker.
    [Fact]
    public void AttemptFile_RequiresExistingPublicationMarker()
    {
        var directory = Path.Combine(root, ".durable-attempts", "owned-test-attempt");
        Directory.CreateDirectory(directory);
        var reference = Path.Combine(directory, "result.json");
        File.WriteAllText(reference, "{}");
        var job = Job(Result(reference));
        var missing = BacktestResultAvailability.Observe(job);
        Assert.True(missing.RequiresAttention);
        Assert.Contains("publication marker", missing.Detail);
        Assert.False(File.Exists(Path.Combine(directory, DurableJobResultPublication.MarkerFileName)));
        File.WriteAllText(Path.Combine(directory, DurableJobResultPublication.MarkerFileName), "{}");
        Assert.Equal("present", BacktestResultAvailability.Observe(job).State);
    }

    // A result recorded before the data folder moved is found at the same place under the configured data folder;
    // without the mapping it stays unavailable, and the stored reference is never changed.
    [Fact]
    public void ResultStoredBeforeTheDataFolderMoved_IsFoundUnderTheConfiguredFolder()
    {
        var paths = new TradingFlow.Web.Services.ProjectPaths(
            Path.Combine(root, "repository"), Path.Combine(root, "operator-data"), Path.Combine(root, "cache"));
        var relative = Path.Combine("backtest", "results", ".durable-attempts", "job", "attempt", "result.json");
        var current = Path.Combine(paths.DataRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(current)!);
        File.WriteAllText(current, "{}");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(current)!, DurableJobResultPublication.MarkerFileName), "{}");
        var stored = Path.Combine(paths.RepositoryRoot, "data", relative);
        var job = Job(Result(stored));

        Assert.True(BacktestResultAvailability.Observe(job).RequiresAttention);
        Assert.Equal("present", BacktestResultAvailability.Observe(job, paths).State);
        Assert.Equal(stored, job.Result!.ResultPath);
    }

    // Presence is deliberately narrower than validation, and failed/running jobs do not inherit a success badge.
    [Theory]
    [InlineData("running")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public void UnfinishedOrFailedExecution_DoesNotClaimAvailableResult(string status)
    {
        var observed = BacktestResultAvailability.Observe(Job(Result(Path.Combine(root, "absent.json"))) with { Status = status });
        Assert.Equal("pending", observed.State);
        Assert.Equal(status, observed.Label);
    }

    // Create only an in-memory immutable result summary; no economic simulation is executed by this presentation fixture.
    private static BacktestResult Result(string reference) => new("fixture", reference,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 100m, 100m, 0m, 0m, 0m, 1, 0m, null,
        0, 0, 0, 0, 0, 0, [], [], new(new(false, 0m, null, []), [], new(false, "", null, []),
            new(0, 0, 0, 0, 0, []), new("fixture", null, "none", false, false, [])), [], [], [], []);

    // Supply terminal metadata independently of result presence to exercise the distinction visible to the operator.
    private static BacktestJobSnapshot Job(BacktestResult? result) => new(Guid.NewGuid(), "fixture", "unused.yaml",
        "completed", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
        null, "completed", 1, 1, [], result);

    // Delete only this fixture's generated files after verifying their direct temporary-directory ownership.
    public void Dispose()
    {
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(root), ignoreCase: true);
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
