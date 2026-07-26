namespace TradingFlow.Data.Evidence.Governance;

public enum StrategyPromotionRegistryOpenMode
{
    BootstrapNew = 1,
    OpenExisting = 2
}

public sealed class StrategyPromotionRegistryOptions
{
    public StrategyPromotionRegistryOptions(
        string databasePath,
        StrategyPromotionRegistryOpenMode openMode)
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

    public StrategyPromotionRegistryOpenMode OpenMode { get; }
}
