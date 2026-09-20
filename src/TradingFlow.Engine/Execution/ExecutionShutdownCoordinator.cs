using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Engine.Execution;

public sealed record ExecutionShutdownOptions
{
    public ExecutionShutdownOptions(
        bool flattenSwingPositions,
        bool allowExtendedHoursTrading,
        int timeoutSeconds = 30,
        int exitFillConfirmationTimeoutSeconds = 5,
        int exitFillPollIntervalMilliseconds = 250,
        int flatConfirmationObservations = 2,
        int protectionRestorationTimeoutSeconds = 5,
        int journalFlushTimeoutSeconds = 5,
        int journalFlushRetryIntervalMilliseconds = 100)
    {
        if (timeoutSeconds is < 5 or > 120) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        if (exitFillConfirmationTimeoutSeconds is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(exitFillConfirmationTimeoutSeconds));
        if (exitFillPollIntervalMilliseconds is < 25 or > 1_000) throw new ArgumentOutOfRangeException(nameof(exitFillPollIntervalMilliseconds));
        if (flatConfirmationObservations is < 2 or > 10) throw new ArgumentOutOfRangeException(nameof(flatConfirmationObservations));
        if (protectionRestorationTimeoutSeconds is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(protectionRestorationTimeoutSeconds));
        if (journalFlushTimeoutSeconds is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(journalFlushTimeoutSeconds));
        if (journalFlushRetryIntervalMilliseconds is < 25 or > 1_000) throw new ArgumentOutOfRangeException(nameof(journalFlushRetryIntervalMilliseconds));

        FlattenSwingPositions = flattenSwingPositions;
        AllowExtendedHoursTrading = allowExtendedHoursTrading;
        Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        ExitFillConfirmationTimeout = TimeSpan.FromSeconds(exitFillConfirmationTimeoutSeconds);
        ExitFillPollInterval = TimeSpan.FromMilliseconds(exitFillPollIntervalMilliseconds);
        FlatConfirmationObservations = flatConfirmationObservations;
        ProtectionRestorationTimeout = TimeSpan.FromSeconds(protectionRestorationTimeoutSeconds);
        JournalFlushTimeout = TimeSpan.FromSeconds(journalFlushTimeoutSeconds);
        JournalFlushRetryInterval = TimeSpan.FromMilliseconds(journalFlushRetryIntervalMilliseconds);
    }

    public bool FlattenSwingPositions { get; }
    public bool AllowExtendedHoursTrading { get; }
    public TimeSpan Timeout { get; }
    public TimeSpan ExitFillConfirmationTimeout { get; }
    public TimeSpan ExitFillPollInterval { get; }
    public int FlatConfirmationObservations { get; }
    public TimeSpan ProtectionRestorationTimeout { get; }
    public TimeSpan JournalFlushTimeout { get; }
    public TimeSpan JournalFlushRetryInterval { get; }
}

public sealed record ExecutionShutdownResult(
    int NonProtectiveOrdersCanceled,
    int PositionsFlattened,
    int PositionsRetained,
    int ProtectionFailures);

public interface IExecutionJournalFlushService
{
    Task FlushAsync(CancellationToken cancellationToken = default);
}

