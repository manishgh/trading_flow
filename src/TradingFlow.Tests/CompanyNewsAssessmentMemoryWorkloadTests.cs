using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using TradingFlow.Application.Jobs;
using TradingFlow.Backtesting;
using TradingFlow.Cli;
using TradingFlow.Data.Evidence.Historical;
using TradingFlow.Data.Evidence.Sentiment;
using TradingFlow.Domain.News;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Engine.Configuration;
using TradingFlow.Web.Services;
using Xunit.Abstractions;

namespace TradingFlow.Tests;

[CollectionDefinition(CompanyNewsAssessmentMemoryCollection.Name, DisableParallelization = true)]
public sealed class CompanyNewsAssessmentMemoryCollection
{
    public const string Name = "Company assessment isolated memory measurements";
}

/// <summary>Opt-in test switch, never an operator or production setting.</summary>
public sealed class CompanyNewsAssessmentMemoryProfileAttribute : FactAttribute
{
    /// <summary>Requires explicit test-host opt-in before any full-shaped raw or assessment fixture can allocate.</summary>
    public CompanyNewsAssessmentMemoryProfileAttribute()
    {
        if (Environment.GetEnvironmentVariable("TRADINGFLOW_PROFILE_COMPANY_ASSESSMENT") != "1")
            Skip = "Set TRADINGFLOW_PROFILE_COMPANY_ASSESSMENT=1 and select CompanyNewsAssessmentMemoryWorkloadTests; run serially with host headroom monitoring.";
    }
}

/// <summary>Measures full-shaped source replay, publication and reader residency using fake model evidence only.</summary>
[Collection(CompanyNewsAssessmentMemoryCollection.Name)]
public sealed class CompanyNewsAssessmentMemoryWorkloadTests(ITestOutputHelper output)
{
    /// <summary>Checks all frozen numeric sizes and generator arithmetic one field at a time.</summary>
    [Fact]
    public void FrozenAnonymousShapePreservesJointTextSizesEscapingAndIssuerDuplication()
    {
        var profile = CompanyNewsSyntheticShape.Read();
        Assert.Equal(2951, profile.Length);
        Assert.Equal(18016, profile.Sum(item => item.IssuerIds.Length));
        Assert.Equal(3261, profile.SelectMany(item => item.IssuerIds).Distinct().Count());
        Assert.Equal(24, CompanyNewsSyntheticShape.SelectedIssuerIds.Length);
        Assert.Equal(3263, profile.Sum(item => item.IssuerIds.Count(CompanyNewsSyntheticShape.SelectedIssuerIds.Contains)));
        Assert.All(profile, item => Assert.Contains(item.IssuerIds, CompanyNewsSyntheticShape.SelectedIssuerIds.Contains));
        Assert.Equal(402, profile.Max(item => item.IssuerIds.Length));
        Assert.Equal(119900, profile.Max(item => item.Body.Characters));
        Assert.Equal(23, profile.Count(item => item.Body.Characters > 32000));
        Assert.Equal(210255892L, profile.Sum(item => (long)Math.Max(0, item.Body.Characters) * item.IssuerIds.Length));
        Assert.Equal(295438811L, profile.Sum(item => (long)Math.Max(0, item.Body.EscapedJsonBytes) * item.IssuerIds.Length));
        // This numeric-only check allocates one source field at a time and cannot create the full assessment packet.
        foreach (var shape in profile)
        foreach (var field in new[] { shape.Headline, shape.Summary, shape.Body })
        {
            var text = field.Text("Z0000 raises guidance");
            if (field.Characters < 0) { Assert.Null(text); continue; }
            Assert.Equal(field.Characters, text!.Length);
            Assert.Equal(field.Utf8Bytes, Encoding.UTF8.GetByteCount(text));
            Assert.Equal(field.EscapedJsonBytes, JsonSerializer.SerializeToUtf8Bytes(text).Length - 2);
        }
    }

