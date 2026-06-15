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

    private static bool IsRepositoryRoot(string? path)
    {
        return !String.IsNullOrWhiteSpace(path) &&
            Directory.Exists(Path.Combine(path, "configs")) &&
            File.Exists(Path.Combine(path, "TradingFlow.sln"));
    }
}
