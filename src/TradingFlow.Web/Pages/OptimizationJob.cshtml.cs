using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services;

namespace TradingFlow.Web.Pages;

public sealed class OptimizationJobModel : PageModel
{
    private readonly OptimizationJobService jobs;

    public OptimizationJobModel(OptimizationJobService jobs)
    {
        this.jobs = jobs;
    }

    public OptimizationJobSnapshot Job { get; private set; } = null!;

    public IActionResult OnGet(Guid id)
    {
        var job = jobs.Get(id);
        if (job is null)
        {
            return RedirectToPage("/Backtests");
        }

        Job = job;
        return Page();
    }

    public IActionResult OnGetSnapshot(Guid id)
    {
        var job = jobs.Get(id);
        if (job is null)
        {
            return new JsonResult(new { success = false });
        }

        return new JsonResult(new
        {
            success = true,
            status = job.Status,
            currentStage = job.CurrentStage,
            startedAt = FormatLocal(job.StartedAt),
            finishedAt = FormatLocal(job.FinishedAt),
            errorMessage = job.ErrorMessage,
            events = job.Events,
            currentRun = job.CurrentRun,
            completedRuns = job.CompletedRuns,
            result = job.Result is null
                ? null
                : new
                {
                    metric = job.Result.Metric,
                    topRuns = job.Result.TopRuns.Select(run =>
                    {
                        var totalTrades = run.WinningTradeCount + run.LosingTradeCount;
                        var winRate = totalTrades == 0 ? 0 : Math.Round((decimal)run.WinningTradeCount / totalTrades * 100, 1);
                        return new
                        {
                            run.Rank,
                            run.ParameterValues,
                            run.MetricValue,
                            run.TotalReturnPct,
                            run.AverageDailyReturnPct,
                            run.NetProfit,
                            run.MaxDrawdownPct,
                            StrategyName = run.BacktestResult.Winner?.StrategyName,
                            TotalTrades = totalTrades,
                            WinRate = winRate
                        };
                    })
                }
        });
    }

    public IActionResult OnPostCancel(Guid id)
    {
        jobs.Cancel(id);
        return RedirectToPage(new { id });
    }

    public string FormatLocal(DateTimeOffset? timestamp)
    {
        return timestamp is null ? "-" : UiDisplayFormatter.FormatLocalTime(timestamp.Value);
    }
}
