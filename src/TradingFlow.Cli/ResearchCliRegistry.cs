using TradingFlow.Data.Evidence.Governance;

namespace TradingFlow.Cli;

internal static class ResearchCliRegistry
{
    /// <summary>Finds the same local research registry as the Web host; no output folder becomes a second ledger.</summary>
    internal static SqliteResearchTrialRegistry Open()
    {
        var configured = Environment.GetEnvironmentVariable("TRADINGFLOW_DATA_ROOT");
        if (!String.IsNullOrWhiteSpace(configured)) return SqliteResearchTrialRegistry.OpenLocal(configured);
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var path = start; path is not null; path = Path.GetDirectoryName(path))
                if (File.Exists(Path.Combine(path, "TradingFlow.sln")))
                    return SqliteResearchTrialRegistry.OpenLocal(Path.Combine(path, "data"));
        throw new InvalidOperationException("Set TRADINGFLOW_DATA_ROOT to use the shared local research registry outside the repository.");
    }
}
