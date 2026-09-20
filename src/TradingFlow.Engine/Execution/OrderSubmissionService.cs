using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Engine.Execution;

public sealed record ValidatedEntryCandidate(
    Guid CandidateId,
    string DiscoverySource,
    string Horizon,
    DateTimeOffset DiscoveredAtUtc,
    DateTimeOffset RevalidatedAtUtc,
    string SetupEvidenceJson,
    int CandidateVersion = 0,
    string SemanticDecisionSha256 = "");

public sealed record EntryOrderSubmission(
    Guid IntentId,
    ValidatedEntryCandidate Candidate,
    ExecutionRunContext RunContext,
    string StrategyId,
    string Side,
    string OrderType,
    string TimeInForce,
    DateOnly SessionDate,
    DateTimeOffset CreatedAtUtc,
    FinalizedOrder Order,
    bool AllowExtendedHoursTrading = false,
    StrategyArtifactIdentity? StrategyIdentity = null,
    StrategySelectionMode? StrategySelectionMode = null,
    OperatorOverrideAuthorization? OperatorOverride = null,
    StrategyArtifactIdentity? ExitPolicyIdentity = null);

public sealed record OrderSubmissionResult(
    string BrokerOrderId,
    string ClientOrderId,
    Guid IntentId,
    DateTimeOffset BrokerAcceptedAtUtc,
    bool SubmittedOutsideRegularHours = false);

public sealed record ProtectiveStopSubmission(
    Guid IntentId,
    ExecutionRunContext RunContext,
    string AccountId,
    string Symbol,
    string Side,
    decimal Quantity,
    decimal StopPrice,
    DateOnly SessionDate,
    DateTimeOffset CreatedAtUtc,
    string PositionGenerationIdentity,
    int ProtectionRevision);

public sealed record PositionExitSubmission(
    ExecutionRunContext RunContext,
    string Symbol,
    decimal Quantity,
    string Reason,
    DateTimeOffset CreatedAtUtc,
    bool AllowExtendedHoursTrading = false,
    string? RequestedOrderType = null,
    string? RequestedTimeInForce = null,
    decimal? RequestedLimitPrice = null,
    decimal? RequestedStopPrice = null);

public sealed record OrderCancellationSubmission(
    string ClientOrderId,
    string BrokerOrderId,
    string Reason,
    DateTimeOffset RequestedAtUtc,
    string? BrokerClientOrderId = null);

public sealed record ProtectiveStopReplacementSubmission(
    string OwnerClientOrderId,
    string BrokerOrderId,
    string Symbol,
    decimal StopPrice,
    string Reason,
    DateTimeOffset RequestedAtUtc);

public sealed record PositionExitExecutionOptions(TimeSpan QuoteMaxAge)
{
    public PositionExitExecutionOptions(int quoteMaxAgeMilliseconds)
        : this(TimeSpan.FromMilliseconds(quoteMaxAgeMilliseconds))
    {
        if (quoteMaxAgeMilliseconds is < 250 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(quoteMaxAgeMilliseconds));
        }
    }
}

public sealed class PositionExitNoLongerRequiredException(string accountId, string symbol) :
    InvalidOperationException(
        $"Position exit is no longer required because {symbol} is flat in account '{accountId}'.")
{
    public string AccountId { get; } = accountId;
    public string Symbol { get; } = symbol;
}

public sealed class PositionExitReconciliationRequiredException(
    string accountId,
    string symbol,
    decimal localQuantity,
    decimal? brokerQuantity) : InvalidOperationException(
        $"Position exit for {symbol} is blocked because local quantity {localQuantity} does not match " +
        $"broker quantity {(brokerQuantity is null ? "missing" : brokerQuantity.Value)} in account '{accountId}'.")
{
    public string AccountId { get; } = accountId;
    public string Symbol { get; } = symbol;
    public decimal LocalQuantity { get; } = localQuantity;
    public decimal? BrokerQuantity { get; } = brokerQuantity;
}

public sealed class PositionExitProtectionRestoreFailedException(
    string accountId,
    string symbol,
    Exception exitFailure,
    Exception restoreFailure) : InvalidOperationException(
        $"Position exit for {symbol} in account '{accountId}' failed after protection handoff, " +
        "and broker-side protection could not be restored. New entries remain blocked.",
        new AggregateException(exitFailure, restoreFailure))
{
    public string AccountId { get; } = accountId;
    public string Symbol { get; } = symbol;
    public Exception ExitFailure { get; } = exitFailure;
    public Exception RestoreFailure { get; } = restoreFailure;
}

public sealed class BrokerCommandRecoveryIncompleteException(
    string operation,
    int recoveredCount,
    int pendingCount,
    int failedCount) : InvalidOperationException(
        $"{operation} recovery is incomplete: {recoveredCount} recovered, {pendingCount} pending, {failedCount} failed.")
{
    public string Operation { get; } = operation;
    public int RecoveredCount { get; } = recoveredCount;
    public int PendingCount { get; } = pendingCount;
    public int FailedCount { get; } = failedCount;
}

public interface IOrderCommandService
{
    Task<OrderSubmissionResult> SubmitEntryOrderAsync(
        EntryOrderSubmission submission,
        IBrokerClient brokerClient,
        CancellationToken cancellationToken);

    Task<OrderSubmissionResult> SubmitProtectiveStopAsync(
        ProtectiveStopSubmission submission,
        IBrokerClient brokerClient,
        CancellationToken cancellationToken);

    Task<OrderSubmissionResult> SubmitPositionExitAsync(
        PositionExitSubmission submission,
        IBrokerClient brokerClient,
        CancellationToken cancellationToken);

    Task<OrderStateSnapshot> RequestCancelAsync(
        OrderCancellationSubmission submission,
        IBrokerClient brokerClient,
        CancellationToken cancellationToken);

    Task<ActiveBrokerOrder> ReplaceProtectiveStopAsync(
        ProtectiveStopReplacementSubmission submission,
        IBrokerClient brokerClient,
        CancellationToken cancellationToken);

    Task<int> RecoverPendingProtectiveStopReplacementsAsync(
        IBrokerClient brokerClient,
        CancellationToken cancellationToken);

    Task<int> RecoverUnpreparedPositionExitsAsync(
        IBrokerClient brokerClient,
        CancellationToken cancellationToken);

    Task<int> RecoverPendingCancellationsAsync(
        IBrokerClient brokerClient,
        CancellationToken cancellationToken);
}

public interface IOrderSubmissionService : IOrderCommandService
{
}

/// <summary>
/// Reserves and fsyncs an immutable intent before calling
/// the broker with the persisted client order ID. A logical retry reuses that ID.
/// </summary>
public sealed class OrderSubmissionService : IOrderSubmissionService
{
    private sealed record CanceledProtection(
        OrderIntentRecord Intent,
        string PositionGenerationIdentity,
        int ProtectionRevision);

    private enum ExitHandoffDisposition
    {
        ExitActiveOrFilled,
        SafeToRestore,
        Ambiguous
    }

    private readonly IOrderIntentRepository intentRepository;
    private readonly IEntryGateChain entryGates;
    private readonly IOrderDispatchService dispatcher;
    private readonly IPositionLedgerRepository? positions;
    private readonly IOrderEventRepository? events;
    private readonly IOrderLifecycleService? lifecycle;
    private readonly IBrokerAccountBindingService? accountBinding;
    private readonly IProtectiveStopReplacementRepository? stopReplacements;
    private readonly PositionExitExecutionOptions exitOptions;
    private readonly TimeSpan replacementLeaseDuration;
    private readonly TimeSpan cancellationConfirmationTimeout;
    private readonly TimeSpan cancellationPollInterval;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<OrderSubmissionService> logger;
    private readonly IBrokerMutationCoordinator brokerMutations;
    private readonly IEntryAdmissionControl? admission;
    private readonly string replacementLeaseOwner =
        $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public OrderSubmissionService(
        IOrderIntentRepository intentRepository,
        IOrderDispatchService dispatcher,
        IEntryGateChain entryGates,
        ILogger<OrderSubmissionService> logger,
        IPositionLedgerRepository? positions = null,
        PositionExitExecutionOptions? exitOptions = null,
        IOrderEventRepository? events = null,
        IOrderLifecycleService? lifecycle = null,
        IBrokerAccountBindingService? accountBinding = null,
        IProtectiveStopReplacementRepository? stopReplacements = null,
        OrderDispatchOptions? dispatchOptions = null,
        TimeProvider? timeProvider = null,
        IEntryAdmissionControl? admission = null,
        IBrokerMutationCoordinator? brokerMutations = null)
    {
        this.intentRepository = intentRepository;
        this.entryGates = entryGates;
        this.dispatcher = dispatcher;
        this.logger = logger;
        this.brokerMutations = brokerMutations ?? new BrokerMutationCoordinator();
        this.positions = positions;
        this.exitOptions = exitOptions ?? new PositionExitExecutionOptions(2_000);
        this.events = events;
        this.lifecycle = lifecycle;
        this.accountBinding = accountBinding;
        this.stopReplacements = stopReplacements;
        replacementLeaseDuration = dispatchOptions?.LeaseDuration ?? TimeSpan.FromSeconds(30);
        cancellationConfirmationTimeout = dispatchOptions?.OrphanTimeout ?? TimeSpan.FromSeconds(30);
        cancellationPollInterval = dispatchOptions?.RecoveryInterval ?? TimeSpan.FromSeconds(1);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.admission = admission;
    }

    public async Task<OrderSubmissionResult> SubmitEntryOrderAsync(
        EntryOrderSubmission submission,
        IBrokerClient brokerClient,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(brokerClient);
        Validate(submission);
        var existingIntent = await intentRepository.GetByIntentIdAsync(
            submission.IntentId,
            cancellationToken);
        if (existingIntent is not null)
        {
            return await dispatcher.DispatchAsync(
                existingIntent.IntentId,
                brokerClient,
                cancellationToken);
        }

        return await entryGates.ExecuteAsync(
            submission,
            brokerClient,
            (approval, token) => SubmitBracketOrderCoreAsync(submission, brokerClient, approval, token),
            cancellationToken);
    }

