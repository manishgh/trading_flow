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

    public void OnGet(Guid id)
    {
        Job = jobs.Get(id);
    }
}