public interface IExecutionShutdownCoordinator
{
    Task<ExecutionShutdownResult> ShutdownAsync(
        IBrokerClient broker,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Closes the process-wide broker mutation fence, drains work already inside it,
/// and then owns the final cancellation, position, protection, and journal snapshot.
/// </summary>
public sealed class ExecutionShutdownCoordinator(
    IEntryAdmissionControl admission,
    IBrokerMutationCoordinator brokerMutations,
    IOrderIntentRepository intents,
    IOrderEventRepository orderEvents,
    IPositionLedgerRepository positions,
    IOrderCommandService commands,
    IOrderSynchronizationCoordinator synchronization,
    IProtectiveOrderInvariantService protection,
    IExecutionJournalFlushService journal,
    ExecutionShutdownOptions options,
    TimeProvider timeProvider,
    ILogger<ExecutionShutdownCoordinator> logger) : IExecutionShutdownCoordinator
{
    internal const string ShutdownBlockSource = "execution_shutdown";

    public async Task<ExecutionShutdownResult> ShutdownAsync(
        IBrokerClient broker,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(broker);
        admission.Block(
            ShutdownBlockSource,
            "EXECUTION_SHUTDOWN_IN_PROGRESS",
            "The process is draining broker work and no longer accepts new entries.",
            timeProvider.GetUtcNow());

        ExecutionShutdownResult? result = null;
        Exception? primaryFailure = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Timeout);
            result = await brokerMutations.StopAcceptingAndExecuteAsync(
                token => DrainBrokerAsync(broker, token),
                options.Timeout,
                timeout.Token);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        Exception? flushFailure = null;
        try
        {
            await FlushJournalAfterQuiescenceAsync();
        }
        catch (Exception exception)
        {
            flushFailure = exception;
        }

        if (primaryFailure is not null && flushFailure is not null)
        {
            logger.LogCritical(
                primaryFailure,
                "Execution shutdown failed before the journal checkpoint. FlushFailure={FlushFailure}",
                flushFailure.Message);
            throw new AggregateException(
                "Execution shutdown failed and the final journal checkpoint also failed.",
                primaryFailure,
                flushFailure);
        }

        if (primaryFailure is not null) ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        if (flushFailure is not null) ExceptionDispatchInfo.Capture(flushFailure).Throw();
        return result ?? throw new InvalidOperationException("Execution shutdown produced no result.");
    }

    private async Task<ExecutionShutdownResult> DrainBrokerAsync(
        IBrokerClient broker,
        CancellationToken cancellationToken)
    {
        var canceledNonProtectiveOrders = 0;
        var positionsFlattened = 0;
        var positionsRetained = 0;
        var protectionFailures = 0;
        var account = await broker.GetAccountSnapshotAsync(cancellationToken);
        var openOrders = await broker.GetOpenOrdersAsync(cancellationToken);
        foreach (var order in openOrders.OrderBy(item => item.CreatedAt))
        {
            // Nested bracket/OCO children are broker-side position protection. Their
            // parent identity is retained for ownership reconciliation, not cancellation.
            if (!String.IsNullOrWhiteSpace(order.ParentClientOrderId))
            {
                continue;
            }

            var ownerId = order.ParentClientOrderId ?? order.ClientOrderId;
            var intent = await intents.GetByClientOrderIdAsync(ownerId, cancellationToken);
            if (intent?.Kind is not (OrderIntentKind.StrategyEntry or OrderIntentKind.OperatorEntry or OrderIntentKind.PositionExit))
            {
                continue;
            }

            var canceled = await commands.RequestCancelAsync(
                new OrderCancellationSubmission(
                    ownerId,
                    order.OrderId,
                    "process_shutdown_order_drain",
                    timeProvider.GetUtcNow().ToUniversalTime(),
                    order.ClientOrderId),
                broker,
                cancellationToken);
            if (!OrderStateMachine.IsTerminal(canceled.State))
            {
                throw new OrderCancellationPendingException(ownerId, order.OrderId);
            }

            canceledNonProtectiveOrders++;
        }

        foreach (var position in (await positions.ListCurrentAsync(account.AccountId, cancellationToken))
                     .Where(item => item.Quantity != 0m)
                     .OrderBy(item => item.Symbol, StringComparer.Ordinal))
        {
            var entryIntent = await intents.GetByClientOrderIdAsync(position.PositionGenerationClientOrderId, cancellationToken);
            var horizon = ReadHorizon(entryIntent?.RequestJson);
            var shouldFlatten = horizon.Equals("swing", StringComparison.OrdinalIgnoreCase) &&
                options.FlattenSwingPositions;
            if (!shouldFlatten || await intents.HasActivePositionExitAsync(
                    account.AccountId,
                    position.Symbol,
                    position.PositionGenerationEventId,
                    cancellationToken))
            {
                positionsRetained++;
                continue;
            }

            if (entryIntent is null)
            {
                positionsRetained++;
                logger.LogCritical(
                    "Shutdown cannot flatten {Symbol}: position generation has no entry intent {ClientOrderId}.",
                    position.Symbol,
                    position.PositionGenerationClientOrderId);
                continue;
            }

            var run = await intents.GetRunAsync(entryIntent.RunId, cancellationToken)
                ?? throw new InvalidOperationException($"Position entry {entryIntent.IntentId:N} has no production run.");
            var handoffStarted = false;
            var confirmedFlat = false;
            Exception? handoffFailure = null;
            try
            {
                handoffStarted = true;
                var exit = await commands.SubmitPositionExitAsync(
                    new PositionExitSubmission(
                        new ExecutionRunContext(run.RunId, run.Profile, run.ConfigHash, run.CodeVersion, run.StartedAtUtc),
                        position.Symbol,
                        Math.Abs(position.Quantity),
                        "process_shutdown_flatten",
                        timeProvider.GetUtcNow().ToUniversalTime(),
                        options.AllowExtendedHoursTrading),
                    broker,
                    cancellationToken);
                confirmedFlat = await WaitForConfirmedFlatPositionAsync(
                    broker,
                    account.AccountId,
                    position.Symbol,
                    exit.ClientOrderId,
                    cancellationToken);
                if (confirmedFlat)
                {
                    positionsFlattened++;
                    continue;
                }

                var canceledExit = await commands.RequestCancelAsync(
                    new OrderCancellationSubmission(
                        exit.ClientOrderId,
                        exit.BrokerOrderId,
                        "process_shutdown_exit_not_filled",
                        timeProvider.GetUtcNow().ToUniversalTime()),
                    broker,
                    cancellationToken);
                if (!OrderStateMachine.IsTerminal(canceledExit.State))
                {
                    throw new OrderCancellationPendingException(exit.ClientOrderId, exit.BrokerOrderId);
                }

                positionsRetained++;
            }
            catch (Exception exception)
            {
                handoffFailure = exception;
            }
            finally
            {
                if (handoffStarted && !confirmedFlat)
                {
                    try
                    {
                        await RestoreProtectionAfterExitHandoffAsync(broker, account.AccountId, position);
                    }
                    catch (Exception restorationFailure)
                    {
                        protectionFailures++;
                        handoffFailure = handoffFailure is null
                            ? restorationFailure
                            : new AggregateException(
                                $"Exit handoff and protection restoration both failed for {position.Symbol}.",
                                handoffFailure,
                                restorationFailure);
                    }
                }
            }

            if (handoffFailure is not null) ExceptionDispatchInfo.Capture(handoffFailure).Throw();
        }

        openOrders = await synchronization.CrossCheckAsync(broker, cancellationToken);
        var brokerPositions = await broker.GetOpenPositionsAsync(cancellationToken);
        var repairs = await protection.EnsureAsync(
            broker,
            account.AccountId,
            brokerPositions,
            openOrders,
            cancellationToken);
        protectionFailures += repairs.Count(item => !item.Succeeded);
        if (protectionFailures > 0)
        {
            throw new InvalidOperationException(
                $"Shutdown left {protectionFailures} position protection failure(s). Recovery remains required.");
        }

        return new ExecutionShutdownResult(
            canceledNonProtectiveOrders,
            positionsFlattened,
            positionsRetained,
            protectionFailures);
    }

    private async Task<bool> WaitForConfirmedFlatPositionAsync(
        IBrokerClient broker,
        string accountId,
        string symbol,
        string exitClientOrderId,
        CancellationToken cancellationToken)
    {
        var stableObservations = 0;
        var startedAt = Stopwatch.GetTimestamp();
        do
        {
            await synchronization.CrossCheckAsync(broker, cancellationToken);
            var exit = await orderEvents.GetCurrentAsync(exitClientOrderId, cancellationToken);
            var local = await positions.GetCurrentAsync(accountId, symbol, cancellationToken);
            var brokerPositions = await broker.GetOpenPositionsAsync(cancellationToken);
            var brokerIsFlat = !brokerPositions.Any(position =>
                position.Qty != 0m && position.Ticker.Equals(symbol, StringComparison.OrdinalIgnoreCase));
            var confirmed = exit?.State == OrderState.Filled &&
                (local is null || local.Quantity == 0m) && brokerIsFlat;
            stableObservations = confirmed ? stableObservations + 1 : 0;
            if (stableObservations >= options.FlatConfirmationObservations) return true;
            if (Stopwatch.GetElapsedTime(startedAt) >= options.ExitFillConfirmationTimeout) return false;
            await Task.Delay(options.ExitFillPollInterval, cancellationToken);
        }
        while (true);
    }

    private async Task RestoreProtectionAfterExitHandoffAsync(
        IBrokerClient broker,
        string accountId,
        PositionLedgerSnapshot position)
    {
        using var timeout = new CancellationTokenSource(options.ProtectionRestorationTimeout);
        await RestoreProtectionAfterExitHandoffCoreAsync(
                broker,
                accountId,
                position,
                timeout.Token)
            .WaitAsync(options.ProtectionRestorationTimeout, CancellationToken.None);
    }

    private async Task RestoreProtectionAfterExitHandoffCoreAsync(
        IBrokerClient broker,
        string accountId,
        PositionLedgerSnapshot position,
        CancellationToken token)
    {
        var openOrders = await synchronization.CrossCheckAsync(broker, token);
        foreach (var order in openOrders.Where(order =>
                     order.Ticker.Equals(position.Symbol, StringComparison.OrdinalIgnoreCase)))
        {
            var ownerId = order.ParentClientOrderId ?? order.ClientOrderId;
            var intent = await intents.GetByClientOrderIdAsync(ownerId, token);
            if (intent?.Kind != OrderIntentKind.PositionExit ||
                intent.PositionGenerationEventId != position.PositionGenerationEventId)
            {
                continue;
            }

            var canceled = await commands.RequestCancelAsync(
                new OrderCancellationSubmission(
                    ownerId,
                    order.OrderId,
                    "process_shutdown_restore_protection",
                    timeProvider.GetUtcNow().ToUniversalTime(),
                    order.ClientOrderId),
                broker,
                token);
            if (!OrderStateMachine.IsTerminal(canceled.State))
            {
                throw new OrderCancellationPendingException(ownerId, order.OrderId);
            }
        }

        openOrders = await synchronization.CrossCheckAsync(broker, token);
        var brokerPositions = await broker.GetOpenPositionsAsync(token);
        var repairs = await protection.EnsureAsync(broker, accountId, brokerPositions, openOrders, token);
        var failures = repairs.Where(item => !item.Succeeded).ToArray();
        if (failures.Length > 0)
        {
            throw new InvalidOperationException(
                $"Protection restoration failed for {position.Symbol}: " +
                String.Join("; ", failures.Select(item => item.Detail)));
        }
    }

    private async Task FlushJournalAfterQuiescenceAsync()
    {
        using var timeout = new CancellationTokenSource(options.JournalFlushTimeout);
        var startedAt = Stopwatch.GetTimestamp();
        Exception? lastFailure = null;
        do
        {
            try
            {
                var remaining = options.JournalFlushTimeout - Stopwatch.GetElapsedTime(startedAt);
                if (remaining <= TimeSpan.Zero) break;
                await brokerMutations.WaitForQuiescenceAsync(remaining, timeout.Token);
                await journal.FlushAsync(timeout.Token).WaitAsync(remaining, timeout.Token);
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lastFailure = exception;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                break;
            }

            var timeLeft = options.JournalFlushTimeout - Stopwatch.GetElapsedTime(startedAt);
            if (timeLeft <= TimeSpan.Zero) break;
            var delay = options.JournalFlushRetryInterval < timeLeft
                ? options.JournalFlushRetryInterval
                : timeLeft;
            try
            {
                await Task.Delay(delay, timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                break;
            }
        }
        while (!timeout.IsCancellationRequested);

        throw new TimeoutException(
            "The execution journal could not be checkpointed after broker and hosted-service writers were quiesced.",
            lastFailure);
    }

    private static string ReadHorizon(string? requestJson)
    {
        if (String.IsNullOrWhiteSpace(requestJson)) return String.Empty;
        using var document = JsonDocument.Parse(requestJson);
        return document.RootElement.TryGetProperty("horizon", out var horizon) &&
               horizon.ValueKind == JsonValueKind.String
            ? horizon.GetString()?.Trim().ToLowerInvariant() ?? String.Empty
            : String.Empty;
    }
}
