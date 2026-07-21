using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Engine.Execution;

public sealed record OrderSynchronizationOptions
{
    public OrderSynchronizationOptions(int pollIntervalSeconds)
    {
        if (pollIntervalSeconds is < 5 or > 60)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pollIntervalSeconds),
                "Order polling must be between 5 and 60 seconds.");
        }

        PollInterval = TimeSpan.FromSeconds(pollIntervalSeconds);
    }

    public TimeSpan PollInterval { get; }
}

public sealed record OrderSynchronizationHealth(
    bool StreamConnected,
    DateTimeOffset? LastStreamUpdateUtc,
    DateTimeOffset? LastRestPollUtc,
    IReadOnlyDictionary<string, int> DivergenceCycles);

public interface ITradeUpdateStreamerFactory
{
    ITradeUpdateStreamer Create();
}

public interface IOrderSynchronizationCoordinator
{
    OrderSynchronizationHealth GetHealth();

    void MarkStreamConnected();

    void MarkStreamDisconnected(string detail);

    Task ProcessStreamUpdateAsync(OrderUpdate update, CancellationToken cancellationToken);

    Task CrossCheckAsync(IBrokerOrderReader broker, CancellationToken cancellationToken);
}

/// <summary>
/// Reconciles the primary account trade stream with REST snapshots. One unresolved REST mismatch
/// is tolerated for propagation; a second consecutive cycle blocks every new entry.
/// </summary>
public sealed class OrderSynchronizationCoordinator : IOrderSynchronizationCoordinator
{
    internal const string StreamBlockSource = "alpaca_order_stream";
    internal const string RestReadyBlockSource = "alpaca_order_rest_initialization";
    internal const string DivergenceBlockSource = "alpaca_order_divergence";

    private readonly IOrderEventRepository events;
    private readonly IOrderLifecycleService lifecycle;
    private readonly IEntryAdmissionControl admission;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<OrderSynchronizationCoordinator> logger;
    private readonly ConcurrentDictionary<string, byte> terminalAwaitingRest =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> divergenceCycles =
        new(StringComparer.Ordinal);
    private long streamConnected;
    private long lastStreamUpdateUnixMilliseconds = -1;
    private long lastRestPollUnixMilliseconds = -1;

    public OrderSynchronizationCoordinator(
        IOrderEventRepository events,
        IOrderLifecycleService lifecycle,
        IEntryAdmissionControl admission,
        TimeProvider timeProvider,
        ILogger<OrderSynchronizationCoordinator> logger)
    {
        this.events = events;
        this.lifecycle = lifecycle;
        this.admission = admission;
        this.timeProvider = timeProvider;
        this.logger = logger;
        var now = timeProvider.GetUtcNow();
        admission.Block(StreamBlockSource, "ORDER_STREAM_NOT_CONNECTED", "The Alpaca account order stream is not connected.", now);
        admission.Block(RestReadyBlockSource, "ORDER_REST_NOT_READY", "The initial broker order cross-check has not completed.", now);
    }

    public OrderSynchronizationHealth GetHealth() => new(
        Interlocked.Read(ref streamConnected) == 1,
        FromUnixMilliseconds(Interlocked.Read(ref lastStreamUpdateUnixMilliseconds)),
        FromUnixMilliseconds(Interlocked.Read(ref lastRestPollUnixMilliseconds)),
        new SortedDictionary<string, int>(divergenceCycles, StringComparer.Ordinal));

    public void MarkStreamConnected()
    {
        Interlocked.Exchange(ref streamConnected, 1);
        admission.Clear(StreamBlockSource);
        logger.LogInformation("Alpaca account trade-update stream connected and authenticated.");
    }

    public void MarkStreamDisconnected(string detail)
    {
        Interlocked.Exchange(ref streamConnected, 0);
        admission.Block(
            StreamBlockSource,
            "ORDER_STREAM_DISCONNECTED",
            String.IsNullOrWhiteSpace(detail) ? "The Alpaca account order stream disconnected." : detail.Trim(),
            timeProvider.GetUtcNow());
        logger.LogCritical("Alpaca account trade-update stream is unavailable. Detail={Detail}", detail);
    }

    public async Task ProcessStreamUpdateAsync(
        OrderUpdate update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.Source != BrokerUpdateSource.TradeStream)
        {
            throw new InvalidOperationException("The primary stream path accepts only trade-stream updates.");
        }

        if (!ClientOrderIdFactory.IsBindingFormat(update.ClientOrderId))
        {
            logger.LogDebug(
                "Ignoring external broker order update {BrokerOrderId} with non-TradingFlow client ID {ClientOrderId}.",
                update.OrderId,
                update.ClientOrderId);
            return;
        }

