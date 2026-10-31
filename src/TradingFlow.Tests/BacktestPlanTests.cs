using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using TradingFlow.Application.Candidates;
using TradingFlow.Application.Jobs;
using TradingFlow.Contracts.Transport;
using TradingFlow.Contracts.V1;
using TradingFlow.Data.Jobs;
using TradingFlow.Domain.Discovery;
using TradingFlow.Domain.Jobs;
using TradingFlow.Web;

namespace TradingFlow.Tests;

public sealed class BacktestPlanTests
{
    /// <summary>The server-selected execution assumption survives save, reopen and every independently generated cohort command.</summary>
    [Fact]
    public async Task SavedPlan_RetainsResolvedExecutionChoiceAcrossReopenAndStart()
    {
        await using var fixture = await Fixture.CreateAsync(4, 1);
        fixture.ResolvedReplayPolicy = TradingFlow.Domain.Backtesting.ExecutionReplayPolicy.IntradayRthFiveMinuteV1;
        var saved = await fixture.Service.SaveAsync(new(fixture.Request, 2));
        var reopened = (await fixture.Service.GetAsync(saved.PlanId))!;
        Assert.Equal(fixture.ResolvedReplayPolicy, reopened.Run.ExecutionReplayPolicyId);
        await fixture.Service.StartAsync(saved.PlanId, new("1.0", "resolved-policy"));
        Assert.All(fixture.Written, item => Assert.Equal(fixture.ResolvedReplayPolicy, item.Run.ExecutionReplayPolicyId));
    }

    /// <summary>All books share one live opaque scope through capture; command replay creates and disposes a fresh operation scope.</summary>
    [Fact]
    public async Task Start_RetainsOneVerificationScopeAcrossAllBooksAndReopensOnReplay()
    {
        await using var fixture = await Fixture.CreateAsync(4, 2);
        var saved = await fixture.Service.SaveAsync(new(fixture.Request, 2));
        Assert.Empty(fixture.VerificationScopes);
        var first = await fixture.Service.StartAsync(saved.PlanId, new("1.0", "scoped-start"));
        var scope = Assert.Single(fixture.VerificationScopes);
        Assert.True(scope.Disposed);
        Assert.Equal(1 + 3 * first.Jobs.Count, fixture.ScopeUses.Count);
        Assert.All(fixture.ScopeUses, used => Assert.Same(scope, used));
        await fixture.Service.StartAsync(saved.PlanId, new("1.0", "scoped-start"));
        Assert.Equal(2, fixture.VerificationScopes.Count);
        Assert.NotSame(scope, fixture.VerificationScopes[1]);
        Assert.True(fixture.VerificationScopes[1].Disposed);
    }

