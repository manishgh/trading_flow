using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingFlow.Domain.Research;
using TradingFlow.Web.Services;

namespace TradingFlow.Web.Pages;

/// <summary>Projects registry metadata only; this page has no market, order, or job dependency.</summary>
public sealed class ResearchLedgerModel(IResearchStartRegistry registry, ProjectPaths paths, ILogger<ResearchLedgerModel> logger) : PageModel
{
    private static readonly IReadOnlyDictionary<string, string> Documents = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["retired-2026-11-07"] = "retired-strategy-artifacts-2026-11-07.md",
        ["retired-2026-11-13"] = "retired-strategy-artifacts-2026-11-13.md",
        ["success-policy"] = "success-definition-draft-2026-11-14.md"
    };

    public ResearchLedger Ledger { get; private set; } = new([], []);
    public string? Error { get; private set; }

    /// <summary>Reads canonical records in place, with an explicit distinction between absent and failed evidence.</summary>
    public async Task OnGetAsync(CancellationToken token)
    {
        try
        {
            Ledger = await registry.ReadLedgerAsync(token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Research ledger evidence could not be verified.");
            Error = "The registry records could not be verified. Trial counts and holdout state are unavailable until the registry can be read.";
        }
    }

    /// <summary>Serves only the explicitly linked published research notes as plain text.</summary>
    public IActionResult OnGetDocument(string name)
    {
        if (!Documents.TryGetValue(name ?? String.Empty, out var fileName)) return NotFound();
        var path = Path.Combine(paths.RepositoryRoot, "docs", "research", fileName);
        return System.IO.File.Exists(path) ? PhysicalFile(path, "text/plain; charset=utf-8") : NotFound();
    }

    /// <summary>Only a declared, verified holdout can be shown as unopened.</summary>
    public static string HoldoutLabel(ResearchHoldoutState? state) => state switch
    {
        ResearchHoldoutState.Unopened => "Unopened in this registry",
        ResearchHoldoutState.Consumed => "Reserved and consumed",
        ResearchHoldoutState.Completed => "Completed",
        _ => "No verified holdout declaration"
    };
}
