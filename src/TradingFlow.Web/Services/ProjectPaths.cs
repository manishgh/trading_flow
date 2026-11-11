namespace TradingFlow.Web.Services;

public sealed record ProjectPaths(string RepositoryRoot, string DataRoot, string CacheRoot)
{
    public ProjectPaths(string repositoryRoot)
        : this(repositoryRoot, Path.Combine(repositoryRoot, "data"), Path.Combine(repositoryRoot, "data", "cache"))
    {
    }

    public string ConfigsRoot => Path.Combine(RepositoryRoot, "configs");
    public string BacktestConfigsRoot => Path.Combine(ConfigsRoot, "backtest");
    public string PaperConfigsRoot => Path.Combine(ConfigsRoot, "paper");
    public string StrategiesRoot => Path.Combine(ConfigsRoot, "strategies");
    public string GeneratedBacktestConfigsRoot => Path.Combine(DataRoot, "runtime", "configs", "backtest");
    public string GeneratedPaperConfigsRoot => Path.Combine(DataRoot, "runtime", "configs", "paper");
    public string EvidenceRoot => Path.Combine(DataRoot, "research", "evidence");
    public string EvidenceCatalogPath => Path.Combine(EvidenceRoot, "catalog.db");
    public string EvidenceObjectRoot => Path.Combine(EvidenceRoot, "objects");
    // Published research screens (research-daily-screen output folders holding screen-manifest.json).
    public string ResearchScreensRoot => Path.Combine(DataRoot, "research", "screens");

    public string ResolveRepositoryPath(string path)
    {
        return Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(RepositoryRoot, path));
    }
}