    /// <summary>Plan exclusions retain all source evidence while only the selected stocks receive independently funded jobs.</summary>
    [Fact]
    public async Task ExcludedStock_SaveReopenAndStartRetainSourceAndCreateTenSelectedBooks()
    {
        string[] symbols = ["ASTS", "BKSY", "BXDC", "CRCL", "CRDO", "DASH", "DIOD", "FPS", "MIAX", "MRVL",
            "MVST", "MXL", "NBIS", "NVTS", "OUST", "PATH", "PGY", "POET", "RDW", "RGTI", "RKLB", "SIVE", "SMCI", "SOXL", "STM"];
        await using var fixture = await Fixture.CreateAsync(25, 2, sourceSymbols: symbols);
        var request = new SaveBacktestPlanRequest(fixture.Request, 5, ExcludedSymbols: [" soxl ", "SOXL"]);
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var wire = System.Text.Json.JsonSerializer.Deserialize<SaveBacktestPlanRequest>(System.Text.Json.JsonSerializer.Serialize(request, options), options)!;
        var saved = await fixture.Service.SaveAsync(wire);
        var reopened = (await fixture.Service.GetAsync(saved.PlanId))!;
        Assert.Equal(new[] { "SOXL" }, reopened.ExcludedSymbols);
        Assert.Equal(10, reopened.ExecutionCount);
        Assert.Equal(new[] { 5, 5, 5, 5, 4 }, reopened.Cohorts.Select(cohort => cohort.Symbols.Count));
        Assert.Equal(symbols.Where(symbol => symbol != "SOXL").Order(), reopened.Cohorts.SelectMany(cohort => cohort.Symbols));
        Assert.Equal(fixture.Source.ContentSha256, reopened.Source.ContentSha256);
        Assert.Equal(symbols.Order(), reopened.Source.Members.Select(member => member.Symbol).Order());
        var persistedPreview = await fixture.Database.Workflows.GetAsync(fixture.Source.UniverseSnapshotId, default);
        var original = UniverseDiscoveryService.ReadPreview(persistedPreview!, fixture.Source.UniverseSnapshotId);
        Assert.Equal(25, original.Members.Count);
        Assert.Contains(original.Members, member => member.Symbol == "SOXL");
        Assert.Empty(await fixture.Jobs.GetAllJobsAsync(default));
        var started = await fixture.Service.StartAsync(saved.PlanId, new("1.0", "excluded-stock"));
        Assert.Equal(10, started.Jobs.Count);
        Assert.All(started.Jobs, job => {
            Assert.Equal(10000m, job.Cohort!.StartingCapital);
            Assert.DoesNotContain("SOXL", job.Cohort.Symbols);
        });
        Assert.All(fixture.Written, written => {
            Assert.Equal(10000m, written.Run.StartingCapital);
            Assert.DoesNotContain(written.Source.Members, member => member.Symbol == "SOXL");
        });
        await using var database = fixture.Database.CreateDbContext();
        Assert.Empty(await database.Wishlists.ToArrayAsync());
    }

