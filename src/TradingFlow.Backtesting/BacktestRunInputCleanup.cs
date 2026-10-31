using System.Security.Cryptography;
using System.Text.Json;

namespace TradingFlow.Backtesting;

/// <summary>Exact file bytes independently classified as disposable or protected within one terminal workspace.</summary>
public sealed record BacktestRunInputCleanupFile(string RelativePath, string Sha256, long ByteLength);

/// <summary>Quant audit permission to remove enumerated generated inputs; this never approves core-data deletion or trading.</summary>
public sealed record BacktestRunInputCleanupReview(
    int SchemaVersion,
    string WorkspaceManifestPath,
    string WorkspaceManifestSha256,
    string Reviewer,
    DateTimeOffset ReviewedAtUtc,
    bool ApprovedForInputCleanup,
    string Rationale,
    IReadOnlyList<BacktestRunInputCleanupFile>? DisposableFiles = null,
    IReadOnlyList<BacktestRunInputCleanupFile>? ProtectedFiles = null);

/// <summary>Durable ownership and terminal-result identity retained beside, never inside, the disposable spool.</summary>
internal sealed record BacktestRunWorkspaceManifest(
    int SchemaVersion, string InputRoot, string ResultOwner, string ResultPath, string RawRoot,
    string Outcome, DateTimeOffset? FinishedAtUtc, string? ResultSha256);

