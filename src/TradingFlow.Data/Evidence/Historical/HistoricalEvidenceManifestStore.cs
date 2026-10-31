using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingFlow.Data.Evidence.Historical;

public static class HistoricalEvidenceManifestStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string WriteExclusive(string directory, HistoricalEvidenceManifest manifest)
    {
        Validate(manifest);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "historical-manifest.json");
        var hashPath = Path.Combine(directory, "historical-manifest.sha256");
        if (File.Exists(path) || File.Exists(hashPath)) throw new IOException("Historical publication already exists; use a new output directory.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        WriteNew(path, bytes);
        WriteNew(hashPath, System.Text.Encoding.UTF8.GetBytes(Hash(bytes) + "\n"));
        return path;
    }

    public static HistoricalEvidenceManifest LoadVerified(string directory, bool verifySourceFiles = true)
    {
        var path = Path.Combine(directory, "historical-manifest.json");
        var bytes = File.ReadAllBytes(path);
        var expected = File.ReadAllText(Path.Combine(directory, "historical-manifest.sha256")).Trim();
        if (!IsHash(expected) || !Hash(bytes).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Historical manifest bytes do not match the published hash.");
        var manifest = JsonSerializer.Deserialize<HistoricalEvidenceManifest>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Historical manifest is empty.");
        Validate(manifest);
        if (verifySourceFiles)
            foreach (var source in manifest.Observations)
            {
                if (Hash(ReadBound(manifest.CollectionRoot, source.ReceiptRelativePath)) != source.ReceiptSha256 ||
                    Hash(ReadBound(manifest.CollectionRoot, source.PayloadRelativePath)) != source.PayloadSha256)
                    throw new InvalidDataException("Historical source receipt or payload changed: " + source.ObservationId);
            }
        return manifest;
    }

    public static void Validate(HistoricalEvidenceManifest manifest)
    {
        if (manifest.SchemaVersion != HistoricalEvidenceManifest.CurrentSchema || !Path.IsPathFullyQualified(manifest.CollectionRoot) ||
            manifest.Observations.IsDefault || manifest.Facts.IsDefault || manifest.Sessions.IsDefault || manifest.Claims.IsDefault)
            throw new InvalidDataException("Historical manifest requires its schema, explicit root and initialized collections.");
        RequireUtc(manifest.BuiltAtUtc);
        var observations = new Dictionary<string, HistoricalSourceObservation>(StringComparer.Ordinal);
        foreach (var source in manifest.Observations)
        {
            RequireUtc(source.CapturedAtUtc);
            if (source.CapturedAtUtc > manifest.BuiltAtUtc) throw new InvalidDataException("Manifest creation precedes a bound actual capture.");
            if (source.OriginalDeliveryObserved || !String.IsNullOrWhiteSpace(source.OriginalDeliveryEvidence))
                throw new InvalidDataException("Original-delivery admission requires a typed independently verified receipt scheme; archive metadata cannot supply it.");
            if (!IsHash(source.ObservationId) || !IsHash(source.ReceiptSha256) || source.ObservationId != source.ReceiptSha256 ||
                !IsHash(source.PayloadSha256) || !IsHash(source.RequestParametersSha256) || source.PayloadBytes < 0 ||
                String.IsNullOrWhiteSpace(source.Provider) || String.IsNullOrWhiteSpace(source.Resource) ||
                source.OriginalDeliveryObserved && String.IsNullOrWhiteSpace(source.OriginalDeliveryEvidence) ||
                !observations.TryAdd(source.ObservationId, source))
                throw new InvalidDataException("Historical observation has invalid or duplicate identity/provenance.");
            BoundPath(manifest.CollectionRoot, source.ReceiptRelativePath);
            BoundPath(manifest.CollectionRoot, source.PayloadRelativePath);
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fact in manifest.Facts)
        {
            RequireUtc(fact.ActualReceivedAtUtc);
            foreach (var time in new[] { fact.EffectiveAtUtc, fact.PublishedAtUtc, fact.UpdatedAtUtc })
                if (time is { } known) RequireUtc(known);
            if (!IsHash(fact.FactId) || !IsHash(fact.ContentSha256) || !ids.Add(fact.FactId) ||
                !Enum.IsDefined(fact.Kind) || !Enum.IsDefined(fact.Basis) ||
                String.IsNullOrWhiteSpace(fact.Subject) || String.IsNullOrWhiteSpace(fact.SourceRecordId) ||
                fact.SourceObservationIds.IsDefaultOrEmpty || fact.SourceObservationIds.Distinct().Count() != fact.SourceObservationIds.Length ||
                fact.SourceObservationIds.Any(id => !observations.ContainsKey(id)))
                throw new InvalidDataException("Historical fact has invalid or unbound identity/provenance.");
            if (fact.ActualReceivedAtUtc != fact.SourceObservationIds.Min(id => observations[id].CapturedAtUtc))
                throw new InvalidDataException("Historical fact receipt must be its earliest actual bound capture, not a simulated date.");
            if (fact.QualityReason is null && (fact.PublishedAtUtc > fact.ActualReceivedAtUtc || fact.UpdatedAtUtc > fact.ActualReceivedAtUtc ||
                fact.PublishedAtUtc is { } published && fact.UpdatedAtUtc is { } updated && updated < published))
                throw new InvalidDataException("Inconsistent source timestamps require explicit quarantine, not silent admission.");
            using var value = JsonDocument.Parse(fact.ValueJson);
            if (value.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Historical fact value must be an object.");
            if (fact.Basis == HistoricalEvidenceBasis.OfficialAcceptedArchive &&
                (fact.Kind != HistoricalFactKind.OfficialFilingAcceptance || fact.PublishedAtUtc is null ||
                 fact.SourceObservationIds.Any(id => !IsSecSource(observations[id]))))
                throw new InvalidDataException("Official acceptance requires a bound SEC archive source and acceptance timestamp.");
        }
        if (manifest.CalendarObservationId is { } calendar && !observations.ContainsKey(calendar))
            throw new InvalidDataException("Historical calendar source is unbound.");
        HistoricalSession? prior = null;
        foreach (var session in manifest.Sessions)
        {
            RequireUtc(session.OpenUtc); RequireUtc(session.CloseUtc);
            if (session.OpenUtc >= session.CloseUtc || prior is not null &&
                (session.ExchangeDate <= prior.ExchangeDate || session.OpenUtc <= prior.CloseUtc))
                throw new InvalidDataException("Historical exchange sessions are invalid or unordered.");
            prior = session;
        }
    }

    internal static bool IsSecSource(HistoricalSourceObservation source) => source.Provider == "sec-edgar" &&
        Uri.TryCreate(source.Resource, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        uri.Host.Equals("data.sec.gov", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.StartsWith("/submissions/CIK", StringComparison.Ordinal);
    internal static void RequireUtc(DateTimeOffset time)
    {
        if (time == default || time.Offset != TimeSpan.Zero) throw new InvalidDataException("Historical instants must be nondefault UTC.");
    }
    internal static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static string JsonHash(JsonElement value) => Hash(JsonSerializer.SerializeToUtf8Bytes(value));
    internal static string BoundPath(string root, string relative)
    {
        if (String.IsNullOrWhiteSpace(relative) || Path.IsPathFullyQualified(relative)) throw new InvalidDataException("Expected a relative historical source path.");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('\\', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Historical source path escapes its collection root.");
        for (var entry = path; entry.Length >= fullRoot.Length; entry = Path.GetDirectoryName(entry) ?? "")
        {
            if ((File.Exists(entry) || Directory.Exists(entry)) && (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Historical source paths cannot traverse symbolic links or reparse points.");
            if (entry == fullRoot) break;
        }
        return path;
    }
    internal static byte[] ReadBound(string root, string relative) => File.ReadAllBytes(BoundPath(root, relative));
    private static void WriteNew(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        stream.Write(bytes); stream.Flush(true);
    }
}
