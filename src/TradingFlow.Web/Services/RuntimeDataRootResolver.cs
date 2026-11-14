namespace TradingFlow.Web.Services;

/// <summary>
/// Resolves the root for mutable runtime state. UI tests default to an isolated
/// repository-local tree so migrations, identity keys, and journals cannot touch
/// the operator's real data when no explicit test root is supplied.
/// </summary>
public static class RuntimeDataRootResolver
{
    /// <summary>Configuration key (for example in appsettings.local.json) naming the operator's data folder.</summary>
    public const string DataRootSettingKey = "TradingFlow:DataRoot";

    /// <summary>
    /// The configured data folder before resolution: the TRADINGFLOW_DATA_ROOT environment variable when set, otherwise
    /// the <see cref="DataRootSettingKey"/> setting. In UI-test mode the setting is ignored, so an operator's local
    /// settings file can never point a test at real data; tests pass their isolated root through the variable.
    /// </summary>
    public static string? SelectConfigured(string? environmentValue, string? settingValue, bool uiTestMode) =>
        !String.IsNullOrWhiteSpace(environmentValue) ? environmentValue
            : uiTestMode || String.IsNullOrWhiteSpace(settingValue) ? null
            : settingValue;

    /// <summary>
    /// The full data folder: the configured folder (a relative one is taken from the repository folder, not the
    /// process's current folder), or the repository's own data folder (UI tests: the isolated test tree).
    /// </summary>
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
                : Path.Combine(repositoryRoot, configuredDataRoot.Trim()));
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
