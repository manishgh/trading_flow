using System.Runtime.CompilerServices;
using System.Text.Json;
using TradingFlow.Backtesting;
using TradingFlow.Data.Csv;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Storage;
using TradingFlow.Engine.Configuration;
using TradingFlow.Application.Jobs;
using TradingFlow.Domain.Jobs;
using TradingFlow.Web.Services;
using TradingFlow.Alpaca;
using TradingFlow.Engine.Risk;
using System.Security.Cryptography;

namespace TradingFlow.Tests;

public sealed class PreparedMarketDataServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 3, 13, 30, 0, TimeSpan.Zero);

    /// <summary>Eight ordinary singleton books share one exact bounded calendar; every child reopens it through the normal pinned provider.</summary>
    [Theory]
    [InlineData(ExecutionReplayPolicy.IntradayRthFiveMinuteV1)]
    [InlineData(ExecutionReplayPolicy.IntradayRthFiveMinuteStopMarketV1)]
    [InlineData(ExecutionReplayPolicy.IntradayRthFiveMinuteNextBarMarketV1)]
    public async Task IntradayEightBooks_PinOnlyRequiredCalendarDates(string replayPolicy)
    {
        using var fixture = new Fixture();
        var provider = new CalendarSyntheticProvider(Path.Combine(fixture.Root, "raw"));
        var service = new PreparedMarketDataService(ConfiguredDataProviders.FromEnvironment(), _ => provider);
        var end = DateTimeOffset.Parse("2026-08-05T04:00:00Z");
        var runs = Enumerable.Range(0, 8).Select(index => fixture.Run("singleton-" + index, ["STOCK" + index]))
            .Select(run => run with { TimeWindow = run.TimeWindow with { End = end },
                Engine = run.Engine with { ExecutionReplayPolicyId = replayPolicy } }).ToArray();
        using var handle = await service.PrepareAsync(fixture.Request("eight-intraday", runs), null, default);
        Assert.Equal(9, handle.Summary.DownloadedQueryCount);
        var captured = Assert.Single(provider.CalendarCaptures);
        Assert.Equal(new DateOnly(2026, 8, 2), captured.Start);
        Assert.Equal(new DateOnly(2026, 8, 4), captured.End);
        foreach (var run in runs)
        {
            var pinned = Assert.IsAssignableFrom<IMarketSessionScheduleProvider>(handle.CreateProvider(run));
            var dates = await pinned.LoadMarketSessionSchedulesAsync(captured.Start, captured.End, default);
            Assert.Equal(3, dates.Count);
            Assert.Equal(captured.End, dates.Keys.Max());
        }
        Assert.Single(provider.CalendarCaptures);
    }

    /// <summary>Preparation preserves an explicit rolling end and uses the admission cutoff only when the profile has no end.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RollingWindow_PreservesConfiguredEndOrUsesCutoff(bool explicitEnd)
    {
        using var fixture = new Fixture();
        var cutoff = Start.AddDays(20);
        var configuredEnd = Start.AddDays(10);
        var run = fixture.Run("rolling", ["AAA"]) with { TimeWindow = new("rolling", 5, null, explicitEnd ? configuredEnd : null, 2) };
        var resolved = PreparedMarketDataService.ResolveRun(run, cutoff);
        Assert.Equal(explicitEnd ? configuredEnd : cutoff, resolved.TimeWindow.End);
        Assert.Equal((explicitEnd ? configuredEnd : cutoff).AddDays(-5), resolved.TimeWindow.Start);
        Assert.Equal("fixed", resolved.TimeWindow.Type);
        Assert.Equal(2, resolved.TimeWindow.WarmupLookbackDays);
    }

    /// <summary>Every source request passes the leaf's exact query contract before any earlier valid query may collect data.</summary>
    [Fact]
    public async Task InvalidLaterQuery_IsRejectedBeforeAnyCollection()
    {
        using var fixture = new Fixture();
        var request = fixture.Request("invalid-query", fixture.Run("first", ["AAA", new string('B', 33)]));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.PrepareAsync(request, null, default));
        Assert.Equal(0, fixture.Provider.Calls);
    }

    /// <summary>Admission publication is idempotent, rejects different accepted parameters and performs no provider collection.</summary>
    [Fact]
    public async Task WriteRequest_ExclusivelyFreezesParametersWithoutCollecting()
    {
        using var fixture = new Fixture();
        var request = fixture.Request("capture", fixture.Run("first", ["AAA"]));
        var path = Path.Combine(fixture.Root, "admission", "request.json");
        var first = await PreparedMarketDataService.WriteRequestAsync(request, path, default);
        var repeated = await PreparedMarketDataService.WriteRequestAsync(request, path, default);
        Assert.Equal(first, repeated);
        Assert.Equal(path, first.Path);
        Assert.Equal(64, first.Sha256.Length);
        await Assert.ThrowsAsync<InvalidDataException>(() => PreparedMarketDataService.WriteRequestAsync(request with { ComparisonId = "different" }, path, default));
        Assert.Equal(0, fixture.Provider.Calls);
    }

    /// <summary>Admission validates every query and each configured allowance before it can publish an accepted comparison request.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Admission_InvalidQueryOrBudgetPublishesNoRequest(bool invalidBudget)
    {
        using var fixture = new Fixture();
        var book = await WriteAdmissionBookAsync(fixture, invalidBudget ? "AAA" : new string('A', 33), "alpaca", invalidBudget ? 0 : 100);
        var adapter = new WorkflowRunConfiguration(null!, null!, new SimpleYamlReader(), null!);
        if (invalidBudget)
            await Assert.ThrowsAsync<InvalidDataException>(() => adapter.WritePreparedComparisonRequestAsync("admission", [book], Start, default));
        else
            await Assert.ThrowsAsync<ArgumentException>(() => adapter.WritePreparedComparisonRequestAsync("admission", [book], Start, default));
        Assert.Empty(Directory.GetFiles(fixture.Root, "request.json", SearchOption.AllDirectories));
        Assert.Equal(0, fixture.Provider.Calls);
    }

    /// <summary>Local CSV admission keeps the existing declared-file path and creates no prepared snapshot or invented provider evidence.</summary>
    [Fact]
    public async Task Admission_LocalCsvKeepsDeclaredFilePathWithoutPreparedClaim()
    {
        using var fixture = new Fixture();
        var book = await WriteAdmissionBookAsync(fixture, "AAA", "csv", 100);
        var adapter = new WorkflowRunConfiguration(null!, null!, new SimpleYamlReader(), null!);
        Assert.Null(await adapter.WritePreparedComparisonRequestAsync("csv-admission", [book], Start, default));
        Assert.Empty(Directory.GetFiles(fixture.Root, "request.json", SearchOption.AllDirectories));
        Assert.Equal(0, fixture.Provider.Calls);
    }

    /// <summary>A mixed comparison is rejected rather than presenting only its provider-backed books as a common fixed snapshot.</summary>
    [Fact]
    public async Task Admission_MixedCsvAndProviderHistoryIsRejected()
    {
        using var fixture = new Fixture();
        var csv = await WriteAdmissionBookAsync(fixture, "AAA", "csv", 100);
        var online = await WriteAdmissionBookAsync(fixture, "BBB", "alpaca", 100);
        var adapter = new WorkflowRunConfiguration(null!, null!, new SimpleYamlReader(), null!);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.WritePreparedComparisonRequestAsync("mixed", [csv, online], Start, default));
        Assert.Contains("cannot mix local CSV", error.Message);
        Assert.Empty(Directory.GetFiles(fixture.Root, "request.json", SearchOption.AllDirectories));
    }

    /// <summary>Finviz retains its existing explicit unsupported historical OHLCV contract instead of a generic evidence error.</summary>
    [Fact]
    public void FinvizHistoricalRequest_RetainsExplicitUnsupportedContract()
    {
        using var fixture = new Fixture();
        var service = new PreparedMarketDataService(ConfiguredDataProviders.FromEnvironment());
        var run = fixture.Run("finviz", ["AAA"]) with { Provider = "finviz" };
        var error = Assert.Throws<NotSupportedException>(() => service.ValidateRequest(fixture.Request("finviz", run)));
        Assert.Contains("Finviz does not provide a raw API for Historical OHLCV Bars", error.Message);
        Assert.Empty(Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories));
    }

    /// <summary>The public source planner has no collection side effects, including when bounds or source allowances are rejected.</summary>
    [Fact]
    public void ValidateRequest_ChecksGraphWithoutCollectingOrPublishing()
    {
        using var fixture = new Fixture();
        var run = fixture.Run("validation", ["AAA"]);
        fixture.Service.ValidateRequest(fixture.Request("valid", run));
        Assert.Throws<InvalidDataException>(() => fixture.Service.ValidateRequest(fixture.Request("budget", run with { Engine = run.Engine with { MaxRetainedReplayBars = 0 } })));
        Assert.Throws<InvalidDataException>(() => fixture.Service.ValidateRequest(fixture.Request("bounds", run with { TimeWindow = run.TimeWindow with { End = Start.AddDays(-1) } })));
        Assert.Equal(0, fixture.Provider.Calls);
        Assert.Empty(Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories));
    }

    /// <summary>A conflicting prepared close cannot replace the frozen SIP session or commit a complete comparison snapshot.</summary>
    [Fact]
    public async Task PreparedCalendarConflict_PreservesFrozenSipAuthority()
    {
        using var fixture = new Fixture();
        using var archives = new SipPortfolioExecutionSourceTests.Sources();
        var calendarPath = Path.Combine(fixture.Root, "calendar.json");
        var receiptPath = Path.Combine(fixture.Root, "calendar.receipt.json");
        var calendar = JsonSerializer.SerializeToUtf8Bytes(new[] { new { date = "2026-08-03", open = "09:30", close = "16:00" } });
        await File.WriteAllBytesAsync(calendarPath, calendar);
        var receipt = JsonSerializer.SerializeToUtf8Bytes(new { HttpStatus = 200, Sha256 = Convert.ToHexStringLower(SHA256.HashData(calendar)),
            RowCount = 1, Start = "2026-08-03", End = "2026-08-03", CapturedUtc = DateTimeOffset.UtcNow, OriginalHistoricalReceiptClaim = false });
        await File.WriteAllBytesAsync(receiptPath, receipt);
        var session = await archives.Session("AAA", SipNanosecondTime.Parse("2026-08-03T13:30:00Z"), [], [], [], sessionDurationNanoseconds: 23_400_000_000_000L);
        var manifest = new EventPortfolioSourceManifest(1, SipPortfolioExecutionSourceTests.Policy(), [session],
            new(ExecutionProtectionActivation.NativeParentFullyFilled, 30_000_000_000L, 0, true, 1, 1, "synthetic_fixture"),
            receiptPath, Convert.ToHexStringLower(SHA256.HashData(receipt)));
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var manifestPath = Path.Combine(fixture.Root, "event-source.json");
        await File.WriteAllBytesAsync(manifestPath, manifestBytes);
        var run = fixture.Run("conflicting-calendar", ["AAA"]);
        run = run with { Engine = run.Engine with { ExecutionSourceManifestPath = manifestPath,
            ExecutionSourceManifestSha256 = Convert.ToHexStringLower(SHA256.HashData(manifestBytes)) } };
        var provider = new CalendarSyntheticProvider(Path.Combine(fixture.Root, "raw"));
        var service = new PreparedMarketDataService(ConfiguredDataProviders.FromEnvironment(), _ => provider);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(fixture.Request("conflict", run), null, default));
        Assert.Contains("conflicts with frozen SIP", error.Message);
        Assert.Equal(calendar, await File.ReadAllBytesAsync(calendarPath));
        Assert.Equal(receipt, await File.ReadAllBytesAsync(receiptPath));
        Assert.Empty(Directory.GetFiles(fixture.Root, "snapshot.json", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(fixture.Root, "selection.json", SearchOption.AllDirectories));
        Assert.NotEmpty(Directory.GetFiles(fixture.Root, "version-*.json", SearchOption.AllDirectories));
    }

    /// <summary>Creates only owned admission config bytes and captures their real strategy source as immutable input evidence.</summary>
    private static async Task<PersistedJob> WriteAdmissionBookAsync(Fixture fixture, string ticker, string provider, int maximumBars)
    {
        var repository = TestRepository.FindRoot();
        var text = (await File.ReadAllTextAsync(Path.Combine(repository, "configs", "backtest", "swing-backtest-profile.yaml"))).Replace("\r\n", "\n", StringComparison.Ordinal);
        var strategyStart = text.IndexOf("\nstrategies:", StringComparison.Ordinal);
        text = text[..strategyStart] + "\nstrategies:\n  - " + RetiredStrategyFixtures.Strategy("swing-connors-rsi2-sma5-risk-managed.bt-v7.yaml").Replace('\\', '/') + "\n";
        text = text.Replace("max_retained_replay_bars: 1000000", "max_retained_replay_bars: " + maximumBars, StringComparison.Ordinal)
            .Replace("  provider: alpaca", "  provider: " + provider, StringComparison.Ordinal)
            .Replace("mode: unresolved_wishlist", "mode: static", StringComparison.Ordinal);
        text += "\ntickers:\n  - " + ticker + "\n";
        var path = Path.Combine(fixture.Root, "admission-job-" + Guid.NewGuid().ToString("N") + ".yaml");
        await File.WriteAllTextAsync(path, text);
        var run = new SimpleYamlReader().ReadBacktestRun(path);
        return new() { Id = Guid.NewGuid(), RunName = "admission-book", ConfigPath = path,
            RequestJson = DurableFileJobRequest.Capture(new[] { path }.Concat(run.Strategies)).Serialize() };
    }

    /// <summary>All books receive one completed snapshot, and an unchanged second comparison reuses exact shared source versions.</summary>
    [Fact]
    public async Task SharedUnion_IsPinnedAcrossBooksAndReusableAcrossComparisons()
    {
        using var fixture = new Fixture();
        var first = fixture.Run("first", ["AAA"]);
        var second = fixture.Run("second", ["AAA", "BBB"]);
        var request = fixture.Request("union", first, second);
        using var handle = await fixture.Service.PrepareAsync(request, null, default);
        Assert.Equal(2, fixture.Provider.Calls);
        Assert.Equal(2, handle.Summary.DownloadedQueryCount);
        Assert.Single(await ReadAsync(handle.CreateProvider(first), "AAA"));
        Assert.Single(await ReadAsync(handle.CreateProvider(second), "BBB"));
        using var reopened = await fixture.Service.PrepareAsync(request, null, default);
        Assert.Equal(handle.Summary.SnapshotSha256, reopened.Summary.SnapshotSha256);
        using var later = await fixture.Service.PrepareAsync(fixture.Request("later", first, second), null, default);
        Assert.Equal(2, fixture.Provider.Calls);
        Assert.Equal(2, later.Summary.ReusedQueryCount);
    }

    /// <summary>A later comparison shares a containing source but its child cannot read beyond its admitted book bounds or swap parents on resume.</summary>
    [Fact]
    public async Task ContainingSource_PreservesAdmittedBookBoundsAndPinnedParentAcrossResume()
    {
        using var fixture = new Fixture();
        var wide = fixture.Run("wide", ["AAA"]);
        wide = wide with { TimeWindow = wide.TimeWindow with { End = Start.AddDays(2) } };
        string parentCsv;
        using (var parent = await fixture.Service.PrepareAsync(fixture.Request("wide", wide), null, default))
            parentCsv = Assert.Single(parent.VerifiedFiles, path => path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));
        var narrow = fixture.Run("narrow", ["AAA"]);
        narrow = narrow with { Engine = narrow.Engine with { MaxRetainedReplayBars = 1 } };
        var request = fixture.Request("narrow", narrow);
        string pinnedSnapshot;
        using (var child = await fixture.Service.PrepareAsync(request, null, default))
        {
            Assert.Equal(1, fixture.Provider.Calls);
            Assert.Equal(parentCsv, Assert.Single(child.VerifiedFiles, path => path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)));
            Assert.Equal(1, child.Summary.ReusedQueryCount);
            Assert.Single(await ReadAsync(child.CreateProvider(narrow), "AAA"));
            var provider = child.CreateProvider(narrow);
            await Assert.ThrowsAsync<InvalidDataException>(async () => {
                await foreach (var _ in provider.GetBarsAsync(["AAA"], ["1d"], Start, Start.AddDays(2), default)) { }
            });
            pinnedSnapshot = child.Summary.SnapshotSha256;
        }
        using (var refresh = await fixture.Service.PrepareAsync(fixture.Request("refresh-wide", wide with { CachePolicy = "refresh" }), null, default))
            Assert.NotEqual(parentCsv, Assert.Single(refresh.VerifiedFiles, path => path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)));
        using var resumed = await fixture.Service.PrepareAsync(request, null, default);
        Assert.Equal(pinnedSnapshot, resumed.Summary.SnapshotSha256);
        Assert.Contains(parentCsv, resumed.VerifiedFiles);
        Assert.Equal(2, fixture.Provider.Calls);
    }

    /// <summary>A canceled refresh keeps completed query choices and a replacement preparer collects only unfinished requests.</summary>
    [Fact]
    public async Task CanceledRefresh_ResumesPreviouslySelectedParts()
    {
        using var fixture = new Fixture();
        var run = fixture.Run("refresh", ["AAA", "BBB", "CCC"]) with { CachePolicy = "refresh" };
        var request = fixture.Request("resume", run);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(value => { if (value.CompletedTickerCount == 1) cancellation.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.PrepareAsync(request, progress, cancellation.Token));
        Assert.Equal(1, fixture.Provider.Calls);
        using var resumed = await fixture.Service.PrepareAsync(request, null, default);
        Assert.Equal(3, fixture.Provider.Calls);
        using var repeated = await fixture.Service.PrepareAsync(request, null, default);
        Assert.Equal(resumed.Summary.SnapshotSha256, repeated.Summary.SnapshotSha256);
        Assert.Equal(3, fixture.Provider.Calls);
    }

    /// <summary>Read leases reject normal mutation, and corruption after disposal fails instead of recollecting a bound comparison.</summary>
    [Fact]
    public async Task BoundCorruption_FailsWithoutReplacementCollection()
    {
        using var fixture = new Fixture();
        var request = fixture.Request("corrupt", fixture.Run("first", ["AAA"]));
        var handle = await fixture.Service.PrepareAsync(request, null, default);
        var csv = Assert.Single(handle.VerifiedFiles, path => path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));
        if (OperatingSystem.IsWindows()) Assert.Throws<IOException>(() => File.WriteAllText(csv, "changed"));
        handle.Dispose();
        await File.WriteAllTextAsync(csv, "changed");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.PrepareAsync(request, null, default));
        Assert.Equal(1, fixture.Provider.Calls);
    }

    /// <summary>The source allowance is shared per book, so collection stops before a later request can exceed that book's budget.</summary>
    [Fact]
    public async Task SourceBudget_IsChargedToEveryReferencingBook()
    {
        using var fixture = new Fixture();
        var large = fixture.Run("large", ["AAA", "BBB"]);
        var small = fixture.Run("small", ["AAA", "BBB"]) with { Engine = large.Engine with { MaxRetainedReplayBars = 1 } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PrepareAsync(fixture.Request("budget", large, small), null, default));
        Assert.Equal(1, fixture.Provider.Calls);
        Assert.Empty(Directory.GetFiles(fixture.Root, "snapshot.json", SearchOption.AllDirectories));
    }

    /// <summary>Sibling preparers share one gate; canceling a waiter does not cancel collection owned by another child.</summary>
    [Fact]
    public async Task ConcurrentWaiterCancellation_DoesNotCancelOwner()
    {
        using var fixture = new Fixture();
        fixture.Provider.Pause = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = fixture.Request("concurrent", fixture.Run("first", ["AAA"]));
        var owner = fixture.Service.PrepareAsync(request, null, default);
        await fixture.Provider.Entered.Task;
        using var cancellation = new CancellationTokenSource();
        var waiter = fixture.Service.PrepareAsync(request, null, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        fixture.Provider.Pause.SetResult();
        using var handle = await owner;
        using var sibling = await fixture.Service.PrepareAsync(request, null, default);
        Assert.Equal(handle.Summary.SnapshotSha256, sibling.Summary.SnapshotSha256);
        Assert.Equal(1, fixture.Provider.Calls);
    }

    /// <summary>A snapshot published before application binding is recovered as-is rather than refreshed a second time.</summary>
    [Fact]
    public async Task BindingFailure_RecoversSameCommittedSnapshot()
    {
        using var fixture = new Fixture();
        var binding = new FailOnceBinding();
        var service = new PreparedMarketDataService(ConfiguredDataProviders.FromEnvironment(), _ => fixture.Provider, binding);
        var request = fixture.Request("binding", fixture.Run("first", ["AAA"]) with { CachePolicy = "refresh" });
        await Assert.ThrowsAsync<IOException>(() => service.PrepareAsync(request, null, default));
        var snapshot = Assert.Single(Directory.GetFiles(fixture.Root, "snapshot.json", SearchOption.AllDirectories));
        var originalBytes = await File.ReadAllBytesAsync(snapshot);
        using var handle = await service.PrepareAsync(request, null, default);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(snapshot));
        Assert.Equal(1, fixture.Provider.Calls);
        Assert.Equal(handle.Summary.SnapshotSha256, binding.Binding!.SnapshotSha256);
    }

    /// <summary>Pinned providers refuse new source ranges instead of using cached latest selection or network fallbacks.</summary>
    [Fact]
    public async Task PinnedProvider_RejectsUnadmittedRanges()
    {
        using var fixture = new Fixture();
        var run = fixture.Run("first", ["AAA"]);
        using var handle = await fixture.Service.PrepareAsync(fixture.Request("exact", run), null, default);
        var provider = handle.CreateProvider(run);
        await Assert.ThrowsAsync<InvalidDataException>(async () => {
            await foreach (var _ in provider.GetBarsAsync(["AAA"], ["1d"], Start.AddDays(-1), Start.AddDays(1), default)) { }
        });
        Assert.Equal(1, fixture.Provider.Calls);
    }

    /// <summary>Reads the same exact query as the immutable fixture request.</summary>
    private static async Task<IReadOnlyList<OhlcvBar>> ReadAsync(IMarketDataProvider provider, string ticker)
    {
        var bars = new List<OhlcvBar>();
        await foreach (var bar in provider.GetBarsAsync([ticker], ["1d"], Start, Start.AddDays(1), default)) bars.Add(bar);
        return bars;
    }

    private sealed class InlineProgress(Action<BacktestProgress> report) : IProgress<BacktestProgress>
    {
        /// <summary>Delivers deterministic cancellation at the completed-part checkpoint.</summary>
        public void Report(BacktestProgress value) => report(value);
    }

    private sealed class FailOnceBinding : IPreparedMarketDataBindingStore
    {
        private bool failed;
        public PreparedMarketDataBinding? Binding { get; private set; }
        /// <summary>Returns only the application-committed selection.</summary>
        public Task<PreparedMarketDataBinding?> GetAsync(string comparisonId, CancellationToken cancellationToken) => Task.FromResult(Binding);
        /// <summary>Simulates a crash after source publication and before event-store commitment.</summary>
        public Task BindAsync(string comparisonId, PreparedMarketDataBinding binding, CancellationToken cancellationToken)
        {
            if (!failed) { failed = true; throw new IOException("Synthetic binding failure."); }
            Binding = binding;
            return Task.CompletedTask;
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tf-prepared-synthetic-" + Guid.NewGuid().ToString("N"));
        public SyntheticProvider Provider { get; }
        public PreparedMarketDataService Service { get; }
        /// <summary>Owns every synthetic source, receipt and output under a fresh isolated temporary root.</summary>
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Provider = new(Path.Combine(Root, "raw"));
            Service = new(ConfiguredDataProviders.FromEnvironment(), _ => Provider);
        }
        /// <summary>Builds a small admitted source-only book; no strategy evaluation or real provider is involved.</summary>
        public BacktestRunConfig Run(string name, IReadOnlyList<string> tickers) => new(name, "backtest", new("bounded", 1, 1, 1, 10, true, 100),
            new("fixed", 0, Start, Start.AddDays(1)), tickers, "synthetic", ["1d"], Path.Combine(Root, "raw"),
            Path.Combine(Root, "normalized"), Path.Combine(Root, "results"), "use_cache", new("1d"),
            new(new(false, 0), new(false, 1, 1), new(false, ""), new(false, 0, 0, 0), new("synthetic", null, "all")),
            new(new("sip")), null!, null!, null!, null!, null!, null!, []);
        /// <summary>Uses distinct durable book identities while keeping the same comparison retry identity.</summary>
        public PreparedMarketDataRequest Request(string id, params BacktestRunConfig[] runs) => new(1, id, Start.AddDays(1),
            runs.Select((run, index) => new PreparedMarketDataBook("book-" + index, run, [])).ToArray());
        /// <summary>Deletes only the root this fixture created after all source leases have been released.</summary>
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }

    private class SyntheticProvider(string rawRoot) : IMarketDataProvider, IMarketDataCacheSourceDescriptorProvider,
        IHistoricalMarketDataEvidenceProvider
    {
        protected string RawRoot { get; } = rawRoot;
        public int Calls { get; private set; }
        public TaskCompletionSource? Pause { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public MarketDataCacheSourceDescriptor CacheSourceDescriptor { get; } = new("synthetic", "sip", "all", "fixture_v1");
        public string HistoricalEndpointIdentity => "synthetic_fixture";
        public string HistoricalParserVersion => "fixture_v1";
        public string HistoricalCalendarIdentity => "none";
        /// <summary>Archives deterministic synthetic query bytes and declares complete fixture coverage explicitly.</summary>
        public async Task<HistoricalBarsCapture> CaptureHistoricalBarsAsync(string ticker, string timeframe,
            DateTimeOffset startUtc, DateTimeOffset endUtc, long maximumBars, CancellationToken cancellationToken)
        {
            Calls++;
            Entered.TrySetResult();
            if (Pause is not null) await Pause.Task.WaitAsync(cancellationToken);
            if (maximumBars < 1) throw new InvalidOperationException("Synthetic row allowance exceeded.");
            var now = DateTimeOffset.UtcNow;
            var bars = new[] { new OhlcvBar(ticker, startUtc, timeframe, 100, 101, 99, 100, 1000, "sip", "all") };
            var receipt = await new FileSystemRawArchiveWriter(new(RawRoot)).ArchiveAsync(
                new("synthetic", "bars", now, "json", "application/json", Attributes: new Dictionary<string, string> { ["evidence_kind"] = "synthetic_fixture" }),
                JsonSerializer.SerializeToUtf8Bytes(new { ticker, timeframe, startUtc, endUtc, bars }), cancellationToken);
            return new(bars, new(now, DateTimeOffset.UtcNow, true, [receipt]));
        }
        /// <summary>This source fixture deliberately has no calendar authority.</summary>
        public virtual Task<HistoricalCalendarCapture> CaptureHistoricalCalendarAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Fixture has no calendar.");
        /// <summary>Direct provider reads are unused; tests verify that prepared reads never invoke them.</summary>
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> timeframes,
            DateTimeOffset start, DateTimeOffset end, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            throw new InvalidOperationException("Use the explicit synthetic evidence capture.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class CalendarSyntheticProvider(string rawRoot) : SyntheticProvider(rawRoot), IMarketSessionScheduleProvider
    {
        public List<(DateOnly Start, DateOnly End)> CalendarCaptures { get; } = [];
        /// <summary>Explicit fixture sessions intentionally disagree by one minute with the frozen execution calendar.</summary>
        public Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>> LoadMarketSessionSchedulesAsync(DateOnly startDateInclusive,
            DateOnly endDateInclusive, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>>(
                Enumerable.Range(startDateInclusive.DayNumber, endDateInclusive.DayNumber - startDateInclusive.DayNumber + 1)
                    .Select(DateOnly.FromDayNumber).ToDictionary(date => date, date => new MarketSessionSchedule(date, true, new(9, 30), new(15, 59))));

        /// <summary>Archives every explicit synthetic calendar date before declaring the fixture response complete.</summary>
        public override async Task<HistoricalCalendarCapture> CaptureHistoricalCalendarAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            CalendarCaptures.Add((startDate, endDate));
            var sessions = await LoadMarketSessionSchedulesAsync(startDate, endDate, cancellationToken);
            var receipt = await new FileSystemRawArchiveWriter(new(RawRoot)).ArchiveAsync(
                new("synthetic", "calendar", now, "json", "application/json"), JsonSerializer.SerializeToUtf8Bytes(sessions), cancellationToken);
            return new(sessions, new(now, DateTimeOffset.UtcNow, true, [receipt]));
        }
    }
}
