using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Backtesting;

/// <summary>Adapters may supply independent evidence; bars alone cannot establish issuer identity or news absence.</summary>
public sealed record StrategyDecisionContextRequest(string Symbol, DateTimeOffset AsOfUtc,
    IReadOnlyList<OhlcvBar> DailyBars, string Mode);

public interface IStrategyDecisionContextProvider
{
    ValueTask<DrawdownDecisionContext?> ResolveAsync(StrategyDecisionContextRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record FrozenDecisionContextSource(string Id, string Path, string Sha256, string Version,
    DateTimeOffset KnownAtUtc, ImmutableArray<string> ContractVersions);
public sealed record FrozenDecisionContextEntry(string Symbol, DateTimeOffset AsOfUtc,
    DateTimeOffset KnownAtUtc, string Path, string Sha256, ImmutableArray<string> SourceIds);
public sealed record FrozenDecisionContextManifest(string SchemaVersion, string Provider, string Version,
    ImmutableArray<FrozenDecisionContextSource> Sources, ImmutableArray<FrozenDecisionContextEntry> Contexts);
public sealed record FrozenDecisionContextDependency(string Path, string Sha256);

/// <summary>
/// Loads only explicitly supplied immutable research files. It never reads operational discovery/news tables.
/// Source hashes bind every evidence reference and availability is checked independently of entry ordering.
/// </summary>
public sealed class FrozenManifestDecisionContextProvider : IStrategyDecisionContextProvider
{
    public const string SchemaVersion = "drawdown-context-manifest.v1";
    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly ImmutableArray<LoadedContext> contexts;
    public FrozenDecisionContextManifest Manifest { get; }
    public IReadOnlyList<FrozenDecisionContextDependency> Dependencies { get; }
    public string DependencyGraphSha256 { get; }
    public string ManifestSha256 { get; }
    public IReadOnlyList<DrawdownDecisionContext> ContextDocuments => contexts.Select(item => item.Context).ToArray();
    /// <summary>Publishes the exact loaded graph identity while keeping physical server paths private.</summary>
    public DecisionContextEvidenceProvenance Provenance => new(ManifestSha256, DependencyGraphSha256,
        Manifest.Provider, Manifest.Version, Manifest.Sources.Select(item => item.Sha256).Distinct().Order().ToArray());
    private sealed record LoadedContext(FrozenDecisionContextEntry Entry, DrawdownDecisionContext Context,
        DateTimeOffset AvailableAtUtc);

    /// <summary>Verifies every declared dependency and optional expected manifest before making any context available.</summary>
    public FrozenManifestDecisionContextProvider(string researchRoot, string manifestPath, string? expectedManifestSha256 = null)
    {
        if (!Path.IsPathFullyQualified(researchRoot))
            throw new InvalidDataException("Evidence research root must be an explicit absolute path.");
        var root = Path.GetFullPath(researchRoot);
        var dependencies = new Dictionary<string, FrozenDecisionContextDependency>(StringComparer.OrdinalIgnoreCase);
        // Retain the hashes of the exact bytes parsed, so capture can detect changes between validation and admission.
        byte[] ReadDependency(string path)
        {
            var bytes = ReadBoundFile(root, path);
            var fullPath = Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(root, path));
            dependencies[fullPath] = new(fullPath, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            return bytes;
        }
        var manifestBytes = ReadDependency(manifestPath);
        ManifestSha256 = Convert.ToHexStringLower(SHA256.HashData(manifestBytes));
        if (expectedManifestSha256 is not null) VerifyHash(manifestBytes, expectedManifestSha256);
        var manifest = ReadJson<FrozenDecisionContextManifest>(manifestBytes);
        Manifest = manifest;
        if (manifest.SchemaVersion != SchemaVersion || String.IsNullOrWhiteSpace(manifest.Provider) ||
            String.IsNullOrWhiteSpace(manifest.Version) || manifest.Sources.IsDefault || manifest.Contexts.IsDefault)
            throw new InvalidDataException("Evidence manifest requires its schema, provider, version, sources and contexts.");
        var sources = new Dictionary<string, FrozenDecisionContextSource>(StringComparer.Ordinal);
        var sourceBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in manifest.Sources)
        {
            RequireUtc(source.KnownAtUtc);
            if (String.IsNullOrWhiteSpace(source.Id) || String.IsNullOrWhiteSpace(source.Version) ||
                source.ContractVersions.IsDefaultOrEmpty || source.ContractVersions.Any(String.IsNullOrWhiteSpace) ||
                !sources.TryAdd(source.Id, source))
                throw new InvalidDataException("Evidence sources require unique IDs, frozen versions and declared contract versions.");
            var verifiedBytes = ReadDependency(source.Path);
            VerifyHash(verifiedBytes, source.Sha256);
            sourceBytes[source.Sha256] = verifiedBytes;
        }
        var loaded = ImmutableArray.CreateBuilder<LoadedContext>();
        foreach (var entry in manifest.Contexts)
        {
            RequireUtc(entry.AsOfUtc);
            RequireUtc(entry.KnownAtUtc);
            if (String.IsNullOrWhiteSpace(entry.Symbol) || entry.SourceIds.IsDefaultOrEmpty ||
                entry.SourceIds.Distinct(StringComparer.Ordinal).Count() != entry.SourceIds.Length)
                throw new InvalidDataException("Context requires a symbol and distinct frozen source IDs.");
            var boundSources = entry.SourceIds.Select(id => sources.TryGetValue(id, out var source) ? source :
                throw new InvalidDataException($"Context references undeclared source '{id}'.")).ToArray();
            var bytes = ReadDependency(entry.Path);
            VerifyHash(bytes, entry.Sha256);
            var context = ReadJson<DrawdownDecisionContext>(bytes);
            if (context.History is { } history)
                context = context with { History = history with {
                    ExpectedSessionTimestamps = history.ExpectedSessionTimestamps.ToImmutableArray() } };
            ValidateReferences(entry.Symbol, context, boundSources, sourceBytes);
            var availability = Availability(context).Append(entry.KnownAtUtc)
                .Concat(boundSources.Select(source => source.KnownAtUtc)).ToArray();
            foreach (var time in availability) RequireUtc(time);
            var available = availability.Max();
            loaded.Add(new(entry, context, available));
        }
        contexts = loaded.ToImmutable();
        if (contexts.GroupBy(item => (Symbol: item.Entry.Symbol.ToUpperInvariant(), item.Entry.AsOfUtc,
                item.AvailableAtUtc)).Any(group => group.Count() > 1))
            throw new InvalidDataException("Context versions have ambiguous availability ordering.");
        Dependencies = dependencies.Values.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        DependencyGraphSha256 = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            Dependencies.Select(item => new { Path = Path.GetRelativePath(root, item.Path).Replace('\\', '/'), item.Sha256 }))));
    }

    public ValueTask<DrawdownDecisionContext?> ResolveAsync(StrategyDecisionContextRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireUtc(request.AsOfUtc);
        var selected = contexts.Where(item => item.Entry.Symbol.Equals(request.Symbol, StringComparison.OrdinalIgnoreCase) &&
                item.Entry.AsOfUtc <= request.AsOfUtc && item.AvailableAtUtc <= request.AsOfUtc)
            .OrderByDescending(item => item.Entry.AsOfUtc).ThenByDescending(item => item.AvailableAtUtc).FirstOrDefault();
        return ValueTask.FromResult(selected?.Context);
    }

    /// <summary>Loads explicit configuration only and rejects any manifest or dependency identity changed since capture.</summary>
    public static IStrategyDecisionContextProvider? FromConfig(EngineConfig config)
    {
        if (String.IsNullOrWhiteSpace(config.EvidenceManifestPath))
        {
            if (!String.IsNullOrWhiteSpace(config.EvidenceResearchRoot) || config.EvidenceManifestSha256 is not null ||
                config.EvidenceDependencyGraphSha256 is not null)
                throw new InvalidDataException("Evidence research root requires an evidence manifest path.");
            return null;
        }
        if (String.IsNullOrWhiteSpace(config.EvidenceResearchRoot))
            throw new InvalidDataException("Evidence manifest requires an explicit evidence research root.");
        var provider = new FrozenManifestDecisionContextProvider(config.EvidenceResearchRoot, config.EvidenceManifestPath,
            config.EvidenceManifestSha256);
        if (config.EvidenceDependencyGraphSha256 is { } graph &&
            !provider.DependencyGraphSha256.Equals(graph, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Frozen decision-context dependency graph changed after selection.");
        return provider;
    }

    private static T ReadJson<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes, JsonOptions)
        ?? throw new InvalidDataException("Frozen evidence JSON is empty.");

    // Shared schema dispatch uses the same root and reparse-point restrictions before reading any bytes.
    internal static byte[] ReadBoundFile(string root, string path)
    {
        if (String.IsNullOrWhiteSpace(path)) throw new InvalidDataException("Evidence file path is required.");
        var full = Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(root, path));
        var relative = Path.GetRelativePath(root, full);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) || relative == ".")
            throw new InvalidDataException("Evidence file escapes the explicit research root.");
        // Reject junctions/symlinks as well as textual traversal; they could point into operator storage.
        var current = root;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Evidence root cannot be a symbolic link or junction.");
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Evidence file path cannot traverse symbolic links or junctions.");
        }
        return File.ReadAllBytes(full);
    }

    private static void VerifyHash(byte[] bytes, string expected)
    {
        if (!IsSha256(expected) || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Frozen evidence file SHA-256 does not match its manifest.");
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static void RequireUtc(DateTimeOffset value)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
            throw new InvalidDataException("Frozen evidence availability must be a non-default UTC timestamp.");
    }

    private static void ValidateReferences(string symbol, DrawdownDecisionContext context,
        IReadOnlyList<FrozenDecisionContextSource> sources, IReadOnlyDictionary<string, byte[]> sourceBytes)
    {
        var hashes = sources.Select(source => source.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        void Version(string version, string hash) { if (!sources.Any(source =>
                source.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase) && source.ContractVersions.Contains(version)))
            throw new InvalidDataException("Context contract version is not declared by its frozen source files."); }
        void Hash(string? hash) { if (!IsSha256(hash) || !hashes.Contains(hash!))
            throw new InvalidDataException("Context evidence hash is not bound to a verified frozen source file."); }
        void Binding(DrawdownPricePrefixBinding? binding, string securityId, string adjustmentVersion) {
            // Missing binding remains an explicit unknown in the shared analyzer.
            if (binding is null) return;
            Hash(binding.EvidenceSha256);
            Version(binding.AdjustmentVersion, binding.EvidenceSha256);
            if (!IsSha256(binding.ContentSha256) || binding.SecurityId != securityId ||
                binding.AdjustmentVersion != adjustmentVersion)
                throw new InvalidDataException("Frozen price prefix binding differs from its security/revision assertion.");
            var matchingSources = sources.Where(source => source.Sha256.Equals(binding.EvidenceSha256,
                StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matchingSources.Length != 1 || matchingSources[0].Version != DrawdownPriceSourceBundle.ContractVersion)
                throw new InvalidDataException("Price binding requires one independently frozen typed price-source bundle.");
            var source = matchingSources[0];
            var bundle = ReadJson<DrawdownPriceSourceBundle>(sourceBytes[binding.EvidenceSha256]);
            RequireUtc(bundle.KnownAtUtc);
            RequireUtc(binding.KnownAtUtc);
            if (bundle.SecurityId != securityId || bundle.AdjustmentVersion != adjustmentVersion ||
                source.KnownAtUtc > binding.KnownAtUtc || bundle.KnownAtUtc > binding.KnownAtUtc || bundle.Bars.IsDefaultOrEmpty)
                throw new InvalidDataException("Price-source bundle security, adjustment revision or receipt differs from its binding.");
            // Preserve physical row ordering: sorting would hide duplicate/reordered provider source data.
            for (var index = 0; index < bundle.Bars.Length; index++) {
                var bar = bundle.Bars[index];
                RequireUtc(bar.Timestamp);
                if (bar.KnownAtUtc is { } known) RequireUtc(known);
                if (bar.Timeframe != "1d" || index > 0 && bar.Timestamp <= bundle.Bars[index - 1].Timestamp)
                    throw new InvalidDataException("Price-source bundle rows must be ordered, unique native daily bars.");
            }
            var prefix = bundle.Bars.Where(bar => bar.Timestamp <= binding.ThroughSourceTimestamp).ToArray();
            if (prefix.Any(bar => bar.Timestamp.AddDays(1) > source.KnownAtUtc ||
                    bar.Timestamp.AddDays(1) > bundle.KnownAtUtc || bar.KnownAtUtc is { } known &&
                    (known > source.KnownAtUtc || known > bundle.KnownAtUtc)))
                throw new InvalidDataException("Price-source bundle or manifest receipt predates its consumed completed price rows.");
            var reason = DrawdownPriceBasisBinding.Validate(binding, securityId, adjustmentVersion,
                prefix, binding.KnownAtUtc);
            if (reason is not null)
                throw new InvalidDataException("Frozen price-source bundle does not match its prefix binding: " + reason);
        }
        void Identity(DrawdownSecurityIdentity identity) {
            if (!identity.Ticker.Equals(symbol, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Context security identity differs from its manifest symbol.");
            Hash(identity.EvidenceSha256);
            Version(identity.AdjustmentVersion, identity.EvidenceSha256);
            Binding(identity.PricePrefixBinding, identity.SecurityId, identity.AdjustmentVersion);
        }
        if (context.History is { } history) {
            if (history.Identity is null) throw new InvalidDataException("History requires independent security identity.");
            Identity(history.Identity); Hash(history.CalendarSha256);
        }
        if (context.Sector is { } sector) {
            Identity(sector.Identity); Hash(sector.MappingSha256); Hash(sector.CalendarSha256);
            Hash(sector.SectorBenchmark.EvidenceSha256); Hash(sector.MarketBenchmark.EvidenceSha256);
            Version(sector.SectorBenchmark.AdjustmentVersion, sector.SectorBenchmark.EvidenceSha256);
            Version(sector.MarketBenchmark.AdjustmentVersion, sector.MarketBenchmark.EvidenceSha256);
            Binding(sector.SectorBenchmark.PricePrefixBinding, sector.SectorBenchmark.SecurityId, sector.SectorBenchmark.AdjustmentVersion);
            Binding(sector.MarketBenchmark.PricePrefixBinding, sector.MarketBenchmark.SecurityId, sector.MarketBenchmark.AdjustmentVersion);
        }
        if (context.Events is { } events) {
            Identity(events.Identity); Hash(events.Evaluation.LabelDatasetSha256); Hash(events.Evaluation.EvaluationSha256);
            Version(events.Evaluation.TaxonomyVersion, events.Evaluation.EvaluationSha256);
            Version(events.Evaluation.ClassifierVersion, events.Evaluation.EvaluationSha256);
            Version(events.Evaluation.ProtocolVersion, events.Evaluation.EvaluationSha256);
            if (events.SectorMapping is { } mapping) Hash(mapping.EvidenceSha256);
            foreach (var coverage in events.ScopeCoverages) Hash(coverage.EvidenceSha256);
            foreach (var item in events.Events) { Hash(item.RevisionContentSha256); Hash(item.EvaluationSha256); }
        }
    }

    private static IEnumerable<DateTimeOffset> Availability(DrawdownDecisionContext context)
    {
        if (context.History is { } history) {
            yield return history.Identity!.KnownAtUtc;
            if (history.Identity.PricePrefixBinding is { } historyBasis) yield return historyBasis.KnownAtUtc;
            yield return history.CalendarKnownAtUtc ?? throw new InvalidDataException("History calendar availability is required.");
        }
        if (context.Sector is { } sector) {
            yield return sector.Identity.KnownAtUtc; yield return sector.MappingKnownAtUtc; yield return sector.CalendarKnownAtUtc;
            if (sector.Identity.PricePrefixBinding is { } stockBasis) yield return stockBasis.KnownAtUtc;
            if (sector.SectorBenchmark.PricePrefixBinding is { } sectorBasis) yield return sectorBasis.KnownAtUtc;
            if (sector.MarketBenchmark.PricePrefixBinding is { } marketBasis) yield return marketBasis.KnownAtUtc;
            yield return sector.SectorBenchmark.KnownAtUtc; yield return sector.MarketBenchmark.KnownAtUtc;
            foreach (var bar in sector.SectorBenchmark.Bars.Concat(sector.MarketBenchmark.Bars))
                yield return bar.KnownAtUtc ?? bar.Timestamp.AddDays(1);
        }
        if (context.Events is { } events) {
            yield return events.Identity.KnownAtUtc; yield return events.Evaluation.EvaluatedAtUtc;
            if (events.Identity.PricePrefixBinding is { } eventBasis) yield return eventBasis.KnownAtUtc;
            if (events.SectorMapping is { } mapping) yield return mapping.KnownAtUtc;
            foreach (var coverage in events.ScopeCoverages) yield return coverage.KnownAtUtc;
            foreach (var item in events.Events) {
                yield return item.KnownAtUtc; yield return item.PublishedAtUtc;
                yield return item.ReceivedAtUtc; yield return item.ClassifiedAtUtc;
                if (item.UpdatedAtUtc is { } updated) yield return updated;
            }
        }
    }
}
