using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingFlow.WarmupService.Models;
using TradingFlow.WarmupService.Services;
using TradingFlow.Data.Candles;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingFlow.Web.Pages;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class WarmupDurabilityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tradingflow-warmup-durability-" + Guid.NewGuid().ToString("N"));

    /// <summary>Accepted inputs survive process replacement and remain independent of subsequent watchlist edits.</summary>
    [Fact]
    public async Task Queue_RestartRecoversOriginalCutoffAndFrozenSettings()
    {
        var options = Options();
        var requests = new WarmupRequestStore(options);
        await requests.UpsertAsync(new(["MSFT"], WarmupDays: 45, IncludeNews: false), default);
        var request = new WarmupJobRequest("restart", "fixture", ["MSFT"], DateTimeOffset.UtcNow);
        using (var queue = new WarmupJobQueue(options, requests))
        {
            await queue.EnqueueAsync(request, default);
            await using var reader = queue.ReadAllAsync(default).GetAsyncEnumerator();
            Assert.True(await reader.MoveNextAsync());
            Assert.Equal("running", Assert.Single(await queue.ListAsync(default)).Status);
        }
        await requests.UpsertAsync(new(["MSFT"], WarmupDays: 120, IncludeNews: true), default);
        using var recovered = new WarmupJobQueue(options, requests);
        await using var resumed = recovered.ReadAllAsync(default).GetAsyncEnumerator();
        Assert.True(await resumed.MoveNextAsync());
        Assert.Equal(request.QueuedAtUtc, resumed.Current.QueuedAtUtc);
        Assert.Equal(45, Assert.Single(resumed.Current.FrozenIntents!).WarmupDays);
        Assert.False(Assert.Single(resumed.Current.FrozenIntents!).IncludeNews);
        await recovered.ExecuteAsync(resumed.Current, _ => Task.CompletedTask, default);
        Assert.Equal("completed", Assert.Single(await recovered.ListAsync(default)).Status);
    }

    /// <summary>Duplicate transport retries do not consume capacity; conflicting IDs and excess accepted work fail explicitly.</summary>
    [Fact]
    public async Task Queue_DeduplicatesAndRejectsCapacityBeforeAcceptance()
    {
        var options = Options(1);
        using var queue = new WarmupJobQueue(options, new(options));
        var request = new WarmupJobRequest("once", "fixture", ["MSFT"], DateTimeOffset.UtcNow);
        await queue.EnqueueAsync(request, default);
        await queue.EnqueueAsync(request with { QueuedAtUtc = DateTimeOffset.UtcNow }, default);
        Assert.Single(await queue.ListAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await queue.EnqueueAsync(request with { Reason = "changed" }, default));
        await Assert.ThrowsAsync<WarmupQueueFullException>(async () => await queue.EnqueueAsync(request with { RunId = "second" }, default));
        Assert.Single(await queue.ListAsync(default));
    }

    /// <summary>Finishing a queued older preparation cannot claim that the operator's later settings were prepared.</summary>
    [Fact]
    public async Task Completion_DoesNotMarkLaterWatchlistSettingsWarmed()
    {
        var requests = new WarmupRequestStore(Options());
        var accepted = await requests.UpsertAsync(new(["MSFT"], WarmupDays: 45, IncludeNews: false), default);
        await requests.UpsertAsync(new(["MSFT"], WarmupDays: 120, IncludeNews: true), default);
        var now = DateTimeOffset.UtcNow;
        await requests.MarkResultsAsync([new("MSFT", true, 1, 0, now.AddDays(-45), now, ["1d"], [], null, CacheCompleted: true)], accepted, default);
        var current = Assert.Single(await requests.ReadAsync(default));
        Assert.Equal(120, current.WarmupDays);
        Assert.Equal("pending", current.LastStatus);
        Assert.Null(current.LastWarmedAtUtc);
    }

    /// <summary>Host shutdown releases accepted work for recovery instead of declaring a successful or lost job.</summary>
    [Fact]
    public async Task Queue_ShutdownReleasesTheSameAcceptedInputs()
    {
        var options = Options();
        using var queue = new WarmupJobQueue(options, new(options));
        var request = new WarmupJobRequest("shutdown", "fixture", ["MSFT"], DateTimeOffset.UtcNow);
        await queue.EnqueueAsync(request, default);
        await using var reader = queue.ReadAllAsync(default).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        using var shutdown = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = queue.ExecuteAsync(reader.Current, async token => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }, shutdown.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        shutdown.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        var persisted = Assert.Single(await queue.ListAsync(default));
        Assert.Equal("queued", persisted.Status);
        Assert.Equal(request.QueuedAtUtc, persisted.Request.QueuedAtUtc);
    }

    /// <summary>Only one warmup host can mutate the configured local queue directory.</summary>
    [Fact]
    public void Queue_RejectsASecondWriterForTheSameStateDirectory()
    {
        var options = Options();
        using var first = new WarmupJobQueue(options, new(options));
        Assert.Throws<IOException>(() => new WarmupJobQueue(options, new(options)));
    }

    /// <summary>Queued and running cancellation are persisted, stop running work, and remain cancelled across restart.</summary>
    [Fact]
    public async Task Queue_CancelsAcceptedAndRunningWork()
    {
        var options = Options();
        using (var queue = new WarmupJobQueue(options, new(options)))
        {
            await queue.EnqueueAsync(new("waiting", "fixture", ["MSFT"], DateTimeOffset.UtcNow), default);
            Assert.True(await queue.CancelAsync("waiting", default));
            await queue.EnqueueAsync(new("running", "fixture", ["MSFT"], DateTimeOffset.UtcNow), default);
            await using var reader = queue.ReadAllAsync(default).GetAsyncEnumerator();
            Assert.True(await reader.MoveNextAsync());
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var task = queue.ExecuteAsync(reader.Current, async token => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }, default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await queue.CancelAsync("running", default);
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(await queue.ListAsync(default), job => Assert.Equal("cancelled", job.Status));
        }
        using var restart = new WarmupJobQueue(options, new(options));
        Assert.All(await restart.ListAsync(default), job => Assert.Equal("cancelled", job.Status));
    }

    /// <summary>A later replacement of ticker cache files cannot change the bytes retried for an earlier archive.</summary>
    [Fact]
    public async Task Archive_RetriesOriginalBytesAfterCacheReplacement()
    {
        Directory.CreateDirectory(root);
        var cache = Path.Combine(root, "cache.jsonl");
        await File.WriteAllTextAsync(cache, "original candles");
        var handler = new ArchiveHandler { FailNext = true };
        using var http = new HttpClient(handler);
        var archive = new CompositeWarmupArchiveSink(new LocalWarmupArchiveSink(Path.Combine(root, "archive")),
            new SasAzureBlobWarmupArchiveSink(http, "https://fixture.invalid/container?sig=fixture", "warmup", NullLogger<SasAzureBlobWarmupArchiveSink>.Instance));
        var pending = await archive.PublishAsync("run", "MSFT", [cache], default);
        Assert.Equal("remote_pending", pending.Status);
        await File.WriteAllTextAsync(cache, "replacement candles");
        var complete = await archive.PublishAsync("run", "MSFT", [cache], default);
        Assert.Equal("remote_committed", complete.Status);
        Assert.Equal("original candles", await File.ReadAllTextAsync(Assert.Single(complete.FrozenPaths)));
        Assert.Equal(handler.Uploads[0].Body, handler.Uploads[1].Body);
        Assert.Equal(handler.Uploads[0].Path, handler.Uploads[1].Path);
        var count = handler.Uploads.Count;
        await archive.PublishAsync("run", "MSFT", [cache], default);
        Assert.Equal(count, handler.Uploads.Count);
    }

    /// <summary>Removing cloud configuration does not turn an earlier pending upload into a completed delivery.</summary>
    [Fact]
    public async Task Archive_MissingRemoteConfigurationKeepsPreviousUploadPending()
    {
        Directory.CreateDirectory(root);
        var cache = Path.Combine(root, "cache.jsonl");
        await File.WriteAllTextAsync(cache, "captured");
        var local = new LocalWarmupArchiveSink(Path.Combine(root, "archive"));
        using var http = new HttpClient(new ArchiveHandler { FailNext = true });
        var original = new CompositeWarmupArchiveSink(local, new SasAzureBlobWarmupArchiveSink(http,
            "https://fixture.invalid/container?sig=fixture", "warmup", NullLogger<SasAzureBlobWarmupArchiveSink>.Instance));
        Assert.Equal("remote_pending", (await original.PublishAsync("pending-config", "MSFT", [cache], default)).Status);
        var unconfigured = new CompositeWarmupArchiveSink(local, new SasAzureBlobWarmupArchiveSink(http,
            null, "warmup", NullLogger<SasAzureBlobWarmupArchiveSink>.Instance));
        var result = await unconfigured.PublishAsync("pending-config", "MSFT", [cache], default);
        Assert.Equal("remote_pending", result.Status);
        Assert.Contains("no remote archive is configured", result.Error);
    }

    /// <summary>Missing files and changed committed bytes are rejected rather than silently omitted from an archive.</summary>
    [Fact]
    public async Task Archive_RejectsMissingAndTamperedFiles()
    {
        Directory.CreateDirectory(root);
        var cache = Path.Combine(root, "cache.jsonl");
        var archive = new LocalWarmupArchiveSink(Path.Combine(root, "archive"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => archive.PublishAsync("missing", "MSFT", [cache], default));
        Assert.False(File.Exists(Path.Combine(root, "archive", "runs", "missing", "MSFT", "manifest.json")));
        await File.WriteAllTextAsync(cache, "original");
        var frozen = await archive.PublishAsync("tampered", "MSFT", [cache], default);
        await File.WriteAllTextAsync(Assert.Single(frozen.FrozenPaths), "changed");
        await Assert.ThrowsAsync<InvalidDataException>(() => archive.PublishAsync("tampered", "MSFT", [cache], default));
    }

    /// <summary>Create-only blob retries require matching length and SHA256; an existing different blob cannot be overwritten.</summary>
    [Fact]
    public async Task Blob_ExistingMatchingBytesReplayButChangedBytesAreRejected()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "captured.jsonl");
        await File.WriteAllTextAsync(path, "frozen");
        var handler = new ArchiveHandler { ExistingMode = true };
        using var http = new HttpClient(handler);
        var sink = new SasAzureBlobWarmupArchiveSink(http, "https://fixture.invalid/container?sig=fixture", "warmup", NullLogger<SasAzureBlobWarmupArchiveSink>.Instance);
        await sink.PublishAsync("run", "MSFT", [path], default);
        await sink.PublishAsync("run", "MSFT", [path], default);
        await File.WriteAllTextAsync(path, "different");
        await Assert.ThrowsAsync<InvalidDataException>(() => sink.PublishAsync("run", "MSFT", [path], default));
        Assert.All(handler.Uploads, upload => Assert.True(upload.CreateOnly));
    }

    /// <summary>A crash after local archival restores original capture metadata instead of fetching a revised provider response.</summary>
    [Fact]
    public async Task Coordinator_RestartAfterTickerArchiveUsesOriginalCaptureAndCounts()
    {
        var options = Options();
        var requests = new WarmupRequestStore(options);
        var intents = await requests.UpsertAsync(new(["MSFT"], WarmupDays: 4, Timeframes: ["1d"], IncludeNews: false), default);
        var runs = new WarmupRunStore(options);
        var providerA = new CapturedBarsProvider(10m, 3);
        var originalArchive = new LocalWarmupArchiveSink(options.Value.ArchiveRoot);
        var interrupted = Coordinator(providerA, new StopAfterArchive(originalArchive), requests, runs, options);
        var job = new WarmupJobRequest("archive-boundary", "fixture", ["MSFT"], DateTimeOffset.UtcNow, intents);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interrupted.RunAsync(job, default));
        Assert.Empty(await runs.ReadAsync(default));
        var original = await runs.ReadTickerCaptureAsync(job.RunId, "MSFT", default);
        Assert.NotNull(original);
        var providerB = new CapturedBarsProvider(90m, 1);
        var resumed = Coordinator(providerB, originalArchive, requests, runs, options);
        var result = Assert.Single((await resumed.RunAsync(job, default)).Tickers);
        Assert.Equal(0, providerB.Calls);
        Assert.Equal(3, result.BarCount);
        Assert.Equal(original.ArtifactPaths, result.ArtifactPaths);
        Assert.Equal("local_committed", result.ArchiveStatus);
        var frozen = await originalArchive.PublishAsync(job.RunId, "MSFT", result.ArtifactPaths, default);
        var barsPath = frozen.FrozenPaths.Single(path => path.EndsWith("-bars.jsonl", StringComparison.Ordinal));
        Assert.Contains("\"close\":10", await File.ReadAllTextAsync(barsPath));
    }

    /// <summary>Archive retry targets survive completed-history trimming and remain addressable by their original run ID.</summary>
    [Theory]
    [InlineData("remote_pending")]
    [InlineData("local_failed")]
    public async Task RunStore_RetainsPendingArchiveBeyondDisplayedHistory(string archiveStatus)
    {
        var options = Options();
        var runs = new WarmupRunStore(options);
        var now = DateTimeOffset.UtcNow;
        var pending = new WarmupRunRecord("pending", "fixture", now.AddYears(-1), now.AddYears(-1), "succeeded", 1, 1, 0,
            [new("MSFT", true, 1, 0, now.AddDays(-1), now, ["1d"], [], null, CacheCompleted: true, ArchiveStatus: archiveStatus)]);
        var history = Enumerable.Range(0, 251).Select(index => new WarmupRunRecord("done-" + index, "fixture", now.AddMinutes(-index), now,
            "succeeded", 0, 0, 0, [])).Append(pending).ToArray();
        Directory.CreateDirectory(Path.Combine(root, "state"));
        await File.WriteAllTextAsync(Path.Combine(root, "state", "runs.json"), JsonSerializer.Serialize(history,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        await runs.AddAsync(new("latest", "fixture", now.AddMinutes(1), now.AddMinutes(1), "succeeded", 0, 0, 0, []), default);
        var retained = await runs.ReadAsync(default);
        Assert.Contains(retained, run => run.RunId == "pending" && run.Tickers[0].ArchiveStatus == archiveStatus);
        Assert.Equal(250, retained.Count(run => run.RunId != "pending"));
    }

    /// <summary>Rejects oversized symbols, excessive history, excessive estimated bars and date underflow before queue acceptance.</summary>
    [Theory]
    [InlineData("symbols")]
    [InlineData("history")]
    [InlineData("bars")]
    [InlineData("date")]
    public async Task Queue_RejectsPerJobWorkOutsideConfiguredBudget(string defect)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new WarmupOptions { DataRoot = root, CacheRoot = Path.Combine(root, "cache"),
            ArchiveRoot = Path.Combine(root, "archive"), MaximumTickersPerJob = 2, MaximumHistoryDays = 90,
            MaximumBarsPerJob = defect == "bars" ? 5 : 2000000, DefaultTimeframes = ["1d"],
            DefaultWarmupDays = defect == "history" ? 91 : 60 });
        using var queue = new WarmupJobQueue(options, new(options));
        var request = new WarmupJobRequest("limits", "fixture", defect == "symbols" ? ["MSFT", "AAPL", "NVDA"] : ["MSFT"],
            defect == "date" ? DateTimeOffset.MinValue.AddHours(12) : DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<ArgumentException>(async () => await queue.EnqueueAsync(request, default));
        Assert.Empty(await queue.ListAsync(default));
    }

    /// <summary>The actual Web client sends the same command key after an uncertain failure, then rotates it after acceptance.</summary>
    [Fact]
    public async Task WebRunNow_PreservesPostedRetryKeyUntilAccepted()
    {
        var keys = new List<string?>();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapPost("/api/warmup/run-now", (WarmupRunNowRequestDto request) => {
            keys.Add(request.IdempotencyKey);
            return keys.Count == 1 ? Results.Json(new { message = "fixture uncertain transport" }, statusCode: 503)
                : Results.Json(new { runId = "accepted", queued = true }, statusCode: 202);
        });
        app.MapGet("/api/warmup/watchlist", () => Array.Empty<WarmupTickerIntentDto>());
        app.MapGet("/api/warmup/runs", () => Array.Empty<WarmupRunRecordDto>());
        app.MapGet("/api/warmup/jobs", () => Array.Empty<TradingFlow.Contracts.V1.WarmupQueueStatus>());
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["WarmupService:BaseUrl"] = address }).Build();
            var model = new WarmupModel(new WarmupServiceClient(configuration)) { RunNowCommandKey = "same-posted-command" };
            await model.OnPostRunNowAsync(default);
            Assert.Equal("same-posted-command", model.RunNowCommandKey);
            await model.OnPostRunNowAsync(default);
            Assert.Equal(new[] { "same-posted-command", "same-posted-command" }, keys);
            Assert.NotEqual("same-posted-command", model.RunNowCommandKey);
        }
        finally { await app.StopAsync(); }
    }

    /// <summary>Concurrent and lost-response add/run retries accept one job and cannot overwrite later operator edits.</summary>
    [Fact]
    public async Task AddAndRun_SameCommandReplaysBeforeWatchlistMutation()
    {
        var options = Options();
        var requests = new WarmupRequestStore(options);
        using var queue = new WarmupJobQueue(options, requests);
        var request = new WarmupWatchRequest(["MSFT", "AAPL"], Reason: "fixture", RequestedBy: "web", WarmupDays: 45,
            Timeframes: ["1d"], IncludeNews: false, RunNow: true, IdempotencyKey: "add-once");
        var responses = await Task.WhenAll(queue.AddWatchAsync(request, default), queue.AddWatchAsync(request, default));
        Assert.All(responses, response => Assert.True(response.RunQueued));
        var accepted = Assert.Single(await queue.ListAsync(default));
        Assert.Equal(WarmupJobQueue.RunIdForKey("add-once"), accepted.Request.RunId);
        Assert.All(accepted.Request.FrozenIntents!, intent => Assert.Equal(45, intent.WarmupDays));
        await requests.UpsertAsync(new(["MSFT"], WarmupDays: 90, IncludeNews: true), default);
        var later = (await requests.ReadAsync(default)).Single(intent => intent.Ticker == "MSFT");
        await queue.AddWatchAsync(request, default);
        Assert.Single(await queue.ListAsync(default));
        var afterRetry = (await requests.ReadAsync(default)).Single(intent => intent.Ticker == "MSFT");
        Assert.Equal(90, afterRetry.WarmupDays);
        Assert.Equal(later.RequestedAtUtc, afterRetry.RequestedAtUtc);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.AddWatchAsync(request with { WarmupDays = 46 }, default));
        Assert.Equal(90, (await requests.ReadAsync(default)).Single(intent => intent.Ticker == "MSFT").WarmupDays);
    }

    /// <summary>The Web add/run client retains its posted key after a response is lost following actual queue acceptance.</summary>
    [Fact]
    public async Task WebAddAndRun_LostResponseRetriesTheSameAcceptedJob()
    {
        var options = Options();
        var requests = new WarmupRequestStore(options);
        using var queue = new WarmupJobQueue(options, requests);
        var keys = new List<string?>();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapPost("/api/warmup/watchlist", async (WarmupWatchRequest request) => {
            keys.Add(request.IdempotencyKey);
            var accepted = await queue.AddWatchAsync(request, default);
            return keys.Count == 1 ? Results.Json(new { message = "fixture lost response after acceptance" }, statusCode: 503)
                : Results.Json(accepted, statusCode: 202);
        });
        app.MapGet("/api/warmup/watchlist", () => requests.ReadAsync(default));
        app.MapGet("/api/warmup/runs", () => Array.Empty<WarmupRunRecordDto>());
        app.MapGet("/api/warmup/jobs", () => Array.Empty<TradingFlow.Contracts.V1.WarmupQueueStatus>());
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["WarmupService:BaseUrl"] = address }).Build();
            var model = new WarmupModel(new WarmupServiceClient(configuration)) { TickersCsv = "MSFT", TimeframesCsv = "1d",
                WarmupDays = 45, IncludeNews = false, RunNow = true, AddCommandKey = "same-add-command" };
            await model.OnPostAddAsync(default);
            Assert.Equal("same-add-command", model.AddCommandKey);
            Assert.Single(await queue.ListAsync(default));
            await model.OnPostAddAsync(default);
            Assert.Equal(new[] { "same-add-command", "same-add-command" }, keys);
            Assert.NotEqual("same-add-command", model.AddCommandKey);
            Assert.Single(await queue.ListAsync(default));
        }
        finally { await app.StopAsync(); }
    }

    /// <summary>A rejected nightly work budget cannot fault the scheduler's next acceptance attempt.</summary>
    [Fact]
    public async Task Nightly_WorkLimitRejectionAllowsTheNextScheduledAttempt()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new WarmupOptions { DataRoot = root,
            CacheRoot = Path.Combine(root, "cache"), ArchiveRoot = Path.Combine(root, "archive"), MaximumHistoryDays = 90 });
        var requests = new WarmupRequestStore(options);
        await requests.UpsertAsync(new(["MSFT"], WarmupDays: 91, Timeframes: ["1d"], IncludeNews: false), default);
        using var queue = new WarmupJobQueue(options, requests);
        using var scheduler = new WarmupBackgroundService(queue, Coordinator(new CapturedBarsProvider(10, 3),
            new LocalWarmupArchiveSink(options.Value.ArchiveRoot), requests, new(options), options), options,
            NullLogger<WarmupBackgroundService>.Instance);
        Assert.False(await scheduler.EnqueueNightlyAsync(default));
        Assert.Empty(await queue.ListAsync(default));
        await requests.UpsertAsync(new(["MSFT"], WarmupDays: 60, Timeframes: ["1d"], IncludeNews: false), default);
        Assert.True(await scheduler.EnqueueNightlyAsync(default));
        Assert.Equal("queued", Assert.Single(await queue.ListAsync(default)).Status);
    }

    /// <summary>Builds the actual coordinator with disposable storage and deterministic provider boundaries.</summary>
    private WarmupCoordinator Coordinator(IMarketDataProvider provider, IWarmupArchiveSink archive, WarmupRequestStore requests,
        WarmupRunStore runs, IOptions<WarmupOptions> options) => new(provider, new NoNewsProvider(),
            new LocalFileCandleStore(Path.Combine(root, "candles")), new WarmupArtifactWriter(options), archive, requests, runs,
            options, NullLogger<WarmupCoordinator>.Instance);

    /// <summary>Creates isolated cache/state/archive roots for every test.</summary>
    private IOptions<WarmupOptions> Options(int maximum = 32) => Microsoft.Extensions.Options.Options.Create(new WarmupOptions {
        DataRoot = root, CacheRoot = Path.Combine(root, "cache"), ArchiveRoot = Path.Combine(root, "archive"), MaximumPendingJobs = maximum });

    /// <summary>Removes only this fixture's verified temporary directory, including copied archives and queue files.</summary>
    public void Dispose()
    {
        if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar))
            throw new InvalidOperationException("Warmup fixture cleanup escaped its temporary parent.");
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class ArchiveHandler : HttpMessageHandler
    {
        public bool FailNext { get; set; }
        public bool ExistingMode { get; set; }
        public List<(string Path, string Body, bool CreateOnly)> Uploads { get; } = [];
        private readonly Dictionary<string, byte[]> stored = [];

        /// <summary>Simulates create-only object storage and HEAD hash checks without a network connection.</summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Head)
            {
                var bytes = stored[path];
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
                response.Headers.Add("x-ms-meta-sha256", Convert.ToHexStringLower(SHA256.HashData(bytes)));
                return response;
            }
            var body = await request.Content!.ReadAsByteArrayAsync(token);
            Uploads.Add((path, System.Text.Encoding.UTF8.GetString(body), request.Headers.IfNoneMatch.Any(value => value.Tag == "*")));
            if (FailNext) { FailNext = false; return new(HttpStatusCode.ServiceUnavailable); }
            if (ExistingMode && stored.ContainsKey(path)) return new(HttpStatusCode.PreconditionFailed);
            stored[path] = body;
            return new(HttpStatusCode.Created);
        }
    }

    private sealed class StopAfterArchive(IWarmupArchiveSink archive) : IWarmupArchiveSink
    {
        /// <summary>Simulates process loss at the precise archive-committed/run-not-published boundary.</summary>
        public async Task<WarmupArchivePublication> PublishAsync(string runId, string ticker, IReadOnlyList<string> paths, CancellationToken token)
        {
            await archive.PublishAsync(runId, ticker, paths, token);
            throw new OperationCanceledException("Simulated process stop after archive commit.");
        }
    }

    private sealed class CapturedBarsProvider(decimal close, int count) : IMarketDataProvider
    {
        public int Calls { get; private set; }

        /// <summary>Returns deterministic original or revised rows so replay must prove it avoided a second provider read.</summary>
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> timeframes,
            DateTimeOffset start, DateTimeOffset end, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            Calls++;
            for (var index = 0; index < count; index++)
            {
                token.ThrowIfCancellationRequested();
                yield return new(tickers.Single(), end.AddDays(-count + index), "1d", close, close + 1, close - 1, close, 1000);
            }
            await Task.CompletedTask;
        }
    }

    private sealed class NoNewsProvider : ICatalystProvider
    {
        public string ProviderName => "fixture";

        /// <summary>Keeps the crash-boundary replay offline and independent of historical news availability.</summary>
        public Task<IReadOnlyList<CatalystEvent>> GetCatalystsAsync(string ticker, DateTimeOffset start, DateTimeOffset end, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<CatalystEvent>>([]);
    }
}
