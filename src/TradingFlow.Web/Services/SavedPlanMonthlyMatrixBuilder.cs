using System.Globalization;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Web.Models;

namespace TradingFlow.Web.Services;

/// <summary>
/// Builds the read-only Strategy × NY-session month comparison matrix for one saved backtest plan from immutable
/// <see cref="MonthlyRunResult"/> records. Never computes accumulated multi-month or pooled multi-strategy/cohort returns.
/// </summary>
public static class SavedPlanMonthlyMatrixBuilder
{
    private const int MaxTopRejections = 8;

    /// <summary>Projects all jobs belonging to <paramref name="plan"/> into distinct sorted months and per-job strategy/batch rows.</summary>
    public static SavedPlanMonthlyMatrixResponse Build(BacktestPlanResponse plan, IReadOnlyList<BacktestJobSnapshot> jobs)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(jobs);

        var rows = jobs
            .Where(job => job.Cohort?.PlanId == plan.PlanId)
            .Select(job => BuildRow(plan, job))
            .OrderBy(row => row.StrategyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.SemanticVersion, StringComparer.Ordinal)
            .ThenBy(row => row.CohortName, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(row => row.CreatedAtUtc)
            .ThenBy(row => row.JobId)
            .ToArray();

        var months = rows
            .SelectMany(row => row.Cells.Select(cell => cell.Month))
            .Where(month => !String.IsNullOrWhiteSpace(month))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new SavedPlanMonthlyMatrixResponse(
            ContractVersions.VersionOne,
            plan.PlanId,
            plan.Run.RunName,
            months,
            rows);
    }

