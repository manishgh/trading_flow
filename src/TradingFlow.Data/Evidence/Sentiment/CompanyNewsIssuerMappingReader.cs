using System.Collections.Immutable;
using System.Text.Json;
using TradingFlow.Data.Evidence.Historical;
using TradingFlow.Domain.News;

namespace TradingFlow.Data.Evidence.Sentiment;

/// <summary>A frozen source selection for retrospective company-name interpretation, never historical universe membership.</summary>
public sealed record CompanyNewsIssuerMappingReference(string ResearchRootRelativePath, string Sha256);
public sealed record CompanyNewsIssuerMappingExclusion(string Symbol, string SourceIdentity, string Reason);
public sealed record CompanyNewsIssuerMappingReadResult(CompanyNewsIssuerMappingReference Source,
    ImmutableDictionary<string, CompanyNewsIssuerMapping> Mappings,
    ImmutableArray<CompanyNewsAssessmentDependency> Dependencies, ImmutableArray<string> UnavailableSymbols,
    ImmutableArray<CompanyNewsIssuerMappingExclusion> Exclusions, ImmutableArray<string> SelectedNewsSymbols);

/// <summary>Derives exact issuer names solely from verified, manifest-declared Finviz earnings receipts and raw payload rows.</summary>
public static class CompanyNewsIssuerMappingReader
{
    /// <summary>Reads the large source manifest once, verifies only its declared earnings graph and rejects conflicting names or aliases.</summary>
    public static CompanyNewsIssuerMappingReadResult ReadVerified(string researchRoot, string manifestPath, string expectedSha256,
        IEnumerable<string>? selectedNewsSymbols = null)
    {
        var selectedSymbols = NormalizeSelectedSymbols(selectedNewsSymbols ?? []);
        var relative = Path.GetRelativePath(researchRoot, Path.GetFullPath(manifestPath));
        var full = HistoricalEvidenceManifestStore.BoundPath(researchRoot, relative);
        var selection = ReadObservationSelection(full, expectedSha256);
        var replay = HistoricalEvidenceManifestBuilder.BuildFromReceiptPaths(selection.CollectionRoot,
            selection.Observations.Select(item => item.ReceiptRelativePath), selection.BuiltAtUtc);
        var actual = replay.Observations.ToDictionary(item => item.ObservationId, StringComparer.Ordinal);
        if (actual.Count != selection.Observations.Length || selection.Observations.Any(item =>
                !actual.TryGetValue(item.ObservationId, out var source) ||
                JsonSerializer.Serialize(item, HistoricalEvidenceManifestStore.JsonOptions) != JsonSerializer.Serialize(source, HistoricalEvidenceManifestStore.JsonOptions)))
            throw new InvalidDataException("Issuer mapping receipt metadata differs from the frozen historical observations.");
        var dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [relative] = expectedSha256 };
        var candidates = new List<CompanyNewsIssuerMapping>();
        var exclusions = ImmutableArray.CreateBuilder<CompanyNewsIssuerMappingExclusion>();
        foreach (var source in selection.Observations)
        {
            AddDependency(source.ReceiptRelativePath, source.ReceiptSha256);
            AddDependency(source.PayloadRelativePath, source.PayloadSha256);
            if (source.HttpStatus != 200) continue;
            using var payload = JsonDocument.Parse(HistoricalEvidenceManifestStore.ReadBound(selection.CollectionRoot, source.PayloadRelativePath));
            if (!payload.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Issuer mapping earnings payload lacks its archived items array.");
            var index = 0;
            foreach (var item in items.EnumerateArray())
            {
                var rowIndex = index++;
                var identity = source.ObservationId + ":items:" + rowIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var symbol = item.TryGetProperty("ticker", out var ticker) && ticker.ValueKind == JsonValueKind.String
                    ? ticker.GetString()!.Trim().ToUpperInvariant() : "";
                if (String.IsNullOrWhiteSpace(symbol)) { exclusions.Add(new("", identity, "symbol_missing")); continue; }
                if (!item.TryGetProperty("company", out var company) || company.ValueKind != JsonValueKind.String || String.IsNullOrWhiteSpace(company.GetString()))
                { exclusions.Add(new(symbol, identity, "official_name_missing")); continue; }
                var candidate = new CompanyNewsIssuerMapping(symbol, company.GetString()!, "finviz", identity, source.PayloadSha256, source.CapturedAtUtc);
                try { CompanyNewsAssessmentClassifier.GetIssuerAliases(candidate); candidates.Add(candidate); }
                catch (ArgumentException) { exclusions.Add(new(symbol, identity, "official_name_or_mapping_metadata_unsupported")); }
            }
        }
        var available = UniqueMappings(candidates, exclusions.Select(item => item.Symbol).Where(item => item.Length > 0), selectedSymbols, out var unavailable);
        return new(new(relative, expectedSha256), available,
            dependencies.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => new CompanyNewsAssessmentDependency(item.Key, item.Value)).ToImmutableArray(),
            unavailable, exclusions.ToImmutable(), selectedSymbols);