/// <summary>Removes only reviewed generated files, preserving normalized originals and all result evidence in place.</summary>
public static class BacktestRunInputCleanup
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Preflights every file before deleting reviewed copies; late changes stop further cleanup and require a fresh review.</summary>
    public static async Task<string> CleanupAsync(string reviewPath, CancellationToken cancellationToken = default)
    {
        reviewPath = Path.GetFullPath(reviewPath);
        EnsureNoReparseAncestors(reviewPath);
        using var reviewLease = File.Open(reviewPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var review = await JsonSerializer.DeserializeAsync<BacktestRunInputCleanupReview>(reviewLease, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("A saved independent quant cleanup review is required.");
        ValidateReview(review);
        var manifestPath = Path.GetFullPath(review.WorkspaceManifestPath);
        EnsureNoReparseAncestors(manifestPath);
        using var manifestLease = File.Open(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var manifestSha256 = Convert.ToHexStringLower(SHA256.HashData(manifestLease));
        if (!manifestSha256.Equals(review.WorkspaceManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The quant review does not match this terminal workspace manifest.");
        manifestLease.Position = 0;
        var manifest = await JsonSerializer.DeserializeAsync<BacktestRunWorkspaceManifest>(manifestLease, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The workspace ownership manifest is empty.");
        ValidateOwnership(manifest, manifestPath);
        if (manifest.Outcome is not ("completed" or "failed" or "cancelled") || manifest.FinishedAtUtc is null ||
            review.ReviewedAtUtc < manifest.FinishedAtUtc.Value)
            throw new InvalidDataException("Cleanup requires a quant review made after this execution reached a terminal outcome.");
        if (IsWithin(reviewPath, manifest.InputRoot))
            throw new InvalidDataException("Quant review evidence must remain outside the disposable inputs.");
        using var resultLease = OpenReviewedResult(manifest);
        var inputRoot = Path.GetFullPath(manifest.InputRoot);
        if (!Directory.Exists(inputRoot))
            throw new DirectoryNotFoundException("The reviewed input directory no longer exists; no deletion was attempted.");
        var disposable = ResolveReviewedFiles(inputRoot, review.DisposableFiles!, disposable: true);
        var protectedFiles = ResolveReviewedFiles(inputRoot, review.ProtectedFiles!, disposable: false);
        if (disposable.Keys.Any(protectedFiles.ContainsKey))
            throw new InvalidDataException("A file cannot be both disposable and protected.");
        var expected = new Dictionary<string, BacktestRunInputCleanupFile>(disposable, PathComparer);
        foreach (var entry in protectedFiles) expected.Add(entry.Key, entry.Value);
        using var protection = new ProtectedFileLeases();
        var directories = PreflightInventory(inputRoot, expected, protectedFiles, protection, cancellationToken);
        DeleteReviewedCopies(inputRoot, disposable, protectedFiles, protection, directories, cancellationToken);
        return inputRoot;
    }

    /// <summary>Writes pending/terminal ownership outside the generated spool; malformed ownership fails without deleting anything.</summary>
    internal static void WriteManifest(BacktestRunWorkspaceManifest manifest)
    {
        var path = Path.GetFullPath(manifest.InputRoot) + ".workspace.json";
        ValidateOwnership(manifest, path);
        EnsureNoReparseAncestors(path);
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, JsonOptions));
    }

    /// <summary>Hashes an existing result under a read lease so terminal ownership records its exact bytes.</summary>
    internal static string FileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>Requires a fresh schema-2 approval with complete file classifications; schema-1 evidence never authorizes deletion.</summary>
    private static void ValidateReview(BacktestRunInputCleanupReview review)
    {
        if (review.SchemaVersion != 2 || review.DisposableFiles is null || review.ProtectedFiles is null ||
            !review.ApprovedForInputCleanup ||
            String.IsNullOrWhiteSpace(review.WorkspaceManifestPath) ||
            String.IsNullOrWhiteSpace(review.WorkspaceManifestSha256) ||
            String.IsNullOrWhiteSpace(review.Reviewer) || String.IsNullOrWhiteSpace(review.Rationale) ||
            review.ReviewedAtUtc == default || review.ReviewedAtUtc.Offset != TimeSpan.Zero ||
            review.ReviewedAtUtc > DateTimeOffset.UtcNow)
            throw new InvalidDataException("Cleanup requires an approved schema-2 independent quant review with complete disposable/protected file inventories, identity, UTC time and rationale.");
    }

    /// <summary>Requires an exact generated name and direct result owner, excluding raw data and published result paths from the spool.</summary>
    private static void ValidateOwnership(BacktestRunWorkspaceManifest manifest, string manifestPath)
    {
        var root = Path.GetFullPath(manifest.InputRoot);
        var owner = Path.GetFullPath(manifest.ResultOwner);
        var result = Path.GetFullPath(manifest.ResultPath);
        var raw = Path.GetFullPath(manifest.RawRoot);
        var name = Path.GetFileName(root);
        const string prefix = ".run-inputs-";
        if (manifest.SchemaVersion != 1 || !name.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[prefix.Length..], "N", out _) ||
            !String.Equals(Path.GetDirectoryName(root), owner, PathComparison) ||
            !String.Equals(Path.GetDirectoryName(result), owner, PathComparison) ||
            !String.Equals(Path.GetFullPath(manifestPath), root + ".workspace.json", PathComparison) ||
            IsWithin(result, root) || IsWithin(raw, root) || IsWithin(root, raw))
            throw new InvalidDataException("Run input cleanup refused an unowned path, original data root or published result path.");
    }

    /// <summary>Holds unchanged published bytes through cleanup and refuses both changed results and a newly appearing result.</summary>
    private static FileStream? OpenReviewedResult(BacktestRunWorkspaceManifest manifest)
    {
        EnsureNoReparseAncestors(manifest.ResultPath);
        if (manifest.ResultSha256 is null)
        {
            if (File.Exists(manifest.ResultPath))
                throw new InvalidDataException("A result appeared after terminal workspace recording; another quant review is required.");
            return null;
        }
        var lease = File.Open(manifest.ResultPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (Convert.ToHexStringLower(SHA256.HashData(lease)).Equals(manifest.ResultSha256, StringComparison.OrdinalIgnoreCase))
                return lease;
            throw new InvalidDataException("Published result bytes changed after terminal workspace recording; inputs remain retained.");
        }
        catch { lease.Dispose(); throw; }
    }

    /// <summary>Compares canonical directory boundaries rather than accepting textual prefix matches.</summary>
    private static bool IsWithin(string path, string root)
    {
        path = Path.GetFullPath(path);
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return path.Equals(root, PathComparison) || path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
    }

    /// <summary>Refuses links or junctions in every existing ancestor before reading ownership or removing a tree.</summary>
    private static void EnsureNoReparseAncestors(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Run input cleanup refused a link or junction in its resolved path.");
    }

    /// <summary>Resolves canonical review paths once and refuses deletion classifications outside existing generated components.</summary>
    private static Dictionary<string, BacktestRunInputCleanupFile> ResolveReviewedFiles(string root,
        IReadOnlyList<BacktestRunInputCleanupFile> files, bool disposable)
    {
        var resolved = new Dictionary<string, BacktestRunInputCleanupFile>(PathComparer);
        foreach (var file in files)
        {
            if (file is null || String.IsNullOrWhiteSpace(file.RelativePath) || file.ByteLength < 0 ||
                file.Sha256 is null || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("Every reviewed file requires a relative path, SHA256 and nonnegative byte length.");
            var segments = file.RelativePath.Split('/');
            if (segments.Any(segment => String.IsNullOrWhiteSpace(segment) || segment is "." or ".." ||
                    segment.EndsWith('.') || Char.IsWhiteSpace(segment[^1]) ||
                    segment.Any(character => Char.IsControl(character) || "\\<>:\"|?*".Contains(character))) ||
                Path.IsPathRooted(file.RelativePath))
                throw new InvalidDataException("Reviewed file paths must use canonical relative segments separated by '/'.");
            if (disposable && (segments.Length < 2 || !IsGeneratedDirectory(segments[0])))
                throw new InvalidDataException("Only reviewed candles and execution/candidate journals are disposable; normalized originals stay protected in place.");
            var path = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
            if (!IsWithin(path, root) || path.Equals(root, PathComparison) || !resolved.TryAdd(path, file))
                throw new InvalidDataException("Reviewed file paths escape the workspace or contain duplicate filesystem identities.");
        }
        return resolved;
    }

    /// <summary>Recognizes only the generated component roots constructed by BacktestRunner.CreateWorkspace and its journal helpers.</summary>
    private static bool IsGeneratedDirectory(string name) =>
        name is "candles" or ".execution-journal" or ".candidate-journal";

    /// <summary>Checks the entire exact inventory before deletion, retaining only protected handles rather than one handle per candle copy.</summary>
    private static IReadOnlyList<string> PreflightInventory(string root,
        IReadOnlyDictionary<string, BacktestRunInputCleanupFile> expected,
        IReadOnlyDictionary<string, BacktestRunInputCleanupFile> protectedFiles,
        ProtectedFileLeases protection, CancellationToken token)
    {
        var found = new HashSet<string>(PathComparer);
        var directories = VisitInventory(root, path =>
        {
            token.ThrowIfCancellationRequested();
            if (!expected.TryGetValue(path, out var reviewed) || !found.Add(path))
                throw new InvalidDataException("An unreviewed file appeared in the workspace; no cleanup was attempted.");
            var lease = OpenVerifiedFile(path, reviewed, FileShare.Read);
            if (protectedFiles.ContainsKey(path)) protection.Add(lease, reviewed);
            else lease.Dispose();
        }, token);
        if (found.Count != expected.Count)
            throw new InvalidDataException("A reviewed file is missing from the workspace; no cleanup was attempted.");
        return directories;
    }

    /// <summary>Rechecks each disposable immediately before individual deletion; a late failure reports partial cleanup without touching protected files.</summary>
    private static void DeleteReviewedCopies(string root,
        IReadOnlyDictionary<string, BacktestRunInputCleanupFile> disposable,
        IReadOnlyDictionary<string, BacktestRunInputCleanupFile> protectedFiles,
        ProtectedFileLeases protection, IReadOnlyList<string> directories, CancellationToken token)
    {
        var removed = 0;
        try
        {
            foreach (var (path, reviewed) in disposable)
            {
                token.ThrowIfCancellationRequested();
                // Deny writes while hashing/deleting this file; terminal workspaces must have no active writer.
                using var lease = OpenVerifiedFile(path, reviewed, FileShare.Read | FileShare.Delete);
                File.Delete(path);
                removed++;
            }
            VerifyRemainingInventory(root, protectedFiles, token);
            protection.Verify();
            RemoveEmptyGeneratedDirectories(root, directories, token);
        }
        catch (OperationCanceledException failure)
        {
            throw new OperationCanceledException($"Cleanup cancelled after removing {removed} reviewed generated files. Protected files remain in place; obtain a fresh inventory review before continuing.", failure, token);
        }
        catch (Exception failure)
        {
            throw new IOException($"Cleanup stopped after removing {removed} reviewed generated files. Protected files were not deletion targets; obtain a fresh inventory review before continuing.", failure);
        }
    }

    /// <summary>Detects late new or missing files before empty-directory cleanup without rescanning history for each deletion.</summary>
    private static void VerifyRemainingInventory(string root,
        IReadOnlyDictionary<string, BacktestRunInputCleanupFile> protectedFiles, CancellationToken token)
    {
        var count = 0;
        VisitInventory(root, path =>
        {
            if (!protectedFiles.ContainsKey(path))
                throw new InvalidDataException("The workspace changed during cleanup; an unreviewed file remains.");
            count++;
        }, token);
        if (count != protectedFiles.Count)
            throw new InvalidDataException("A protected file disappeared during cleanup.");
    }

    /// <summary>Opens a single unchanged file under the requested sharing policy after rejecting links in its current path.</summary>
    private static FileStream OpenVerifiedFile(string path, BacktestRunInputCleanupFile reviewed, FileShare sharing)
    {
        EnsureNoReparseAncestors(path);
        var lease = File.Open(path, FileMode.Open, FileAccess.Read, sharing);
        try
        {
            VerifyFileBytes(lease, reviewed);
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    /// <summary>Compares exact length and hash against the independent review, resetting held streams for repeat checks.</summary>
    private static void VerifyFileBytes(FileStream lease, BacktestRunInputCleanupFile reviewed)
    {
        lease.Position = 0;
        if (lease.Length != reviewed.ByteLength ||
            !Convert.ToHexStringLower(SHA256.HashData(lease)).Equals(reviewed.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Reviewed input bytes changed: {reviewed.RelativePath}.");
    }

    /// <summary>Removes empty generated directories deepest-first with nonrecursive calls; normalized and unknown directories are preserved.</summary>
    private static void RemoveEmptyGeneratedDirectories(string root, IReadOnlyList<string> directories, CancellationToken token)
    {
        for (var index = directories.Count - 1; index >= 0; index--)
        {
            token.ThrowIfCancellationRequested();
            var directory = directories[index];
            var relative = Path.GetRelativePath(root, directory);
            var firstSegment = relative.Split(Path.DirectorySeparatorChar)[0];
            if (!directory.Equals(root, PathComparison) && !IsGeneratedDirectory(firstSegment)) continue;
            EnsureNoReparseAncestors(directory);
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory, recursive: false);
        }
    }

    /// <summary>Visits files once and records parent-first directories, refusing links before descending into any subtree.</summary>
    private static IReadOnlyList<string> VisitInventory(string root, Action<string> visitFile, CancellationToken token)
    {
        EnsureNoReparseAncestors(root);
        var directories = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            EnsureNoReparseAncestors(directory);
            directories.Add(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Run input cleanup refused a link or junction inside its owned directory.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else visitFile(entry);
            }
        }
        return directories;
    }

    /// <summary>Keeps only protected original files open through cleanup so Windows denies their mutation or replacement.</summary>
    private sealed class ProtectedFileLeases : IDisposable
    {
        private readonly List<(FileStream Lease, BacktestRunInputCleanupFile Reviewed)> files = [];

        /// <summary>Takes ownership of a verified protected handle until the cleanup operation ends.</summary>
        public void Add(FileStream lease, BacktestRunInputCleanupFile reviewed) => files.Add((lease, reviewed));

        /// <summary>Rehashes held protected originals after deleting disposable copies, preserving every original timestamp byte.</summary>
        public void Verify()
        {
            foreach (var (lease, reviewed) in files) VerifyFileBytes(lease, reviewed);
        }

        /// <summary>Releases protected read leases after success or any preflight/deletion failure.</summary>
        public void Dispose()
        {
            foreach (var (lease, _) in files) lease.Dispose();
        }
    }
}
