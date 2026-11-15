using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingFlow.Domain.Execution;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Engine.Execution;

public sealed record AccountReconciliationOptions
{
    public AccountReconciliationOptions(int reconcileIntervalSeconds, int orphanTimeoutSeconds)
    {
        if (reconcileIntervalSeconds is < 15 or > 300)
        {
            throw new ArgumentOutOfRangeException(nameof(reconcileIntervalSeconds));
        }

        if (orphanTimeoutSeconds is < 10 or > 120)
        {
            throw new ArgumentOutOfRangeException(nameof(orphanTimeoutSeconds));
        }

        ReconcileInterval = TimeSpan.FromSeconds(reconcileIntervalSeconds);
        OrphanTimeout = TimeSpan.FromSeconds(orphanTimeoutSeconds);
    }

    public TimeSpan ReconcileInterval { get; }
    public TimeSpan OrphanTimeout { get; }
}

public sealed record ReconciliationRunContext(ProductionRun Run);

public sealed record ReconciliationDifference(
    string Kind,
    string Key,
    string Detail);

public sealed record AccountReconciliationHealth(
    bool InitialReconciliationCompleted,
    DateTimeOffset? LastCompletedAtUtc,
    string Status,
    Guid? OutstandingReconciliationId,
    int DifferenceCount);

public sealed record AccountReconciliationResult(
    Guid ReconciliationId,
    string Status,
    IReadOnlyList<ReconciliationDifference> Differences,
    bool RequiresAcknowledgement,
    int ProtectionRepairFailureCount = 0)
{
    public bool BrokerProtectionReady => ProtectionRepairFailureCount == 0;
}

public interface IAccountReconciliationService
{
    AccountReconciliationHealth GetHealth();

    Task<AccountReconciliationResult> ReconcileAsync(
        IBrokerClient broker,
        IReadOnlyList<ActiveBrokerOrder> openOrders,
        CancellationToken cancellationToken = default);

    // Return false unless a fresh terminal order and its resulting position protection can be verified.
    Task<bool> IsOpeningOrderResolvedAsync(
        IBrokerClient broker, string clientOrderId, string brokerOrderId, string symbol, decimal minimumFilledQuantity,
        CancellationToken cancellationToken = default);