    /// <summary>Checks the complete profiling fixture path with two stories before allocating any full-shaped packet.</summary>
    [Fact]
    public async Task SmallSyntheticFixture_BuildPublishReportsReadAndBothScopeLifetimes()
    {
        ImmutableArray<CompanyNewsSyntheticShape> profile = [
            new(new(40, 40, 40), new(40, 40, 40), new(80, 80, 80), [265, 510]),
            new(new(32, 32, 32), new(0, 0, 0), new(0, 0, 0), [510])
        ];
        await RunShapeAsync(profile, expectFull: false);
    }

    /// <summary>Measures complete full-population production build, reports, publication and read paths serially.</summary>
    [CompanyNewsAssessmentMemoryProfile]
    [Trait("Category", "Performance")]
    public async Task FullArchiveShape_BuildPublishReportsReadAndSummary_MeasuresMemory()
    {
        WriteEnvironment();
        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        await RunFullShapeAsync();
        WriteRetained("after_fixture_disposal", baseline);
    }

    /// <summary>Stresses the original largest body and largest tag fanout with denser economic cues.</summary>
    [CompanyNewsAssessmentMemoryProfile]
    [Trait("Category", "Performance")]
    public async Task LargeBodyAndHighFanout_BuildPublishRead_RetainsFullSpans()
    {
        WriteEnvironment();
        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        var profile = CompanyNewsSyntheticShape.Read();
        // The actual longest body and highest-fanout story remain paired with their original numeric shapes.
        var stress = new[] { profile.MaxBy(item => item.Body.Characters)!, profile.MaxBy(item => item.IssuerIds.Length)! }
            .Distinct().ToImmutableArray();
        await RunShapeAsync(stress, expectFull: false, cueDense: true);
        WriteRetained("after_large_body_fixture_disposal", baseline);
    }

    /// <summary>Keeps the entire fixture graph outside the caller's post-disposal retained-heap measurement.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Task RunFullShapeAsync() => RunShapeAsync(CompanyNewsSyntheticShape.Read(), expectFull: true);

    /// <summary>Runs one isolated shape and releases builder graphs before measuring a newly deserialized packet.</summary>
    private async Task RunShapeAsync(ImmutableArray<CompanyNewsSyntheticShape> profile, bool expectFull, bool cueDense = false)
    {
        using var fixture = await MeasureAsync("synthetic_archive_and_source_replay", () => Task.FromResult(CompanyNewsSyntheticWorkloadFixture.Create(profile, cueDense)));
        Assert.Equal(profile.Length, fixture.Sources.Length);
        Assert.Equal(profile.Sum(item => item.IssuerIds.Length), fixture.Sources.Sum(item => item.Symbols.Length));
        var publication = await BuildPublishReportsAsync(fixture, expectFull);
        var diagnostic = fixture.PublishDiagnostic(publication);
        fixture.ReleaseSelectionObjects();
        // Collect only at a phase boundary, never as a production optimization or while timing a verifier.
        output.WriteLine(JsonSerializer.Serialize(new { Phase = "builder_graph_released_before_reader", RetainedManagedBytes = GC.GetTotalMemory(true) }));
        await ReadAndSummarizeAsync(fixture.Root, publication, profile.Sum(item => item.IssuerIds.Length));
        output.WriteLine(JsonSerializer.Serialize(new { Phase = "reader_graph_released_before_start_scope", RetainedManagedBytes = GC.GetTotalMemory(true) }));
        var run = new SimpleYamlReader().ReadBacktestRun(Path.Combine(TestRepository.FindRoot(), "configs", "backtest", "swing-backtest-profile.yaml"));
        run = run with { Strategies = [], Engine = run.Engine with { EvidenceResearchRoot = fixture.Root,
            EvidenceManifestPath = diagnostic.Path, EvidenceManifestSha256 = diagnostic.Sha256, EvidenceDependencyGraphSha256 = null } };
        var configPath = Path.Combine(fixture.Root, "synthetic-scope-input.yaml");
        File.WriteAllText(configPath, "synthetic immutable input marker; this file is never executed");
        var beforeScopes = GC.GetTotalMemory(true);
        var prepared = await ExerciseStartScopeAsync(run, configPath);
        WriteRetained("start_scope_disposed", beforeScopes);
        await ExerciseWorkerScopeAsync(prepared.Run, configPath, prepared.Capture);
        WriteRetained("fresh_worker_scope_disposed", beforeScopes);
    }

