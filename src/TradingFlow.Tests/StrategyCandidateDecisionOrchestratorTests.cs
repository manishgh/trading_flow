using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using TradingFlow.Data.Context;
using TradingFlow.Data.Orders;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Tests;

public sealed class StrategyCandidateDecisionOrchestratorTests
{
    [Fact]
    public async Task EvaluateAsync_PersistsCompleteTriggerPathBeforeReturningTrigger()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteCandidateRepository(database.Factory);
        var orchestrator = new StrategyCandidateDecisionOrchestrator(
            new StrategyDecisionKernel(),
            repository);
        var original = StrategyDecisionKernelTests.CreateRequest();
        var request = original with
        {
            Candidate = original.Candidate with
            {
                State = StrategyCandidateState.Discovered,
                Version = 0,
                Discovery = original.Candidate.Discovery with
                {
                    IsPersisted = false,
                    Sources = ["finviz:large-cap-momentum"]
                }
            }
        };
        var run = CreateRun(request.AsOfUtc.AddMinutes(-15));

        var persisted = await orchestrator.EvaluateAsync(run, request);

        Assert.True(persisted.IsNewTrigger);
        Assert.Equal(StrategyCandidateState.Triggered, persisted.Candidate.State);
        Assert.Equal(persisted.Decision!.SemanticDecisionSha256, persisted.Candidate.SemanticDecisionSha256);
        var transitions = await repository.GetTransitionsAsync(persisted.Candidate.CandidateId);
        Assert.Collection(
            transitions,
            item => Assert.Equal(StrategyCandidateState.DataWarming, item.NewState),
            item => Assert.Equal(StrategyCandidateState.Qualified, item.NewState),
            item => Assert.Equal(StrategyCandidateState.Armed, item.NewState),
            item => Assert.Equal(StrategyCandidateState.Triggered, item.NewState));
        Assert.All(transitions, item => Assert.Equal("strategy_decision_kernel", item.Source));

