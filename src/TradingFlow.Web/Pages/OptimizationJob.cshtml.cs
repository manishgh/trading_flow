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
            return RedirectToPage("/Optimize");
        }

        Job = job;
        return Page();
    }
}
