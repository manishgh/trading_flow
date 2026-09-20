using System.Text.Json;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Pipeline;
using TradingFlow.Engine.Strategies;
using TradingFlow.Engine.Storage;
using TradingFlow.Data.Catalysts;
using Microsoft.Extensions.Logging;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Backtesting;

// Technical-exit + broker trailing-stop handling (LiveRunner partial split).
public sealed partial class LiveRunner
{
    private async Task<bool> TrySubmitTechnicalExitAsync(
        BacktestRunConfig run,
        StrategyDefinition strategy,
        string ticker,
        IReadOnlyDictionary<string, List<OhlcvBar>> barsByTimeframe,
        IReadOnlyDictionary<string, IReadOnlyList<IndicatorSnapshot>> snapshotsByTimeframe,
        IReadOnlyCollection<ActiveBrokerOrder> openOrders,
        IReadOnlyCollection<BrokerPosition> openPositions,
        CancellationToken cancellationToken,
        IProgress<string>? progress)
    {
        if (_brokerClient is null ||
            _orderIntents is null ||
            _orderEvents is null ||
            _positionLedger is null ||
            _executionRunContext is null ||
            run.Execution.DryRun ||
            !run.Execution.AllowLiveOrders)
        {
            return false;
        }

        var position = openPositions.FirstOrDefault(x =>
            x.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase) &&
            x.Qty > 0 &&
            !x.Side.Equals("short", StringComparison.OrdinalIgnoreCase));
        if (position is null)
        {
            return false;
        }