    /// <summary>True for a calendar month key "yyyy-MM" (month 01-12), the form the monthly records use.</summary>
    public static bool IsMonthKey([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? month) =>
        month is { Length: 7 } && DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <summary>The strategy a job ran, from its cohort, its monthly records, its results, or (last) its run name.</summary>
    public static string ResolveStrategyId(BacktestJobSnapshot job) =>
        job.Cohort?.Strategy.StrategyId
        ?? job.MonthlyResults?.FirstOrDefault()?.Strategy.StrategyId
        ?? job.Result?.StrategyResults?.FirstOrDefault()?.StrategyId
        ?? job.RunName;

    /// <summary>
    /// Lists one job's completed trades that exited in New York month <paramref name="month"/> (yyyy-MM), newest first,
    /// up to <paramref name="max"/>, for the drawer opened on that month. Loaded on demand so the matrix stays small;
    /// monthly % returns still come only from the stored monthly records.
    /// </summary>
    public static IReadOnlyList<SavedPlanMonthlyTradeSample> BuildMonthTrades(BacktestJobSnapshot job, string month, int max = 100) =>
        BuildSampleTrades(job, ResolveStrategyId(job), int.MaxValue)
            .Where(trade => trade.Month == month)
            .Take(max)
            .ToArray();

    // Projects one cohort/strategy/batch job into a matrix row with immutable monthly cells, canonical hash, and diagnostics.
    private static SavedPlanMonthlyMatrixRow BuildRow(BacktestPlanResponse plan, BacktestJobSnapshot job)
    {
        var firstMonthly = job.MonthlyResults?.FirstOrDefault();
        var cohortName = job.Cohort?.CohortName ?? firstMonthly?.CohortName ?? job.RunName;
        var strategyId = ResolveStrategyId(job);
        var semanticVersion = job.Cohort?.Strategy.SemanticVersion
            ?? firstMonthly?.Strategy.SemanticVersion
            ?? String.Empty;
        var contentSha256 = job.Cohort?.Strategy.ContentSha256
            ?? firstMonthly?.Strategy.ContentSha256
            ?? String.Empty;
        var strategyName = ResolveStrategyName(plan, job, strategyId, semanticVersion);
        var cells = BuildCells(job);
        var topRejections = BuildTopRejections(job, strategyId);
        var unavailableReason = ResolveMonthlyUnavailableReason(job, cells);

        return new SavedPlanMonthlyMatrixRow(
            job.JobId,
            cohortName,
            strategyId,
            strategyName,
            semanticVersion,
            contentSha256,
            job.CreatedAt.ToUniversalTime(),
            job.Status,
            cells,
            topRejections,
            unavailableReason,
            SampleTrades: null);
    }

    // Surfaces a concrete explanation whenever a job has no monthly cells (failed, cancelled, pending, or writer rejected).
    private static string? ResolveMonthlyUnavailableReason(BacktestJobSnapshot job, IReadOnlyList<SavedPlanMonthlyMatrixCell> cells)
    {
        if (cells.Count > 0) return job.MonthlyUnavailableReason;
        if (!String.IsNullOrWhiteSpace(job.MonthlyUnavailableReason)) return job.MonthlyUnavailableReason;
        if (!String.IsNullOrWhiteSpace(job.ErrorMessage)) return job.ErrorMessage;
        if (job.Status is "queued" or "running" or "cancelling") return $"Execution {job.Status}; monthly evidence pending.";
        return $"No monthly evidence recorded (execution {job.Status}).";
    }

    // Resolves the readable strategy name from immutable monthly metadata, saved plan settings, or job results.
    private static string ResolveStrategyName(BacktestPlanResponse plan, BacktestJobSnapshot job, string strategyId, string semanticVersion) =>
        job.MonthlyResults?.FirstOrDefault()?.StrategyName
        ?? plan.ResolvedSettings?.Strategies
            .FirstOrDefault(item =>
                item.Identity.StrategyId.Equals(strategyId, StringComparison.OrdinalIgnoreCase) &&
                (String.IsNullOrEmpty(semanticVersion) || item.Identity.SemanticVersion.Equals(semanticVersion, StringComparison.Ordinal)))
            ?.Name
        ?? job.Result?.StrategyResults?
            .FirstOrDefault(item => item.StrategyId.Equals(strategyId, StringComparison.OrdinalIgnoreCase))
            ?.StrategyName
        ?? job.Result?.StrategyResults?.FirstOrDefault()?.StrategyName
        ?? strategyId;

    // Copies immutable NY-session monthly records verbatim in chronological month order without aggregating across months.
    private static IReadOnlyList<SavedPlanMonthlyMatrixCell> BuildCells(BacktestJobSnapshot job)
    {
        if (job.MonthlyResults is not { Count: > 0 } monthly) return [];
        return monthly
            .OrderBy(item => item.Month, StringComparer.Ordinal)
            .Select(item => new SavedPlanMonthlyMatrixCell(
                item.Month,
                item.ReturnPct,
                item.OpeningEquity,
                item.ClosingEquity,
                item.MaxDrawdownPct,
                item.ClosedTrades,
                item.OpenPositions,
                item.PartialMonth,
                item.ValuationBasis,
                item.UnavailableReason))
            .ToArray();
    }

    // Extracts the highest-frequency rejection codes recorded for this single job's strategy diagnostic.
    private static IReadOnlyList<SavedPlanRejectionSummary> BuildTopRejections(BacktestJobSnapshot job, string strategyId)
    {
        var diagnostics = job.Result?.Diagnostics;
        if (diagnostics is not { Count: > 0 }) return [];
        var match = diagnostics.FirstOrDefault(item => item.StrategyId.Equals(strategyId, StringComparison.OrdinalIgnoreCase))
            ?? (diagnostics.Count == 1 ? diagnostics[0] : null);
        if (match?.RejectionCounts is not { Count: > 0 } counts) return [];

        return counts
            .Where(pair => pair.Value > 0)
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(MaxTopRejections)
            .Select(pair => new SavedPlanRejectionSummary(pair.Key, pair.Value))
            .ToArray();
    }

    // Projects this strategy's completed trades, newest first and up to max, stamped with NY exit month and realised R-multiple; never pools or aggregates returns.
    private static IReadOnlyList<SavedPlanMonthlyTradeSample> BuildSampleTrades(BacktestJobSnapshot job, string strategyId, int max)
    {
        var completedTrades = job.Result?.CompletedTrades;
        if (completedTrades is not { Count: > 0 }) return [];

        var strategyResults = job.Result?.StrategyResults;
        var strategyResult = strategyResults?
            .FirstOrDefault(item => item.StrategyId.Equals(strategyId, StringComparison.OrdinalIgnoreCase))
            ?? (strategyResults is { Count: 1 } ? strategyResults[0] : null);
        var valuationBasis = strategyResult?.PortfolioState?.ValuationBasis;
        var singleStrategyJob = strategyResults is null || strategyResults.Count <= 1;

        return completedTrades
            .Where(trade =>
                trade.StrategyId.Equals(strategyId, StringComparison.OrdinalIgnoreCase) ||
                (String.IsNullOrEmpty(trade.StrategyId) && singleStrategyJob))
            .OrderByDescending(trade => trade.ExitTimestamp)
            .ThenBy(trade => trade.Ticker, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(trade => new SavedPlanMonthlyTradeSample(
                MonthlyBacktestPresentation.ExitSessionDate(trade, valuationBasis)
                    .ToString("yyyy-MM", CultureInfo.InvariantCulture),
                trade.Ticker,
                trade.Direction,
                trade.EntryPrice,
                trade.ExitPrice,
                trade.EntryTimestamp.ToUniversalTime(),
                trade.ExitTimestamp.ToUniversalTime(),
                trade.ExitReason,
                trade.NetProfit,
                BacktestDerivedMetrics.RMultiple(trade)))
            .ToArray();
    }
}
