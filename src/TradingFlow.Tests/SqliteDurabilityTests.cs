using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Data.Orders;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Tests;

public sealed class SqliteDurabilityTests
{
    private static readonly DateTimeOffset CandidateReservationTime =
        new(2026, 7, 21, 14, 30, 30, TimeSpan.Zero);

    [Fact]
    public async Task InitializeAsync_FileDatabase_EnforcesWalAndConnectionDurability()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);

        await using var context = new TradingFlowDbContext(options);
        await new TradingFlowDatabaseInitializer().InitializeAsync(context);
        await context.Database.OpenConnectionAsync();

        var connection = (SqliteConnection)context.Database.GetDbConnection();
        Assert.Equal("wal", await ScalarStringAsync(connection, "PRAGMA journal_mode;"));
        Assert.Equal(2, await ScalarLongAsync(connection, "PRAGMA synchronous;"));
        Assert.Equal(1, await ScalarLongAsync(connection, "PRAGMA foreign_keys;"));
        Assert.Equal(5_000, await ScalarLongAsync(connection, "PRAGMA busy_timeout;"));
    }

    [Fact]
    public async Task ReserveAsync_RepeatedLogicalIntent_ReusesPersistedClientOrderId()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var repository = new SqliteOrderIntentRepository(new TestDbContextFactory(options));
        var run = CreateRun(Guid.NewGuid());
        var reservation = CreateReservation(Guid.NewGuid(), "MSFT");

        await using (var context = new TradingFlowDbContext(options))
        {
            await new TradingFlowDatabaseInitializer().InitializeAsync(context);
        }

        var first = await repository.ReserveAsync(run, reservation);
        var replay = await repository.ReserveAsync(run, reservation);

        Assert.True(first.Created);
        Assert.False(replay.Created);
        Assert.Equal(first.Intent.ClientOrderId, replay.Intent.ClientOrderId);
        Assert.Equal(1, replay.Intent.SequenceNumber);
        await using var verification = new TradingFlowDbContext(options);
        Assert.Equal(1, await verification.OrderIntents.CountAsync());
    }

    [Fact]
    public async Task ReserveAsync_ParallelDistinctIntents_AllocatesDistinctMonotonicSequences()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var repository = new SqliteOrderIntentRepository(new TestDbContextFactory(options));
        var run = CreateRun(Guid.NewGuid());

        await using (var context = new TradingFlowDbContext(options))
        {
            await new TradingFlowDatabaseInitializer().InitializeAsync(context);
        }

        var reservations = Enumerable.Range(0, 8)
            .Select(_ => CreateReservation(Guid.NewGuid(), "MSFT"))
            .ToArray();
        var reserved = await Task.WhenAll(
            reservations.Select(reservation => repository.ReserveAsync(run, reservation)));

        Assert.Equal(Enumerable.Range(1, 8), reserved.Select(result => result.Intent.SequenceNumber).Order());
        Assert.Equal(8, reserved.Select(result => result.Intent.ClientOrderId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task ReserveAsync_ParallelRepositoryInstances_AllocateDistinctMonotonicSequences()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var contextFactory = new TestDbContextFactory(options);
        var repositories = Enumerable.Range(0, 4)
            .Select(_ => new SqliteOrderIntentRepository(contextFactory))
            .ToArray();
        var run = CreateRun(Guid.NewGuid());

        await using (var context = new TradingFlowDbContext(options))
        {
            await new TradingFlowDatabaseInitializer().InitializeAsync(context);
        }

        var reservations = Enumerable.Range(0, 8)
            .Select(_ => CreateReservation(Guid.NewGuid(), "MSFT"))
            .ToArray();
        var reserved = await Task.WhenAll(reservations.Select((reservation, index) =>
            repositories[index % repositories.Length].ReserveAsync(run, reservation)));

        Assert.Equal(Enumerable.Range(1, 8), reserved.Select(result => result.Intent.SequenceNumber).Order());
        Assert.Equal(8, reserved.Select(result => result.Intent.ClientOrderId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task ReserveAsync_TriggeredCandidate_ConsumesCandidateAndCreatesIntentAtomically()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var repository = CreateCandidateRepository(new TestDbContextFactory(options));
        var run = CreateRun(Guid.NewGuid());
        var candidateId = Guid.NewGuid();
        var semanticHash = new string('c', 64);
        var reservation = CreateCandidateReservation(
            Guid.NewGuid(),
            candidateId,
            "MSFT",
            semanticHash);
        await SeedTriggeredCandidateAsync(options, run, candidateId, "MSFT", semanticHash);

        var result = await repository.ReserveAsync(run, reservation);
        var intent = result.Intent;

        await using var verification = new TradingFlowDbContext(options);
        var candidate = await verification.Candidates.AsNoTracking().SingleAsync();
        var transition = await verification.CandidateTransitions
            .AsNoTracking()
            .OrderBy(item => item.Sequence)
            .LastAsync();
        var orderEvent = await verification.OrderEvents.AsNoTracking().SingleAsync();
        Assert.Equal(candidateId, intent.CandidateId);
        Assert.Equal(StrategyCandidateState.Consumed, candidate.State);
        Assert.Equal(5, candidate.Version);
        Assert.Equal(StrategyCandidateState.Triggered, transition.PreviousState);
        Assert.Equal(StrategyCandidateState.Consumed, transition.NewState);
        Assert.Equal("candidate_consumed_by_order_intent", transition.ReasonCode);
        Assert.Equal(4, intent.CandidateTriggeredVersion);
        Assert.Equal(5, intent.CandidateConsumedVersion);
        Assert.Equal(semanticHash, intent.CandidateSemanticDecisionSha256);
        Assert.NotNull(intent.CandidateTriggeredEvidenceSha256);
        Assert.NotNull(intent.CandidateConsumptionEvidenceSha256);
        Assert.Contains(intent.IntentId.ToString(), transition.EvidenceJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(OrderState.Intent.ToStorageValue(), orderEvent.NewState);
        Assert.Equal(intent.ClientOrderId, orderEvent.ClientOrderId);
        Assert.Equal(CandidateReservationTime, intent.CreatedAtUtc);
        Assert.Equal(CandidateReservationTime, orderEvent.LocalTimestampUtc);
    }

    [Fact]
    public async Task ReserveAsync_SameCandidateIntentRetry_DoesNotConsumeCandidateTwice()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var repository = CreateCandidateRepository(new TestDbContextFactory(options));
        var run = CreateRun(Guid.NewGuid());
        var candidateId = Guid.NewGuid();
        var semanticHash = new string('c', 64);
        var reservation = CreateCandidateReservation(
            Guid.NewGuid(),
            candidateId,
            "MSFT",
            semanticHash);
        await SeedTriggeredCandidateAsync(options, run, candidateId, "MSFT", semanticHash);

        var first = await repository.ReserveAsync(run, reservation);
        var retry = await repository.ReserveAsync(run, reservation);

        Assert.True(first.Created);
        Assert.False(retry.Created);
        Assert.Equal(first.Intent.ClientOrderId, retry.Intent.ClientOrderId);
        await using var verification = new TradingFlowDbContext(options);
        Assert.Equal(1, await verification.OrderIntents.CountAsync());
        Assert.Equal(1, await verification.OrderEvents.CountAsync());
        Assert.Equal(2, await verification.CandidateTransitions.CountAsync());
        Assert.Equal(
            StrategyCandidateState.Consumed,
            (await verification.Candidates.AsNoTracking().SingleAsync()).State);
    }

    [Fact]
    public async Task ReserveAsync_DifferentIntentForConsumedCandidate_FailsClosed()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var repository = CreateCandidateRepository(new TestDbContextFactory(options));
        var run = CreateRun(Guid.NewGuid());
        var candidateId = Guid.NewGuid();
        var semanticHash = new string('c', 64);
        var first = CreateCandidateReservation(Guid.NewGuid(), candidateId, "MSFT", semanticHash);
        var second = CreateCandidateReservation(Guid.NewGuid(), candidateId, "MSFT", semanticHash);
        await SeedTriggeredCandidateAsync(options, run, candidateId, "MSFT", semanticHash);
        await repository.ReserveAsync(run, first);

        var error = await Assert.ThrowsAsync<CandidateOrderIntentConflictException>(
            () => repository.ReserveAsync(run, second));

        Assert.Contains(candidateId.ToString("N"), error.Message, StringComparison.Ordinal);
        await using var verification = new TradingFlowDbContext(options);
        Assert.Equal(1, await verification.OrderIntents.CountAsync());
        Assert.Equal(2, await verification.CandidateTransitions.CountAsync());
    }

    [Fact]
    public async Task ReserveAsync_ParallelRepositoryInstances_AllowOnlyOneIntentPerCandidate()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var run = CreateRun(Guid.NewGuid());
        var candidateId = Guid.NewGuid();
        var semanticHash = new string('c', 64);
        await SeedTriggeredCandidateAsync(options, run, candidateId, "MSFT", semanticHash);
        var reservations = new[]
        {
            CreateCandidateReservation(Guid.NewGuid(), candidateId, "MSFT", semanticHash),
            CreateCandidateReservation(Guid.NewGuid(), candidateId, "MSFT", semanticHash)
        };
        var repositories = new[]
        {
            CreateCandidateRepository(factory),
            CreateCandidateRepository(factory)
        };

        var attempts = await Task.WhenAll(repositories.Select(async (repository, index) =>
        {
            try
            {
                return (Result: await repository.ReserveAsync(run, reservations[index]), Error: (Exception?)null);
            }
            catch (Exception error)
            {
                return (Result: (OrderIntentReservationResult?)null, Error: error);
            }
        }));

        Assert.Single(attempts, attempt => attempt.Result is not null);
        Assert.Single(attempts, attempt => attempt.Error is not null);
        await using var verification = new TradingFlowDbContext(options);
        Assert.Equal(1, await verification.OrderIntents.CountAsync());
        Assert.Equal(1, await verification.OrderEvents.CountAsync());
        Assert.Equal(2, await verification.CandidateTransitions.CountAsync());
        var candidate = await verification.Candidates.AsNoTracking().SingleAsync();
        Assert.Equal(StrategyCandidateState.Consumed, candidate.State);
        Assert.Equal(5, candidate.Version);
    }

    [Fact]
    public async Task ReserveAsync_StrategyEntryWithoutCandidateAuthorization_FailsBeforePersistence()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var repository = new SqliteOrderIntentRepository(new TestDbContextFactory(options));
        var run = CreateRun(Guid.NewGuid());
        var reservation = CreateReservation(Guid.NewGuid(), "MSFT") with
        {
            Kind = OrderIntentKind.StrategyEntry
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.ReserveAsync(run, reservation));

        Assert.Contains("requires candidate identity", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(database.DatabasePath));
    }

    [Fact]
    public async Task ReserveAsync_ServerClockExpiredCandidate_TerminalizesWithoutIntent()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var repository = CreateCandidateRepository(factory);
        var run = CreateRun(Guid.NewGuid());
        var candidateId = Guid.NewGuid();
        var semanticHash = new string('c', 64);
        await SeedTriggeredCandidateAsync(options, run, candidateId, "MSFT", semanticHash);
        await using (var context = new TradingFlowDbContext(options))
        {
            var candidate = await context.Candidates.SingleAsync();
            candidate.ExpiresAtUtc = CandidateReservationTime.AddSeconds(-1);
            await context.SaveChangesAsync();
        }

        var error = await Assert.ThrowsAsync<ExpiredCandidateOrderIntentException>(() =>
            repository.ReserveAsync(
                run,
                CreateCandidateReservation(Guid.NewGuid(), candidateId, "MSFT", semanticHash)));

        Assert.Equal(candidateId, error.CandidateId);
        await using var verification = new TradingFlowDbContext(options);
        var persisted = await verification.Candidates.AsNoTracking().SingleAsync();
        Assert.Equal(StrategyCandidateState.Expired, persisted.State);
        Assert.Equal(5, persisted.Version);
        Assert.Empty(await verification.OrderIntents.ToArrayAsync());
        Assert.Empty(await verification.OrderEvents.ToArrayAsync());
        var transitions = await verification.CandidateTransitions
            .OrderBy(record => record.Sequence)
            .ToArrayAsync();
        Assert.Equal(2, transitions.Length);
        Assert.Equal("candidate_expired_before_order_intent", transitions[^1].ReasonCode);
    }

    [Fact]
    public async Task ReserveAsync_TriggeredTransitionHashMismatch_RollsBackWithoutIntent()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var repository = CreateCandidateRepository(factory);
        var run = CreateRun(Guid.NewGuid());
        var candidateId = Guid.NewGuid();
        var semanticHash = new string('c', 64);
        await SeedTriggeredCandidateAsync(options, run, candidateId, "MSFT", semanticHash);
        await using (var context = new TradingFlowDbContext(options))
        {
            var transition = await context.CandidateTransitions.SingleAsync();
            transition.SemanticDecisionSha256 = new string('e', 64);
            await context.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.ReserveAsync(
                run,
                CreateCandidateReservation(Guid.NewGuid(), candidateId, "MSFT", semanticHash)));

        await using var verification = new TradingFlowDbContext(options);
        Assert.Equal(StrategyCandidateState.Triggered, (await verification.Candidates.SingleAsync()).State);
        Assert.Empty(await verification.OrderIntents.ToArrayAsync());
        Assert.Empty(await verification.OrderEvents.ToArrayAsync());
    }

    [Fact]
    public async Task ReserveAsync_ExactRetryWithTamperedConsumptionEvidence_FailsClosed()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var repository = CreateCandidateRepository(factory);
        var run = CreateRun(Guid.NewGuid());
        var candidateId = Guid.NewGuid();
        var semanticHash = new string('c', 64);
        var reservation = CreateCandidateReservation(Guid.NewGuid(), candidateId, "MSFT", semanticHash);
        await SeedTriggeredCandidateAsync(options, run, candidateId, "MSFT", semanticHash);
        await repository.ReserveAsync(run, reservation);
        await using (var context = new TradingFlowDbContext(options))
        {
            var transition = await context.CandidateTransitions
                .SingleAsync(item => item.NewState == StrategyCandidateState.Consumed);
            transition.EvidenceJson = "{\"tampered\":true}";
            await context.SaveChangesAsync();
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.ReserveAsync(run, reservation));

        Assert.Contains("valid persisted candidate consumption", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReserveAsync_ReusedIntentWithChangedRequest_FailsClosed()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var repository = new SqliteOrderIntentRepository(new TestDbContextFactory(options));
        var run = CreateRun(Guid.NewGuid());
        var reservation = CreateReservation(Guid.NewGuid(), "MSFT");

        await using (var context = new TradingFlowDbContext(options))
        {
            await new TradingFlowDatabaseInitializer().InitializeAsync(context);
        }

        await repository.ReserveAsync(run, reservation);
        var changed = reservation with { RequestedQuantity = reservation.RequestedQuantity + 1m };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.ReserveAsync(run, changed));
        Assert.Contains("different order request", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReserveAsync_ReusedIntentWithChangedProfile_FailsClosed()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var repository = new SqliteOrderIntentRepository(new TestDbContextFactory(options));
        var run = CreateRun(Guid.NewGuid());
        var reservation = CreateReservation(Guid.NewGuid(), "MSFT");

        await using (var context = new TradingFlowDbContext(options))
        {
            await new TradingFlowDatabaseInitializer().InitializeAsync(context);
        }

        await repository.ReserveAsync(run, reservation);
        var changedRun = new ProductionRun
        {
            RunId = run.RunId,
            SchemaVersion = run.SchemaVersion,
            ConfigHash = run.ConfigHash,
            CodeVersion = run.CodeVersion,
            Profile = "live",
            Status = run.Status,
            StartedAtUtc = run.StartedAtUtc
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.ReserveAsync(changedRun, reservation));
        Assert.Contains("different provenance", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReserveAsync_ReusedIntentForCompletedRun_ReturnsExistingWithoutAuthorizingNewWork()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var repository = new SqliteOrderIntentRepository(new TestDbContextFactory(options));
        var run = CreateRun(Guid.NewGuid());
        var reservation = CreateReservation(Guid.NewGuid(), "MSFT");

        await using (var context = new TradingFlowDbContext(options))
        {
            await new TradingFlowDatabaseInitializer().InitializeAsync(context);
        }

        await repository.ReserveAsync(run, reservation);
        await using (var context = new TradingFlowDbContext(options))
        {
            var persistedRun = await context.ProductionRuns.SingleAsync(record => record.RunId == run.RunId);
            persistedRun.Status = "completed";
            await context.SaveChangesAsync();
        }

        var replay = await repository.ReserveAsync(run, reservation);

        Assert.False(replay.Created);
        Assert.Equal("completed", replay.OwningRunStatus);
        Assert.Equal(reservation.IntentId, replay.Intent.IntentId);
    }

    [Fact]
    public async Task ReserveAsync_ProtectiveIntentReplayAcrossReconciliationRun_AdoptsOriginalIntent()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var repository = new SqliteOrderIntentRepository(
            new TestDbContextFactory(options),
            new FixedTimeProvider(CandidateReservationTime));
        var originalRun = CreateRun(Guid.NewGuid());
        var restartedRun = CreateRun(Guid.NewGuid());
        var reservation = CreateReservation(Guid.NewGuid(), "MSFT") with
        {
            Kind = OrderIntentKind.ProtectiveStop,
            StrategyId = "BACKSTOP",
            Side = "sell",
            OrderType = "stop",
            TimeInForce = "gtc",
            LimitPrice = null,
            StopPrice = 98m,
            RequestJson = "{\"positionGenerationIdentity\":\"ledger:1:entry-1\"}"
        };

        await using (var context = new TradingFlowDbContext(options))
        {
            await new TradingFlowDatabaseInitializer().InitializeAsync(context);
        }

        var created = await repository.ReserveAsync(originalRun, reservation);
        var replay = await repository.ReserveAsync(restartedRun, reservation);

        Assert.True(created.Created);
        Assert.False(replay.Created);
        Assert.Equal(created.Intent.IntentId, replay.Intent.IntentId);
        Assert.Equal(originalRun.RunId, replay.Intent.RunId);
        await using var verification = new TradingFlowDbContext(options);
        Assert.Single(await verification.OrderIntents.AsNoTracking().ToArrayAsync());
        Assert.Single(await verification.ProductionRuns.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task ReserveAsync_NewIntentWithNonRunningOwner_FailsBeforePersistence()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var repository = new SqliteOrderIntentRepository(new TestDbContextFactory(options));
        var run = CreateRun(Guid.NewGuid());
        run.Status = "completed";

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.ReserveAsync(run, CreateReservation(Guid.NewGuid(), "MSFT")));

        Assert.Contains("requires a running owner", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(database.DatabasePath));
    }

    [Fact]
    public async Task ListActiveForSymbolAsync_ReturnsOnlyLatestNonterminalEntryIntents()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var intents = new SqliteOrderIntentRepository(factory);
        var events = new SqliteOrderEventRepository(factory);
        var run = CreateRun(Guid.NewGuid());

        await using (var context = new TradingFlowDbContext(options))
        {
            await new TradingFlowDatabaseInitializer().InitializeAsync(context);
        }

        var active = (await intents.ReserveAsync(
            run,
            CreateReservation(Guid.NewGuid(), "MSFT", "SWGA"))).Intent;
        var terminal = (await intents.ReserveAsync(
            run,
            CreateReservation(Guid.NewGuid(), "MSFT", "OTHER"))).Intent;
        await events.TransitionAsync(new OrderTransitionRequest(
            terminal.ClientOrderId,
            OrderState.Intent,
            OrderState.Submitted,
            "engine",
            terminal.CreatedAtUtc.AddSeconds(1),
            PayloadJson: "{\"state\":\"submitted\"}"));
        await events.TransitionAsync(new OrderTransitionRequest(
            terminal.ClientOrderId,
            OrderState.Submitted,
            OrderState.Rejected,
            "engine",
            terminal.CreatedAtUtc.AddSeconds(2),
            PayloadJson: "{\"reason\":\"test\"}"));

        var result = await intents.ListActiveForSymbolAsync("msft");

        var item = Assert.Single(result);
        Assert.Equal(active.ClientOrderId, item.ClientOrderId);
        Assert.Equal("SWGA", item.StrategyId);
        Assert.Equal(OrderState.Intent, item.State);
    }

    private static ProductionRun CreateRun(Guid runId) => new()
    {
        RunId = runId,
        SchemaVersion = 1,
        ConfigHash = new string('b', 64),
        CodeVersion = "test-code-version",
        Profile = "paper",
        Status = "running",
        StartedAtUtc = DateTimeOffset.UtcNow
    };

    private static OrderIntentReservation CreateReservation(
        Guid intentId,
        string symbol,
        string strategyId = "SWGA") => new(
        intentId,
        Kind: OrderIntentKind.OperatorEntry,
        CandidateId: null,
        StrategyId: strategyId,
        Symbol: symbol,
        Side: "buy",
        OrderType: "limit",
        TimeInForce: "day",
        RequestedQuantity: 10m,
        LimitPrice: 100m,
        StopPrice: 98m,
        SessionDate: new DateOnly(2026, 7, 21),
        CreatedAtUtc: new DateTimeOffset(2026, 7, 21, 14, 30, 0, TimeSpan.Zero),
        RequestJson: $"{{\"symbol\":\"{symbol}\",\"quantity\":10}}");

    private static OrderIntentReservation CreateCandidateReservation(
        Guid intentId,
        Guid candidateId,
        string symbol,
        string semanticHash) =>
        CreateReservation(intentId, symbol) with
        {
            Kind = OrderIntentKind.StrategyEntry,
            CandidateId = candidateId,
            CandidateExpectedVersion = 4,
            CandidateSemanticDecisionSha256 = semanticHash
        };

    private static async Task SeedTriggeredCandidateAsync(
        DbContextOptions<TradingFlowDbContext> options,
        ProductionRun run,
        Guid candidateId,
        string symbol,
        string semanticHash)
    {
        await using var context = new TradingFlowDbContext(options);
        await new TradingFlowDatabaseInitializer().InitializeAsync(context);
        context.ProductionRuns.Add(run);
        context.Candidates.Add(new CandidateRecord
        {
            CandidateId = candidateId,
            Symbol = symbol,
            DiscoveredAtUtc = new DateTimeOffset(2026, 7, 21, 14, 0, 0, TimeSpan.Zero),
            RevalidatedAtUtc = new DateTimeOffset(2026, 7, 21, 14, 29, 0, TimeSpan.Zero),
            DiscoverySource = "test-discovery",
            FinvizPreset = String.Empty,
            Horizon = "swing",
            SetupScoresJson = "{}",
            SelectedStrategy = "SWGA",
            StrategyContentSha256 = new string('d', 64),
            AdmissionProfileId = "test-admission",
            AdmissionProfileVersion = "1.0.0",
            SetupKey = $"test:{symbol}:{candidateId:N}",
            DiscoveryWindowStartUtc = new DateTimeOffset(2026, 7, 21, 14, 0, 0, TimeSpan.Zero),
            DiscoveryWindowEndUtc = new DateTimeOffset(2026, 7, 21, 14, 29, 0, TimeSpan.Zero),
            State = StrategyCandidateState.Triggered,
            Version = 4,
            ExpiresAtUtc = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero),
            SemanticDecisionSha256 = semanticHash,
            RejectReasonsJson = "[]",
            RunId = run.RunId,
            SchemaVersion = run.SchemaVersion,
            ConfigHash = run.ConfigHash,
            CodeVersion = run.CodeVersion
        });
        context.CandidateTransitions.Add(new CandidateTransitionRecord
        {
            CandidateId = candidateId,
            Sequence = 4,
            PreviousState = StrategyCandidateState.Armed,
            NewState = StrategyCandidateState.Triggered,
            OccurredAtUtc = new DateTimeOffset(2026, 7, 21, 14, 29, 0, TimeSpan.Zero),
            ReasonCode = "test_trigger_satisfied",
            Source = "test",
            SemanticDecisionSha256 = semanticHash,
            EvidenceJson = $"{{\"symbol\":\"{symbol}\",\"decision\":\"triggered\"}}",
            RunId = run.RunId,
            SchemaVersion = run.SchemaVersion,
            ConfigHash = run.ConfigHash,
            CodeVersion = run.CodeVersion
        });
        await context.SaveChangesAsync();
    }

    private static SqliteOrderIntentRepository CreateCandidateRepository(
        IDbContextFactory<TradingFlowDbContext> factory) =>
        new(factory, new FixedTimeProvider(CandidateReservationTime));

    private static async Task<string?> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync());
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private sealed class TestDbContextFactory(DbContextOptions<TradingFlowDbContext> options)
        : IDbContextFactory<TradingFlowDbContext>
    {
        public TradingFlowDbContext CreateDbContext() => new(options);

        public Task<TradingFlowDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class FixedTimeProvider(DateTimeOffset timestampUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => timestampUtc;
    }

    private sealed class TemporarySqliteDatabase : IAsyncDisposable
    {
        private readonly string root = Path.Combine(
            Path.GetTempPath(),
            $"tradingflow-durability-{Guid.NewGuid():N}");

        public TemporarySqliteDatabase()
        {
            Directory.CreateDirectory(root);
        }

        public string DatabasePath => Path.Combine(root, "tradingflow.db");

        public DbContextOptions<TradingFlowDbContext> CreateOptions(bool withDurabilityInterceptor)
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Pooling = false
            }.ToString();
            var builder = new DbContextOptionsBuilder<TradingFlowDbContext>()
                .UseSqlite(connectionString);
            if (withDurabilityInterceptor)
            {
                builder.AddInterceptors(new SqliteConnectionDurabilityInterceptor());
            }

            return builder.Options;
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
