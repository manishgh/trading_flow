using System.Collections.Concurrent;
using System.Text.Json;
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

public sealed record PartialFillExecutionOptions
{
    public PartialFillExecutionOptions(
        decimal swingMinimumFillRatioPct,
        bool allowExtendedHoursTrading = false,
        TimeOnly? swingEntryWindowEnd = null)
    {
        if (swingMinimumFillRatioPct is < 10m or > 100m)
        {
            throw new ArgumentOutOfRangeException(nameof(swingMinimumFillRatioPct));
        }

        SwingMinimumFillRatioPct = swingMinimumFillRatioPct;
        AllowExtendedHoursTrading = allowExtendedHoursTrading;
        SwingEntryWindowEnd = swingEntryWindowEnd ?? new TimeOnly(10, 30);
    }

    public decimal SwingMinimumFillRatioPct { get; }
    public bool AllowExtendedHoursTrading { get; }
    public TimeOnly SwingEntryWindowEnd { get; }
}

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

    Task<IReadOnlyList<ActiveBrokerOrder>> CrossCheckAsync(
        IAccountScopedBrokerOrderReader broker,
        CancellationToken cancellationToken);
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
    private readonly IOrderIntentRepository intents;
    private readonly IOrderLifecycleService lifecycle;
    private readonly IEntryAdmissionControl admission;
    private readonly AccountReconciliationOptions reconciliationOptions;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<OrderSynchronizationCoordinator> logger;
    private readonly IProtectiveStopReplacementRepository? stopReplacements;
    private readonly ConcurrentDictionary<string, byte> terminalAwaitingRest =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> divergenceCycles =
        new(StringComparer.Ordinal);
    private long streamConnected;
    private long lastStreamUpdateUnixMilliseconds = -1;
    private long lastRestPollUnixMilliseconds = -1;

    public OrderSynchronizationCoordinator(
        IOrderEventRepository events,
        IOrderIntentRepository intents,
        IOrderLifecycleService lifecycle,
        IEntryAdmissionControl admission,
        AccountReconciliationOptions reconciliationOptions,
        TimeProvider timeProvider,
        ILogger<OrderSynchronizationCoordinator> logger,
        IProtectiveStopReplacementRepository? stopReplacements = null)
    {
        this.events = events;
        this.intents = intents;
        this.lifecycle = lifecycle;
        this.admission = admission;
        this.reconciliationOptions = reconciliationOptions;
        this.timeProvider = timeProvider;
        this.logger = logger;
        this.stopReplacements = stopReplacements;
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

        var isBindingClientOrderId = ClientOrderIdFactory.IsBindingFormat(update.ClientOrderId);
        var isReplacementClientOrderId = update.ClientOrderId.StartsWith(
            "TFR-",
            StringComparison.Ordinal);
        if (!isBindingClientOrderId && !isReplacementClientOrderId)
        {
            logger.LogDebug(
                "Ignoring external broker order update {BrokerOrderId} with non-TradingFlow client ID {ClientOrderId}.",
                update.OrderId,
                update.ClientOrderId);
            return;
        }

        var ownedIntent = await intents.GetByClientOrderIdAsync(
            update.ClientOrderId,
            cancellationToken);
        if (ownedIntent is null && stopReplacements is not null)
        {
            var replacement = await stopReplacements.GetVerifiedByReplacementClientOrderIdAsync(
                update.ClientOrderId,
                cancellationToken);
            if (replacement is not null)
            {
                ownedIntent = await intents.GetByClientOrderIdAsync(
                    replacement.OwnerClientOrderId,
                    cancellationToken);
            }
        }

        if (ownedIntent is null)
        {
            if (!isBindingClientOrderId)
            {
                logger.LogDebug(
                    "Ignoring external broker order update {BrokerOrderId} with non-TradingFlow client ID {ClientOrderId}.",
                    update.OrderId,
                    update.ClientOrderId);
                return;
            }

            RegisterImmediateDivergence(
                update.ClientOrderId,
                "Broker update has a TradingFlow-shaped client ID but no local order intent.");
            return;
        }

        var ownedUpdate = update.ClientOrderId.Equals(
            ownedIntent.ClientOrderId,
            StringComparison.Ordinal)
                ? update
                : update with { ClientOrderId = ownedIntent.ClientOrderId };

        try
        {
            ValidateOwnedUpdate(ownedIntent, ownedUpdate);
        }
        catch (InvalidOperationException exception)
        {
            RegisterImmediateDivergence(ownedIntent.ClientOrderId, exception.Message);
            throw;
        }

        try
        {
            var positionFill = ownedUpdate.Status is OrderStatus.PartiallyFilled or OrderStatus.Filled
                ? new OrderFillProjection(
                    ownedUpdate.Ticker,
                    ownedUpdate.Side,
                    ownedUpdate.FilledQuantity,
                    ownedUpdate.FilledPrice,
                    ownedUpdate.ExecutionId ?? throw new InvalidOperationException(
                        "A stream fill requires execution_id."),
                    ownedUpdate.PositionQuantity ?? throw new InvalidOperationException(
                        "A stream fill requires authoritative position_qty."),
                    ownedUpdate.LastFillQuantity,
                    ownedUpdate.LastFillPrice ?? throw new InvalidOperationException(
                        "A stream fill requires the execution price."))
                : null;
            var snapshot = positionFill is null
                ? await lifecycle.ApplyBrokerUpdateAsync(ownedUpdate, cancellationToken)
                : await lifecycle.ApplyBrokerUpdateWithFillAsync(
                    ownedUpdate,
                    positionFill,
                    cancellationToken);
            Interlocked.Exchange(
                ref lastStreamUpdateUnixMilliseconds,
                timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            if (OrderStateMachine.IsTerminal(snapshot.State))
            {
                terminalAwaitingRest[ownedIntent.ClientOrderId] = 0;
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

    private static void ValidateOwnedUpdate(OrderIntentRecord intent, OrderUpdate update)
    {
        if (!intent.Symbol.Equals(update.Ticker, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Broker symbol {update.Ticker} does not match owned intent symbol {intent.Symbol}.");
        }

        if (!intent.Side.Equals(update.Side, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Broker side {update.Side} does not match owned intent side {intent.Side}.");
        }

        if (update.FilledQuantity < 0m || update.FilledQuantity > intent.RequestedQuantity)
        {
            throw new InvalidOperationException(
                $"Broker cumulative fill {update.FilledQuantity} is outside owned quantity " +
                $"0..{intent.RequestedQuantity}.");
        }

        if (update.FilledQuantity > 0m && update.FilledPrice <= 0m)
        {
            throw new InvalidOperationException(
                "Broker cumulative fill has no positive average fill price.");
        }

        if (update.Status is not (OrderStatus.PartiallyFilled or OrderStatus.Filled))
        {
            return;
        }

        if (update.LastFillQuantity <= 0m ||
            update.LastFillQuantity > update.FilledQuantity ||
            update.FilledPrice <= 0m ||
            update.PositionQuantity is null ||
            String.IsNullOrWhiteSpace(update.ExecutionId))
        {
            throw new InvalidOperationException(
                "Broker fill is missing valid cumulative quantity, last-fill quantity, price, " +
                "position quantity, or execution identity.");
        }
    }

    public async Task<IReadOnlyList<ActiveBrokerOrder>> CrossCheckAsync(
        IAccountScopedBrokerOrderReader broker,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(broker);
        var account = await broker.GetAccountSnapshotAsync(cancellationToken);
        var local = (await events.ListReconcilableAsync(account.AccountId, cancellationToken))
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
        var ownedBrokerOrders = new List<ActiveBrokerOrder>();
        foreach (var order in openOrders)
        {
            var ownerClientOrderId = order.ParentClientOrderId ?? order.ClientOrderId;
            if (!local.ContainsKey(ownerClientOrderId) && stopReplacements is not null)
            {
                var replacement = await stopReplacements.GetVerifiedByReplacementClientOrderIdAsync(
                    order.ClientOrderId,
                    cancellationToken);
                ownerClientOrderId = replacement?.OwnerClientOrderId ?? ownerClientOrderId;
            }

            if (local.ContainsKey(ownerClientOrderId))
            {
                ownedBrokerOrders.Add(order with { ClientOrderId = ownerClientOrderId });
            }
        }

        var brokerOrders = ownedBrokerOrders
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

        return openOrders;
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
            var age = timeProvider.GetUtcNow() - local.LocalTimestampUtc.ToUniversalTime();
            if (age < reconciliationOptions.OrphanTimeout)
            {
                logger.LogDebug(
                    "Tracked order {ClientOrderId} has no broker REST record at age {AgeSeconds:F3}s; deferring until orphan timeout {TimeoutSeconds:F0}s.",
                    local.ClientOrderId,
                    age.TotalSeconds,
                    reconciliationOptions.OrphanTimeout.TotalSeconds);
                return new CrossCheckResult(local.ClientOrderId, local.State, Matches: true);
            }

            RegisterCycleDivergence(local.ClientOrderId, "Broker REST returned no order for a tracked client order ID.");
            return new CrossCheckResult(local.ClientOrderId, local.State, Matches: false);
        }

        try
        {
            await ApplyRestUpdateWithPositionRepairAsync(local, brokerOrder, cancellationToken);
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

    private async Task ApplyRestUpdateWithPositionRepairAsync(
        OrderStateSnapshot local,
        ActiveBrokerOrder brokerOrder,
        CancellationToken cancellationToken)
    {
        var update = BrokerOrderUpdateFactory.Create(brokerOrder);
        var positionFill = brokerOrder.FilledQuantity > 0m
            ? new OrderFillProjection(
                brokerOrder.Ticker,
                brokerOrder.Side,
                brokerOrder.FilledQuantity,
                brokerOrder.FilledAveragePrice ?? throw new InvalidOperationException(
                    $"Broker fill {brokerOrder.OrderId} is missing filled_avg_price."),
                $"rest:{brokerOrder.OrderId}:{brokerOrder.FilledQuantity.ToString(System.Globalization.CultureInfo.InvariantCulture)}")
            : null;
        if (positionFill is null)
        {
            await lifecycle.ApplyBrokerUpdateAsync(update, cancellationToken);
        }
        else
        {
            await lifecycle.ApplyBrokerUpdateWithFillAsync(
                update,
                positionFill,
                cancellationToken);
        }
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
    IBrokerClient broker,
    IOrderSynchronizationCoordinator coordinator,
    IAccountReconciliationService reconciliation,
    OrderSynchronizationOptions options,
    TimeProvider timeProvider,
    ILogger<OrderSynchronizationRunner> logger,
    IOrderIntentRepository? intents = null,
    IOrderCommandService? orderCommands = null,
    IEntryAdmissionControl? admission = null,
    PartialFillExecutionOptions? partialFillOptions = null) : IOrderSynchronizationRunner
{
    private const string RestProtectionPendingBlockSource = "rest_fill_protection_pending";

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
        var initialOrders = await CrossCheckAndProtectAsync(
            RestProtectionPendingBlockSource,
            "Initial broker state is being reconciled and protected.",
            cancellationToken);
        if (await HandleRestPartialEntriesAsync(initialOrders, cancellationToken))
        {
            await CrossCheckAndProtectAsync(
                RestProtectionPendingBlockSource,
                "A startup partial-fill decision is being reconciled and protected.",
                cancellationToken);
        }

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
            if (update.Status is OrderStatus.PartiallyFilled or OrderStatus.Filled)
            {
                // Protect the committed fill before doing anything to the working remainder.
                // Reconciliation merges the local fill ledger with the broker snapshot, which
                // covers the normal interval where Alpaca's position REST view lags the stream.
                var fillProtectionSource = $"fill_protection_pending:{update.ClientOrderId}";
                var openOrders = await CrossCheckAndProtectAsync(
                    fillProtectionSource,
                    $"Committed fill {update.ClientOrderId} is awaiting broker-resting protection verification.",
                    cancellationToken);

                if (update.Status == OrderStatus.PartiallyFilled)
                {
                    if (await ApplyPartialEntryPolicyAsync(update, cancellationToken))
                    {
                        await CrossCheckAndProtectAsync(
                            fillProtectionSource,
                            $"Partial-fill decision for {update.ClientOrderId} is awaiting final protection verification.",
                            cancellationToken);
                    }
                }
            }
        }
    }

    private async Task<bool> ApplyPartialEntryPolicyAsync(
        OrderUpdate update,
        CancellationToken cancellationToken)
    {
        if (intents is null || orderCommands is null)
        {
            return false;
        }

        var intent = await intents.GetByClientOrderIdAsync(update.ClientOrderId, cancellationToken);
        if (intent?.Kind is not (OrderIntentKind.StrategyEntry or OrderIntentKind.OperatorEntry))
        {
            return false;
        }

        var horizon = ReadHorizon(intent.RequestJson);
        if (horizon != "swing" || partialFillOptions is null ||
            !SwingEntryWindowHasClosed(timeProvider.GetUtcNow(), partialFillOptions.SwingEntryWindowEnd))
        {
            return false;
        }

        var cancellationBlockSource = $"partial_entry_remainder_cancellation:{update.ClientOrderId}";
        admission?.Block(
            cancellationBlockSource,
            "PARTIAL_ENTRY_REMAINDER_CANCELLING",
            $"Canceling the unfilled remainder of {update.ClientOrderId} after a partial fill.",
            timeProvider.GetUtcNow());
        try
        {
            var result = await orderCommands.RequestCancelAsync(
                new OrderCancellationSubmission(
                    update.ClientOrderId,
                    update.OrderId,
                    "partial_entry_fill_remainder",
                    timeProvider.GetUtcNow().ToUniversalTime()),
                broker,
                cancellationToken);
            if (!OrderStateMachine.IsTerminal(result.State))
            {
                throw new OrderCancellationPendingException(update.ClientOrderId, update.OrderId);
            }

            var finalFilledQuantity = result.FilledQuantity ?? update.FilledQuantity;
            // Cancellation can race a final broker fill. The cancellation command has already
            // projected its terminal cumulative quantity; reconcile that projection into a
            // broker-resting stop before an abort order takes ownership of the exposure.
            await CrossCheckAndProtectAsync(
                $"partial_entry_final_fill_protection:{update.ClientOrderId}",
                $"Terminal cancellation fill {finalFilledQuantity} for {update.ClientOrderId} " +
                "is awaiting broker-resting protection verification.",
                cancellationToken);

            var ratioPct = intent.RequestedQuantity > 0m
                ? finalFilledQuantity / intent.RequestedQuantity * 100m
                : 0m;
            if (ratioPct < partialFillOptions.SwingMinimumFillRatioPct)
            {
                var quantity = Math.Abs(finalFilledQuantity);
                var run = await intents.GetRunAsync(intent.RunId, cancellationToken)
                    ?? throw new InvalidOperationException(
                        $"Partial-fill intent {intent.IntentId:N} has no production run.");
                await orderCommands.SubmitPositionExitAsync(
                    new PositionExitSubmission(
                        new ExecutionRunContext(
                            run.RunId,
                            run.Profile,
                            run.ConfigHash,
                            run.CodeVersion,
                            run.StartedAtUtc),
                        intent.Symbol,
                        quantity,
                        "MIN_FILL_ABORT",
                        timeProvider.GetUtcNow().ToUniversalTime(),
                        partialFillOptions.AllowExtendedHoursTrading),
                    broker,
                    cancellationToken);
            }

            admission?.Clear(cancellationBlockSource);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            admission?.Block(
                cancellationBlockSource,
                "PARTIAL_ENTRY_REMAINDER_UNRESOLVED",
                $"{update.ClientOrderId}: {exception.Message}",
                timeProvider.GetUtcNow());
            logger.LogCritical(
                exception,
                "Partial entry remainder could not be canceled. ClientOrderId={ClientOrderId} BrokerOrderId={BrokerOrderId}.",
                update.ClientOrderId,
                update.OrderId);
            throw;
        }
    }

    private async Task<bool> HandleRestPartialEntriesAsync(
        IReadOnlyList<ActiveBrokerOrder> openOrders,
        CancellationToken cancellationToken)
    {
        var handled = false;
        foreach (var order in openOrders.Where(order =>
                     order.FilledQuantity > 0m &&
                     order.Qty is > 0m &&
                     order.FilledQuantity < order.Qty.Value &&
                     order.Status.Equals("partially_filled", StringComparison.OrdinalIgnoreCase)))
        {
            handled |= await ApplyPartialEntryPolicyAsync(
                BrokerOrderUpdateFactory.Create(order), cancellationToken);
        }

        return handled;
    }

    private async Task<IReadOnlyList<ActiveBrokerOrder>> CrossCheckAndProtectAsync(
        string blockSource,
        string detail,
        CancellationToken cancellationToken)
    {
        admission?.Block(
            blockSource,
            "FILL_PROTECTION_PENDING",
            detail,
            timeProvider.GetUtcNow());
        try
        {
            var openOrders = await coordinator.CrossCheckAsync(broker, cancellationToken);
            await reconciliation.ReconcileAsync(broker, openOrders, cancellationToken);
            if (admission is null || !admission.GetSnapshot().Blocks.Any(block =>
                    block.Source == AccountReconciliationService.ProtectionBlockSource))
            {
                admission?.Clear(blockSource);
                if (admission is not null)
                {
                    foreach (var pendingFill in admission.GetSnapshot().Blocks.Where(block =>
                                 block.Source.StartsWith("fill_protection_pending:", StringComparison.Ordinal)))
                    {
                        admission.Clear(pendingFill.Source);
                    }
                }
            }

            return openOrders;
        }
        catch
        {
            // Keep the block. A later successful reconciliation owns clearing it.
            throw;
        }
    }

    private static string ReadHorizon(string requestJson)
    {
        if (String.IsNullOrWhiteSpace(requestJson))
        {
            return String.Empty;
        }

        using var document = JsonDocument.Parse(requestJson);
        return document.RootElement.TryGetProperty("horizon", out var horizon) &&
               horizon.ValueKind == JsonValueKind.String
            ? horizon.GetString()?.Trim().ToLowerInvariant() ?? String.Empty
            : String.Empty;
    }

    private static bool SwingEntryWindowHasClosed(DateTimeOffset utcNow, TimeOnly windowEnd)
    {
        var eastern = TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");
        var local = TimeZoneInfo.ConvertTime(utcNow, eastern);
        return TimeOnly.FromDateTime(local.DateTime) >= windowEnd;
    }

    private async Task PollRestAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(options.PollInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var openOrders = await CrossCheckAndProtectAsync(
                RestProtectionPendingBlockSource,
                "REST order state is being reconciled and protected.",
                cancellationToken);
            if (await HandleRestPartialEntriesAsync(openOrders, cancellationToken))
            {
                await CrossCheckAndProtectAsync(
                    RestProtectionPendingBlockSource,
                    "REST partial-fill cancellation is being reconciled and protected.",
                    cancellationToken);
            }
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