        try
        {
            var snapshot = await lifecycle.ApplyBrokerUpdateAsync(update, cancellationToken);
            Interlocked.Exchange(
                ref lastStreamUpdateUnixMilliseconds,
                timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            if (OrderStateMachine.IsTerminal(snapshot.State))
            {
                terminalAwaitingRest[update.ClientOrderId] = 0;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RegisterImmediateDivergence(
                update.ClientOrderId,
                $"Stream update could not be applied: {exception.Message}");
            throw;
        }
    }

    public async Task CrossCheckAsync(
        IBrokerOrderReader broker,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(broker);
        var local = (await events.ListReconcilableAsync(cancellationToken))
            .ToDictionary(snapshot => snapshot.ClientOrderId, StringComparer.Ordinal);
        foreach (var clientOrderId in terminalAwaitingRest.Keys)
        {
            var snapshot = await events.GetCurrentAsync(clientOrderId, cancellationToken);
            if (snapshot is not null)
            {
                local[clientOrderId] = snapshot;
            }
        }

        var openOrders = await broker.GetOpenOrdersAsync(cancellationToken);
        var brokerOrders = openOrders
            .Where(order => ClientOrderIdFactory.IsBindingFormat(order.ClientOrderId))
            .GroupBy(order => order.ClientOrderId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Single(),
                StringComparer.Ordinal);

        var unexpectedBrokerOrders = brokerOrders.Keys
            .Where(clientOrderId => !local.ContainsKey(clientOrderId))
            .ToArray();
        foreach (var clientOrderId in unexpectedBrokerOrders)
        {
            RegisterCycleDivergence(clientOrderId, "Broker has a TradingFlow order that is absent from the local lifecycle query.");
        }

        var checks = local.Values.Select(snapshot =>
            CrossCheckOrderAsync(snapshot, brokerOrders, broker, cancellationToken));
        var results = await Task.WhenAll(checks);
        var currentDivergences = unexpectedBrokerOrders
            .Concat(results.Where(result => !result.Matches).Select(result => result.ClientOrderId))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var result in results.Where(result => result.Matches))
        {
            divergenceCycles.TryRemove(result.ClientOrderId, out _);
            if (OrderStateMachine.IsTerminal(result.LocalState))
            {
                terminalAwaitingRest.TryRemove(result.ClientOrderId, out _);
            }
        }

        foreach (var stale in divergenceCycles.Keys.Where(key => !currentDivergences.Contains(key)).ToArray())
        {
            divergenceCycles.TryRemove(stale, out _);
        }

        Interlocked.Exchange(
            ref lastRestPollUnixMilliseconds,
            timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        admission.Clear(RestReadyBlockSource);
        if (divergenceCycles.Values.Any(cycles => cycles > 1))
        {
            var detail = String.Join(
                ", ",
                divergenceCycles
                    .Where(item => item.Value > 1)
                    .OrderBy(item => item.Key, StringComparer.Ordinal)
                    .Select(item => $"{item.Key}:{item.Value}"));
            admission.Block(
                DivergenceBlockSource,
                "ORDER_STREAM_REST_DIVERGENCE",
                $"Order stream and REST disagree for more than one polling cycle: {detail}.",
                timeProvider.GetUtcNow());
            logger.LogCritical(
                "Order stream and REST divergence exceeded one polling cycle. Divergences={Divergences}",
                detail);
        }
        else
        {
            admission.Clear(DivergenceBlockSource);
        }
    }

    private async Task<CrossCheckResult> CrossCheckOrderAsync(
        OrderStateSnapshot local,
        IReadOnlyDictionary<string, ActiveBrokerOrder> openOrders,
        IBrokerOrderReader broker,
        CancellationToken cancellationToken)
    {
        ActiveBrokerOrder? brokerOrder = openOrders.GetValueOrDefault(local.ClientOrderId);
        brokerOrder ??= await broker.GetOrderByClientOrderIdAsync(local.ClientOrderId, cancellationToken);
        if (brokerOrder is null)
        {
            RegisterCycleDivergence(local.ClientOrderId, "Broker REST returned no order for a tracked client order ID.");
            return new CrossCheckResult(local.ClientOrderId, local.State, Matches: false);
        }

        try
        {
            await lifecycle.ApplyBrokerUpdateAsync(
                BrokerOrderUpdateFactory.Create(brokerOrder),
                cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            logger.LogWarning(
                exception,
                "REST order snapshot could not be projected for {ClientOrderId}; comparing the latest local state.",
                local.ClientOrderId);
        }

        var refreshed = await events.GetCurrentAsync(local.ClientOrderId, cancellationToken)
            ?? throw new InvalidOperationException($"Order '{local.ClientOrderId}' disappeared during REST cross-check.");
        var matches = BrokerOrderStateProjection.Matches(refreshed, brokerOrder);
        if (!matches)
        {
            RegisterCycleDivergence(
                local.ClientOrderId,
                $"Local={refreshed.State.ToStorageValue()}, Broker={brokerOrder.Status}, LocalFilled={refreshed.FilledQuantity ?? 0m}, BrokerFilled={brokerOrder.FilledQuantity}.");
        }

        return new CrossCheckResult(local.ClientOrderId, refreshed.State, matches);
    }

    private void RegisterImmediateDivergence(string clientOrderId, string detail)
    {
        divergenceCycles.AddOrUpdate(clientOrderId, 2, (_, current) => Math.Max(2, current + 1));
        admission.Block(
            DivergenceBlockSource,
            "ORDER_STREAM_APPLY_FAILED",
            $"{clientOrderId}: {detail}",
            timeProvider.GetUtcNow());
        logger.LogCritical(
            "Authoritative stream update failed for {ClientOrderId}. Detail={Detail}",
            clientOrderId,
            detail);
    }

    private void RegisterCycleDivergence(string clientOrderId, string detail)
    {
        var cycles = divergenceCycles.AddOrUpdate(clientOrderId, 1, (_, current) => current + 1);
        logger.LogWarning(
            "Order stream/REST mismatch for {ClientOrderId} on consecutive cycle {Cycle}. Detail={Detail}",
            clientOrderId,
            cycles,
            detail);
    }

    private static DateTimeOffset? FromUnixMilliseconds(long value) =>
        value < 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(value);

    private sealed record CrossCheckResult(string ClientOrderId, OrderState LocalState, bool Matches);
}

public interface IOrderSynchronizationRunner
{
    Task RunAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Owns the single account trade stream and the timed REST cross-check loop.
/// </summary>
public sealed class OrderSynchronizationRunner(
    ITradeUpdateStreamerFactory streamerFactory,
    IBrokerOrderReader broker,
    IOrderSynchronizationCoordinator coordinator,
    OrderSynchronizationOptions options,
    TimeProvider timeProvider,
    ILogger<OrderSynchronizationRunner> logger) : IOrderSynchronizationRunner
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var streamer = streamerFactory.Create();
            try
            {
                await RunConnectedSessionAsync(streamer, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                coordinator.MarkStreamDisconnected(exception.Message);
                logger.LogError(exception, "Alpaca order synchronization session failed; retrying in one second.");
                await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, cancellationToken);
            }
        }
    }

