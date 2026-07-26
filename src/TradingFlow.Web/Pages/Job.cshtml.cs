using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services;

namespace TradingFlow.Web.Pages;

public sealed class JobModel : PageModel
{
    private readonly BacktestJobService jobs;

    public JobModel(BacktestJobService jobs)
    {
        this.jobs = jobs;
    }

    public BacktestJobSnapshot? Job { get; private set; }

    public IActionResult OnGet(Guid id)
    {
        Job = jobs.Get(id);
        return Job is null ? RedirectToPage("/Backtests") : Page();
    }

    public IActionResult OnGetSnapshot(Guid id)
    {
        var job = jobs.Get(id);
        if (job is null)
        {
            return NotFound();
        }

        var elapsed = (job.FinishedAt ?? DateTimeOffset.UtcNow) - (job.StartedAt ?? job.CreatedAt);
        var progressPercent = job.TotalTickerCount <= 0
            ? 0m
            : Math.Clamp((decimal)job.CompletedTickerCount / job.TotalTickerCount * 100m, 0m, 100m);
        return new JsonResult(new
        {
            job.JobId,
            job.Status,
            job.CurrentStage,
            job.CompletedTickerCount,
            job.TotalTickerCount,
            ProgressPercent = Math.Round(progressPercent, 1),
            Elapsed = elapsed.ToString(@"hh\:mm\:ss"),
            StartedAt = FormatTime(job.StartedAt),
            FinishedAt = FormatTime(job.FinishedAt),
            job.ErrorMessage,
            CanCancel = job.Status is "queued" or "running" or "cancelling",
            Events = job.Events.TakeLast(40),
            StrategyGroups = (job.StrategyGroups ?? [])
                .Select(group => new
                {
                    group.StrategyName,
                    group.Status,
                    group.CompletedTickerCount,
                    group.TotalTickerCount,
                    group.CurrentTicker,
                    ProgressPercent = group.TotalTickerCount <= 0
                        ? 0m
                        : Math.Round(Math.Clamp((decimal)group.CompletedTickerCount / group.TotalTickerCount * 100m, 0m, 100m), 1),
                    RecentEvents = group.RecentEvents.TakeLast(4)
                })
        });
    }

    public IActionResult OnPostCancel(Guid id)
    {
        var outcome = jobs.CancelJob(id);
        return outcome switch
        {
            BacktestCancellationOutcome.NotFound => NotFound(),
            _ => RedirectToPage(new { id })
        };
    }

    private static string FormatTime(DateTimeOffset? timestamp)
    {
        return timestamp is null ? "-" : UiDisplayFormatter.FormatLocalTime(timestamp.Value);
    }
}