    Task AcknowledgeAsync(
        Guid reconciliationId,
        string actor,
        string reason,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reconciles the broker account against the durable order and position journals.
/// A mismatch is never adopted as truth. Position state advances only from authoritative
/// stream fills or idempotent REST repair for an order already owned by TradingFlow.
/// </summary>
public sealed class AccountReconciliationService : IAccountReconciliationService
{
    internal const string StartupBlockSource = "account_reconciliation_startup";
    internal const string MismatchBlockSource = "account_reconciliation";
    internal const string ProtectionBlockSource = "broker_side_protection";
    internal const string PositionExitHandoffBlockSource = "position_exit_handoff";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IOrderEventRepository orderEvents;
    private readonly IPositionLedgerRepository positions;
    private readonly IReconciliationRepository reconciliations;
    private readonly IEntryAdmissionControl admission;
    private readonly IProtectiveOrderInvariantService protectiveOrders;
    private readonly ReconciliationRunContext runContext;
    private readonly AccountReconciliationOptions options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AccountReconciliationService> logger;
    private readonly IProtectiveStopReplacementRepository? stopReplacements;
    private readonly IOrderIntentRepository? orderIntents;
    private const string AccountLossSupervisionBlockSource = "account_loss_supervision";
    private readonly AccountLossGuard? lossGuard;
    private readonly IOrderCommandService? orderCommands;
    private readonly SemaphoreSlim reconcileLock = new(1, 1);
    private volatile bool initialCompleted;
    private long lastCompletedUnixMilliseconds = -1;
    private string status = "not_ready";
    private Guid? outstandingId;
    private int differenceCount;

    // Wire periodic loss observation into the broker supervision that already owns protection.
    public AccountReconciliationService(
        IOrderEventRepository orderEvents,
        IPositionLedgerRepository positions,
        IReconciliationRepository reconciliations,
        IEntryAdmissionControl admission,
        IProtectiveOrderInvariantService protectiveOrders,
        ReconciliationRunContext runContext,
        AccountReconciliationOptions options,
        TimeProvider timeProvider,
        ILogger<AccountReconciliationService> logger,
        IProtectiveStopReplacementRepository? stopReplacements = null,
        IOrderIntentRepository? orderIntents = null,
        AccountLossGuard? lossGuard = null,
        IOrderCommandService? orderCommands = null)
    {
        this.orderEvents = orderEvents;
        this.positions = positions;
        this.reconciliations = reconciliations;
        this.admission = admission;
        this.protectiveOrders = protectiveOrders;
        this.runContext = runContext;
        this.options = options;
        this.timeProvider = timeProvider;
        this.logger = logger;
        this.stopReplacements = stopReplacements;
        this.orderIntents = orderIntents;
        this.lossGuard = lossGuard;
        this.orderCommands = orderCommands;
        admission.Block(
            StartupBlockSource,
            "RECONCILIATION_NOT_READY",
            "The startup broker/account reconciliation has not completed.",
            timeProvider.GetUtcNow());
    }

    public AccountReconciliationHealth GetHealth() => new(
        initialCompleted,
        FromUnixMilliseconds(Interlocked.Read(ref lastCompletedUnixMilliseconds)),
        status,
        outstandingId,
        Volatile.Read(ref differenceCount));

    // Reconcile positions, protect broker exposure, and supervise the account entry stop.
    public async Task<AccountReconciliationResult> ReconcileAsync(
        IBrokerClient broker,
        IReadOnlyList<ActiveBrokerOrder> openOrders,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(openOrders);
        await reconcileLock.WaitAsync(cancellationToken);
        try
        {
            var startedAt = timeProvider.GetUtcNow();
            admission.Block(
                ProtectionBlockSource,
                "BROKER_SIDE_PROTECTION_NOT_READY",
                "Broker-side position protection is being verified.",
                startedAt);
            var account = await broker.GetAccountSnapshotAsync(cancellationToken);
            var brokerPositions = await broker.GetOpenPositionsAsync(cancellationToken);
            var localPositions = await positions.ListCurrentAsync(
                account.AccountId,
                cancellationToken);
            var localOrders = (await orderEvents.ListReconcilableAsync(
                account.AccountId,
                cancellationToken)).ToList();
            var ownedOpenOrders = await ResolveReplacementOwnersAsync(
                openOrders,
                cancellationToken);
            foreach (var parentClientOrderId in ownedOpenOrders
                         .Select(order => order.ParentClientOrderId)
                         .Where(ClientOrderIdFactory.IsBindingFormat)
                         .Distinct(StringComparer.Ordinal))
            {
                if (localOrders.Any(order => order.ClientOrderId == parentClientOrderId))
                {
                    continue;
                }

                var parent = await orderEvents.GetCurrentAsync(parentClientOrderId!, cancellationToken);
                if (parent is not null)
                {
                    localOrders.Add(parent);
                }
            }
            var differences = BuildDifferences(
                brokerPositions,
                ownedOpenOrders,
                localPositions,
                localOrders,
                startedAt,
                options.OrphanTimeout);
            var brokerSnapshotJson = JsonSerializer.Serialize(new
            {
                positions = brokerPositions.OrderBy(item => item.Ticker, StringComparer.Ordinal),
                openOrders = openOrders.OrderBy(item => item.OrderId, StringComparer.Ordinal)
            }, JsonOptions);
            var localSnapshotJson = JsonSerializer.Serialize(new
            {
                positions = localPositions.OrderBy(item => item.Symbol, StringComparer.Ordinal),
                openOrders = localOrders.OrderBy(item => item.ClientOrderId, StringComparer.Ordinal)
            }, JsonOptions);
            var diffJson = JsonSerializer.Serialize(differences, JsonOptions);
            var diffHash = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(diffJson)));
            var completedAt = timeProvider.GetUtcNow();
            var hasDifferences = differences.Count > 0;
            var recorded = await reconciliations.RecordAsync(
                new ReconciliationWriteRequest(
                    runContext.Run,
                    Guid.NewGuid(),
                    startedAt,
                    completedAt,
                    hasDifferences ? "reconcile_mismatch" : "clean",
                    brokerSnapshotJson,
                    localSnapshotJson,
                    diffJson,
                    diffHash,
                    hasDifferences),
                cancellationToken);
            var requiresAcknowledgement = recorded.RequiresAcknowledgement;

            if (requiresAcknowledgement)
            {
                status = "reconcile_mismatch";
                outstandingId = recorded.ReconciliationId;
                admission.Block(
                    MismatchBlockSource,
                    "RECONCILE_MISMATCH",
                    $"Broker/account reconciliation {recorded.ReconciliationId:N} found {differences.Count} difference(s).",
                    completedAt);
                logger.LogCritical(
                    "Broker/account reconciliation mismatch. ReconciliationId={ReconciliationId} Differences={Differences}",
                    recorded.ReconciliationId,
                    diffJson);
            }
            else if (hasDifferences)
            {
                status = "acknowledged_mismatch";
                outstandingId = null;
                admission.Clear(MismatchBlockSource);
                logger.LogWarning(
                    "Broker/account reconciliation still has an explicitly acknowledged diff. ReconciliationId={ReconciliationId} Differences={Differences}",
                    recorded.ReconciliationId,
                    diffJson);
            }
            else
            {
                var outstanding = await reconciliations.GetOutstandingAsync(cancellationToken);
                if (outstanding is null)
                {
                    status = "clean";
                    outstandingId = null;
                    admission.Clear(MismatchBlockSource);
                }
                else
                {
                    status = "acknowledgement_required";
                    outstandingId = outstanding.ReconciliationId;
                    admission.Block(
                        MismatchBlockSource,
                        "RECONCILE_ACK_REQUIRED",
                        $"Reconciliation {outstanding.ReconciliationId:N} still requires operator acknowledgement.",
                        completedAt);
                }
            }

            // Run the invariant on every reconciliation. It deliberately merges the
            // committed local position ledger with REST positions, so a just-received
            // fill is protected even while Alpaca's position endpoint is catching up.
            IReadOnlyList<ProtectiveOrderRepair> repairs;
            try
            {
                repairs = await protectiveOrders.EnsureAsync(
                    broker,
                    account.AccountId,
                    brokerPositions,
                    openOrders,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                status = "protection_verification_failed";
                admission.Block(
                    ProtectionBlockSource,
                    "BROKER_SIDE_PROTECTION_FAILED",
                    exception.Message,
                    timeProvider.GetUtcNow());
                logger.LogCritical(
                    exception,
                    "Broker-side protective-order verification failed during account reconciliation.");
                throw;
            }
            foreach (var repair in repairs)
            {
                logger.Log(
                    repair.Succeeded ? LogLevel.Warning : LogLevel.Critical,
                    "Protective-order invariant result for {Symbol}. Succeeded={Succeeded} Detail={Detail} BrokerOrderId={BrokerOrderId}",
                    repair.Symbol,
                    repair.Succeeded,
                    repair.Detail,
                    repair.BrokerOrderId);
            }

            var failedRepairs = repairs.Where(repair => !repair.Succeeded).ToArray();
            if (failedRepairs.Length > 0)
            {
                admission.Block(
                    ProtectionBlockSource,
                    "BROKER_SIDE_PROTECTION_FAILED",
                    String.Join("; ", failedRepairs.Select(repair => $"{repair.Symbol}: {repair.Detail}")),
                    completedAt);
            }
            else
            {
                admission.Clear(ProtectionBlockSource);
            }

            var hasActiveExitHandoff = false;
            foreach (var localPosition in localPositions.Where(item => item.Quantity != 0m))
            {
                if (await HasActiveExitAsync(localPosition, cancellationToken))
                {
                    hasActiveExitHandoff = true;
                    break;
                }
            }

            if (hasActiveExitHandoff)
            {
                admission.Block(
                    PositionExitHandoffBlockSource,
                    "POSITION_EXIT_HANDOFF_ACTIVE",
                    "A durable position exit owns an open position while broker protection is exchanged for the closing order.",
                    completedAt);
            }
            else if (failedRepairs.Length == 0)
            {
                admission.Clear(PositionExitHandoffBlockSource);
            }

            await SuperviseAccountLossAsync(broker, account, openOrders, cancellationToken);
            initialCompleted = true;
            Interlocked.Exchange(ref lastCompletedUnixMilliseconds, completedAt.ToUnixTimeMilliseconds());
            admission.Clear(StartupBlockSource);
            Volatile.Write(ref differenceCount, differences.Count);

            return new AccountReconciliationResult(
                recorded.ReconciliationId,
                status,
                differences,
                requiresAcknowledgement,
                failedRepairs.Length);
        }
        finally
        {
            reconcileLock.Release();
        }
    }

    // The partial-entry close-window gate uses the same broker and position proof as a stored loss review.
    public async Task<bool> IsOpeningOrderResolvedAsync(
        IBrokerClient broker, string clientOrderId, string brokerOrderId, string symbol, decimal minimumFilledQuantity,
        CancellationToken cancellationToken = default)
    {
        await reconcileLock.WaitAsync(cancellationToken);
        try
        {
            var account = await broker.GetAccountSnapshotAsync(cancellationToken);
            var review = new AccountLossCancellationReview(clientOrderId, brokerOrderId, symbol, minimumFilledQuantity, timeProvider.GetUtcNow());
            return await GetCancellationReviewIssueAsync(broker, account.AccountId, review, cancellationToken) is null;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogCritical(exception, "Opening order resolution could not be verified for {ClientOrderId} ({Symbol}).",
                clientOrderId, symbol);
            return false;
        }
        finally
        {
            reconcileLock.Release();
        }
    }

    // Monitor loss even when there is no entry candidate. Loss evidence failure
    // cannot skip reconciliation or stop the broker-side protection check.
    private async Task SuperviseAccountLossAsync(
        IBrokerClient broker, BrokerAccountSnapshot account, IReadOnlyList<ActiveBrokerOrder> openOrders,
        CancellationToken cancellationToken)
    {
        if (lossGuard is null)
        {
            return;
        }

        try
        {
            var state = await lossGuard.ObserveAsync(runContext.Run, broker, account, cancellationToken);
            var orders = state.CancellationSweepPending || state.CancellationReviews is { Count: > 0 }
                ? await broker.GetOpenOrdersAsync(cancellationToken)
                : openOrders;
            if (orderIntents is not null && orderCommands is not null &&
                (state.Breached || state.CancellationSweepPending || state.CancellationReviews is { Count: > 0 }))
            {
                await CancelOwnedOpeningOrdersAsync(broker, account.AccountId, orders, state, cancellationToken);
                if (state.CancellationSweepPending)
                {
                    await lossGuard.CompleteCancellationSweepAsync(
                        runContext.Run, account.AccountId, state.CancellationSweepGeneration, cancellationToken);
                }
            }
            await RecheckCancellationReviewsAsync(broker, account.AccountId, orders,
                state.CancellationReviews ?? [], cancellationToken);
            admission.Clear(AccountLossSupervisionBlockSource);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            admission.Block(AccountLossSupervisionBlockSource, "ACCOUNT_LOSS_EVIDENCE_UNAVAILABLE",
                "Account loss supervision failed to persist its observation.", timeProvider.GetUtcNow());
            logger.LogError(exception, "Account loss supervision failed. New entries require a fresh verified loss observation.");
        }
    }

    // Cancel standalone opening remainders through the immutable command path.
    // Linked bracket groups require a reviewed handoff to preserve their stops.
    private async Task CancelOwnedOpeningOrdersAsync(
        IBrokerClient broker, string accountId, IReadOnlyList<ActiveBrokerOrder> orders,
        AccountLossState state, CancellationToken cancellationToken)
    {
        foreach (var order in orders)
        {
            var pendingReview = state.CancellationReviews?.FirstOrDefault(review =>
                review.BrokerOrderId == order.OrderId && review.ClientOrderId == order.ClientOrderId);
            if ((!state.Breached && !state.CancellationSweepPending && pendingReview is null) ||
                !OrderSubmissionService.IsRootOpeningOrder(order))
            {
                continue;
            }

            var intent = await orderIntents!.GetByClientOrderIdAsync(order.ClientOrderId, cancellationToken);
            if (intent?.Kind is not (OrderIntentKind.StrategyEntry or OrderIntentKind.OperatorEntry) ||
                !String.Equals(intent.AccountId, accountId, StringComparison.Ordinal))
            {
                continue;
            }

            var review = pendingReview ?? new AccountLossCancellationReview(
                intent.ClientOrderId, order.OrderId, intent.Symbol, order.FilledQuantity, timeProvider.GetUtcNow());
            if (!OrderSubmissionService.IsStandaloneOpeningOrder(order) ||
                orders.Any(child => child.ParentClientOrderId == order.ClientOrderId || child.ParentOrderId == order.OrderId))
            {
                await RequireCancellationReviewAsync(accountId, review, cancellationToken);
                continue;
            }

            await CancelStandaloneOpeningOrderAsync(broker, accountId, review, pendingReview is not null, cancellationToken);
        }
    }

    // Journal a cancellation before it is attempted so a crash or pending reply
    // leaves an explicit account stop, then retry its exact standalone order.
    private async Task CancelStandaloneOpeningOrderAsync(
        IBrokerClient broker, string accountId, AccountLossCancellationReview review,
        bool wasPending, CancellationToken cancellationToken)
    {
        if (!wasPending)
        {
            await lossGuard!.SetCancellationReviewAsync(runContext.Run, accountId, review, true, cancellationToken);
        }

        try
        {
            var result = await orderCommands!.RequestCancelAsync(new OrderCancellationSubmission(
                review.ClientOrderId, review.BrokerOrderId, "account_loss_entry_stop",
                timeProvider.GetUtcNow().ToUniversalTime(), review.ClientOrderId, RequireStandaloneOpeningOrder: true),
                broker, cancellationToken);
            if (result.ClientOrderId == review.ClientOrderId && result.BrokerOrderId == review.BrokerOrderId &&
                OrderStateMachine.IsTerminal(result.State))
            {
                // The next review verifies the accounted fill against fresh positions and resting coverage.
                return;
            }
            logger.LogCritical("Cancellation is still pending for opening order {ClientOrderId}.", review.ClientOrderId);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogCritical(exception, "Account loss cancellation remains unresolved for {ClientOrderId}.", review.ClientOrderId);
        }
    }

    // Unverified linked protection needs review before cancellation or new entries.
    private async Task RequireCancellationReviewAsync(
        string accountId, AccountLossCancellationReview review, CancellationToken cancellationToken)
    {
        await lossGuard!.SetCancellationReviewAsync(runContext.Run, accountId, review, true, cancellationToken);
        logger.LogCritical("Account loss stop requires protection review before cancelling opening order {ClientOrderId}. FilledQuantity={FilledQuantity}",
            review.ClientOrderId, review.FilledQuantity);
    }

    // Keep the durable entry stop until the broker and journal agree on the terminal order and its resulting exposure.
    private async Task RecheckCancellationReviewsAsync(
        IBrokerClient broker, string accountId, IReadOnlyList<ActiveBrokerOrder> openOrders,
        IReadOnlyList<AccountLossCancellationReview> reviews, CancellationToken cancellationToken)
    {
        foreach (var review in reviews)
        {
            if (HasWorkingOpeningOrder(openOrders, review))
            {
                continue;
            }

            try
            {
                var reason = await GetCancellationReviewIssueAsync(broker, accountId, review, cancellationToken);
                if (reason is null)
                {
                    await lossGuard!.SetCancellationReviewAsync(runContext.Run, accountId, review, false, cancellationToken);
                }
                else
                {
                    logger.LogCritical("Cancellation review remains unresolved for {ClientOrderId} ({Symbol}): {Reason}",
                        review.ClientOrderId, review.Symbol, reason);
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogCritical(exception, "Cancellation review verification failed for {ClientOrderId} ({Symbol}).",
                    review.ClientOrderId, review.Symbol);
            }
        }
    }

    // Verify exposure after filling any gap in the durable order and position journals.
    private async Task<string?> GetCancellationReviewIssueAsync(
        IBrokerClient broker, string accountId, AccountLossCancellationReview review, CancellationToken cancellationToken)
    {
        if (orderCommands is null)
        {
            return "The durable order command service is unavailable.";
        }

        var terminal = await orderCommands.ReconcileTerminalOpeningOrderAsync(
            accountId, review.ClientOrderId, review.BrokerOrderId, broker, cancellationToken);
        if (terminal is null || !terminal.Ticker.Equals(review.Symbol, StringComparison.OrdinalIgnoreCase) ||
            terminal.FilledQuantity < review.FilledQuantity)
        {
            return "The exact terminal parent and its reconciled cumulative fill could not be verified.";
        }

        var local = await positions.GetCurrentAsync(accountId, review.Symbol, cancellationToken);
        var beforePositions = await broker.GetOpenPositionsAsync(cancellationToken);
        var beforeOrders = await broker.GetOpenOrdersAsync(cancellationToken);
        var repairs = await protectiveOrders.EnsureAsync(broker, accountId, beforePositions, beforeOrders, cancellationToken);
        if (repairs.Any(repair => !repair.Succeeded))
        {
            return "Broker-side protection repair reported a failure.";
        }

        var finalOrders = await broker.GetOpenOrdersAsync(cancellationToken);
        var finalPositions = await broker.GetOpenPositionsAsync(cancellationToken);
        var finalAccount = await broker.GetAccountSnapshotAsync(cancellationToken);
        var finalTerminal = await broker.GetOrderByClientOrderIdAsync(review.ClientOrderId, cancellationToken);
        if (finalAccount.AccountId != accountId || !MatchesTerminalParent(finalTerminal, terminal) ||
            HasWorkingOpeningOrder(beforeOrders, review) || HasWorkingOpeningOrder(finalOrders, review))
        {
            return "The broker account, terminal parent, or replacement evidence changed during protection verification.";
        }

        var first = FindBrokerPosition(beforePositions, review.Symbol);
        var last = FindBrokerPosition(finalPositions, review.Symbol);
        var quantity = local?.Quantity ?? 0m;
        if ((first is null ? 0m : SignedBrokerQuantity(first)) != quantity ||
            (last is null ? 0m : SignedBrokerQuantity(last)) != quantity)
        {
            return "Fresh broker positions disagree with each other or with the accounted position.";
        }

        if (last is null ?
            beforeOrders.Concat(finalOrders).Any(order => order.Ticker.Equals(review.Symbol, StringComparison.OrdinalIgnoreCase)) :
            !await protectiveOrders.HasVerifiedCoverageAsync(broker, accountId, last, finalOrders, cancellationToken))
        {
            return "The broker has unverified working orders or the exposure lacks verified resting stop coverage.";
        }

        var finalLocal = await positions.GetCurrentAsync(accountId, review.Symbol, cancellationToken);
        var finalState = await orderEvents.GetCurrentAsync(review.ClientOrderId, cancellationToken);
        var expectedState = BrokerOrderStateProjection.Project(OrderStatusCodec.ParseBrokerValue(terminal.Status));
        if (finalLocal?.PositionEventId != local?.PositionEventId || finalState is null || finalState.BrokerOrderId != review.BrokerOrderId ||
            finalState.State != expectedState || (finalState.FilledQuantity ?? 0m) != terminal.FilledQuantity)
        {
            return "The order or position journal changed during protection verification.";
        }

        return null;
    }

    // A new cumulative fill, replacement, or identity change invalidates the completed protection check.
    private static bool MatchesTerminalParent(ActiveBrokerOrder? current, ActiveBrokerOrder terminal) =>
        current is not null && OrderSubmissionService.IsRootOpeningOrder(current) &&
        OrderSubmissionService.IsConfirmedTerminalOrder(current) &&
        current.OrderId == terminal.OrderId && current.ClientOrderId == terminal.ClientOrderId &&
        current.GroupId == terminal.GroupId && current.GroupRole == terminal.GroupRole &&
        current.ReplacesOrderId == terminal.ReplacesOrderId && current.OrderClass == terminal.OrderClass &&
        (current.NestedLegOrderIds ?? []).Order(StringComparer.Ordinal)
            .SequenceEqual((terminal.NestedLegOrderIds ?? []).Order(StringComparer.Ordinal), StringComparer.Ordinal) &&
        current.Ticker == terminal.Ticker &&
        current.Side == terminal.Side && current.Qty == terminal.Qty && current.Status == terminal.Status &&
        current.UpdatedAt == terminal.UpdatedAt && current.FilledQuantity == terminal.FilledQuantity &&
        current.FilledAveragePrice == terminal.FilledAveragePrice;

    // Treat an open exact root or an observed successor as contradictory terminal evidence.
    private static bool HasWorkingOpeningOrder(IReadOnlyList<ActiveBrokerOrder> orders, AccountLossCancellationReview review) =>
        orders.Any(order => order.OrderId == review.BrokerOrderId || order.ReplacesOrderId == review.BrokerOrderId ||
            order.ClientOrderId == review.ClientOrderId);

    // An absent symbol is flat only when the durable ledger independently agrees after fill repair.
    private static BrokerPosition? FindBrokerPosition(IReadOnlyList<BrokerPosition> positions, string symbol) =>
        positions.SingleOrDefault(position => position.Ticker.Equals(symbol, StringComparison.OrdinalIgnoreCase)) is { Qty: not 0m } position
            ? position
            : null;

    private Task<bool> HasActiveExitAsync(
        PositionLedgerSnapshot position,
        CancellationToken cancellationToken)
    {
        return orderIntents is null
            ? Task.FromResult(false)
            : orderIntents.HasActivePositionExitAsync(
                position.AccountId,
                position.Symbol,
                position.PositionGenerationEventId,
                cancellationToken);
    }

    private async Task<IReadOnlyList<ActiveBrokerOrder>> ResolveReplacementOwnersAsync(
        IReadOnlyList<ActiveBrokerOrder> brokerOrders,
        CancellationToken cancellationToken)
    {
        if (stopReplacements is null)
        {
            return brokerOrders;
        }

        var resolved = new List<ActiveBrokerOrder>(brokerOrders.Count);
        foreach (var order in brokerOrders)
        {
            var replacement = await stopReplacements.GetVerifiedByReplacementClientOrderIdAsync(
                order.ClientOrderId,
                cancellationToken);
            resolved.Add(replacement is null
                ? order
                : order with
                {
                    ClientOrderId = replacement.OwnerClientOrderId,
                    ParentClientOrderId = replacement.OwnerClientOrderId
                });
        }

        return resolved;
    }

    public async Task AcknowledgeAsync(
        Guid reconciliationId,
        string actor,
        string reason,
        CancellationToken cancellationToken = default)
    {
        await reconcileLock.WaitAsync(cancellationToken);
        try
        {
            var outstanding = await reconciliations.GetOutstandingAsync(cancellationToken);
            if (outstanding is null || outstanding.ReconciliationId != reconciliationId)
            {
                throw new InvalidOperationException(
                    $"Reconciliation {reconciliationId:N} is not the active mismatch.");
            }

            var acknowledged = await reconciliations.AcknowledgeAsync(
                new ReconciliationAcknowledgement(
                    reconciliationId,
                    actor,
                    reason,
                    timeProvider.GetUtcNow()),
                cancellationToken);

            outstandingId = null;
            status = initialCompleted ? "acknowledged_pending_recheck" : "not_ready";
            Volatile.Write(ref differenceCount, 0);
            admission.Clear(MismatchBlockSource);
            logger.LogWarning(
                "Reconciliation mismatch acknowledged. ReconciliationId={ReconciliationId} Actor={Actor} Reason={Reason}",
                reconciliationId,
                actor.Trim(),
                reason.Trim());
        }
        finally
        {
            reconcileLock.Release();
        }
    }

    private static IReadOnlyList<ReconciliationDifference> BuildDifferences(
        IReadOnlyList<BrokerPosition> brokerPositions,
        IReadOnlyList<ActiveBrokerOrder> brokerOrders,
        IReadOnlyList<PositionLedgerSnapshot> localPositions,
        IReadOnlyList<OrderStateSnapshot> localOrders,
        DateTimeOffset now,
        TimeSpan orphanTimeout)
    {
        var differences = new List<ReconciliationDifference>();
        var brokerPositionMap = brokerPositions.ToDictionary(
            item => NormalizeSymbol(item.Ticker),
            SignedBrokerQuantity,
            StringComparer.Ordinal);
        var localPositionMap = localPositions.ToDictionary(
            item => NormalizeSymbol(item.Symbol),
            item => item.Quantity,
            StringComparer.Ordinal);
        foreach (var symbol in brokerPositionMap.Keys.Concat(localPositionMap.Keys).Distinct(StringComparer.Ordinal).Order())
        {
            var brokerQuantity = brokerPositionMap.GetValueOrDefault(symbol);
            var localQuantity = localPositionMap.GetValueOrDefault(symbol);
            if (brokerQuantity != localQuantity)
            {
                differences.Add(new ReconciliationDifference(
                    "position_quantity",
                    symbol,
                    $"Broker quantity={brokerQuantity}; ledger quantity={localQuantity}."));
            }
        }

        foreach (var brokerPosition in brokerPositions.Where(position => position.Qty != 0m))
        {
            var ownedProtectiveOrders = brokerOrders.Where(order =>
                localOrders.Any(localOrder =>
                    localOrder.ClientOrderId.Equals(order.ClientOrderId, StringComparison.Ordinal) ||
                    String.Equals(localOrder.ClientOrderId, order.ParentClientOrderId, StringComparison.Ordinal)))
                .ToArray();
            var coverage = ProtectiveOrderInvariantService.ProtectiveCoverage(
                brokerPosition,
                ownedProtectiveOrders);
            var required = Math.Abs(brokerPosition.Qty);
            if (coverage < required)
            {
                differences.Add(new ReconciliationDifference(
                    "missing_protective_order",
                    NormalizeSymbol(brokerPosition.Ticker),
                    $"Broker position side={brokerPosition.Side} quantity={required} has active opposite-side stop coverage={coverage}."));
            }
            else if (coverage > required)
            {
                differences.Add(new ReconciliationDifference(
                    "protective_order_overcoverage",
                    NormalizeSymbol(brokerPosition.Ticker),
                    $"Broker position side={brokerPosition.Side} quantity={required} has excessive active stop coverage={coverage}."));
            }
        }

        foreach (var brokerOrder in brokerOrders.Where(order =>
                     !localOrders.Any(localOrder => OrdersMatch(localOrder, order))))
        {
            differences.Add(new ReconciliationDifference(
                "unexpected_broker_order",
                !String.IsNullOrWhiteSpace(brokerOrder.ClientOrderId)
                    ? brokerOrder.ClientOrderId
                    : brokerOrder.OrderId,
                $"Broker has open {brokerOrder.Side} {brokerOrder.Ticker} order {brokerOrder.OrderId} absent from the local nonterminal ledger."));
        }

        foreach (var localOrder in localOrders.Where(order =>
                     !brokerOrders.Any(brokerOrder => OrdersMatch(order, brokerOrder))))
        {
            var age = now - localOrder.LocalTimestampUtc.ToUniversalTime();
            if (age >= orphanTimeout)
            {
                differences.Add(new ReconciliationDifference(
                    "orphan_local_order",
                    localOrder.ClientOrderId,
                    $"Local state={localOrder.State.ToStorageValue()} has no broker open-order record after {age.TotalSeconds:0.###} seconds."));
            }
        }

        return differences
            .OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool OrdersMatch(OrderStateSnapshot local, ActiveBrokerOrder broker) =>
        (!String.IsNullOrWhiteSpace(local.BrokerOrderId) &&
         local.BrokerOrderId.Equals(broker.OrderId, StringComparison.Ordinal)) ||
        (ClientOrderIdFactory.IsBindingFormat(broker.ClientOrderId) &&
         local.ClientOrderId.Equals(broker.ClientOrderId, StringComparison.Ordinal)) ||
        (ClientOrderIdFactory.IsBindingFormat(broker.ParentClientOrderId) &&
         local.ClientOrderId.Equals(broker.ParentClientOrderId, StringComparison.Ordinal));

    private static decimal SignedBrokerQuantity(BrokerPosition position) =>
        position.Side.Trim().ToLowerInvariant() switch
        {
            "long" => Math.Abs(position.Qty),
            "short" => -Math.Abs(position.Qty),
            _ => throw new InvalidOperationException(
                $"Broker position {position.Ticker} has unsupported side '{position.Side}'.")
        };

    private static string NormalizeSymbol(string symbol) =>
        !String.IsNullOrWhiteSpace(symbol)
            ? symbol.Trim().ToUpperInvariant()
            : throw new InvalidOperationException("Position symbol is required.");

    private static DateTimeOffset? FromUnixMilliseconds(long value) =>
        value < 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(value);
}