    /// <summary>Leaves only publication identity after exercising actual assessment and CLI report implementations.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<HistoricalDiagnosticNewsPublication> BuildPublishReportsAsync(CompanyNewsSyntheticWorkloadFixture fixture, bool expectFull)
    {
        var manifest = await MeasureAsync("assessment_build_and_validation", () => fixture.BuildAsync());
        if (expectFull)
        {
            Assert.Equal(18016, manifest.Rows.Length);
            Assert.Contains(manifest.Rows, item => item.Assessment.State == CompanyNewsAssessmentState.Positive);
            Assert.Contains(manifest.Rows, item => item.Assessment.State == CompanyNewsAssessmentState.Unknown);
            Assert.Contains(manifest.Rows, item => item.Assessment.Extraction.IssuerMapping is not null);
            Assert.Contains(manifest.Rows, item => item.Assessment.Extraction.IssuerMapping is null);
            Assert.True(fixture.FakeInferenceCalls > 0);
        }
        var publication = await MeasureAsync("assessment_publish_with_source_replay", () => Task.FromResult(
            CompanyNewsAssessmentManifestStore.PublishExclusive(Path.Combine(fixture.Root, "assessments"), manifest)));
        await MeasureAsync("samples_report", async () => {
            await using var stream = File.Create(Path.Combine(fixture.Root, "synthetic-samples.md"));
            await InvokeReportAsync("WriteSamplesAsync", stream, manifest, CancellationToken.None);
            return stream.Length;
        });
        await MeasureAsync("blind_review_report", async () => {
            await using var stream = File.Create(Path.Combine(fixture.Root, "synthetic-blind-review.json"));
            await InvokeReportAsync("WriteBlindReviewAsync", stream, publication.Sha256, fixture.Sources, CancellationToken.None);
            return stream.Length;
        });
        output.WriteLine(JsonSerializer.Serialize(new { Phase = "published_artifact_sizes", PacketBytes = new FileInfo(publication.Path).Length,
            SampleBytes = new FileInfo(Path.Combine(fixture.Root, "synthetic-samples.md")).Length,
            BlindReviewBytes = new FileInfo(Path.Combine(fixture.Root, "synthetic-blind-review.json")).Length,
            IssuerRows = manifest.Rows.Length, UniqueVersions = fixture.Sources.Length, FakeEvidenceCalls = fixture.FakeInferenceCalls,
            StateCounts = manifest.Rows.GroupBy(row => row.Assessment.State).ToDictionary(group => group.Key.ToString(), group => group.Count()),
            RuleCueCount = manifest.Rows.Sum(row => row.Assessment.Extraction.RuleResult.MatchedCues.Length),
            RuleCueRetainedCharacters = manifest.Rows.Sum(row => row.Assessment.Extraction.RuleResult.MatchedCues.Sum(cue =>
                (long)cue.MatchedText.Length + cue.EvidenceExcerpt.Length + cue.AssertionPrefix.Length)),
            ShapeApproximation = "Exact joint original field UTF16/UTF8/escaped JSON sizes and anonymous cross-story issuer tags. Synthetic assertions and bounded economic paragraphs approximate rule cues; the separate long-body/high-fanout case increases cue density. Original cue distribution and issuer-name lengths are not reproduced." }));
        return publication;
    }

