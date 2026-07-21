using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TradingFlow.Engine.Storage;

/// <summary>
/// Persists immutable raw payloads under data/raw/{source}/{yyyy-MM-dd}. Payload publication uses
/// an exclusive atomic move so concurrent writers cannot overwrite an existing archive entry.
/// </summary>
public sealed class FileSystemRawArchiveWriter : IRawArchiveWriter
{
    private const int ManifestSchemaVersion = 1;
    private const int WriteBufferSize = 64 * 1024;

    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private static readonly HashSet<string> SensitiveHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Proxy-Authorization",
        "Cookie",
        "Set-Cookie",
        "X-Api-Key",
        "APCA-API-KEY-ID",
        "APCA-API-SECRET-KEY"
    };

    private static readonly HashSet<string> SensitiveQueryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "auth",
        "token",
        "api_key",
        "apikey",
        "key",
        "secret",
        "signature"
    };

    private readonly RawArchiveOptions options;

    public FileSystemRawArchiveWriter(RawArchiveOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<RawArchiveReceipt> ArchiveAsync(
        RawArchiveRequest request,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.ReceivedAtUtc == default)
        {
            throw new ArgumentException("A provider receive timestamp is required.", nameof(request));
        }

        var source = ValidatePathSegment(request.Source, nameof(request.Source));
        var artifactType = ValidatePathSegment(request.ArtifactType, nameof(request.ArtifactType));
        var extension = ValidateExtension(request.FileExtension);
        var receivedAtUtc = request.ReceivedAtUtc.ToUniversalTime();
        var attributes = SanitizeAttributes(request.Attributes);
        var http = SanitizeHttpMetadata(request.Http);

        // Copy at the API boundary so a caller cannot mutate a pooled buffer while it is hashed/written.
        var immutablePayload = payload.ToArray();
        var sha256 = Convert.ToHexString(SHA256.HashData(immutablePayload)).ToLowerInvariant();
        var archiveId = $"{receivedAtUtc:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}";
        var fileName = $"{archiveId}-{artifactType}.{extension}";
        var dayDirectory = ResolveDayDirectory(source, receivedAtUtc);
        Directory.CreateDirectory(dayDirectory);

        var payloadPath = EnsureInsideRoot(Path.Combine(dayDirectory, fileName));
        var manifestPath = EnsureInsideRoot(payloadPath + ".manifest.json");
        var manifest = new RawArchiveManifest(
            ManifestSchemaVersion,
            archiveId,
            source,
            artifactType,
            receivedAtUtc,
            fileName,
            String.IsNullOrWhiteSpace(request.ContentType) ? null : request.ContentType.Trim(),
            immutablePayload.LongLength,
            sha256,
            NormalizeOptional(request.PresetId),
            NormalizeOptional(request.ProviderRecordId),
            NormalizeOptional(request.CorrelationId),
            NormalizeOptional(request.RunId),
            NormalizeOptional(request.ConfigHash),
            NormalizeOptional(request.CodeVersion),
            http,
            attributes);

        await WriteAndPublishExclusiveAsync(payloadPath, immutablePayload, cancellationToken);

        // Once the immutable payload is visible, finish its manifest even if the caller cancels.
        // This creates a clear commit boundary and prevents an avoidable orphaned payload.
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestJsonOptions);
        await WriteAndPublishExclusiveAsync(manifestPath, manifestBytes, CancellationToken.None);
        return new RawArchiveReceipt(payloadPath, manifestPath, manifest);
    }

    public Task<RawArchiveRetentionResult> EnforceRetentionAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var deleteBeforeDate = DateOnly.FromDateTime(nowUtc.UtcDateTime.Date.AddDays(-options.RetentionDays));
        if (!Directory.Exists(options.RootPath))
        {
            return Task.FromResult(new RawArchiveRetentionResult(0, 0, deleteBeforeDate));
        }

        var examined = 0;
        var deleted = 0;
        foreach (var sourcePath in Directory.EnumerateDirectories(options.RootPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsReparsePoint(sourcePath))
            {
                continue;
            }

            foreach (var datePath in Directory.EnumerateDirectories(sourcePath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsReparsePoint(datePath) || !TryParseDateDirectory(datePath, out var archiveDate))
                {
                    continue;
                }

                examined++;
                if (archiveDate >= deleteBeforeDate)
                {
                    continue;
                }

                EnsureInsideRoot(datePath);
                Directory.Delete(datePath, recursive: true);
                deleted++;
            }
        }

        return Task.FromResult(new RawArchiveRetentionResult(examined, deleted, deleteBeforeDate));
    }

    private async Task WriteAndPublishExclusiveAsync(
        string destinationPath,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        var temporaryPath = EnsureInsideRoot(Path.Combine(
            Path.GetDirectoryName(destinationPath)!,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp"));

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                WriteBufferSize,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, destinationPath, overwrite: false);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private string ResolveDayDirectory(string source, DateTimeOffset receivedAtUtc)
    {
        var path = Path.Combine(
            options.RootPath,
            source,
            receivedAtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return EnsureInsideRoot(path);
    }

    private string EnsureInsideRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var rootWithSeparator = Path.TrimEndingDirectorySeparator(options.RootPath) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.StartsWith(rootWithSeparator, comparison))
        {
            throw new InvalidOperationException("Resolved raw archive path escapes the configured root.");
        }

        return fullPath;
    }

    private static string ValidatePathSegment(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length > 64 || normalized.Any(character => !IsSafeSegmentCharacter(character)))
        {
            throw new ArgumentException(
                "Archive path segments may contain only ASCII letters, numbers, hyphens, and underscores.",
                parameterName);
        }

        return normalized;
    }

    private static string ValidateExtension(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().TrimStart('.').ToLowerInvariant();
        if (normalized.Length is < 1 or > 16 || normalized.Any(character => !IsAsciiLetterOrDigit(character)))
        {
            throw new ArgumentException("Archive file extensions must be 1-16 ASCII letters or numbers.", nameof(value));
        }

        return normalized;
    }

    private static IReadOnlyDictionary<string, string> SanitizeAttributes(
        IReadOnlyDictionary<string, string>? attributes)
    {
        var sanitized = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (attributes is null)
        {
            return sanitized;
        }

        foreach (var attribute in attributes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(attribute.Key);
            if (IsSensitiveName(attribute.Key))
            {
                throw new ArgumentException(
                    $"Sensitive metadata key '{attribute.Key}' cannot be written to the raw archive.",
                    nameof(attributes));
            }

            sanitized[attribute.Key.Trim()] = attribute.Value ?? String.Empty;
        }

        return sanitized;
    }

    private static RawArchiveHttpMetadata? SanitizeHttpMetadata(RawArchiveHttpMetadata? http)
    {
        if (http is null)
        {
            return null;
        }

        if (http.StatusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(http), "HTTP status code must be between 100 and 599.");
        }

        var headers = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (http.Headers is not null)
        {
            foreach (var header in http.Headers)
            {
                if (SensitiveHeaderNames.Contains(header.Key) || IsSensitiveName(header.Key))
                {
                    continue;
                }

                headers[header.Key] = Array.AsReadOnly(header.Value?.ToArray() ?? []);
            }
        }

        return new RawArchiveHttpMetadata(SanitizeUri(http.RequestUri), http.StatusCode, headers);
    }

    private static Uri? SanitizeUri(Uri? uri)
    {
        if (uri is null)
        {
            return null;
        }

        if (!uri.IsAbsoluteUri)
        {
            throw new ArgumentException("Archived HTTP request URIs must be absolute.", nameof(uri));
        }

        var safePairs = new List<string>();
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var encodedName = separator < 0 ? pair : pair[..separator];
            var decodedName = Uri.UnescapeDataString(encodedName.Replace('+', ' '));
            if (!SensitiveQueryNames.Contains(decodedName) && !IsSensitiveName(decodedName))
            {
                safePairs.Add(pair);
            }
        }

        var builder = new UriBuilder(uri)
        {
            UserName = String.Empty,
            Password = String.Empty,
            Query = String.Join("&", safePairs)
        };
        return builder.Uri;
    }

    private static bool IsSensitiveName(string name)
    {
        var normalized = new string(name.Where(IsAsciiLetterOrDigit).ToArray()).ToLowerInvariant();
        return normalized.Contains("authorization", StringComparison.Ordinal)
               || normalized.Contains("apikey", StringComparison.Ordinal)
               || normalized.Contains("secret", StringComparison.Ordinal)
               || normalized.EndsWith("token", StringComparison.Ordinal)
               || normalized.Equals("auth", StringComparison.Ordinal);
    }

    private static bool TryParseDateDirectory(string path, out DateOnly date)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return DateOnly.TryParseExact(
            name,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool IsSafeSegmentCharacter(char character) =>
        IsAsciiLetterOrDigit(character) || character is '-' or '_';

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is >= 'a' and <= 'z'
        || character is >= 'A' and <= 'Z'
        || character is >= '0' and <= '9';

    private static string? NormalizeOptional(string? value) =>
        String.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Cleanup must not hide the original persistence failure.
        }
    }
}