    private async Task<OrderSubmissionResult> SubmitBracketOrderCoreAsync(
        EntryOrderSubmission submission,
        IBrokerClient brokerClient,
        EntryGateApproval approval,
        CancellationToken cancellationToken)
    {
        using var commandMutation = brokerMutations.Enter(
            $"reserve and dispatch entry {submission.IntentId:N}");
        var sessionDate = approval.TradeDate;
        var submitOutsideRegularHours = approval.SubmitOutsideRegularHours;
        if (accountBinding is not null)
        {
            var boundAccount = await accountBinding.ValidateAsync(brokerClient, cancellationToken);
            if (!boundAccount.AccountId.Equals(
                    approval.PortfolioRisk.AccountId,
                    StringComparison.Ordinal))
            {
                throw new BrokerAccountMismatchException(
                    approval.PortfolioRisk.AccountId,
                    boundAccount.AccountId);
            }
        }

        if (submission.OperatorOverride is not null)
        {
            if (!submission.RunContext.Profile.Equals("paper", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Operator-direct entries are permitted only in the paper profile.");
            }

            if (submission.StrategyIdentity is not null || submission.StrategySelectionMode is not null)
            {
                throw new InvalidOperationException(
                    "An operator override cannot also claim strategy execution authorization.");
            }
        }
        var requestJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            submission.StrategyId,
            submission.Side,
            submission.OrderType,
            submission.TimeInForce,
            symbol = submission.Order.Ticker,
            quantity = submission.Order.ShareQuantity,
            limitPrice = submission.Order.LimitPrice,
            stopPrice = submission.Order.StopLossPrice,
            takeProfitPrice = submission.Order.TakeProfitPrice,
            submission.AllowExtendedHoursTrading,
            strategyIdentity = submission.StrategyIdentity,
            strategySelectionMode = submission.StrategySelectionMode,
            operatorOverride = submission.OperatorOverride is not null,
            operatorOverrideActor = submission.OperatorOverride?.Actor,
            operatorOverrideReason = submission.OperatorOverride?.Reason,
            operatorOverrideIssuedAtUtc = submission.OperatorOverride?.IssuedAtUtc,
            exitPolicyIdentity = submission.ExitPolicyIdentity,
            horizon = submission.Candidate.Horizon,
            candidateId = submission.OperatorOverride is null
                ? submission.Candidate.CandidateId
                : (Guid?)null,
            candidateVersion = submission.OperatorOverride is null
                ? submission.Candidate.CandidateVersion
                : (int?)null,
            candidateSemanticDecisionSha256 = submission.OperatorOverride is null
                ? submission.Candidate.SemanticDecisionSha256
                : null,
            requestedAtUtc = submission.CreatedAtUtc.ToUniversalTime(),
            submitOutsideRegularHours,
            sessionDate
        });
        var run = ToRun(submission.RunContext);
        var reservationResult = await intentRepository.ReserveAsync(
            run,
            new OrderIntentReservation(
                submission.IntentId,
                submission.OperatorOverride is null
                    ? OrderIntentKind.StrategyEntry
                    : OrderIntentKind.OperatorEntry,
                submission.OperatorOverride is not null ? null : submission.Candidate.CandidateId,
                approval.PortfolioRisk.AccountId,
                submission.StrategyId,
                submission.Order.Ticker.Trim().ToUpperInvariant(),
                NormalizeSide(submission.Side),
                submission.OrderType.Trim().ToLowerInvariant(),
                submission.TimeInForce.Trim().ToLowerInvariant(),
                submission.Order.ShareQuantity,
                submission.Order.LimitPrice,
                submission.Order.StopLossPrice,
                sessionDate,
                submission.CreatedAtUtc.ToUniversalTime(),
                requestJson,
                DispatchExpiresAtUtc: approval.DispatchExpiresAtUtc.ToUniversalTime(),
                PortfolioRisk: approval.PortfolioRisk,
                CandidateExpectedVersion: submission.OperatorOverride is null
                    ? submission.Candidate.CandidateVersion
                    : null,
                CandidateSemanticDecisionSha256: submission.OperatorOverride is null
                    ? submission.Candidate.SemanticDecisionSha256
                    : null,
                PositionGenerationEventId: null),
            cancellationToken);
        var intent = reservationResult.Intent;

        if (!reservationResult.OwningRunStatus.Equals("running", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Order '{intent.ClientOrderId}' belongs to run state '{reservationResult.OwningRunStatus}' and cannot begin broker submission.");
        }

        logger.LogInformation(
            "Reserved persisted order intent {IntentId} with client order ID {ClientOrderId} for {Symbol} {StrategyId}.",
            intent.IntentId,
            intent.ClientOrderId,
            intent.Symbol,
            intent.StrategyId);
        return await dispatcher.DispatchAsync(intent.IntentId, brokerClient, cancellationToken);
    }

    public async Task<OrderSubmissionResult> SubmitProtectiveStopAsync(
        ProtectiveStopSubmission submission,
        IBrokerClient brokerClient,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(brokerClient);
        using var commandMutation = brokerMutations.Enter(
            $"reserve and dispatch protection {submission.IntentId:N}");
        return await SubmitProtectiveStopCoreAsync(submission, brokerClient, cancellationToken);
    }

