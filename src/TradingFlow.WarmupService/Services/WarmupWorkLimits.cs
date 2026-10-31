using TradingFlow.Engine.Market;
using TradingFlow.WarmupService.Models;

namespace TradingFlow.WarmupService.Services;

/// <summary>Applies host resource limits before accepting or collecting one warmup request.</summary>
public static class WarmupWorkLimits
{
    /// <summary>Validates symbols/history and a conservative all-hours bar estimate without starting provider work.</summary>
    public static void Validate(WarmupJobRequest job, WarmupOptions options)
    {
        if (options.MaximumTickersPerJob <= 0 || options.MaximumHistoryDays <= 0 || options.MaximumBarsPerJob <= 0)
            throw new InvalidOperationException("Warmup symbol, history and bar budgets must be positive.");
        var intents = job.FrozenIntents ?? throw new InvalidOperationException("Warmup settings must be resolved before work acceptance.");
        if (intents.Count == 0 || intents.Count > options.MaximumTickersPerJob)
            throw new ArgumentException($"Warmup selected {intents.Count} stocks; the configured per-job limit is {options.MaximumTickersPerJob}.");
        if (intents.Select(intent => intent.Ticker).Distinct(StringComparer.OrdinalIgnoreCase).Count() != intents.Count)
            throw new ArgumentException("Warmup contains duplicate stock settings.");
        long estimate = 0;
        foreach (var intent in intents)
        {
            if (intent.WarmupDays <= 0 || intent.WarmupDays > options.MaximumHistoryDays ||
                intent.NewsLookbackDays < 0 || intent.NewsLookbackDays > options.MaximumHistoryDays || intent.Timeframes.Count == 0)
                throw new ArgumentException($"Warmup history must be within the configured {options.MaximumHistoryDays}-day limit, with at least one interval.");
            try
            {
                _ = job.QueuedAtUtc.AddDays(-intent.WarmupDays);
                _ = job.QueuedAtUtc.AddDays(-intent.NewsLookbackDays);
            }
            catch (ArgumentOutOfRangeException)
            { throw new ArgumentException("Warmup history would exceed the supported date range."); }
            foreach (var timeframe in intent.Timeframes)
            {
                TimeSpan duration;
                try { duration = TimeframeParser.Parse(timeframe); }
                catch (Exception error) when (error is NotSupportedException or OverflowException or ArgumentException)
                { throw new ArgumentException($"Unsupported warmup interval: {timeframe}.", error); }
                if (duration <= TimeSpan.Zero) throw new ArgumentException("Warmup intervals must have positive duration.");
                var perDay = Math.Max(1, (long)Math.Ceiling(TimeSpan.FromDays(1).Ticks / (decimal)duration.Ticks));
                estimate = checked(estimate + ((long)intent.WarmupDays + 1) * perDay);
                if (estimate > options.MaximumBarsPerJob)
                    throw new ArgumentException($"Warmup could request {estimate} bars, above the configured {options.MaximumBarsPerJob} per-job budget. Reduce stocks/history or review the host resource settings.");
            }
        }
    }
}