    /// <summary>Verifies every full span, summarizes the verified object and records resident reader heap.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task ReadAndSummarizeAsync(string root, HistoricalDiagnosticNewsPublication publication, int expectedRows)
    {
        var loaded = await MeasureAsync("assessment_read_published_and_full_source_verification", () => Task.FromResult(
            CompanyNewsAssessmentManifestStore.ReadPublished(publication)));
        Assert.Equal(expectedRows, loaded.Rows.Length);
        // All spans still point to complete original fields, including long-tail supporting fields over the assertion limit.
        foreach (var row in loaded.Rows)
        foreach (var span in row.Assessment.Extraction.EvidenceSpans)
        {
            var original = span.Field switch { "headline" => row.Source.Revision.Headline, "summary" => row.Source.Revision.Summary, "body" => row.Source.Revision.Body, _ => null };
            Assert.Equal(original, span.FullContext);
            Assert.Equal(original, span.Text);
            Assert.Equal(0, span.Start);
            Assert.Equal(original!.Length, span.Length);
        }
        var summary = await MeasureAsync("summary_of_already_verified_packet", () => Task.FromResult(CompanyNewsAssessmentPreviewService.Summarize(loaded)));
        Assert.Equal(expectedRows, summary.AssessmentCount);
        Assert.Equal(expectedRows, summary.StateCounts.Values.Sum());
        output.WriteLine(JsonSerializer.Serialize(new { Phase = "reader_graph_retained", ManagedBytesAfterFullCollection = GC.GetTotalMemory(true),
            IssuerRows = loaded.Rows.Length, summary.StateCounts }));
        GC.KeepAlive(loaded);
    }

    /// <summary>Measures one real scope replay followed by twenty resolver consumers; this exercises resources, not economic simulation.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<(BacktestRunConfig Run, DurableFileJobRequest Capture)> ExerciseStartScopeAsync(BacktestRunConfig run, string configPath)
    {
        using var scope = await MeasureAsync("start_scope_open_full_replay", () => Task.FromResult(
            WorkflowEvidenceVerificationScope.Open(new object(), run.Engine)
            ?? throw new InvalidDataException("Synthetic mapped diagnostic did not produce a verification scope.")));
        run = run with { Engine = scope.Bind(run.Engine) };
        var verified = scope.Resolve(run.Engine);
        var required = await MeasureAsync("start_scope_twenty_sequential_resolver_consumers", () => {
            IReadOnlyList<string>? first = null;
            for (var book = 0; book < 20; book++)
            {
                scope.RequireLive(run.Engine);
                Assert.Same(verified, scope.Resolve(run.Engine));
                var inputs = BacktestRunInputFiles.Resolve(configPath, run, verified, scope.Guard);
                Assert.Equal(verified.Dependencies.Count + 1, inputs.Count);
                if (first is null) first = inputs;
                else Assert.Equal(first, inputs);
            }
            return Task.FromResult(first!);
        });
        var capture = await MeasureAsync("durable_input_capture_existing_path", () => Task.FromResult(DurableFileJobRequest.Capture(required)));
        output.WriteLine(JsonSerializer.Serialize(new { Phase = "start_scope_resource_exercise", SequentialConsumers = 20,
            DependencyFiles = verified.Dependencies.Count, GraphSha256 = verified.DependencyGraphSha256,
            Meaning = "Existing leased scope, identity guard and input resolver paths; no economic simulation or job enqueue. Multi-book workflow plumbing is verified separately on a smaller fixture." }));
        return (run, capture);
    }

    /// <summary>Reacquires durable inputs before a new worker scope and reuses its object at nested resolver boundaries.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task ExerciseWorkerScopeAsync(BacktestRunConfig run, string configPath, DurableFileJobRequest capture)
    {
        using var capturedLease = await MeasureAsync("worker_captured_input_lease", () => Task.FromResult(capture.AcquireVerifiedReadLease()));
        using var scope = await MeasureAsync("fresh_worker_scope_open_full_replay", () => Task.FromResult(
            WorkflowEvidenceVerificationScope.Open(new object(), run.Engine)
            ?? throw new InvalidDataException("Fresh worker failed to verify synthetic mapped evidence.")));
        var verified = scope.Resolve(run.Engine);
        await MeasureAsync("worker_and_nested_scope_resolver_consumers", () => {
            for (var nesting = 0; nesting < 3; nesting++)
            {
                scope.RequireLive(run.Engine);
                Assert.Same(verified, scope.Resolve(run.Engine));
                Assert.Same(verified, FrozenBacktestEvidence.FromConfig(run.Engine, verified, scope.Guard));
                capture.EnsureContains(BacktestRunInputFiles.Resolve(configPath, run, verified, scope.Guard));
            }
            return Task.FromResult(verified.Dependencies.Count);
        });
        output.WriteLine(JsonSerializer.Serialize(new { Phase = "worker_scope_resource_exercise", NestedResolverConsumers = 3,
            GraphSha256 = verified.DependencyGraphSha256, Meaning = "Fresh worker lease and scope replay after Start disposal; no backtest or custom economic driver." }));
    }