    private async Task<OrderSubmissionResult> SubmitProtectiveStopCoreAsync(
        ProtectiveStopSubmission submission,
        IBrokerClient brokerClient,
        CancellationToken cancellationToken)
    {
        Validate(submission);

        if (accountBinding is not null)
        {
            await ValidateBrokerAccountAsync(
                submission.AccountId,
                brokerClient,
                cancellationToken);
        }

        var symbol = submission.Symbol.Trim().ToUpperInvariant();
        var side = NormalizeSide(submission.Side);
        var requestJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            strategyId = "BACKSTOP",
            symbol,
            side,
            orderType = "stop",
            timeInForce = "gtc",
            quantity = submission.Quantity,
            stopPrice = submission.StopPrice,
            submission.PositionGenerationIdentity,
            submission.ProtectionRevision,
            submission.SessionDate
        });
        var run = new ProductionRun
        {
            RunId = submission.RunContext.RunId,
            SchemaVersion = 1,
            ConfigHash = submission.RunContext.ConfigHash,
            CodeVersion = submission.RunContext.CodeVersion,
            Profile = submission.RunContext.Profile,
            Status = "running",
            StartedAtUtc = submission.RunContext.StartedAtUtc
        };
        var reservationResult = await intentRepository.ReserveAsync(
            run,
            new OrderIntentReservation(
                submission.IntentId,
                OrderIntentKind.ProtectiveStop,
                null,
                submission.AccountId,
                "BACKSTOP",
                symbol,
                side,
                "stop",
                "gtc",
                submission.Quantity,
                null,
                submission.StopPrice,
                submission.SessionDate,
                submission.CreatedAtUtc.ToUniversalTime(),
                requestJson,
                PositionGenerationEventId: ParsePositionGenerationEventId(
                    submission.PositionGenerationIdentity)),
            cancellationToken);
        var intent = reservationResult.Intent;

        var result = await dispatcher.DispatchAsync(intent.IntentId, brokerClient, cancellationToken);
        logger.LogCritical(
            "Restored broker protection for {Symbol}. ClientOrderId={ClientOrderId} BrokerOrderId={BrokerOrderId} StopPrice={StopPrice} Quantity={Quantity}.",
            symbol,
            result.ClientOrderId,
            result.BrokerOrderId,
            submission.StopPrice,
            submission.Quantity);
        return result;
    }

    public async Task<OrderSubmissionResult> SubmitPositionExitAsync(
        PositionExitSubmission submission,
        IBrokerClient brokerClient,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(brokerClient);
        using var commandMutation = brokerMutations.Enter(
            $"prepare and dispatch position exit {submission.Symbol}");
        if (positions is null || events is null || lifecycle is null)
        {
            throw new InvalidOperationException(
                "Position ledger, order events, and lifecycle are required for durable exits.");
        }

        var symbol = submission.Symbol.Trim().ToUpperInvariant();
        if (submission.RunContext.RunId == Guid.Empty || String.IsNullOrWhiteSpace(symbol) ||
            submission.Quantity <= 0m || String.IsNullOrWhiteSpace(submission.Reason) ||
            submission.CreatedAtUtc == default || submission.CreatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "A durable exit requires run, symbol, quantity, reason, and a UTC timestamp.");
        }

        var account = accountBinding is null
            ? await brokerClient.GetAccountSnapshotAsync(cancellationToken)
            : await accountBinding.ValidateAsync(brokerClient, cancellationToken);
        var position = await positions.GetCurrentAsync(account.AccountId, symbol, cancellationToken)
            ?? throw new InvalidOperationException(
                $"No locally owned position exists for {symbol} in account '{account.AccountId}'.");
        var openQuantity = Math.Abs(position.Quantity);
        if (openQuantity <= 0m || submission.Quantity > openQuantity)
        {
            throw new InvalidOperationException(
                $"Exit quantity {submission.Quantity} exceeds locally owned {symbol} quantity {openQuantity}.");
        }

        var intentId = Guid.Empty;
        for (var attempt = 0; attempt <= 100; attempt++)
        {
            var candidateIntentId = PositionExitIntentIdFactory.Create(
                submission.RunContext.RunId,
                account.AccountId,
                symbol,
                position.PositionGenerationEventId,
                submission.Quantity,
                attempt);
            var existing = await intentRepository.GetByIntentIdAsync(
                candidateIntentId,
                cancellationToken);
            if (existing is null)
            {
                intentId = candidateIntentId;
                break;
            }

            if (events is null)
            {
                intentId = candidateIntentId;
                break;
            }

            var existingState = await events.GetCurrentAsync(
                existing.ClientOrderId,
                cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Exit intent {candidateIntentId:N} has no lifecycle state.");
            if (existingState.State == OrderState.Intent)
            {
                intentId = candidateIntentId;
                break;
            }

            if (!OrderStateMachine.IsTerminal(existingState.State) || existingState.State == OrderState.Filled)
            {
                return await dispatcher.DispatchAsync(
                    candidateIntentId,
                    brokerClient,
                    cancellationToken);
            }
        }

        if (intentId == Guid.Empty)
        {
            throw new InvalidOperationException(
                $"Position exit retry history exceeded the supported bound for {symbol}.");
        }
        var side = position.Quantity > 0m ? "sell" : "buy";
        var execution = await ResolveExitExecutionAsync(
            brokerClient,
            symbol,
            side,
            submission.AllowExtendedHoursTrading,
            submission.CreatedAtUtc,
            submission.RequestedOrderType,
            submission.RequestedTimeInForce,
            submission.RequestedLimitPrice,
            submission.RequestedStopPrice,
            cancellationToken);
        var requestJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            symbol,
            side,
            quantity = submission.Quantity,
            execution.OrderType,
            execution.TimeInForce,
            execution.LimitPrice,
            execution.SubmitOutsideRegularHours,
            session = execution.Session.ToString(),
            reason = submission.Reason.Trim(),
            expectedPositionGenerationEventId = position.PositionGenerationEventId,
            expectedPositionGenerationClientOrderId = position.PositionGenerationClientOrderId
        });
        var reservation = await intentRepository.ReserveAsync(
            ToRun(submission.RunContext),
            new OrderIntentReservation(
                intentId,
                OrderIntentKind.PositionExit,
                null,
                account.AccountId,
                position.StrategyId,
                symbol,
                side,
                execution.OrderType,
                execution.TimeInForce,
                submission.Quantity,
                execution.LimitPrice,
                execution.StopPrice,
                ExecutionRunContextFactory.ResolveSessionDate(
                    submission.CreatedAtUtc,
                    "America/New_York"),
                submission.CreatedAtUtc,
                requestJson,
                PositionGenerationEventId: position.PositionGenerationEventId),
            cancellationToken);

        return await PrepareAndDispatchPositionExitAsync(
            reservation.Intent,
            brokerClient,
            cancellationToken);
    }

    public async Task<int> RecoverUnpreparedPositionExitsAsync(
        IBrokerClient brokerClient,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(brokerClient);
        using var commandMutation = brokerMutations.Enter("recover unprepared position exits");
        var account = accountBinding is null
            ? await brokerClient.GetAccountSnapshotAsync(cancellationToken)
            : await accountBinding.ValidateAsync(brokerClient, cancellationToken);
        var pending = await intentRepository.ListUnpreparedPositionExitsAsync(
            account.AccountId,
            1_000,
            cancellationToken);
        var recovered = 0;
        var failed = 0;
        foreach (var intent in pending)
        {
            try
            {
                await PrepareAndDispatchPositionExitAsync(intent, brokerClient, cancellationToken);
                recovered++;
            }
            catch (PositionExitNoLongerRequiredException)
            {
                recovered++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failed++;
                logger.LogError(
                    exception,
                    "Durable position-exit preparation recovery failed. IntentId={IntentId} Symbol={Symbol}.",
                    intent.IntentId,
                    intent.Symbol);
            }
        }

        var remaining = await intentRepository.ListUnpreparedPositionExitsAsync(
            account.AccountId,
            1,
            cancellationToken) ?? [];
        var pendingCount = remaining.Count;
        if (pendingCount > 0 || failed > 0)
        {
            throw new BrokerCommandRecoveryIncompleteException(
                "Position-exit preparation",
                recovered,
                pendingCount,
                failed);
        }

        return recovered;
    }

    private async Task<OrderSubmissionResult> PrepareAndDispatchPositionExitAsync(
        OrderIntentRecord exitIntent,
        IBrokerClient brokerClient,
        CancellationToken cancellationToken)
    {
        if (positions is null || events is null)
        {
            throw new InvalidOperationException(
                "Position ledger and order events are required for durable exit preparation.");
        }

        var position = await positions.GetCurrentAsync(
            exitIntent.AccountId,
            exitIntent.Symbol,
            cancellationToken);
        if (position is null || position.Quantity == 0m ||
            position.PositionGenerationEventId != exitIntent.PositionGenerationEventId)
        {
            await ExpireUnsubmittedExitAsync(
                exitIntent,
                "position_generation_no_longer_open",
                cancellationToken);
            throw new PositionExitNoLongerRequiredException(
                exitIntent.AccountId,
                exitIntent.Symbol);
        }

        admission?.Block(
            AccountReconciliationService.PositionExitHandoffBlockSource,
            "POSITION_EXIT_HANDOFF_ACTIVE",
            $"Durable exit {exitIntent.IntentId:N} owns {exitIntent.Symbol} while its protective stop is exchanged for the closing order.",
            timeProvider.GetUtcNow());

        var canceledProtections = new List<CanceledProtection>();
        try
        {
            await CancelOpenEntryRemainderForExitAsync(position, brokerClient, cancellationToken);
            await CancelOwnedProtectionForExitAsync(
                exitIntent,
                brokerClient,
                canceledProtections,
                cancellationToken);
            var preparedPosition = await positions.GetCurrentAsync(
                exitIntent.AccountId,
                exitIntent.Symbol,
                cancellationToken);
            if (preparedPosition is null ||
                preparedPosition.PositionGenerationEventId != exitIntent.PositionGenerationEventId ||
                preparedPosition.Quantity == 0m)
            {
                await ExpireUnsubmittedExitAsync(
                    exitIntent,
                    "position_flat_after_protection_cancellation",
                    cancellationToken);
                throw new PositionExitNoLongerRequiredException(
                    exitIntent.AccountId,
                    exitIntent.Symbol);
            }

            var brokerPosition = (await brokerClient.GetOpenPositionsAsync(cancellationToken))
                .SingleOrDefault(candidate =>
                    candidate.Ticker.Equals(exitIntent.Symbol, StringComparison.OrdinalIgnoreCase));
            var brokerQuantity = ToSignedQuantity(brokerPosition);
            if (brokerQuantity is null || brokerQuantity.Value != preparedPosition.Quantity)
            {
                await ExpireUnsubmittedExitAsync(
                    exitIntent,
                    "broker_local_position_mismatch",
                    cancellationToken);
                throw new PositionExitReconciliationRequiredException(
                    exitIntent.AccountId,
                    exitIntent.Symbol,
                    preparedPosition.Quantity,
                    brokerQuantity);
            }

            if (exitIntent.RequestedQuantity > Math.Abs(preparedPosition.Quantity))
            {
                await ExpireUnsubmittedExitAsync(
                    exitIntent,
                    "position_quantity_changed_during_protection_cancellation",
                    cancellationToken);
                throw new PositionExitReconciliationRequiredException(
                    exitIntent.AccountId,
                    exitIntent.Symbol,
                    preparedPosition.Quantity,
                    brokerQuantity);
            }

            var currentExit = await events!.GetCurrentAsync(
                exitIntent.ClientOrderId,
                cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Exit intent {exitIntent.IntentId:N} has no lifecycle state.");
            if (currentExit.State == OrderState.Intent)
            {
                await events.TransitionAsync(
                    new OrderTransitionRequest(
                        exitIntent.ClientOrderId,
                        OrderState.Intent,
                        OrderState.Submitted,
                        Source: "engine",
                        LocalTimestampUtc: timeProvider.GetUtcNow().ToUniversalTime(),
                        PayloadJson: JsonSerializer.Serialize(new
                        {
                            action = "position_exit_prepared",
                            exitIntent.AccountId,
                            symbol = exitIntent.Symbol,
                            positionGenerationEventId = preparedPosition.PositionGenerationEventId,
                            quantity = exitIntent.RequestedQuantity
                        })),
                    cancellationToken);
            }

            return await dispatcher.DispatchAsync(
                exitIntent.IntentId,
                brokerClient,
                cancellationToken);
        }
        catch (Exception exitFailure)
        {
            try
            {
                await RestoreCanceledProtectionAfterFailedExitAsync(
                    exitIntent,
                    canceledProtections,
                    brokerClient);
            }
            catch (Exception restoreFailure)
            {
                var blockSource = $"position_exit_protection_restore:{exitIntent.AccountId}:{exitIntent.Symbol}";
                admission?.Block(
                    blockSource,
                    "POSITION_EXIT_PROTECTION_RESTORE_FAILED",
                    $"Exit {exitIntent.IntentId:N} failed and protection restoration is incomplete: {restoreFailure.Message}",
                    timeProvider.GetUtcNow());
                logger.LogCritical(
                    restoreFailure,
                    "Exit failed after protection handoff and protection restoration failed. IntentId={IntentId} AccountId={AccountId} Symbol={Symbol} ExitFailure={ExitFailure}.",
                    exitIntent.IntentId,
                    exitIntent.AccountId,
                    exitIntent.Symbol,
                    exitFailure.Message);
                throw new PositionExitProtectionRestoreFailedException(
                    exitIntent.AccountId,
                    exitIntent.Symbol,
                    exitFailure,
                    restoreFailure);
            }

            throw;
        }
    }

    private async Task CancelOpenEntryRemainderForExitAsync(
        PositionLedgerSnapshot position,
        IBrokerClient brokerClient,
        CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(position.PositionGenerationClientOrderId))
        {
            return;
        }

        var entryIntent = await intentRepository.GetByClientOrderIdAsync(
            position.PositionGenerationClientOrderId,
            cancellationToken);
        if (entryIntent?.Kind is not (OrderIntentKind.StrategyEntry or OrderIntentKind.OperatorEntry))
        {
            return;
        }

        var state = await events!.GetCurrentAsync(entryIntent.ClientOrderId, cancellationToken);
        if (state is null || OrderStateMachine.IsTerminal(state.State))
        {
            return;
        }

        var openOrder = (await brokerClient.GetOpenOrdersAsync(cancellationToken))
            .SingleOrDefault(order =>
                (order.ParentClientOrderId ?? order.ClientOrderId)
                    .Equals(entryIntent.ClientOrderId, StringComparison.Ordinal));
        if (openOrder is null)
        {
            throw new OrderCancellationPendingException(
                entryIntent.ClientOrderId,
                state.BrokerOrderId ?? "broker_order_not_visible");
        }

        var canceled = await RequestCancelAsync(
            new OrderCancellationSubmission(
                entryIntent.ClientOrderId,
                openOrder.OrderId,
                "position_exit_cancel_entry_remainder",
                timeProvider.GetUtcNow().ToUniversalTime(),
                openOrder.ClientOrderId),
            brokerClient,
            cancellationToken);
        if (!OrderStateMachine.IsTerminal(canceled.State))
        {
            throw new OrderCancellationPendingException(
                entryIntent.ClientOrderId,
                openOrder.OrderId);
        }
    }

    private async Task CancelOwnedProtectionForExitAsync(
        OrderIntentRecord exitIntent,
        IBrokerClient brokerClient,
        ICollection<CanceledProtection> canceledProtections,
        CancellationToken cancellationToken)
    {
        var positionGenerationEventId = exitIntent.PositionGenerationEventId
            ?? throw new InvalidOperationException("Position exit has no generation identity.");
        var active = await intentRepository.ListActiveProtectiveForSymbolAsync(
            exitIntent.AccountId,
            exitIntent.Symbol,
            cancellationToken);
        foreach (var candidate in active)
        {
            var protectiveIntent = await intentRepository.GetByClientOrderIdAsync(
                candidate.ClientOrderId,
                cancellationToken);
            if (protectiveIntent is null ||
                protectiveIntent.Kind != OrderIntentKind.ProtectiveStop ||
                protectiveIntent.PositionGenerationEventId != positionGenerationEventId ||
                !protectiveIntent.Side.Equals(exitIntent.Side, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var protection = ParseCanceledProtection(protectiveIntent);
            if (!canceledProtections.Any(item => item.Intent.IntentId == protectiveIntent.IntentId))
            {
                canceledProtections.Add(protection);
            }

            await CancelOwnedProtectiveIntentAsync(
                protectiveIntent,
                brokerClient,
                cancellationToken);
        }
    }

    private async Task RestoreCanceledProtectionAfterFailedExitAsync(
        OrderIntentRecord exitIntent,
        IReadOnlyList<CanceledProtection> canceledProtections,
        IBrokerClient brokerClient)
    {
        if (canceledProtections.Count == 0)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(cancellationConfirmationTimeout);
        var token = timeout.Token;
        var brokerPositions = await brokerClient.GetOpenPositionsAsync(token) ?? [];
        var brokerPosition = brokerPositions.SingleOrDefault(candidate =>
            candidate.Ticker.Equals(exitIntent.Symbol, StringComparison.OrdinalIgnoreCase));
        if (brokerPosition is null || brokerPosition.Qty == 0m)
        {
            return;
        }

        var disposition = await ResolveExitHandoffDispositionAsync(
            exitIntent,
            brokerClient,
            token);
        if (disposition == ExitHandoffDisposition.ExitActiveOrFilled)
        {
            return;
        }

        if (disposition == ExitHandoffDisposition.Ambiguous)
        {
            throw new InvalidOperationException(
                $"Broker ownership of exit '{exitIntent.ClientOrderId}' is ambiguous; restoring a stop could create a reversing order.");
        }

        await ExpireExitBeforeProtectionRestoreAsync(exitIntent, token);
        brokerPositions = await brokerClient.GetOpenPositionsAsync(token) ?? [];
        brokerPosition = brokerPositions.SingleOrDefault(candidate =>
            candidate.Ticker.Equals(exitIntent.Symbol, StringComparison.OrdinalIgnoreCase));
        if (brokerPosition is null || brokerPosition.Qty == 0m)
        {
            return;
        }

        var existingOrders = await brokerClient.GetOpenOrdersAsync(token) ?? [];
        if (existingOrders.Any(order => canceledProtections.Any(protection =>
                (order.ParentClientOrderId ?? order.ClientOrderId).Equals(
                    protection.Intent.ClientOrderId,
                    StringComparison.Ordinal) &&
                !OrderStateMachine.IsTerminal(BrokerOrderStateProjection.Project(
                    OrderStatusCodec.ParseBrokerValue(order.Status))))))
        {
            return;
        }

        var localPosition = await positions!.GetCurrentAsync(
            exitIntent.AccountId,
            exitIntent.Symbol,
            token) ?? throw new InvalidOperationException(
                $"Local position {exitIntent.Symbol} disappeared during protection restoration.");
        var brokerQuantity = ToSignedQuantity(brokerPosition);
        if (brokerQuantity is null ||
            Math.Sign(brokerQuantity.Value) != Math.Sign(localPosition.Quantity) ||
            localPosition.PositionGenerationEventId != exitIntent.PositionGenerationEventId)
        {
            throw new PositionExitReconciliationRequiredException(
                exitIntent.AccountId,
                exitIntent.Symbol,
                localPosition.Quantity,
                brokerQuantity);
        }

        var side = localPosition.Quantity > 0m ? "sell" : "buy";
        var matching = canceledProtections
            .Where(item =>
                item.Intent.PositionGenerationEventId == localPosition.PositionGenerationEventId &&
                item.Intent.Side.Equals(side, StringComparison.OrdinalIgnoreCase) &&
                item.Intent.StopPrice is > 0m)
            .ToArray();
        if (matching.Length == 0)
        {
            throw new InvalidOperationException(
                $"No canceled stop contract matches the current {exitIntent.Symbol} position generation.");
        }

        var stopPrice = side == "sell"
            ? matching.Max(item => item.Intent.StopPrice!.Value)
            : matching.Min(item => item.Intent.StopPrice!.Value);
        var positionGenerationIdentity = matching[0].PositionGenerationIdentity;
        var revision = await ResolveNextProtectionRevisionAsync(
            exitIntent.AccountId,
            exitIntent.Symbol,
            side,
            positionGenerationIdentity,
            matching.Max(item => item.ProtectionRevision) + 1,
            token);
        var intentId = ProtectiveOrderIntentIdFactory.Create(
            exitIntent.AccountId,
            exitIntent.Symbol,
            side,
            positionGenerationIdentity,
            revision);
        var run = await intentRepository.GetRunAsync(exitIntent.RunId, token)
            ?? throw new InvalidOperationException(
                $"Owning run {exitIntent.RunId:N} is unavailable during protection restoration.");
        await SubmitProtectiveStopCoreAsync(
            new ProtectiveStopSubmission(
                intentId,
                new ExecutionRunContext(
                    run.RunId,
                    run.Profile,
                    run.ConfigHash,
                    run.CodeVersion,
                    run.StartedAtUtc),
                exitIntent.AccountId,
                exitIntent.Symbol,
                side,
                Math.Abs(brokerQuantity.Value),
                stopPrice,
                exitIntent.SessionDate,
                timeProvider.GetUtcNow().ToUniversalTime(),
                positionGenerationIdentity,
                revision),
            brokerClient,
            token);

        logger.LogWarning(
            "Restored successor protection after failed exit handoff. ExitIntentId={ExitIntentId} ProtectionIntentId={ProtectionIntentId} Symbol={Symbol} Quantity={Quantity} StopPrice={StopPrice}.",
            exitIntent.IntentId,
            intentId,
            exitIntent.Symbol,
            Math.Abs(brokerQuantity.Value),
            stopPrice);
    }

    private async Task<ExitHandoffDisposition> ResolveExitHandoffDispositionAsync(
        OrderIntentRecord exitIntent,
        IBrokerClient brokerClient,
        CancellationToken cancellationToken)
    {
        var persistedIntent = await intentRepository.GetByIntentIdAsync(
            exitIntent.IntentId,
            cancellationToken) ?? throw new InvalidOperationException(
                $"Exit intent {exitIntent.IntentId:N} disappeared during protection restoration.");
        var brokerOrder = await brokerClient.GetOrderByClientOrderIdAsync(
            persistedIntent.ClientOrderId,
            cancellationToken);
        if (brokerOrder is not null)
        {
            var brokerState = BrokerOrderStateProjection.Project(
                OrderStatusCodec.ParseBrokerValue(brokerOrder.Status));
            if (brokerState == OrderState.Filled || !OrderStateMachine.IsTerminal(brokerState))
            {
                return ExitHandoffDisposition.ExitActiveOrFilled;
            }

            await ApplyBrokerOrderUpdateWithFillRepairAsync(
                ProjectBrokerOrderToOwner(brokerOrder, persistedIntent.ClientOrderId),
                cancellationToken);
            return ExitHandoffDisposition.SafeToRestore;
        }

        var localState = await events!.GetCurrentAsync(persistedIntent.ClientOrderId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Exit intent {exitIntent.IntentId:N} has no lifecycle state during protection restoration.");
        if (localState.State == OrderState.Filled)
        {
            return ExitHandoffDisposition.ExitActiveOrFilled;
        }

        if (localState.State is OrderState.Rejected or OrderState.Canceled or OrderState.Expired)
        {
            return ExitHandoffDisposition.SafeToRestore;
        }

        if (persistedIntent.DispatchAttemptCount == 0 &&
            localState.State is OrderState.Intent or OrderState.Submitted)
        {
            return ExitHandoffDisposition.SafeToRestore;
        }

        return ExitHandoffDisposition.Ambiguous;
    }

    private async Task ExpireExitBeforeProtectionRestoreAsync(
        OrderIntentRecord exitIntent,
        CancellationToken cancellationToken)
    {
        var expired = await intentRepository.TryExpireUnattemptedUnleasedPositionExitAsync(
            exitIntent.IntentId,
            "exit_handoff_failed_before_broker_ownership",
            timeProvider.GetUtcNow().ToUniversalTime(),
            cancellationToken);
        if (!expired)
        {
            throw new InvalidOperationException(
                $"Exit intent {exitIntent.IntentId:N} acquired dispatch ownership before protection could be restored.");
        }
    }

    private async Task<int> ResolveNextProtectionRevisionAsync(
        string accountId,
        string symbol,
        string side,
        string positionGenerationIdentity,
        int startingRevision,
        CancellationToken cancellationToken)
    {
        for (var revision = Math.Max(0, startingRevision); revision < 10_000; revision++)
        {
            var intentId = ProtectiveOrderIntentIdFactory.Create(
                accountId,
                symbol,
                side,
                positionGenerationIdentity,
                revision);
            if (await intentRepository.GetByIntentIdAsync(intentId, cancellationToken) is null)
            {
                return revision;
            }
        }

        throw new InvalidOperationException(
            $"Protective restoration history exceeded the supported bound for {symbol}.");
    }

    private static CanceledProtection ParseCanceledProtection(OrderIntentRecord intent)
    {
        using var document = JsonDocument.Parse(intent.RequestJson);
        var root = document.RootElement;
        if (!TryGetJsonString(root, "PositionGenerationIdentity", out var identity) ||
            String.IsNullOrWhiteSpace(identity))
        {
            throw new InvalidOperationException(
                $"Protective intent {intent.IntentId:N} has no position-generation identity.");
        }

        if (!TryGetJsonInt32(root, "ProtectionRevision", out var revision) || revision < 0)
        {
            throw new InvalidOperationException(
                $"Protective intent {intent.IntentId:N} has no valid protection revision.");
        }

        return new CanceledProtection(intent, identity!, revision);
    }

    private static bool TryGetJsonString(
        JsonElement root,
        string name,
        out string? value)
    {
        if (root.TryGetProperty(name, out var property) ||
            root.TryGetProperty(JsonNamingPolicy.CamelCase.ConvertName(name), out property))
        {
            value = property.ValueKind == JsonValueKind.String ? property.GetString() : null;
            return value is not null;
        }

        value = null;
        return false;
    }

    private static bool TryGetJsonInt32(
        JsonElement root,
        string name,
        out int value)
    {
        if ((root.TryGetProperty(name, out var property) ||
             root.TryGetProperty(JsonNamingPolicy.CamelCase.ConvertName(name), out property)) &&
            property.TryGetInt32(out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static decimal? ToSignedQuantity(BrokerPosition? brokerPosition) =>
        brokerPosition is null
            ? null
            : brokerPosition.Side.Equals("short", StringComparison.OrdinalIgnoreCase)
                ? -Math.Abs(brokerPosition.Qty)
                : Math.Abs(brokerPosition.Qty);

    private async Task CancelOwnedProtectiveIntentAsync(
        OrderIntentRecord protectiveIntent,
        IBrokerClient brokerClient,
        CancellationToken cancellationToken)
    {
        var current = await events!.GetCurrentAsync(
            protectiveIntent.ClientOrderId,
            cancellationToken)
            ?? throw new InvalidOperationException(
                $"Protective intent {protectiveIntent.IntentId:N} has no lifecycle state.");
        if (OrderStateMachine.IsTerminal(current.State))
        {
            return;
        }

        if (current.State == OrderState.Intent ||
            current.State == OrderState.Submitted &&
            protectiveIntent.DispatchAttemptCount == 0 &&
            String.IsNullOrWhiteSpace(current.BrokerOrderId))
        {
            try
            {
                await events.TransitionAsync(
                    new OrderTransitionRequest(
                        protectiveIntent.ClientOrderId,
                        current.State,
                        OrderState.Expired,
                        Source: "engine",
                        LocalTimestampUtc: timeProvider.GetUtcNow().ToUniversalTime(),
                        PayloadJson: JsonSerializer.Serialize(new
                        {
                            action = "protective_intent_expired",
                            reason = "position_exit_in_progress",
                            protectiveIntent.PositionGenerationEventId
                        })),
                    cancellationToken);
                return;
            }
            catch (InvalidOperationException)
            {
                var latest = await events.GetCurrentAsync(
                    protectiveIntent.ClientOrderId,
                    cancellationToken);
                if (latest is null)
                {
                    throw;
                }

                current = latest;
                if (OrderStateMachine.IsTerminal(current.State))
                {
                    return;
                }
            }
        }

        var brokerOrderId = current.BrokerOrderId;
        var brokerClientOrderId = String.IsNullOrWhiteSpace(brokerOrderId)
            ? protectiveIntent.ClientOrderId
            : await ResolveCancellationBrokerClientOrderIdAsync(
                protectiveIntent,
                brokerOrderId,
                requestedBrokerClientOrderId: null,
                cancellationToken);
        var brokerOrder = await brokerClient.GetOrderByClientOrderIdAsync(
            brokerClientOrderId,
            cancellationToken)
            ?? throw new InvalidOperationException(
                $"Protective order '{protectiveIntent.ClientOrderId}' is not visible at the broker; exit remains blocked.");
        brokerOrderId ??= brokerOrder.OrderId;
        await RequireCancellationOwnershipAsync(
            protectiveIntent,
            brokerOrderId,
            brokerOrder,
            cancellationToken);
        if (current.State == OrderState.Submitted)
        {
            current = await ApplyBrokerOrderUpdateWithFillRepairAsync(
                ProjectBrokerOrderToOwner(brokerOrder, protectiveIntent.ClientOrderId),
                cancellationToken);
            if (OrderStateMachine.IsTerminal(current.State))
            {
                return;
            }
        }

        var cancellation = await RequestCancelAsync(
            new OrderCancellationSubmission(
                protectiveIntent.ClientOrderId,
                brokerOrderId,
                "cancel_owned_protection_before_position_exit",
                timeProvider.GetUtcNow().ToUniversalTime(),
                brokerOrder.ClientOrderId),
            brokerClient,
            cancellationToken);
        if (!OrderStateMachine.IsTerminal(cancellation.State))
        {
            throw new OrderCancellationPendingException(
                protectiveIntent.ClientOrderId,
                brokerOrderId);
        }
    }

    private async Task ExpireUnsubmittedExitAsync(
        OrderIntentRecord exitIntent,
        string reason,
        CancellationToken cancellationToken)
    {
        var current = await events!.GetCurrentAsync(exitIntent.ClientOrderId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Exit intent {exitIntent.IntentId:N} has no lifecycle state.");
        if (current.State == OrderState.Expired)
        {
            return;
        }

        if (current.State != OrderState.Intent)
        {
            throw new InvalidOperationException(
                $"Exit intent {exitIntent.IntentId:N} cannot be expired from {current.State.ToStorageValue()}.");
        }

        await events.TransitionAsync(
            new OrderTransitionRequest(
                exitIntent.ClientOrderId,
                OrderState.Intent,
                OrderState.Expired,
                Source: "engine",
                LocalTimestampUtc: timeProvider.GetUtcNow().ToUniversalTime(),
                PayloadJson: JsonSerializer.Serialize(new { action = "position_exit_expired", reason })),
            cancellationToken);
    }

    public async Task<OrderStateSnapshot> RequestCancelAsync(
        OrderCancellationSubmission submission,
        IBrokerClient brokerClient,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(brokerClient);
        using var commandMutation = brokerMutations.Enter(
            $"durable cancellation {submission.ClientOrderId}");
        if (events is null || lifecycle is null)
        {
            throw new InvalidOperationException(
                "Order events and lifecycle are required for durable cancellation.");
        }

        if (String.IsNullOrWhiteSpace(submission.ClientOrderId) ||
            String.IsNullOrWhiteSpace(submission.BrokerOrderId) ||
            String.IsNullOrWhiteSpace(submission.Reason) ||
            submission.RequestedAtUtc == default ||
            submission.RequestedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "Cancellation requires client and broker order identity, reason, and a UTC timestamp.");
        }

        var intent = await intentRepository.GetByClientOrderIdAsync(
            submission.ClientOrderId.Trim(),
            cancellationToken)
            ?? throw new InvalidOperationException(
                $"Cannot cancel unowned client order '{submission.ClientOrderId}'.");
        await ValidateBrokerAccountAsync(intent.AccountId, brokerClient, cancellationToken);
        var brokerClientOrderId = await ResolveCancellationBrokerClientOrderIdAsync(
            intent,
            submission.BrokerOrderId.Trim(),
            submission.BrokerClientOrderId,
            cancellationToken);
        var brokerOrder = await brokerClient.GetOrderByClientOrderIdAsync(
            brokerClientOrderId,
            cancellationToken);
        await RequireCancellationOwnershipAsync(
            intent,
            submission.BrokerOrderId.Trim(),
            brokerOrder,
            cancellationToken);
        if (OrderStateMachine.IsTerminal(BrokerOrderStateProjection.Project(
                OrderStatusCodec.ParseBrokerValue(brokerOrder!.Status))))
        {
            return await ApplyBrokerOrderUpdateWithFillRepairAsync(
                ProjectBrokerOrderToOwner(brokerOrder, intent.ClientOrderId),
                cancellationToken);
        }

        var pending = await lifecycle.RequestCancelAsync(
            intent.ClientOrderId,
            submission.BrokerOrderId.Trim(),
            cancellationToken);
        await SendOrVerifyCancellationAsync(
            intent,
            pending,
            brokerClientOrderId,
            brokerClient,
            cancellationToken);
        logger.LogInformation(
            "Cancellation requested for owned order {ClientOrderId} BrokerOrderId={BrokerOrderId} Reason={Reason}.",
            intent.ClientOrderId,
            submission.BrokerOrderId,
            submission.Reason.Trim());
        return await events!.GetCurrentAsync(intent.ClientOrderId, cancellationToken) ?? pending;
    }

    public async Task<ActiveBrokerOrder> ReplaceProtectiveStopAsync(
        ProtectiveStopReplacementSubmission submission,
        IBrokerClient brokerClient,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(brokerClient);
        using var commandMutation = brokerMutations.Enter(
            $"durable protective replacement {submission.OwnerClientOrderId}");
        if (String.IsNullOrWhiteSpace(submission.OwnerClientOrderId) ||
            String.IsNullOrWhiteSpace(submission.BrokerOrderId) ||
            String.IsNullOrWhiteSpace(submission.Symbol) ||
            String.IsNullOrWhiteSpace(submission.Reason) ||
            submission.StopPrice <= 0m ||
            submission.RequestedAtUtc == default ||
            submission.RequestedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "Protective replacement requires owner, broker order, symbol, stop, reason, and UTC time.");
        }

        if (stopReplacements is null)
        {
            throw new InvalidOperationException(
                "Protective-stop replacement repository is required before broker mutation.");
        }

        var owner = await intentRepository.GetByClientOrderIdAsync(
            submission.OwnerClientOrderId.Trim(),
            cancellationToken)
            ?? throw new InvalidOperationException(
                $"Cannot replace protection for unowned client order '{submission.OwnerClientOrderId}'.");
        var symbol = submission.Symbol.Trim().ToUpperInvariant();
        if (!owner.Symbol.Equals(symbol, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Protective order symbol {symbol} does not match owner {owner.Symbol}.");
        }

        await ValidateBrokerAccountAsync(owner.AccountId, brokerClient, cancellationToken);
        var brokerOrderId = submission.BrokerOrderId.Trim();
        var commandId = ProtectiveStopReplacementIdFactory.Create(
            owner.AccountId,
            owner.ClientOrderId,
            brokerOrderId,
            symbol,
            submission.StopPrice);
        var replacementClientOrderId =
            ProtectiveStopReplacementIdFactory.CreateReplacementClientOrderId(commandId);
        var command = await stopReplacements.ReserveAsync(
            new ProtectiveStopReplacementReservation(
                commandId,
                owner.AccountId,
                owner.ClientOrderId,
                brokerOrderId,
                replacementClientOrderId,
                symbol,
                submission.StopPrice,
                submission.Reason.Trim(),
                submission.RequestedAtUtc,
                owner.RunId,
                owner.SchemaVersion,
                owner.ConfigHash,
                owner.CodeVersion),
            cancellationToken);
        if (command.State == ProtectiveStopReplacementState.Verified)
        {
            return await ResolveVerifiedProtectiveReplacementAsync(
                command,
                owner,
                brokerClient,
                cancellationToken);
        }

        var lease = await stopReplacements.TryAcquireLeaseAsync(
            command.CommandId,
            owner.AccountId,
            replacementLeaseOwner,
            replacementLeaseDuration,
            cancellationToken)
            ?? throw new InvalidOperationException(
                $"Protective-stop command {command.CommandId:N} is already owned by another dispatcher.");
        try
        {
            return await ExecuteProtectiveStopReplacementAsync(
                lease,
                owner,
                brokerClient,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await stopReplacements.ReleaseLeaseAsync(
                lease.Command.CommandId,
                lease.LeaseToken,
                exception.Message,
                CancellationToken.None);
            throw;
        }
    }

    public async Task<int> RecoverPendingProtectiveStopReplacementsAsync(
        IBrokerClient brokerClient,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(brokerClient);
        using var commandMutation = brokerMutations.Enter(
            "recover pending protective replacements");
        if (stopReplacements is null)
        {
            throw new InvalidOperationException(
                "Protective-stop replacement repository is required for recovery.");
        }

        var account = accountBinding is null
            ? await brokerClient.GetAccountSnapshotAsync(cancellationToken)
            : await accountBinding.ValidateAsync(brokerClient, cancellationToken);
        var commandIds = await stopReplacements.ListRecoverableCommandIdsAsync(
            account.AccountId,
            100,
            cancellationToken);
        var recovered = 0;
        var pendingCount = 0;
        var failed = 0;
        foreach (var commandId in commandIds)
        {
            var lease = await stopReplacements.TryAcquireLeaseAsync(
                commandId,
                account.AccountId,
                replacementLeaseOwner,
                replacementLeaseDuration,
                cancellationToken);
            if (lease is null)
            {
                pendingCount++;
                continue;
            }

            try
            {
                var owner = await intentRepository.GetByClientOrderIdAsync(
                    lease.Command.OwnerClientOrderId,
                    cancellationToken)
                    ?? throw new InvalidOperationException(
                        $"Protective-stop command {commandId:N} has no owning order intent.");
                await ExecuteProtectiveStopReplacementAsync(
                    lease,
                    owner,
                    brokerClient,
                    cancellationToken);
                recovered++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failed++;
                await stopReplacements.ReleaseLeaseAsync(
                    lease.Command.CommandId,
                    lease.LeaseToken,
                    exception.Message,
                    CancellationToken.None);
                logger.LogError(
                    exception,
                    "Protective-stop replacement recovery failed for {CommandId}; remaining commands will still be processed.",
                    commandId);
            }
        }

        var remaining = await stopReplacements.ListRecoverableCommandIdsAsync(
            account.AccountId,
            1,
            cancellationToken) ?? [];
        pendingCount = Math.Max(pendingCount, remaining.Count);
        if (pendingCount > 0 || failed > 0)
        {
            throw new BrokerCommandRecoveryIncompleteException(
                "Protective-stop replacement",
                recovered,
                pendingCount,
                failed);
        }

        return recovered;
    }

    public async Task<int> RecoverPendingCancellationsAsync(
        IBrokerClient brokerClient,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(brokerClient);
        using var commandMutation = brokerMutations.Enter("recover pending cancellations");
        if (events is null || lifecycle is null)
        {
            throw new InvalidOperationException("Order events and lifecycle are required for cancellation recovery.");
        }

        var account = accountBinding is null
            ? await brokerClient.GetAccountSnapshotAsync(cancellationToken)
            : await accountBinding.ValidateAsync(brokerClient, cancellationToken);
        var pending = (await events.ListReconcilableAsync(account.AccountId, cancellationToken))
            .Where(snapshot =>
                snapshot.State == OrderState.CancelPending &&
                !String.IsNullOrWhiteSpace(snapshot.BrokerOrderId))
            .ToArray();
        var recovered = 0;
        var failed = 0;
        foreach (var snapshot in pending)
        {
            try
            {
                var intent = await intentRepository.GetByClientOrderIdAsync(
                    snapshot.ClientOrderId,
                    cancellationToken)
                    ?? throw new InvalidOperationException(
                        $"Cancellation recovery found no owner for '{snapshot.ClientOrderId}'.");
                if (!intent.AccountId.Equals(account.AccountId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Cancellation owner '{snapshot.ClientOrderId}' belongs to another account.");
                }

                var brokerClientOrderId = await ResolveCancellationBrokerClientOrderIdAsync(
                    intent,
                    snapshot.BrokerOrderId!,
                    requestedBrokerClientOrderId: null,
                    cancellationToken);
                await SendOrVerifyCancellationAsync(
                    intent,
                    snapshot,
                    brokerClientOrderId,
                    brokerClient,
                    cancellationToken);
                recovered++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failed++;
                logger.LogError(
                    exception,
                    "Cancellation recovery failed for owned order {ClientOrderId}; remaining orders will still be processed.",
                    snapshot.ClientOrderId);
            }
        }

        if (failed > 0)
        {
            throw new BrokerCommandRecoveryIncompleteException(
                "Order cancellation",
                recovered,
                pendingCount: 0,
                failed);
        }

        return recovered;
    }

    private async Task SendOrVerifyCancellationAsync(
        OrderIntentRecord intent,
        OrderStateSnapshot pending,
        string brokerClientOrderId,
        IBrokerClient brokerClient,
        CancellationToken cancellationToken)
    {
        if (events is null || lifecycle is null || String.IsNullOrWhiteSpace(pending.BrokerOrderId))
        {
            throw new InvalidOperationException("Durable cancellation state is incomplete.");
        }

        using var brokerMutation = brokerMutations.Enter(
            $"cancel {intent.ClientOrderId}");
        await brokerClient.CancelOrderAsync(pending.BrokerOrderId, cancellationToken);
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        do
        {
            var brokerOrder = await brokerClient.GetOrderByClientOrderIdAsync(
                brokerClientOrderId,
                cancellationToken);
            if (brokerOrder is not null)
            {
                await RequireCancellationOwnershipAsync(
                    intent,
                    pending.BrokerOrderId,
                    brokerOrder,
                    cancellationToken);
                var resolved = await ApplyBrokerOrderUpdateWithFillRepairAsync(
                    ProjectBrokerOrderToOwner(brokerOrder, intent.ClientOrderId),
                    cancellationToken);
                if (OrderStateMachine.IsTerminal(resolved.State))
                {
                    return;
                }
            }

            if (System.Diagnostics.Stopwatch.GetElapsedTime(startedAt) >= cancellationConfirmationTimeout)
            {
                break;
            }

            await Task.Delay(cancellationPollInterval, cancellationToken);
        }
        while (true);

        throw new OrderCancellationPendingException(intent.ClientOrderId, pending.BrokerOrderId);
    }

    private Task<OrderStateSnapshot> ApplyBrokerOrderUpdateWithFillRepairAsync(
        ActiveBrokerOrder brokerOrder,
        CancellationToken cancellationToken)
    {
        if (lifecycle is null)
        {
            throw new InvalidOperationException("Order lifecycle is required for broker reconciliation.");
        }

        var update = BrokerOrderUpdateFactory.Create(brokerOrder);
        if (brokerOrder.FilledQuantity <= 0m)
        {
            return lifecycle.ApplyBrokerUpdateAsync(update, cancellationToken);
        }

        var averageFillPrice = brokerOrder.FilledAveragePrice
            ?? throw new InvalidOperationException(
                $"Broker order {brokerOrder.OrderId} reports fills without filled_avg_price.");
        return lifecycle.ApplyBrokerUpdateWithFillAsync(
            update,
            new OrderFillProjection(
                brokerOrder.Ticker,
                brokerOrder.Side,
                brokerOrder.FilledQuantity,
                averageFillPrice,
                $"rest:{brokerOrder.OrderId}:{brokerOrder.FilledQuantity.ToString(System.Globalization.CultureInfo.InvariantCulture)}"),
            cancellationToken);
    }

    private async Task<string> ResolveCancellationBrokerClientOrderIdAsync(
        OrderIntentRecord intent,
        string brokerOrderId,
        string? requestedBrokerClientOrderId,
        CancellationToken cancellationToken)
    {
        var requested = requestedBrokerClientOrderId?.Trim();
        if (String.IsNullOrWhiteSpace(requested) || requested.Equals(intent.ClientOrderId, StringComparison.Ordinal))
        {
            if (stopReplacements is null)
            {
                return intent.ClientOrderId;
            }

            var replacement = await stopReplacements.GetVerifiedBySuccessorBrokerOrderIdAsync(
                intent.AccountId,
                brokerOrderId,
                cancellationToken);
            return replacement?.ReplacementClientOrderId ?? intent.ClientOrderId;
        }

        if (stopReplacements is not null)
        {
            var replacement = await stopReplacements.GetVerifiedBySuccessorBrokerOrderIdAsync(
                intent.AccountId,
                brokerOrderId,
                cancellationToken);
            if (replacement is not null &&
                replacement.OwnerClientOrderId.Equals(intent.ClientOrderId, StringComparison.Ordinal) &&
                replacement.ReplacementClientOrderId.Equals(requested, StringComparison.Ordinal))
            {
                return requested;
            }
        }

        return requested;
    }

    private async Task RequireCancellationOwnershipAsync(
        OrderIntentRecord intent,
        string expectedBrokerOrderId,
        ActiveBrokerOrder? brokerOrder,
        CancellationToken cancellationToken)
    {
        var directlyOwned = brokerOrder is not null &&
            (brokerOrder.ClientOrderId.Equals(intent.ClientOrderId, StringComparison.Ordinal) ||
             String.Equals(brokerOrder.ParentClientOrderId, intent.ClientOrderId, StringComparison.Ordinal));
        var replacement = directlyOwned || brokerOrder is null || stopReplacements is null
            ? null
            : await stopReplacements.GetVerifiedBySuccessorBrokerOrderIdAsync(
                intent.AccountId,
                expectedBrokerOrderId,
                cancellationToken);
        var replacementOwned = replacement is not null &&
            replacement.OwnerClientOrderId.Equals(intent.ClientOrderId, StringComparison.Ordinal) &&
            replacement.ReplacementClientOrderId.Equals(brokerOrder!.ClientOrderId, StringComparison.Ordinal);
        if (brokerOrder is null ||
            !brokerOrder.OrderId.Equals(expectedBrokerOrderId, StringComparison.Ordinal) ||
            !(directlyOwned || replacementOwned) ||
            !brokerOrder.Ticker.Equals(intent.Symbol, StringComparison.OrdinalIgnoreCase) ||
            !brokerOrder.Side.Equals(intent.Side, StringComparison.OrdinalIgnoreCase) ||
            brokerOrder.Qty != intent.RequestedQuantity ||
            !brokerOrder.OrderType.Equals(intent.OrderType, StringComparison.OrdinalIgnoreCase) ||
            String.IsNullOrWhiteSpace(brokerOrder.TimeInForce) ||
            !brokerOrder.TimeInForce.Equals(intent.TimeInForce, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Broker order '{expectedBrokerOrderId}' is not owned by cancellation client order '{intent.ClientOrderId}'.");
        }
    }

    private static ActiveBrokerOrder ProjectBrokerOrderToOwner(
        ActiveBrokerOrder brokerOrder,
        string ownerClientOrderId) =>
        brokerOrder.ClientOrderId.Equals(ownerClientOrderId, StringComparison.Ordinal)
            ? brokerOrder
            : brokerOrder with { ClientOrderId = ownerClientOrderId };

    private async Task ValidateBrokerAccountAsync(
        string expectedIntentAccountId,
        IBrokerClient brokerClient,
        CancellationToken cancellationToken)
    {
        var account = accountBinding is null
            ? await brokerClient.GetAccountSnapshotAsync(cancellationToken)
            : await accountBinding.ValidateAsync(brokerClient, cancellationToken);
        if (!account.AccountId.Equals(expectedIntentAccountId, StringComparison.Ordinal))
        {
            throw new BrokerAccountMismatchException(expectedIntentAccountId, account.AccountId);
        }
    }

    private static long? ParsePositionGenerationEventId(string identity)
    {
        const string prefix = "ledger:";
        if (!identity.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var delimiter = identity.IndexOf(':', prefix.Length);
        if (delimiter <= prefix.Length ||
            !Int64.TryParse(identity.AsSpan(prefix.Length, delimiter - prefix.Length), out var eventId) ||
            eventId <= 0)
        {
            throw new InvalidOperationException(
                $"Position generation identity '{identity}' is invalid.");
        }

        return eventId;
    }

    private async Task<ActiveBrokerOrder> ExecuteProtectiveStopReplacementAsync(
        ProtectiveStopReplacementLease lease,
        OrderIntentRecord owner,
        IBrokerClient brokerClient,
        CancellationToken cancellationToken)
    {
        var command = lease.Command;
        if (!owner.AccountId.Equals(command.AccountId, StringComparison.Ordinal) ||
            !owner.ClientOrderId.Equals(command.OwnerClientOrderId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Protective-stop command {command.CommandId:N} no longer matches its owning intent.");
        }

        await ValidateBrokerAccountAsync(command.AccountId, brokerClient, cancellationToken);
        var predecessorBrokerOrderId = await stopReplacements!.ResolveCurrentBrokerOrderIdAsync(
            command.AccountId,
            command.RootBrokerOrderId,
            cancellationToken);
        var adopted = await brokerClient.GetOrderByClientOrderIdAsync(
            command.ReplacementClientOrderId,
            cancellationToken);
        if (adopted is not null)
        {
            var verifiedAdoption = RequireReplacementSuccessor(
                adopted,
                command,
                predecessorBrokerOrderId,
                expectedCurrent: null);
            await MarkProtectiveReplacementVerifiedAsync(
                command,
                lease.LeaseToken,
                verifiedAdoption,
                predecessorBrokerOrderId,
                cancellationToken);
            return verifiedAdoption;
        }

        var current = await RequireOwnedProtectiveStopAsync(
            await brokerClient.GetOpenOrdersAsync(cancellationToken),
            owner,
            predecessorBrokerOrderId,
            command.Symbol,
            cancellationToken);
        if (current.StopPrice is { } currentStop && currentStop > command.StopPrice)
        {
            throw new InvalidOperationException(
                $"Protective stop cannot be lowered from {currentStop} to {command.StopPrice}.");
        }

        if (current.StopPrice == command.StopPrice)
        {
            await MarkProtectiveReplacementVerifiedAsync(
                command,
                lease.LeaseToken,
                current,
                predecessorBrokerOrderId,
                cancellationToken);
            return current;
        }

        if (current.OrderType.Equals("trailing_stop", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Broker trailing_stop orders cannot be repriced with stop_price. " +
                "TradingFlow ATR trailing requires an ordinary stop or stop_limit order.");
        }

        if (current.OrderType.Equals("stop_limit", StringComparison.OrdinalIgnoreCase) &&
            current.LimitPrice is not > 0m)
        {
            throw new InvalidOperationException(
                $"Protective stop-limit order '{current.OrderId}' has no limit price to preserve.");
        }

        using var brokerMutation = brokerMutations.Enter(
            $"replace protective stop {command.ReplacementClientOrderId}");
        await stopReplacements!.AssertExecutionAuthorityAsync(
            command.CommandId,
            lease.LeaseToken,
            cancellationToken);

        ActiveBrokerOrder successor;
        try
        {
            successor = await brokerClient.ReplaceProtectiveOrderAsync(
                new BrokerProtectiveOrderReplacement(
                    current.OrderId,
                    command.ReplacementClientOrderId,
                    current.OrderType,
                    command.StopPrice,
                    current.OrderType.Equals("stop_limit", StringComparison.OrdinalIgnoreCase)
                        ? current.LimitPrice
                        : null),
                cancellationToken);
        }
        catch (Exception brokerFailure) when (brokerFailure is not OperationCanceledException)
        {
            var recovered = await brokerClient.GetOrderByClientOrderIdAsync(
                command.ReplacementClientOrderId,
                cancellationToken);
            if (recovered is null)
            {
                throw new InvalidOperationException(
                    $"Protective replacement for {command.Symbol} was not returned and its successor could not be adopted.",
                    brokerFailure);
            }

            successor = recovered;
        }

        var verified = RequireReplacementSuccessor(
            successor,
            command,
            predecessorBrokerOrderId,
            current);
        await MarkProtectiveReplacementVerifiedAsync(
            command,
            lease.LeaseToken,
            verified,
            predecessorBrokerOrderId,
            cancellationToken);
        return verified;
    }

    private async Task MarkProtectiveReplacementVerifiedAsync(
        ProtectiveStopReplacementRecord command,
        Guid leaseToken,
        ActiveBrokerOrder verified,
        string predecessorBrokerOrderId,
        CancellationToken cancellationToken)
    {
        var verifiedAtUtc = timeProvider.GetUtcNow().ToUniversalTime();
        var lifecycleTransition = verified.OrderId.Equals(predecessorBrokerOrderId, StringComparison.Ordinal)
            ? null
            : new BrokerOrderReplacementTransition(
                command.OwnerClientOrderId,
                predecessorBrokerOrderId,
                verified.OrderId,
                verified.UpdatedAt.ToUniversalTime(),
                verifiedAtUtc,
                JsonSerializer.Serialize(new
                {
                    action = "protective_stop_replaced",
                    commandId = command.CommandId,
                    replacementClientOrderId = command.ReplacementClientOrderId,
                    predecessorBrokerOrderId,
                    successorBrokerOrderId = verified.OrderId,
                    command.StopPrice,
                    command.Reason
                }));
        await stopReplacements!.MarkVerifiedWithLifecycleAsync(
            command.CommandId,
            leaseToken,
            verified.OrderId,
            verifiedAtUtc,
            lifecycleTransition,
            cancellationToken);
        logger.LogInformation(
            "Verified durable protective stop replacement for {Symbol}. CommandId={CommandId} OwnerClientOrderId={OwnerClientOrderId} BrokerOrderId={BrokerOrderId} StopPrice={StopPrice} Reason={Reason}.",
            command.Symbol,
            command.CommandId,
            command.OwnerClientOrderId,
            verified.OrderId,
            command.StopPrice,
            command.Reason);
    }

    private async Task<ActiveBrokerOrder> ResolveVerifiedProtectiveReplacementAsync(
        ProtectiveStopReplacementRecord command,
        OrderIntentRecord owner,
        IBrokerClient brokerClient,
        CancellationToken cancellationToken)
    {
        var successor = await brokerClient.GetOrderByClientOrderIdAsync(
            command.ReplacementClientOrderId,
            cancellationToken);
        if (successor is not null)
        {
            var verified = RequireReplacementSuccessor(
                successor,
                command,
                command.BrokerOrderId,
                expectedCurrent: null);
            await RecordProtectiveReplacementLifecycleAsync(
                command,
                verified,
                timeProvider.GetUtcNow().ToUniversalTime(),
                cancellationToken);
            return verified;
        }

        var owned = await RequireOwnedProtectiveStopAsync(
            await brokerClient.GetOpenOrdersAsync(cancellationToken),
            owner,
            command.VerifiedBrokerOrderId ?? command.BrokerOrderId,
            command.Symbol,
            cancellationToken,
            command.StopPrice);
        await RecordProtectiveReplacementLifecycleAsync(
            command,
            owned,
            timeProvider.GetUtcNow().ToUniversalTime(),
            cancellationToken);
        return owned;
    }

    private async Task RecordProtectiveReplacementLifecycleAsync(
        ProtectiveStopReplacementRecord command,
        ActiveBrokerOrder verified,
        DateTimeOffset localTimestampUtc,
        CancellationToken cancellationToken)
    {
        if (verified.OrderId.Equals(command.BrokerOrderId, StringComparison.Ordinal))
        {
            return;
        }

        if (events is null)
        {
            throw new InvalidOperationException(
                "Order-event persistence is required to record a broker replacement successor.");
        }

        await events.RecordBrokerReplacementAsync(
            new BrokerOrderReplacementTransition(
                command.OwnerClientOrderId,
                command.BrokerOrderId,
                verified.OrderId,
                verified.UpdatedAt.ToUniversalTime(),
                localTimestampUtc,
                JsonSerializer.Serialize(new
                {
                    action = "protective_stop_replaced",
                    commandId = command.CommandId,
                    replacementClientOrderId = command.ReplacementClientOrderId,
                    predecessorBrokerOrderId = command.BrokerOrderId,
                    successorBrokerOrderId = verified.OrderId,
                    command.StopPrice,
                    command.Reason
                })),
            cancellationToken);
    }

    private async Task<ActiveBrokerOrder> RequireOwnedProtectiveStopAsync(
        IReadOnlyCollection<ActiveBrokerOrder> openOrders,
        OrderIntentRecord owner,
        string brokerOrderId,
        string symbol,
        CancellationToken cancellationToken,
        decimal? expectedStopPrice = null)
    {
        var existing = openOrders.SingleOrDefault(order =>
            order.OrderId.Equals(brokerOrderId, StringComparison.Ordinal));
        var directlyOwned = existing is not null &&
            (existing.ClientOrderId.Equals(owner.ClientOrderId, StringComparison.Ordinal) ||
             String.Equals(existing.ParentClientOrderId, owner.ClientOrderId, StringComparison.Ordinal));
        var predecessor = directlyOwned || existing is null
            ? null
            : await stopReplacements!.GetVerifiedBySuccessorBrokerOrderIdAsync(
                owner.AccountId,
                brokerOrderId,
                cancellationToken);
        var chainOwned = predecessor is not null &&
            predecessor.OwnerClientOrderId.Equals(owner.ClientOrderId, StringComparison.Ordinal) &&
            predecessor.ReplacementClientOrderId.Equals(existing!.ClientOrderId, StringComparison.Ordinal);
        if (existing is null ||
            !existing.Ticker.Equals(symbol, StringComparison.OrdinalIgnoreCase) ||
            !existing.Side.Equals("sell", StringComparison.OrdinalIgnoreCase) ||
            existing.OrderType is not ("stop" or "stop_limit") ||
            existing.StopPrice is null ||
            !(directlyOwned || chainOwned) ||
            expectedStopPrice is { } expected && existing.StopPrice != expected)
        {
            throw new InvalidOperationException(
                $"Protective broker order '{brokerOrderId}' is not matching active protection owned by '{owner.ClientOrderId}'.");
        }

        return existing;
    }

    private static ActiveBrokerOrder RequireReplacementSuccessor(
        ActiveBrokerOrder successor,
        ProtectiveStopReplacementRecord command,
        string predecessorBrokerOrderId,
        ActiveBrokerOrder? expectedCurrent)
    {
        var expectedOrderType = expectedCurrent?.OrderType;
        var expectedLimitPrice = expectedCurrent?.OrderType.Equals(
            "stop_limit",
            StringComparison.OrdinalIgnoreCase) == true
                ? expectedCurrent.LimitPrice
                : null;
        if (!successor.ClientOrderId.Equals(command.ReplacementClientOrderId, StringComparison.Ordinal) ||
            successor.OrderId.Equals(predecessorBrokerOrderId, StringComparison.Ordinal) ||
            !String.Equals(successor.ReplacesOrderId, predecessorBrokerOrderId, StringComparison.Ordinal) ||
            !successor.Ticker.Equals(command.Symbol, StringComparison.OrdinalIgnoreCase) ||
            !successor.Side.Equals("sell", StringComparison.OrdinalIgnoreCase) ||
            successor.OrderType is not ("stop" or "stop_limit") ||
            expectedOrderType is not null &&
                !successor.OrderType.Equals(expectedOrderType, StringComparison.OrdinalIgnoreCase) ||
            successor.StopPrice != command.StopPrice ||
            expectedLimitPrice is not null && successor.LimitPrice != expectedLimitPrice)
        {
            throw new InvalidOperationException(
                $"Broker order '{successor.OrderId}' is not the expected successor for protective-stop command {command.CommandId:N}.");
        }

        return successor;
    }

    private async Task<ResolvedExitExecution> ResolveExitExecutionAsync(
        IBrokerClient brokerClient,
        string symbol,
        string side,
        bool allowExtendedHoursTrading,
        DateTimeOffset createdAtUtc,
        string? requestedOrderType,
        string? requestedTimeInForce,
        decimal? requestedLimitPrice,
        decimal? requestedStopPrice,
        CancellationToken cancellationToken)
    {
        var session = await brokerClient.GetSessionAsync(createdAtUtc, cancellationToken);
        if (session.Session == EquityTradingSession.Closed)
        {
            throw new InvalidOperationException(
                $"Position exit rejected because trade date {session.TradeDate:yyyy-MM-dd} is closed.");
        }

        if (session.Session == EquityTradingSession.Regular)
        {
            return ResolveRequestedRegularExit(
                session.Session,
                requestedOrderType,
                requestedTimeInForce,
                requestedLimitPrice,
                requestedStopPrice);
        }

        if (!allowExtendedHoursTrading)
        {
            throw new InvalidOperationException(
                $"Position exit rejected during {session.Session}: allow_extended_hours_trading is false.");
        }

        var eligibility = await brokerClient.GetEligibilityAsync(symbol, cancellationToken);
        if (eligibility is null || !eligibility.Active || !eligibility.Tradable ||
            session.Session == EquityTradingSession.Overnight && !eligibility.OvernightTradable)
        {
            throw new InvalidOperationException(
                $"Extended-hours exit rejected for {symbol}: current asset eligibility is unavailable or false.");
        }

        if (brokerClient is not IBrokerMarketObservationProvider marketProvider)
        {
            throw new InvalidOperationException(
                "Extended-hours exit requires an authoritative broker quote.");
        }

        var market = await marketProvider.GetMarketObservationAsync(
            symbol,
            session.Session,
            cancellationToken);
        var quoteAtUtc = market.QuoteTimestampUtc?.ToUniversalTime();
        var quoteAge = quoteAtUtc is null ? null : market.ObservedAtUtc.ToUniversalTime() - quoteAtUtc;
        var limitPrice = side.Equals("sell", StringComparison.Ordinal)
            ? market.BidPrice
            : market.AskPrice;
        if (limitPrice is not > 0m || quoteAge is null || quoteAge < TimeSpan.Zero ||
            quoteAge > exitOptions.QuoteMaxAge)
        {
            throw new InvalidOperationException(
                $"Extended-hours exit rejected for {symbol}: executable quote is missing or stale.");
        }

        if (!String.IsNullOrWhiteSpace(requestedOrderType))
        {
            var requested = ResolveRequestedRegularExit(
                session.Session,
                requestedOrderType,
                requestedTimeInForce,
                requestedLimitPrice,
                requestedStopPrice);
            if (requested.OrderType != "limit" || requested.TimeInForce != "day")
            {
                throw new InvalidOperationException(
                    "Extended-hours exits require an explicit limit order with DAY expiration.");
            }

            return requested with { SubmitOutsideRegularHours = true };
        }

        return new ResolvedExitExecution(
            "limit",
            "day",
            limitPrice,
            null,
            true,
            session.Session);
    }

    private static ResolvedExitExecution ResolveRequestedRegularExit(
        EquityTradingSession session,
        string? requestedOrderType,
        string? requestedTimeInForce,
        decimal? requestedLimitPrice,
        decimal? requestedStopPrice)
    {
        if (String.IsNullOrWhiteSpace(requestedOrderType))
        {
            return new ResolvedExitExecution("market", "day", null, null, false, session);
        }

        var orderType = requestedOrderType.Trim().ToLowerInvariant();
        var timeInForce = String.IsNullOrWhiteSpace(requestedTimeInForce)
            ? "day"
            : requestedTimeInForce.Trim().ToLowerInvariant();
        if (orderType is not ("market" or "limit" or "stop" or "stop_limit") ||
            timeInForce is not ("day" or "gtc" or "ioc" or "fok"))
        {
            throw new InvalidOperationException("Exit order type or time in force is unsupported.");
        }

        if (orderType is "limit" or "stop_limit" && requestedLimitPrice is not > 0m ||
            orderType is "stop" or "stop_limit" && requestedStopPrice is not > 0m ||
            orderType == "market" && (requestedLimitPrice is not null || requestedStopPrice is not null))
        {
            throw new InvalidOperationException("Exit price fields do not match the requested order type.");
        }

        return new ResolvedExitExecution(
            orderType,
            timeInForce,
            orderType is "limit" or "stop_limit" ? requestedLimitPrice : null,
            orderType is "stop" or "stop_limit" ? requestedStopPrice : null,
            false,
            session);
    }

    private sealed record ResolvedExitExecution(
        string OrderType,
        string TimeInForce,
        decimal? LimitPrice,
        decimal? StopPrice,
        bool SubmitOutsideRegularHours,
        EquityTradingSession Session);

    private static void Validate(EntryOrderSubmission submission)
    {
        if (submission.IntentId == Guid.Empty || submission.Candidate.CandidateId == Guid.Empty ||
            submission.RunContext.RunId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "Run, candidate, and intent identity are required before order submission.");
        }

        if (submission.OperatorOverride is not null)
        {
            if (!submission.RunContext.Profile.Equals("paper", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Operator-direct entries are permitted only in the paper profile.");
            }

            if (submission.StrategyIdentity is not null || submission.StrategySelectionMode is not null)
            {
                throw new InvalidOperationException(
                    "An operator override cannot also claim strategy execution authorization.");
            }
        }

        if (submission.Candidate.RevalidatedAtUtc == default ||
            submission.Candidate.DiscoveredAtUtc == default ||
            String.IsNullOrWhiteSpace(submission.Candidate.DiscoverySource) ||
            String.IsNullOrWhiteSpace(submission.Candidate.Horizon) ||
            String.IsNullOrWhiteSpace(submission.Candidate.SetupEvidenceJson))
        {
            throw new InvalidOperationException(
                "A validated candidate requires source, horizon, timestamps, and setup evidence.");
        }

        if (submission.Order.ShareQuantity <= 0 ||
            submission.Order.LimitPrice <= 0m ||
            submission.Order.StopLossPrice <= 0m ||
            submission.Order.TakeProfitPrice <= 0m)
        {
            throw new InvalidOperationException("Bracket order quantity and prices must all be positive.");
        }

        if (!NormalizeSide(submission.Side).Equals("buy", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The current bracket-entry broker contract supports long buy entries only.");
        }

        if (!submission.OrderType.Trim().Equals("limit", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Production bracket entries require an explicit limit price; unbounded market entries are not supported.");
        }

        if (submission.TimeInForce.Trim().ToLowerInvariant() is not ("day" or "gtc"))
        {
            throw new InvalidOperationException("Bracket entry time in force must be day or gtc.");
        }

        if (submission.RunContext.ConfigHash.Length != 64 ||
            !submission.RunContext.ConfigHash.All(Uri.IsHexDigit) ||
            submission.RunContext.CodeVersion.Length is not (40 or 64) ||
            !submission.RunContext.CodeVersion.All(Uri.IsHexDigit) ||
            submission.RunContext.Profile is not ("paper" or "live"))
        {
            throw new InvalidOperationException("Execution run provenance is invalid.");
        }
    }

    private static ProductionRun ToRun(ExecutionRunContext context) => new()
    {
        RunId = context.RunId,
        SchemaVersion = 1,
        ConfigHash = context.ConfigHash,
        CodeVersion = context.CodeVersion,
        Profile = context.Profile,
        Status = "running",
        StartedAtUtc = context.StartedAtUtc
    };

    private static bool ResolveSubmittedOutsideRegularHours(string requestJson, bool fallback)
    {
        try
        {
            using var document = JsonDocument.Parse(requestJson);
            return document.RootElement.TryGetProperty("submitOutsideRegularHours", out var value) &&
                value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? value.GetBoolean()
                    : fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private static void Validate(ProtectiveStopSubmission submission)
    {
        if (submission.IntentId == Guid.Empty || submission.RunContext.RunId == Guid.Empty ||
            String.IsNullOrWhiteSpace(submission.AccountId) ||
            String.IsNullOrWhiteSpace(submission.Symbol) || submission.Quantity <= 0m ||
            submission.Quantity != Decimal.Truncate(submission.Quantity) || submission.StopPrice <= 0m)
        {
            throw new InvalidOperationException(
                "A GTC protective stop requires run/intent identity, symbol, positive whole-share quantity, and stop price.");
        }

        if (String.IsNullOrWhiteSpace(submission.PositionGenerationIdentity) ||
            submission.ProtectionRevision < 0)
        {
            throw new InvalidOperationException(
                "A protective stop requires its persisted position-generation identity and non-negative replacement revision.");
        }

        _ = NormalizeSide(submission.Side);
        if (submission.RunContext.ConfigHash.Length != 64 ||
            !submission.RunContext.ConfigHash.All(Uri.IsHexDigit) ||
            submission.RunContext.CodeVersion.Length is not (40 or 64) ||
            !submission.RunContext.CodeVersion.All(Uri.IsHexDigit) ||
            submission.RunContext.Profile is not ("paper" or "live"))
        {
            throw new InvalidOperationException("Protective execution run provenance is invalid.");
        }
    }

    private static string NormalizeSide(string side) => side.Trim().ToLowerInvariant() switch
    {
        "b" or "buy" => "buy",
        "s" or "sell" => "sell",
        _ => throw new InvalidOperationException("Order side must be buy or sell.")
    };
}
