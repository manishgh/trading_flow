using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingFlow.Domain.Research;

public sealed record EvidenceContentAddress
{
    public EvidenceContentAddress(string sha256, long byteLength, string mediaType)
    {
        Sha256 = EvidenceValue.NormalizeSha256(sha256);
        if (byteLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteLength));
        }

        ByteLength = byteLength;
        MediaType = EvidenceValue.NormalizeRequired(mediaType, nameof(mediaType));
    }

    public string Sha256 { get; }

    public long ByteLength { get; }

    public string MediaType { get; }
}

public sealed record EvidenceObjectNamespace
{
    private static readonly IReadOnlySet<string> WindowsDeviceNames =
        new HashSet<string>(
            new[]
            {
                "aux", "clock$", "con", "nul", "prn",
                "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
                "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9"
            },
            StringComparer.OrdinalIgnoreCase);

    public EvidenceObjectNamespace(string value)
    {
        var normalized = EvidenceValue.NormalizeRequired(value, nameof(value))
            .Replace('\\', '/')
            .ToLowerInvariant();
        var segments = normalized.Split('/', StringSplitOptions.None);
        if (segments.Length == 0 ||
            segments.Any(segment =>
                segment.Length == 0 ||
                segment is "." or ".." ||
                WindowsDeviceNames.Contains(segment) ||
                segment.Any(character =>
                    !(Char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))))
        {
            throw new ArgumentException(
                "An evidence namespace must contain only safe slash-separated ASCII segments.",
                nameof(value));
        }

        Value = String.Join('/', segments);
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record EvidenceArtifactReference
{
    public EvidenceArtifactReference(
        EvidenceContentAddress content,
        EvidenceObjectNamespace objectNamespace)
    {
        Content = content ?? throw new ArgumentNullException(nameof(content));
        ObjectNamespace = objectNamespace ?? throw new ArgumentNullException(nameof(objectNamespace));
    }

    public EvidenceContentAddress Content { get; }

    public EvidenceObjectNamespace ObjectNamespace { get; }
}

public sealed record EvidenceSourceReference
{
    public EvidenceSourceReference(
        string observationId,
        EvidenceArtifactReference artifact,
        DateTimeOffset receivedAtUtc)
    {
        ObservationId = EvidenceValue.NormalizeRequired(observationId, nameof(observationId));
        Artifact = artifact ?? throw new ArgumentNullException(nameof(artifact));
        EvidenceValue.EnsureUtc(receivedAtUtc, nameof(receivedAtUtc));
        ReceivedAtUtc = receivedAtUtc;
    }

    public string ObservationId { get; }

    public EvidenceArtifactReference Artifact { get; }

    public DateTimeOffset ReceivedAtUtc { get; }
}

/// <summary>
/// Exact raw-observation lineage for one normalized row. Ordering is canonical so the same
/// source set has the same identity regardless of provider page arrival order.
/// </summary>
public sealed class EvidenceRowLineage
{
    public EvidenceRowLineage(IReadOnlyList<EvidenceSourceReference> sources)
    {
        var normalized = (sources ?? throw new ArgumentNullException(nameof(sources)))
            .Select(source => source ?? throw new ArgumentException("Lineage cannot contain null sources.", nameof(sources)))
            .GroupBy(source => source.ObservationId, StringComparer.Ordinal)
            .Select(group =>
            {
                var values = group.ToArray();
                if (values.Distinct().Count() != 1)
                {
                    throw new ArgumentException(
                        $"Observation '{group.Key}' has conflicting lineage metadata.",
                        nameof(sources));
                }

                return values[0];
            })
            .OrderBy(source => source.ObservationId, StringComparer.Ordinal)
            .ThenBy(source => source.Artifact.Content.Sha256, StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("At least one source observation is required.", nameof(sources));
        }

        Sources = new ReadOnlyCollection<EvidenceSourceReference>(normalized);
        LineageHash = EvidenceValue.ComputeSha256(String.Join(
            "\n",
            normalized.Select(source =>
                $"{source.ObservationId}|{source.Artifact.ObjectNamespace.Value}|" +
                $"{source.Artifact.Content.Sha256}|{source.Artifact.Content.ByteLength}|" +
                $"{source.Artifact.Content.MediaType}|{source.ReceivedAtUtc:O}")));
    }

    public IReadOnlyList<EvidenceSourceReference> Sources { get; }

    public string LineageHash { get; }
}

internal static class EvidenceValue
{
    private static readonly IReadOnlySet<string> SafeResponseHeaders =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "content-length",
            "content-type",
            "date",
            "etag",
            "last-modified",
            "retry-after",
            "x-ratelimit-limit",
            "x-ratelimit-remaining",
            "x-ratelimit-reset"
        };

    private static readonly string[] SecretMarkers =
    [
        "access_key",
        "access-key",
        "auth",
        "authorization",
        "api-key",
        "api_key",
        "apikey",
        "cookie",
        "credential",
        "password",
        "private_key",
        "private-key",
        "secret",
        "sig",
        "signature",
        "signed",
        "token"
    ];

    public static string NormalizeRequired(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    public static string NormalizeSha256(string value)
    {
        var normalized = NormalizeRequired(value, nameof(value)).ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "A SHA-256 value must contain exactly 64 hexadecimal characters.",
                nameof(value));
        }

        return normalized;
    }

