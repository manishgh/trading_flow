using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TradingFlow.Data.Evidence.Collection.SharedNews;

internal static class LocalPublicationFiles
{
    internal static string Root(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Shared news consumption currently requires Windows local-file locking and publication.");
        CollectionWire.Require(Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal), "A fixed local absolute root is required.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        CollectionWire.Require(root.Length <= 4096, "Shared news root exceeds its path budget.");
        CollectionWire.Require(root.IndexOf(':', 2) < 0 && new DriveInfo(Path.GetPathRoot(root)!).DriveType == DriveType.Fixed, "A fixed local drive is required.");
        Plain(root);
        return root;
    }

    internal static void Plain(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                CollectionWire.Require((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0, "Shared news paths cannot contain links or reparse points.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static byte[] Read(string path, int maximum, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Plain(path);
        CollectionWire.Require((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.Device)) == 0, "Shared news record must be a regular file.");
        using var stream = File.OpenRead(path);
        CollectionWire.Require(stream.Length is > 0 && stream.Length <= maximum, "Shared news file exceeds its byte budget.");
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (buffer.Length <= maximum)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(chunk, 0, Math.Min(chunk.Length, maximum + 1 - (int)buffer.Length));
            if (read == 0) break;
            buffer.Write(chunk, 0, read);
        }
        CollectionWire.Require(buffer.Length <= maximum, "Shared news file grew past its byte budget.");
        return buffer.ToArray();
    }

    internal static List<string> Entries(string path, int maximum, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Plain(path);
        if (!Directory.Exists(path))
        {
            CollectionWire.Require(!File.Exists(path), "Shared news directory is a file.");
            return [];
        }
        var entries = new List<string>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CollectionWire.Require(entries.Count < maximum, "Shared news directory exceeds its entry budget.");
            Plain(entry);
            entries.Add(entry);
        }
        return entries;
    }
    internal static bool Pending(string path) => Directory.Exists(path) && CollectionWire.Matches(Path.GetFileName(path), "\\.pending-[0-9a-f]{32}");
    internal static void Files(string path, params string[] names)
    {
        var entries = Entries(path, names.Length);
        CollectionWire.Require(entries.Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal).SetEquals(names) && entries.All(File.Exists),
            "Shared news bundle has missing or unexpected files.");
    }

    internal static FileStream Lock(string path, bool create)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        Plain(path);
        var stream = new FileStream(path, create ? FileMode.OpenOrCreate : FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        try
        {
            if (create && stream.Length == 0) { stream.WriteByte(0); stream.Flush(true); }
            CollectionWire.Require(stream.Length == 1, "Shared news lock file must contain exactly one byte.");
            stream.Lock(0, 1);
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    internal static void DirectoryPath(string path)
    {
        Plain(path);
        if (Directory.Exists(path)) return;
        var parent = Path.GetDirectoryName(path) ?? throw new InvalidDataException("Missing local parent directory.");
        DirectoryPath(parent);
        Publish(path, new Dictionary<string, byte[]>());
    }
    internal static void Publish(string target, IReadOnlyDictionary<string, byte[]> files, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Plain(target);
        CollectionWire.Require(!Directory.Exists(target) && !File.Exists(target), "Refusing to overwrite shared news publication.");
        var staging = Path.Combine(Path.GetDirectoryName(target)!, ".pending-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        foreach (var (name, bytes) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new FileStream(Path.Combine(staging, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(bytes);
            stream.Flush(true);
        }
        // Preserve staging on interruption; it is never a committed import or acknowledgement.
        cancellationToken.ThrowIfCancellationRequested();
        if (!MoveFileEx(staging, target, 0x8)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existing, string destination, uint flags);
}
