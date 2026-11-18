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

    /// <summary>
    /// Locates a stored absolute path that was recorded while data lived in the repository's default data folder
    /// (<c>&lt;repository&gt;/data</c>) after the operator moved data to a configured <see cref="DataRoot"/>: the same
    /// relative location under <see cref="DataRoot"/> is returned. Relative paths, paths already under
    /// <see cref="DataRoot"/>, paths outside the default folder and every path when the two folders are the same are
    /// returned unchanged. Pure string mapping: no file is probed, nothing stored is rewritten, and callers still apply
    /// their own confinement and hash checks.
    /// </summary>
    public string LocateStored(string path)
    {
        if (String.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return path;
        var defaultRoot = Path.Combine(RepositoryRoot, "data");
        if (RelativeInside(DataRoot, path) is not null || RelativeInside(defaultRoot, DataRoot) == ".") return path;
        return RelativeInside(defaultRoot, path) switch
        {
            null => path,
            "." => Path.GetFullPath(DataRoot),
            var relative => Path.Combine(Path.GetFullPath(DataRoot), relative)
        };
    }

    /// <summary>
    /// The path of <paramref name="path"/> relative to <paramref name="root"/> after both are made absolute ("." when
    /// they are the same folder), or null when it lies outside. Comparison follows the platform's file-name rules.
    /// </summary>
    public static string? RelativeInside(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
            ? null
            : relative;
    }
}
