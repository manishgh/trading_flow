namespace TradingFlow.Data.Evidence;

public sealed class ImmutableArtifactStoreOptions
{
    public ImmutableArtifactStoreOptions(
        string rootPath,
        IEnumerable<string>? forbiddenRoots = null,
        TimeSpan? temporaryDirectoryGracePeriod = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = Path.GetFullPath(rootPath);
        var forbidden = (forbiddenRoots ?? Array.Empty<string>())
            .Select(Path.GetFullPath)
            .ToArray();
        if (forbidden.Any(path =>
                IsSameOrChild(RootPath, path) ||
                IsSameOrChild(path, RootPath)))
        {
            throw new ArgumentException(
                "The immutable evidence root must be separate from operational storage.",
                nameof(rootPath));
        }

        TemporaryDirectoryGracePeriod =
            temporaryDirectoryGracePeriod ?? TimeSpan.FromHours(1);
        if (TemporaryDirectoryGracePeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(temporaryDirectoryGracePeriod));
        }
    }

    public string RootPath { get; }

    public TimeSpan TemporaryDirectoryGracePeriod { get; }

    private static bool IsSameOrChild(string candidate, string parent)
    {
        var relative = Path.GetRelativePath(parent, candidate);
        return relative == "." ||
               (!relative.StartsWith("..", StringComparison.Ordinal) &&
                !Path.IsPathRooted(relative));
    }
}