        var ownedPositions = (await _positionLedger.ListCurrentForRunAsync(
                _executionRunContext.RunId,
                cancellationToken))
            .Where(item => item.Position.Symbol.Equals(ticker, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (ownedPositions.Length != 1)
        {
            logger.LogWarning(
                "Technical exit for {Ticker} {StrategyName} requires exactly one run-owned position generation; found {Count}.",
                ticker,
                strategy.StrategyName,
                ownedPositions.Length);
            return false;
        }

        var ownedPosition = ownedPositions[0].Position;
        var entryIntent = await _orderIntents.GetByClientOrderIdAsync(
            ownedPosition.PositionGenerationClientOrderId,
            cancellationToken);
        if (entryIntent is null ||
            !entryIntent.StrategyId.Equals(strategy.StrategyId, StringComparison.Ordinal))
        {
            logger.LogDebug(
                "Open position for {Ticker} is not owned by strategy {StrategyId}; technical exit evaluation skipped.",
                ticker,
                strategy.StrategyId);
            return false;
        }

        var entryState = await _orderEvents.GetCurrentAsync(entryIntent.ClientOrderId, cancellationToken);
        if (entryState is null || entryState.State is not (OrderState.PartiallyFilled or OrderState.Filled))
        {
            logger.LogWarning(
                "Open position for {Ticker} has no authoritative partial/full entry fill for {ClientOrderId}; technical exit evaluation skipped.",
                ticker,
                entryIntent.ClientOrderId);
            return false;
        }

        if (!barsByTimeframe.TryGetValue(strategy.Execution.Timeframe, out var executionBars) ||
            !snapshotsByTimeframe.TryGetValue(strategy.Execution.Timeframe, out var executionSnapshots) ||
            executionBars.Count == 0 ||
            executionSnapshots.Count == 0)
        {
            logger.LogWarning(
                "Cannot evaluate technical exit for {Ticker} {StrategyName}; execution timeframe {Timeframe} is missing.",
                ticker,
                strategy.StrategyName,
                strategy.Execution.Timeframe);
            return false;
        }

        var entryTimestamp = entryState.BrokerTimestampUtc ?? entryIntent.CreatedAtUtc;
        var entryIndex = FindFirstBarIndexAtOrAfter(executionBars, entryTimestamp);
        if (entryIndex >= executionBars.Count)
        {
            return true;
        }

        var entryPrice = position.EntryPrice > 0
            ? position.EntryPrice
            : entryState.FillPrice ?? entryIntent.LimitPrice ?? 0m;
        if (entryPrice <= 0)
        {
            logger.LogWarning(
                "Cannot evaluate technical exit for {Ticker} {StrategyName}; entry price is unavailable.",
                ticker,
                strategy.StrategyName);
            return false;
        }

        var latestSnapshot = executionSnapshots[^1];
        var initialStopLossPrice = entryIntent.StopPrice is > 0m
            ? entryIntent.StopPrice.Value
            : entryPrice - ((latestSnapshot.Atr ?? 0m) * strategy.ExitRules.StopAtrMultiple);
        var stopDistance = entryPrice - initialStopLossPrice;
        if (stopDistance <= 0)
        {
            logger.LogWarning(
                "Cannot evaluate technical exit for {Ticker} {StrategyName}; stop distance is invalid. Entry={EntryPrice} Stop={StopLossPrice}",
                ticker,
                strategy.StrategyName,
                entryPrice,
                initialStopLossPrice);
            return false;
        }

        var configuredTakeProfitPrice = ReadDecimalProperty(entryIntent.RequestJson, "takeProfitPrice");
        var takeProfitPrice = configuredTakeProfitPrice is > 0m
            ? configuredTakeProfitPrice.Value
            : entryPrice + (stopDistance * strategy.ExitRules.TargetRMultiple);
        var currentStopLossPrice = initialStopLossPrice;
        var highestHighSinceEntry = entryPrice;

        string? exitReason = null;
        decimal? exitPrice = null;
        DateTimeOffset exitSignalTimestamp = DateTimeOffset.UtcNow;
        var exitStartIndex = TimeframeParser.IsDailyOrHigher(strategy.Execution.Timeframe) ||
            !strategy.ExitRules.AllowSameBarStopTarget
            ? entryIndex + 1
            : entryIndex;
        for (var index = exitStartIndex; index < executionBars.Count; index++)
        {
            var exitPriceLogTrend = TechnicalExecutionEngine.ComputeLogTrend(
                executionBars,
                index,
                strategy.ExitRules.ExitLogPriceLookbackBars,
                x => x.Close,
                addOne: false);
            var exitVolumeLogTrend = TechnicalExecutionEngine.ComputeLogTrend(
                executionBars,
                index,
                strategy.ExitRules.ExitLogVolumeLookbackBars,
                x => x.Volume,
                addOne: true);
            if (_technicalExecutionEngine.ShouldExitLongOnConfirmedVwapFailure(
                strategy,
                executionSnapshots,
                index,
                entryIndex,
                entryPrice,
                stopDistance,
                Math.Max(highestHighSinceEntry, executionBars[index].High),
                index - entryIndex))
            {
                exitReason = "confirmed_vwap_failure";
                exitPrice = executionBars[index].Close;
                exitSignalTimestamp = executionBars[index].Timestamp;
                break;
            }

            var (candidateExitPrice, candidateExitReason) = _technicalExecutionEngine.EvaluateBarForExit(
                strategy,
                executionBars[index],
                executionSnapshots[index],
                entryPrice,
                initialStopLossPrice,
                takeProfitPrice,
                entryTimestamp,
                stopDistance,
                index - entryIndex,
                ref currentStopLossPrice,
                ref highestHighSinceEntry,
                exitPriceLogTrend?.Slope,
                exitVolumeLogTrend?.Slope,
                index > 0 ? executionSnapshots[index - 1] : null);

            if (candidateExitReason is not null)
            {
                exitReason = candidateExitReason;
                exitPrice = candidateExitPrice;
                exitSignalTimestamp = executionBars[index].Timestamp;
                break;
            }
        }

        if (exitReason is null)
        {
            await TryRaiseBrokerTrailingStopAsync(
                strategy,
                ticker,
                ownedPosition.PositionGenerationEventId,
                openOrders,
                initialStopLossPrice,
                currentStopLossPrice,
                cancellationToken,
                progress);

            return false;
        }

        logger.LogInformation(
            "Technical exit triggered for {Ticker} {StrategyName}. Reason={ExitReason} SignalTime={SignalTime} EstimatedExitPrice={ExitPrice}",
            ticker,
            strategy.StrategyName,
            exitReason,
            exitSignalTimestamp,
            exitPrice);
        progress?.Report($"Technical exit triggered for {ticker}: {exitReason}. Closing broker position...");

        if (_orderSubmissionService is null || _executionRunContext is null)
        {
            throw new InvalidOperationException(
                "Technical exits require the durable order command service and execution run context.");
        }

        var refreshedPosition = (await _brokerClient.GetOpenPositionsAsync(cancellationToken))
            .FirstOrDefault(candidate =>
                candidate.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase) &&
                candidate.Qty > 0m &&
                !candidate.Side.Equals("short", StringComparison.OrdinalIgnoreCase));
        if (refreshedPosition is null)
        {
            logger.LogInformation(
                "Technical exit for {Ticker} did not submit because the broker position is flat.",
                ticker);
            progress?.Report($"{ticker} is flat; no additional exit was submitted.");
            return true;
        }

        var closeQuantity = Math.Min(
            Math.Abs(ownedPosition.Quantity),
            Math.Abs(refreshedPosition.Qty));
        var submittedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        OrderSubmissionResult exitSubmission;
        try
        {
            exitSubmission = await _orderSubmissionService.SubmitPositionExitAsync(
                new PositionExitSubmission(
                    _executionRunContext,
                    ticker,
                    closeQuantity,
                    exitReason,
                    submittedAtUtc,
                    run.Execution.AllowExtendedHoursTrading),
                _brokerClient,
                cancellationToken);
        }
        catch (PositionExitNoLongerRequiredException)
        {
            progress?.Report($"{ticker} became flat while its owned protection was being resolved.");
            return true;
        }
        _auditor.LogEvent(
            ticker,
            strategy.StrategyName,
            submittedAtUtc,
            ExecutionState.ExitSubmitted,
            $"Technical exit submitted: {exitReason}");
        if (_auditRepo is not null)
        {
            await _auditRepo.SaveAuditAsync(new TradingFlow.Domain.Audit.DecisionAuditRecord
            {
                RunName = run.RunName,
                Ticker = ticker,
                StrategyName = strategy.StrategyName,
                Timestamp = DateTimeOffset.UtcNow,
                Decision = "ExitSubmitted",
                RejectionReason = exitReason,
                SignalJson = JsonSerializer.Serialize(new
                {
                    ticker,
                    strategy = strategy.StrategyName,
                    exitReason,
                    exitSubmission.IntentId,
                    exitSubmission.ClientOrderId,
                    exitSubmission.BrokerOrderId,
                    exitSignalTimestamp,
                    estimatedExitPrice = exitPrice,
                    entryPrice,
                    currentPrice = position.CurrentPrice,
                    unrealizedPl = position.UnrealizedPl
                })
            }, cancellationToken);
        }

        progress?.Report($"Submitted technical exit for {ticker}: {exitReason}.");
        return true;
    }

