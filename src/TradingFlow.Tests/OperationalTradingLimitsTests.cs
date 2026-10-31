using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Data.Orders;
using TradingFlow.Domain.Execution;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Tests;

public sealed class OperationalTradingLimitsTests
{
    // Read defaults without creating records; saving one mode must survive restart and leave the other mode independent.
    [Fact]
    public async Task SavedProfiles_AreSharedPersistentAndDoNotStartWork()
    {
        await using var fixture = await Fixture.CreateAsync();
        var paper = await fixture.Store.GetAsync("paper");
        Assert.Equal(5, paper.MaxNewStocksPerDay);
        Assert.Equal(5, paper.MaxOpenPositions);
        await using (var context = fixture.Factory.CreateDbContext()) Assert.Empty(await context.ApplicationEvents.ToArrayAsync());
        var saved = await fixture.Store.SaveAsync("paper", 2, 3, paper.Revision, fixture.Clock.Now);
        var reloaded = await new SqliteOperationalTradingLimitStore(fixture.Factory).GetAsync("paper");
        Assert.Equal(saved, reloaded);
        Assert.Equal(5, (await fixture.Store.GetAsync("live")).MaxNewStocksPerDay);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.SaveAsync("paper", 1, 2, 0, fixture.Clock.Now));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.GetAsync("backtest"));
        await using var verification = fixture.Factory.CreateDbContext();
        Assert.Empty(await verification.Jobs.ToArrayAsync());
        Assert.Empty(await verification.Wishlists.ToArrayAsync());
    }

    // Separate repository instances must not both reserve the last daily stock slot under concurrent runs.
    [Fact]
    public async Task ConcurrentRuns_ReserveLastDailySlotOnlyOnce()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Store.SaveAsync("paper", 1, 5, 0, fixture.Clock.Now);
        var requests = new[] { fixture.Reservation("MSFT"), fixture.Reservation("DIOD") };
        var results = await Task.WhenAll(requests.Select(async request =>
        {
            try
            {
                await new SqliteOrderIntentRepository(fixture.Factory, fixture.Clock).ReserveAsync(fixture.Run(), request);
                return "accepted";
            }
            catch (PortfolioRiskReservationRejectedException error) { return error.ReasonCode; }
        }));
        Assert.Single(results, result => result == "accepted");
        Assert.Single(results, result => result == "daily_new_stock_limit");
        await using var context = fixture.Factory.CreateDbContext();
        Assert.Single(await context.OrderIntents.ToArrayAsync());
    }

    // Actual entry fills remain counted after closing, while closing fills and partials do not add another stock.
    [Fact]
    public async Task FilledStocks_AreDistinctAcrossReentriesAndClosingFills()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Store.SaveAsync("paper", 1, 5, 0, fixture.Clock.Now);
        var fills = new[]
        {
            fixture.Fill("MSFT", "buy_to_open", 4, 4),
            fixture.Fill("MSFT", "buy_to_open", 6, 10),
            fixture.Fill("MSFT", "sell_to_close", 10, 10),
            fixture.Fill("MSFT", "buy_to_open", 5, 5)
        };
        var history = fixture.History(fills);
        var error = await Assert.ThrowsAsync<PortfolioRiskReservationRejectedException>(() =>
            fixture.Intents.ReserveAsync(fixture.Run(), fixture.Reservation("DIOD", history)));
        Assert.Equal("daily_new_stock_limit", error.ReasonCode);
        Assert.True((await fixture.Intents.ReserveAsync(fixture.Run(), fixture.Reservation("MSFT", history))).Created);
    }

    // A partial execution from an order first filled on an earlier day does not create a second first-entry day.
    [Fact]
    public async Task LaterPartialFill_DoesNotConsumeAnotherDailyStock()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Store.SaveAsync("paper", 1, 5, 0, fixture.Clock.Now);
        var history = fixture.History([fixture.Fill("AAPL", null, 3, 10)]);
        Assert.True((await fixture.Intents.ReserveAsync(fixture.Run(), fixture.Reservation("MSFT", history))).Created);
    }

    // An unfilled rejection releases reserved capacity; a submitted order alone is never reported as an actual entered stock.
    [Fact]
    public async Task UnfilledRejection_ReleasesPendingDailyCapacity()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Store.SaveAsync("paper", 1, 5, 0, fixture.Clock.Now);
        var first = (await fixture.Intents.ReserveAsync(fixture.Run(), fixture.Reservation("MSFT"))).Intent;
        var events = new SqliteOrderEventRepository(fixture.Factory);
        await events.TransitionAsync(new(first.ClientOrderId, OrderState.Intent, OrderState.Submitted, "engine", fixture.Clock.Now));
        await events.TransitionAsync(new(first.ClientOrderId, OrderState.Submitted, OrderState.Rejected, "broker_rest", fixture.Clock.Now,
            BrokerTimestampUtc: fixture.Clock.Now, BrokerOrderId: "rejected-fixture-order", FilledQuantity: 0, PayloadJson: "{}"));
        Assert.True((await fixture.Intents.ReserveAsync(fixture.Run(), fixture.Reservation("DIOD"))).Created);
    }

    // Current broker holdings count even without an owning TradingFlow run, and remain until the broker reports flat.
    [Fact]
    public async Task BrokerHoldings_ConsumeTheSharedOpenPositionLimit()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Store.SaveAsync("paper", 5, 1, 0, fixture.Clock.Now);
        var request = fixture.Reservation("MSFT");
        request = request with { PortfolioRisk = request.PortfolioRisk! with { BrokerPositionSymbols = new HashSet<string>(["AAPL"]) } };
        var error = await Assert.ThrowsAsync<PortfolioRiskReservationRejectedException>(() => fixture.Intents.ReserveAsync(fixture.Run(), request));
        Assert.Equal("position_slot_limit", error.ReasonCode);
        Assert.True((await fixture.Intents.ReserveAsync(fixture.Run(), fixture.Reservation("MSFT"))).Created);
    }

    /// <summary>External pending opening orders consume shared stock slots, including symbols belonging to no TradingFlow run.</summary>
    [Theory]
    [InlineData(1, 5, "daily_new_stock_limit")]
    [InlineData(5, 1, "position_slot_limit")]
    public async Task ExternalPendingOrders_ReserveSharedCapacity(int dailyLimit, int openLimit, string expectedReason)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Store.SaveAsync("paper", dailyLimit, openLimit, 0, fixture.Clock.Now);
        var pending = new ActiveBrokerOrder("external-1", "AAPL", "buy", "new", "limit", 100, null, 10,
            fixture.Clock.Now, "external-client", 0, null, fixture.Clock.Now, PositionIntent: "buy_to_open");
        var history = fixture.History() with { PendingOrders = [pending] };
        var error = await Assert.ThrowsAsync<PortfolioRiskReservationRejectedException>(() =>
            fixture.Intents.ReserveAsync(fixture.Run(), fixture.Reservation("MSFT", history)));
        Assert.Equal(expectedReason, error.ReasonCode);
        var unknown = history with { PendingOrders = [pending with { PositionIntent = null }] };
        var unclassified = await Assert.ThrowsAsync<PortfolioRiskReservationRejectedException>(() =>
            fixture.Intents.ReserveAsync(fixture.Run(), fixture.Reservation("MSFT", unknown)));
        Assert.Equal("unclassified_broker_fill", unclassified.ReasonCode);
        var closing = history with { PendingOrders = [pending with { PositionIntent = "buy_to_close" }] };
        Assert.True((await fixture.Intents.ReserveAsync(fixture.Run(), fixture.Reservation("MSFT", closing))).Created);
    }

    /// <summary>Five pending external entries occupy the five default slots before a sixth stock can reserve capacity.</summary>
    [Fact]
    public async Task FiveExternalPendingStocks_RejectSixthStock()
    {
        await using var fixture = await Fixture.CreateAsync();
        var pending = new[] { "AAPL", "AMD", "DIOD", "BKSY", "NVDA" }.Select(symbol =>
            new ActiveBrokerOrder("external-" + symbol, symbol, "buy", "new", "limit", 100, null, 10,
                fixture.Clock.Now, "client-" + symbol, 0, null, fixture.Clock.Now, PositionIntent: "buy_to_open")).ToArray();
        var error = await Assert.ThrowsAsync<PortfolioRiskReservationRejectedException>(() =>
            fixture.Intents.ReserveAsync(fixture.Run(), fixture.Reservation("MSFT", fixture.History() with { PendingOrders = pending })));
        Assert.Equal("daily_new_stock_limit", error.ReasonCode);
    }

    /// <summary>Null-role stop/target legs of an exact filled owned entry are closing orders, while unrelated parents remain unknown.</summary>
    [Fact]
    public async Task FilledOwnedEntry_BracketChildren_DoNotReserveNewDailyOrHoldingSlots()
    {
        await using var fixture = await Fixture.CreateAsync();
        var today = fixture.Clock.Now;
        fixture.Clock.Now = today.AddDays(-1);
        var parent = (await fixture.Intents.ReserveAsync(fixture.Run(), fixture.Reservation("AAPL"))).Intent;
        var events = new SqliteOrderEventRepository(fixture.Factory);
        await events.TransitionAsync(new(parent.ClientOrderId, OrderState.Intent, OrderState.Submitted, "engine", fixture.Clock.Now));
        await events.TransitionAsync(new(parent.ClientOrderId, OrderState.Submitted, OrderState.Acked, "broker_rest", fixture.Clock.Now,
            BrokerTimestampUtc: fixture.Clock.Now, BrokerOrderId: "filled-parent", PayloadJson: "{}"));
        await events.TransitionAsync(new(parent.ClientOrderId, OrderState.Acked, OrderState.Filled, "broker_rest", fixture.Clock.Now,
            BrokerTimestampUtc: fixture.Clock.Now, BrokerOrderId: "filled-parent", FilledQuantity: 10, FillPrice: 100, PayloadJson: "{}"));
        fixture.Clock.Now = today;
        await fixture.Store.SaveAsync("paper", 1, 2, 0, today);
        var stop = new ActiveBrokerOrder("stop-child", "AAPL", "sell", "new", "stop", null, 98, 10,
            today.AddDays(-1), "stop-client", 0, null, today, ParentClientOrderId: parent.ClientOrderId);
        var target = stop with { OrderId = "target-child", ClientOrderId = "target-client", OrderType = "limit", LimitPrice = 104, StopPrice = null };
        var unknownHistory = fixture.History() with { PendingOrders = [stop with { ParentClientOrderId = "external-parent" }] };
        var unknown = await Assert.ThrowsAsync<PortfolioRiskReservationRejectedException>(() =>
            fixture.Intents.ReserveAsync(fixture.Run(), fixture.Reservation("MSFT", unknownHistory)));
        Assert.Equal("unclassified_broker_fill", unknown.ReasonCode);
        var history = fixture.History() with { PendingOrders = [stop, target] };
        var request = fixture.Reservation("MSFT", history);
        request = request with { PortfolioRisk = request.PortfolioRisk! with { BrokerPositionSymbols = new HashSet<string>(["AAPL"]) } };
        Assert.True((await fixture.Intents.ReserveAsync(fixture.Run(), request)).Created);
    }

    /// <summary>Missing mode values are field errors instead of a null-reference failure.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MissingMode_IsExplicitInputError(string? mode) =>
        Assert.ThrowsAny<ArgumentException>(() => OperationalTradingLimitProfile.Mode(mode!));

    // Missing, stale, partial or unclassified account evidence must reject admission rather than manufacture a zero count.
    [Theory]
    [InlineData("missing", "fill_history_missing")]
    [InlineData("stale", "fill_history_unavailable")]
    [InlineData("partial", "fill_history_unavailable")]
    [InlineData("unknown", "unclassified_broker_fill")]
    public async Task InvalidHistory_RejectsNewEntries(string variant, string expectedReason)
    {
        await using var fixture = await Fixture.CreateAsync();
        var history = fixture.History();
        history = variant switch
        {
            "stale" => history with { WindowEndUtc = fixture.Clock.Now.AddSeconds(-61), RequestedAtUtc = fixture.Clock.Now.AddSeconds(-61) },
            "partial" => history with { Complete = false },
            "unknown" => fixture.History([fixture.Fill("AAPL", null, 10, 10)]),
            _ => history
        };
        var request = fixture.Reservation("MSFT", history);
        if (variant == "missing") request = request with { PortfolioRisk = request.PortfolioRisk! with { FillHistory = null } };
        var error = await Assert.ThrowsAsync<PortfolioRiskReservationRejectedException>(() => fixture.Intents.ReserveAsync(fixture.Run(), request));
        Assert.Equal(expectedReason, error.ReasonCode);
    }

    // Saved-profile edits and expired evidence require a new reviewed entry before the first broker dispatch.
    [Theory]
    [InlineData(true, "trading_limits_changed")]
    [InlineData(false, "entry_capacity_evidence_expired")]
    public async Task FirstDispatch_RechecksProfileAndFreshEvidence(bool changeProfile, string reason)
    {
        await using var fixture = await Fixture.CreateAsync();
        var intent = (await fixture.Intents.ReserveAsync(fixture.Run(), fixture.Reservation("MSFT"))).Intent;
        if (changeProfile) await fixture.Store.SaveAsync("paper", 2, 3, 0, fixture.Clock.Now);
        else fixture.Clock.Now = fixture.Clock.Now.AddMinutes(2);
        var lease = await fixture.Intents.TryAcquireDispatchLeaseAsync(intent.IntentId, "paper-account", "fixture",
            TimeSpan.FromSeconds(30));
        Assert.NotNull(lease);
        var error = await Assert.ThrowsAsync<PortfolioRiskReservationRejectedException>(() =>
            fixture.Intents.RecordDispatchAttemptAsync(intent.IntentId, lease.LeaseToken));
        Assert.Equal(reason, error.ReasonCode);
    }

    // The next local day resolves a new counter window, including the correct daylight-saving offset.
    [Theory]
    [InlineData(2026, 3, 8, 23)]
    [InlineData(2026, 11, 1, 25)]
    public void NewYorkDay_UsesActualDstLength(int year, int month, int day, int hours)
    {
        var date = new DateOnly(year, month, day);
        Assert.Equal(hours, (TradingLimitDay.StartUtc(date.AddDays(1)) - TradingLimitDay.StartUtc(date)).TotalHours);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required string Root { get; init; }
        public required ContextFactory Factory { get; init; }
        public required Clock Clock { get; init; }
        public required SqliteOrderIntentRepository Intents { get; init; }
        public required SqliteOperationalTradingLimitStore Store { get; init; }

        // Create one disposable SQLite database and fixed clock, with no operational secrets or provider requests.
        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "tradingflow-operational-limits-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var factory = new ContextFactory(new DbContextOptionsBuilder<TradingFlowDbContext>()
                .UseSqlite($"Data Source={Path.Combine(root, "fixture.db")};Pooling=False").Options);
            await using var context = factory.CreateDbContext();
            await context.Database.EnsureCreatedAsync();
            var clock = new Clock();
            return new() { Root = root, Factory = factory, Clock = clock,
                Intents = new(factory, clock), Store = new(factory) };
        }

        // Every caller gets an independent run, while account-level reservations remain shared.
        public ProductionRun Run() => new() { RunId = Guid.NewGuid(), ConfigHash = new string('b', 64),
            CodeVersion = "fixture", Profile = "paper", Status = "running", StartedAtUtc = Clock.Now };

        // Bind all risk evidence to the same fixed account and clock used by the repository.
        public OrderIntentReservation Reservation(string symbol, BrokerFillHistorySnapshot? history = null) =>
            new(Guid.NewGuid(), OrderIntentKind.OperatorEntry, null, "paper-account", "fixture", symbol,
                "buy", "limit", "day", 10, 100, 98, TradingLimitDay.At(Clock.Now), Clock.Now, "{}", Clock.Now.AddMinutes(5),
                new("paper-account", "swing", 100000, 100000, 0, 0, new HashSet<string>(), 1000, 20, 100000,
                    2000, 20, Clock.Now, Clock.Now, history ?? History(), 60));

        // Fake complete pages are explicit test data and never enter the operator's broker/history archive.
        public BrokerFillHistorySnapshot History(IReadOnlyList<BrokerFillActivity>? fills = null) =>
            OperationalTradingLimitTestEvidence.Empty("paper-account", Clock.Now) with { Activities = fills ?? [] };

        // Label explicit provider opening/closing intent without inferring meaning merely from buy/sell side.
        public BrokerFillActivity Fill(string symbol, string? intent, decimal quantity, decimal cumulative) =>
            new(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), symbol,
                intent?.StartsWith("sell", StringComparison.Ordinal) == true ? "sell" : "buy",
                quantity, cumulative, Clock.Now.AddMinutes(-1), intent);

        // Remove all database and fixture files after checking the resolved root remains inside the temporary directory.
        public ValueTask DisposeAsync()
        {
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(Root), ignoreCase: true);
            Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ContextFactory(DbContextOptions<TradingFlowDbContext> options) : IDbContextFactory<TradingFlowDbContext>
    {
        // Use independent connections so concurrency tests exercise SQLite serialization across repository instances.
        public TradingFlowDbContext CreateDbContext() => new(options);
        // Match the production factory API without sharing a DbContext between concurrent calls.
        public Task<TradingFlowDbContext> CreateDbContextAsync(CancellationToken token = default) => Task.FromResult(CreateDbContext());
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-21T15:00:00Z");
        // Keep admission and fill-history clocks deterministic, including deliberate expiry tests.
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
