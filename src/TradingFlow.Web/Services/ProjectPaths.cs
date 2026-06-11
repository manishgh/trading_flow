namespace TradingFlow.Web.Services;

public sealed record ProjectPaths(string RepositoryRoot)
{
    public string ConfigsRoot => Path.Combine(RepositoryRoot, "configs");
    public string BacktestConfigsRoot => Path.Combine(ConfigsRoot, "backtest");
    public string PaperConfigsRoot => Path.Combine(ConfigsRoot, "paper");
    public string StrategiesRoot => Path.Combine(ConfigsRoot, "strategies");
    public string GeneratedBacktestConfigsRoot => Path.Combine(BacktestConfigsRoot, "ui-runs");

    public string ResolveRepositoryPath(string path)
    {
        return Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(RepositoryRoot, path));
    }
}
