using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
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
    bool RequiresAcknowledgement);

public interface IAccountReconciliationService
{
    AccountReconciliationHealth GetHealth();

    Task<AccountReconciliationResult> ReconcileAsync(
        IBrokerClient broker,
        IReadOnlyList<ActiveBrokerOrder> openOrders,
        CancellationToken cancellationToken = default);

    Task AcknowledgeAsync(
        Guid reconciliationId,
        string actor,
        string reason,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Performs EXE-08 account reconciliation against the durable order and position journals.
/// A mismatch is never adopted as truth. Position state advances only from authoritative
/// stream fills or idempotent REST repair for an order already owned by TradingFlow.
/// </summary>
public sealed class AccountReconciliationService : IAccountReconciliationService
{
    internal const string StartupBlockSource = "account_reconciliation_startup";
    internal const string MismatchBlockSource = "account_reconciliation";

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
    private readonly SemaphoreSlim reconcileLock = new(1, 1);
    private volatile bool initialCompleted;
    private long lastCompletedUnixMilliseconds = -1;
    private string status = "not_ready";
    private Guid? outstandingId;
    private int differenceCount;

    public AccountReconciliationService(
        IOrderEventRepository orderEvents,
        IPositionLedgerRepository positions,
        IReconciliationRepository reconciliations,
        IEntryAdmissionControl admission,
        IProtectiveOrderInvariantService protectiveOrders,
        ReconciliationRunContext runContext,
        AccountReconciliationOptions options,
        TimeProvider timeProvider,
        ILogger<AccountReconciliationService> logger)
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
            var brokerPositions = await broker.GetOpenPositionsAsync(cancellationToken);
            var localPositions = await positions.ListCurrentAsync(cancellationToken);
            var localOrders = (await orderEvents.ListReconcilableAsync(cancellationToken)).ToList();
            foreach (var parentClientOrderId in openOrders
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
                openOrders,
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

            initialCompleted = true;
            Interlocked.Exchange(ref lastCompletedUnixMilliseconds, completedAt.ToUnixTimeMilliseconds());
            admission.Clear(StartupBlockSource);
            Volatile.Write(ref differenceCount, differences.Count);
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

            if (differences.Any(item => item.Kind is "missing_protective_order" or "protective_order_overcoverage"))
            {
                var repairs = await protectiveOrders.EnsureAsync(
                    broker,
                    brokerPositions,
                    openOrders,
                    cancellationToken);
                foreach (var repair in repairs)
                {
                    logger.Log(
                        repair.Succeeded ? LogLevel.Critical : LogLevel.Error,
                        "EXE-09 repair result for {Symbol}. Succeeded={Succeeded} Detail={Detail} BrokerOrderId={BrokerOrderId}",
                        repair.Symbol,
                        repair.Succeeded,
                        repair.Detail,
                        repair.BrokerOrderId);
                }
            }

            return new AccountReconciliationResult(
                recorded.ReconciliationId,
                status,
                differences,
                requiresAcknowledgement);
        }
        finally
        {
            reconcileLock.Release();
        }
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
            var coverage = ProtectiveOrderInvariantService.ProtectiveCoverage(brokerPosition, brokerOrders);
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
