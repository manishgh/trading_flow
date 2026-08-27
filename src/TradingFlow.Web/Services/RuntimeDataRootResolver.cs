namespace TradingFlow.Web.Services;

/// <summary>
/// Resolves the root for mutable runtime state. UI tests default to an isolated
/// repository-local tree so migrations, identity keys, and journals cannot touch
/// the operator's real data when no explicit test root is supplied.
/// </summary>
public static class RuntimeDataRootResolver
{
    public static string Resolve(
        string repositoryRoot,
        bool uiTestMode,
        string? configuredDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var uiTestRoot = Path.GetFullPath(Path.Combine(repositoryRoot, ".tmp", "ui-tests"));
        var fallback = uiTestMode ? uiTestRoot : Path.Combine(repositoryRoot, "data");
        var resolved = Path.GetFullPath(
            String.IsNullOrWhiteSpace(configuredDataRoot)
                ? fallback
                : configuredDataRoot.Trim());
        if (uiTestMode &&
            !resolved.Equals(uiTestRoot, StringComparison.OrdinalIgnoreCase) &&
            !resolved.StartsWith(uiTestRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"UI-test data root '{resolved}' must remain inside isolated root '{uiTestRoot}'.");
        }

        return resolved;
    }
}