        var duplicate = await orchestrator.EvaluateAsync(run, request);
        Assert.True(duplicate.WasAlreadyTerminal);
        Assert.Null(duplicate.Decision);
        Assert.Equal(transitions.Count, (await repository.GetTransitionsAsync(persisted.Candidate.CandidateId)).Count);
    }

    [Fact]
    public async Task EvaluateAsync_InsufficientWarmupCannotReachTriggered()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteCandidateRepository(database.Factory);
        var orchestrator = new StrategyCandidateDecisionOrchestrator(
            new StrategyDecisionKernel(),
            repository);
        var original = StrategyDecisionKernelTests.CreateRequest();
        var request = original with
        {
            Candidate = original.Candidate with
            {
                State = StrategyCandidateState.Discovered,
                Version = 0,
                Discovery = original.Candidate.Discovery with { IsPersisted = false },
                MarketState = original.Candidate.MarketState with { IsWarm = false }
            }
        };

        var persisted = await orchestrator.EvaluateAsync(
            CreateRun(request.AsOfUtc.AddMinutes(-15)),
            request);

        Assert.False(persisted.IsNewTrigger);
        Assert.Equal(StrategyCandidateState.DataWarming, persisted.Candidate.State);
        Assert.Equal("market_state_not_warm", persisted.Decision!.NoEntryReason);
        Assert.DoesNotContain(
            await repository.GetTransitionsAsync(persisted.Candidate.CandidateId),
            item => item.NewState == StrategyCandidateState.Triggered);
    }

    [Fact]
    public async Task EvaluateAsync_BacktestJournalRequiresRealPersistenceBeforeKernelEvaluation()
    {
        var repository = new BacktestCandidateJournal();
        var orchestrator = new StrategyCandidateDecisionOrchestrator(
            new StrategyDecisionKernel(),
            repository);
        var original = StrategyDecisionKernelTests.CreateRequest();
        var run = BacktestCandidateJournal.CreateRun(
            new { Name = "journal-contract" },
            "journal-contract:RGTI",
            original.AsOfUtc.AddMinutes(-15));
        var request = original with
        {
            Candidate = original.Candidate with
            {
                CandidateId = StrategyDecisionRequestAssembler.CreateDeterministicId(new
                {
                    run.RunId,
                    original.Candidate.Identity
                }),
                State = StrategyCandidateState.Discovered,
                Version = 0,
                Discovery = original.Candidate.Discovery with
                {
                    IsPersisted = false,
                    Sources = ["backtest_candidate_journal"]
                }
            }
        };

        Assert.Null(await repository.GetAsync(request.Candidate.CandidateId));

        var result = await orchestrator.EvaluateAsync(run, request);

        Assert.True(result.IsNewTrigger);
        Assert.NotNull(await repository.GetAsync(request.Candidate.CandidateId));
        Assert.Collection(
            await repository.GetTransitionsAsync(request.Candidate.CandidateId),
            item => Assert.Equal(StrategyCandidateState.DataWarming, item.NewState),
            item => Assert.Equal(StrategyCandidateState.Qualified, item.NewState),
            item => Assert.Equal(StrategyCandidateState.Armed, item.NewState),
            item => Assert.Equal(StrategyCandidateState.Triggered, item.NewState));
    }

    [Fact]
    public async Task EvaluateAsync_BacktestJournalFlushesCrashRecoverableDecisionRecords()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "trading-flow-candidate-journal-tests",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "candidate-decisions.ndjson");
        try
        {
            var original = StrategyDecisionKernelTests.CreateRequest();
            var run = BacktestCandidateJournal.CreateRun(
                new { Name = "durable-journal" },
                "durable-journal:RGTI",
                original.AsOfUtc.AddMinutes(-15));
            var request = original with
            {
                Candidate = original.Candidate with
                {
                    CandidateId = StrategyDecisionRequestAssembler.CreateDeterministicId(new
                    {
                        run.RunId,
                        original.Candidate.Identity
                    }),
                    State = StrategyCandidateState.Discovered,
                    Version = 0,
                    Discovery = original.Candidate.Discovery with { IsPersisted = false }
                }
            };

            using (var repository = new BacktestCandidateJournal(path))
            {
                var result = await new StrategyCandidateDecisionOrchestrator(
                    new StrategyDecisionKernel(),
                    repository).EvaluateAsync(run, request);
                Assert.True(result.IsNewTrigger);
            }

            var records = File.ReadLines(path)
                .Select(line => JsonSerializer.Deserialize<BacktestCandidateJournalEntry>(line))
                .ToArray();
            Assert.Equal(2, records.Length);
            Assert.Equal("discovered", records[0]!.RecordType);
            Assert.Equal("decision", records[1]!.RecordType);
            Assert.Contains(
                records[1]!.Transitions,
                transition => transition.NewState == StrategyCandidateState.Triggered);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("long_regular_no_catalyst")]
    [InlineData("long_regular_catalyst")]
    [InlineData("short_research")]
    [InlineData("long_extended")]
    public async Task EvaluateAsync_BacktestAndPaperAdaptersPersistTheSameCanonicalDecision(
        string scenario)
    {
        await using var database = await TestDatabase.CreateAsync();
        var backtestRepository = new BacktestCandidateJournal();
        var paperRepository = new SqliteCandidateRepository(database.Factory);
        var original = StrategyDecisionKernelTests.CreateParityScenario(scenario);
        var backtestRuntime = new AuthorizedRuntimeStrategy(
            original.StrategyIdentity,
            StrategySelectionMode.Backtest,
            original.Strategy,
            original.AdmissionProfile);
        var paperRuntime = new AuthorizedRuntimeStrategy(
            original.StrategyIdentity,
            StrategySelectionMode.RunPaperShadow,
            original.Strategy,
            original.AdmissionProfile);
        var setupKey = StrategyDecisionRequestAssembler.CreateSetupKey(
            original.Strategy.StrategyId,
            original.Strategy.Timeframe,
            original.SnapshotsByTimeframe[original.Strategy.Timeframe][^1].Timestamp);
        var backtestRun = BacktestCandidateJournal.CreateRun(
            new { Name = "adapter-parity" },
            "adapter-parity:RGTI",
            original.AsOfUtc.AddMinutes(-15));
        var paperRun = CreateRun(original.AsOfUtc.AddMinutes(-15));
        var backtestRequest = CreateAdapterRequest(
            backtestRuntime,
            backtestRun.RunId,
            StrategyDecisionRequestAssembler.CreateBacktestDiscovery(
                original.Candidate.Identity.Symbol,
                setupKey,
                original.Candidate.Identity.DiscoveryWindowStartUtc,
                original.Candidate.Identity.DiscoveryWindowEndUtc));
        var paperRequest = CreateAdapterRequest(
            paperRuntime,
            paperRun.RunId,
            StrategyDecisionRequestAssembler.CreateConfiguredLiveDiscovery(
                paperRun.RunId,
                original.Candidate.Identity.Symbol,
                original.Candidate.Identity.DiscoveryWindowStartUtc,
                original.Candidate.Identity.DiscoveryWindowEndUtc));

        var backtest = await new StrategyCandidateDecisionOrchestrator(
            new StrategyDecisionKernel(),
            backtestRepository).EvaluateAsync(backtestRun, backtestRequest);
        var paper = await new StrategyCandidateDecisionOrchestrator(
            new StrategyDecisionKernel(),
            paperRepository).EvaluateAsync(paperRun, paperRequest);

        Assert.Equal(backtest.Candidate.State, paper.Candidate.State);
        Assert.Equal(backtest.Decision!.SemanticDecisionSha256, paper.Decision!.SemanticDecisionSha256);
        Assert.Equal(backtest.Decision.OrderPlan, paper.Decision.OrderPlan);
        Assert.Equal(backtest.Decision.Direction, paper.Decision.Direction);
        Assert.Equal(backtest.Decision.NoEntryReason, paper.Decision.NoEntryReason);
        Assert.Equal(
            backtest.Decision.Rules.Select(rule => (rule.Stage, rule.Code, rule.Passed)),
            paper.Decision.Rules.Select(rule => (rule.Stage, rule.Code, rule.Passed)));
        var backtestTransitions = backtestRepository.Snapshot().Single().Transitions;
        var paperTransitions = await paperRepository.GetTransitionsAsync(paper.Candidate.CandidateId);
        Assert.Equal(
            backtestTransitions.Select(item => (item.PreviousState, item.NewState, item.ReasonCode)),
            paperTransitions.Select(item => (item.PreviousState, item.NewState, item.ReasonCode)));

        if (scenario == "long_regular_no_catalyst")
        {
            Assert.Equal(StrategyCandidateState.Triggered, backtest.Candidate.State);
            Assert.NotNull(backtest.Decision.OrderPlan);
        }

        StrategyDecisionRequest CreateAdapterRequest(
            AuthorizedRuntimeStrategy runtime,
            Guid runId,
            StrategyDiscoveryEvidence discovery) => StrategyDecisionRequestAssembler.Create(
                runtime,
                original.Candidate.Identity.Symbol,
                original.BarsByTimeframe.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<TradingFlow.Domain.Market.OhlcvBar>)pair.Value,
                    StringComparer.OrdinalIgnoreCase),
                original.SnapshotsByTimeframe.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<TradingFlow.Domain.Market.IndicatorSnapshot>)pair.Value,
                    StringComparer.OrdinalIgnoreCase),
                original.Catalysts.Select(item => item.Event).ToArray(),
                original.AsOfUtc,
                discovery,
                original.Candidate.Universe,
                original.Candidate.Regime,
                StrategyCandidateState.Discovered,
                0,
                setupKey,
                original.Candidate.Identity.DiscoveryWindowStartUtc,
                original.Candidate.Identity.DiscoveryWindowEndUtc,
                2,
                runId);
    }

    [Fact]
    public async Task EvaluateAsync_ArmedCandidateAcceptsNewDecisionEvidenceWithoutMutatingDiscovery()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteCandidateRepository(database.Factory);
        var orchestrator = new StrategyCandidateDecisionOrchestrator(
            new StrategyDecisionKernel(),
            repository);
        var original = StrategyDecisionKernelTests.CreateRequest();
        var strategy = original.Strategy with
        {
            Execution = original.Strategy.Execution with
            {
                Confirmation = new ExecutionConfirmationRules(
                    true,
                    2,
                    ExecutionConfirmationRules.CloseAboveSetupClose,
                    ExecutionConfirmationRules.NoFilter,
                    ExecutionConfirmationRules.NoFilter)
            }
        };
        var identity = new StrategyArtifactIdentity(
            strategy.StrategyId,
            original.StrategyIdentity.SemanticVersion,
            EvidenceCanonicalJson.ComputeSha256(new CanonicalStrategyDocument(
                strategy.StrategyId,
                original.StrategyIdentity.SemanticVersion,
                strategy,
                original.AdmissionProfile)));
        var request = original with
        {
            Strategy = strategy,
            StrategyIdentity = identity,
            Candidate = original.Candidate with
            {
                State = StrategyCandidateState.Discovered,
                Version = 0,
                Identity = original.Candidate.Identity with
                {
                    StrategyContentSha256 = identity.ContentSha256
                },
                Discovery = original.Candidate.Discovery with { IsPersisted = false }
            }
        };
        var run = CreateRun(request.AsOfUtc.AddMinutes(-15));

        var first = await orchestrator.EvaluateAsync(run, request);
        Assert.Equal(StrategyCandidateState.Armed, first.Candidate.State);

        var changedEvidence = request with
        {
            Candidate = request.Candidate with
            {
                MarketState = request.Candidate.MarketState with
                {
                    StateFingerprint = "next-completed-market-state"
                },
                Universe = request.Candidate.Universe with
                {
                    EvidenceId = "universe-next-version",
                    EvidenceJson = "{\"eligible\":true,\"version\":2}"
                },
                Regime = request.Candidate.Regime with
                {
                    EvidenceId = "regime-next-version",
                    EvidenceJson = "{\"eligible\":true,\"version\":2}"
                }
            }
        };

        var second = await orchestrator.EvaluateAsync(run, changedEvidence);

        Assert.False(second.WasAlreadyTerminal);
        Assert.Equal(StrategyCandidateState.Armed, second.Candidate.State);
        Assert.Equal(first.Candidate.SetupScoresJson, second.Candidate.SetupScoresJson);
        Assert.Equal(first.Candidate.Version + 1, second.Candidate.Version);
        Assert.Equal(
            StrategyCandidateState.Armed,
            (await repository.GetTransitionsAsync(second.Candidate.CandidateId))[^1].NewState);
    }

    private static ProductionRun CreateRun(DateTimeOffset startedAtUtc) => new()
    {
        RunId = Guid.NewGuid(),
        SchemaVersion = 1,
        ConfigHash = new string('a', 64),
        CodeVersion = new string('b', 40),
        Profile = "paper",
        Status = "running",
        StartedAtUtc = startedAtUtc
    };

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private TestDatabase(
            SqliteConnection connection,
            IDbContextFactory<TradingFlowDbContext> factory)
        {
            this.connection = connection;
            Factory = factory;
        }

        public IDbContextFactory<TradingFlowDbContext> Factory { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<TradingFlowDbContext>()
                .UseSqlite(connection)
                .Options;
            await using var context = new TradingFlowDbContext(options);
            await context.Database.EnsureCreatedAsync();
            return new TestDatabase(connection, new TestDbContextFactory(options));
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();

        private sealed class TestDbContextFactory(DbContextOptions<TradingFlowDbContext> options)
            : IDbContextFactory<TradingFlowDbContext>
        {
            public TradingFlowDbContext CreateDbContext() => new(options);

            public Task<TradingFlowDbContext> CreateDbContextAsync(
                CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
        }
    }
}
