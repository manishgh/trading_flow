using System.Text.Json;
using TradingFlow.Domain.Execution;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Engine.Execution;

public enum EntryGateSlot
{
    SystemState = 1,
    CalendarWindow = 2,
    CandidateState = 3,
    HaltOrLuld = 4,
    QuoteAge = 5,
    Spread = 6,
    Account = 7,
    PositionConflict = 8,
    Sizing = 9,
    Exposure = 10,
    ExpectedSlippage = 11,
    DuplicateOrder = 12
}

public sealed record EntryGateOptions(
    int SetupMaxAgeSeconds,
    int QuoteMaxAgeMilliseconds,
    decimal MaxSpreadBps,
    decimal MaxExpectedSlippageBps,
    decimal MaxNotionalPerTradePct,
    decimal MaxGrossExposurePct,
    int MaxPositions,
    decimal PerTradeRiskPct = 0.5m,
    int AccountSnapshotMaxAgeSeconds = 60)
{
    public TimeSpan SetupMaxAge => TimeSpan.FromSeconds(SetupMaxAgeSeconds);
    public TimeSpan QuoteMaxAge => TimeSpan.FromMilliseconds(QuoteMaxAgeMilliseconds);
    public TimeSpan AccountSnapshotMaxAge => TimeSpan.FromSeconds(AccountSnapshotMaxAgeSeconds);
}

public sealed class EntryGateRejectedException(
    EntryGateSlot slot,
    RejectCode rejectCode,
    string message,
    Exception? innerException = null) : InvalidOperationException(message, innerException)
{
    public EntryGateSlot Slot { get; } = slot;
    public RejectCode RejectCode { get; } = rejectCode;
}

public interface IEntryGateChain
{
    Task<T> ExecuteAsync<T>(
        EntryOrderSubmission submission,
        IBrokerClient brokerClient,
        Func<EntryGateApproval, CancellationToken, Task<T>> submit,
        CancellationToken cancellationToken = default);
}

public sealed record EntryGateApproval(
    DateOnly TradeDate,
    EquityTradingSession Session,
    bool SubmitOutsideRegularHours,
    DateTimeOffset DispatchExpiresAtUtc,
    PortfolioRiskReservationRequest PortfolioRisk);

