using System.Text.Json;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Orders;
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

// Pipeline-failure audits + small helpers (LiveRunner partial split).
public sealed partial class LiveRunner
{
    private async Task SaveTickerPipelineFailureAuditsAsync(
        BacktestRunConfig run,
        IReadOnlyCollection<StrategyDefinition> strategies,
        string ticker,
        string reason,
        CancellationToken cancellationToken)
    {
        if (_auditRepo is null)
        {
            return;
        }

        var signalJson = JsonSerializer.Serialize(new
        {
            ticker,
            error = reason,
            timestamp = DateTimeOffset.UtcNow
        });

        foreach (var strategy in strategies)
        {
            await _auditRepo.SaveAuditAsync(new TradingFlow.Domain.Audit.DecisionAuditRecord
            {
                RunName = run.RunName,
                Ticker = ticker,
                StrategyName = strategy.StrategyName,
                Timestamp = DateTimeOffset.UtcNow,
                Decision = "Rejected",
                RejectionReason = $"ticker_pipeline_failed ({reason})",
                SignalJson = signalJson
            }, cancellationToken);
        }
    }

    private static StrategyDefinition[] ApplyRunSessionPolicy(BacktestRunConfig run, IReadOnlyCollection<StrategyDefinition> strategies)
    {
        return strategies
            .Select(strategy => strategy with
            {
                Session = strategy.Session with
                {
                    UseExtendedHours = run.Execution.AllowExtendedHoursTrading
                }
            })
            .ToArray();
    }

    private static string[] ResolveRequiredTimeframes(BacktestRunConfig run, IReadOnlyCollection<StrategyDefinition> strategies)
    {
        var required = new HashSet<string>(run.Intervals.Where(x => !String.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
        foreach (var strategy in strategies)
        {
            required.Add(strategy.Timeframe);
            required.Add(strategy.Execution.Timeframe);
            if (strategy.Confluence.Enabled)
            {
                required.Add(strategy.Confluence.Timeframe);
            }
        }

        return required.ToArray();
    }

    private static int FindFirstBarIndexAtOrAfter(IReadOnlyList<OhlcvBar> bars, DateTimeOffset timestamp)
    {
        for (var index = 0; index < bars.Count; index++)
        {
            if (bars[index].Timestamp >= timestamp)
            {
                return index;
            }
        }

        return bars.Count;
    }

    private static TimeSpan ParseTimeframe(string timeframe) => TimeframeParser.Parse(timeframe);

    private static string FormatLocalTime(DateTimeOffset timestamp)
    {
        try
        {
            var timezone = TimeZoneInfo.FindSystemTimeZoneById("Central Europe Standard Time");
            return TimeZoneInfo.ConvertTime(timestamp, timezone).ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return timestamp.LocalDateTime.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private static string BuildSignalAuditJson(
        TradeSignal signal,
        decimal relativeVolumeUsed,
        string relativeVolumeSource,
        decimal? calculatedRelativeVolume,
        decimal? slotRelativeVolume,
        decimal? sessionRelativeVolume,
        decimal? slotAverageVolume,
        decimal? cumulativeAverageVolume,
        decimal? averageSessionVolume,
        int relativeVolumeSampleCount)
    {
        return JsonSerializer.Serialize(new
        {
            signal,
            relativeVolumeUsed,
            relativeVolumeSource,
            calculatedRelativeVolume,
            slotRelativeVolume,
            sessionRelativeVolume,
            slotAverageVolume,
            cumulativeAverageVolume,
            averageSessionVolume,
            relativeVolumeSampleCount
        });
    }

    private static string ResolveExchangeTimezone(IReadOnlyCollection<StrategyDefinition> strategies)
    {
        return strategies
            .Select(strategy => strategy.Session.ExchangeTimezone)
            .FirstOrDefault(timezone => !String.IsNullOrWhiteSpace(timezone))
            ?? "America/New_York";
    }

    private static CandleStoreContext CreateCandleStoreContext(BacktestRunConfig run)
    {
        return new CandleStoreContext(
            String.IsNullOrWhiteSpace(run.Mode) ? "paper" : run.Mode,
            run.RunName,
            run.Provider);
    }

    private static bool IsEntryExposureOrder(ActiveBrokerOrder order)
    {
        return order.Side.Equals("buy", StringComparison.OrdinalIgnoreCase) &&
               IsOpenBrokerStatus(order.Status);
    }

    private static bool AllowsLong(StrategyDefinition strategy)
    {
        return strategy.Direction.Equals("long", StringComparison.OrdinalIgnoreCase) ||
            strategy.Direction.Equals("long_short", StringComparison.OrdinalIgnoreCase) ||
            strategy.Direction.Equals("both", StringComparison.OrdinalIgnoreCase);
    }

    private static bool AllowsShort(StrategyDefinition strategy)
    {
        return strategy.EntryRules.EnableShort &&
            (strategy.Direction.Equals("short", StringComparison.OrdinalIgnoreCase) ||
             strategy.Direction.Equals("long_short", StringComparison.OrdinalIgnoreCase) ||
             strategy.Direction.Equals("both", StringComparison.OrdinalIgnoreCase));
    }

    private static string ChoosePrimaryRejection(string longRejection, string shortRejection)
    {
        if (!longRejection.StartsWith("direction_", StringComparison.OrdinalIgnoreCase))
        {
            return longRejection;
        }

        if (!shortRejection.StartsWith("direction_", StringComparison.OrdinalIgnoreCase))
        {
            return shortRejection;
        }

        return longRejection;
    }

    private static bool IsOpenExposurePosition(BrokerPosition position)
    {
        return position.Qty != 0;
    }

    private static bool IsOpenBrokerStatus(string status)
    {
        return status.Equals("new", StringComparison.OrdinalIgnoreCase) ||
               status.Equals("accepted", StringComparison.OrdinalIgnoreCase) ||
               status.Equals("pending_new", StringComparison.OrdinalIgnoreCase) ||
               status.Equals("partially_filled", StringComparison.OrdinalIgnoreCase);
    }

    private static int ResolveWorkerCount(int configuredWorkerCount)
    {
        if (configuredWorkerCount > 0)
        {
            return configuredWorkerCount;
        }

        return Math.Max(1, Environment.ProcessorCount - 1);
    }

    private static decimal? ParseFinvizRelativeVolumeFilter(string filter)
    {
        if (String.IsNullOrWhiteSpace(filter))
        {
            return null;
        }

        var query = Uri.UnescapeDataString(filter);
        var match = System.Text.RegularExpressions.Regex.Match(
            query,
            @"sh_relvol_o(?<value>\d+(?:\.\d+)?)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(250));

        return match.Success &&
            Decimal.TryParse(
                match.Groups["value"].Value,
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value)
            ? value
            : null;
    }
}
