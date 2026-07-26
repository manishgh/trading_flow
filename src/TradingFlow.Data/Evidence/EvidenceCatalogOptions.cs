namespace TradingFlow.Data.Evidence;

public enum EvidenceCatalogOpenMode
{
    BootstrapNew = 1,
    OpenExisting = 2
}

public sealed class EvidenceCatalogOptions
{
    public EvidenceCatalogOptions(
        string databasePath,
        EvidenceCatalogOpenMode openMode)
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

    public EvidenceCatalogOpenMode OpenMode { get; }
}