    /// <summary>Calls the CLI's actual private bounded report methods without introducing a second report implementation.</summary>
    private static Task InvokeReportAsync(string name, params object[] arguments) =>
        (Task)(typeof(CompanyNewsAssessmentCommand).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(typeof(CompanyNewsAssessmentCommand).FullName, name)).Invoke(null, arguments)!;

    /// <summary>Records process-wide allocation/GC deltas and sampled working/private peaks for a named production phase.</summary>
    private async Task<T> MeasureAsync<T>(string phase, Func<Task<T>> action)
    {
        using var process = Process.GetCurrentProcess();
        long peakWorking = 0, peakPrivate = 0;
        var sampleLock = new object();
        // Record process peaks under one lock so timer and phase-boundary samples cannot race.
        void Sample()
        {
            lock (sampleLock)
            {
                process.Refresh();
                peakWorking = Math.Max(peakWorking, process.WorkingSet64);
                peakPrivate = Math.Max(peakPrivate, process.PrivateMemorySize64);
            }
        }
        Sample();
        var allocated = GC.GetTotalAllocatedBytes(true);
        var counts = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        var stopwatch = Stopwatch.StartNew();
        using var timer = new Timer(_ => Sample(), null, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));
        try { return await action(); }
        finally
        {
            stopwatch.Stop();
            await timer.DisposeAsync();
            Sample();
            var info = GC.GetGCMemoryInfo();
            output.WriteLine(JsonSerializer.Serialize(new { Phase = phase, ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
                AllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated,
                Gen0Collections = GC.CollectionCount(0) - counts[0], Gen1Collections = GC.CollectionCount(1) - counts[1],
                Gen2Collections = GC.CollectionCount(2) - counts[2], SampledPeakWorkingSetBytes = peakWorking,
                SampledPeakPrivateBytes = peakPrivate, ProcessLifetimePeakWorkingSetBytes = process.PeakWorkingSet64,
                ManagedBytesWithoutCollection = GC.GetTotalMemory(false), LastGcHeapBytes = info.HeapSizeBytes,
                LastGcFragmentedBytes = info.FragmentedBytes, info.MemoryLoadBytes, info.HighMemoryLoadThresholdBytes }));
        }
    }

    /// <summary>Labels host limits and measurement semantics so an observed peak is not confused with retained state.</summary>
    private void WriteEnvironment()
    {
        var info = GC.GetGCMemoryInfo();
        output.WriteLine(JsonSerializer.Serialize(new { Fixture = "anonymous_frozen_company_news_archive_shape_v1",
            CompanyNewsSyntheticShape.SourceManifestSha256, CompanyNewsSyntheticShape.ProfileSha256,
            Environment.Version, Environment.Is64BitProcess, Environment.ProcessorCount, info.TotalAvailableMemoryBytes,
            info.HighMemoryLoadThresholdBytes,
            Meaning = "Process-wide allocations include 50ms memory sampling. Sampled peaks may miss shorter spikes; OS working-set peak is host-lifetime. Reader runs after releasing builder graphs; full GC only measures phase-boundary retention. No inference, economic run, operator data write or automatic resource approval." }));
    }

    /// <summary>Measures managed retention after all owned fixture objects and files have been disposed.</summary>
    private void WriteRetained(string phase, long baseline)
    {
        var retained = GC.GetTotalMemory(true);
        using var process = Process.GetCurrentProcess();
        output.WriteLine(JsonSerializer.Serialize(new { Phase = phase, BaselineManagedBytes = baseline, RetainedManagedBytes = retained,
            RetainedManagedDeltaBytes = retained - baseline, WorkingSetBytes = process.WorkingSet64, PrivateBytes = process.PrivateMemorySize64 }));
    }
}
