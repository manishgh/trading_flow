namespace TradingFlow.Tests;

internal static class TestRepository
{
    public static string FindRoot()
    {
        var explicitRoot = Environment.GetEnvironmentVariable("TRADINGFLOW_REPO_ROOT");
        if (IsRepositoryRoot(explicitRoot))
        {
            return Path.GetFullPath(explicitRoot!);
        }

        foreach (var startPath in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(startPath);
            while (directory is not null)
            {
                if (IsRepositoryRoot(directory.FullName))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not find trading_flow repository root.");
    }

    /// <summary>
    /// The strategies the operator switched off on 18 November 2026 that tests still use as vehicles for shared machinery:
    /// the same-day opening-range versions (set aside) and the ETF trend core with its buy-and-hold control (blocked: index
    /// and sector funds now only judge the trend). ETF rotation stays disabled; technical.bollinger-squeeze-breakout@1.0.0
    /// is enabled in the operator catalog for research backtests.
    /// </summary>
    private static readonly HashSet<string> SetAsideStrategyIds =
        ["technical.opening-range-breakout", "etf.trend-10m", "etf.buy-and-hold-control"];

    /// <summary>
    /// Writes a test-owned copy of the retired/set-aside fixture catalog into <paramref name="folder"/> with the strategies
    /// set aside on 18 November 2026 enabled, and returns its path. Tests that exercise the shared opening-range, intraday,
    /// month-end and research-screen machinery through those recipes pair this copy with <see cref="RetiredStrategyFixtures.Root"/>
    /// instead of keeping disabled strategies in the live operator catalog. Every other entry, path and content hash is unchanged.
    /// </summary>
    public static string SetAsideStrategiesEnabledCatalog(string folder)
    {
        var catalog = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(RetiredStrategyFixtures.CatalogPath))!;
        EnableSetAsideStrategies(catalog);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "strategy-catalog.json");
        File.WriteAllText(path, catalog.ToJsonString());
        return path;
    }

    /// <summary>Removes the switched-off flag from the set-aside entries of a parsed catalog (see <see cref="SetAsideStrategiesEnabledCatalog"/>).</summary>
    public static void EnableSetAsideStrategies(System.Text.Json.Nodes.JsonNode catalog)
    {
        foreach (var entry in catalog["strategies"]!.AsArray())
            if (SetAsideStrategyIds.Contains((string?)entry!["strategyId"] ?? ""))
            {
                entry.AsObject().Remove("enabled");
                entry.AsObject().Remove("disabledReason");
            }
    }

    private static bool IsRepositoryRoot(string? path)
    {
        return !String.IsNullOrWhiteSpace(path) &&
            Directory.Exists(Path.Combine(path, "configs")) &&
            File.Exists(Path.Combine(path, "TradingFlow.sln"));
    }
}