    private async Task RunConnectedSessionAsync(
        ITradeUpdateStreamer streamer,
        CancellationToken cancellationToken)
    {
        await streamer.ConnectAsync(cancellationToken);
        coordinator.MarkStreamConnected();
        await coordinator.CrossCheckAsync(broker, cancellationToken);

        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var streamTask = ConsumeStreamAsync(streamer, sessionCancellation.Token);
        var pollingTask = PollRestAsync(sessionCancellation.Token);
        var completed = await Task.WhenAny(streamTask, pollingTask);
        sessionCancellation.Cancel();
        Exception? sessionFailure = null;
        try
        {
            await completed;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            sessionFailure = exception;
        }

        await Task.WhenAll(
            ObserveSessionEndAsync(streamTask, sessionCancellation.Token),
            ObserveSessionEndAsync(pollingTask, sessionCancellation.Token));

        if (sessionFailure is not null)
        {
            throw new InvalidOperationException("The Alpaca order synchronization session failed.", sessionFailure);
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("The Alpaca trade-update stream ended unexpectedly.");
        }
    }

    private async Task ConsumeStreamAsync(
        ITradeUpdateStreamer streamer,
        CancellationToken cancellationToken)
    {
        await foreach (var update in streamer.ReadUpdatesAsync(cancellationToken))
        {
            await coordinator.ProcessStreamUpdateAsync(update, cancellationToken);
        }
    }

    private async Task PollRestAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(options.PollInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await coordinator.CrossCheckAsync(broker, cancellationToken);
        }
    }

    private static async Task ObserveSessionEndAsync(Task task, CancellationToken sessionCancellation)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested)
        {
        }
        catch
        {
            // The completed task's failure is preserved above. A sibling may also fail while the
            // session is being torn down, but it must not replace the initiating failure.
        }
    }
}

public static class BrokerOrderStateProjection
{
    public static OrderState Project(OrderStatus status) => status switch
    {
        OrderStatus.New or
        OrderStatus.Accepted or
        OrderStatus.PendingNew or
        OrderStatus.AcceptedForBidding => OrderState.Acked,
        OrderStatus.PartiallyFilled => OrderState.PartiallyFilled,
        OrderStatus.Filled => OrderState.Filled,
        OrderStatus.PendingCancel => OrderState.CancelPending,
        OrderStatus.Canceled => OrderState.Canceled,
        OrderStatus.Expired or OrderStatus.DoneForDay => OrderState.Expired,
        OrderStatus.Rejected => OrderState.Rejected,
        _ => throw new InvalidOperationException(
            $"Broker order status {status} requires explicit cancel/replace reconciliation.")
    };

    public static bool Matches(OrderStateSnapshot local, ActiveBrokerOrder broker)
    {
        var brokerState = Project(OrderStatusCodec.ParseBrokerValue(broker.Status));
        if (local.State != brokerState)
        {
            return false;
        }

        return brokerState is not (OrderState.PartiallyFilled or OrderState.Filled) ||
               local.FilledQuantity == broker.FilledQuantity;
    }
}
