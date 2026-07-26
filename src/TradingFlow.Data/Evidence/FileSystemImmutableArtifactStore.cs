using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Data.Evidence;

/// <summary>
/// Publishes one content-addressed object as an atomic directory containing bytes and metadata.
/// Temporary directories are never visible through the evidence namespace.
/// </summary>
public sealed class FileSystemImmutableArtifactStore :
    IImmutableArtifactLifecycleStore
{
    private const string ContentFileName = "content.bin";
    private const string MetadataFileName = "metadata.json";
    private readonly string rootPath;
    private readonly TimeSpan temporaryDirectoryGracePeriod;

    public FileSystemImmutableArtifactStore(ImmutableArtifactStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        rootPath = options.RootPath;
        temporaryDirectoryGracePeriod = options.TemporaryDirectoryGracePeriod;
        Directory.CreateDirectory(rootPath);
    }

    public async Task<ImmutableArtifactReceipt> PutIfAbsentAsync(
        ImmutableArtifactWriteRequest request,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatePayload(request.Artifact.Content, content.Span);

        var finalDirectory = GetObjectDirectory(request.Artifact);
        if (Directory.Exists(finalDirectory))
        {
            await EnsureValidExistingAsync(request.Artifact, cancellationToken);
            return Receipt(request.Artifact, alreadyExisted: true);
        }

        var parent = Path.GetDirectoryName(finalDirectory)
            ?? throw new InvalidOperationException("Evidence object has no parent directory.");
        Directory.CreateDirectory(parent);
        var temporaryDirectory = Path.Combine(
            parent,
            $".tmp-{request.Artifact.Content.Sha256}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);

        try
        {
            await WriteDurablyAsync(
                Path.Combine(temporaryDirectory, ContentFileName),
                content,
                cancellationToken);
            await WriteDurablyAsync(
                Path.Combine(temporaryDirectory, MetadataFileName),
                EvidenceCanonicalJson.SerializeToUtf8Bytes(
                    StoredArtifactMetadata.From(request.Artifact)),
                cancellationToken);

            try
            {
                Directory.Move(temporaryDirectory, finalDirectory);
            }
            catch (IOException) when (Directory.Exists(finalDirectory))
            {
                await EnsureValidExistingAsync(request.Artifact, cancellationToken);
                return Receipt(request.Artifact, alreadyExisted: true);
            }

            return Receipt(request.Artifact, alreadyExisted: false);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    public async Task<Stream> OpenReadAsync(
        EvidenceArtifactReference artifact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        await EnsureValidExistingAsync(artifact, cancellationToken);
        return new FileStream(
            GetContentPath(artifact),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    public async Task<ImmutableArtifactVerification> VerifyAsync(
        EvidenceArtifactReference artifact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var contentPath = GetContentPath(artifact);
        var metadataPath = GetMetadataPath(artifact);
        if (!File.Exists(contentPath) || !File.Exists(metadataPath))
        {
            return new ImmutableArtifactVerification(
                false,
                artifact,
                File.Exists(contentPath) ? new FileInfo(contentPath).Length : null,
                null,
                "Object content or metadata is missing.");
        }

        try
        {
            var metadataBytes = await File.ReadAllBytesAsync(metadataPath, cancellationToken);
            var metadata = EvidenceCanonicalJson.Deserialize<StoredArtifactMetadata>(metadataBytes);
            if (!metadata.Matches(artifact))
            {
                return new ImmutableArtifactVerification(
                    false,
                    artifact,
                    new FileInfo(contentPath).Length,
                    null,
                    "Stored metadata does not match the requested artifact.");
            }

            await using var stream = new FileStream(
                contentPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actualLength = stream.Length;
            var actualHash = Convert.ToHexString(
                    await SHA256.HashDataAsync(stream, cancellationToken))
                .ToLowerInvariant();
            var valid = actualLength == artifact.Content.ByteLength &&
                        actualHash.Equals(artifact.Content.Sha256, StringComparison.Ordinal);
            return new ImmutableArtifactVerification(
                valid,
                artifact,
                actualLength,
                actualHash,
                valid ? null : "Stored bytes do not match the content address.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return new ImmutableArtifactVerification(
                false,
                artifact,
                File.Exists(contentPath) ? new FileInfo(contentPath).Length : null,
                null,
                exception.Message);
        }
    }

    public async Task<ImmutableArtifactDeleteResult> DeleteIfMatchesAsync(
        EvidenceArtifactReference artifact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var objectDirectory = GetObjectDirectory(artifact);
        if (!Directory.Exists(objectDirectory))
        {
            return new ImmutableArtifactDeleteResult(
                artifact,
                ImmutableArtifactDeleteOutcome.AlreadyAbsent);
        }

        var verification = await VerifyAsync(artifact, cancellationToken);
        if (!verification.IsValid)
        {
            throw new InvalidDataException(
                $"Refusing to delete immutable artifact {artifact.ObjectNamespace}/" +
                $"{artifact.Content.Sha256}: {verification.FailureReason}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var parent = Path.GetDirectoryName(objectDirectory)
            ?? throw new InvalidOperationException("Evidence object has no parent directory.");
        var deleteDirectory = Path.Combine(
            parent,
            $".delete-{artifact.Content.Sha256}-{Guid.NewGuid():N}");
        try
        {
            Directory.Move(objectDirectory, deleteDirectory);
        }
        catch (DirectoryNotFoundException)
        {
            return new ImmutableArtifactDeleteResult(
                artifact,
                ImmutableArtifactDeleteOutcome.AlreadyAbsent);
        }

        try
        {
            Directory.Delete(deleteDirectory, recursive: true);
        }
        catch
        {
            try
            {
                if (!Directory.Exists(objectDirectory) && Directory.Exists(deleteDirectory))
                {
                    Directory.Move(deleteDirectory, objectDirectory);
                }
            }
            catch (Exception rollbackException) when (
                rollbackException is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    "Immutable artifact deletion failed and the object directory could not be restored.",
                    rollbackException);
            }

            throw;
        }

        return new ImmutableArtifactDeleteResult(
            artifact,
            ImmutableArtifactDeleteOutcome.Deleted);
    }

    public async IAsyncEnumerable<ImmutableArtifactScanEntry> ScanAsync(
        EvidenceObjectNamespace? objectNamespace = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(rootPath))
        {
            yield return new ImmutableArtifactScanEntry(
                String.Empty,
                null,
                null,
                null,
                null,
                null,
                [Finding(
                    ImmutableArtifactScanIssue.RootUnavailable,
                    $"Immutable artifact root '{rootPath}' does not exist.")]);
            yield break;
        }

        var scanRoot = objectNamespace is null
            ? rootPath
            : GetNamespaceRoot(objectNamespace);
        if (!Directory.Exists(scanRoot))
        {
            yield break;
        }

        await RecoverMaintenanceDirectoriesAsync(scanRoot, cancellationToken);
        foreach (var candidate in FindCandidateDirectories(scanRoot)
                     .OrderBy(
                         value => NormalizeObjectKey(Path.GetRelativePath(rootPath, value.Path)),
                         StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.Error is not null)
            {
                yield return new ImmutableArtifactScanEntry(
                    NormalizeObjectKey(Path.GetRelativePath(rootPath, candidate.Path)),
                    null,
                    null,
                    null,
                    null,
                    null,
                    [Finding(candidate.ErrorCode, candidate.Error.Message)]);
                continue;
            }

            yield return await ScanCandidateAsync(candidate.Path, cancellationToken);
        }
    }

    private async Task<ImmutableArtifactScanEntry> ScanCandidateAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        var objectKey = NormalizeObjectKey(Path.GetRelativePath(rootPath, directory));
        var parts = objectKey.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var findings = new List<ImmutableArtifactScanFinding>();
        string? physicalNamespace = null;
        string? physicalHash = null;
        string? physicalShard = null;
        if (parts.Length < 3)
        {
            findings.Add(Finding(
                ImmutableArtifactScanIssue.PathLayoutInvalid,
                "Object path must contain a namespace, two-character shard, and SHA-256 directory."));
        }
        else
        {
            physicalNamespace = String.Join('/', parts[..^2]);
            physicalShard = parts[^2];
            physicalHash = parts[^1];
            if (!IsLowerHex(physicalHash, 64) || !IsLowerHex(physicalShard, 2))
            {
                findings.Add(Finding(
                    ImmutableArtifactScanIssue.PathLayoutInvalid,
                    "Object shard and hash directory are not lowercase hexadecimal."));
            }
            else if (!physicalShard.Equals(physicalHash[..2], StringComparison.Ordinal))
            {
                findings.Add(Finding(
                    ImmutableArtifactScanIssue.ShardPathMismatch,
                    $"Physical shard '{physicalShard}' does not match hash prefix '{physicalHash[..2]}'."));
            }
        }

        EvidenceArtifactReference? declaredArtifact = null;
        StoredArtifactMetadata? metadata = null;
        var metadataPath = Path.Combine(directory, MetadataFileName);
        if (!File.Exists(metadataPath))
        {
            findings.Add(Finding(
                ImmutableArtifactScanIssue.MetadataMissing,
                "metadata.json is missing."));
        }
        else
        {
            try
            {
                var metadataBytes = await File.ReadAllBytesAsync(metadataPath, cancellationToken);
                metadata = EvidenceCanonicalJson.Deserialize<StoredArtifactMetadata>(metadataBytes);
                if (metadata.SchemaVersion != 1)
                {
                    findings.Add(Finding(
                        ImmutableArtifactScanIssue.MetadataSchemaUnsupported,
                        $"Metadata schema {metadata.SchemaVersion} is not supported."));
                }
                else
                {
                    declaredArtifact = metadata.ToReference();
                    if (physicalNamespace is not null &&
                        !physicalNamespace.Equals(
                            declaredArtifact.ObjectNamespace.Value,
                            StringComparison.Ordinal))
                    {
                        findings.Add(Finding(
                            ImmutableArtifactScanIssue.NamespacePathMismatch,
                            $"Physical namespace '{physicalNamespace}' differs from metadata namespace " +
                            $"'{declaredArtifact.ObjectNamespace.Value}'."));
                    }

                    if (physicalHash is not null &&
                        !physicalHash.Equals(
                            declaredArtifact.Content.Sha256,
                            StringComparison.Ordinal))
                    {
                        findings.Add(Finding(
                            ImmutableArtifactScanIssue.HashPathMismatch,
                            $"Physical hash '{physicalHash}' differs from metadata hash " +
                            $"'{declaredArtifact.Content.Sha256}'."));
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or System.Text.Json.JsonException
                    or ArgumentException
                    or InvalidDataException)
            {
                findings.Add(Finding(
                    ImmutableArtifactScanIssue.MetadataMalformed,
                    exception.Message));
            }
        }

        long? actualLength = null;
        string? actualHash = null;
        var contentPath = Path.Combine(directory, ContentFileName);
        if (!File.Exists(contentPath))
        {
            findings.Add(Finding(
                ImmutableArtifactScanIssue.ContentMissing,
                "content.bin is missing."));
        }
        else
        {
            try
            {
                await using var stream = new FileStream(
                    contentPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                actualLength = stream.Length;
                actualHash = Convert.ToHexString(
                        await SHA256.HashDataAsync(stream, cancellationToken))
                    .ToLowerInvariant();
                if (metadata is not null && metadata.SchemaVersion == 1)
                {
                    if (actualLength != metadata.ByteLength)
                    {
                        findings.Add(Finding(
                            ImmutableArtifactScanIssue.ContentLengthMismatch,
                            $"Actual length {actualLength} differs from declared length {metadata.ByteLength}."));
                    }

                    if (!actualHash.Equals(metadata.Sha256, StringComparison.Ordinal))
                    {
                        findings.Add(Finding(
                            ImmutableArtifactScanIssue.ContentHashMismatch,
                            $"Actual SHA-256 '{actualHash}' differs from declared SHA-256 '{metadata.Sha256}'."));
                    }
                }
                if (physicalHash is not null &&
                    IsLowerHex(physicalHash, 64) &&
                    !actualHash.Equals(physicalHash, StringComparison.Ordinal))
                {
                    findings.Add(Finding(
                        ImmutableArtifactScanIssue.ContentHashMismatch,
                        $"Actual SHA-256 '{actualHash}' differs from physical hash path '{physicalHash}'."));
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                findings.Add(Finding(
                    ImmutableArtifactScanIssue.ContentUnreadable,
                    exception.Message));
            }
        }

        return new ImmutableArtifactScanEntry(
            objectKey,
            physicalNamespace,
            physicalHash,
            declaredArtifact,
            actualLength,
            actualHash,
            findings.ToArray());
    }

    private IEnumerable<PhysicalScanCandidate> FindCandidateDirectories(string scanRoot)
    {
        var pending = new Stack<string>();
        pending.Push(scanRoot);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            string[]? children = null;
            Exception? directoryError = null;
            try
            {
                children = Directory.GetDirectories(current);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                directoryError = exception;
            }

            if (directoryError is not null)
            {
                yield return new PhysicalScanCandidate(
                    current,
                    ImmutableArtifactScanIssue.DirectoryUnreadable,
                    directoryError);
                continue;
            }

            foreach (var child in children!)
            {
                var name = Path.GetFileName(child);
                if (name.StartsWith(".tmp-", StringComparison.Ordinal))
                {
                    if (IsFreshTemporaryDirectory(child))
                    {
                        continue;
                    }

                    yield return new PhysicalScanCandidate(
                        child,
                        ImmutableArtifactScanIssue.CandidateUnreadable,
                        new IOException("Stale immutable-artifact temporary directory was not recovered."));
                    continue;
                }

                FileAttributes attributes = default;
                Exception? candidateError = null;
                try
                {
                    attributes = File.GetAttributes(child);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    candidateError = exception;
                }

                if (candidateError is not null)
                {
                    yield return new PhysicalScanCandidate(
                        child,
                        ImmutableArtifactScanIssue.CandidateUnreadable,
                        candidateError);
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    yield return new PhysicalScanCandidate(
                        child,
                        ImmutableArtifactScanIssue.ReparsePointUnsupported,
                        new InvalidDataException(
                            "Reparse-point directories are not followed by evidence maintenance scans."));
                    continue;
                }

                if (File.Exists(Path.Combine(child, MetadataFileName)) ||
                    File.Exists(Path.Combine(child, ContentFileName)) ||
                    IsLowerHex(name, 64))
                {
                    yield return new PhysicalScanCandidate(
                        child,
                        ImmutableArtifactScanIssue.CandidateUnreadable,
                        null);
                    continue;
                }

                pending.Push(child);
            }
        }
    }

    private async Task RecoverMaintenanceDirectoriesAsync(
        string scanRoot,
        CancellationToken cancellationToken)
    {
        foreach (var directory in EnumerateMaintenanceDirectories(scanRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(directory);
            if (name.StartsWith(".delete-", StringComparison.Ordinal))
            {
                await TryCompleteStrandedDeleteAsync(directory, cancellationToken);
                continue;
            }

            if (name.StartsWith(".tmp-", StringComparison.Ordinal) &&
                !IsFreshTemporaryDirectory(directory) &&
                IsStoreTemporaryDirectoryName(name))
            {
                TryDeleteDirectory(directory);
            }
        }
    }

    private IEnumerable<string> EnumerateMaintenanceDirectories(string scanRoot)
    {
        var pending = new Stack<string>();
        pending.Push(scanRoot);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            string[] children;
            try
            {
                children = Directory.GetDirectories(current);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(child);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                var name = Path.GetFileName(child);
                if (name.StartsWith(".tmp-", StringComparison.Ordinal) ||
                    name.StartsWith(".delete-", StringComparison.Ordinal))
                {
                    yield return child;
                    continue;
                }

                pending.Push(child);
            }
        }
    }

    private async Task TryCompleteStrandedDeleteAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(directory);
        if (!TryReadMaintenanceHash(name, ".delete-", out var expectedHash))
        {
            return;
        }

        try
        {
            var metadataPath = Path.Combine(directory, MetadataFileName);
            var contentPath = Path.Combine(directory, ContentFileName);
            if (!File.Exists(metadataPath) || !File.Exists(contentPath))
            {
                return;
            }

            var metadata = EvidenceCanonicalJson.Deserialize<StoredArtifactMetadata>(
                await File.ReadAllBytesAsync(metadataPath, cancellationToken));
            var artifact = metadata.ToReference();
            if (!artifact.Content.Sha256.Equals(expectedHash, StringComparison.Ordinal) ||
                !Path.GetFullPath(Path.GetDirectoryName(directory)!).Equals(
                    Path.GetFullPath(Path.GetDirectoryName(GetObjectDirectory(artifact))!),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            long actualLength;
            string actualHash;
            await using (var stream = new FileStream(
                             contentPath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                actualLength = stream.Length;
                actualHash = Convert.ToHexString(
                        await SHA256.HashDataAsync(stream, cancellationToken))
                    .ToLowerInvariant();
            }

            if (metadata.SchemaVersion != 1 ||
                actualLength != artifact.Content.ByteLength ||
                !actualHash.Equals(expectedHash, StringComparison.Ordinal))
            {
                return;
            }

            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Text.Json.JsonException
                or ArgumentException
                or InvalidDataException)
        {
            // A remnant that cannot be proven safe remains visible to the scan and
            // therefore blocks retention instead of being removed speculatively.
        }
    }

    private bool IsFreshTemporaryDirectory(string directory) =>
        DateTimeOffset.UtcNow -
        new DateTimeOffset(Directory.GetLastWriteTimeUtc(directory), TimeSpan.Zero) <
        temporaryDirectoryGracePeriod;

    private static bool IsStoreTemporaryDirectoryName(string name) =>
        TryReadMaintenanceHash(name, ".tmp-", out _);

    private static bool TryReadMaintenanceHash(
        string name,
        string prefix,
        out string hash)
    {
        hash = String.Empty;
        if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
            name.Length <= prefix.Length + 65)
        {
            return false;
        }

        var candidate = name.Substring(prefix.Length, 64);
        if (!IsLowerHex(candidate, 64) || name[prefix.Length + 64] != '-')
        {
            return false;
        }

        hash = candidate;
        return true;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // The subsequent maintenance scan reports the stranded directory.
        }
    }

    private static ImmutableArtifactScanFinding Finding(
        ImmutableArtifactScanIssue code,
        string detail) =>
        new(code, String.IsNullOrWhiteSpace(detail) ? code.ToString() : detail.Trim());

    private static bool IsLowerHex(string value, int length) =>
        value.Length == length &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record PhysicalScanCandidate(
        string Path,
        ImmutableArtifactScanIssue ErrorCode,
        Exception? Error);

    private async Task EnsureValidExistingAsync(
        EvidenceArtifactReference artifact,
        CancellationToken cancellationToken)
    {
        var verification = await VerifyAsync(artifact, cancellationToken);
        if (!verification.IsValid)
        {
            throw new InvalidDataException(
                $"Immutable evidence collision or corruption for " +
                $"{artifact.ObjectNamespace}/{artifact.Content.Sha256}: " +
                verification.FailureReason);
        }
    }

    private ImmutableArtifactReceipt Receipt(
        EvidenceArtifactReference artifact,
        bool alreadyExisted) =>
        new(
            artifact,
            NormalizeObjectKey(Path.GetRelativePath(rootPath, GetObjectDirectory(artifact))),
            alreadyExisted);

    private string GetObjectDirectory(EvidenceArtifactReference artifact)
    {
        var path = Path.Combine(
            GetNamespaceRoot(artifact.ObjectNamespace),
            artifact.Content.Sha256[..2],
            artifact.Content.Sha256);
        return EnsureInsideRoot(path);
    }

    private string GetNamespaceRoot(EvidenceObjectNamespace objectNamespace) =>
        EnsureInsideRoot(Path.Combine(
            rootPath,
            objectNamespace.Value.Replace('/', Path.DirectorySeparatorChar)));

    private string GetContentPath(EvidenceArtifactReference artifact) =>
        Path.Combine(GetObjectDirectory(artifact), ContentFileName);

    private string GetMetadataPath(EvidenceArtifactReference artifact) =>
        Path.Combine(GetObjectDirectory(artifact), MetadataFileName);

    private string EnsureInsideRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(rootPath, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException("Evidence path escaped the configured root.");
        }

        return fullPath;
    }

    private static void ValidatePayload(
        EvidenceContentAddress expected,
        ReadOnlySpan<byte> content)
    {
        if (content.Length != expected.ByteLength)
        {
            throw new InvalidDataException(
                $"Payload length {content.Length} does not match declared length {expected.ByteLength}.");
        }

        var actualHash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        if (!actualHash.Equals(expected.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Payload SHA-256 does not match its content address.");
        }
    }

    private static async Task WriteDurablyAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(content, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private static string NormalizeObjectKey(string value) =>
        value.Replace(Path.DirectorySeparatorChar, '/');

    private sealed record StoredArtifactMetadata(
        int SchemaVersion,
        string Sha256,
        long ByteLength,
        string MediaType,
        string ObjectNamespace)
    {
        public static StoredArtifactMetadata From(EvidenceArtifactReference artifact) =>
            new(
                1,
                artifact.Content.Sha256,
                artifact.Content.ByteLength,
                artifact.Content.MediaType,
                artifact.ObjectNamespace.Value);

        public bool Matches(EvidenceArtifactReference artifact) =>
            SchemaVersion == 1 &&
            Sha256.Equals(artifact.Content.Sha256, StringComparison.Ordinal) &&
            ByteLength == artifact.Content.ByteLength &&
            MediaType.Equals(artifact.Content.MediaType, StringComparison.Ordinal) &&
            ObjectNamespace.Equals(artifact.ObjectNamespace.Value, StringComparison.Ordinal);

        public EvidenceArtifactReference ToReference() =>
            new(
                new EvidenceContentAddress(Sha256, ByteLength, MediaType),
                new EvidenceObjectNamespace(ObjectNamespace));
    }
}