    private async Task TryRaiseBrokerTrailingStopAsync(
        StrategyDefinition strategy,
        string ticker,
        long positionGenerationEventId,
        IReadOnlyCollection<ActiveBrokerOrder> openOrders,
        decimal initialStopLossPrice,
        decimal calculatedStopLossPrice,
        CancellationToken cancellationToken,
        IProgress<string>? progress)
    {
        if (_brokerClient is null ||
            _orderIntents is null ||
            !strategy.ExitRules.EnableAtrTrailingStop ||
            calculatedStopLossPrice <= initialStopLossPrice ||
            positionGenerationEventId <= 0)
        {
            return;
        }

        var candidateStopOrders = openOrders
            .Where(order =>
                order.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase) &&
                order.Side.Equals("sell", StringComparison.OrdinalIgnoreCase) &&
                order.OrderType is "stop" or "stop_limit" &&
                order.StopPrice is not null &&
                calculatedStopLossPrice > order.StopPrice.Value + 0.01m)
            .OrderByDescending(order => order.CreatedAt)
            .ToArray();
        if (candidateStopOrders.Length == 0)
        {
            return;
        }

        var stopOrders = new List<ActiveBrokerOrder>();
        foreach (var candidate in candidateStopOrders)
        {
            var owner = await _orderIntents.GetByClientOrderIdAsync(
                candidate.ParentClientOrderId ?? candidate.ClientOrderId,
                cancellationToken);
            if (owner is not null &&
                owner.Kind == OrderIntentKind.ProtectiveStop &&
                owner.PositionGenerationEventId == positionGenerationEventId)
            {
                stopOrders.Add(candidate);
            }
        }
        if (stopOrders.Count == 0)
        {
            return;
        }

        if (_orderSubmissionService is null)
        {
            throw new InvalidOperationException(
                "Trailing-stop replacement requires the common order command service.");
        }

        foreach (var stopOrder in stopOrders)
        {
            await _orderSubmissionService.ReplaceProtectiveStopAsync(
                new ProtectiveStopReplacementSubmission(
                    stopOrder.ParentClientOrderId ?? stopOrder.ClientOrderId,
                    stopOrder.OrderId,
                    ticker,
                    calculatedStopLossPrice,
                    "atr_trailing_stop_raise",
                    _timeProvider.GetUtcNow().ToUniversalTime()),
                _brokerClient,
                cancellationToken);
        }

        logger.LogInformation(
            "Raised {StopCount} trailing stop tranche(s) for {Ticker} {StrategyName}. NewStop={StopLossPrice}",
            stopOrders.Count,
            ticker,
            strategy.StrategyName,
            calculatedStopLossPrice);
        progress?.Report($"Raised trailing stop for {ticker} to {calculatedStopLossPrice:0.00}.");
    }

    private static decimal? ReadDecimalProperty(string json, string propertyName)
    {
        if (String.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(propertyName, out var property) &&
               property.TryGetDecimal(out var value)
            ? value
            : null;
    }
}