/// <summary>
/// Collects authoritative evidence lazily in binding order, persists every evaluated
/// gate, and invokes broker submission only after all twelve gates pass.
/// </summary>
public sealed class EntryGateChain(
    IEntryAdmissionControl admission,
    IPositionConflictGuard positionConflict,
    ICandidateRepository candidates,
    IGateEvaluationRepository evaluations,
    IStrategyAuthorizationPolicy strategyAuthorizations,
    ISecurityTradingStatusProvider tradingStatuses,
    EntryGateOptions options,
    TimeProvider timeProvider) : IEntryGateChain
{
    public async Task<T> ExecuteAsync<T>(
        EntryOrderSubmission submission,
        IBrokerClient brokerClient,
        Func<EntryGateApproval, CancellationToken, Task<T>> submit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(brokerClient);
        ArgumentNullException.ThrowIfNull(submit);
        var now = timeProvider.GetUtcNow();
        var results = new List<GateEvaluationAppendRequest>(12);

        var admissionSnapshot = admission.GetSnapshot();
        await RequireAsync(
            submission, results, EntryGateSlot.SystemState,
            admissionSnapshot.EntriesAllowed, RejectCode.REJECT_DEGRADED_DATA,
            new { admissionSnapshot.EntriesAllowed, admissionSnapshot.Blocks }, now, cancellationToken);

        TradingSessionSnapshot? session = null;
        string? calendarError = null;
        try
        {
            session = await brokerClient.GetSessionAsync(now, cancellationToken);
            ExtendedHoursOrderPolicy.Validate(
                session,
                submission.OrderType,
                submission.TimeInForce,
                submission.AllowExtendedHoursTrading);
            if (session.Session == EquityTradingSession.Overnight)
            {
                ExtendedHoursOrderPolicy.ValidateOvernightAsset(
                    submission.Order.Ticker,
                    await brokerClient.GetEligibilityAsync(submission.Order.Ticker, cancellationToken));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            calendarError = exception.Message;
        }

        await RequireAsync(
            submission, results, EntryGateSlot.CalendarWindow,
            session is not null && calendarError is null, RejectCode.REJECT_SETUP_INVALID,
            new { session = session?.Session.ToString(), session?.TradeDate, error = calendarError },
            now, cancellationToken);

        var candidate = submission.OperatorOverride is not null
            ? null
            : await candidates.GetAsync(submission.Candidate.CandidateId, cancellationToken);
        var candidateTransitions = candidate is null
            ? []
            : await candidates.GetTransitionsAsync(candidate.CandidateId, cancellationToken);
        var persistedTrigger = candidateTransitions.LastOrDefault(transition =>
            transition.NewState == TradingFlow.Domain.Strategies.StrategyCandidateState.Triggered &&
            transition.Sequence == candidate?.Version &&
            transition.SemanticDecisionSha256.Equals(
                submission.Candidate.SemanticDecisionSha256,
                StringComparison.Ordinal));
        var candidateAge = candidate is null ? (TimeSpan?)null : now - candidate.RevalidatedAtUtc.ToUniversalTime();
        StrategyEntryAdmission? strategyAdmission = null;
        string? strategyAuthorizationError = null;
        if (submission.OperatorOverride is null)
        {
            try
            {
                strategyAdmission = await strategyAuthorizations.AdmitNewEntryAsync(
                    submission.StrategyIdentity
                        ?? throw new UnauthorizedAccessException(
                            "Strategy-routed entry has no immutable strategy identity."),
                    submission.StrategySelectionMode
                        ?? throw new UnauthorizedAccessException(
                            "Strategy-routed entry has no explicit execution mode."),
                    submission.IntentId,
                    now,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                strategyAuthorizationError = exception.Message;
            }
        }

        var swingHorizon = submission.Candidate.Horizon.Equals("swing", StringComparison.OrdinalIgnoreCase);
        var candidateValid = swingHorizon &&
            (submission.OperatorOverride is not null ||
            candidate is not null &&
            strategyAuthorizationError is null &&
            candidate.State == TradingFlow.Domain.Strategies.StrategyCandidateState.Triggered &&
            persistedTrigger is not null &&
            candidate.Symbol.Equals(submission.Order.Ticker, StringComparison.OrdinalIgnoreCase) &&
            candidate.SelectedStrategy?.Equals(submission.StrategyId, StringComparison.Ordinal) == true &&
            candidate.Version == submission.Candidate.CandidateVersion &&
            candidate.SemanticDecisionSha256.Equals(
                submission.Candidate.SemanticDecisionSha256,
                StringComparison.Ordinal) &&
            (submission.StrategyIdentity is null || candidate.StrategyContentSha256.Equals(
                submission.StrategyIdentity.ContentSha256,
                StringComparison.Ordinal)) &&
            candidate.ExpiresAtUtc > now &&
            candidateAge is { } age && age >= TimeSpan.Zero && age <= options.SetupMaxAge);
        await RequireAsync(
            submission, results, EntryGateSlot.CandidateState,
            candidateValid, RejectCode.REJECT_SETUP_INVALID,
            new
            {
                available = candidate is not null,
                operatorOverride = submission.OperatorOverride is not null,
                operatorOverrideActor = submission.OperatorOverride?.Actor,
                operatorOverrideReason = submission.OperatorOverride?.Reason,
                submittedHorizon = submission.Candidate.Horizon,
                swingHorizon,
                strategySelectionMode = strategyAdmission?.SelectionMode.ToString(),
                strategyGrantDecisionId = strategyAdmission?.GrantDecisionId,
                strategyAuthorizationError,
                candidate?.State,
                candidate?.RevalidatedAtUtc,
                candidate?.ExpiresAtUtc,
                candidate?.Version,
                triggerTransitionPersisted = persistedTrigger is not null,
                triggerTransitionSequence = persistedTrigger?.Sequence,
                submittedVersion = submission.Candidate.CandidateVersion,
                semanticDecisionMatches = candidate?.SemanticDecisionSha256.Equals(
                    submission.Candidate.SemanticDecisionSha256,
                    StringComparison.Ordinal),
                candidateAgeMs = candidateAge?.TotalMilliseconds,
                maxAgeMs = options.SetupMaxAge.TotalMilliseconds,
                symbolMatches = candidate?.Symbol.Equals(submission.Order.Ticker, StringComparison.OrdinalIgnoreCase),
                strategyMatches = candidate?.SelectedStrategy?.Equals(submission.StrategyId, StringComparison.Ordinal)
            },
            now, cancellationToken);

        BrokerMarketObservation? market = null;
        string? marketError = null;
        var statusObservationAcquired = false;
        SecurityTradingStatus? tradingStatusSnapshot = null;
        try
        {
            await tradingStatuses.EnsureObservedAsync(submission.Order.Ticker, cancellationToken);
            statusObservationAcquired = true;
            if (brokerClient is not IBrokerMarketObservationProvider marketProvider)
            {
                throw new InvalidOperationException("Broker does not provide authoritative quote and trade observations.");
            }

            market = await marketProvider.GetMarketObservationAsync(
                submission.Order.Ticker,
                session!.Session,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            marketError = exception.Message;
        }
        finally
        {
            tradingStatusSnapshot = tradingStatuses.GetStatus(submission.Order.Ticker);
            if (statusObservationAcquired)
            {
                await tradingStatuses.ReleaseObservationAsync(
                    submission.Order.Ticker,
                    CancellationToken.None);
            }
        }

        var tradingStatus = tradingStatusSnapshot ?? tradingStatuses.GetStatus(submission.Order.Ticker);
        var tradeAge = market?.LastTradeTimestampUtc is { } tradeTimestamp
            ? now - tradeTimestamp.ToUniversalTime()
            : (TimeSpan?)null;
        var freshTradeObserved = tradeAge is { } observedTradeAge &&
            observedTradeAge >= TimeSpan.Zero && observedTradeAge <= options.QuoteMaxAge;
        var tradingObserved = tradingStatus.State == SecurityTradingState.TradingObserved ||
            (tradingStatus.State == SecurityTradingState.Unknown && freshTradeObserved);
        await RequireAsync(
            submission, results, EntryGateSlot.HaltOrLuld,
            marketError is null && tradingObserved, RejectCode.REJECT_HALT_OR_LULD,
            new
            {
                tradingStatus.State,
                tradingStatus.ProviderStatusCode,
                tradingStatus.ProviderReasonCode,
                tradingStatus.ProviderTimestampUtc,
                market?.LastTradeTimestampUtc,
                tradeAgeMs = tradeAge?.TotalMilliseconds,
                error = marketError
            },
            now, cancellationToken);

        var quoteAge = market?.QuoteTimestampUtc is { } quoteTimestamp
            ? now - quoteTimestamp.ToUniversalTime()
            : (TimeSpan?)null;
        var quoteFresh = quoteAge is { } observedQuoteAge &&
            observedQuoteAge >= TimeSpan.Zero && observedQuoteAge <= options.QuoteMaxAge;
        await RequireAsync(
            submission, results, EntryGateSlot.QuoteAge,
            quoteFresh, RejectCode.REJECT_DEGRADED_DATA,
            new
            {
                market?.Feed,
                market?.QuoteTimestampUtc,
                quoteAgeMs = quoteAge?.TotalMilliseconds,
                maxAgeMs = options.QuoteMaxAge.TotalMilliseconds
            },
            now, cancellationToken);

        await RequireAsync(
            submission, results, EntryGateSlot.Spread,
            market?.SpreadBps is >= 0m && market.SpreadBps <= options.MaxSpreadBps,
            RejectCode.REJECT_WIDE_SPREAD,
            new { market?.BidPrice, market?.AskPrice, market?.SpreadBps, options.MaxSpreadBps },
            now, cancellationToken);

        BrokerAccountSnapshot? account = null;
        AssetTradingEligibility? eligibility = null;
        string? accountError = null;
        try
        {
            if (brokerClient is not IBrokerAccountProvider accountProvider)
            {
                throw new InvalidOperationException("Broker does not provide an authoritative account snapshot.");
            }

            account = await accountProvider.GetAccountSnapshotAsync(cancellationToken);
            eligibility = await brokerClient.GetEligibilityAsync(submission.Order.Ticker, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            accountError = exception.Message;
        }

        var accountEvaluationAtUtc = timeProvider.GetUtcNow();
        var notional = submission.Order.ShareQuantity * submission.Order.LimitPrice;
        var accountAge = account is null
            ? (TimeSpan?)null
            : accountEvaluationAtUtc - account.ObservedAtUtc.ToUniversalTime();
        var accountTimelineValid = account is not null &&
            account.RequestedAtUtc.ToUniversalTime() <= account.ObservedAtUtc.ToUniversalTime() &&
            account.ObservedAtUtc.ToUniversalTime() <= accountEvaluationAtUtc;
        var accountSnapshotFresh = accountTimelineValid &&
            accountAge is { } observedAccountAge &&
            observedAccountAge >= TimeSpan.Zero &&
            observedAccountAge <= options.AccountSnapshotMaxAge;
        var availableBuyingPower = account is null
            ? (decimal?)null
            : Math.Min(account.BuyingPower, account.RegulationTBuyingPower);
        var accountOperational = account is not null &&
            account.Status.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase) &&
            !account.AccountBlocked && !account.TradingBlocked && !account.TradeSuspendedByUser;
        var assetAllowed = eligibility is { Active: true, Tradable: true } &&
            (submission.Side.Equals("buy", StringComparison.OrdinalIgnoreCase) ||
             account?.ShortingEnabled == true && eligibility.Shortable &&
             !String.Equals(
                 eligibility.BorrowStatus,
                 "unavailable",
                 StringComparison.OrdinalIgnoreCase));
        var buyingPowerSufficient = availableBuyingPower >= notional;
        var accountAllowed = accountError is null && accountSnapshotFresh &&
            accountOperational && assetAllowed && buyingPowerSufficient;
        var accountReject = !assetAllowed && submission.Side.Equals("sell", StringComparison.OrdinalIgnoreCase)
            ? RejectCode.REJECT_NO_SHORT_AVAILABILITY
            : buyingPowerSufficient != true
                ? RejectCode.REJECT_BUDGET_EXHAUSTED
                : accountSnapshotFresh
                    ? RejectCode.REJECT_SETUP_INVALID
                    : RejectCode.REJECT_DEGRADED_DATA;
        await RequireAsync(
            submission, results, EntryGateSlot.Account,
            accountAllowed, accountError is null ? accountReject : RejectCode.REJECT_DEGRADED_DATA,
            new
            {
                account?.AccountId,
                account?.Status,
                account?.AccountBlocked,
                account?.TradingBlocked,
                account?.TradeSuspendedByUser,
                account?.ShortingEnabled,
                account?.BuyingPower,
                account?.RegulationTBuyingPower,
                availableBuyingPower,
                account?.Equity,
                account?.RequestedAtUtc,
                account?.ObservedAtUtc,
                accountAgeMs = accountAge?.TotalMilliseconds,
                maxAccountAgeMs = options.AccountSnapshotMaxAge.TotalMilliseconds,
                eligibility,
                requiredBuyingPower = notional,
                horizon = submission.Candidate.Horizon,
                pdtProviderApplicable = false,
                error = accountError
            },
            now, cancellationToken);

        try
        {
            return await positionConflict.ExecuteEntryAsync(
                account!.AccountId,
                submission.Order.Ticker,
                submission.StrategyId,
                async token =>
                {
                    results.Add(Result(
                        submission, EntryGateSlot.PositionConflict, true, null,
                        new { symbol = submission.Order.Ticker, strategy = submission.StrategyId }, now));

                    var reservationAccount = await brokerClient.GetAccountSnapshotAsync(token);
                    var reservationObservedAtUtc = timeProvider.GetUtcNow();
                    var reservationAccountAge = reservationObservedAtUtc -
                        reservationAccount.ObservedAtUtc.ToUniversalTime();
                    var reservationAccountValid =
                        reservationAccount.AccountId.Equals(account!.AccountId, StringComparison.Ordinal) &&
                        reservationAccount.Status.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase) &&
                        !reservationAccount.AccountBlocked &&
                        !reservationAccount.TradingBlocked &&
                        !reservationAccount.TradeSuspendedByUser &&
                        reservationAccount.RequestedAtUtc.ToUniversalTime() <=
                            reservationAccount.ObservedAtUtc.ToUniversalTime() &&
                        reservationAccount.ObservedAtUtc.ToUniversalTime() <= reservationObservedAtUtc &&
                        reservationAccountAge >= TimeSpan.Zero &&
                        reservationAccountAge <= options.AccountSnapshotMaxAge;
                    var reservationBuyingPower = Math.Min(
                        reservationAccount.BuyingPower,
                        reservationAccount.RegulationTBuyingPower);
                    var reservationAccountAllowed = reservationAccountValid &&
                        reservationBuyingPower >= notional;
                    var reservationAccountReject = reservationAccountValid
                        ? RejectCode.REJECT_BUDGET_EXHAUSTED
                        : RejectCode.REJECT_DEGRADED_DATA;
                    var accountGateIndex = results.FindIndex(result =>
                        result.GateOrder == (int)EntryGateSlot.Account);
                    if (accountGateIndex < 0)
                    {
                        throw new InvalidOperationException("The initial account gate result is missing.");
                    }

                    results[accountGateIndex] = Result(
                        submission,
                        EntryGateSlot.Account,
                        reservationAccountAllowed,
                        reservationAccountAllowed ? null : reservationAccountReject,
                        new
                        {
                            phase = "reservation_refresh",
                            initialAccountId = account.AccountId,
                            refreshedAccountId = reservationAccount.AccountId,
                            reservationAccount.Status,
                            reservationAccount.AccountBlocked,
                            reservationAccount.TradingBlocked,
                            reservationAccount.TradeSuspendedByUser,
                            reservationAccount.Equity,
                            reservationBuyingPower,
                            requiredBuyingPower = notional,
                            requestedAtUtc = reservationAccount.RequestedAtUtc,
                            accountObservedAtUtc = reservationAccount.ObservedAtUtc,
                            gateObservedAtUtc = reservationObservedAtUtc,
                            accountAgeMs = reservationAccountAge.TotalMilliseconds,
                            maxAccountAgeMs = options.AccountSnapshotMaxAge.TotalMilliseconds
                        },
                        reservationObservedAtUtc);
                    if (!reservationAccountAllowed)
                    {
                        await PersistAsync(
                            submission,
                            results,
                            CreateCandidateRejection(
                                submission,
                                EntryGateSlot.Account,
                                reservationAccountReject),
                            token);
                        throw new EntryGateRejectedException(
                            EntryGateSlot.Account,
                            reservationAccountReject,
                            $"Entry rejected at account for {submission.Order.Ticker}: {reservationAccountReject}.");
                    }

                    var perTradeRiskPct = options.PerTradeRiskPct;
                    var perTradeRiskLimit = reservationAccount.Equity * perTradeRiskPct / 100m;
                    var sizingValid = IsSizingValid(
                        submission,
                        reservationAccount,
                        perTradeRiskLimit,
                        out var riskDollars);
                    await RequireAsync(
                        submission, results, EntryGateSlot.Sizing,
                        sizingValid, RejectCode.REJECT_SETUP_INVALID,
                        new
                        {
                            submission.Order.ShareQuantity,
                            submission.Order.LimitPrice,
                            submission.Order.StopLossPrice,
                            submission.Order.TakeProfitPrice,
                            riskDollars,
                            perTradeRiskLimit,
                            perTradeRiskPct,
                            notional,
                            reservationAccount.Equity,
                            maxNotional = reservationAccount.Equity * options.MaxNotionalPerTradePct / 100m
                        },
                        now, token);

                    var positions = await brokerClient.GetOpenPositionsAsync(token);
                    var grossExposure = Math.Abs(reservationAccount.LongMarketValue) +
                        Math.Abs(reservationAccount.ShortMarketValue);
                    var maxGrossPct = options.MaxGrossExposurePct;
                    var maxPositions = options.MaxPositions;
                    var exposureAllowed = grossExposure + notional <= reservationAccount.Equity * maxGrossPct / 100m &&
                        positions.Count < maxPositions;
                    await RequireAsync(
                        submission, results, EntryGateSlot.Exposure,
                        exposureAllowed, RejectCode.REJECT_BUDGET_EXHAUSTED,
                        new
                        {
                            horizon = submission.Candidate.Horizon,
                            currentGrossExposure = grossExposure,
                            proposedNotional = notional,
                            maxGrossExposure = reservationAccount.Equity * maxGrossPct / 100m,
                            currentPositions = positions.Count,
                            maxPositions
                        },
                        now, token);

                    var expectedSlippageBps = CalculateExpectedSlippageBps(submission, market!);
                    await RequireAsync(
                        submission, results, EntryGateSlot.ExpectedSlippage,
                        expectedSlippageBps is >= 0m && expectedSlippageBps <= options.MaxExpectedSlippageBps,
                        RejectCode.REJECT_SETUP_INVALID,
                        new { expectedSlippageBps, options.MaxExpectedSlippageBps, market!.BidPrice, market.AskPrice },
                        now, token);

                    var duplicateOrders = (await brokerClient.GetOpenOrdersAsync(token))
                        .Where(order =>
                            order.Ticker.Equals(submission.Order.Ticker, StringComparison.OrdinalIgnoreCase) &&
                            order.Side.Equals(submission.Side, StringComparison.OrdinalIgnoreCase))
                        .Select(order => new { order.OrderId, order.ClientOrderId, order.Status })
                        .ToArray();
                    await RequireAsync(
                        submission, results, EntryGateSlot.DuplicateOrder,
                        duplicateOrders.Length == 0, RejectCode.REJECT_DUPLICATE_EVENT,
                        new { duplicateOrders }, now, token);

                    await PersistAsync(submission, results, candidateRejection: null, token);
                    var maxGrossExposure = reservationAccount.Equity * maxGrossPct / 100m;
                    var maxPortfolioRisk = perTradeRiskLimit * maxPositions;
                    return await submit(
                        new EntryGateApproval(
                            session!.TradeDate,
                            session.Session,
                            session.Session != EquityTradingSession.Regular,
                            candidate?.ExpiresAtUtc ?? now.Add(options.SetupMaxAge),
                            new PortfolioRiskReservationRequest(
                                reservationAccount.AccountId,
                                "swing",
                                reservationAccount.Equity,
                                reservationBuyingPower,
                                grossExposure,
                                reservationAccount.LongMarketValue + reservationAccount.ShortMarketValue,
                                positions
                                    .Select(position => position.Ticker.Trim().ToUpperInvariant())
                                    .ToHashSet(StringComparer.Ordinal),
                                notional,
                                riskDollars,
                                maxGrossExposure,
                                maxPortfolioRisk,
                                maxPositions,
                                reservationAccount.RequestedAtUtc.ToUniversalTime(),
                                reservationAccount.ObservedAtUtc.ToUniversalTime())),
                        token);
                },
                cancellationToken);
        }
        catch (PositionConflictException exception)
        {
            results.Add(Result(
                submission, EntryGateSlot.PositionConflict, false, exception.RejectCode,
                new { error = exception.Message }, now));
            await PersistAsync(
                submission,
                results,
                CreateCandidateRejection(submission, EntryGateSlot.PositionConflict, exception.RejectCode),
                cancellationToken);
            throw new EntryGateRejectedException(
                EntryGateSlot.PositionConflict,
                exception.RejectCode,
                exception.Message,
                exception);
        }
    }

    private bool IsSizingValid(
        EntryOrderSubmission submission,
        BrokerAccountSnapshot account,
        decimal perTradeRiskLimit,
        out decimal riskDollars)
    {
        var order = submission.Order;
        var isLong = submission.Side.Equals("buy", StringComparison.OrdinalIgnoreCase);
        var stopDistance = isLong
            ? order.LimitPrice - order.StopLossPrice
            : order.StopLossPrice - order.LimitPrice;
        var targetDistance = isLong
            ? order.TakeProfitPrice - order.LimitPrice
            : order.LimitPrice - order.TakeProfitPrice;
        riskDollars = order.ShareQuantity * stopDistance;
        return order.ShareQuantity > 0 && order.LimitPrice > 0m && stopDistance > 0m &&
            targetDistance > 0m && riskDollars > 0m && riskDollars <= perTradeRiskLimit &&
            account.Equity > 0m &&
            order.ShareQuantity * order.LimitPrice <=
                account.Equity * options.MaxNotionalPerTradePct / 100m;
    }

    private static decimal? CalculateExpectedSlippageBps(
        EntryOrderSubmission submission,
        BrokerMarketObservation market)
    {
        if (market.MidPrice is not > 0m)
        {
            return null;
        }

        var executable = submission.Side.Equals("buy", StringComparison.OrdinalIgnoreCase)
            ? market.AskPrice
            : market.BidPrice;
        if (executable is not > 0m)
        {
            return null;
        }

        return Math.Abs(executable.Value - market.MidPrice.Value) /
            market.MidPrice.Value * 10_000m;
    }

    private async Task RequireAsync(
        EntryOrderSubmission submission,
        List<GateEvaluationAppendRequest> results,
        EntryGateSlot slot,
        bool passed,
        RejectCode rejectCode,
        object inputs,
        DateTimeOffset evaluatedAt,
        CancellationToken cancellationToken)
    {
        results.Add(Result(
            submission, slot, passed, passed ? null : rejectCode, inputs, evaluatedAt));
        if (passed)
        {
            return;
        }

        await PersistAsync(
            submission,
            results,
            CreateCandidateRejection(submission, slot, rejectCode),
            cancellationToken);
        throw new EntryGateRejectedException(
            slot,
            rejectCode,
            $"Entry rejected at {GateName(slot)} for {submission.Order.Ticker}: {rejectCode}.");
    }

    private Task PersistAsync(
        EntryOrderSubmission submission,
        IReadOnlyList<GateEvaluationAppendRequest> results,
        CandidateGateRejection? candidateRejection,
        CancellationToken cancellationToken) =>
        evaluations.AppendBatchAsync(
            ToRun(submission.RunContext),
            results,
            candidateRejection,
            cancellationToken);

    private static CandidateGateRejection? CreateCandidateRejection(
        EntryOrderSubmission submission,
        EntryGateSlot slot,
        RejectCode rejectCode) =>
        submission.OperatorOverride is not null
            ? null
            : new CandidateGateRejection(
                submission.Candidate.CandidateId,
                submission.Candidate.CandidateVersion,
                submission.Candidate.SemanticDecisionSha256,
                submission.Order.Ticker.Trim().ToUpperInvariant(),
                submission.StrategyId,
                GateName(slot),
                rejectCode);

    private static GateEvaluationAppendRequest Result(
        EntryOrderSubmission submission,
        EntryGateSlot slot,
        bool passed,
        RejectCode? rejectCode,
        object inputs,
        DateTimeOffset evaluatedAt) =>
        new(
            submission.Candidate.CandidateId,
            null,
            (int)slot,
            GateName(slot),
            passed,
            rejectCode,
            evaluatedAt,
            JsonSerializer.Serialize(inputs));

    private static string GateName(EntryGateSlot slot) => slot switch
    {
        EntryGateSlot.SystemState => "system_state",
        EntryGateSlot.CalendarWindow => "calendar_window",
        EntryGateSlot.CandidateState => "candidate_state",
        EntryGateSlot.HaltOrLuld => "halt_or_luld",
        EntryGateSlot.QuoteAge => "quote_age",
        EntryGateSlot.Spread => "spread",
        EntryGateSlot.Account => "account",
        EntryGateSlot.PositionConflict => "position_conflict",
        EntryGateSlot.Sizing => "sizing",
        EntryGateSlot.Exposure => "exposure",
        EntryGateSlot.ExpectedSlippage => "expected_slippage",
        EntryGateSlot.DuplicateOrder => "duplicate_order",
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, "Unknown entry gate.")
    };

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
}