    /// <summary>Invalid exclusions fail before publishing a plan or creating any child input or job.</summary>
    [Theory]
    [InlineData("UNKNOWN")]
    [InlineData(" ")]
    [InlineData("ALL")]
    public async Task InvalidExclusions_DoNotSaveOrAdmit(string choice)
    {
        await using var fixture = await Fixture.CreateAsync(2, 1);
        var excluded = choice == "ALL" ? fixture.Source.Members.Select(member => member.Symbol).ToArray() : new[] { choice };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveAsync(new(fixture.Request, 5, ExcludedSymbols: excluded)));
        Assert.Empty(await fixture.Service.ListAsync());
        Assert.Empty(fixture.Written);
        Assert.Empty(await fixture.Jobs.GetAllJobsAsync(default));
    }

    /// <summary>Manual groups must cover the explicitly selected membership and cannot reintroduce an excluded source stock.</summary>
    [Fact]
    public async Task ManualGroups_ValidateSelectedMembershipAfterExclusion()
    {
        await using var fixture = await Fixture.CreateAsync(3, 1);
        var symbols = fixture.Source.Members.Select(member => member.Symbol).Order().ToArray();
        var request = new SaveBacktestPlanRequest(fixture.Request, null,
            [new("Selected", symbols[..2], 10000m)], [symbols[2]]);
        var saved = await fixture.Service.SaveAsync(request);
        Assert.Equal(symbols[..2], Assert.Single(saved.Cohorts).Symbols);
        Assert.Equal(3, saved.Source.Members.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveAsync(request with {
            Cohorts = [new("Excluded", symbols, 10000m)] }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveAsync(request with {
            Cohorts = [new("Missing", [symbols[0]], 10000m)] }));
    }

    /// <summary>Preview and Save reject an unknown or changed exact artifact before persisting plans or generating execution files.</summary>
    [Fact]
    public async Task UnavailableStrategy_IsRejectedBeforePreviewOrSave()
    {
        await using var fixture = await Fixture.CreateAsync(5, 1);
        fixture.Identities.Setup(item => item.RequireBacktestStrategyAsync(fixture.Request.Strategies[0], It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The exact selected strategy is unavailable."));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PreviewAsync(new(fixture.Request, 5)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveAsync(new(fixture.Request, 5)));
        Assert.Empty(await fixture.Service.ListAsync());
        Assert.Empty(await fixture.Jobs.GetAllJobsAsync(default));
        Assert.Empty(fixture.Written);
    }

    /// <summary>Checks complete partition coverage and exact independent jobs through the same HTTP client used by Android.</summary>
    [Theory]
    [InlineData(50, 10, 4, 20)]
    [InlineData(30, 5, 2, 12)]
    [InlineData(25, 5, 2, 10)]
    [InlineData(11, 5, 2, 6)]
    public async Task SharedApi_SavesWithoutRunningAndAdmitsCompleteIndependentMatrix(int symbols, int size, int strategies, int expected)
    {
        await using var fixture = await Fixture.CreateAsync(symbols, strategies);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(fixture.Service);
        await using var app = builder.Build();
        app.MapGroup("/api/v1").MapBacktestPlanEndpoints();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient();
            var client = new TradingWorkflowClient(http, () => address);
            var saved = await client.SaveBacktestPlanAsync(new(fixture.Request, size));
            Assert.Equal(expected, saved.ExecutionCount);
            Assert.Equal(symbols, saved.Cohorts.Sum(cohort => cohort.Symbols.Count));
            Assert.Equal(symbols, saved.Cohorts.SelectMany(cohort => cohort.Symbols).Distinct().Count());
            Assert.Equal(fixture.Source.ContentSha256, saved.Source.ContentSha256);
            Assert.Empty(await fixture.Jobs.GetAllJobsAsync(default));
            Assert.Empty(fixture.Written);
            var started = await client.StartBacktestPlanAsync(saved.PlanId, new("1.0", "start-once"));
            Assert.Equal(expected, started.Jobs.Count);
            Assert.Equal(expected, started.Jobs.Select(job => job.JobId).Distinct().Count());
            Assert.All(started.Jobs, job => Assert.Single(job.Provenance!.Strategies));
            Assert.All(started.Jobs, job => Assert.Equal(fixture.Request.StartingCapital, job.Cohort!.StartingCapital));
            Assert.All(fixture.Written, item => Assert.Equal(fixture.Hash, item.Run.ConfigurationSha256));
            foreach (var write in fixture.Written)
                Assert.Equal(saved.Cohorts.Single(cohort => cohort.Symbols.SequenceEqual(write.Source.Members.Select(member => member.Symbol))).StartingCapital,
                    write.Run.StartingCapital);
            await using var context = fixture.Database.CreateDbContext();
            Assert.Empty(await context.Wishlists.ToArrayAsync());
            Assert.Empty(await context.Candidates.ToArrayAsync());
        }
        finally { await app.StopAsync(); }
    }

    /// <summary>Saved historical membership survives discovery expiry, while retries reuse queued/running/finished jobs.</summary>
    [Fact]
    public async Task SavedResearch_StartsAfterSourceExpiryAndReplaysEveryJobStatus()
    {
        await using var fixture = await Fixture.CreateAsync(10, 2);
        var saved = await fixture.Service.SaveAsync(new(fixture.Request, 5));
        fixture.Clock.Now = fixture.Clock.Now.AddDays(1);
        var command = new StartBacktestPlanRequest("1.0", "retry");
        var first = await fixture.Service.StartAsync(saved.PlanId, command);
        var lease = await fixture.Jobs.TryAcquireAsync(first.Jobs[0].JobId, "test-worker", fixture.Clock.Now, TimeSpan.FromMinutes(1), default);
        Assert.NotNull(lease);
        Assert.Equal(first.Jobs.Select(job => job.JobId), (await fixture.Service.StartAsync(saved.PlanId, command)).Jobs.Select(job => job.JobId));
        Assert.True(await fixture.Jobs.CompleteAsync(lease!.Job.Id, lease.LeaseToken, "completed", null, null, null, fixture.Clock.Now, default));
        Assert.Equal("completed", (await fixture.Service.StartAsync(saved.PlanId, command)).Jobs[0].Status);
        await fixture.Jobs.RequestCancellationAsync(first.Jobs[1].JobId, fixture.Clock.Now, default);
        Assert.Equal(first.Jobs.Count, (await fixture.Service.StartAsync(saved.PlanId, command)).Jobs.Count);
        Assert.Equal(first.Jobs.Count, fixture.Written.Count);
        Assert.Single(fixture.PreparedRequests);
        var reference = fixture.PreparedRequests[0];
        Assert.NotNull(reference);
        Assert.True(File.Exists(reference.Path));
        Assert.All(await fixture.Jobs.GetAllJobsAsync(default), job =>
            Assert.Equal(reference, TradingFlow.Application.Jobs.DurableFileJobRequest.Deserialize(job.RequestJson).PreparedMarketData));
    }

    /// <summary>A full queue rejects the entire comparison and leaves previously accepted work untouched.</summary>
    [Fact]
    public async Task InsufficientCapacity_AcceptsNoPartialMatrix()
    {
        await using var fixture = await Fixture.CreateAsync(10, 2, maximum: 4);
        var saved = await fixture.Service.SaveAsync(new(fixture.Request, 5));
        var existing = new PersistedJob { Id = Guid.NewGuid(), JobType = "backtest", RunName = "existing", ConfigPath = "fixture" };
        Assert.True(await fixture.Jobs.TryEnqueueAsync(existing, 4, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.StartAsync(saved.PlanId, new("1.0", "start")));
        Assert.Equal(existing.Id, Assert.Single(await fixture.Jobs.GetAllJobsAsync(default)).Id);
        Assert.Empty(fixture.ExistingInputs);
        Assert.NotEmpty(fixture.PreparedRequests);
        Assert.All(fixture.PreparedRequests, reference => Assert.False(File.Exists(reference.Path)));
    }

    /// <summary>Saving cannot silently accept a different profile after preview; a fresh preview freezes the newly reviewed hash.</summary>
    [Fact]
    public async Task SavingReviewedPreview_RejectsChangedProfileUntilPreviewedAgain()
    {
        await using var fixture = await Fixture.CreateAsync(5, 1);
        var preview = await fixture.Service.PreviewAsync(new(fixture.Request, 5));
        Assert.Equal(fixture.Hash, preview.Run.ConfigurationSha256);
        fixture.Hash = new('c', 64);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveAsync(new(preview.Run, 5)));
        Assert.Contains("profile changed", error.Message);
        Assert.Empty(await fixture.Service.ListAsync());
        Assert.Empty(fixture.Written);
        Assert.Empty(await fixture.Jobs.GetAllJobsAsync(default));
        var reviewed = await fixture.Service.PreviewAsync(new(preview.Run with { ConfigurationSha256 = null }, 5));
        var saved = await fixture.Service.SaveAsync(new(reviewed.Run, 5));
        Assert.Equal(fixture.Hash, saved.ConfigurationSha256);
        Assert.Equal(fixture.Hash, saved.Run.ConfigurationSha256);
    }

    /// <summary>Manual groups retain their explicit balances and reject duplicate/outside-source symbols.</summary>
    [Fact]
    public async Task ManualGroups_KeepSeparateCapitalAndRejectInvalidMembership()
    {
        await using var fixture = await Fixture.CreateAsync(4, 1);
        var symbols = fixture.Source.Members.Select(member => member.Symbol).Order().ToArray();
        var groups = new[] { new BacktestCohort("First", symbols[..2], 12000), new BacktestCohort("Second", symbols[2..], 18000) };
        var saved = await fixture.Service.SaveAsync(new(fixture.Request, null, groups));
        await fixture.Service.StartAsync(saved.PlanId, new("1.0", "manual"));
        Assert.Equal(new[] { 12000m, 18000m }, fixture.Written.Select(write => write.Run.StartingCapital).ToArray());
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveAsync(new(fixture.Request, null,
            [new("Duplicate", [symbols[0], symbols[0]], 10000)])));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveAsync(new(fixture.Request, null,
            [new("Outside", ["OUTSIDE"], 10000)])));
    }

    /// <summary>Unrelated event IDs cannot remove saved plans from the picker.</summary>
    [Fact]
    public async Task Listing_DoesNotTreatGlobalEventSequencesAsPlanCounts()
    {
        await using var fixture = await Fixture.CreateAsync(5, 1);
        var first = await fixture.Service.SaveAsync(new(fixture.Request, 5));
        await using (var context = fixture.Database.CreateDbContext())
        {
            context.ApplicationEvents.AddRange(Enumerable.Range(0, 510).Select(index => new TradingFlow.Domain.Persistence.ApplicationEventRecord {
                StreamName = "unrelated-fixture", EventType = "fixture", OccurredAtUtc = fixture.Clock.Now, PayloadJson = "{}" }));
            await context.SaveChangesAsync();
        }
        var second = await fixture.Service.SaveAsync(new(fixture.Request, 5));
        Assert.Equal(new[] { second.PlanId, first.PlanId }, (await fixture.Service.ListAsync()).Select(plan => plan.PlanId));
    }

    /// <summary>Saved evidence and cost facts survive event-store reopen and job projection without consulting a newer profile.</summary>
    [Fact]
    public async Task EvidenceAndCosts_SaveReopenAndJobDetailsRetainReviewedFacts()
    {
        await using var fixture = await Fixture.CreateAsync(2, 1);
        fixture.Evidence = SyntheticEvidence();
        fixture.Settings = new("fixture.yaml", fixture.Hash, 0.5m, 0.6m, 0.00001m, 0.0001m, 5m, 5m, 2m,
            null, null, [new(fixture.Request.Strategies[0], "Synthetic recipe", 10m, null, new("synthetic", "1.0.0", "synthetic", "synthetic"))]);
        var request = fixture.Request with { DecisionContextEvidence = fixture.Evidence.Reference };
        var saved = await fixture.Service.SaveAsync(new(request, 2));
        fixture.Settings = fixture.Settings with { FixedBuyFee = 999m };
        var reopened = (await fixture.Service.GetAsync(saved.PlanId))!;
        Assert.Equal(0.5m, reopened.ResolvedSettings!.FixedBuyFee);
        Assert.Equal(fixture.Evidence.DependencyGraphSha256, reopened.EvidenceSummary!.DependencyGraphSha256);
        Assert.Equal(request.DecisionContextEvidence, reopened.Run.DecisionContextEvidence);
        var started = await fixture.Service.StartAsync(saved.PlanId, new("1.0", "evidence-costs"));
        var job = Assert.Single(started.Jobs);
        Assert.Equal(fixture.Evidence.DependencyGraphSha256, job.EvidenceSummary!.DependencyGraphSha256);
        var persisted = (await fixture.Jobs.GetJobAsync(job.JobId, default))!;
        var durable = DurableFileJobRequest.Deserialize(persisted.RequestJson);
        Assert.Equal(fixture.Evidence.Reference, durable.Workflow!.EvidenceSummary!.Reference);
        Assert.Equal(fixture.Evidence.DependencyGraphSha256, RunWorkflowService.Project(persisted).EvidenceSummary!.DependencyGraphSha256);
    }

    /// <summary>A graph change fails the complete saved plan before creating child files or accepting even one job.</summary>
    [Fact]
    public async Task ChangedEvidenceGraph_RejectsSavedPlanBeforeAnyChildAdmission()
    {
        await using var fixture = await Fixture.CreateAsync(4, 1);
        fixture.Evidence = SyntheticEvidence();
        var plan = await fixture.Service.SaveAsync(new(fixture.Request with { DecisionContextEvidence = fixture.Evidence.Reference }, 2));
        fixture.Evidence = fixture.Evidence with { DependencyGraphSha256 = new('f', 64) };
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.StartAsync(plan.PlanId, new("1.0", "changed-evidence")));
        Assert.Empty(fixture.Written);
        Assert.Empty(await fixture.Jobs.GetAllJobsAsync(default));
    }

    /// <summary>Provides explicitly synthetic transport facts without representing a real classifier evaluation or provider observation.</summary>
    private static DecisionContextEvidenceSummary SyntheticEvidence() => new(new("synthetic/profile.yaml", new('d', 64)),
        "synthetic-test", "1", ["synthetic-taxonomy"], ["synthetic-classifier"], [new string('e', 64)],
        ["synthetic actual-receipt test clocks"], ["company", "sector", "global"], 5, 24, [], new('c', 64));

    private sealed class Fixture : IAsyncDisposable
    {
        public CandidateWorkflowTestDatabase Database { get; } = new();
        public CandidateWorkflowTestClock Clock { get; } = new(CandidateAuditIntegrityTests.Now);
        public string Hash { get; set; } = new('a', 64);
        public string? ResolvedReplayPolicy { get; set; }
        public DecisionContextEvidenceSummary? Evidence { get; set; }
        public BacktestResolvedSettingsResponse? Settings { get; set; }
        public Mock<IStrategyIdentityResolver> Identities { get; } = new();
        public List<(StartBacktestRequest Run, UniversePreviewResponse Source)> Written { get; } = [];
        public List<TradingFlow.Domain.Backtesting.PreparedMarketDataRequestReference> PreparedRequests { get; } = [];
        public List<VerificationScope> VerificationScopes { get; } = [];
        public List<IWorkflowEvidenceVerificationScope> ScopeUses { get; } = [];
        public string[] ExistingInputs => Directory.GetFiles(root);
        public SqliteJobRepository Jobs { get; private set; } = null!;
        public BacktestPlanService Service { get; private set; } = null!;
        public StartBacktestRequest Request { get; private set; } = null!;
        public UniversePreviewResponse Source { get; private set; } = null!;
        private readonly string root = Path.Combine(Path.GetTempPath(), "tradingflow-plan-test-" + Guid.NewGuid().ToString("N"));

        /// <summary>Creates isolated persistence and immutable configuration/comparison evidence without provider collection.</summary>
        public static async Task<Fixture> CreateAsync(int symbols, int strategies, int maximum = 32, IReadOnlyList<string>? sourceSymbols = null)
        {
            var fixture = new Fixture();
            Directory.CreateDirectory(fixture.root);
            await fixture.Database.InitializeAsync();
            var source = new Mock<IUniverseSourceResolver>();
            source.Setup(item => item.ResolveAsync(It.IsAny<UniverseSourceSelectionRequest>(), "swing", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new UniverseSourceCapture("wishlist", "fixture", fixture.Clock.Now, fixture.Clock.Now.AddMinutes(3),
                    (sourceSymbols ?? Enumerable.Range(0, symbols).Select(index => "S" + index.ToString("D3")).ToArray())
                        .Select(symbol => new DiscoverySymbolObservation(symbol)).ToArray()));
            var discovery = new UniverseDiscoveryService(source.Object, fixture.Database.Discovery, fixture.Database.Workflows, fixture.Database.Workflows, fixture.Clock);
            fixture.Source = await discovery.PreviewAsync(new("1.0", "swing", null, [new("wishlist", "fixture")]));
            fixture.Request = new("1.0", "cohorts", fixture.Source.UniverseSnapshotId,
                Enumerable.Range(0, strategies).Select(index => new StrategyReference("strategy-" + index, "1.0.0", new string('b', 64))).ToArray(),
                fixture.Clock.Now.AddMonths(-6), fixture.Clock.Now.AddDays(-1), 10000, 1, 25, 5, "use_cache", "full", "saved", "fixture.yaml");
            var configuration = new Mock<IWorkflowRunConfiguration>(MockBehavior.Strict);
            // Retain fixture commands unchanged unless a test explicitly supplies a resolved execution choice.
            configuration.Setup(item => item.NormalizeBacktestRequestAsync(It.IsAny<StartBacktestRequest>(), It.IsAny<CancellationToken>()))
                .Returns<StartBacktestRequest, CancellationToken>((request, _) => Task.FromResult(
                    request with { ExecutionReplayPolicyId = fixture.ResolvedReplayPolicy ?? request.ExecutionReplayPolicyId }));
            configuration.Setup(item => item.OpenEvidenceVerificationScopeAsync(It.IsAny<StartBacktestRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => {
                    var scope = new VerificationScope();
                    fixture.VerificationScopes.Add(scope);
                    return (IWorkflowEvidenceVerificationScope?)scope;
                });
            configuration.Setup(item => item.GetDecisionContextEvidenceAsync(It.IsAny<StartBacktestRequest>(), It.IsAny<CancellationToken>(), It.IsAny<IWorkflowEvidenceVerificationScope?>()))
                .Returns<StartBacktestRequest, CancellationToken, IWorkflowEvidenceVerificationScope?>((_, _, scope) => {
                    fixture.RecordScope(scope);
                    return Task.FromResult(fixture.Evidence);
                });
            configuration.Setup(item => item.GetBacktestResolvedSettingsAsync(It.IsAny<StartBacktestRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => fixture.Settings);
            configuration.Setup(item => item.ValidateCapturedBacktestInputsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>(), It.IsAny<IWorkflowEvidenceVerificationScope?>()))
                .Returns<string, IReadOnlyList<string>, CancellationToken, IWorkflowEvidenceVerificationScope?>((_, _, _, scope) => {
                    fixture.RecordScope(scope);
                    return Task.CompletedTask;
                });
            configuration.Setup(item => item.DiscardUnacceptedAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
                .Returns<string, IReadOnlyList<string>, CancellationToken>((_, paths, _) => {
                    foreach (var path in paths)
                    {
                        if (Path.GetDirectoryName(Path.GetFullPath(path)) != fixture.root)
                            throw new InvalidOperationException("Fixture cleanup escaped its owned root.");
                        File.Delete(path);
                    }
                    return Task.CompletedTask;
                });
            configuration.Setup(item => item.GetBacktestConfigurationHashAsync("fixture.yaml", It.IsAny<CancellationToken>())).ReturnsAsync(() => fixture.Hash);
            configuration.Setup(item => item.WriteAsync(It.IsAny<StartBacktestRequest>(), It.IsAny<UniversePreviewResponse>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IWorkflowEvidenceVerificationScope?>()))
                .Returns<StartBacktestRequest, UniversePreviewResponse, string, CancellationToken, IWorkflowEvidenceVerificationScope?>(async (run, universe, name, token, scope) => {
                    fixture.RecordScope(scope);
                    fixture.Written.Add((run, universe));
                    var path = Path.Combine(fixture.root, name + ".yaml");
                    await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(run), token);
                    return new[] { path };
                });
            configuration.Setup(item => item.WritePreparedComparisonRequestAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<PersistedJob>>(),
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .Returns<string, IReadOnlyList<PersistedJob>, DateTimeOffset, CancellationToken>(async (comparison, books, cutoff, token) => {
                    var reference = await PreparedWorkflowTestRequest.WriteAsync(fixture.root, comparison, books, cutoff, fixture.Source, token);
                    fixture.PreparedRequests.Add(reference!);
                    return reference;
                });
            fixture.Jobs = new(fixture.Database);
            var limits = new RunQueueLimits(maximum, 16);
            var workflow = new RunWorkflowService(fixture.Jobs, fixture.Database.Workflows, fixture.Identities.Object, configuration.Object, fixture.Clock, limits);
            fixture.Service = new(fixture.Database.Workflows, fixture.Database.Workflows, fixture.Jobs, workflow, configuration.Object, limits, fixture.Clock);
            return fixture;
        }

        /// <summary>Records only active operation tokens; preview/save use their ordinary no-token configuration path.</summary>
        private void RecordScope(IWorkflowEvidenceVerificationScope? scope)
        {
            if (scope is null) return;
            Assert.False(Assert.IsType<VerificationScope>(scope).Disposed);
            ScopeUses.Add(scope);
        }

        /// <summary>Deletes every disposable input and database created by this test fixture.</summary>
        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar))
                throw new InvalidOperationException("Fixture cleanup escaped the temporary directory.");
            Directory.Delete(root, true);
        }
    }

    private sealed class VerificationScope : IWorkflowEvidenceVerificationScope
    {
        public bool Disposed { get; private set; }
        /// <summary>Records application ownership ending without supplying any provider or verification bypass.</summary>
        public void Dispose() => Disposed = true;
    }
}