        // Keep source files bounded by the same research root used by immutable assessment publications.
        void AddDependency(string sourceRelativePath, string hash)
        {
            var path = HistoricalEvidenceManifestStore.BoundPath(selection.CollectionRoot, sourceRelativePath);
            var rootRelative = Path.GetRelativePath(researchRoot, path);
            HistoricalEvidenceManifestStore.BoundPath(researchRoot, rootRelative);
            if (dependencies.TryGetValue(rootRelative, out var existing) && existing != hash)
                throw new InvalidDataException("Issuer mapping source path has conflicting frozen hashes.");
            dependencies[rootRelative] = hash;
        }
    }

    /// <summary>Freezes only symbols actually supplied by the selected archived news revisions, without guessing a global ticker universe.</summary>
    internal static ImmutableArray<string> NormalizeSelectedSymbols(IEnumerable<string> symbols) => symbols
        .Where(item => !String.IsNullOrWhiteSpace(item)).Select(item => item.Trim().ToUpperInvariant())
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();

    /// <summary>Ignores normalized facts and universe filters; only declared Finviz earnings observation provenance supplies names.</summary>
    private static (string CollectionRoot, DateTimeOffset BuiltAtUtc, HistoricalSourceObservation[] Observations)
        ReadObservationSelection(string path, string expectedSha256)
    {
        using var stream = File.OpenRead(path);
        if (!HistoricalEvidenceManifestStore.IsHash(expectedSha256) ||
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream)) != expectedSha256)
            throw new InvalidDataException("Issuer mapping manifest differs from its frozen expected SHA256.");
        stream.Position = 0;
        using var document = JsonDocument.Parse(stream);
        var value = document.RootElement;
        if (value.GetProperty("SchemaVersion").GetString() != HistoricalEvidenceManifest.CurrentSchema)
            throw new InvalidDataException("Issuer mappings require an existing historical evidence manifest.");
        var root = value.GetProperty("CollectionRoot").GetString()!;
        if (!Path.IsPathFullyQualified(root)) throw new InvalidDataException("Issuer mapping collection root must be absolute.");
        var built = value.GetProperty("BuiltAtUtc").GetDateTimeOffset();
        HistoricalEvidenceManifestStore.RequireUtc(built);
        var observations = value.GetProperty("Observations").EnumerateArray()
            .Where(item => item.GetProperty("Provider").GetString() == "finviz" && item.GetProperty("Resource").GetString() == "/api/calendar/earnings")
            .Select(item => item.Deserialize<HistoricalSourceObservation>(HistoricalEvidenceManifestStore.JsonOptions)!).ToArray();
        if (observations.Length == 0 || observations.Select(item => item.ObservationId).Distinct(StringComparer.Ordinal).Count() != observations.Length ||
            observations.Any(item => item.CapturedAtUtc > built))
            throw new InvalidDataException("Issuer mapping manifest contains no unique, chronologically bound earnings observations.");
        return (root, built, observations);
    }

    /// <summary>Rejects every affected issuer when official names conflict or a permitted name alias belongs to multiple symbols.</summary>
    private static ImmutableDictionary<string, CompanyNewsIssuerMapping> UniqueMappings(List<CompanyNewsIssuerMapping> candidates,
        IEnumerable<string> unsupportedSymbols,
        ImmutableArray<string> selectedNewsSymbols,
        out ImmutableArray<string> unavailable)
    {
        var rejected = candidates.GroupBy(item => item.Symbol, StringComparer.Ordinal)
            .Where(group => group.Select(item => item.OfficialName.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
            .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        rejected.UnionWith(unsupportedSymbols);
        RejectAliasesMatchingOtherIssuerSymbols(candidates, selectedNewsSymbols.Concat(rejected), rejected);
        var aliasOwners = candidates.SelectMany(item => CompanyNewsAssessmentClassifier.GetIssuerAliases(item)
            .Select(alias => (Alias: alias.Trim(), item.Symbol)))
            .GroupBy(item => item.Alias, StringComparer.OrdinalIgnoreCase);
        foreach (var collision in aliasOwners.Where(group => group.Select(item => item.Symbol).Distinct(StringComparer.Ordinal).Count() > 1))
            foreach (var item in collision) rejected.Add(item.Symbol);
        unavailable = rejected.Order(StringComparer.Ordinal).ToImmutableArray();
        return candidates.Where(item => !rejected.Contains(item.Symbol)).GroupBy(item => item.Symbol, StringComparer.Ordinal)
            .ToImmutableDictionary(group => group.Key, group => group.OrderBy(item => item.ObservedAtUtc)
                .ThenBy(item => item.SourceIdentity, StringComparer.Ordinal).First(), StringComparer.Ordinal);
    }

    /// <summary>Withholds a company-name alias that equals a different known ticker, including issuers using literal fallback only.</summary>
    private static void RejectAliasesMatchingOtherIssuerSymbols(List<CompanyNewsIssuerMapping> candidates,
        IEnumerable<string> selectedAndUnsupportedSymbols, HashSet<string> rejected)
    {
        var knownSymbols = candidates.Select(item => item.Symbol).Concat(selectedAndUnsupportedSymbols)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (CompanyNewsAssessmentClassifier.GetIssuerAliases(candidate).Any(alias =>
                    !alias.Equals(candidate.Symbol, StringComparison.OrdinalIgnoreCase) && knownSymbols.Contains(alias)))
                rejected.Add(candidate.Symbol);
        }
    }
}
