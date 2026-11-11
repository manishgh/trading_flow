namespace TradingFlow.Data.Evidence;

public enum EvidenceCatalogOpenMode
{
    BootstrapNew = 1,
    OpenExisting = 2,
    // Opens an existing catalog for reading only: no schema migration, no journal-mode change and no full integrity scan.
    // A catalog that would need a migration is refused instead.
    ReadOnly = 3
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
