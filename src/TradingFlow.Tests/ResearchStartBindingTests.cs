using Moq;
using TradingFlow.Application.Jobs;
using TradingFlow.Data.Evidence.Governance;
using TradingFlow.Domain.Jobs;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Research;

namespace TradingFlow.Tests;

public sealed class ResearchStartBindingTests
{
    private static readonly DateTimeOffset Before = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Holdout = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly string Rule = ResearchStartIdentity.HashText("exact rule");
    private static readonly string Code = ResearchStartIdentity.HashText("test binaries");

    /// <summary>Independent variants receive consecutive ordinals; group jobs share their frozen variant.</summary>
    [Fact]
    public async Task ConcurrentRetry_BindsOneOrdinalPerVariantAndSharesItAcrossGroups()
    {
        using var fixture = new TemporaryFolder();
        var registry = Registry(fixture);
        var request = Start("grouped", ResearchStartKind.CatalogStudy, [Variant("first"), Variant("second")],
            new Dictionary<string, string[]> { ["group-a"] = ["first"], ["group-b"] = ["first"], ["group-c"] = ["second"] });
        var registrations = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Registry(fixture).ReserveStartAsync(request, default))));
        Assert.All(registrations, registration => Assert.Equal(new[] { 1, 2 }, registration.Binding.Trials.Select(trial => trial.FamilyOrdinal)));
        var ledger = await registry.ReadLedgerAsync(default);
        Assert.Equal(2, ledger.Trials.Count);
        Assert.Single(ledger.Starts);
        Assert.Equal(ResearchHoldoutState.Unopened, ledger.Trials[0].HoldoutState);
        Assert.Equal(ResearchStartState.Pending, ledger.Starts[0].Start.State);
        await Assert.ThrowsAsync<IdempotencyKeyConflictException>(() => registry.ReserveStartAsync(request with { CodeSha256 = Rule }, default));
    }

    /// <summary>The first observable computation can already find the exact canonical trial, and cancellation keeps its original ordinal.</summary>
    [Fact]
    public async Task DirectCancellation_IsRetainedAndRetryReusesTrial()
    {
        using var fixture = new TemporaryFolder();
        var registry = Registry(fixture);
        var request = Start("cancelled", ResearchStartKind.SignalStudy, [Variant("v")]);
        await Assert.ThrowsAsync<OperationCanceledException>(() => registry.RunDirectAsync<string>(request, "compute",
            async (start, _) =>
            {
                var actual = await registry.GetTrialAsync(start.Binding.Trials[0].ExperimentId);
                Assert.Equal(start.Binding.Trials[0].DefinitionSha256, EvidenceCanonicalJson.ComputeSha256(actual!));
                throw new OperationCanceledException();
            }, (_, _) => throw new InvalidOperationException("There is no completed result yet."), default));
        var afterCancel = await registry.ReadLedgerAsync(default);
        Assert.Equal("cancelled", Assert.Single(Assert.Single(afterCancel.Starts).Executions).State);
        Assert.Equal(ResearchStartState.Interrupted, afterCancel.Starts[0].Start.State);
        var published = await PublishStudyAsync(fixture, registry, request);
        var replay = await registry.RunDirectAsync<string>(request, "compute",
            (_, _) => throw new InvalidOperationException("Completed research is not recomputed."),
            (prior, _) => Task.FromResult(prior.ArtifactReference!), default);
        Assert.Equal(published.Artifact, replay);
        var final = await registry.ReadLedgerAsync(default);
        Assert.Equal(1, Assert.Single(final.Trials).Trial.FamilyTrialCount);
        Assert.Equal(new[] { "cancelled", "completed" }, Assert.Single(final.Starts).Executions.Select(result => result.State));
    }

    /// <summary>Portfolio labels cannot grant access, while a specific reviewed replay can be honestly recorded without an ordinal.</summary>
    [Fact]
    public async Task Portfolio_RequiresExactReviewAndExistingReplayHasNoInventedTrial()
    {
        using var fixture = new TemporaryFolder();
        var request = Start("plain", ResearchStartKind.Portfolio, [Variant("diagnostic-replay")]);
        var refused = await Registry(fixture).ReserveStartAsync(request, default);
        Assert.Equal(ResearchStartState.Refused, refused.State);
        Assert.Empty(refused.Binding.Trials);
        Assert.False(refused.Binding.DiagnosticReplay);

        var originalArtifact = Path.Combine(fixture.Path, "original.json");
        await File.WriteAllTextAsync(originalArtifact, "retained original evidence");
        var replay = request with { StartId = "approved-replay", RequestSha256 = ResearchStartIdentity.HashText("exact-original"),
            Variants = [request.Variants[0] with { Partitions = [Development()] }] };
        var reviews = new ResearchAdmissionReviews([], [new(replay.RequestSha256, ResearchStartIdentity.HashFile(originalArtifact),
            "reviewed exact plan / retained artifact", originalArtifact, Holdout)]);
        var registry = Registry(fixture, reviews);
        var admitted = await registry.ReserveStartAsync(replay, default);
        Assert.Equal(ResearchStartState.Pending, admitted.State);
        Assert.True(admitted.Binding.DiagnosticReplay);
        Assert.Empty(admitted.Binding.Trials);
        Assert.Equal(Holdout, admitted.Binding.ProtectedObservationStartUtc);
        Assert.Empty((await registry.ReadLedgerAsync(default)).Trials);
    }

    /// <summary>A study result is accepted only for the same family, exact rule, precise result hash, and unopened-window exclusion.</summary>
    [Fact]
    public async Task SignalStudyReview_VerifiesExactRegisteredAndPublishedEvidence()
    {
        using var fixture = new TemporaryFolder();
        var registration = Start("successful-study", ResearchStartKind.SignalStudy, [Variant("v")]);
        var published = await PublishStudyAsync(fixture, Registry(fixture), registration);
        var candidate = Start("new-portfolio", ResearchStartKind.Portfolio, [Variant("p") with { Partitions = [Development()] }]);
        var exact = new ReviewedSignalStudy(candidate.RequestSha256, "test-family", Rule, published.Result.ExperimentId,
            published.Result.TrialDefinitionSha256, published.Result.ResultId, EvidenceCanonicalJson.ComputeSha256(published.Result),
            ResearchStartIdentity.HashFile(published.Artifact), "reviewed synthetic result");
        var registry = Registry(fixture, new([exact], []));
        Assert.Equal(ResearchStartState.Pending, (await registry.ReserveStartAsync(candidate, default)).State);
        var anotherFamily = candidate with { StartId = "wrong-family", Variants = [candidate.Variants[0] with { FamilyId = "other" }] };
        Assert.Equal(ResearchStartState.Refused, (await registry.ReserveStartAsync(anotherFamily, default)).State);
        var wrongHash = Registry(fixture, new([exact with { ResultSha256 = Rule }], []));
        Assert.Equal(ResearchStartState.Refused, (await wrongHash.ReserveStartAsync(candidate with { StartId = "wrong-hash" }, default)).State);
        var overlaps = candidate with { StartId = "overlap-holdout", Variants = [candidate.Variants[0] with
        {
            Partitions = [new("development", Holdout.AddDays(-7), Holdout.AddDays(1), TimeSpan.Zero)]
        }] };
        Assert.Equal(ResearchStartState.Refused, (await registry.ReserveStartAsync(overlaps, default)).State);
        var beforeDevelopment = candidate with { StartId = "before-development", Variants = [candidate.Variants[0] with
        {
            Partitions = [new("development", Before.AddDays(-1), Holdout, TimeSpan.Zero)]
        }] };
        Assert.Equal(ResearchStartState.Refused, (await registry.ReserveStartAsync(beforeDevelopment, default)).State);
        await File.WriteAllTextAsync(published.Artifact, "corrupted published study");
        Assert.Equal(ResearchStartState.Refused, (await registry.ReserveStartAsync(candidate with { StartId = "changed-artifact" }, default)).State);
    }

    /// <summary>Recovers after the queue commits but its caller sees a timeout and rejects another competing worker payload.</summary>
    [Fact]
    public async Task QueueRetry_ReconcilesExactWinnerAndRejectsChangedWorkerCodeOrInput()
    {
        using var fixture = new TemporaryFolder();
        var id = Guid.NewGuid();
        var request = Start("queued", ResearchStartKind.Portfolio, [Variant("v") with { Partitions = [Development()] }],
            new Dictionary<string, string[]> { [id.ToString("N")] = ["v"] });
        var signal = await PublishStudyAsync(fixture, Registry(fixture), Start("source-study", ResearchStartKind.SignalStudy, [Variant("v")]));
        var review = new ReviewedSignalStudy(request.RequestSha256, "test-family", Rule, signal.Result.ExperimentId,
            signal.Result.TrialDefinitionSha256, signal.Result.ResultId, EvidenceCanonicalJson.ComputeSha256(signal.Result),
            ResearchStartIdentity.HashFile(signal.Artifact), "synthetic approved study");
        var registry = Registry(fixture, new([review], []));
        var jobs = new Mock<IDurableJobRepository>();
        var queue = new ResearchQueue(registry, jobs.Object, Code);
        var path = Path.Combine(fixture.Path, "synthetic-config.txt");
        await File.WriteAllTextAsync(path, "frozen");
        var job = new PersistedJob { Id = id, JobType = "backtest", ConfigPath = path,
            RequestJson = DurableFileJobRequest.Capture([path]).Serialize(), CreatedAt = Before };
        PersistedJob? enqueued = null;
        jobs.Setup(value => value.GetJobAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(() => enqueued);
        jobs.Setup(value => value.TryEnqueueAsync(It.IsAny<PersistedJob>(), 1, It.IsAny<CancellationToken>()))
            .Returns<PersistedJob, int, CancellationToken>((candidate, _, _) =>
            {
                enqueued = candidate;
                throw new TimeoutException("Synthetic failure after the queue committed.");
            });
        await Assert.ThrowsAsync<TimeoutException>(() => queue.AdmitAsync(request, 1, (_, _) => Task.FromResult<IReadOnlyList<PersistedJob>>([job]), default));
        var reconciled = await queue.AdmitAsync(request, 1, (_, _) => throw new InvalidOperationException("Retry cannot recreate inputs."), default);
        Assert.Equal(job.RequestJson, Assert.Single(reconciled).RequestJson);
        await registry.RequireQueueJobAsync(job, Code, default);
        await Assert.ThrowsAsync<InvalidDataException>(() => registry.RequireQueueJobAsync(job, Rule, default));
        var changed = new PersistedJob { Id = id, JobType = "backtest", ConfigPath = path, RequestJson = "changed", CreatedAt = Before };
        await Assert.ThrowsAsync<InvalidDataException>(() => registry.RequireQueueJobAsync(changed, Code, default));
        job.Status = "cancelled";
        job.FinishedAt = Before.AddMinutes(1);
        await queue.RecordResultAsync(job, default);
        var ledger = await registry.ReadLedgerAsync(default);
        Assert.Equal("cancelled", Assert.Single(ledger.Starts.Single(item => item.Start.Binding.Request.StartId == "queued").Executions).State);
    }

    /// <summary>An empty ledger is honest and does not create even the registry database.</summary>
    [Fact]
    public async Task ReadOnlyLedger_DoesNotCreateMissingRegistry()
    {
        using var fixture = new TemporaryFolder();
        var ledger = await Registry(fixture).ReadLedgerAsync(default);
        Assert.Empty(ledger.Trials);
        Assert.Empty(ledger.Starts);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Path));
    }

    /// <summary>Two explicit creators constructed against an absent file cannot both open that registry as new.</summary>
    [Fact]
    public async Task StrictBootstrap_RechecksFileAfterAcquiringWriteTransaction()
    {
        using var fixture = new TemporaryFolder();
        var options = new ResearchTrialRegistryOptions(Path.Combine(fixture.Path, "registry.db"), ResearchTrialRegistryOpenMode.BootstrapNew);
        var winner = new SqliteResearchTrialRegistry(options);
        var contender = new SqliteResearchTrialRegistry(options);
        var request = Start("bootstrap", ResearchStartKind.SignalStudy, [Variant("v")]);
        await winner.ReserveStartAsync(request, default);
        await Assert.ThrowsAsync<IOException>(() => contender.ReserveStartAsync(request, default));
        Assert.Single((await winner.ReadLedgerAsync(default)).Trials);
    }

    /// <summary>Equivalent optimization publications are idempotent even with separately allocated permutation arrays.</summary>
    [Fact]
    public async Task TerminalPublication_ComparesCanonicalVariantEvidenceOnReplay()
    {
        using var fixture = new TemporaryFolder();
        var registry = Registry(fixture);
        var request = Start("outcome-replay", ResearchStartKind.CatalogStudy, [Variant("v")]);
        await registry.ReserveStartAsync(request, default);
        var variant = new ResearchVariantOutcome("v", "metric", 12m, Rule, Code);
        var initial = new ResearchExecutionResult("compute", "completed", Before, "summary", Rule,
            Publication: new("manifest", Code, [variant]));
        await registry.RecordExecutionResultAsync(request.StartId, initial, default);
        await registry.RecordExecutionResultAsync(request.StartId,
            initial with { Publication = new("manifest", Code, [variant with { }]) }, default);
        await Assert.ThrowsAsync<InvalidDataException>(() => registry.RecordExecutionResultAsync(request.StartId,
            initial with { Publication = new("manifest", Code, [variant with { Metric = "different" }]) }, default));
        Assert.Single(Assert.Single((await registry.ReadLedgerAsync(default)).Starts).Executions);
    }

    /// <summary>Publishes a deterministic synthetic passing result without market data or a holdout read.</summary>
    private static async Task<(CanonicalResearchResultManifest Result, string Artifact)> PublishStudyAsync(TemporaryFolder fixture,
        SqliteResearchTrialRegistry registry, FrozenResearchStart request)
    {
        CanonicalResearchResultManifest? result = null;
        var artifact = Path.Combine(fixture.Path, "study.json");
        await registry.RunDirectAsync(request, "compute", async (start, _) =>
        {
            var trial = Assert.Single(start.Binding.Trials);
            var report = Path.Combine(fixture.Path, "report.json");
            await File.WriteAllTextAsync(report, """{"decision":"ADVANCE_TO_HOLDOUT"}""");
            var reportHash = ResearchStartIdentity.HashFile(report);
            await File.WriteAllTextAsync(artifact, System.Text.Json.JsonSerializer.Serialize(new
            {
                variants = new[] { new { artifacts = new Dictionary<string, string> { ["report.json"] = reportHash } } }
            }));
            var hash = ResearchStartIdentity.HashFile(artifact);
            result = new(trial.ExperimentId + "-result", trial.ExperimentId, trial.DefinitionSha256, request.StartId, Code,
                "development", "passed", new Dictionary<string, decimal> { ["passed"] = 1m }, [hash, reportHash], start.RegisteredAtUtc);
            return new DirectResearchCompletion<string>(artifact, artifact, hash, [result]);
        }, (_, _) => throw new InvalidOperationException("The synthetic study was already published."), default);
        return (result!, artifact);
    }

    /// <summary>Uses a fresh caller per request against the same fixture-owned SQLite registry.</summary>
    private static SqliteResearchTrialRegistry Registry(TemporaryFolder fixture, ResearchAdmissionReviews? reviews = null) =>
        new(new ResearchTrialRegistryOptions(Path.Combine(fixture.Path, "registry.db"), ResearchTrialRegistryOpenMode.OpenOrCreate), reviews);

    /// <summary>Builds one complete immutable request used only by this test fixture.</summary>
    private static FrozenResearchStart Start(string id, ResearchStartKind kind, FrozenResearchVariant[] variants,
        IReadOnlyDictionary<string, string[]>? executions = null) => new(id, kind, ResearchStartIdentity.HashText(id), Code,
        variants, executions ?? new Dictionary<string, string[]> { ["compute"] = variants.Select(variant => variant.VariantKey).ToArray() });

    /// <summary>Declares a synthetic development/hypothesis and unopened holdout sufficient for registry behavior tests.</summary>
    private static FrozenResearchVariant Variant(string id) => new(id, "test-family", "Synthetic hypothesis", Rule,
        ["exact-dataset"], new Dictionary<string, string> { ["clock"] = "UTC" }, new Dictionary<string, string> { ["horizon"] = "1d" },
        new Dictionary<string, decimal> { ["n"] = 1m }, new Dictionary<string, string> { ["cost"] = "10 bps" },
        [Development(), new("holdout", Holdout, Holdout.AddYears(1), TimeSpan.Zero)],
        "passed", ["missing required evidence"]);

    /// <summary>Uses an exclusive development boundary shared with the protected test window.</summary>
    private static ResearchPartitionDefinition Development() => new("development", Before, Holdout, TimeSpan.Zero);
}
