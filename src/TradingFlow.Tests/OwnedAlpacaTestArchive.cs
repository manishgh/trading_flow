using TradingFlow.Engine.Storage;

namespace TradingFlow.Tests;

/// <summary>Owns only synthetic fake-HTTP archives; never accepts a caller's data directory.</summary>
internal sealed class OwnedAlpacaTestArchive : IDisposable
{
    private readonly string ownerId = Guid.NewGuid().ToString("N");
    private readonly string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "trading-flow-alpaca-http-tests"));

    public string Root { get; }
    public IRawArchiveWriter Writer { get; }

    /// <summary>Allocates a unique synthetic archive owner for one test's using/finally lifetime.</summary>
    public OwnedAlpacaTestArchive()
    {
        Root = Path.Combine(parent, ownerId);
        Writer = new FileSystemRawArchiveWriter(new RawArchiveOptions(Root));
    }

    /// <summary>Deletes only the verified unique fixture child when the test completes or throws.</summary>
    public void Dispose()
    {
        var resolved = Path.GetFullPath(Root);
        if (!String.Equals(Path.GetDirectoryName(resolved), parent, StringComparison.OrdinalIgnoreCase) ||
            !String.Equals(Path.GetFileName(resolved), ownerId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to remove an archive outside this synthetic test owner.");
        }

        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}
