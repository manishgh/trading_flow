namespace TradingFlow.Data.Evidence.Governance;

public enum ResearchTrialRegistryOpenMode
{
    BootstrapNew = 1,
    OpenExisting = 2
}

public sealed class ResearchTrialRegistryOptions
{
    public ResearchTrialRegistryOptions(
        string databasePath,
        ResearchTrialRegistryOpenMode openMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!Enum.IsDefined(openMode))
        {
            throw new ArgumentOutOfRangeException(nameof(openMode));
        }

        DatabasePath = Path.GetFullPath(databasePath);
        OpenMode = openMode;
    }

    public string DatabasePath { get; }

    public ResearchTrialRegistryOpenMode OpenMode { get; }
}
