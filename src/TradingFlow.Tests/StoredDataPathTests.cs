using Microsoft.Extensions.Logging;
using TradingFlow.Application.Jobs;
using TradingFlow.Domain.Jobs;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

/// <summary>
/// Paths stored while data lived in the repository's default data folder are read from the configured data folder,
/// and a missing result is logged once per job instead of on every projection refresh.
/// </summary>
public sealed class StoredDataPathTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tf-stored-paths-" + Guid.NewGuid().ToString("N"));
    private readonly ProjectPaths paths;

    public StoredDataPathTests()
    {
        Directory.CreateDirectory(root);
        paths = new ProjectPaths(Path.Combine(root, "repository"), Path.Combine(root, "operator-data"), Path.Combine(root, "cache"));
    }

    private string Former(params string[] parts) => Path.Combine([paths.RepositoryRoot, "data", .. parts]);
    private string Current(params string[] parts) => Path.Combine([paths.DataRoot, .. parts]);

    [Fact]
    public void LocateStored_MapsOnlyPathsInsideTheDefaultDataFolder()
    {
        Assert.Equal(Current("backtest", "results", "r.json"), paths.LocateStored(Former("backtest", "results", "r.json")));
        Assert.Equal(Path.GetFullPath(paths.DataRoot), paths.LocateStored(Former()));
        Assert.Equal(Current("x.yaml"), paths.LocateStored(Former("x.yaml").Replace('\\', '/')));

        var lookAlike = Path.Combine(paths.RepositoryRoot, "data2", "r.json");
        var elsewhere = Path.Combine(root, "other", "r.json");
        var alreadyCurrent = Current("r.json");
        Assert.Equal(lookAlike, paths.LocateStored(lookAlike));
        Assert.Equal(elsewhere, paths.LocateStored(elsewhere));
        Assert.Equal(alreadyCurrent, paths.LocateStored(alreadyCurrent));
        Assert.Equal("configs/backtest/run.yaml", paths.LocateStored("configs/backtest/run.yaml"));
        Assert.Equal("", paths.LocateStored(""));
    }

    [Fact]
    public void LocateStored_NeverMapsAPathTwiceWhenTheDataFolderIsInsideTheDefaultFolder()
    {
        var nested = new ProjectPaths(paths.RepositoryRoot, Former("live"), Path.Combine(root, "cache"));
        var alreadyCurrent = Former("live", "x.json");
        Assert.Equal(alreadyCurrent, nested.LocateStored(alreadyCurrent));
        Assert.Equal(Former("live", "backtest", "r.json"), nested.LocateStored(Former("backtest", "r.json")));
    }

    [Fact]
    public void LocateStored_AcceptsATrailingSeparatorAndPlatformCaseRules()
    {
        var trailing = new ProjectPaths(paths.RepositoryRoot, paths.DataRoot + Path.DirectorySeparatorChar, Path.Combine(root, "cache"));
        Assert.Equal(Current("r.json"), trailing.LocateStored(Former("r.json")));
        if (!OperatingSystem.IsWindows()) return;
        // Windows file names ignore letter case, so a stored path with different case is the same folder.
        Assert.Equal(Current("R.json"), paths.LocateStored(Path.Combine(paths.RepositoryRoot.ToUpperInvariant(), "DATA", "R.json")));
    }

    [Fact]
    public void LocateStored_LeavesPathsAloneWhenDataStaysInTheDefaultFolder()
    {
        var unmoved = new ProjectPaths(paths.RepositoryRoot);
        var stored = Former("backtest", "results", "r.json");
        Assert.Equal(stored, unmoved.LocateStored(stored));
    }

    [Fact]
    public async Task Markers_PublishAResultStoredUnderTheFormerFolderWithoutWarnings()
    {
        var relative = Path.Combine("backtest", "results", ".durable-attempts", "backtest", "job", "attempt", "result.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Current(relative))!);
        await File.WriteAllTextAsync(Current(relative), "{}");
        var logger = new RecordingLogger<StoredDataPathTests>();
        var job = Completed(Former(relative));

        await new DurableResultMarkers("backtest", logger, paths).PublishAsync(job, default);

        Assert.True(DurableJobResultPublication.IsPublished(Current(relative)));
        Assert.Equal(Former(relative), job.ResultReference);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task Markers_LogAMissingResultOnceThenAgainOnlyOnChangeAndOnceOnRecovery()
    {
        var relative = Path.Combine("backtest", "results", ".durable-attempts", "backtest", "job", "attempt", "result.json");
        var logger = new RecordingLogger<StoredDataPathTests>();
        var markers = new DurableResultMarkers("backtest", logger, paths);
        var job = Completed(Former(relative));

        for (var refresh = 0; refresh < 5; refresh++) await markers.PublishAsync(job, default);
        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(Current(relative), warning.Message);

        job.ResultReference = Former("backtest", "results", ".durable-attempts", "backtest", "job", "attempt-2", "result.json");
        await markers.PublishAsync(job, default);
        await markers.PublishAsync(job, default);
        Assert.Equal(2, logger.Entries.Count(entry => entry.Level == LogLevel.Warning));

        Directory.CreateDirectory(Path.GetDirectoryName(Locate(job))!);
        await File.WriteAllTextAsync(Locate(job), "{}");
        await markers.PublishAsync(job, default);
        await markers.PublishAsync(job, default);
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Information);
        Assert.Equal(3, logger.Entries.Count);
        Assert.True(DurableJobResultPublication.IsPublished(Locate(job)));
    }

    [Fact]
    public async Task Markers_LogAFailedMarkerWriteOnceEvenThoughEachAttemptUsesANewTemporaryFile()
    {
        var relative = Path.Combine("backtest", "results", ".durable-attempts", "backtest", "job", "attempt", "result.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Current(relative))!);
        await File.WriteAllTextAsync(Current(relative), "{}");
        // A folder where the marker file belongs makes every write attempt fail after its random temporary file.
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Current(relative))!, DurableJobResultPublication.MarkerFileName));
        var logger = new RecordingLogger<StoredDataPathTests>();
        var markers = new DurableResultMarkers("backtest", logger, paths);
        var job = Completed(Former(relative));

        for (var refresh = 0; refresh < 4; refresh++) await markers.PublishAsync(job, default);

        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Entries).Level);
        Assert.True(File.Exists(Current(relative)));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Current(relative))!, "*.tmp"));
    }

    [Fact]
    public async Task Markers_IgnoreJobsThatAreNotCompleted()
    {
        var logger = new RecordingLogger<StoredDataPathTests>();
        var job = Completed(Former("missing", "result.json"));
        job.Status = "failed";
        await new DurableResultMarkers("backtest", logger, paths).PublishAsync(job, default);
        Assert.Empty(logger.Entries);
    }

    private string Locate(PersistedJob job) => paths.LocateStored(job.ResultReference!);

    private static PersistedJob Completed(string resultReference) => new()
    {
        Id = Guid.NewGuid(),
        JobType = "backtest",
        Status = "completed",
        ResultReference = resultReference,
        FinishedAt = DateTimeOffset.UtcNow
    };

    public void Dispose() => Directory.Delete(root, true);

}

/// <summary>Keeps every log call so a test can count warnings per refresh.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