    public static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The timestamp must be a non-default UTC value.", parameterName);
        }
    }

    public static string ComputeSha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    public static IReadOnlyDictionary<string, string> CopySorted(
        IReadOnlyDictionary<string, string>? values)
    {
        var copy = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (values is not null)
        {
            foreach (var pair in values)
            {
                var key = NormalizeRequired(pair.Key, nameof(values));
                if (!copy.TryAdd(key, pair.Value?.Trim() ?? String.Empty))
                {
                    throw new ArgumentException(
                        $"Metadata contains duplicate normalized key '{key}'.",
                        nameof(values));
                }
            }
        }

        return new ReadOnlyDictionary<string, string>(copy);
    }

    public static IReadOnlyDictionary<string, string> CopySafeResponseHeaders(
        IReadOnlyDictionary<string, string>? values)
    {
        var normalized = CopySorted(values);
        var unsafeKey = normalized.Keys.FirstOrDefault(key => !SafeResponseHeaders.Contains(key));
        if (unsafeKey is not null)
        {
            throw new ArgumentException(
                $"Response header '{unsafeKey}' is not in the non-secret evidence allowlist.",
                nameof(values));
        }

        return normalized;
    }

    public static IReadOnlyDictionary<string, string> CopyNonSecretMetadata(
        IReadOnlyDictionary<string, string>? values)
    {
        var normalized = CopySorted(values);
        foreach (var pair in normalized)
        {
            var combined = $"{pair.Key}={pair.Value}";
            if (SecretMarkers.Any(marker =>
                    combined.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException(
                    $"Metadata key '{pair.Key}' may contain authentication material.",
                    nameof(values));
            }
        }

        return normalized;
    }

    public static string NormalizeNonSecretEndpoint(string value, string parameterName)
    {
        var endpoint = NormalizeRequired(value, parameterName);
        if (!Uri.TryCreate(endpoint, UriKind.RelativeOrAbsolute, out var uri))
        {
            throw new ArgumentException("Endpoint must be a valid URI.", parameterName);
        }

        if (uri.IsAbsoluteUri && !String.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException(
                "Endpoint user information cannot contain authentication material.",
                parameterName);
        }

        var fragmentIndex = endpoint.IndexOf('#', StringComparison.Ordinal);
        if (fragmentIndex >= 0)
        {
            throw new ArgumentException("Evidence endpoints cannot contain URI fragments.", parameterName);
        }

        var queryIndex = endpoint.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex < 0 || queryIndex == endpoint.Length - 1)
        {
            return endpoint;
        }

        foreach (var component in endpoint[(queryIndex + 1)..]
                     .Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = component.IndexOf('=');
            var encodedKey = separator < 0 ? component : component[..separator];
            var encodedValue = separator < 0 ? String.Empty : component[(separator + 1)..];
            var key = Uri.UnescapeDataString(encodedKey.Replace('+', ' '));
            var queryValue = Uri.UnescapeDataString(encodedValue.Replace('+', ' '));
            if (IsSensitiveQueryKey(key) ||
                queryValue.Equals("secret", StringComparison.OrdinalIgnoreCase) ||
                queryValue.Equals("password", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Endpoint query key '{key}' may contain authentication material. " +
                    "Pass credentials through the HTTP client configuration, never the evidence URI.",
                    parameterName);
            }
        }

        return endpoint;
    }

    private static bool IsSensitiveQueryKey(string key)
    {
        var normalized = new string(
            key.Where(Char.IsLetterOrDigit)
                .Select(Char.ToLowerInvariant)
                .ToArray());
        return normalized is "auth" or "authorization" or "apikey" or "cookie"
                   or "credential" or "password" or "privatekey" or "secret"
                   or "sig" or "signature" or "signed" or "token" or "accesstoken"
                   or "accesskey" ||
               normalized.EndsWith("authorization", StringComparison.Ordinal) ||
               normalized.EndsWith("apikey", StringComparison.Ordinal) ||
               normalized.EndsWith("signature", StringComparison.Ordinal) ||
               normalized.EndsWith("token", StringComparison.Ordinal);
    }
}

public static class EvidenceCanonicalJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.SnakeCaseLower,
            allowIntegerValues: false));
        return options;
    }

    public static byte[] SerializeToUtf8Bytes<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.SerializeToUtf8Bytes(value, Options);
    }

    public static string ComputeSha256<T>(T value) =>
        Convert.ToHexString(SHA256.HashData(SerializeToUtf8Bytes(value)))
            .ToLowerInvariant();

    public static T Deserialize<T>(ReadOnlySpan<byte> utf8Json)
    {
        var value = JsonSerializer.Deserialize<T>(utf8Json, Options);
        return value ?? throw new JsonException(
            $"Canonical evidence JSON did not contain a {typeof(T).FullName} value.");
    }
}
