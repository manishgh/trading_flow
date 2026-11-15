using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TradingFlow.Data.Context;
using TradingFlow.Data.Discovery;
using TradingFlow.Data.Jobs;
using TradingFlow.Domain.Discovery;
using TradingFlow.Domain.Execution;
using TradingFlow.Domain.Jobs;
using TradingFlow.Domain.Research;
using TradingFlow.Data.Orders;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Execution;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services;

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
    public async Task ExecutionJournalFlush_CheckpointsCommittedWalPages()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        await InitializeAsync(options);

        await using (var context = await factory.CreateDbContextAsync())
        {
            context.ProductionRuns.Add(CreateRun(Guid.NewGuid()));
            await context.SaveChangesAsync();
        }

        await new SqliteExecutionJournalFlushService(factory).FlushAsync();

        await using var verification = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = database.DatabasePath, Pooling = false }.ToString());
        await verification.OpenAsync();
        await using var command = verification.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
        await using var checkpoint = await command.ExecuteReaderAsync();
        Assert.True(await checkpoint.ReadAsync());
        Assert.Equal(0, checkpoint.GetInt32(0));
        Assert.Equal(checkpoint.GetInt32(1), checkpoint.GetInt32(2));
    }

    [Fact]
    public async Task ReserveAsync_RepeatedLogicalIntent_ReusesPersistedClientOrderId()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var repository = new SqliteOrderIntentRepository(new TestDbContextFactory(options), new FixedTimeProvider(new DateTimeOffset(2026, 7, 21, 14, 30, 0, TimeSpan.Zero)));
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

    // Run the actual journal and paper read path with delayed admission, retries, duplicate fills and two New York months.
    [Theory]
    [InlineData("paper")]
    [InlineData("paper_shadow")]
    [InlineData("paper_experiment")]
    public async Task PaperExecutionTelemetry_PreservesFirstQuoteAndSeparatesUnitFillsAndBaselines(string profile)
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var clock = new MutableTimeProvider(CandidateReservationTime);
        var run = CreateRun(Guid.NewGuid());
        run.Profile = profile;
        var candidateId = Guid.NewGuid();
        var semantic = new { orderPlan = new { triggerReferencePrice = 99m, triggeredAtUtc = clock.GetUtcNow().AddMinutes(-1) } };
        var semanticHash = EvidenceCanonicalJson.ComputeSha256(semantic);
        var identity = new StrategyArtifactIdentity("SWGA", "1.0.0", new string('d', 64));
        var receipt = new AdmissionQuoteReceipt("receipt-1", new string('a', 64), clock.GetUtcNow().AddMilliseconds(-20));
        var quote = new AdmissionOrderQuote(99.99m, 100m, "sip", clock.GetUtcNow().AddMilliseconds(-100),
            clock.GetUtcNow(), 500, receipt);
        // Reviewed strategy entry has no caller-supplied decision price; the exact persisted trigger supplies it.
        var admission = new OrderAdmissionEvidence(quote, null, identity);
        var reservation = CreateCandidateReservation(Guid.NewGuid(), candidateId, "MSFT", semanticHash)
            with { AdmissionEvidence = admission };
        await SeedTriggeredCandidateAsync(options, run, candidateId, "MSFT", semanticHash,
            evidenceJson: System.Text.Json.JsonSerializer.Serialize(new { semantic }));
        await using (var seeded = new TradingFlowDbContext(options))
        {
            var trigger = await seeded.CandidateTransitions.SingleAsync(row => row.CandidateId == candidateId && row.Sequence == 4);
            trigger.Source = "strategy_decision_kernel";
            trigger.OccurredAtUtc = semantic.orderPlan.triggeredAtUtc;
            (await seeded.Candidates.SingleAsync(row => row.CandidateId == candidateId)).RevalidatedAtUtc = trigger.OccurredAtUtc;
            await seeded.SaveChangesAsync();
        }
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var records = new SqliteOrderEventRepository(factory);
        var lifecycle = new OrderLifecycleService(records, clock);
        var intent = (await intents.ReserveAsync(run, reservation)).Intent;
        var replay = await intents.ReserveAsync(run, reservation with
        {
            AdmissionEvidence = admission with { Quote = quote with { Ask = 120m, ObservedAtUtc = quote.ObservedAtUtc.AddSeconds(1) } }
        });
        Assert.False(replay.Created);
        Assert.Equal(reservation.RequestJson, replay.Intent.RequestJson);
        await intents.ReserveAsync(run, CreateReservation(Guid.NewGuid(), "NVDA"));
        await records.TransitionAsync(new(intent.ClientOrderId, OrderState.Intent, OrderState.Submitted, "engine",
            clock.GetUtcNow(), PayloadJson: intent.RequestJson));
        var lease = await intents.TryAcquireDispatchLeaseAsync(intent.IntentId, intent.AccountId, "fixture", TimeSpan.FromSeconds(30));
        clock.Advance(TimeSpan.FromSeconds(1));
        await intents.RecordDispatchAttemptAsync(intent.IntentId, lease!.LeaseToken);
        clock.Advance(TimeSpan.FromSeconds(1));
        await intents.RecordDispatchAttemptAsync(intent.IntentId, lease.LeaseToken);

        var july = new DateTimeOffset(2026, 8, 1, 3, 59, 0, TimeSpan.Zero);
        var august = july.AddMinutes(2);
        await ObservePaperFillAsync(lifecycle, intent, "partial-one", OrderStatus.PartiallyFilled, july, july.AddMinutes(2), 4, 4, 100, 100);
        await ObservePaperFillAsync(lifecycle, intent, "partial-one", OrderStatus.PartiallyFilled, july, july.AddMinutes(2), 4, 4, 100, 100);
        await ObservePaperFillAsync(lifecycle, intent, "partial-two", OrderStatus.PartiallyFilled, august, august.AddSeconds(1), 3, 7, 102, 706m / 7m);
        await ObservePaperFillAsync(lifecycle, intent, "legacy-fill", OrderStatus.Filled, null, august.AddSeconds(2), 3, 10, 110, 103.6m);

        var view = new PaperExecutionComparisonService(factory, NullLogger<PaperExecutionComparisonService>.Instance);
        var result = await view.ReadAsync(run.RunId, default);
        Assert.Null(result.UnavailableReason);
        var detail = Assert.Single(result.Orders, order => order.Intent.IntentId == intent.IntentId);
        Assert.Equal(identity, detail.Identity);
        Assert.Equal("receipt-1", detail.Admission!.QuoteReceiptId);
        Assert.Equal(100m, detail.Admission.Ask);
        Assert.Equal(quote.ObservedAtUtc, detail.Admission.QuoteObservedAtUtc);
        Assert.Equal([1, 2], detail.Attempts.Select(attempt => attempt.AttemptNumber));
        Assert.Equal(TimeSpan.FromSeconds(61), detail.DecisionToFirstAttempt);
        Assert.Equal(new PaperDispatchQuoteTiming(TimeSpan.FromMilliseconds(1100), TimeSpan.FromSeconds(1), true),
            detail.QuoteTiming(detail.FirstPrePostAtUtc));
        Assert.Equal([3m, 4m, 3m], detail.Fills.Select(fill => fill.Quantity));
        Assert.Equal(103.6m, detail.WeightedFillPrice);
        var manual = Assert.Single(result.Orders, order => order.Intent.Symbol == "NVDA");
        Assert.Null(manual.Admission);
        Assert.Null(manual.Reference(PaperPriceReference.Decision));
        Assert.Equal(1, Assert.Single(result.Coverage, row => row.State == "FILLED").FillsWithoutClock);

        var ask = result.Months.Where(row => row.ReferenceKind == PaperPriceReference.AdmissionAsk).ToArray();
        Assert.Equal([("2026-07", 4m, 0m), ("2026-08", 3m, 200m)],
            ask.Select(row => (row.Month, row.FillQuantity, row.AdverseBps!.Value)));
        Assert.All(result.Months.Where(row => row.ReferenceKind == PaperPriceReference.ModeledExecutable),
            row => Assert.Null(row.AdverseBps));
        Assert.Equal(2, result.Months.Count(row => row.ReferenceKind == PaperPriceReference.Decision && row.AdverseBps > 0));
    }

    // A replayed broker update carries its execution ID and delta through the unchanged order lifecycle.
    private static Task<OrderStateSnapshot> ObservePaperFillAsync(OrderLifecycleService lifecycle, OrderIntentRecord intent,
        string executionId, OrderStatus status, DateTimeOffset? sourceTimeUtc, DateTimeOffset lifecycleTimeUtc,
        decimal units, decimal cumulativeUnits, decimal price, decimal averagePrice) =>
        lifecycle.ApplyBrokerUpdateWithFillAsync(new("broker-fixture", intent.ClientOrderId, intent.Symbol, intent.Side,
            status, cumulativeUnits, averagePrice, units, cumulativeUnits, executionId, lifecycleTimeUtc,
            BrokerUpdateSource.TradeStream, price, sourceTimeUtc),
            new(intent.Symbol, intent.Side, cumulativeUnits, averagePrice, executionId, cumulativeUnits, units, price));

    // A failed telemetry write path is irrelevant to an independently fenced protective dispatch.
    [Fact]
    public async Task PaperExecutionTelemetry_FailingAttemptTableDoesNotDisableProtection()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        await InitializeAsync(options);
        var intents = new SqliteOrderIntentRepository(factory, new FixedTimeProvider(CandidateReservationTime));
        var reserved = (await intents.ReserveAsync(CreateRun(Guid.NewGuid()), CreateProtectiveReservation(Guid.NewGuid(), "MSFT"))).Intent;
        await using var context = new TradingFlowDbContext(options);
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER fail_attempt_telemetry BEFORE INSERT ON order_dispatch_attempt_telemetry
            BEGIN SELECT RAISE(ABORT, 'fixture rejected telemetry'); END;
            """);
        var lease = await intents.TryAcquireDispatchLeaseAsync(reserved.IntentId, reserved.AccountId, "fixture", TimeSpan.FromSeconds(30));
        var attempted = await intents.RecordDispatchAttemptAsync(reserved.IntentId, lease!.LeaseToken);
        Assert.Equal(1, attempted.DispatchAttemptCount);
        Assert.Empty(await context.Set<OrderDispatchAttemptTelemetryRecord>().AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task ReserveAsync_ParallelDistinctIntents_AllocatesDistinctMonotonicSequences()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var repository = new SqliteOrderIntentRepository(new TestDbContextFactory(options), new FixedTimeProvider(new DateTimeOffset(2026, 7, 21, 14, 30, 0, TimeSpan.Zero)));
        var run = CreateRun(Guid.NewGuid());

        await using (var context = new TradingFlowDbContext(options))
        {
            await new TradingFlowDatabaseInitializer().InitializeAsync(context);
        }

        var reservations = Enumerable.Range(0, 8)
            .Select(_ => CreateProtectiveReservation(Guid.NewGuid(), "MSFT"))
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
            .Select(_ => new SqliteOrderIntentRepository(contextFactory, new FixedTimeProvider(new DateTimeOffset(2026, 7, 21, 14, 30, 0, TimeSpan.Zero))))
            .ToArray();
        var run = CreateRun(Guid.NewGuid());

        await using (var context = new TradingFlowDbContext(options))
        {
            await new TradingFlowDatabaseInitializer().InitializeAsync(context);
        }

        var reservations = Enumerable.Range(0, 8)
            .Select(_ => CreateProtectiveReservation(Guid.NewGuid(), "MSFT"))
            .ToArray();
        var reserved = await Task.WhenAll(reservations.Select((reservation, index) =>
            repositories[index % repositories.Length].ReserveAsync(run, reservation)));

        Assert.Equal(Enumerable.Range(1, 8), reserved.Select(result => result.Intent.SequenceNumber).Order());
        Assert.Equal(8, reserved.Select(result => result.Intent.ClientOrderId).Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("day")]
    [InlineData("")]
    [InlineData("unknown")]
    public async Task ReserveAsync_NonSwingRiskHorizonPersistsNothing(string horizon)
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        await InitializeAsync(options);
        var intents = new SqliteOrderIntentRepository(new TestDbContextFactory(options), new FixedTimeProvider(new DateTimeOffset(2026, 7, 21, 14, 30, 0, TimeSpan.Zero)));
        var reservation = CreateReservation(Guid.NewGuid(), "MSFT");
        reservation = reservation with { PortfolioRisk = reservation.PortfolioRisk! with { Horizon = horizon } };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            intents.ReserveAsync(CreateRun(Guid.NewGuid()), reservation));

        await using var verification = new TradingFlowDbContext(options);
        Assert.Empty(await verification.OrderIntents.ToListAsync());
        Assert.Empty(await verification.PortfolioRiskReservations.ToListAsync());
        Assert.Empty(await verification.OrderEvents.ToListAsync());
    }

    [Theory]
    [InlineData("day", false)]
    [InlineData("day", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public async Task RecoverPendingAsync_NonSwingEntryExpiresWithoutBrokerPost(
        string? horizon, bool alreadySubmitted)
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 21, 14, 31, 0, TimeSpan.Zero));
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        await InitializeAsync(options);
        var intent = (await intents.ReserveAsync(
            CreateRun(Guid.NewGuid()), CreateReservation(Guid.NewGuid(), "MSFT"))).Intent;
        // Simulate a pre-retirement persisted entry without weakening current reservation validation.
        await using (var context = new TradingFlowDbContext(options))
        {
            var risk = await context.PortfolioRiskReservations.SingleAsync();
            if (horizon is null)
                context.PortfolioRiskReservations.Remove(risk);
            else
                risk.Horizon = horizon;
            await context.SaveChangesAsync();
        }
        if (alreadySubmitted)
        {
            await events.TransitionAsync(new OrderTransitionRequest(
                intent.ClientOrderId, OrderState.Intent, OrderState.Submitted,
                "engine", clock.GetUtcNow(), PayloadJson: intent.RequestJson));
        }
        var broker = CreateDispatchBroker(intent, brokerOrder: null, "must-not-submit");
        var dispatcher = CreateDispatcher(intents, events, clock);

        Assert.Equal(0, await dispatcher.RecoverPendingAsync(broker.Object));
        var state = await events.GetCurrentAsync(intent.ClientOrderId);
        Assert.Equal(OrderState.Expired, state!.State);
        Assert.Equal(0, (await intents.GetByIntentIdAsync(intent.IntentId))!.DispatchAttemptCount);
        broker.Verify(client => client.SubmitOrderAsync(
            It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()), Times.Never);
        await using var verification = new TradingFlowDbContext(options);
        var savedRisk = await verification.PortfolioRiskReservations.SingleOrDefaultAsync();
        if (horizon is not null)
        {
            Assert.Equal(horizon, savedRisk!.Horizon);
            Assert.Equal(PortfolioRiskReservationState.Released, savedRisk.State);
        }
        Assert.Equal(0, await dispatcher.RecoverPendingAsync(broker.Object));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DispatchAsync_RetiredHorizonPriorAttemptRequiresBrokerEvidence(bool brokerObserved)
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 21, 14, 31, 0, TimeSpan.Zero));
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        await InitializeAsync(options);
        var intent = (await intents.ReserveAsync(
            CreateRun(Guid.NewGuid()), CreateReservation(Guid.NewGuid(), "MSFT"))).Intent;
        await events.TransitionAsync(new OrderTransitionRequest(
            intent.ClientOrderId, OrderState.Intent, OrderState.Submitted,
            "engine", clock.GetUtcNow(), PayloadJson: intent.RequestJson));
        var lease = await intents.TryAcquireDispatchLeaseAsync(
            intent.IntentId, intent.AccountId, "previous-owner", TimeSpan.FromSeconds(30));
        await intents.RecordDispatchAttemptAsync(intent.IntentId, lease!.LeaseToken);
        await intents.ReleaseDispatchLeaseAsync(intent.IntentId, lease.LeaseToken);
        await using (var context = new TradingFlowDbContext(options))
        {
            (await context.PortfolioRiskReservations.SingleAsync()).Horizon = "day";
            await context.SaveChangesAsync();
        }
        clock.Advance(TimeSpan.FromSeconds(31));
        var brokerOrder = brokerObserved ? CreateMatchingBrokerOrder(intent, "existing-order", "new") : null;
        var broker = CreateDispatchBroker(intent, brokerOrder, "must-not-submit");
        var dispatcher = CreateDispatcher(intents, events, clock);

        if (brokerObserved)
        {
            Assert.Equal("existing-order", (await dispatcher.DispatchAsync(intent.IntentId, broker.Object)).BrokerOrderId);
            Assert.Equal(OrderState.Acked, (await events.GetCurrentAsync(intent.ClientOrderId))!.State);
        }
        else
        {
            await Assert.ThrowsAsync<OrderDispatchAdoptionRequiredException>(() =>
                dispatcher.DispatchAsync(intent.IntentId, broker.Object));
            Assert.Equal(OrderState.Submitted, (await events.GetCurrentAsync(intent.ClientOrderId))!.State);
        }
        broker.Verify(client => client.SubmitOrderAsync(
            It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()), Times.Never);
        await using var verification = new TradingFlowDbContext(options);
        var attempt = Assert.Single(await verification.Set<OrderDispatchAttemptTelemetryRecord>().AsNoTracking().ToArrayAsync());
        Assert.Equal(intent.IntentId, attempt.IntentId);
        Assert.Equal(1, attempt.AttemptNumber);
    }

    [Fact]
    public async Task DispatchAsync_DurableIntentAfterCrash_SubmitsAndAcknowledgesExactlyOnce()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 21, 14, 31, 0, TimeSpan.Zero));
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        await InitializeAsync(options);
        var reserved = await intents.ReserveAsync(
            CreateRun(Guid.NewGuid()),
            CreateReservation(Guid.NewGuid(), "MSFT"));
        var broker = CreateDispatchBroker(reserved.Intent, brokerOrder: null, "broker-recovered");
        var dispatcher = CreateDispatcher(intents, events, clock);

        var result = await dispatcher.DispatchAsync(
            reserved.Intent.IntentId,
            broker.Object,
            CancellationToken.None);

        Assert.Equal("broker-recovered", result.BrokerOrderId);
        Assert.Equal(OrderState.Acked, (await events.GetCurrentAsync(reserved.Intent.ClientOrderId))!.State);
        Assert.Equal(1, (await intents.GetByIntentIdAsync(reserved.Intent.IntentId))!.DispatchAttemptCount);
        broker.Verify(client => client.SubmitOrderAsync(
            It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_AcceptedResponseLost_AdoptsByClientOrderIdWithoutSecondPost()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 21, 14, 31, 0, TimeSpan.Zero));
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        await InitializeAsync(options);
        var reserved = await intents.ReserveAsync(
            CreateRun(Guid.NewGuid()),
            CreateReservation(Guid.NewGuid(), "MSFT"));
        var firstLease = await intents.TryAcquireDispatchLeaseAsync(
            reserved.Intent.IntentId,
            "paper-account",
            "crashed-owner",
            TimeSpan.FromSeconds(30));
        Assert.NotNull(firstLease);
        await events.TransitionAsync(new OrderTransitionRequest(
            reserved.Intent.ClientOrderId,
            OrderState.Intent,
            OrderState.Submitted,
            "engine",
            clock.GetUtcNow(),
            PayloadJson: reserved.Intent.RequestJson));
        await intents.RecordDispatchAttemptAsync(reserved.Intent.IntentId, firstLease!.LeaseToken);
        await intents.ReleaseDispatchLeaseAsync(reserved.Intent.IntentId, firstLease.LeaseToken);
        var brokerOrder = CreateMatchingBrokerOrder(reserved.Intent, "broker-adopted", "new");
        var broker = CreateDispatchBroker(reserved.Intent, brokerOrder, "must-not-submit");
        var dispatcher = CreateDispatcher(intents, events, clock);

        var result = await dispatcher.DispatchAsync(
            reserved.Intent.IntentId,
            broker.Object,
            CancellationToken.None);

        Assert.Equal("broker-adopted", result.BrokerOrderId);
        Assert.Equal(OrderState.Acked, (await events.GetCurrentAsync(reserved.Intent.ClientOrderId))!.State);
        broker.Verify(client => client.SubmitOrderAsync(
            It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DispatchAsync_PriorAmbiguousAttemptNeverPostsAgainWhenBrokerLookupIsEmpty()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 21, 14, 31, 0, TimeSpan.Zero));
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        await InitializeAsync(options);
        var reserved = await intents.ReserveAsync(
            CreateRun(Guid.NewGuid()),
            CreateReservation(Guid.NewGuid(), "MSFT"));
        var firstLease = await intents.TryAcquireDispatchLeaseAsync(
            reserved.Intent.IntentId,
            "paper-account",
            "crashed-owner",
            TimeSpan.FromSeconds(30));
        Assert.NotNull(firstLease);
        await events.TransitionAsync(new OrderTransitionRequest(
            reserved.Intent.ClientOrderId,
            OrderState.Intent,
            OrderState.Submitted,
            "engine",
            clock.GetUtcNow(),
            PayloadJson: reserved.Intent.RequestJson));
        await intents.RecordDispatchAttemptAsync(reserved.Intent.IntentId, firstLease!.LeaseToken);
        await intents.ReleaseDispatchLeaseAsync(reserved.Intent.IntentId, firstLease.LeaseToken);
        clock.Advance(TimeSpan.FromSeconds(31));
        var broker = CreateDispatchBroker(reserved.Intent, brokerOrder: null, "must-not-submit");
        var dispatcher = CreateDispatcher(intents, events, clock);

        await Assert.ThrowsAsync<OrderDispatchAdoptionRequiredException>(() =>
            dispatcher.DispatchAsync(reserved.Intent.IntentId, broker.Object));

        Assert.Equal(OrderState.Submitted, (await events.GetCurrentAsync(reserved.Intent.ClientOrderId))!.State);
        broker.Verify(client => client.SubmitOrderAsync(
            It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DispatchAsync_ConcurrentDispatchers_PostOneBrokerOrder()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 21, 14, 31, 0, TimeSpan.Zero));
        var firstIntents = new SqliteOrderIntentRepository(factory, clock);
        var secondIntents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        await InitializeAsync(options);
        var reserved = await firstIntents.ReserveAsync(
            CreateRun(Guid.NewGuid()),
            CreateReservation(Guid.NewGuid(), "MSFT"));
        var broker = CreateDispatchBroker(reserved.Intent, brokerOrder: null, "broker-once");
        var first = CreateDispatcher(firstIntents, events, clock);
        var second = CreateDispatcher(secondIntents, events, clock);

        var results = await Task.WhenAll(
            CaptureDispatchAsync(first, reserved.Intent.IntentId, broker.Object),
            CaptureDispatchAsync(second, reserved.Intent.IntentId, broker.Object));

        Assert.Contains(results, result => result is OrderSubmissionResult);
        Assert.All(results, result => Assert.True(
            result is OrderSubmissionResult or OrderDispatchPendingException));
        broker.Verify(client => client.SubmitOrderAsync(
            It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchLease_ExpiredOwnerCannotRecordAttemptAfterTakeover()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 21, 14, 31, 0, TimeSpan.Zero));
        var first = new SqliteOrderIntentRepository(factory, clock);
        var second = new SqliteOrderIntentRepository(factory, clock);
        await InitializeAsync(options);
        var reserved = await first.ReserveAsync(
            CreateRun(Guid.NewGuid()),
            CreateReservation(Guid.NewGuid(), "MSFT"));
        var stale = await first.TryAcquireDispatchLeaseAsync(
            reserved.Intent.IntentId, "paper-account", "first", TimeSpan.FromSeconds(30));
        Assert.NotNull(stale);

        clock.Advance(TimeSpan.FromSeconds(31));
        var replacement = await second.TryAcquireDispatchLeaseAsync(
            reserved.Intent.IntentId, "paper-account", "second", TimeSpan.FromSeconds(30));

        Assert.NotNull(replacement);
        Assert.NotEqual(stale!.LeaseToken, replacement!.LeaseToken);
        await Assert.ThrowsAsync<OrderDispatchLeaseLostException>(() =>
            first.RecordDispatchAttemptAsync(reserved.Intent.IntentId, stale.LeaseToken));
    }

    [Fact]
    public async Task PositionExitExpiry_ActiveDispatchLeaseWinsAtomicOwnership()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var now = new DateTimeOffset(2026, 7, 21, 14, 31, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var first = new SqliteOrderIntentRepository(factory, clock);
        var second = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        var positions = new SqlitePositionLedgerRepository(factory);
        await InitializeAsync(options);
        var run = CreateRun(Guid.NewGuid());
        run.StartedAtUtc = now.AddHours(-1);
        var position = await SeedPositionAsync(
            positions, run, "paper-account", "MSFT", 10m, now.AddMinutes(-5));
        var reserved = await first.ReserveAsync(
            run,
            CreateUnpreparedExitReservation(
                Guid.NewGuid(), position.PositionGenerationEventId));
        var lease = await second.TryAcquireDispatchLeaseAsync(
            reserved.Intent.IntentId,
            "paper-account",
            "recovery-dispatcher",
            TimeSpan.FromSeconds(30));

        var expired = await first.TryExpireUnattemptedUnleasedPositionExitAsync(
            reserved.Intent.IntentId,
            "failed_exit_handoff",
            now);

        Assert.NotNull(lease);
        Assert.False(expired);
        Assert.Equal(
            OrderState.Intent,
            (await events.GetCurrentAsync(reserved.Intent.ClientOrderId))!.State);
    }

    [Fact]
    public async Task RecordDispatchAttempt_ExpiredLifecycleCannotReachBrokerOwnership()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var now = new DateTimeOffset(2026, 7, 21, 14, 31, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        var positions = new SqlitePositionLedgerRepository(factory);
        await InitializeAsync(options);
        var run = CreateRun(Guid.NewGuid());
        run.StartedAtUtc = now.AddHours(-1);
        var position = await SeedPositionAsync(
            positions, run, "paper-account", "MSFT", 10m, now.AddMinutes(-5));
        var reserved = await intents.ReserveAsync(
            run,
            CreateUnpreparedExitReservation(
                Guid.NewGuid(), position.PositionGenerationEventId));
        var lease = await intents.TryAcquireDispatchLeaseAsync(
            reserved.Intent.IntentId,
            "paper-account",
            "recovery-dispatcher",
            TimeSpan.FromSeconds(30));
        Assert.NotNull(lease);
        await events.TransitionAsync(new OrderTransitionRequest(
            reserved.Intent.ClientOrderId,
            OrderState.Intent,
            OrderState.Expired,
            "engine",
            now,
            PayloadJson: "{\"reason\":\"concurrent_expiry\"}"));

        await Assert.ThrowsAsync<OrderDispatchLeaseLostException>(() =>
            intents.RecordDispatchAttemptAsync(
                reserved.Intent.IntentId,
                lease!.LeaseToken));

        Assert.Equal(
            0,
            (await intents.GetByIntentIdAsync(reserved.Intent.IntentId))!.DispatchAttemptCount);
    }

    [Fact]
    public async Task DispatchAsync_ExpiredBeforeFirstPost_TerminalizesAndReleasesRisk()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var reservationClock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 21, 14, 31, 0, TimeSpan.Zero));
        var reservationRepository = new SqliteOrderIntentRepository(factory, reservationClock);
        var events = new SqliteOrderEventRepository(factory);
        await InitializeAsync(options);
        var reserved = await reservationRepository.ReserveAsync(
            CreateRun(Guid.NewGuid()),
            CreateReservation(Guid.NewGuid(), "MSFT"));
        var expiredClock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 21, 14, 36, 0, TimeSpan.Zero));
        var expiredRepository = new SqliteOrderIntentRepository(factory, expiredClock);
        var broker = CreateDispatchBroker(reserved.Intent, brokerOrder: null, "must-not-submit");
        var dispatcher = CreateDispatcher(expiredRepository, events, expiredClock);

        await Assert.ThrowsAsync<OrderDispatchExpiredException>(() =>
            dispatcher.DispatchAsync(reserved.Intent.IntentId, broker.Object, CancellationToken.None));

        Assert.Equal(OrderState.Expired, (await events.GetCurrentAsync(reserved.Intent.ClientOrderId))!.State);
        Assert.Equal(
            PortfolioRiskReservationState.Released,
            (await expiredRepository.GetRiskReservationByIntentIdAsync(reserved.Intent.IntentId))!.State);
        broker.Verify(client => client.SubmitOrderAsync(
            It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Capacity rejection expires an undispatched order with its actual reason and is a completed recovery outcome.</summary>
    [Fact]
    public async Task RecoverPendingAsync_ChangedTradingLimits_ExpiresWithSpecificReasonAndReleasesRisk()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 21, 14, 30, 0, TimeSpan.Zero));
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        await InitializeAsync(options);
        var reserved = await intents.ReserveAsync(CreateRun(Guid.NewGuid()), CreateReservation(Guid.NewGuid(), "MSFT"));
        await new SqliteOperationalTradingLimitStore(factory).SaveAsync("paper", 2, 3, 0, clock.GetUtcNow());
        var broker = CreateDispatchBroker(reserved.Intent, brokerOrder: null, "must-not-submit");
        var dispatcher = CreateDispatcher(intents, events, clock);
        Assert.Equal(0, await dispatcher.RecoverPendingAsync(broker.Object));
        Assert.Equal(OrderState.Expired, (await events.GetCurrentAsync(reserved.Intent.ClientOrderId))!.State);
        Assert.Equal(PortfolioRiskReservationState.Released,
            (await intents.GetRiskReservationByIntentIdAsync(reserved.Intent.IntentId))!.State);
        await using var verification = new TradingFlowDbContext(options);
        var terminal = await verification.OrderEvents.Where(row => row.ClientOrderId == reserved.Intent.ClientOrderId)
            .OrderByDescending(row => row.EventId).FirstAsync();
        Assert.Contains("trading_limits_changed", terminal.PayloadJson);
        Assert.DoesNotContain("owning_run_inactive", terminal.PayloadJson);
        Assert.Contains("message", terminal.PayloadJson);
        broker.Verify(client => client.SubmitOrderAsync(It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Submit a real durable exit for the original position after a rebuild takes the journal lease.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubmitPositionExitAsync_RepeatedRequest_PostsOneDurableExit(bool rebuilt)
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var run = CreateRun(Guid.NewGuid());
        run.StartedAtUtc = now.AddMinutes(-30);
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        var positions = new SqlitePositionLedgerRepository(factory);
        await InitializeAsync(options);
        var jobs = new SqliteJobRepository(factory);
        var originalLease = rebuilt ? await StartPaperAttemptAsync(jobs, run) : null;
        await SeedPositionAsync(positions, run, "paper-account", "MSFT", 10m, now.AddMinutes(-5));
        if (originalLease is not null)
        {
            var resumed = await ContinueAfterRebuildAsync(jobs, run, originalLease, default);
            Assert.NotNull(resumed.ExitOnlyContinuation);
            Assert.Equal(run.CodeVersion, resumed.Run.CodeVersion);
            run = resumed.Run;
        }
        var broker = CreateExitBroker("paper-account", now, "exit-order-1");
        var dispatcher = CreateDispatcher(intents, events, clock, positions);
        var service = new OrderSubmissionService(
            intents,
            dispatcher,
            new Mock<IEntryGateChain>(MockBehavior.Strict).Object,
            NullLogger<OrderSubmissionService>.Instance,
            positions,
            events: events,
            lifecycle: new OrderLifecycleService(events, clock),
            timeProvider: clock);
        var context = new ExecutionRunContext(
            run.RunId,
            run.Profile,
            run.ConfigHash,
            run.CodeVersion,
            run.StartedAtUtc);
        var request = new PositionExitSubmission(
            context,
            "MSFT",
            10m,
            "technical_exit",
            now);

        var first = await service.SubmitPositionExitAsync(
            request,
            broker.Object,
            CancellationToken.None);
        var replay = await service.SubmitPositionExitAsync(
            request,
            broker.Object,
            CancellationToken.None);

        Assert.Equal(first.IntentId, replay.IntentId);
        Assert.Equal(first.ClientOrderId, replay.ClientOrderId);
        Assert.Equal("exit-order-1", replay.BrokerOrderId);
        broker.Verify(client => client.SubmitExitOrderAsync(
            It.Is<BrokerExitOrder>(order =>
                order.Ticker == "MSFT" &&
                order.Side == "sell" &&
                order.Quantity == 10m &&
                order.OrderType == "market" &&
                !order.SubmitOutsideRegularHours),
            It.IsAny<CancellationToken>()), Times.Once);
        await using var verification = new TradingFlowDbContext(options);
        var intent = await verification.OrderIntents.AsNoTracking().SingleAsync();
        Assert.Equal(OrderIntentKind.PositionExit, intent.Kind);
        Assert.Equal(run.RunId, intent.RunId);
        Assert.Equal(run.CodeVersion, intent.CodeVersion);
        Assert.Equal("paper-account", intent.AccountId);
        Assert.Equal(OrderState.Acked, (await events.GetCurrentAsync(intent.ClientOrderId))!.State);
    }

    [Fact]
    public async Task SubmitPositionExitAsync_TerminalFailureCreatesOneNewDurableAttempt()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var run = CreateRun(Guid.NewGuid());
        run.StartedAtUtc = now.AddMinutes(-30);
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        var positions = new SqlitePositionLedgerRepository(factory);
        await InitializeAsync(options);
        await SeedPositionAsync(positions, run, "paper-account", "MSFT", 10m, now.AddMinutes(-5));
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateAccountSnapshot("paper-account", now));
        broker.Setup(client => client.GetSessionAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TradingSessionSnapshot(
                DateOnly.FromDateTime(now.UtcDateTime),
                EquityTradingSession.Regular,
                now,
                now.AddHours(-1),
                now.AddHours(5)));
        broker.Setup(client => client.GetOrderByClientOrderIdAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ActiveBrokerOrder?)null);
        broker.SetupSequence(client => client.SubmitExitOrderAsync(
                It.IsAny<BrokerExitOrder>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BrokerOrderRejectedException("submit-exit", 422, "temporary broker rejection"))
            .ReturnsAsync(new BrokerOrderReceipt("exit-order-2", now.AddSeconds(2)));
        broker.Setup(client => client.GetOpenPositionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new BrokerPosition("MSFT", "long", 10m, 100m, 100m, 0m)]);
        var dispatcher = CreateDispatcher(intents, events, clock, positions);
        var service = new OrderSubmissionService(
            intents,
            dispatcher,
            new Mock<IEntryGateChain>(MockBehavior.Strict).Object,
            NullLogger<OrderSubmissionService>.Instance,
            positions,
            events: events,
            lifecycle: new OrderLifecycleService(events, clock),
            accountBinding: new BrokerAccountBindingService(
                new BrokerAccountBindingOptions("paper-account")),
            timeProvider: clock);
        var request = new PositionExitSubmission(
            new ExecutionRunContext(run.RunId, run.Profile, run.ConfigHash, run.CodeVersion, run.StartedAtUtc),
            "MSFT",
            10m,
            "technical_exit",
            now);

        await Assert.ThrowsAsync<BrokerOrderRejectedException>(() =>
            service.SubmitPositionExitAsync(request, broker.Object, CancellationToken.None));
        var retry = await service.SubmitPositionExitAsync(request, broker.Object, CancellationToken.None);

        Assert.Equal("exit-order-2", retry.BrokerOrderId);
        await using var verification = new TradingFlowDbContext(options);
        var exitIntents = await verification.OrderIntents
            .AsNoTracking()
            .Where(intent => intent.Kind == OrderIntentKind.PositionExit)
            .OrderBy(intent => intent.SequenceNumber)
            .ToListAsync();
        Assert.Equal(2, exitIntents.Count);
        Assert.NotEqual(exitIntents[0].IntentId, exitIntents[1].IntentId);
        Assert.Equal(OrderState.Rejected, (await events.GetCurrentAsync(exitIntents[0].ClientOrderId))!.State);
        Assert.Equal(OrderState.Acked, (await events.GetCurrentAsync(exitIntents[1].ClientOrderId))!.State);
        broker.Verify(client => client.SubmitExitOrderAsync(
            It.IsAny<BrokerExitOrder>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task DispatchAsync_AccountMismatch_FailsBeforeBrokerLookupOrPost()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 21, 14, 31, 0, TimeSpan.Zero));
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        await InitializeAsync(options);
        var reserved = await intents.ReserveAsync(
            CreateRun(Guid.NewGuid()),
            CreateReservation(Guid.NewGuid(), "MSFT"));
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateAccountSnapshot("other-account", clock.GetUtcNow()));
        var dispatcher = CreateDispatcher(
            intents,
            events,
            clock,
            expectedAccountId: "paper-account");

        await Assert.ThrowsAsync<BrokerAccountMismatchException>(() =>
            dispatcher.DispatchAsync(reserved.Intent.IntentId, broker.Object));

        broker.Verify(client => client.GetOrderByClientOrderIdAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        broker.Verify(client => client.SubmitOrderAsync(
            It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(OrderState.Intent, (await events.GetCurrentAsync(reserved.Intent.ClientOrderId))!.State);
    }

    [Fact]
    public async Task DispatchAsync_CancelledRun_DoesNotPostFreshEntry()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 21, 14, 31, 0, TimeSpan.Zero));
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        await InitializeAsync(options);
        var reserved = await intents.ReserveAsync(
            CreateRun(Guid.NewGuid()),
            CreateReservation(Guid.NewGuid(), "MSFT"));
        await using (var context = new TradingFlowDbContext(options))
        {
            var run = await context.ProductionRuns.SingleAsync();
            run.Status = "cancelled";
            await context.SaveChangesAsync();
        }

        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateAccountSnapshot("paper-account", clock.GetUtcNow()));
        var dispatcher = CreateDispatcher(intents, events, clock);

        await Assert.ThrowsAsync<OrderDispatchPendingException>(() =>
            dispatcher.DispatchAsync(reserved.Intent.IntentId, broker.Object));

        broker.Verify(client => client.GetOrderByClientOrderIdAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        broker.Verify(client => client.SubmitOrderAsync(
            It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(OrderState.Intent, (await events.GetCurrentAsync(reserved.Intent.ClientOrderId))!.State);
    }

    // A stop committed after dispatch obtains ownership still fences its first POST.
    // A broker-confirmed order can be adopted even when the stop was just latched.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DispatchAsync_PaperStopDuringBrokerLookupFencesFirstPostButAllowsAdoption(bool adopt, bool rebuilt)
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        await InitializeAsync(options);
        var jobs = new SqliteJobRepository(factory);
        var run = CreateRun(Guid.NewGuid());
        var lease = await StartPaperAttemptAsync(jobs, run);
        var clock = new FixedTimeProvider(CandidateReservationTime);
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        var reserved = (await intents.ReserveAsync(run, CreateReservation(Guid.NewGuid(), "MSFT"))).Intent;
        var broker = CreateDispatchBroker(reserved, brokerOrder: null, "unused");
        broker.Setup(client => client.GetOrderByClientOrderIdAsync(reserved.ClientOrderId, It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken token) =>
            {
                if (rebuilt)
                {
                    await ContinueAfterRebuildAsync(jobs, run, lease, token);
                }
                else
                {
                    var stopped = DateTimeOffset.UtcNow;
                    Assert.True(await jobs.SavePaperEntryStopSnapshotAsync(run.RunId, lease.LeaseToken, "{}", stopped, stopped, token));
                }

                return adopt ? CreateMatchingBrokerOrder(reserved, "existing-at-broker", "new") : null;
            });

        var dispatcher = CreateDispatcher(intents, events, clock);
        if (adopt)
        {
            Assert.Equal("existing-at-broker", (await dispatcher.DispatchAsync(reserved.IntentId, broker.Object)).BrokerOrderId);
        }
        else
        {
            var blocked = await Assert.ThrowsAsync<PaperEntryDispatchBlockedException>(() =>
                dispatcher.DispatchAsync(reserved.IntentId, broker.Object));
            Assert.Equal(rebuilt ? PaperExitOnlyContinuation.EventType : "paper_pipeline_entries_stopped", blocked.ReasonCode);
        }

        var stored = await intents.GetByIntentIdAsync(reserved.IntentId);
        Assert.NotNull(stored);
        Assert.Equal(0, stored.DispatchAttemptCount);
        Assert.Equal(adopt ? OrderState.Acked : OrderState.Expired, (await events.GetCurrentAsync(reserved.ClientOrderId))!.State);
        broker.Verify(client => client.SubmitOrderAsync(It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Recovery may settle a zero-POST intent but cannot submit it for an abandoned owner.
    // An unrelated live paper job still dispatches normally.
    [Theory]
    [InlineData("expired")]
    [InlineData("released")]
    [InlineData("cancelled")]
    public async Task RecoverPendingAsync_PaperEntryRequiresItsOwnUncancelledLease(string ownerState)
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        await InitializeAsync(options);
        var jobs = new SqliteJobRepository(factory);
        var run = CreateRun(Guid.NewGuid());
        var lease = await StartPaperAttemptAsync(jobs, run);
        var peer = CreateRun(Guid.NewGuid());
        await StartPaperAttemptAsync(jobs, peer);
        var clock = new FixedTimeProvider(CandidateReservationTime);
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        var reserved = (await intents.ReserveAsync(run, CreateReservation(Guid.NewGuid(), "MSFT"))).Intent;
        var live = (await intents.ReserveAsync(peer, CreateReservation(Guid.NewGuid(), "AAPL"))).Intent;
        var submitted = await events.TransitionAsync(new OrderTransitionRequest(
            reserved.ClientOrderId, OrderState.Intent, OrderState.Submitted, "engine",
            CandidateReservationTime, PayloadJson: reserved.RequestJson));
        Assert.True(submitted.Applied);
        switch (ownerState)
        {
            case "expired":
                await using (var context = factory.CreateDbContext())
                {
                    await context.Jobs.Where(job => job.Id == run.RunId).ExecuteUpdateAsync(update =>
                        update.SetProperty(job => job.LeaseExpiresAtUtcTicks, 0L));
                }
                break;
            case "released":
                await jobs.ReleaseLeaseAsync(run.RunId, lease.LeaseToken, default);
                break;
            case "cancelled":
                Assert.True(await jobs.RequestCancellationAsync(run.RunId, DateTimeOffset.UtcNow, default));
                break;
        }

        var broker = CreateDispatchBroker(live, brokerOrder: null, "live-peer-entry");
        broker.Setup(client => client.GetOrderByClientOrderIdAsync(reserved.ClientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ActiveBrokerOrder?)null);
        var dispatcher = CreateDispatcher(intents, events, clock);

        Assert.Equal(1, await dispatcher.RecoverPendingAsync(broker.Object));
        Assert.Equal(OrderState.Expired, (await events.GetCurrentAsync(reserved.ClientOrderId))!.State);
        Assert.Equal(OrderState.Acked, (await events.GetCurrentAsync(live.ClientOrderId))!.State);
        Assert.Equal(0, (await intents.GetByIntentIdAsync(reserved.IntentId))!.DispatchAttemptCount);
        broker.Verify(client => client.SubmitOrderAsync(It.Is<BrokerEntryOrder>(entry => entry.Order.Ticker == "MSFT"),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // The same valid setup remains alive while its discovery membership can expire independently.
    [Theory]
    [InlineData("live")]
    [InlineData("expired")]
    [InlineData("removed")]
    [InlineData("unrelated-alternate")]
    [InlineData("recorded-alternate")]
    [InlineData("session-degraded")]
    [InlineData("legacy-degraded")]
    [InlineData("recovered")]
    public async Task RecoverPendingAsync_PaperDiscoveryRequiresCurrentTriggeredMembership(string sourceState)
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        await InitializeAsync(options);
        var run = CreateRun(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        var discovery = new SqliteDiscoveryRepository(factory);
        await discovery.CommitSnapshotAsync(new DiscoverySnapshotRequest(run.RunId, Guid.NewGuid(),
            DiscoverySourceKinds.Wishlist, "sql-test", "swing", now.AddSeconds(-1), now.AddMinutes(5), 0, [new("MSFT")]));
        string[]? sources = null;
        if (sourceState is "unrelated-alternate" or "recorded-alternate" or "session-degraded" or "legacy-degraded" or "recovered")
        {
            await discovery.CommitSnapshotAsync(new DiscoverySnapshotRequest(run.RunId, Guid.NewGuid(),
                DiscoverySourceKinds.Finviz, "alternate", "swing", now.AddSeconds(-1), now.AddMinutes(5), 0, [new("MSFT")]));
            sources = sourceState switch
            {
                "legacy-degraded" => null,
                "recorded-alternate" => ["wishlist:sql-test", "finviz:alternate"],
                _ => ["wishlist:sql-test"]
            };
        }
        var aggregate = Assert.Single(await discovery.GetActiveAsync(run.RunId, now));
        var evidence = System.Text.Encoding.UTF8.GetString(EvidenceCanonicalJson.SerializeToUtf8Bytes(new
        {
            discovery = new { aggregate.AggregateId, AggregateVersion = aggregate.Version, sources }
        }));
        var candidate = Guid.NewGuid();
        var hash = new string('f', 64);
        await SeedTriggeredCandidateAsync(options, run, candidate, "MSFT", hash, now.AddHours(1), evidence);
        var jobs = new SqliteJobRepository(factory);
        var lease = await StartPaperAttemptAsync(jobs, run);

        var clock = new FixedTimeProvider(CandidateReservationTime);
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        var reserved = (await intents.ReserveAsync(run,
            CreateCandidateReservation(Guid.NewGuid(), candidate, "MSFT", hash))).Intent;
        if (sourceState == "removed")
        {
            await discovery.CommitSnapshotAsync(new DiscoverySnapshotRequest(run.RunId, Guid.NewGuid(),
                DiscoverySourceKinds.Wishlist, "sql-test", "swing", now, now.AddMinutes(5), 1, []));
        }
        else if (sourceState == "expired")
        {
            // Advance just this source's deadline without changing the candidate's setup expiry.
            await using var context = factory.CreateDbContext();
            await context.DiscoverySourceMemberships.Where(source => source.AggregateId == aggregate.AggregateId)
                .ExecuteUpdateAsync(update => update.SetProperty(source => source.ExpiresAtUtc, now.AddSeconds(-1)));
        }

        if (sourceState is "unrelated-alternate" or "recorded-alternate" or "session-degraded" or "legacy-degraded" or "recovered")
        {
            var refreshHealth = new DiscoveryRefreshHealth(
                sourceState == "session-degraded" ? new DiscoveryRefreshFailure(now, "Source projection failed.") : null,
                [new DiscoverySourceRefreshFailure("wishlist", "sql-test", now, "Source capture failed.")]);
            Assert.True(await jobs.SaveSnapshotAsync(run.RunId, lease.LeaseToken,
                System.Text.Json.JsonSerializer.Serialize(new { paperPipelineHealth = new { discovery = refreshHealth } }),
                now, default));
            if (sourceState == "recovered")
            {
                refreshHealth = new DiscoveryRefreshHealth(null, []);
                Assert.True(await jobs.SaveSnapshotAsync(run.RunId, lease.LeaseToken,
                    System.Text.Json.JsonSerializer.Serialize(new { paperPipelineHealth = new { discovery = refreshHealth } }),
                    now, default));
            }
        }

        var broker = CreateDispatchBroker(reserved, brokerOrder: null, "fresh-membership-entry");
        var dispatcher = CreateDispatcher(intents, events, clock);

        var shouldPost = sourceState is "live" or "recorded-alternate" or "recovered";
        Assert.Equal(shouldPost ? 1 : 0, await dispatcher.RecoverPendingAsync(broker.Object));
        Assert.Equal(shouldPost ? OrderState.Acked : OrderState.Expired, (await events.GetCurrentAsync(reserved.ClientOrderId))!.State);
        broker.Verify(client => client.SubmitOrderAsync(It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()),
            shouldPost ? Times.Once() : Times.Never());
    }

    // Register a paper run with the same lifecycle and lease APIs used by production.
    private static async Task<DurableJobLease> StartPaperAttemptAsync(SqliteJobRepository jobs, ProductionRun run)
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(await jobs.TryEnqueueAsync(new PersistedJob
        {
            Id = run.RunId,
            JobType = "paper",
            RunName = $"dispatch-fixture-{run.RunId:N}",
            CreatedAt = now
        }, 10, default));
        var lease = await jobs.TryAcquireAsync(run.RunId, "dispatch-test-worker", now, TimeSpan.FromMinutes(1), default);
        Assert.NotNull(lease);
        Assert.NotNull(await jobs.StartPaperRunAsync(run, lease.LeaseToken, default));
        return lease;
    }

    // Move the paper run to a new lease and register a different executing build.
    private static async Task<PaperRunStart> ContinueAfterRebuildAsync(
        SqliteJobRepository jobs, ProductionRun run, DurableJobLease oldLease, CancellationToken token)
    {
        await jobs.ReleaseLeaseAsync(run.RunId, oldLease.LeaseToken, token);
        var lease = await jobs.TryAcquireAsync(run.RunId, "rebuilt-worker", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), token);
        Assert.NotNull(lease);
        var requested = CreateRun(run.RunId);
        requested.StartedAtUtc = run.StartedAtUtc;
        requested.CodeVersion = new string('f', 40);
        var continued = await jobs.StartPaperRunAsync(requested, lease.LeaseToken, token);
        Assert.NotNull(continued);
        return continued;
    }

    // Recheck the durable account breach after claiming an intent and before first broker POST.
    [Fact]
    public async Task DispatchAsync_AccountLossBreachFromAnotherRunFencesFirstBrokerPost()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        await InitializeAsync(options);
        var policy = new AccountLossGuardOptions("alpaca", new(2m, 4m, 10m), TimeSpan.FromMinutes(1));
        var clock = new FixedTimeProvider(CandidateReservationTime);
        var intents = new SqliteOrderIntentRepository(factory, clock, accountLossOptions: policy);
        var events = new SqliteOrderEventRepository(factory);
        var store = new SqliteAccountLossStateStore(factory);
        var scope = new AccountLossScope("alpaca", "paper", "paper-account");
        var run = CreateRun(Guid.NewGuid());
        await store.RecordAsync(scope, run, LossSample(100_000m, CandidateReservationTime.AddSeconds(-5)), policy, default);
        var reserved = (await intents.ReserveAsync(run, CreateReservation(Guid.NewGuid(), "MSFT"))).Intent;
        var observingRun = CreateRun(Guid.NewGuid());
        observingRun.Profile = "paper_shadow";
        var broker = CreateDispatchBroker(reserved, brokerOrder: null, "unused");
        broker.Setup(client => client.GetOrderByClientOrderIdAsync(reserved.ClientOrderId, It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken token) =>
            {
                await store.RecordAsync(scope, observingRun, LossSample(97_000m, CandidateReservationTime), policy, token);
                return (ActiveBrokerOrder?)null;
            });

        var dispatcher = CreateDispatcher(intents, events, clock);
        var rejection = await Assert.ThrowsAsync<PortfolioRiskReservationRejectedException>(() =>
            dispatcher.DispatchAsync(reserved.IntentId, broker.Object));

        Assert.Equal("account_loss_entry_blocked", rejection.ReasonCode);
        Assert.Equal(OrderState.Expired, (await events.GetCurrentAsync(reserved.ClientOrderId))!.State);
        Assert.Equal(0, (await intents.GetByIntentIdAsync(reserved.IntentId))!.DispatchAttemptCount);
        broker.Verify(client => client.SubmitOrderAsync(It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()), Times.Never);
        await using var context = factory.CreateDbContext();
        var breach = Assert.Single(await context.RiskEvents.Where(item => item.EventType == "account_daily_loss_breach").ToArrayAsync());
        Assert.Equal(3m, breach.ObservedValue);
        Assert.Equal(2m, breach.LimitValue);
        Assert.Equal(observingRun.RunId, breach.RunId);
    }

    // Reopening the store and providing a profitable later observation keeps the day's latch shared across runs.
    [Fact]
    public async Task ReserveAsync_AccountLossLatchSurvivesRestartAndSpansRunsOnlyInItsAccount()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        await InitializeAsync(options);
        var policy = new AccountLossGuardOptions("alpaca", new(2m, 4m, 10m), TimeSpan.FromMinutes(1));
        var scope = new AccountLossScope("alpaca", "paper", "paper-account");
        var breachedRun = CreateRun(Guid.NewGuid());
        await new SqliteAccountLossStateStore(factory).RecordAsync(scope, breachedRun,
            LossSample(97_000m, CandidateReservationTime.AddSeconds(-2)), policy, default);
        var restarted = new SqliteAccountLossStateStore(factory);
        var peer = CreateRun(Guid.NewGuid());
        peer.Profile = "paper_shadow";
        var current = await restarted.RecordAsync(scope, peer, LossSample(102_000m, CandidateReservationTime), policy, default);
        Assert.NotNull(current.DailyBreach);
        Assert.Null(await restarted.GetAsync(scope with { AccountId = "other-account" }, default));
        Assert.Null(await restarted.GetAsync(scope with { Mode = "live" }, default));

        var intents = new SqliteOrderIntentRepository(factory, new FixedTimeProvider(CandidateReservationTime), accountLossOptions: policy);
        var rejection = await Assert.ThrowsAsync<PortfolioRiskReservationRejectedException>(() =>
            intents.ReserveAsync(peer, CreateReservation(Guid.NewGuid(), "AAPL")));
        Assert.Equal("account_loss_entry_blocked", rejection.ReasonCode);
        await using var context = factory.CreateDbContext();
        Assert.Empty(await context.OrderIntents.ToArrayAsync());
        Assert.Single(await context.RiskEvents.Where(item => item.EventType == "account_daily_loss_breach").ToArrayAsync());
    }

    // A linked or ambiguous cancellation survives a healthy new session until proved terminal.
    [Fact]
    public async Task AccountLossCancelReview_PersistsOnceAcrossRestartAndSessionReset()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        await InitializeAsync(options);
        var limits = new AccountLossGuardOptions("alpaca", new(2m, 4m, 10m), TimeSpan.FromMinutes(1));
        var scope = new AccountLossScope("alpaca", "paper", "paper-account");
        var run = CreateRun(Guid.NewGuid());
        var store = new SqliteAccountLossStateStore(factory);
        await store.RecordAsync(scope, run, LossSample(97_000m, CandidateReservationTime), limits, default);
        var review = new AccountLossCancellationReview("owned-bracket", "broker-order", "MSFT", 1m, CandidateReservationTime);
        await store.SetCancellationReviewAsync(scope, run, review, true, default);
        await store.SetCancellationReviewAsync(scope, run, review, true, default);

        var restarted = new SqliteAccountLossStateStore(factory);
        var sample = LossSample(100_000m, CandidateReservationTime.AddDays(1));
        var priorEvidence = sample.Evidence!;
        sample = sample with { Evidence = priorEvidence with { Session = priorEvidence.Session.AddDays(1) } };
        var state = await restarted.RecordAsync(scope, run, sample, limits, default);
        Assert.False(state.Breached);
        Assert.Single(state.CancellationReviews!);
        Assert.NotNull(AccountLossLimitPolicy.Refusal(state, limits, sample.Evidence!.Session, sample.ObservedAtUtc));

        Assert.True(await restarted.CompleteCancellationSweepAsync(scope, state.CancellationSweepGeneration, default));
        await restarted.SetCancellationReviewAsync(scope, run, review, false, default);
        var cleared = await restarted.GetAsync(scope, default);
        Assert.Null(AccountLossLimitPolicy.Refusal(cleared, limits, sample.Evidence!.Session, sample.ObservedAtUtc));
        await using var context = factory.CreateDbContext();
        Assert.Single(await context.RiskEvents.Where(item => item.EventType == "account_loss_cancel_review_required").ToArrayAsync());
        Assert.Single(await context.RiskEvents.Where(item => item.EventType == "account_loss_cancel_review_resolved").ToArrayAsync());
    }

    // A process can restart after the daily latch rolls over and before it ever lists broker orders.
    [Fact]
    public async Task AccountLossSweep_SurvivesRestartBeforeFirstSweepAndFencesOlderCompletion()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        await InitializeAsync(options);
        var limits = new AccountLossGuardOptions("alpaca", new(2m, 4m, 10m), TimeSpan.FromMinutes(1));
        var scope = new AccountLossScope("alpaca", "paper", "paper-account");
        var run = CreateRun(Guid.NewGuid());
        var original = new SqliteAccountLossStateStore(factory);
        var first = await original.RecordAsync(scope, run, LossSample(97_000m, CandidateReservationTime), limits, default);
        Assert.True(first.CancellationSweepPending);

        var restarted = new SqliteAccountLossStateStore(factory);
        var next = LossSampleNextSession(100_000m);
        var reset = await restarted.RecordAsync(scope, run, next, limits, default);
        Assert.False(reset.Breached);
        Assert.True(reset.CancellationSweepPending);
        Assert.Contains("sweep", AccountLossLimitPolicy.Refusal(reset, limits, next.Evidence!.Session, next.ObservedAtUtc));

        var concurrent = LossSampleNextSession(97_000m, secondsLater: 1);
        var second = await restarted.RecordAsync(scope, run, concurrent, limits, default);
        Assert.True(second.CancellationSweepGeneration > first.CancellationSweepGeneration);
        Assert.False(await restarted.CompleteCancellationSweepAsync(scope, first.CancellationSweepGeneration, default));
        Assert.True(await restarted.CompleteCancellationSweepAsync(scope, second.CancellationSweepGeneration, default));

        var sweepComplete = await restarted.GetAsync(scope, default);
        Assert.NotNull(sweepComplete);
        Assert.False(sweepComplete.CancellationSweepPending);
        Assert.NotNull(sweepComplete.DailyBreach);
    }

    // A previously recorded first POST might become visible after the fresh broker snapshot.
    [Fact]
    public async Task AccountLossSweep_UnknownBrokerAttemptPreventsCompletionUntilBrokerReconciles()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        await InitializeAsync(options);
        var limits = new AccountLossGuardOptions("alpaca", new(2m, 4m, 10m), TimeSpan.FromMinutes(1));
        var scope = new AccountLossScope("alpaca", "paper", "paper-account");
        var run = CreateRun(Guid.NewGuid());
        var intents = new SqliteOrderIntentRepository(factory, new FixedTimeProvider(CandidateReservationTime));
        var events = new SqliteOrderEventRepository(factory);
        var reserved = (await intents.ReserveAsync(run, CreateReservation(Guid.NewGuid(), "MSFT"))).Intent;
        var lease = await intents.TryAcquireDispatchLeaseAsync(reserved.IntentId, scope.AccountId, "sweep-fixture",
            TimeSpan.FromMinutes(1), default);
        Assert.NotNull(lease);
        Assert.True((await events.TransitionAsync(new OrderTransitionRequest(
            reserved.ClientOrderId, OrderState.Intent, OrderState.Submitted, "engine",
            CandidateReservationTime, PayloadJson: reserved.RequestJson))).Applied);
        await intents.RecordDispatchAttemptAsync(reserved.IntentId, lease.LeaseToken, default);

        var original = new SqliteAccountLossStateStore(factory);
        var breached = await original.RecordAsync(scope, run, LossSample(97_000m, CandidateReservationTime), limits, default);
        var restarted = new SqliteAccountLossStateStore(factory);
        var next = LossSampleNextSession(100_000m);
        await restarted.RecordAsync(scope, run, next, limits, default);
        Assert.False(await restarted.CompleteCancellationSweepAsync(scope, breached.CancellationSweepGeneration, default));

        Assert.True((await events.TransitionAsync(new OrderTransitionRequest(
            reserved.ClientOrderId, OrderState.Submitted, OrderState.Rejected, "broker_rest",
            next.ObservedAtUtc, next.ObservedAtUtc, "broker-rejected-order"))).Applied);
        Assert.True(await restarted.CompleteCancellationSweepAsync(scope, breached.CancellationSweepGeneration, default));
        Assert.Null(AccountLossLimitPolicy.Refusal(await restarted.GetAsync(scope, default), limits,
            next.Evidence!.Session, next.ObservedAtUtc));
    }

    // Advance the evidence to the following synthetic exchange session.
    private static AccountLossSample LossSampleNextSession(decimal equity, int secondsLater = 0)
    {
        var sample = LossSample(equity, CandidateReservationTime.AddDays(1).AddSeconds(secondsLater));
        var evidence = sample.Evidence!;
        var newPrior = evidence.PriorClose with
        {
            Session = evidence.Session,
            CloseUtc = evidence.PriorClose.CloseUtc.AddDays(1)
        };
        return sample with
        {
            Evidence = evidence with
            {
                Session = evidence.Session.AddDays(1),
                PriorClose = newPrior,
                CompletedSessions = [evidence.WeekAnchor, evidence.PriorClose, newPrior]
            }
        };
    }

    // Keep loss data on the same synthetic exchange date as the order and exposure fixtures.
    private static AccountLossSample LossSample(decimal equity, DateTimeOffset observedAt)
    {
        var previous = new AccountClosingEquity(new DateOnly(2026, 7, 20),
            new DateTimeOffset(2026, 7, 20, 20, 0, 0, TimeSpan.Zero), 100_000m);
        var weekly = new AccountClosingEquity(new DateOnly(2026, 7, 17),
            new DateTimeOffset(2026, 7, 17, 20, 0, 0, TimeSpan.Zero), 100_000m);
        return new AccountLossSample(observedAt.AddSeconds(-1), observedAt, equity,
            new AccountLossEvidence(new DateOnly(2026, 7, 21), previous, weekly, [weekly, previous], null), null);
    }

    [Fact]
    public async Task DispatchAsync_DefinitiveBrokerRejection_ReleasesEntryRisk()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 21, 14, 31, 0, TimeSpan.Zero));
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        await InitializeAsync(options);
        var reserved = await intents.ReserveAsync(
            CreateRun(Guid.NewGuid()),
            CreateReservation(Guid.NewGuid(), "MSFT"));
        var broker = CreateDispatchBroker(reserved.Intent, brokerOrder: null, "unused");
        broker.Setup(client => client.SubmitOrderAsync(
                It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BrokerOrderRejectedException(
                "submit-entry", 422, "The broker rejected the immutable order contract."));
        var dispatcher = CreateDispatcher(intents, events, clock);

        await Assert.ThrowsAsync<BrokerOrderRejectedException>(() =>
            dispatcher.DispatchAsync(reserved.Intent.IntentId, broker.Object));

        Assert.Equal(OrderState.Rejected, (await events.GetCurrentAsync(reserved.Intent.ClientOrderId))!.State);
        Assert.Equal(
            PortfolioRiskReservationState.Released,
            (await intents.GetRiskReservationByIntentIdAsync(reserved.Intent.IntentId))!.State);
        broker.Verify(client => client.SubmitOrderAsync(
            It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_AmbiguousBrokerFailure_RemainsSubmittedAndLaterAdopts()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 21, 14, 31, 0, TimeSpan.Zero));
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        await InitializeAsync(options);
        var reserved = await intents.ReserveAsync(
            CreateRun(Guid.NewGuid()),
            CreateReservation(Guid.NewGuid(), "MSFT"));
        var adopted = CreateMatchingBrokerOrder(reserved.Intent, "broker-accepted", "new");
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateAccountSnapshot("paper-account", clock.GetUtcNow()));
        broker.SetupSequence(client => client.GetOrderByClientOrderIdAsync(
                reserved.Intent.ClientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ActiveBrokerOrder?)null)
            .ReturnsAsync((ActiveBrokerOrder?)null)
            .ReturnsAsync(adopted);
        broker.Setup(client => client.SubmitOrderAsync(
                It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection closed after request transmission."));
        var dispatcher = CreateDispatcher(intents, events, clock);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            dispatcher.DispatchAsync(reserved.Intent.IntentId, broker.Object));
        Assert.Equal(OrderState.Submitted, (await events.GetCurrentAsync(reserved.Intent.ClientOrderId))!.State);

        var result = await dispatcher.DispatchAsync(reserved.Intent.IntentId, broker.Object);

        Assert.Equal("broker-accepted", result.BrokerOrderId);
        Assert.Equal(OrderState.Acked, (await events.GetCurrentAsync(reserved.Intent.ClientOrderId))!.State);
        broker.Verify(client => client.SubmitOrderAsync(
            It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_ContractMismatch_IsNeverAdoptedOrPosted()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 21, 14, 31, 0, TimeSpan.Zero));
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        await InitializeAsync(options);
        var reserved = await intents.ReserveAsync(
            CreateRun(Guid.NewGuid()),
            CreateReservation(Guid.NewGuid(), "MSFT"));
        var mismatch = CreateMatchingBrokerOrder(reserved.Intent, "broker-wrong-contract", "new") with
        {
            LimitPrice = 999m
        };
        var broker = CreateDispatchBroker(reserved.Intent, mismatch, "must-not-post");
        var dispatcher = CreateDispatcher(intents, events, clock);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dispatcher.DispatchAsync(reserved.Intent.IntentId, broker.Object));

        Assert.Contains("does not match owned intent", exception.Message, StringComparison.Ordinal);
        Assert.Equal(OrderState.Intent, (await events.GetCurrentAsync(reserved.Intent.ClientOrderId))!.State);
        broker.Verify(client => client.SubmitOrderAsync(
            It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SubmitPositionExitAsync_PositionGenerationChangesBeforePost_FailsClosed()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var run = CreateRun(Guid.NewGuid());
        run.StartedAtUtc = now.AddMinutes(-30);
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        var positions = new SqlitePositionLedgerRepository(factory);
        await InitializeAsync(options);
        await SeedPositionAsync(positions, run, "paper-account", "MSFT", 10m, now.AddMinutes(-5));
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateAccountSnapshot("paper-account", now));
        broker.Setup(client => client.GetSessionAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TradingSessionSnapshot(
                DateOnly.FromDateTime(now.UtcDateTime),
                EquityTradingSession.Regular,
                now,
                now.AddHours(-1),
                now.AddHours(5)));
        var changed = false;
        broker.Setup(client => client.GetOrderByClientOrderIdAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                if (!changed)
                {
                    changed = true;
                    await positions.AppendFillAsync(new PositionFillAppendRequest(
                        run, "paper-account", "MSFT", "SWGA", 0m, 10m, 99m, "sell",
                        "exit-msft-1", "exit-client-msft-1", "exit-fill-msft-1",
                        "broker_rest", now.AddMinutes(-1), now.AddMinutes(-1), "{}"));
                    await positions.AppendFillAsync(new PositionFillAppendRequest(
                        run, "paper-account", "MSFT", "SWGA", 10m, 10m, 101m, "buy",
                        "entry-msft-2", "entry-client-msft-2", "entry-fill-msft-2",
                        "broker_rest", now.AddSeconds(-30), now.AddSeconds(-30), "{}"));
                }

                return null;
            });
        broker.Setup(client => client.GetOpenPositionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new BrokerPosition("MSFT", "long", 10m, 100m, 100m, 0m)]);
        var dispatcher = CreateDispatcher(intents, events, clock, positions);
        var service = new OrderSubmissionService(
            intents,
            dispatcher,
            new Mock<IEntryGateChain>(MockBehavior.Strict).Object,
            NullLogger<OrderSubmissionService>.Instance,
            positions,
            events: events,
            lifecycle: new OrderLifecycleService(events, clock),
            timeProvider: clock);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SubmitPositionExitAsync(
                new PositionExitSubmission(
                    new ExecutionRunContext(run.RunId, run.Profile, run.ConfigHash, run.CodeVersion, run.StartedAtUtc),
                    "MSFT", 10m, "technical_exit", now),
                broker.Object,
                CancellationToken.None));

        Assert.Contains("stale", exception.Message, StringComparison.OrdinalIgnoreCase);
        broker.Verify(client => client.SubmitExitOrderAsync(
            It.IsAny<BrokerExitOrder>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SubmitPositionExitAsync_AccountBindingMismatch_FailsBeforeIntentReservation()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var run = CreateRun(Guid.NewGuid());
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        var positions = new SqlitePositionLedgerRepository(factory);
        await InitializeAsync(options);
        await SeedPositionAsync(positions, run, "paper-account", "MSFT", 10m, now.AddMinutes(-5));
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateAccountSnapshot("other-account", now));
        var service = new OrderSubmissionService(
            intents,
            CreateDispatcher(intents, events, clock, positions),
            new Mock<IEntryGateChain>(MockBehavior.Strict).Object,
            NullLogger<OrderSubmissionService>.Instance,
            positions,
            events: events,
            lifecycle: new OrderLifecycleService(events, clock),
            accountBinding: new BrokerAccountBindingService(
                new BrokerAccountBindingOptions("paper-account")),
            timeProvider: clock);

        await Assert.ThrowsAsync<BrokerAccountMismatchException>(() =>
            service.SubmitPositionExitAsync(
                new PositionExitSubmission(
                    new ExecutionRunContext(run.RunId, run.Profile, run.ConfigHash, run.CodeVersion, run.StartedAtUtc),
                    "MSFT", 10m, "technical_exit", now),
                broker.Object,
                CancellationToken.None));

        await using var verification = new TradingFlowDbContext(options);
        Assert.Empty(await verification.OrderIntents.AsNoTracking().ToListAsync());
        broker.Verify(client => client.SubmitExitOrderAsync(
            It.IsAny<BrokerExitOrder>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SubmitPositionExitAsync_ExtendedHours_RequiresFreshQuoteAndPreservesExplicitLimit()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var now = new DateTimeOffset(2026, 7, 21, 21, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var run = CreateRun(Guid.NewGuid());
        run.StartedAtUtc = now.AddMinutes(-30);
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        var positions = new SqlitePositionLedgerRepository(factory);
        await InitializeAsync(options);
        await SeedPositionAsync(positions, run, "paper-account", "MSFT", 10m, now.AddMinutes(-5));
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateAccountSnapshot("paper-account", now));
        broker.Setup(client => client.GetSessionAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TradingSessionSnapshot(
                new DateOnly(2026, 7, 21), EquityTradingSession.AfterHours, now,
                now.AddHours(-7.5), now.AddHours(-1)));
        broker.Setup(client => client.GetEligibilityAsync("MSFT", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssetTradingEligibility("MSFT", true, true, true, now));
        broker.As<IBrokerMarketObservationProvider>()
            .Setup(provider => provider.GetMarketObservationAsync(
                "MSFT", EquityTradingSession.AfterHours, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrokerMarketObservation(
                "MSFT", "sip", 99m, 100m, now.AddMilliseconds(-250), 99.50m,
                now.AddMilliseconds(-200), now));
        broker.Setup(client => client.GetOrderByClientOrderIdAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ActiveBrokerOrder?)null);
        broker.Setup(client => client.SubmitExitOrderAsync(
                It.IsAny<BrokerExitOrder>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrokerOrderReceipt("extended-exit", now));
        broker.Setup(client => client.GetOpenPositionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new BrokerPosition("MSFT", "long", 10m, 100m, 100m, 0m)]);
        var service = new OrderSubmissionService(
            intents,
            CreateDispatcher(intents, events, clock, positions),
            new Mock<IEntryGateChain>(MockBehavior.Strict).Object,
            NullLogger<OrderSubmissionService>.Instance,
            positions,
            new PositionExitExecutionOptions(2_000),
            events: events,
            lifecycle: new OrderLifecycleService(events, clock),
            accountBinding: new BrokerAccountBindingService(
                new BrokerAccountBindingOptions("paper-account")),
            timeProvider: clock);

        await service.SubmitPositionExitAsync(
            new PositionExitSubmission(
                new ExecutionRunContext(run.RunId, run.Profile, run.ConfigHash, run.CodeVersion, run.StartedAtUtc),
                "MSFT", 10m, "technical_exit", now,
                AllowExtendedHoursTrading: true,
                RequestedOrderType: "limit",
                RequestedTimeInForce: "day",
                RequestedLimitPrice: 98.75m),
            broker.Object,
            CancellationToken.None);

        broker.Verify(client => client.SubmitExitOrderAsync(
            It.Is<BrokerExitOrder>(order =>
                order.OrderType == "limit" &&
                order.TimeInForce == "day" &&
                order.LimitPrice == 98.75m &&
                order.SubmitOutsideRegularHours),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SubmitPositionExitAsync_ExtendedHoursStaleQuote_FailsBeforeIntentReservation()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var now = new DateTimeOffset(2026, 7, 21, 21, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var run = CreateRun(Guid.NewGuid());
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        var positions = new SqlitePositionLedgerRepository(factory);
        await InitializeAsync(options);
        await SeedPositionAsync(positions, run, "paper-account", "MSFT", 10m, now.AddMinutes(-5));
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateAccountSnapshot("paper-account", now));
        broker.Setup(client => client.GetSessionAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TradingSessionSnapshot(
                new DateOnly(2026, 7, 21), EquityTradingSession.AfterHours, now,
                now.AddHours(-7.5), now.AddHours(-1)));
        broker.Setup(client => client.GetEligibilityAsync("MSFT", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssetTradingEligibility("MSFT", true, true, true, now));
        broker.As<IBrokerMarketObservationProvider>()
            .Setup(provider => provider.GetMarketObservationAsync(
                "MSFT", EquityTradingSession.AfterHours, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrokerMarketObservation(
                "MSFT", "sip", 99m, 100m, now.AddSeconds(-3), 99.50m,
                now.AddSeconds(-3), now));
        var service = new OrderSubmissionService(
            intents,
            CreateDispatcher(intents, events, clock, positions),
            new Mock<IEntryGateChain>(MockBehavior.Strict).Object,
            NullLogger<OrderSubmissionService>.Instance,
            positions,
            new PositionExitExecutionOptions(2_000),
            events: events,
            lifecycle: new OrderLifecycleService(events, clock),
            accountBinding: new BrokerAccountBindingService(
                new BrokerAccountBindingOptions("paper-account")),
            timeProvider: clock);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SubmitPositionExitAsync(
                new PositionExitSubmission(
                    new ExecutionRunContext(run.RunId, run.Profile, run.ConfigHash, run.CodeVersion, run.StartedAtUtc),
                    "MSFT", 10m, "technical_exit", now,
                    AllowExtendedHoursTrading: true),
                broker.Object,
                CancellationToken.None));

        Assert.Contains("missing or stale", error.Message, StringComparison.Ordinal);
        await using var verification = new TradingFlowDbContext(options);
        Assert.Empty(await verification.OrderIntents.AsNoTracking().ToListAsync());
        broker.Verify(client => client.SubmitExitOrderAsync(
            It.IsAny<BrokerExitOrder>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SubmitPositionExitAsync_ConcurrentRuns_ReserveClosableQuantityOnce()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var owningRun = CreateRun(Guid.NewGuid());
        owningRun.StartedAtUtc = now.AddHours(-1);
        var competingRun = CreateRun(Guid.NewGuid());
        competingRun.StartedAtUtc = now.AddMinutes(-30);
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        var positions = new SqlitePositionLedgerRepository(factory);
        await InitializeAsync(options);
        await SeedPositionAsync(
            positions, owningRun, "paper-account", "MSFT", 10m, now.AddMinutes(-5));
        var broker = CreateExitBroker("paper-account", now, "single-exit-order");
        var service = new OrderSubmissionService(
            intents,
            CreateDispatcher(intents, events, clock, positions),
            new Mock<IEntryGateChain>(MockBehavior.Strict).Object,
            NullLogger<OrderSubmissionService>.Instance,
            positions,
            events: events,
            lifecycle: new OrderLifecycleService(events, clock),
            accountBinding: new BrokerAccountBindingService(
                new BrokerAccountBindingOptions("paper-account")),
            timeProvider: clock);
        PositionExitSubmission CreateExit(ProductionRun run) => new(
            new ExecutionRunContext(
                run.RunId, run.Profile, run.ConfigHash, run.CodeVersion, run.StartedAtUtc),
            "MSFT", 10m, "technical_exit", now);

        var outcomes = await Task.WhenAll(
            CaptureExitAsync(service, CreateExit(owningRun), broker.Object),
            CaptureExitAsync(service, CreateExit(competingRun), broker.Object));

        Assert.Single(outcomes, outcome => outcome is OrderSubmissionResult);
        Assert.Single(outcomes, outcome => outcome is PositionExitReservationRejectedException);
        broker.Verify(client => client.SubmitExitOrderAsync(
            It.IsAny<BrokerExitOrder>(), It.IsAny<CancellationToken>()), Times.Once);
        await using var verification = new TradingFlowDbContext(options);
        var exit = Assert.Single(await verification.OrderIntents
            .AsNoTracking()
            .Where(intent => intent.Kind == OrderIntentKind.PositionExit)
            .ToListAsync());
        Assert.True(exit.PositionGenerationEventId > 0);
    }

    [Fact]
    public async Task SubmitPositionExitAsync_BrokerPositionMismatch_ReleasesExitReservation()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var run = CreateRun(Guid.NewGuid());
        run.StartedAtUtc = now.AddMinutes(-30);
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        var positions = new SqlitePositionLedgerRepository(factory);
        await InitializeAsync(options);
        var position = await SeedPositionAsync(
            positions, run, "paper-account", "MSFT", 10m, now.AddMinutes(-5));
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateAccountSnapshot("paper-account", now));
        broker.Setup(client => client.GetSessionAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TradingSessionSnapshot(
                DateOnly.FromDateTime(now.UtcDateTime),
                EquityTradingSession.Regular,
                now,
                now.AddHours(-1),
                now.AddHours(5)));
        broker.Setup(client => client.GetOpenPositionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new BrokerPosition("MSFT", "long", 9m, 100m, 100m, 0m)]);
        var service = new OrderSubmissionService(
            intents,
            CreateDispatcher(intents, events, clock, positions),
            new Mock<IEntryGateChain>(MockBehavior.Strict).Object,
            NullLogger<OrderSubmissionService>.Instance,
            positions,
            events: events,
            lifecycle: new OrderLifecycleService(events, clock),
            accountBinding: new BrokerAccountBindingService(
                new BrokerAccountBindingOptions("paper-account")),
            timeProvider: clock);

        await Assert.ThrowsAsync<PositionExitReconciliationRequiredException>(() =>
            service.SubmitPositionExitAsync(
                new PositionExitSubmission(
                    new ExecutionRunContext(
                        run.RunId, run.Profile, run.ConfigHash, run.CodeVersion, run.StartedAtUtc),
                    "MSFT", 10m, "technical_exit", now),
                broker.Object,
                CancellationToken.None));

        await using var verification = new TradingFlowDbContext(options);
        var exit = Assert.Single(await verification.OrderIntents
            .AsNoTracking()
            .Where(candidate => candidate.Kind == OrderIntentKind.PositionExit)
            .ToListAsync());
        Assert.Equal(OrderState.Expired, (await events.GetCurrentAsync(exit.ClientOrderId))!.State);
        Assert.False(await intents.HasActivePositionExitAsync(
            "paper-account", "MSFT", position.PositionGenerationEventId));
        broker.Verify(client => client.SubmitExitOrderAsync(
            It.IsAny<BrokerExitOrder>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SubmitPositionExitAsync_BrokerRejection_RestoresCanceledProtection()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var run = CreateRun(Guid.NewGuid());
        run.StartedAtUtc = now.AddMinutes(-30);
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        var positions = new SqlitePositionLedgerRepository(factory);
        await InitializeAsync(options);
        var position = await SeedPositionAsync(
            positions, run, "paper-account", "MSFT", 10m, now.AddMinutes(-5));
        var protective = await SeedActiveProtectionAsync(
            intents, events, run, position, 10m, 98m, now);
        var originalBrokerStop = CreateProtectiveBrokerOrder(
            "broker-stop-1", protective.ClientOrderId, "MSFT", 98m, now);
        var protectionCanceled = false;
        var broker = CreateProtectedExitBroker(
            now,
            protective,
            originalBrokerStop,
            () => protectionCanceled,
            () => protectionCanceled = true,
            10m);
        broker.Setup(client => client.SubmitExitOrderAsync(
                It.IsAny<BrokerExitOrder>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BrokerOrderRejectedException(
                "submit-exit", 422, "broker rejected closing order"));
        ProtectiveStopOrder? restored = null;
        broker.Setup(client => client.SubmitProtectiveStopAsync(
                It.IsAny<ProtectiveStopOrder>(), It.IsAny<CancellationToken>()))
            .Callback<ProtectiveStopOrder, CancellationToken>((order, _) => restored = order)
            .ReturnsAsync(new BrokerOrderReceipt("broker-stop-2", now.AddSeconds(2)));
        var service = CreateProtectedExitService(
            intents, events, positions, clock);

        await Assert.ThrowsAsync<BrokerOrderRejectedException>(() =>
            service.SubmitPositionExitAsync(
                CreatePositionExit(run, now),
                broker.Object,
                CancellationToken.None));

        Assert.True(protectionCanceled);
        Assert.NotNull(restored);
        Assert.Equal(10m, restored!.Quantity);
        Assert.Equal(98m, restored.StopPrice);
        Assert.Equal("gtc", restored.TimeInForce);
        await using var verification = new TradingFlowDbContext(options);
        var protectionIntents = await verification.OrderIntents
            .AsNoTracking()
            .Where(intent => intent.Kind == OrderIntentKind.ProtectiveStop)
            .OrderBy(intent => intent.SequenceNumber)
            .ToListAsync();
        Assert.Equal(2, protectionIntents.Count);
        Assert.Equal(OrderState.Canceled, (await events.GetCurrentAsync(protective.ClientOrderId))!.State);
        Assert.Equal(OrderState.Acked, (await events.GetCurrentAsync(protectionIntents[1].ClientOrderId))!.State);
    }

    [Fact]
    public async Task SubmitPositionExitAsync_AttemptedExitNotYetVisible_DoesNotCreateCompetingStop()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var run = CreateRun(Guid.NewGuid());
        run.StartedAtUtc = now.AddMinutes(-30);
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        var positions = new SqlitePositionLedgerRepository(factory);
        await InitializeAsync(options);
        var position = await SeedPositionAsync(
            positions, run, "paper-account", "MSFT", 10m, now.AddMinutes(-5));
        var protective = await SeedActiveProtectionAsync(
            intents, events, run, position, 10m, 98m, now);
        var originalBrokerStop = CreateProtectiveBrokerOrder(
            "broker-stop-1", protective.ClientOrderId, "MSFT", 98m, now);
        var protectionCanceled = false;
        var broker = CreateProtectedExitBroker(
            now,
            protective,
            originalBrokerStop,
            () => protectionCanceled,
            () => protectionCanceled = true,
            10m);
        broker.Setup(client => client.SubmitExitOrderAsync(
                It.IsAny<BrokerExitOrder>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("broker response was lost"));
        var admission = new EntryAdmissionControl();
        var service = CreateProtectedExitService(
            intents, events, positions, clock, admission);

        var error = await Assert.ThrowsAsync<PositionExitProtectionRestoreFailedException>(() =>
            service.SubmitPositionExitAsync(
                CreatePositionExit(run, now),
                broker.Object,
                CancellationToken.None));

        Assert.IsType<TimeoutException>(error.ExitFailure);
        Assert.Contains("ambiguous", error.RestoreFailure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(protectionCanceled);
        Assert.Contains(
            admission.GetSnapshot().Blocks,
            block => block.Code == "POSITION_EXIT_PROTECTION_RESTORE_FAILED");
        broker.Verify(client => client.SubmitProtectiveStopAsync(
            It.IsAny<ProtectiveStopOrder>(), It.IsAny<CancellationToken>()), Times.Never);
        var exit = (await intents.ListByRunAsync(run.RunId))
            .Single(intent => intent.Kind == OrderIntentKind.PositionExit);
        Assert.Equal(1, exit.DispatchAttemptCount);
        Assert.Equal(OrderState.Submitted, (await events.GetCurrentAsync(exit.ClientOrderId))!.State);
    }

    [Fact]
    public async Task SubmitPositionExitAsync_LaterBrokerRejection_IsJournaledBeforeProtectionRestore()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var run = CreateRun(Guid.NewGuid());
        run.StartedAtUtc = now.AddMinutes(-30);
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        var positions = new SqlitePositionLedgerRepository(factory);
        await InitializeAsync(options);
        var position = await SeedPositionAsync(
            positions, run, "paper-account", "MSFT", 10m, now.AddMinutes(-5));
        var protective = await SeedActiveProtectionAsync(
            intents, events, run, position, 10m, 98m, now);
        var originalBrokerStop = CreateProtectiveBrokerOrder(
            "broker-stop-1", protective.ClientOrderId, "MSFT", 98m, now);
        var protectionCanceled = false;
        var exitClientOrderId = String.Empty;
        var exitLookupCount = 0;
        var broker = CreateProtectedExitBroker(
            now,
            protective,
            originalBrokerStop,
            () => protectionCanceled,
            () => protectionCanceled = true,
            10m);
        broker.Setup(client => client.SubmitExitOrderAsync(
                It.IsAny<BrokerExitOrder>(), It.IsAny<CancellationToken>()))
            .Callback<BrokerExitOrder, CancellationToken>((order, _) =>
                exitClientOrderId = order.ClientOrderId)
            .ThrowsAsync(new TimeoutException("broker response was lost"));
        broker.Setup(client => client.GetOrderByClientOrderIdAsync(
                It.Is<string>(clientOrderId => clientOrderId != protective.ClientOrderId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string clientOrderId, CancellationToken _) =>
            {
                if (clientOrderId != exitClientOrderId || String.IsNullOrWhiteSpace(exitClientOrderId))
                {
                    return null;
                }

                exitLookupCount++;
                return exitLookupCount == 1
                    ? null
                    : new ActiveBrokerOrder(
                        "exit-rejected", "MSFT", "sell", "rejected", "market", null, null,
                        10m, now.AddSeconds(1), clientOrderId, 0m, null, now.AddSeconds(1),
                        TimeInForce: "day", OrderClass: "simple", ExtendedHours: false);
            });
        ProtectiveStopOrder? restored = null;
        broker.Setup(client => client.SubmitProtectiveStopAsync(
                It.IsAny<ProtectiveStopOrder>(), It.IsAny<CancellationToken>()))
            .Callback<ProtectiveStopOrder, CancellationToken>((order, _) => restored = order)
            .ReturnsAsync(new BrokerOrderReceipt("broker-stop-2", now.AddSeconds(2)));
        var service = CreateProtectedExitService(
            intents, events, positions, clock);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            service.SubmitPositionExitAsync(
                CreatePositionExit(run, now),
                broker.Object,
                CancellationToken.None));

        Assert.True(exitLookupCount >= 2);
        Assert.NotNull(restored);
        var exit = (await intents.ListByRunAsync(run.RunId))
            .Single(intent => intent.Kind == OrderIntentKind.PositionExit);
        Assert.Equal(OrderState.Rejected, (await events.GetCurrentAsync(exit.ClientOrderId))!.State);
        var protections = (await intents.ListByRunAsync(run.RunId))
            .Where(intent => intent.Kind == OrderIntentKind.ProtectiveStop)
            .ToArray();
        Assert.Equal(2, protections.Length);
    }

    [Fact]
    public async Task SubmitPositionExitAsync_BrokerQuantityMismatch_RestoresProtectionForBrokerQuantity()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var run = CreateRun(Guid.NewGuid());
        run.StartedAtUtc = now.AddMinutes(-30);
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        var positions = new SqlitePositionLedgerRepository(factory);
        await InitializeAsync(options);
        var position = await SeedPositionAsync(
            positions, run, "paper-account", "MSFT", 10m, now.AddMinutes(-5));
        var protective = await SeedActiveProtectionAsync(
            intents, events, run, position, 10m, 98m, now);
        var originalBrokerStop = CreateProtectiveBrokerOrder(
            "broker-stop-1", protective.ClientOrderId, "MSFT", 98m, now);
        var protectionCanceled = false;
        var broker = CreateProtectedExitBroker(
            now,
            protective,
            originalBrokerStop,
            () => protectionCanceled,
            () => protectionCanceled = true,
            9m);
        ProtectiveStopOrder? restored = null;
        broker.Setup(client => client.SubmitProtectiveStopAsync(
                It.IsAny<ProtectiveStopOrder>(), It.IsAny<CancellationToken>()))
            .Callback<ProtectiveStopOrder, CancellationToken>((order, _) => restored = order)
            .ReturnsAsync(new BrokerOrderReceipt("broker-stop-2", now.AddSeconds(2)));
        var service = CreateProtectedExitService(
            intents, events, positions, clock);

        await Assert.ThrowsAsync<PositionExitReconciliationRequiredException>(() =>
            service.SubmitPositionExitAsync(
                CreatePositionExit(run, now),
                broker.Object,
                CancellationToken.None));

        Assert.True(protectionCanceled);
        Assert.NotNull(restored);
        Assert.Equal(9m, restored!.Quantity);
        broker.Verify(client => client.SubmitExitOrderAsync(
            It.IsAny<BrokerExitOrder>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SubmitPositionExitAsync_RequestCanceledDuringStopHandoff_RestoresProtectionIndependently()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var run = CreateRun(Guid.NewGuid());
        run.StartedAtUtc = now.AddMinutes(-30);
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        var positions = new SqlitePositionLedgerRepository(factory);
        await InitializeAsync(options);
        var position = await SeedPositionAsync(
            positions, run, "paper-account", "MSFT", 10m, now.AddMinutes(-5));
        var protective = await SeedActiveProtectionAsync(
            intents, events, run, position, 10m, 98m, now);
        var originalBrokerStop = CreateProtectiveBrokerOrder(
            "broker-stop-1", protective.ClientOrderId, "MSFT", 98m, now);
        using var requestCancellation = new CancellationTokenSource();
        var protectionCanceled = false;
        var cancelObservationCount = 0;
        var broker = CreateProtectedExitBroker(
            now,
            protective,
            originalBrokerStop,
            () => protectionCanceled,
            () => protectionCanceled = true,
            10m);
        broker.Setup(client => client.GetOrderByClientOrderIdAsync(
                protective.ClientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                if (!protectionCanceled)
                {
                    return originalBrokerStop;
                }

                cancelObservationCount++;
                requestCancellation.Cancel();
                return originalBrokerStop with { Status = "canceled" };
            });
        ProtectiveStopOrder? restored = null;
        broker.Setup(client => client.SubmitProtectiveStopAsync(
                It.IsAny<ProtectiveStopOrder>(), It.IsAny<CancellationToken>()))
            .Callback<ProtectiveStopOrder, CancellationToken>((order, _) => restored = order)
            .ReturnsAsync(new BrokerOrderReceipt("broker-stop-2", now.AddSeconds(2)));
        var service = CreateProtectedExitService(
            intents, events, positions, clock);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SubmitPositionExitAsync(
                CreatePositionExit(run, now),
                broker.Object,
                requestCancellation.Token));

        Assert.True(protectionCanceled);
        Assert.True(cancelObservationCount > 0);
        Assert.NotNull(restored);
        Assert.False(requestCancellation.Token.CanBeCanceled && !requestCancellation.IsCancellationRequested);
        broker.Verify(client => client.SubmitExitOrderAsync(
            It.IsAny<BrokerExitOrder>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SubmitPositionExitAsync_AcknowledgementJournalFailure_DoesNotRaceActiveBrokerExitWithStop()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var run = CreateRun(Guid.NewGuid());
        run.StartedAtUtc = now.AddMinutes(-30);
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var durableEvents = new SqliteOrderEventRepository(factory);
        var positions = new SqlitePositionLedgerRepository(factory);
        await InitializeAsync(options);
        var position = await SeedPositionAsync(
            positions, run, "paper-account", "MSFT", 10m, now.AddMinutes(-5));
        var protective = await SeedActiveProtectionAsync(
            intents, durableEvents, run, position, 10m, 98m, now);
        var events = new FaultingOrderEventRepository(
            durableEvents,
            request => request.NewState == OrderState.Acked &&
                       request.BrokerOrderId == "exit-accepted");
        var originalBrokerStop = CreateProtectiveBrokerOrder(
            "broker-stop-1", protective.ClientOrderId, "MSFT", 98m, now);
        var protectionCanceled = false;
        var exitSubmitted = false;
        var submittedExitClientOrderId = String.Empty;
        var broker = CreateProtectedExitBroker(
            now,
            protective,
            originalBrokerStop,
            () => protectionCanceled,
            () => protectionCanceled = true,
            10m);
        broker.Setup(client => client.SubmitExitOrderAsync(
                It.IsAny<BrokerExitOrder>(), It.IsAny<CancellationToken>()))
            .Callback<BrokerExitOrder, CancellationToken>((order, _) =>
            {
                exitSubmitted = true;
                submittedExitClientOrderId = order.ClientOrderId;
            })
            .ReturnsAsync(new BrokerOrderReceipt("exit-accepted", now.AddSeconds(1)));
        broker.Setup(client => client.GetOrderByClientOrderIdAsync(
                It.Is<string>(clientOrderId => clientOrderId != protective.ClientOrderId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string clientOrderId, CancellationToken _) =>
                exitSubmitted && clientOrderId == submittedExitClientOrderId
                    ? new ActiveBrokerOrder(
                        "exit-accepted", "MSFT", "sell", "new", "market", null, null,
                        10m, now.AddSeconds(1), clientOrderId, 0m, null, now.AddSeconds(1),
                        TimeInForce: "day", OrderClass: "simple", ExtendedHours: false)
                    : null);
        var admission = new EntryAdmissionControl();
        var dispatcher = new OrderDispatchService(
            intents,
            intents,
            events,
            positions,
            new BrokerAccountBindingService(new BrokerAccountBindingOptions("paper-account")),
            new OrderLifecycleService(events, clock),
            new OrderDispatchOptions(30, 30, 5, 4),
            clock,
            NullLogger<OrderDispatchService>.Instance,
            admission);
        var service = new OrderSubmissionService(
            intents,
            dispatcher,
            new Mock<IEntryGateChain>(MockBehavior.Strict).Object,
            NullLogger<OrderSubmissionService>.Instance,
            positions,
            events: events,
            lifecycle: new OrderLifecycleService(events, clock),
            accountBinding: new BrokerAccountBindingService(
                new BrokerAccountBindingOptions("paper-account")),
            timeProvider: clock,
            admission: admission);

        await Assert.ThrowsAsync<IOException>(() =>
            service.SubmitPositionExitAsync(
                CreatePositionExit(run, now),
                broker.Object,
                CancellationToken.None));

        Assert.True(exitSubmitted);
        Assert.True(protectionCanceled);
        Assert.Contains(
            admission.GetSnapshot().Blocks,
            block => block.Code == "BROKER_ACK_JOURNAL_FAILED");
        broker.Verify(client => client.SubmitProtectiveStopAsync(
            It.IsAny<ProtectiveStopOrder>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DispatchProtectiveStop_WhileExitOwnsGeneration_ExpiresWithoutBrokerPost()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var run = CreateRun(Guid.NewGuid());
        run.StartedAtUtc = now.AddMinutes(-30);
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        var positions = new SqlitePositionLedgerRepository(factory);
        await InitializeAsync(options);
        var position = await SeedPositionAsync(
            positions, run, "paper-account", "MSFT", 10m, now.AddMinutes(-5));
        var protective = (await intents.ReserveAsync(
            run,
            CreateProtectiveReservation(Guid.NewGuid(), "MSFT") with
            {
                PositionGenerationEventId = position.PositionGenerationEventId
            })).Intent;
        await intents.ReserveAsync(
            run,
            CreateReservation(Guid.NewGuid(), "MSFT") with
            {
                Kind = OrderIntentKind.PositionExit,
                Side = "sell",
                OrderType = "market",
                TimeInForce = "day",
                LimitPrice = null,
                StopPrice = null,
                PortfolioRisk = null,
                DispatchExpiresAtUtc = null,
                PositionGenerationEventId = position.PositionGenerationEventId,
                RequestJson = "{\"symbol\":\"MSFT\",\"quantity\":10,\"reason\":\"technical_exit\"}"
            });
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateAccountSnapshot("paper-account", now));
        broker.Setup(client => client.GetOrderByClientOrderIdAsync(
                protective.ClientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ActiveBrokerOrder?)null);
        var dispatcher = CreateDispatcher(intents, events, clock, positions);

        await Assert.ThrowsAsync<PositionExitInProgressException>(() =>
            dispatcher.DispatchAsync(protective.IntentId, broker.Object));

        Assert.Equal(
            OrderState.Expired,
            (await events.GetCurrentAsync(protective.ClientOrderId))!.State);
        broker.Verify(client => client.SubmitProtectiveStopAsync(
            It.IsAny<ProtectiveStopOrder>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecoverProtectiveStop_FromClosedGeneration_ExpiresWithoutBrokerPost()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var run = CreateRun(Guid.NewGuid());
        run.StartedAtUtc = now.AddMinutes(-30);
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        var positions = new SqlitePositionLedgerRepository(factory);
        await InitializeAsync(options);
        var original = await SeedPositionAsync(
            positions, run, "paper-account", "MSFT", 10m, now.AddMinutes(-5));
        var protective = (await intents.ReserveAsync(
            run,
            CreateProtectiveReservation(Guid.NewGuid(), "MSFT") with
            {
                PositionGenerationEventId = original.PositionGenerationEventId
            })).Intent;
        await positions.AppendFillAsync(new PositionFillAppendRequest(
            run,
            "paper-account",
            "MSFT",
            "SWGA",
            0m,
            10m,
            101m,
            "sell",
            "exit-msft",
            "exit-client-msft",
            "exit-fill-paper-account-msft",
            "broker_rest",
            now.AddMinutes(-1),
            now.AddMinutes(-1),
            "{\"source\":\"test\"}"));
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateAccountSnapshot("paper-account", now));
        broker.Setup(client => client.GetOrderByClientOrderIdAsync(
                protective.ClientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ActiveBrokerOrder?)null);
        var dispatcher = CreateDispatcher(intents, events, clock, positions);

        var recovered = await dispatcher.RecoverPendingAsync(broker.Object);

        Assert.Equal(0, recovered);
        Assert.Equal(
            OrderState.Expired,
            (await events.GetCurrentAsync(protective.ClientOrderId))!.State);
        broker.Verify(client => client.SubmitProtectiveStopAsync(
            It.IsAny<ProtectiveStopOrder>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DispatchPositionExit_BeforeProtectionPreparation_RefusesBrokerPost()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var run = CreateRun(Guid.NewGuid());
        run.StartedAtUtc = now.AddMinutes(-30);
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        var positions = new SqlitePositionLedgerRepository(factory);
        await InitializeAsync(options);
        var position = await SeedPositionAsync(
            positions, run, "paper-account", "MSFT", 10m, now.AddMinutes(-5));
        var exit = (await intents.ReserveAsync(
            run,
            CreateReservation(Guid.NewGuid(), "MSFT") with
            {
                Kind = OrderIntentKind.PositionExit,
                Side = "sell",
                OrderType = "market",
                TimeInForce = "day",
                LimitPrice = null,
                StopPrice = null,
                PortfolioRisk = null,
                DispatchExpiresAtUtc = null,
                PositionGenerationEventId = position.PositionGenerationEventId,
                RequestJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    symbol = "MSFT",
                    quantity = 10m,
                    expectedPositionGenerationEventId = position.PositionGenerationEventId,
                    expectedPositionGenerationClientOrderId = position.PositionGenerationClientOrderId
                })
            })).Intent;
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateAccountSnapshot("paper-account", now));
        broker.Setup(client => client.GetOrderByClientOrderIdAsync(
                exit.ClientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ActiveBrokerOrder?)null);
        var dispatcher = CreateDispatcher(intents, events, clock, positions);

        await Assert.ThrowsAsync<PositionExitPreparationRequiredException>(() =>
            dispatcher.DispatchAsync(exit.IntentId, broker.Object));

        Assert.Equal(OrderState.Intent, (await events.GetCurrentAsync(exit.ClientOrderId))!.State);
        broker.Verify(client => client.SubmitExitOrderAsync(
            It.IsAny<BrokerExitOrder>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecoverUnpreparedPositionExit_AfterProtectionCancellation_PreparesAndDispatchesSameIntent()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var run = CreateRun(Guid.NewGuid());
        run.StartedAtUtc = now.AddMinutes(-30);
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        var positions = new SqlitePositionLedgerRepository(factory);
        await InitializeAsync(options);
        var position = await SeedPositionAsync(
            positions, run, "paper-account", "MSFT", 10m, now.AddMinutes(-5));
        var exit = (await intents.ReserveAsync(
            run,
            CreateReservation(Guid.NewGuid(), "MSFT") with
            {
                Kind = OrderIntentKind.PositionExit,
                Side = "sell",
                OrderType = "market",
                TimeInForce = "day",
                LimitPrice = null,
                StopPrice = null,
                PortfolioRisk = null,
                DispatchExpiresAtUtc = null,
                PositionGenerationEventId = position.PositionGenerationEventId,
                RequestJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    symbol = "MSFT",
                    side = "sell",
                    quantity = 10m,
                    orderType = "market",
                    timeInForce = "day",
                    limitPrice = (decimal?)null,
                    submitOutsideRegularHours = false,
                    reason = "technical_exit",
                    expectedPositionGenerationEventId = position.PositionGenerationEventId,
                    expectedPositionGenerationClientOrderId = position.PositionGenerationClientOrderId
                })
            })).Intent;
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateAccountSnapshot("paper-account", now));
        broker.Setup(client => client.GetOpenPositionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new BrokerPosition("MSFT", "long", 10m, 100m, 100m, 0m)]);
        broker.Setup(client => client.GetOrderByClientOrderIdAsync(
                exit.ClientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ActiveBrokerOrder?)null);
        broker.Setup(client => client.SubmitExitOrderAsync(
                It.Is<BrokerExitOrder>(order => order.ClientOrderId == exit.ClientOrderId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrokerOrderReceipt("recovered-exit", now));
        var dispatcher = CreateDispatcher(intents, events, clock, positions);
        var service = new OrderSubmissionService(
            intents,
            dispatcher,
            new Mock<IEntryGateChain>(MockBehavior.Strict).Object,
            NullLogger<OrderSubmissionService>.Instance,
            positions,
            events: events,
            lifecycle: new OrderLifecycleService(events, clock),
            accountBinding: new BrokerAccountBindingService(
                new BrokerAccountBindingOptions("paper-account")),
            timeProvider: clock);

        var recovered = await service.RecoverUnpreparedPositionExitsAsync(
            broker.Object,
            CancellationToken.None);

        Assert.Equal(1, recovered);
        var state = await events.GetCurrentAsync(exit.ClientOrderId);
        Assert.Equal(OrderState.Acked, state!.State);
        Assert.Equal("recovered-exit", state.BrokerOrderId);
        broker.Verify(client => client.SubmitExitOrderAsync(
            It.IsAny<BrokerExitOrder>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // A safe standalone cancellation journals before using the broker's DELETE endpoint.
    [Fact]
    public async Task RequestCancelAsync_JournalsPendingBeforeBrokerDelete()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        await InitializeAsync(options);
        var intent = await SeedAcknowledgedOrderAsync(
            intents, events, CreateRun(Guid.NewGuid()), Guid.NewGuid(), "MSFT", "broker-msft", now);
        OrderState? stateAtBrokerCall = null;
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateAccountSnapshot("paper-account", now));
        var openOrder = CreateMatchingBrokerOrder(intent, "broker-msft", "new");
        broker.Setup(client => client.GetOpenOrdersAsync(It.IsAny<CancellationToken>())).ReturnsAsync([openOrder]);
        broker.SetupSequence(client => client.GetOrderByClientOrderIdAsync(
                intent.ClientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(openOrder)
            .ReturnsAsync(openOrder)
            .ReturnsAsync(CreateMatchingBrokerOrder(intent, "broker-msft", "canceled"));
        broker.Setup(client => client.CancelOrderAsync("broker-msft", It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken cancellationToken) =>
            {
                stateAtBrokerCall = (await events.GetCurrentAsync(
                    intent.ClientOrderId,
                    cancellationToken))?.State;
                return true;
            });
        var service = CreateCommandService(
            intents,
            events,
            clock,
            new SqliteProtectiveStopReplacementRepository(factory, clock));

        var result = await service.RequestCancelAsync(
            new OrderCancellationSubmission(
                intent.ClientOrderId,
                "broker-msft",
                "operator_cancel",
                now),
            broker.Object,
            CancellationToken.None);

        Assert.Equal(OrderState.CancelPending, stateAtBrokerCall);
        Assert.Equal(OrderState.Canceled, result.State);
        broker.VerifyAll();
    }

    // A fill that wins the race with a standalone DELETE is journaled before cancellation returns.
    [Fact]
    public async Task RequestCancelAsync_BrokerFillDuringCancellation_AtomicallyUpdatesPositionLedger()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        var positions = new SqlitePositionLedgerRepository(factory);
        await InitializeAsync(options);
        var intent = await SeedAcknowledgedOrderAsync(
            intents, events, CreateRun(Guid.NewGuid()), Guid.NewGuid(), "MSFT", "broker-msft", now);
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateAccountSnapshot("paper-account", now));
        var openOrder = CreateMatchingBrokerOrder(intent, "broker-msft", "new");
        broker.Setup(client => client.GetOpenOrdersAsync(It.IsAny<CancellationToken>())).ReturnsAsync([openOrder]);
        broker.SetupSequence(client => client.GetOrderByClientOrderIdAsync(
                intent.ClientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(openOrder)
            .ReturnsAsync(openOrder)
            .ReturnsAsync(CreateMatchingBrokerOrder(
                intent,
                "broker-msft",
                "canceled",
                filledQuantity: 4m,
                filledAveragePrice: 100.25m));
        broker.Setup(client => client.CancelOrderAsync("broker-msft", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var service = CreateCommandService(intents, events, clock);

        var result = await service.RequestCancelAsync(
            new OrderCancellationSubmission(
                intent.ClientOrderId,
                "broker-msft",
                "operator_cancel",
                now),
            broker.Object,
            CancellationToken.None);

        var position = await positions.GetCurrentAsync("paper-account", "MSFT");
        Assert.Equal(OrderState.Canceled, result.State);
        Assert.Equal(4m, result.FilledQuantity);
        Assert.NotNull(position);
        Assert.Equal(4m, position.Quantity);
        Assert.Equal(100.25m, position.LatestFillPrice);
        Assert.Equal(position.PositionEventId, position.PositionGenerationEventId);
    }

    [Fact]
    public async Task RequestCancelAsync_MismatchedBrokerOrderId_FailsBeforeJournalOrDelete()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        await InitializeAsync(options);
        var intent = await SeedAcknowledgedOrderAsync(
            intents, events, CreateRun(Guid.NewGuid()), Guid.NewGuid(), "MSFT", "broker-msft", now);
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateAccountSnapshot("paper-account", now));
        broker.Setup(client => client.GetOrderByClientOrderIdAsync(
                intent.ClientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateMatchingBrokerOrder(intent, "different-broker-order", "new"));
        var service = CreateCommandService(intents, events, clock);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RequestCancelAsync(
                new OrderCancellationSubmission(
                    intent.ClientOrderId,
                    "broker-msft",
                    "operator_cancel",
                    now),
                broker.Object,
                CancellationToken.None));

        Assert.Equal(OrderState.Acked, (await events.GetCurrentAsync(intent.ClientOrderId))!.State);
        broker.Verify(client => client.CancelOrderAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RequestCancelAsync_AccountMismatch_DoesNotJournalOrCallDelete()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        await InitializeAsync(options);
        var intent = await SeedAcknowledgedOrderAsync(
            intents, events, CreateRun(Guid.NewGuid()), Guid.NewGuid(), "MSFT", "broker-msft", now);
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateAccountSnapshot("other-account", now));
        var service = CreateCommandService(
            intents,
            events,
            clock,
            new SqliteProtectiveStopReplacementRepository(factory, clock));

        await Assert.ThrowsAsync<BrokerAccountMismatchException>(() =>
            service.RequestCancelAsync(
                new OrderCancellationSubmission(
                    intent.ClientOrderId,
                    "broker-msft",
                    "operator_cancel",
                    now),
                broker.Object,
                CancellationToken.None));

        Assert.Equal(OrderState.Acked, (await events.GetCurrentAsync(intent.ClientOrderId))!.State);
        broker.Verify(client => client.CancelOrderAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Recovery still processes a standalone cancellation after another order's broker failure.
    [Fact]
    public async Task RecoverPendingCancellationsAsync_OneBrokerFailure_DoesNotBlockRemainingOrders()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        await InitializeAsync(options);
        var aapl = await SeedAcknowledgedOrderAsync(
            intents, events, CreateRun(Guid.NewGuid()), Guid.NewGuid(), "AAPL", "broker-aapl", now);
        var msft = await SeedAcknowledgedOrderAsync(
            intents, events, CreateRun(Guid.NewGuid()), Guid.NewGuid(), "MSFT", "broker-msft", now);
        await events.TransitionAsync(new OrderTransitionRequest(
            aapl.ClientOrderId, OrderState.Acked, OrderState.CancelPending, "engine", now,
            BrokerOrderId: "broker-aapl", PayloadJson: "{\"action\":\"cancel_requested\"}"));
        await events.TransitionAsync(new OrderTransitionRequest(
            msft.ClientOrderId, OrderState.Acked, OrderState.CancelPending, "engine", now,
            BrokerOrderId: "broker-msft", PayloadJson: "{\"action\":\"cancel_requested\"}"));
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateAccountSnapshot("paper-account", now));
        var openAapl = CreateMatchingBrokerOrder(aapl, "broker-aapl", "new");
        var openMsft = CreateMatchingBrokerOrder(msft, "broker-msft", "new");
        broker.Setup(client => client.GetOpenOrdersAsync(It.IsAny<CancellationToken>())).ReturnsAsync([openAapl, openMsft]);
        broker.Setup(client => client.GetOrderByClientOrderIdAsync(aapl.ClientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(openAapl);
        broker.Setup(client => client.CancelOrderAsync("broker-aapl", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("temporary broker failure"));
        broker.Setup(client => client.CancelOrderAsync("broker-msft", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        broker.SetupSequence(client => client.GetOrderByClientOrderIdAsync(
                msft.ClientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(openMsft)
            .ReturnsAsync(CreateMatchingBrokerOrder(msft, "broker-msft", "canceled"));
        var service = CreateCommandService(intents, events, clock);

        var error = await Assert.ThrowsAsync<BrokerCommandRecoveryIncompleteException>(() =>
            service.RecoverPendingCancellationsAsync(
                broker.Object,
                CancellationToken.None));

        Assert.Equal("Order cancellation", error.Operation);
        Assert.Equal(1, error.RecoveredCount);
        Assert.Equal(1, error.FailedCount);
        broker.Verify(client => client.CancelOrderAsync(
            "broker-aapl", It.IsAny<CancellationToken>()), Times.Once);
        broker.Verify(client => client.CancelOrderAsync(
            "broker-msft", It.IsAny<CancellationToken>()), Times.Once);
    }

    // An older pending journal event is never authority to DELETE a working linked group.
    [Theory]
    [InlineData("partially_filled", false)]
    [InlineData("done_for_day", false)]
    [InlineData("filled", true)]
    public async Task RecoverLinkedCancellation_RepairsOnlyConfirmedTerminalParentWithoutDelete(string status, bool recovered)
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        var positions = new SqlitePositionLedgerRepository(factory);
        await InitializeAsync(options);
        var parent = await SeedAcknowledgedOrderAsync(
            intents, events, CreateRun(Guid.NewGuid()), Guid.NewGuid(), "MSFT", "broker-parent", now);
        await events.TransitionAsync(new OrderTransitionRequest(
            parent.ClientOrderId, OrderState.Acked, OrderState.CancelPending, "engine", now, BrokerOrderId: "broker-parent"));
        var brokerOrder = CreateMatchingBrokerOrder(parent, "broker-parent", status,
            filledQuantity: recovered ? parent.RequestedQuantity : 1m, filledAveragePrice: 100m) with { OrderClass = "bracket" };
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateAccountSnapshot("paper-account", now));
        broker.Setup(client => client.GetOpenOrdersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(recovered ? [] : [brokerOrder]);
        broker.Setup(client => client.GetOrderByClientOrderIdAsync(parent.ClientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(brokerOrder);
        var service = CreateCommandService(intents, events, clock, positions: positions);

        if (recovered)
        {
            Assert.Equal(1, await service.RecoverPendingCancellationsAsync(broker.Object, default));
            Assert.Equal(parent.RequestedQuantity, await positions.GetAccountedFillQuantityAsync("paper-account", "broker-parent"));
        }
        else
        {
            await Assert.ThrowsAsync<OpeningOrderCancellationReviewRequiredException>(() =>
                service.RequestCancelAsync(new(parent.ClientOrderId, "broker-parent", "generic", now), broker.Object, default));
            var error = await Assert.ThrowsAsync<BrokerCommandRecoveryIncompleteException>(() =>
                service.RecoverPendingCancellationsAsync(broker.Object, default));
            Assert.Equal(1, error.FailedCount);
        }

        var state = await events.GetCurrentAsync(parent.ClientOrderId);
        Assert.Equal(recovered ? OrderState.Filled : OrderState.CancelPending, state!.State);
        var confirmed = await service.ReconcileTerminalOpeningOrderAsync("paper-account", parent.ClientOrderId,
            "broker-parent", broker.Object, default);
        Assert.Equal(recovered ? brokerOrder : null, confirmed);
        broker.Verify(client => client.CancelOrderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // A nested leg revealed only by the last direct broker read prevents a standalone cancellation retry.
    [Fact]
    public async Task RequestCancelAsync_NestedLegAppearsAtDeleteBoundary_PreservesPendingOrder()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        await InitializeAsync(options);
        var parent = await SeedAcknowledgedOrderAsync(
            intents, events, CreateRun(Guid.NewGuid()), Guid.NewGuid(), "MSFT", "broker-parent", now);
        var openRoot = CreateMatchingBrokerOrder(parent, "broker-parent", "new");
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateAccountSnapshot("paper-account", now));
        broker.Setup(client => client.GetOpenOrdersAsync(It.IsAny<CancellationToken>())).ReturnsAsync([openRoot]);
        broker.SetupSequence(client => client.GetOrderByClientOrderIdAsync(parent.ClientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(openRoot)
            .ReturnsAsync(openRoot with { NestedLegOrderIds = ["broker-stop"] });
        var service = CreateCommandService(intents, events, clock);

        await Assert.ThrowsAsync<OpeningOrderCancellationReviewRequiredException>(() =>
            service.RequestCancelAsync(new(parent.ClientOrderId, "broker-parent", "generic", now), broker.Object, default));

        Assert.Equal(OrderState.CancelPending, (await events.GetCurrentAsync(parent.ClientOrderId))!.State);
        broker.Verify(client => client.CancelOrderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // A session pause leaves a GTC order open, repairs cumulative fills from REST, and preserves a pending cancellation.
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 3)]
    [InlineData(true, 0)]
    [InlineData(true, 3)]
    public async Task DoneForDay_GtcOrderRemainsReconcilableAcrossSessions(bool cancellationPending, int filledAtClose)
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        var positions = new SqlitePositionLedgerRepository(factory);
        var lifecycle = new OrderLifecycleService(events, clock);
        await InitializeAsync(options);
        const string brokerId = "broker-gtc";
        var intent = await SeedAcknowledgedOrderAsync(
            intents, events, CreateRun(Guid.NewGuid()), Guid.NewGuid(), "MSFT", brokerId, now, timeInForce: "gtc");
        if (cancellationPending)
        {
            await lifecycle.RequestCancelAsync(intent.ClientOrderId, brokerId);
        }

        clock.Advance(TimeSpan.FromHours(5));
        var doneForDay = CreateMatchingBrokerOrder(intent, brokerId, "done_for_day", filledAtClose,
            filledAtClose > 0 ? 100m : null) with { UpdatedAt = clock.GetUtcNow() };
        var update = BrokerOrderUpdateFactory.Create(doneForDay);
        var notice = await lifecycle.ApplyBrokerUpdateAsync(update with { Source = BrokerUpdateSource.TradeStream });
        Assert.Equal(cancellationPending ? OrderState.CancelPending : OrderState.Acked, notice.State);
        Assert.Equal(0m, notice.FilledQuantity ?? 0m);

        var snapshot = filledAtClose > 0
            ? await lifecycle.ApplyBrokerUpdateWithFillAsync(update,
                new OrderFillProjection(intent.Symbol, intent.Side, filledAtClose, 100m, "close-rest-fill"))
            : await lifecycle.ApplyBrokerUpdateAsync(update);
        var state = cancellationPending ? OrderState.CancelPending : filledAtClose > 0 ? OrderState.PartiallyFilled : OrderState.Acked;
        Assert.Equal(state, snapshot.State);
        Assert.True(BrokerOrderStateProjection.Matches(snapshot, doneForDay));
        Assert.False(BrokerOrderStateProjection.Matches(snapshot, doneForDay with { FilledQuantity = filledAtClose + 1 }));
        Assert.Equal(filledAtClose, await positions.GetAccountedFillQuantityAsync(intent.AccountId, brokerId));

        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateAccountSnapshot(intent.AccountId, clock.GetUtcNow()));
        broker.Setup(client => client.GetOrderByClientOrderIdAsync(intent.ClientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(doneForDay);
        var commands = CreateCommandService(intents, events, clock, positions: positions);
        Assert.Null(await commands.ReconcileTerminalOpeningOrderAsync(intent.AccountId, intent.ClientOrderId, brokerId,
            broker.Object, default));

        clock.Advance(TimeSpan.FromHours(18));
        if (!cancellationPending && filledAtClose > 0)
        {
            var additionalFill = update with
            {
                Status = OrderStatus.PartiallyFilled,
                FilledQuantity = filledAtClose + 1,
                Timestamp = clock.GetUtcNow()
            };
            var partiallyFilled = await lifecycle.ApplyBrokerUpdateWithFillAsync(additionalFill,
                new OrderFillProjection(intent.Symbol, intent.Side, additionalFill.FilledQuantity, 100m, "reopened-rest-fill"));
            Assert.Equal(OrderState.PartiallyFilled, partiallyFilled.State);
            Assert.Equal(filledAtClose + 1, await positions.GetAccountedFillQuantityAsync(intent.AccountId, brokerId));
        }
        else
        {
            var reopened = await lifecycle.ApplyBrokerUpdateAsync(update with { Status = OrderStatus.New, Timestamp = clock.GetUtcNow() });
            Assert.Equal(state, reopened.State);
            var pending = await lifecycle.RequestCancelAsync(intent.ClientOrderId, brokerId);
            Assert.Equal(OrderState.CancelPending, pending.State);
            var canceled = await lifecycle.ApplyBrokerUpdateAsync(update with { Status = OrderStatus.Canceled, Timestamp = clock.GetUtcNow() });
            Assert.Equal(OrderState.Canceled, canceled.State);
        }
    }

    [Fact]
    public async Task ReplaceProtectiveStopAsync_UnownedBrokerOrder_FailsBeforePatch()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        await InitializeAsync(options);
        var owner = await SeedAcknowledgedOrderAsync(
            intents, events, CreateRun(Guid.NewGuid()), Guid.NewGuid(), "MSFT", "broker-owner", now,
            protective: true);
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateAccountSnapshot("paper-account", now));
        broker.Setup(client => client.GetOrderByClientOrderIdAsync(
                It.Is<string>(value => value.StartsWith("TFR-", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((ActiveBrokerOrder?)null);
        broker.Setup(client => client.GetOpenOrdersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([CreateProtectiveBrokerOrder(
                "other-broker-order", "unowned-client-order", "MSFT", 98m, now)]);
        var service = CreateCommandService(
            intents,
            events,
            clock,
            new SqliteProtectiveStopReplacementRepository(factory, clock));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ReplaceProtectiveStopAsync(
                new ProtectiveStopReplacementSubmission(
                    owner.ClientOrderId,
                    "other-broker-order",
                    "MSFT",
                    99m,
                    "atr_trailing_stop_raise",
                    now),
                broker.Object,
                CancellationToken.None));

        broker.Verify(client => client.ReplaceProtectiveOrderAsync(
            It.IsAny<BrokerProtectiveOrderReplacement>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReplaceProtectiveStopAsync_ResponseLost_AdoptsVerifiedBrokerState()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        await InitializeAsync(options);
        var owner = await SeedAcknowledgedOrderAsync(
            intents, events, CreateRun(Guid.NewGuid()), Guid.NewGuid(), "MSFT", "broker-stop", now,
            protective: true);
        var commandId = ProtectiveStopReplacementIdFactory.Create(
            "paper-account", owner.ClientOrderId, "broker-stop", "MSFT", 99m);
        var replacementClientOrderId =
            ProtectiveStopReplacementIdFactory.CreateReplacementClientOrderId(commandId);
        var before = CreateProtectiveBrokerOrder(
            "broker-stop", owner.ClientOrderId, "MSFT", 98m, now);
        var after = before with
        {
            OrderId = "broker-stop-v2",
            ClientOrderId = replacementClientOrderId,
            StopPrice = 99m,
            UpdatedAt = now.AddSeconds(1),
            ReplacesOrderId = "broker-stop"
        };
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateAccountSnapshot("paper-account", now));
        broker.SetupSequence(client => client.GetOrderByClientOrderIdAsync(
                replacementClientOrderId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((ActiveBrokerOrder?)null)
            .ReturnsAsync(after)
            .ReturnsAsync(after)
            .ReturnsAsync(after with { Status = "canceled", UpdatedAt = now.AddSeconds(2) });
        broker.Setup(client => client.GetOpenOrdersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([before]);
        broker.Setup(client => client.ReplaceProtectiveOrderAsync(
                It.Is<BrokerProtectiveOrderReplacement>(replacement =>
                    replacement.BrokerOrderId == "broker-stop" &&
                    replacement.ReplacementClientOrderId == replacementClientOrderId &&
                    replacement.StopPrice == 99m),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("response lost after broker accepted replace"));
        broker.Setup(client => client.CancelOrderAsync(
                "broker-stop-v2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var service = CreateCommandService(
            intents,
            events,
            clock,
            new SqliteProtectiveStopReplacementRepository(factory, clock));

        var verified = await service.ReplaceProtectiveStopAsync(
            new ProtectiveStopReplacementSubmission(
                owner.ClientOrderId,
                "broker-stop",
                "MSFT",
                99m,
                "atr_trailing_stop_raise",
                now),
            broker.Object,
            CancellationToken.None);

        Assert.Equal(99m, verified.StopPrice);
        Assert.Equal("broker-stop-v2", verified.OrderId);
        await using (var verification = new TradingFlowDbContext(options))
        {
            var command = Assert.Single(await verification.ProtectiveStopReplacements
                .AsNoTracking()
                .ToListAsync());
            Assert.Equal(ProtectiveStopReplacementState.Verified, command.State);
            Assert.Equal("broker-stop-v2", command.VerifiedBrokerOrderId);
        }
        Assert.Equal(
            "broker-stop-v2",
            (await events.GetCurrentAsync(owner.ClientOrderId))!.BrokerOrderId);

        var canceled = await service.RequestCancelAsync(
            new OrderCancellationSubmission(
                owner.ClientOrderId,
                "broker-stop-v2",
                "technical_position_exit",
                now.AddSeconds(2),
                replacementClientOrderId),
            broker.Object,
            CancellationToken.None);

        Assert.Equal(OrderState.Canceled, canceled.State);
        Assert.Equal("broker-stop-v2", canceled.BrokerOrderId);
        broker.Verify(client => client.CancelOrderAsync(
            "broker-stop-v2", It.IsAny<CancellationToken>()), Times.Once);
        broker.VerifyAll();
    }

    [Fact]
    public async Task RecoverProtectiveStopReplacementAsync_CrashBeforePatchAppliesAndVerifiesCommand()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        var replacements = new SqliteProtectiveStopReplacementRepository(factory, clock);
        await InitializeAsync(options);
        var owner = await SeedAcknowledgedOrderAsync(
            intents, events, CreateRun(Guid.NewGuid()), Guid.NewGuid(), "MSFT", "broker-stop", now,
            protective: true);
        var commandId = ProtectiveStopReplacementIdFactory.Create(
            "paper-account", owner.ClientOrderId, "broker-stop", "MSFT", 99m);
        await replacements.ReserveAsync(CreateReplacementReservation(commandId, owner, 99m, now));
        var replacementClientOrderId =
            ProtectiveStopReplacementIdFactory.CreateReplacementClientOrderId(commandId);
        var before = CreateProtectiveBrokerOrder("broker-stop", owner.ClientOrderId, "MSFT", 98m, now);
        var after = before with
        {
            OrderId = "broker-stop-v2",
            ClientOrderId = replacementClientOrderId,
            StopPrice = 99m,
            UpdatedAt = now.AddSeconds(1),
            ReplacesOrderId = "broker-stop"
        };
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateAccountSnapshot("paper-account", now));
        broker.Setup(client => client.GetOrderByClientOrderIdAsync(
                replacementClientOrderId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((ActiveBrokerOrder?)null);
        broker.Setup(client => client.GetOpenOrdersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([before]);
        broker.Setup(client => client.ReplaceProtectiveOrderAsync(
                It.IsAny<BrokerProtectiveOrderReplacement>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(after);
        var service = CreateCommandService(intents, events, clock, replacements);

        var recovered = await service.RecoverPendingProtectiveStopReplacementsAsync(
            broker.Object,
            CancellationToken.None);

        Assert.Equal(1, recovered);
        await using var verification = new TradingFlowDbContext(options);
        Assert.Equal(
            ProtectiveStopReplacementState.Verified,
            (await verification.ProtectiveStopReplacements.AsNoTracking().SingleAsync()).State);
        Assert.Equal(
            "broker-stop-v2",
            (await events.GetCurrentAsync(owner.ClientOrderId))!.BrokerOrderId);
        broker.VerifyAll();
    }

    [Fact]
    public async Task RecoverProtectiveStopReplacementAsync_CrashAfterPatchAdoptsWithoutSecondPatch()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        var replacements = new SqliteProtectiveStopReplacementRepository(factory, clock);
        await InitializeAsync(options);
        var owner = await SeedAcknowledgedOrderAsync(
            intents, events, CreateRun(Guid.NewGuid()), Guid.NewGuid(), "MSFT", "broker-stop", now,
            protective: true);
        var commandId = ProtectiveStopReplacementIdFactory.Create(
            "paper-account", owner.ClientOrderId, "broker-stop", "MSFT", 99m);
        await replacements.ReserveAsync(CreateReplacementReservation(commandId, owner, 99m, now));
        var replacementClientOrderId =
            ProtectiveStopReplacementIdFactory.CreateReplacementClientOrderId(commandId);
        var after = CreateProtectiveBrokerOrder(
            "broker-stop-v2", replacementClientOrderId, "MSFT", 99m, now.AddSeconds(1)) with
        {
            ReplacesOrderId = "broker-stop"
        };
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateAccountSnapshot("paper-account", now));
        broker.Setup(client => client.GetOrderByClientOrderIdAsync(
                replacementClientOrderId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(after);
        var service = CreateCommandService(intents, events, clock, replacements);

        var recovered = await service.RecoverPendingProtectiveStopReplacementsAsync(
            broker.Object,
            CancellationToken.None);

        Assert.Equal(1, recovered);
        broker.Verify(client => client.ReplaceProtectiveOrderAsync(
            It.IsAny<BrokerProtectiveOrderReplacement>(),
            It.IsAny<CancellationToken>()), Times.Never);
        await using var verification = new TradingFlowDbContext(options);
        Assert.Equal(
            ProtectiveStopReplacementState.Verified,
            (await verification.ProtectiveStopReplacements.AsNoTracking().SingleAsync()).State);
        Assert.Equal(
            "broker-stop-v2",
            (await events.GetCurrentAsync(owner.ClientOrderId))!.BrokerOrderId);
    }

    [Fact]
    public async Task ProtectiveStopReplacement_HigherStopSupersedesPendingLowerStopAndCannotBeLowered()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var intents = new SqliteOrderIntentRepository(factory, clock);
        var events = new SqliteOrderEventRepository(factory);
        var replacements = new SqliteProtectiveStopReplacementRepository(factory, clock);
        await InitializeAsync(options);
        var owner = await SeedAcknowledgedOrderAsync(
            intents, events, CreateRun(Guid.NewGuid()), Guid.NewGuid(), "MSFT", "broker-stop", now,
            protective: true);
        var lowerCommandId = ProtectiveStopReplacementIdFactory.Create(
            "paper-account", owner.ClientOrderId, "broker-stop", "MSFT", 99m);
        var higherCommandId = ProtectiveStopReplacementIdFactory.Create(
            "paper-account", owner.ClientOrderId, "broker-stop", "MSFT", 100m);

        await replacements.ReserveAsync(
            CreateReplacementReservation(lowerCommandId, owner, 99m, now));
        await replacements.ReserveAsync(
            CreateReplacementReservation(higherCommandId, owner, 100m, now.AddSeconds(1)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            replacements.ReserveAsync(CreateReplacementReservation(
                ProtectiveStopReplacementIdFactory.Create(
                    "paper-account", owner.ClientOrderId, "broker-stop", "MSFT", 98m),
                owner,
                98m,
                now.AddSeconds(2))));
        Assert.Contains("cannot be lowered", error.Message, StringComparison.OrdinalIgnoreCase);

        await using var verification = new TradingFlowDbContext(options);
        var commands = await verification.ProtectiveStopReplacements
            .AsNoTracking()
            .OrderBy(record => record.RequestedAtUtc)
            .ToArrayAsync();
        Assert.Equal(2, commands.Length);
        Assert.Equal(ProtectiveStopReplacementState.Superseded, commands[0].State);
        Assert.Equal(ProtectiveStopReplacementState.Pending, commands[1].State);
        Assert.Equal([higherCommandId], await replacements.ListRecoverableCommandIdsAsync(
            "paper-account", 10));
    }

    [Fact]
    public async Task PositionLedger_SameSymbolAndExecutionId_AreIsolatedByAccount()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var positions = new SqlitePositionLedgerRepository(new TestDbContextFactory(options));
        await InitializeAsync(options);
        var run = CreateRun(Guid.NewGuid());
        var at = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var commonExecutionId = "same-provider-execution-id";

        await positions.AppendFillAsync(new PositionFillAppendRequest(
            run, "paper-account-a", "MSFT", "SWGA", 3m, 3m, 100m, "buy",
            "broker-a", "client-a", commonExecutionId, "broker_rest", at, at, "{}"));
        await positions.AppendFillAsync(new PositionFillAppendRequest(
            run, "paper-account-b", "MSFT", "SWGA", 7m, 7m, 101m, "buy",
            "broker-b", "client-b", commonExecutionId, "broker_rest", at, at, "{}"));

        var first = await positions.GetCurrentAsync("paper-account-a", "MSFT");
        var second = await positions.GetCurrentAsync("paper-account-b", "MSFT");
        Assert.Equal(3m, first!.Quantity);
        Assert.Equal(100m, first.LatestFillPrice);
        Assert.Equal(7m, second!.Quantity);
        Assert.Equal(101m, second.LatestFillPrice);
    }

    [Fact]
    public async Task PositionLedger_PartialFillsAndExitsKeepOneStablePositionGeneration()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var positions = new SqlitePositionLedgerRepository(new TestDbContextFactory(options));
        await InitializeAsync(options);
        var run = CreateRun(Guid.NewGuid());
        var at = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);

        var first = await positions.AppendFillAsync(new PositionFillAppendRequest(
            run, "paper-account", "MSFT", "SWGA", 40m, 40m, 100m, "buy",
            "entry-broker", "entry-client", "entry-fill-40", "broker_rest", at, at, "{}"));
        var second = await positions.AppendFillAsync(new PositionFillAppendRequest(
            run, "paper-account", "MSFT", "SWGA", 100m, 60m, 101m, "buy",
            "entry-broker", "entry-client", "entry-fill-100", "broker_rest", at.AddSeconds(1), at.AddSeconds(1), "{}"));
        var partialExit = await positions.AppendFillAsync(new PositionFillAppendRequest(
            run, "paper-account", "MSFT", "SWGA", 75m, 25m, 102m, "sell",
            "exit-broker", "exit-client", "exit-fill-25", "broker_rest", at.AddSeconds(2), at.AddSeconds(2), "{}"));
        var flat = await positions.AppendFillAsync(new PositionFillAppendRequest(
            run, "paper-account", "MSFT", "SWGA", 0m, 75m, 103m, "sell",
            "exit-broker-2", "exit-client-2", "exit-fill-flat", "broker_rest", at.AddSeconds(3), at.AddSeconds(3), "{}"));
        var reopened = await positions.AppendFillAsync(new PositionFillAppendRequest(
            run, "paper-account", "MSFT", "SWGA", 10m, 10m, 99m, "buy",
            "entry-broker-2", "entry-client-2", "entry-fill-reopen", "broker_rest", at.AddSeconds(4), at.AddSeconds(4), "{}"));

        Assert.Equal(first.PositionEventId, first.PositionGenerationEventId);
        Assert.Equal(first.PositionGenerationEventId, second.PositionGenerationEventId);
        Assert.Equal(first.PositionGenerationEventId, partialExit.PositionGenerationEventId);
        Assert.Equal(first.PositionGenerationEventId, flat.PositionGenerationEventId);
        Assert.All([first, second, partialExit, flat], snapshot =>
            Assert.Equal("entry-client", snapshot.PositionGenerationClientOrderId));
        Assert.Equal(reopened.PositionEventId, reopened.PositionGenerationEventId);
        Assert.Equal("entry-client-2", reopened.PositionGenerationClientOrderId);
    }

    [Fact]
    public async Task OrderLifecycle_ConcurrentRepositoryRepair_AppendsOnePositionDelta()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var run = CreateRun(Guid.NewGuid());
        var intents = new SqliteOrderIntentRepository(factory, new FixedTimeProvider(new DateTimeOffset(2026, 7, 21, 14, 30, 0, TimeSpan.Zero)));
        var seedEvents = new SqliteOrderEventRepository(factory);
        await InitializeAsync(options);
        var intent = (await intents.ReserveAsync(
            run,
            CreateReservation(Guid.NewGuid(), "MSFT"))).Intent;
        await seedEvents.TransitionAsync(new OrderTransitionRequest(
            intent.ClientOrderId, OrderState.Intent, OrderState.Submitted, "engine",
            intent.CreatedAtUtc, PayloadJson: intent.RequestJson));
        await seedEvents.TransitionAsync(new OrderTransitionRequest(
            intent.ClientOrderId, OrderState.Submitted, OrderState.Acked, "broker_rest",
            intent.CreatedAtUtc.AddSeconds(1), intent.CreatedAtUtc.AddSeconds(1), "broker-concurrent"));
        var update = CreateOrderUpdate(
            intent,
            "broker-concurrent",
            OrderStatus.Canceled,
            4m,
            100m,
            3);
        var projection = new OrderFillProjection(
            "MSFT", "buy", 4m, 100m, "rest:broker-concurrent:4");
        var lifecycleClock = new FixedTimeProvider(intent.CreatedAtUtc.AddSeconds(4));
        var first = new OrderLifecycleService(new SqliteOrderEventRepository(factory), lifecycleClock);
        var second = new OrderLifecycleService(new SqliteOrderEventRepository(factory), lifecycleClock);

        var results = await Task.WhenAll(
            first.ApplyBrokerUpdateWithFillAsync(update, projection),
            second.ApplyBrokerUpdateWithFillAsync(update, projection));

        Assert.All(results, snapshot => Assert.Equal(OrderState.Canceled, snapshot.State));
        await using var verification = new TradingFlowDbContext(options);
        var positionEvent = Assert.Single(await verification.PositionEvents.AsNoTracking().ToArrayAsync());
        Assert.Equal(4m, positionEvent.FillQuantity);
        Assert.Equal(4m, positionEvent.QuantityAfter);
    }

    [Fact]
    public async Task ApplyBrokerUpdateWithFillAsync_MultiplePrices_PreservesExactCostBasis()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var run = CreateRun(Guid.NewGuid());
        var intents = new SqliteOrderIntentRepository(factory, new FixedTimeProvider(new DateTimeOffset(2026, 7, 21, 14, 30, 0, TimeSpan.Zero)));
        var events = new SqliteOrderEventRepository(factory);
        var lifecycle = new OrderLifecycleService(events);
        await InitializeAsync(options);
        var intent = (await intents.ReserveAsync(
            run,
            CreateReservation(Guid.NewGuid(), "MSFT"))).Intent;
        await events.TransitionAsync(new OrderTransitionRequest(
            intent.ClientOrderId,
            OrderState.Intent,
            OrderState.Submitted,
            "engine",
            intent.CreatedAtUtc,
            PayloadJson: intent.RequestJson));
        await events.TransitionAsync(new OrderTransitionRequest(
            intent.ClientOrderId,
            OrderState.Submitted,
            OrderState.Acked,
            "broker_rest",
            intent.CreatedAtUtc,
            intent.CreatedAtUtc.AddSeconds(2),
            "broker-multi-price"));

        await lifecycle.ApplyBrokerUpdateWithFillAsync(
            CreateOrderUpdate(intent, "broker-multi-price", OrderStatus.PartiallyFilled, 4m, 100m, 3),
            new OrderFillProjection(
                "MSFT", "buy", 4m, 100m, "fill-1",
                AuthoritativePositionQuantity: 4m,
                AuthoritativeLastFillQuantity: 4m,
                AuthoritativeLastFillPrice: 100m));
        await lifecycle.ApplyBrokerUpdateWithFillAsync(
            CreateOrderUpdate(intent, "broker-multi-price", OrderStatus.Filled, 10m, 103m, 4),
            new OrderFillProjection(
                "MSFT", "buy", 10m, 103m, "fill-2",
                AuthoritativePositionQuantity: 10m,
                AuthoritativeLastFillQuantity: 6m,
                AuthoritativeLastFillPrice: 105m));

        await using var verification = new TradingFlowDbContext(options);
        var fills = await verification.PositionEvents.AsNoTracking()
            .OrderBy(record => record.PositionEventId)
            .ToArrayAsync();
        Assert.Equal([4m, 6m], fills.Select(fill => fill.FillQuantity));
        Assert.Equal([100m, 105m], fills.Select(fill => fill.FillPrice));
        Assert.Equal(1_030m, fills.Sum(fill => fill.FillQuantity * fill.FillPrice));
        Assert.Equal(10m, fills[^1].QuantityAfter);
    }

    [Fact]
    public async Task ProtectiveExit_FlattensPartialEntryFill_WhileRemainderRiskStaysReserved()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var run = CreateRun(Guid.NewGuid());
        var intentClock = new FixedTimeProvider(
            new DateTimeOffset(2026, 7, 21, 14, 30, 0, TimeSpan.Zero));
        var intents = new SqliteOrderIntentRepository(factory, intentClock);
        var events = new SqliteOrderEventRepository(factory);
        await InitializeAsync(options);
        var entry = (await intents.ReserveAsync(
            run,
            CreateReservation(Guid.NewGuid(), "MSFT"))).Intent;
        var lifecycle = new OrderLifecycleService(
            events,
            new FixedTimeProvider(entry.CreatedAtUtc.AddSeconds(10)));
        await events.TransitionAsync(new OrderTransitionRequest(
            entry.ClientOrderId,
            OrderState.Intent,
            OrderState.Submitted,
            "engine",
            entry.CreatedAtUtc,
            PayloadJson: entry.RequestJson));
        await events.TransitionAsync(new OrderTransitionRequest(
            entry.ClientOrderId,
            OrderState.Submitted,
            OrderState.Acked,
            "broker_rest",
            entry.CreatedAtUtc.AddSeconds(1),
            entry.CreatedAtUtc.AddSeconds(1),
            "broker-entry"));
        await lifecycle.ApplyBrokerUpdateWithFillAsync(
            CreateOrderUpdate(entry, "broker-entry", OrderStatus.PartiallyFilled, 4m, 100m, 2),
            new OrderFillProjection(
                "MSFT",
                "buy",
                4m,
                100m,
                "entry-fill-1",
                AuthoritativePositionQuantity: 4m,
                AuthoritativeLastFillQuantity: 4m,
                AuthoritativeLastFillPrice: 100m));

        await using var positionRead = new TradingFlowDbContext(options);
        var positionGeneration = (await positionRead.PositionEvents
            .AsNoTracking()
            .OrderByDescending(record => record.PositionEventId)
            .FirstAsync()).PositionEventId;
        var exitReservation = CreateReservation(Guid.NewGuid(), "MSFT") with
        {
            Kind = OrderIntentKind.PositionExit,
            Side = "sell",
            OrderType = "market",
            LimitPrice = null,
            StopPrice = null,
            RequestedQuantity = 4m,
            PortfolioRisk = null,
            DispatchExpiresAtUtc = null,
            PositionGenerationEventId = positionGeneration,
            RequestJson = "{\"symbol\":\"MSFT\",\"quantity\":4,\"reason\":\"protective_stop_fill\"}"
        };
        var exit = (await intents.ReserveAsync(run, exitReservation)).Intent;
        await events.TransitionAsync(new OrderTransitionRequest(
            exit.ClientOrderId,
            OrderState.Intent,
            OrderState.Submitted,
            "engine",
            exit.CreatedAtUtc.AddSeconds(3),
            PayloadJson: exit.RequestJson));
        await events.TransitionAsync(new OrderTransitionRequest(
            exit.ClientOrderId,
            OrderState.Submitted,
            OrderState.Acked,
            "broker_rest",
            exit.CreatedAtUtc.AddSeconds(4),
            exit.CreatedAtUtc.AddSeconds(4),
            "broker-exit"));

        await lifecycle.ApplyBrokerUpdateWithFillAsync(
            CreateOrderUpdate(exit, "broker-exit", OrderStatus.Filled, 4m, 99m, 5),
            new OrderFillProjection(
                "MSFT",
                "sell",
                4m,
                99m,
                "exit-fill-1",
                AuthoritativePositionQuantity: 0m,
                AuthoritativeLastFillQuantity: 4m,
                AuthoritativeLastFillPrice: 99m));

        var retained = await intents.GetRiskReservationByIntentIdAsync(entry.IntentId);
        Assert.Equal(PortfolioRiskReservationState.PartiallyFilled, retained?.State);
        Assert.Equal(6m, retained?.PendingQuantity);
        Assert.Equal(0m, retained?.OpenPositionQuantity);
        Assert.Equal(600m, retained?.ReservedGrossExposure);
        Assert.Equal(12m, retained?.ReservedPortfolioRisk);
        Assert.Equal("filled_quantity_flat_pending_entry_remainder", retained?.ReleaseReason);
    }

    [Fact]
    public async Task ApplyBrokerUpdateWithFillAsync_OlderTimestampWithNewFill_AdvancesQuantityAndKeepsBrokerWatermark()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var run = CreateRun(Guid.NewGuid());
        var intentClock = new FixedTimeProvider(
            new DateTimeOffset(2026, 7, 21, 14, 30, 0, TimeSpan.Zero));
        var intents = new SqliteOrderIntentRepository(factory, intentClock);
        var events = new SqliteOrderEventRepository(factory);
        var lifecycle = new OrderLifecycleService(
            events,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 21, 14, 30, 30, TimeSpan.Zero)));
        await InitializeAsync(options);
        var intent = (await intents.ReserveAsync(
            run,
            CreateReservation(Guid.NewGuid(), "MSFT"))).Intent;
        await events.TransitionAsync(new OrderTransitionRequest(
            intent.ClientOrderId,
            OrderState.Intent,
            OrderState.Submitted,
            "engine",
            intent.CreatedAtUtc,
            PayloadJson: intent.RequestJson));
        var restTimestamp = intent.CreatedAtUtc.AddSeconds(20);
        await events.TransitionAsync(new OrderTransitionRequest(
            intent.ClientOrderId,
            OrderState.Submitted,
            OrderState.Acked,
            "broker_rest",
            restTimestamp,
            restTimestamp,
            "broker-out-of-order"));
        await lifecycle.ApplyBrokerUpdateWithFillAsync(
            CreateOrderUpdate(intent, "broker-out-of-order", OrderStatus.PartiallyFilled, 4m, 100m, 21),
            new OrderFillProjection("MSFT", "buy", 4m, 100m, "rest-fill-4"));
        var current = await events.GetCurrentAsync(intent.ClientOrderId);
        var delayedButLarger = CreateOrderUpdate(
            intent,
            "broker-out-of-order",
            OrderStatus.PartiallyFilled,
            6m,
            101m,
            10);

        var advanced = await lifecycle.ApplyBrokerUpdateWithFillAsync(
            delayedButLarger,
            new OrderFillProjection(
                "MSFT",
                "buy",
                6m,
                101m,
                "stream-fill-6",
                AuthoritativePositionQuantity: 6m,
                AuthoritativeLastFillQuantity: 2m,
                AuthoritativeLastFillPrice: 103m));

        Assert.Equal(6m, advanced.FilledQuantity);
        Assert.Equal(current!.BrokerTimestampUtc, advanced.BrokerTimestampUtc);
        await using var verification = new TradingFlowDbContext(options);
        Assert.Equal(6m, (await verification.PositionEvents
            .AsNoTracking()
            .OrderByDescending(record => record.PositionEventId)
            .FirstAsync()).QuantityAfter);
    }

    [Fact]
    public async Task ApplyBrokerUpdateWithFillAsync_TerminalRestartRepair_AppendsMissingDeltaOnce()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var run = CreateRun(Guid.NewGuid());
        var intents = new SqliteOrderIntentRepository(factory, new FixedTimeProvider(new DateTimeOffset(2026, 7, 21, 14, 30, 0, TimeSpan.Zero)));
        var firstEvents = new SqliteOrderEventRepository(factory);
        var firstLifecycle = new OrderLifecycleService(firstEvents);
        await InitializeAsync(options);
        var intent = (await intents.ReserveAsync(
            run,
            CreateReservation(Guid.NewGuid(), "MSFT"))).Intent;
        await firstEvents.TransitionAsync(new OrderTransitionRequest(
            intent.ClientOrderId,
            OrderState.Intent,
            OrderState.Submitted,
            "engine",
            intent.CreatedAtUtc,
            PayloadJson: intent.RequestJson));
        await firstEvents.TransitionAsync(new OrderTransitionRequest(
            intent.ClientOrderId,
            OrderState.Submitted,
            OrderState.Acked,
            "broker_rest",
            intent.CreatedAtUtc,
            intent.CreatedAtUtc.AddSeconds(2),
            "broker-terminal-repair"));
        await firstLifecycle.ApplyBrokerUpdateWithFillAsync(
            CreateOrderUpdate(intent, "broker-terminal-repair", OrderStatus.PartiallyFilled, 4m, 100m, 3),
            new OrderFillProjection(
                "MSFT", "buy", 4m, 100m, "stream-fill-1",
                AuthoritativePositionQuantity: 4m,
                AuthoritativeLastFillQuantity: 4m,
                AuthoritativeLastFillPrice: 100m));

        var restartedEvents = new SqliteOrderEventRepository(factory);
        var restartedLifecycle = new OrderLifecycleService(restartedEvents);
        var terminalUpdate = CreateOrderUpdate(
            intent,
            "broker-terminal-repair",
            OrderStatus.Canceled,
            6m,
            101m,
            5);
        var repair = new OrderFillProjection(
            "MSFT",
            "buy",
            6m,
            101m,
            "rest:broker-terminal-repair:6");

        await restartedLifecycle.ApplyBrokerUpdateWithFillAsync(terminalUpdate, repair);
        await restartedLifecycle.ApplyBrokerUpdateWithFillAsync(terminalUpdate, repair);

        await using var verification = new TradingFlowDbContext(options);
        var fills = await verification.PositionEvents.AsNoTracking()
            .OrderBy(record => record.PositionEventId)
            .ToArrayAsync();
        Assert.Equal(2, fills.Length);
        Assert.Equal(6m, fills[^1].QuantityAfter);
        Assert.Equal(103m, fills[^1].FillPrice);
        var risk = await verification.PortfolioRiskReservations.AsNoTracking().SingleAsync();
        Assert.Equal(PortfolioRiskReservationState.BackingOpenPosition, risk.State);
        Assert.Equal(6m, risk.CumulativeFilledQuantity);
        Assert.Equal(0m, risk.PendingQuantity);
    }

    [Fact]
    public async Task ApplyBrokerUpdateWithFillAsync_InvalidPositionProjection_RollsBackAllJournals()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var run = CreateRun(Guid.NewGuid());
        var intents = new SqliteOrderIntentRepository(factory, new FixedTimeProvider(new DateTimeOffset(2026, 7, 21, 14, 30, 0, TimeSpan.Zero)));
        var events = new SqliteOrderEventRepository(factory);
        var lifecycle = new OrderLifecycleService(events);
        await InitializeAsync(options);
        var intent = (await intents.ReserveAsync(
            run,
            CreateReservation(Guid.NewGuid(), "MSFT"))).Intent;
        await events.TransitionAsync(new OrderTransitionRequest(
            intent.ClientOrderId,
            OrderState.Intent,
            OrderState.Submitted,
            "engine",
            intent.CreatedAtUtc,
            PayloadJson: intent.RequestJson));
        await events.TransitionAsync(new OrderTransitionRequest(
            intent.ClientOrderId,
            OrderState.Submitted,
            OrderState.Acked,
            "broker_rest",
            intent.CreatedAtUtc,
            intent.CreatedAtUtc.AddSeconds(2),
            "broker-invalid-fill"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lifecycle.ApplyBrokerUpdateWithFillAsync(
                CreateOrderUpdate(intent, "broker-invalid-fill", OrderStatus.PartiallyFilled, 4m, 100m, 3),
                new OrderFillProjection(
                    "MSFT", "buy", 4m, 100m, "bad-fill",
                    AuthoritativePositionQuantity: 99m,
                    AuthoritativeLastFillQuantity: 4m,
                    AuthoritativeLastFillPrice: 100m)));

        await using var verification = new TradingFlowDbContext(options);
        Assert.Empty(await verification.PositionEvents.AsNoTracking().ToArrayAsync());
        Assert.Equal(
            OrderState.Acked,
            (await events.GetCurrentAsync(intent.ClientOrderId))!.State);
        var risk = await verification.PortfolioRiskReservations.AsNoTracking().SingleAsync();
        Assert.Equal(PortfolioRiskReservationState.BrokerAccepted, risk.State);
        Assert.Equal(0m, risk.CumulativeFilledQuantity);
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
        var repository = new SqliteOrderIntentRepository(new TestDbContextFactory(options), new FixedTimeProvider(new DateTimeOffset(2026, 7, 21, 14, 30, 0, TimeSpan.Zero)));
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
        var repository = new SqliteOrderIntentRepository(new TestDbContextFactory(options), new FixedTimeProvider(new DateTimeOffset(2026, 7, 21, 14, 30, 0, TimeSpan.Zero)));
        var run = CreateRun(Guid.NewGuid());
        var reservation = CreateReservation(Guid.NewGuid(), "MSFT");

        await using (var context = new TradingFlowDbContext(options))
        {
            await new TradingFlowDatabaseInitializer().InitializeAsync(context);
        }

        await repository.ReserveAsync(run, reservation);
        var changed = reservation with
        {
            RequestedQuantity = reservation.RequestedQuantity + 1m,
            PortfolioRisk = reservation.PortfolioRisk! with { ProposedNotional = 1_100m }
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.ReserveAsync(run, changed));
        Assert.Contains("different order request", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReserveAsync_ReusedIntentWithChangedProfile_FailsClosed()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var repository = new SqliteOrderIntentRepository(new TestDbContextFactory(options), new FixedTimeProvider(new DateTimeOffset(2026, 7, 21, 14, 30, 0, TimeSpan.Zero)));
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
        var repository = new SqliteOrderIntentRepository(new TestDbContextFactory(options), new FixedTimeProvider(new DateTimeOffset(2026, 7, 21, 14, 30, 0, TimeSpan.Zero)));
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
            RequestJson = "{\"positionGenerationIdentity\":\"ledger:1:entry-1\"}",
            DispatchExpiresAtUtc = null,
            PortfolioRisk = null
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
        var repository = new SqliteOrderIntentRepository(new TestDbContextFactory(options), new FixedTimeProvider(new DateTimeOffset(2026, 7, 21, 14, 30, 0, TimeSpan.Zero)));
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
        var intents = new SqliteOrderIntentRepository(factory, new FixedTimeProvider(new DateTimeOffset(2026, 7, 21, 14, 30, 0, TimeSpan.Zero)));
        var events = new SqliteOrderEventRepository(factory);
        var run = CreateRun(Guid.NewGuid());

        await using (var context = new TradingFlowDbContext(options))
        {
            await new TradingFlowDatabaseInitializer().InitializeAsync(context);
        }

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
        var active = (await intents.ReserveAsync(
            run,
            CreateReservation(Guid.NewGuid(), "MSFT", "SWGA"))).Intent;

        var result = await intents.ListActiveForSymbolAsync("paper-account", "msft");

        var item = Assert.Single(result);
        Assert.Equal(active.ClientOrderId, item.ClientOrderId);
        Assert.Equal("SWGA", item.StrategyId);
        Assert.Equal(OrderState.Intent, item.State);
    }

    [Fact]
    public async Task ReserveAsync_ParallelRepositories_AtomicallyEnforceLastPositionSlot()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        await using (var context = new TradingFlowDbContext(options))
        {
            await new TradingFlowDatabaseInitializer().InitializeAsync(context);
        }

        var run = CreateRun(Guid.NewGuid());
        var repositories = new[]
        {
            new SqliteOrderIntentRepository(factory, new FixedTimeProvider(new DateTimeOffset(2026, 7, 21, 14, 30, 0, TimeSpan.Zero))),
            new SqliteOrderIntentRepository(factory, new FixedTimeProvider(new DateTimeOffset(2026, 7, 21, 14, 30, 0, TimeSpan.Zero)))
        };
        var requests = new[]
        {
            WithRiskLimits(CreateReservation(Guid.NewGuid(), "MSFT"), maxPositions: 1),
            WithRiskLimits(CreateReservation(Guid.NewGuid(), "NVDA"), maxPositions: 1)
        };
        await new SqliteOperationalTradingLimitStore(factory).SaveAsync("paper", 5, 1, 0,
            new DateTimeOffset(2026, 7, 21, 14, 30, 0, TimeSpan.Zero));
        var outcomes = await Task.WhenAll(requests.Select(async (request, index) =>
        {
            try
            {
                return (Result: await repositories[index].ReserveAsync(run, request), Error: (Exception?)null);
            }
            catch (Exception exception)
            {
                return (Result: (OrderIntentReservationResult?)null, Error: exception);
            }
        }));

        Assert.Single(outcomes, outcome => outcome.Result is not null);
        var rejection = Assert.IsType<PortfolioRiskReservationRejectedException>(
            Assert.Single(outcomes, outcome => outcome.Error is not null).Error);
        Assert.Equal("position_slot_limit", rejection.ReasonCode);
        await using var verification = new TradingFlowDbContext(options);
        Assert.Single(await verification.OrderIntents.AsNoTracking().ToArrayAsync());
        Assert.Single(await verification.PortfolioRiskReservations.AsNoTracking().ToArrayAsync());
        Assert.Equal(2, await verification.RiskEvents.CountAsync());
    }

    [Fact]
    public async Task ReserveAsync_StrategyRiskRejection_TerminalizesTriggeredCandidateWithoutIntent()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        var repository = CreateCandidateRepository(factory);
        var run = CreateRun(Guid.NewGuid());
        var candidateId = Guid.NewGuid();
        var semanticHash = new string('c', 64);
        await SeedTriggeredCandidateAsync(options, run, candidateId, "MSFT", semanticHash);
        var reservation = CreateCandidateReservation(
            Guid.NewGuid(), candidateId, "MSFT", semanticHash);
        reservation = reservation with
        {
            PortfolioRisk = reservation.PortfolioRisk! with
            {
                BrokerPositionSymbols = new HashSet<string>(["AAPL"], StringComparer.Ordinal),
                MaxPositions = 1
            }
        };
        await new SqliteOperationalTradingLimitStore(factory).SaveAsync("paper", 5, 1, 0,
            new DateTimeOffset(2026, 7, 21, 14, 30, 0, TimeSpan.Zero));

        var error = await Assert.ThrowsAsync<PortfolioRiskReservationRejectedException>(() =>
            repository.ReserveAsync(run, reservation));

        Assert.Equal("position_slot_limit", error.ReasonCode);
        await using var verification = new TradingFlowDbContext(options);
        var candidate = await verification.Candidates.AsNoTracking().SingleAsync();
        Assert.Equal(StrategyCandidateState.RiskBlocked, candidate.State);
        Assert.Equal(5, candidate.Version);
        Assert.Empty(await verification.OrderIntents.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verification.PortfolioRiskReservations.AsNoTracking().ToArrayAsync());
        var transition = await verification.CandidateTransitions
            .AsNoTracking()
            .SingleAsync(record => record.NewState == StrategyCandidateState.RiskBlocked);
        Assert.Equal("position_slot_limit", transition.ReasonCode);
        Assert.Single(await verification.RiskEvents.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task OrderLifecycle_TerminalZeroFill_ReleasesCapacityIdempotently()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        await using (var context = new TradingFlowDbContext(options))
        {
            await new TradingFlowDatabaseInitializer().InitializeAsync(context);
        }

        var run = CreateRun(Guid.NewGuid());
        var intents = new SqliteOrderIntentRepository(factory, new FixedTimeProvider(new DateTimeOffset(2026, 7, 21, 14, 30, 0, TimeSpan.Zero)));
        var events = new SqliteOrderEventRepository(factory);
        var first = (await intents.ReserveAsync(run, CreateReservation(Guid.NewGuid(), "MSFT"))).Intent;
        await events.TransitionAsync(new OrderTransitionRequest(
            first.ClientOrderId,
            OrderState.Intent,
            OrderState.Submitted,
            "engine",
            first.CreatedAtUtc.AddSeconds(1)));
        await events.TransitionAsync(new OrderTransitionRequest(
            first.ClientOrderId,
            OrderState.Submitted,
            OrderState.Rejected,
            "engine",
            first.CreatedAtUtc.AddSeconds(2)));

        var released = await intents.GetRiskReservationByIntentIdAsync(first.IntentId);
        Assert.Equal(PortfolioRiskReservationState.Released, released?.State);
        Assert.Equal("rejected", released?.ReleaseReason);
        var replacement = await intents.ReserveAsync(run, CreateReservation(Guid.NewGuid(), "MSFT"));
        Assert.True(replacement.Created);
        Assert.Equal(2, replacement.Intent.SequenceNumber);
    }

    [Fact]
    public async Task ReserveAsync_AccountRequestStartedBeforeAcknowledgement_DoesNotReleaseLocalBuyingPower()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        await using (var context = new TradingFlowDbContext(options))
        {
            await new TradingFlowDatabaseInitializer().InitializeAsync(context);
        }

        var run = CreateRun(Guid.NewGuid());
        var intents = new SqliteOrderIntentRepository(factory, new FixedTimeProvider(new DateTimeOffset(2026, 7, 21, 14, 30, 0, TimeSpan.Zero)));
        var events = new SqliteOrderEventRepository(factory);
        var first = (await intents.ReserveAsync(run, CreateReservation(Guid.NewGuid(), "MSFT"))).Intent;
        var acknowledgedAtUtc = first.CreatedAtUtc.AddSeconds(2);
        await events.TransitionAsync(new OrderTransitionRequest(
            first.ClientOrderId,
            OrderState.Intent,
            OrderState.Submitted,
            "engine",
            first.CreatedAtUtc.AddSeconds(1)));
        await events.TransitionAsync(new OrderTransitionRequest(
            first.ClientOrderId,
            OrderState.Submitted,
            OrderState.Acked,
            "broker_rest",
            acknowledgedAtUtc,
            acknowledgedAtUtc,
            "broker-first"));

        var second = CreateReservation(Guid.NewGuid(), "AAPL");
        second = second with
        {
            PortfolioRisk = second.PortfolioRisk! with
            {
                AvailableBuyingPower = 1_000m,
                AccountSnapshotRequestedAtUtc = acknowledgedAtUtc.AddSeconds(-1),
                AccountSnapshotObservedAtUtc = acknowledgedAtUtc.AddSeconds(1)
            }
        };

        var error = await Assert.ThrowsAsync<PortfolioRiskReservationRejectedException>(() =>
            intents.ReserveAsync(run, second));

        Assert.Equal("buying_power_reserved", error.ReasonCode);
        await using var verification = new TradingFlowDbContext(options);
        Assert.Single(await verification.OrderIntents.AsNoTracking().ToArrayAsync());
        Assert.Single(await verification.PortfolioRiskReservations.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task OrderLifecycle_PartialFillThenCancel_RetainsOnlyFilledRisk()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        await using (var context = new TradingFlowDbContext(options))
        {
            await new TradingFlowDatabaseInitializer().InitializeAsync(context);
        }

        var run = CreateRun(Guid.NewGuid());
        var intents = new SqliteOrderIntentRepository(factory, new FixedTimeProvider(new DateTimeOffset(2026, 7, 21, 14, 30, 0, TimeSpan.Zero)));
        var events = new SqliteOrderEventRepository(factory);
        var intent = (await intents.ReserveAsync(run, CreateReservation(Guid.NewGuid(), "MSFT"))).Intent;
        var brokerOrderId = "broker-partial";
        await events.TransitionAsync(new OrderTransitionRequest(
            intent.ClientOrderId,
            OrderState.Intent,
            OrderState.Submitted,
            "engine",
            intent.CreatedAtUtc.AddSeconds(1)));
        await events.TransitionAsync(new OrderTransitionRequest(
            intent.ClientOrderId,
            OrderState.Submitted,
            OrderState.Acked,
            "broker_rest",
            intent.CreatedAtUtc.AddSeconds(2),
            intent.CreatedAtUtc.AddSeconds(2),
            brokerOrderId));
        await events.TransitionAsync(new OrderTransitionRequest(
            intent.ClientOrderId,
            OrderState.Acked,
            OrderState.PartiallyFilled,
            "broker_stream",
            intent.CreatedAtUtc.AddSeconds(3),
            intent.CreatedAtUtc.AddSeconds(3),
            brokerOrderId,
            FilledQuantity: 4m,
            FillPrice: 100m));
        await events.TransitionAsync(new OrderTransitionRequest(
            intent.ClientOrderId,
            OrderState.PartiallyFilled,
            OrderState.Canceled,
            "broker_stream",
            intent.CreatedAtUtc.AddSeconds(4),
            intent.CreatedAtUtc.AddSeconds(4),
            brokerOrderId,
            FilledQuantity: 4m,
            FillPrice: 100m));

        var retained = await intents.GetRiskReservationByIntentIdAsync(intent.IntentId);
        Assert.Equal(PortfolioRiskReservationState.BackingOpenPosition, retained?.State);
        Assert.Equal(4m, retained?.OpenPositionQuantity);
        Assert.Equal(0m, retained?.PendingQuantity);
        Assert.Equal(400m, retained?.ReservedGrossExposure);
        Assert.Equal(8m, retained?.ReservedPortfolioRisk);
        Assert.Equal("unfilled_remainder_released", retained?.ReleaseReason);

        var nextReservation = CreateReservation(Guid.NewGuid(), "AAPL");
        nextReservation = nextReservation with
        {
            PortfolioRisk = nextReservation.PortfolioRisk! with
            {
                MaxGrossExposure = 1_400m
            }
        };
        var next = await intents.ReserveAsync(run, nextReservation);
        Assert.True(next.Created);
    }

    [Fact]
    public async Task PositionLedger_AuthoritativeFlat_ReleasesPositionBackedCapacity()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var factory = new TestDbContextFactory(options);
        await using (var context = new TradingFlowDbContext(options))
        {
            await new TradingFlowDatabaseInitializer().InitializeAsync(context);
        }

        var run = CreateRun(Guid.NewGuid());
        var intents = new SqliteOrderIntentRepository(factory, new FixedTimeProvider(new DateTimeOffset(2026, 7, 21, 14, 30, 0, TimeSpan.Zero)));
        var events = new SqliteOrderEventRepository(factory);
        var positions = new SqlitePositionLedgerRepository(factory);
        var intent = (await intents.ReserveAsync(run, CreateReservation(Guid.NewGuid(), "MSFT"))).Intent;
        var brokerOrderId = "broker-filled";
        await events.TransitionAsync(new OrderTransitionRequest(
            intent.ClientOrderId, OrderState.Intent, OrderState.Submitted, "engine",
            intent.CreatedAtUtc.AddSeconds(1)));
        await events.TransitionAsync(new OrderTransitionRequest(
            intent.ClientOrderId, OrderState.Submitted, OrderState.Acked, "broker_rest",
            intent.CreatedAtUtc.AddSeconds(2), intent.CreatedAtUtc.AddSeconds(2), brokerOrderId));
        await positions.AppendFillAsync(new PositionFillAppendRequest(
            run, "paper-account", "MSFT", "SWGA", 10m, 10m, 100m, "buy", brokerOrderId,
            intent.ClientOrderId, "execution-entry", "broker_stream",
            intent.CreatedAtUtc.AddSeconds(3), intent.CreatedAtUtc.AddSeconds(3), "{}"));
        await events.TransitionAsync(new OrderTransitionRequest(
            intent.ClientOrderId, OrderState.Acked, OrderState.Filled, "broker_stream",
            intent.CreatedAtUtc.AddSeconds(3), intent.CreatedAtUtc.AddSeconds(3), brokerOrderId,
            FilledQuantity: 10m, FillPrice: 100m));

        await positions.AppendFillAsync(new PositionFillAppendRequest(
            run, "paper-account", "MSFT", "SWGA", 0m, 10m, 105m, "sell", "broker-exit",
            "exit-client-order", "execution-exit", "broker_stream",
            intent.CreatedAtUtc.AddMinutes(30), intent.CreatedAtUtc.AddMinutes(30), "{}"));

        var released = await intents.GetRiskReservationByIntentIdAsync(intent.IntentId);
        Assert.Equal(PortfolioRiskReservationState.Released, released?.State);
        Assert.Equal(0m, released?.OpenPositionQuantity);
        Assert.Equal("authoritative_position_flat", released?.ReleaseReason);
    }

    private static ProductionRun CreateRun(Guid runId) => new()
    {
        RunId = runId,
        SchemaVersion = 1,
        ConfigHash = new string('b', 64),
        CodeVersion = new string('c', 40),
        Profile = "paper",
        Status = "running",
        StartedAtUtc = DateTimeOffset.UtcNow
    };

    // Supply explicit fake account fill history alongside immutable reservation inputs under the fixture clock.
    private static OrderIntentReservation CreateReservation(
        Guid intentId,
        string symbol,
        string strategyId = "SWGA") => new(
        intentId,
        Kind: OrderIntentKind.OperatorEntry,
        CandidateId: null,
        AccountId: "paper-account",
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
        RequestJson: $"{{\"symbol\":\"{symbol}\",\"quantity\":10,\"takeProfitPrice\":104,\"submitOutsideRegularHours\":false}}",
        DispatchExpiresAtUtc: new DateTimeOffset(2026, 7, 21, 14, 35, 0, TimeSpan.Zero),
        PortfolioRisk: new PortfolioRiskReservationRequest(
            "paper-account",
            "swing",
            100_000m,
            100_000m,
            0m,
            0m,
            new HashSet<string>(StringComparer.Ordinal),
            1_000m,
            20m,
            100_000m,
            2_000m,
            20,
            new DateTimeOffset(2026, 7, 21, 14, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 21, 14, 30, 0, TimeSpan.Zero),
            OperationalTradingLimitTestEvidence.Empty("paper-account", new DateTimeOffset(2026, 7, 21, 14, 30, 0, TimeSpan.Zero)), 60));

    private static OrderIntentReservation CreateUnpreparedExitReservation(
        Guid intentId,
        long positionGenerationEventId) =>
        CreateReservation(intentId, "MSFT") with
        {
            Kind = OrderIntentKind.PositionExit,
            Side = "sell",
            OrderType = "market",
            TimeInForce = "day",
            LimitPrice = null,
            StopPrice = null,
            PortfolioRisk = null,
            DispatchExpiresAtUtc = null,
            PositionGenerationEventId = positionGenerationEventId,
            RequestJson = "{\"symbol\":\"MSFT\",\"quantity\":10,\"reason\":\"technical_exit\"}"
        };

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

    private static OrderIntentReservation WithRiskLimits(
        OrderIntentReservation reservation,
        int maxPositions) => reservation with
        {
            PortfolioRisk = reservation.PortfolioRisk! with
            {
                MaxPositions = maxPositions,
                MaxPortfolioRisk = 2_000m
            }
        };

    private static OrderIntentReservation CreateProtectiveReservation(
        Guid intentId,
        string symbol) => CreateReservation(intentId, symbol, "BACKSTOP") with
        {
            Kind = OrderIntentKind.ProtectiveStop,
            Side = "sell",
            OrderType = "stop",
            TimeInForce = "gtc",
            LimitPrice = null,
            DispatchExpiresAtUtc = null,
            PortfolioRisk = null,
            RequestJson = $"{{\"symbol\":\"{symbol}\",\"protectionRevision\":{intentId.GetHashCode()}}}"
        };

    // Insert real candidate and trigger journal rows with optional live dispatch evidence.
    private static async Task SeedTriggeredCandidateAsync(
        DbContextOptions<TradingFlowDbContext> options,
        ProductionRun run,
        Guid candidateId,
        string symbol,
        string semanticHash,
        DateTimeOffset? expiresAtUtc = null,
        string? evidenceJson = null)
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
            ExpiresAtUtc = expiresAtUtc ?? new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero),
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
            EvidenceJson = evidenceJson ?? $"{{\"symbol\":\"{symbol}\",\"decision\":\"triggered\"}}",
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

    private static async Task InitializeAsync(DbContextOptions<TradingFlowDbContext> options)
    {
        await using var context = new TradingFlowDbContext(options);
        await new TradingFlowDatabaseInitializer().InitializeAsync(context);
    }

    private static Task<PositionLedgerSnapshot> SeedPositionAsync(
        IPositionLedgerRepository positions,
        ProductionRun run,
        string accountId,
        string symbol,
        decimal quantity,
        DateTimeOffset timestampUtc) =>
        positions.AppendFillAsync(new PositionFillAppendRequest(
            run,
            accountId,
            symbol,
            "SWGA",
            quantity,
            quantity,
            100m,
            "buy",
            $"entry-{symbol.ToLowerInvariant()}",
            $"entry-client-{symbol.ToLowerInvariant()}",
            $"entry-fill-{accountId}-{symbol}",
            "broker_rest",
            timestampUtc,
            timestampUtc,
            "{\"source\":\"test\"}"));

    /// <summary>Seed an acknowledged order with account-fill evidence tied to this cancellation fixture's explicit clock.</summary>
    private static async Task<OrderIntentRecord> SeedAcknowledgedOrderAsync(
        SqliteOrderIntentRepository intents,
        SqliteOrderEventRepository events,
        ProductionRun run,
        Guid intentId,
        string symbol,
        string brokerOrderId,
        DateTimeOffset timestampUtc,
        bool protective = false,
        string? timeInForce = null)
    {
        var reservation = protective
            ? CreateProtectiveReservation(intentId, symbol)
            : CreateReservation(intentId, symbol);
        if (timeInForce is not null)
        {
            reservation = reservation with { TimeInForce = timeInForce };
        }
        if (reservation.PortfolioRisk is { } risk)
            reservation = reservation with { PortfolioRisk = risk with
            {
                AccountSnapshotRequestedAtUtc = timestampUtc,
                AccountSnapshotObservedAtUtc = timestampUtc,
                FillHistory = OperationalTradingLimitTestEvidence.Empty(reservation.AccountId, timestampUtc)
            } };
        var reserved = await intents.ReserveAsync(run, reservation);
        await events.TransitionAsync(new OrderTransitionRequest(
            reserved.Intent.ClientOrderId,
            OrderState.Intent,
            OrderState.Submitted,
            "engine",
            timestampUtc,
            PayloadJson: reservation.RequestJson));
        await events.TransitionAsync(new OrderTransitionRequest(
            reserved.Intent.ClientOrderId,
            OrderState.Submitted,
            OrderState.Acked,
            "broker_rest",
            timestampUtc,
            BrokerTimestampUtc: timestampUtc,
            BrokerOrderId: brokerOrderId,
            PayloadJson: "{\"status\":\"new\"}"));
        return reserved.Intent;
    }

    // Build the actual durable command service with only broker and dispatch mocked by its callers.
    private static OrderSubmissionService CreateCommandService(
        SqliteOrderIntentRepository intents,
        SqliteOrderEventRepository events,
        TimeProvider clock,
        IProtectiveStopReplacementRepository? stopReplacements = null,
        IPositionLedgerRepository? positions = null) => new(
        intents,
        new Mock<IOrderDispatchService>(MockBehavior.Strict).Object,
        new Mock<IEntryGateChain>(MockBehavior.Strict).Object,
        NullLogger<OrderSubmissionService>.Instance,
        positions: positions,
        events: events,
        lifecycle: new OrderLifecycleService(events, clock),
        accountBinding: new BrokerAccountBindingService(
            new BrokerAccountBindingOptions("paper-account")),
        stopReplacements: stopReplacements,
        timeProvider: clock);

    private static ActiveBrokerOrder CreateProtectiveBrokerOrder(
        string brokerOrderId,
        string clientOrderId,
        string symbol,
        decimal stopPrice,
        DateTimeOffset timestampUtc) => new(
        brokerOrderId,
        symbol,
        "sell",
        "new",
        "stop",
        null,
        stopPrice,
        10m,
        timestampUtc,
        clientOrderId,
        0m,
        null,
        timestampUtc,
        TimeInForce: "gtc",
        OrderClass: "simple",
        ExtendedHours: false);

    private static ProtectiveStopReplacementReservation CreateReplacementReservation(
        Guid commandId,
        OrderIntentRecord owner,
        decimal stopPrice,
        DateTimeOffset requestedAtUtc) => new(
            commandId,
            owner.AccountId,
            owner.ClientOrderId,
            "broker-stop",
            ProtectiveStopReplacementIdFactory.CreateReplacementClientOrderId(commandId),
            owner.Symbol,
            stopPrice,
            "atr_trailing_stop_raise",
            requestedAtUtc,
            owner.RunId,
            owner.SchemaVersion,
            owner.ConfigHash,
            owner.CodeVersion);

    private static PositionExitSubmission CreatePositionExit(
        ProductionRun run,
        DateTimeOffset now) => new(
        new ExecutionRunContext(
            run.RunId,
            run.Profile,
            run.ConfigHash,
            run.CodeVersion,
            run.StartedAtUtc),
        "MSFT",
        10m,
        "technical_exit",
        now);

    private static async Task<OrderIntentRecord> SeedActiveProtectionAsync(
        SqliteOrderIntentRepository intents,
        IOrderEventRepository events,
        ProductionRun run,
        PositionLedgerSnapshot position,
        decimal quantity,
        decimal stopPrice,
        DateTimeOffset createdAtUtc)
    {
        var identity = $"ledger:{position.PositionGenerationEventId}:{position.PositionGenerationClientOrderId}";
        const int revision = 0;
        var intentId = ProtectiveOrderIntentIdFactory.Create(
            position.AccountId,
            position.Symbol,
            "sell",
            identity,
            revision);
        var requestJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            strategyId = "BACKSTOP",
            symbol = position.Symbol,
            side = "sell",
            orderType = "stop",
            timeInForce = "gtc",
            quantity,
            stopPrice,
            PositionGenerationIdentity = identity,
            ProtectionRevision = revision,
            sessionDate = new DateOnly(2026, 7, 21)
        });
        var reserved = await intents.ReserveAsync(
            run,
            new OrderIntentReservation(
                intentId,
                OrderIntentKind.ProtectiveStop,
                null,
                position.AccountId,
                "BACKSTOP",
                position.Symbol,
                "sell",
                "stop",
                "gtc",
                quantity,
                null,
                stopPrice,
                new DateOnly(2026, 7, 21),
                createdAtUtc,
                requestJson,
                PositionGenerationEventId: position.PositionGenerationEventId));
        await events.TransitionAsync(new OrderTransitionRequest(
            reserved.Intent.ClientOrderId,
            OrderState.Intent,
            OrderState.Submitted,
            "engine",
            createdAtUtc,
            PayloadJson: requestJson));
        await events.TransitionAsync(new OrderTransitionRequest(
            reserved.Intent.ClientOrderId,
            OrderState.Submitted,
            OrderState.Acked,
            "broker_rest",
            createdAtUtc,
            BrokerTimestampUtc: createdAtUtc,
            BrokerOrderId: "broker-stop-1",
            PayloadJson: "{\"status\":\"new\"}"));
        return reserved.Intent;
    }

    private static Mock<IBrokerClient> CreateProtectedExitBroker(
        DateTimeOffset now,
        OrderIntentRecord protective,
        ActiveBrokerOrder brokerStop,
        Func<bool> isProtectionCanceled,
        Action cancelProtection,
        decimal brokerPositionQuantity)
    {
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateAccountSnapshot("paper-account", now));
        broker.Setup(client => client.GetSessionAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TradingSessionSnapshot(
                DateOnly.FromDateTime(now.UtcDateTime),
                EquityTradingSession.Regular,
                now,
                now.AddHours(-1),
                now.AddHours(5)));
        broker.Setup(client => client.GetOrderByClientOrderIdAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string clientOrderId, CancellationToken _) =>
                clientOrderId == protective.ClientOrderId
                    ? isProtectionCanceled()
                        ? brokerStop with { Status = "canceled" }
                        : brokerStop
                    : null);
        broker.Setup(client => client.CancelOrderAsync(
                brokerStop.OrderId, It.IsAny<CancellationToken>()))
            .Callback(cancelProtection)
            .ReturnsAsync(true);
        broker.Setup(client => client.GetOpenOrdersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => isProtectionCanceled()
                ? []
                : [brokerStop]);
        broker.Setup(client => client.GetOpenPositionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new BrokerPosition(
                    "MSFT", "long", brokerPositionQuantity, 100m, 100m, 0m)
            ]);
        return broker;
    }

    private static OrderSubmissionService CreateProtectedExitService(
        SqliteOrderIntentRepository intents,
        IOrderEventRepository events,
        IPositionLedgerRepository positions,
        TimeProvider clock,
        IEntryAdmissionControl? admission = null)
    {
        var accountBinding = new BrokerAccountBindingService(
            new BrokerAccountBindingOptions("paper-account"));
        var dispatcher = new OrderDispatchService(
            intents,
            intents,
            events,
            positions,
            accountBinding,
            new OrderLifecycleService(events, clock),
            new OrderDispatchOptions(30, 30, 5, 4),
            clock,
            NullLogger<OrderDispatchService>.Instance,
            admission);
        return new OrderSubmissionService(
            intents,
            dispatcher,
            new Mock<IEntryGateChain>(MockBehavior.Strict).Object,
            NullLogger<OrderSubmissionService>.Instance,
            positions,
            events: events,
            lifecycle: new OrderLifecycleService(events, clock),
            accountBinding: accountBinding,
            timeProvider: clock,
            admission: admission);
    }

    private static Mock<IBrokerClient> CreateExitBroker(
        string accountId,
        DateTimeOffset now,
        string brokerOrderId)
    {
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateAccountSnapshot(accountId, now));
        broker.Setup(client => client.GetSessionAsync(
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TradingSessionSnapshot(
                DateOnly.FromDateTime(now.UtcDateTime),
                EquityTradingSession.Regular,
                now,
                now.AddHours(-1),
                now.AddHours(5)));
        broker.Setup(client => client.GetOrderByClientOrderIdAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((ActiveBrokerOrder?)null);
        broker.Setup(client => client.SubmitExitOrderAsync(
                It.IsAny<BrokerExitOrder>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrokerOrderReceipt(brokerOrderId, now));
        broker.Setup(client => client.GetOpenPositionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new BrokerPosition("MSFT", "long", 10m, 100m, 100m, 0m)]);
        return broker;
    }

    private static BrokerAccountSnapshot CreateAccountSnapshot(
        string accountId,
        DateTimeOffset now) => new(
        accountId,
        "ACTIVE",
        false,
        false,
        false,
        true,
        100_000m,
        100_000m,
        100_000m,
        0m,
        0m,
        now,
        now);

    private static OrderUpdate CreateOrderUpdate(
        OrderIntentRecord intent,
        string brokerOrderId,
        OrderStatus status,
        decimal cumulativeQuantity,
        decimal cumulativeAveragePrice,
        int secondsAfterIntent) => new(
        brokerOrderId,
        intent.ClientOrderId,
        intent.Symbol,
        intent.Side,
        status,
        cumulativeQuantity,
        cumulativeAveragePrice,
        0m,
        null,
        null,
        intent.CreatedAtUtc.AddSeconds(secondsAfterIntent),
        BrokerUpdateSource.BrokerRest);

    private static OrderDispatchService CreateDispatcher(
        SqliteOrderIntentRepository intents,
        SqliteOrderEventRepository events,
        TimeProvider clock,
        IPositionLedgerRepository? positions = null,
        string expectedAccountId = "paper-account") => new(
        intents,
        intents,
        events,
        positions ?? new Mock<IPositionLedgerRepository>(MockBehavior.Strict).Object,
        new BrokerAccountBindingService(new BrokerAccountBindingOptions(expectedAccountId)),
        new OrderLifecycleService(events),
        new OrderDispatchOptions(30, 30, 5, 4),
        clock,
        NullLogger<OrderDispatchService>.Instance);

    private static Mock<IBrokerClient> CreateDispatchBroker(
        OrderIntentRecord intent,
        ActiveBrokerOrder? brokerOrder,
        string submittedOrderId)
    {
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrokerAccountSnapshot(
                intent.AccountId,
                "ACTIVE",
                false,
                false,
                false,
                true,
                100_000m,
                100_000m,
                100_000m,
                0m,
                0m,
                intent.CreatedAtUtc,
                intent.CreatedAtUtc));
        broker.Setup(client => client.GetOrderByClientOrderIdAsync(
                intent.ClientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(brokerOrder);
        broker.Setup(client => client.SubmitOrderAsync(
                It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrokerOrderReceipt(submittedOrderId, intent.CreatedAtUtc.AddSeconds(1)));
        return broker;
    }

    private static ActiveBrokerOrder CreateMatchingBrokerOrder(
        OrderIntentRecord intent,
        string brokerOrderId,
        string status,
        decimal filledQuantity = 0m,
        decimal? filledAveragePrice = null) => new(
        brokerOrderId,
        intent.Symbol,
        intent.Side,
        status,
        intent.OrderType,
        intent.LimitPrice,
        null,
        intent.RequestedQuantity,
        intent.CreatedAtUtc,
        intent.ClientOrderId,
        filledQuantity,
        filledAveragePrice,
        intent.CreatedAtUtc.AddSeconds(1),
        TimeInForce: intent.TimeInForce,
        OrderClass: "simple",
        ExtendedHours: false);

    private static async Task<object> CaptureDispatchAsync(
        IOrderDispatchService dispatcher,
        Guid intentId,
        IBrokerClient broker)
    {
        try
        {
            return await dispatcher.DispatchAsync(intentId, broker, CancellationToken.None);
        }
        catch (OrderDispatchPendingException exception)
        {
            return exception;
        }
    }

    private static async Task<object> CaptureExitAsync(
        IOrderSubmissionService service,
        PositionExitSubmission submission,
        IBrokerClient broker)
    {
        try
        {
            return await service.SubmitPositionExitAsync(
                submission,
                broker,
                CancellationToken.None);
        }
        catch (PositionExitReservationRejectedException exception)
        {
            return exception;
        }
    }

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

    private sealed class FaultingOrderEventRepository(
        IOrderEventRepository inner,
        Func<OrderTransitionRequest, bool> shouldFail) : IOrderEventRepository
    {
        public Task<OrderStateSnapshot?> GetCurrentAsync(
            string clientOrderId,
            CancellationToken cancellationToken = default) =>
            inner.GetCurrentAsync(clientOrderId, cancellationToken);

        public Task<IReadOnlyList<OrderStateSnapshot>> ListReconcilableAsync(
            string accountId,
            CancellationToken cancellationToken = default) =>
            inner.ListReconcilableAsync(accountId, cancellationToken);

        public Task<OrderTransitionResult> TransitionAsync(
            OrderTransitionRequest request,
            CancellationToken cancellationToken = default) =>
            shouldFail(request)
                ? Task.FromException<OrderTransitionResult>(
                    new IOException("simulated acknowledgement journal failure"))
                : inner.TransitionAsync(request, cancellationToken);

        public Task<OrderStateSnapshot> RecordBrokerReplacementAsync(
            BrokerOrderReplacementTransition request,
            CancellationToken cancellationToken = default) =>
            inner.RecordBrokerReplacementAsync(request, cancellationToken);
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

    private sealed class MutableTimeProvider(DateTimeOffset timestampUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => timestampUtc;

        public void Advance(TimeSpan duration) => timestampUtc = timestampUtc.Add(duration);
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
